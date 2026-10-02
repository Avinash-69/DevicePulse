using DevicePulse.Api.Authorization;
using DevicePulse.Api.Data;
using DevicePulse.Api.Entities;
using DevicePulse.Api.Exceptions;
using DevicePulse.Api.Models;
using DevicePulse.Api.Options;
using DevicePulse.Api.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevicePulse.Api.Services;

public interface IAuthService
{
    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task<AuthResponse> RefreshAsync(string refreshToken, CancellationToken ct = default);
    Task LogoutAsync(string refreshToken, CancellationToken ct = default);
    Task<CurrentUserResponse> GetCurrentAsync(CancellationToken ct = default);
    Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default);
}

public sealed class AuthService : IAuthService
{
    private readonly DevicePulseDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly SecurityOptions _security;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        DevicePulseDbContext db,
        IPasswordHasher passwordHasher,
        ITokenService tokenService,
        ICurrentUser currentUser,
        IAuditService audit,
        IOptions<SecurityOptions> security,
        ILogger<AuthService> logger)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _currentUser = currentUser;
        _audit = audit;
        _security = security.Value;
        _logger = logger;
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var email = Normalize(request.Email);

        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
            throw new ConflictException("An account with that email address already exists.");

        _passwordHasher.ValidatePolicy(request.Password);

        // Self-registration deliberately grants the lowest role. Anything more has to be
        // assigned by an administrator — a public endpoint that could mint an Admin would
        // make the whole RBAC model decorative.
        var viewerRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == SystemRoles.Viewer, ct)
            ?? throw new ValidationException("The default role is not configured. Contact an administrator.");

        var user = new User
        {
            Name = request.Name.Trim(),
            Email = email,
            PasswordHash = _passwordHasher.Hash(request.Password),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        user.UserRoles.Add(new UserRole { RoleId = viewerRole.RoleId });
        _db.Users.Add(user);

        _audit.Record(AuditActions.UserCreated, nameof(User), null,
            newValue: new { user.Name, user.Email, Roles = new[] { viewerRole.Name }, Source = "self-registration" });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The AnyAsync above and this insert are not atomic, so two simultaneous
            // registrations for the same address can both pass the check. The unique index on
            // Email is the real arbiter, and this is where it reports.
            //
            // The re-query has to happen inside the catch body rather than in a `when` filter,
            // because C# does not allow await in an exception filter.
            _db.ChangeTracker.Clear();

            if (await _db.Users.AnyAsync(u => u.Email == email, ct))
                throw new ConflictException("An account with that email address already exists.");

            // Some other constraint failed; let it surface as the defect it is.
            throw;
        }

        _logger.LogInformation("New account registered: {Email}", user.Email);

        return await IssueTokensAsync(user.UserId, ct);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var email = Normalize(request.Email);

        var user = await _db.Users
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(u => u.Email == email, ct);

        // One generic message for "no such user", "wrong password" and "inactive account".
        // Distinguishing them turns the login form into an account-enumeration oracle.
        const string genericFailure = "Invalid email address or password.";

        if (user is null)
        {
            _logger.LogWarning("Login attempt for an unknown address: {Email}", email);
            throw new AuthenticationException(genericFailure);
        }

        if (user.LockedOutUntil is not null && user.LockedOutUntil > DateTime.UtcNow)
        {
            // The lockout *is* disclosed, unlike the cases above: the user needs to know why
            // waiting will help, and an attacker who triggered it already knows.
            var minutes = Math.Max(1, (int)Math.Ceiling((user.LockedOutUntil.Value - DateTime.UtcNow).TotalMinutes));
            throw new AuthenticationException(
                $"This account is temporarily locked after too many failed sign-in attempts. Try again in {minutes} minute(s).");
        }

        if (!_passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            await RegisterFailedAttemptAsync(user, ct);
            throw new AuthenticationException(genericFailure);
        }

        if (!user.IsActive)
        {
            _logger.LogWarning("Login attempt against a deactivated account: {Email}", user.Email);
            throw new AuthenticationException(genericFailure);
        }

        // A successful sign-in clears the counter, so a user who mistypes twice and then gets
        // it right does not stay one attempt away from a lockout indefinitely.
        user.FailedLoginAttempts = 0;
        user.LockedOutUntil = null;
        user.LastLoginAt = DateTime.UtcNow;

        _audit.Record(AuditActions.LoginSucceeded, nameof(User), user.UserId,
            actingUserId: user.UserId, actingUserEmail: user.Email);
        await _db.SaveChangesAsync(ct);

        return await IssueTokensAsync(user.UserId, ct);
    }

    public async Task<AuthResponse> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new AuthenticationException("A refresh token is required.");

        var hash = _tokenService.HashRefreshToken(refreshToken);

        var stored = await _db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
            ?? throw new AuthenticationException("That refresh token is not valid.");

        if (stored.RevokedAt is not null)
        {
            // A revoked token being presented means either a stale client or a stolen token
            // being replayed. We cannot tell which, so we assume the worse case and revoke the
            // whole family, forcing a fresh login.
            _logger.LogWarning(
                "A revoked refresh token was presented for user {UserId}; revoking all of that user's tokens.",
                stored.UserId);

            await RevokeAllForUserAsync(stored.UserId, "Replay of a revoked token detected", ct);
            await _db.SaveChangesAsync(ct);

            throw new AuthenticationException("That refresh token is no longer valid. Please sign in again.");
        }

        if (stored.ExpiresAt <= DateTime.UtcNow)
            throw new AuthenticationException("That refresh token has expired. Please sign in again.");

        if (stored.User is null || !stored.User.IsActive)
            throw new AuthenticationException("This account is no longer active.");

        // Rotation: the presented token is retired as part of issuing its replacement, so a
        // captured token is good for at most one use.
        stored.RevokedAt = DateTime.UtcNow;
        stored.RevokedReason = "Rotated on refresh";

        var response = await IssueTokensAsync(stored.UserId, ct, rotatedFrom: stored);
        return response;
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return;

        var hash = _tokenService.HashRefreshToken(refreshToken);
        var stored = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        // Logging out an already-invalid token is not an error — the caller wanted the session
        // gone and it is gone. Reporting a failure here just makes clients handle a non-problem.
        if (stored is null || stored.RevokedAt is not null)
            return;

        stored.RevokedAt = DateTime.UtcNow;
        stored.RevokedReason = "Signed out";

        await _db.SaveChangesAsync(ct);
    }

    public async Task<CurrentUserResponse> GetCurrentAsync(CancellationToken ct = default)
    {
        var userId = _currentUser.UserId
            ?? throw new AuthenticationException("Not authenticated.");

        return await BuildCurrentUserAsync(userId, ct);
    }

    public async Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default)
    {
        var userId = _currentUser.UserId
            ?? throw new AuthenticationException("Not authenticated.");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == userId, ct)
            ?? throw new NotFoundException(nameof(User), userId);

        if (!_passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
            throw new AuthenticationException("The current password is incorrect.");

        _passwordHasher.ValidatePolicy(request.NewPassword);

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        user.UpdatedAt = DateTime.UtcNow;

        // Every existing session is invalidated: a password change usually means the old one is
        // considered compromised, and leaving live refresh tokens behind would defeat the point.
        await RevokeAllForUserAsync(userId, "Password changed", ct);

        // The audit entry records that it happened, never the password or its hash (§17, D.5).
        _audit.Record(AuditActions.UserPasswordReset, nameof(User), userId,
            newValue: new { ChangedBy = "self" });

        await _db.SaveChangesAsync(ct);
    }

    private async Task RegisterFailedAttemptAsync(User user, CancellationToken ct)
    {
        user.FailedLoginAttempts++;

        if (user.FailedLoginAttempts >= _security.MaxFailedLoginAttempts)
        {
            user.LockedOutUntil = DateTime.UtcNow.AddMinutes(_security.LockoutMinutes);
            user.FailedLoginAttempts = 0;

            _logger.LogWarning(
                "Account {Email} locked out until {LockedOutUntil} after {Attempts} failed attempts.",
                user.Email, user.LockedOutUntil, _security.MaxFailedLoginAttempts);

            _audit.Record(AuditActions.AccountLockedOut, nameof(User), user.UserId,
                newValue: new { user.LockedOutUntil },
                actingUserId: user.UserId, actingUserEmail: user.Email);
        }
        else
        {
            _audit.Record(AuditActions.LoginFailed, nameof(User), user.UserId,
                newValue: new { user.FailedLoginAttempts },
                actingUserId: user.UserId, actingUserEmail: user.Email);
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task RevokeAllForUserAsync(int userId, string reason, CancellationToken ct)
    {
        var active = await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var token in active)
        {
            token.RevokedAt = DateTime.UtcNow;
            token.RevokedReason = reason;
        }
    }

    private async Task<AuthResponse> IssueTokensAsync(int userId, CancellationToken ct, RefreshToken? rotatedFrom = null)
    {
        var user = await _db.Users.FirstAsync(u => u.UserId == userId, ct);
        var (roles, permissions) = await LoadRolesAndPermissionsAsync(userId, ct);

        var (accessToken, accessExpiresAt) = _tokenService.CreateAccessToken(user, roles, permissions);
        var (refreshToken, refreshHash, refreshExpiresAt) = _tokenService.CreateRefreshToken();

        var stored = new RefreshToken
        {
            UserId = userId,
            TokenHash = refreshHash,
            ExpiresAt = refreshExpiresAt,
            CreatedAt = DateTime.UtcNow,
            CreatedByIp = _currentUser.IpAddress
        };

        _db.RefreshTokens.Add(stored);
        await _db.SaveChangesAsync(ct);

        // Linking the old token to its successor makes a replay chain reconstructable after
        // the fact, which is the whole reason ReplacedByTokenId exists.
        if (rotatedFrom is not null)
        {
            rotatedFrom.ReplacedByTokenId = stored.RefreshTokenId;
            await _db.SaveChangesAsync(ct);
        }

        return new AuthResponse(
            accessToken,
            refreshToken,
            accessExpiresAt,
            new CurrentUserResponse(user.UserId, user.Name, user.Email, roles, permissions));
    }

    private async Task<CurrentUserResponse> BuildCurrentUserAsync(int userId, CancellationToken ct)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId, ct)
            ?? throw new NotFoundException(nameof(User), userId);

        var (roles, permissions) = await LoadRolesAndPermissionsAsync(userId, ct);
        return new CurrentUserResponse(user.UserId, user.Name, user.Email, roles, permissions);
    }

    /// <summary>
    /// Resolves the User → Roles → Permissions chain (§11). Only active roles count, so
    /// disabling a role immediately stops granting its permissions on the next token issue.
    /// </summary>
    private async Task<(List<string> Roles, List<string> Permissions)> LoadRolesAndPermissionsAsync(
        int userId, CancellationToken ct)
    {
        var roleRows = await _db.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId && ur.Role!.IsActive)
            .Select(ur => new
            {
                ur.Role!.Name,
                Permissions = ur.Role.RolePermissions.Select(rp => rp.Permission!.Key).ToList()
            })
            .ToListAsync(ct);

        var roles = roleRows.Select(r => r.Name).Distinct().OrderBy(n => n).ToList();

        var permissions = roleRows
            .SelectMany(r => r.Permissions)
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        return (roles, permissions);
    }

    /// <summary>
    /// Email is the login key, so it is stored and compared in one canonical form. Without
    /// this, "Admin@x.com" and "admin@x.com" become two accounts on a case-sensitive collation.
    /// </summary>
    private static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
