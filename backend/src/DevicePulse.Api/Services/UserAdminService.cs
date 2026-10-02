using DevicePulse.Api.Authorization;
using DevicePulse.Api.Data;
using DevicePulse.Api.Entities;
using DevicePulse.Api.Exceptions;
using DevicePulse.Api.Models;
using DevicePulse.Api.Models.Common;
using DevicePulse.Api.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace DevicePulse.Api.Services;

public interface IUserAdminService
{
    Task<PagedResult<UserResponse>> QueryAsync(UserQuery query, CancellationToken ct = default);
    Task<UserResponse> GetByIdAsync(int id, CancellationToken ct = default);
    Task<UserResponse> CreateAsync(CreateUserRequest request, CancellationToken ct = default);
    Task<UserResponse> UpdateAsync(int id, UpdateUserRequest request, CancellationToken ct = default);
    Task<UserResponse> SetStatusAsync(int id, bool isActive, CancellationToken ct = default);
    Task ResetPasswordAsync(int id, ResetUserPasswordRequest request, CancellationToken ct = default);
}

/// <summary>
/// Administrator-driven user management (§12).
///
/// Note what this service deliberately protects against: an administrator locking the
/// application out of its own administration. Removing the last Super Admin, or deactivating
/// yourself, is refused rather than allowed-and-regretted.
/// </summary>
public sealed class UserAdminService : IUserAdminService
{
    private readonly DevicePulseDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditService _audit;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<UserAdminService> _logger;

    public UserAdminService(
        DevicePulseDbContext db,
        IPasswordHasher passwordHasher,
        IAuditService audit,
        ICurrentUser currentUser,
        ILogger<UserAdminService> logger)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _audit = audit;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<PagedResult<UserResponse>> QueryAsync(UserQuery query, CancellationToken ct = default)
    {
        var q = _db.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(u => u.Name.Contains(term) || u.Email.Contains(term));
        }

        if (query.IsActive is not null)
            q = q.Where(u => u.IsActive == query.IsActive);

        if (query.RoleId is not null)
            q = q.Where(u => u.UserRoles.Any(ur => ur.RoleId == query.RoleId));

        var total = await q.CountAsync(ct);

        var items = await q
            .OrderBy(u => u.Name)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(Projection)
            .ToListAsync(ct);

        return new PagedResult<UserResponse>(items, query.Page, query.PageSize, total);
    }

    public async Task<UserResponse> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking()
            .Where(u => u.UserId == id)
            .Select(Projection)
            .FirstOrDefaultAsync(ct);

        return user ?? throw new NotFoundException(nameof(User), id);
    }

    public async Task<UserResponse> CreateAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
            throw new ConflictException("An account with that email address already exists.");

        _passwordHasher.ValidatePolicy(request.Password);

        var roles = await ResolveRolesAsync(request.RoleIds, ct);

        var user = new User
        {
            Name = request.Name.Trim(),
            Email = email,
            PasswordHash = _passwordHasher.Hash(request.Password),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        foreach (var role in roles)
            user.UserRoles.Add(new UserRole { RoleId = role.RoleId });

        _db.Users.Add(user);

        // The role names go into the audit entry, not the ids: a reader six months from now
        // should not have to join against a table to understand what was granted.
        _audit.Record(AuditActions.UserCreated, nameof(User), null,
            newValue: new { user.Name, user.Email, Roles = roles.Select(r => r.Name).ToArray() });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Re-queried here rather than in a `when` filter, which cannot await.
            _db.ChangeTracker.Clear();

            if (await _db.Users.AnyAsync(u => u.Email == email, ct))
                throw new ConflictException("An account with that email address already exists.");

            throw;
        }

        _logger.LogInformation("User {Email} created by {ActorId}.", user.Email, _currentUser.UserId);
        return await GetByIdAsync(user.UserId, ct);
    }

    public async Task<UserResponse> UpdateAsync(int id, UpdateUserRequest request, CancellationToken ct = default)
    {
        var user = await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.UserId == id, ct)
            ?? throw new NotFoundException(nameof(User), id);

        var email = request.Email.Trim().ToLowerInvariant();

        if (await _db.Users.AnyAsync(u => u.Email == email && u.UserId != id, ct))
            throw new ConflictException("Another account already uses that email address.");

        var newRoles = await ResolveRolesAsync(request.RoleIds, ct);
        var oldRoleNames = user.UserRoles.Select(ur => ur.Role!.Name).OrderBy(n => n).ToArray();

        await EnsureNotRemovingLastSuperAdminAsync(user, newRoles, ct);

        var before = new { user.Name, user.Email, Roles = oldRoleNames };

        user.Name = request.Name.Trim();
        user.Email = email;
        user.UpdatedAt = DateTime.UtcNow;

        // Replaced wholesale rather than diffed: the request states the complete intended set,
        // which makes the operation idempotent and removes a class of partial-update bugs.
        user.UserRoles.Clear();
        foreach (var role in newRoles)
            user.UserRoles.Add(new UserRole { UserId = id, RoleId = role.RoleId });

        var newRoleNames = newRoles.Select(r => r.Name).OrderBy(n => n).ToArray();

        _audit.Record(AuditActions.UserUpdated, nameof(User), id,
            oldValue: before,
            newValue: new { user.Name, user.Email, Roles = newRoleNames });

        if (!oldRoleNames.SequenceEqual(newRoleNames))
        {
            // Recorded separately from the profile edit: "who gave this person settings.manage"
            // is a question that gets asked on its own, and it should be filterable on its own.
            _audit.Record(AuditActions.UserRolesChanged, nameof(User), id,
                oldValue: oldRoleNames, newValue: newRoleNames);
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("User {UserId} updated by {ActorId}.", id, _currentUser.UserId);
        return await GetByIdAsync(id, ct);
    }

    public async Task<UserResponse> SetStatusAsync(int id, bool isActive, CancellationToken ct = default)
    {
        var user = await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.UserId == id, ct)
            ?? throw new NotFoundException(nameof(User), id);

        // Deactivating your own account would log you out with no way back in — almost
        // certainly a misclick, and trivially preventable.
        if (!isActive && id == _currentUser.UserId)
            throw new ValidationException("You cannot deactivate your own account.");

        if (!isActive)
            await EnsureNotRemovingLastSuperAdminAsync(user, [], ct);

        if (user.IsActive == isActive)
            return await GetByIdAsync(id, ct);

        user.IsActive = isActive;
        user.UpdatedAt = DateTime.UtcNow;

        if (!isActive)
        {
            // Deactivation has to end live sessions too. Leaving valid refresh tokens behind
            // would let a disabled account keep working until they happened to expire.
            var activeTokens = await _db.RefreshTokens
                .Where(t => t.UserId == id && t.RevokedAt == null)
                .ToListAsync(ct);

            foreach (var token in activeTokens)
            {
                token.RevokedAt = DateTime.UtcNow;
                token.RevokedReason = "Account deactivated";
            }
        }
        else
        {
            // Reactivating clears any standing lockout; otherwise the user would be re-enabled
            // and still unable to sign in.
            user.FailedLoginAttempts = 0;
            user.LockedOutUntil = null;
        }

        _audit.Record(AuditActions.UserStatusChanged, nameof(User), id,
            oldValue: new { IsActive = !isActive },
            newValue: new { IsActive = isActive });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("User {UserId} {State} by {ActorId}.",
            id, isActive ? "activated" : "deactivated", _currentUser.UserId);

        return await GetByIdAsync(id, ct);
    }

    public async Task ResetPasswordAsync(int id, ResetUserPasswordRequest request, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == id, ct)
            ?? throw new NotFoundException(nameof(User), id);

        _passwordHasher.ValidatePolicy(request.NewPassword);

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        user.UpdatedAt = DateTime.UtcNow;

        // An administrative reset also clears a lockout — that is usually the reason the reset
        // is being performed in the first place.
        user.FailedLoginAttempts = 0;
        user.LockedOutUntil = null;

        var activeTokens = await _db.RefreshTokens
            .Where(t => t.UserId == id && t.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var token in activeTokens)
        {
            token.RevokedAt = DateTime.UtcNow;
            token.RevokedReason = "Password reset by an administrator";
        }

        // Records that a reset happened and who did it — never the password itself (§17, D.5).
        _audit.Record(AuditActions.UserPasswordReset, nameof(User), id,
            newValue: new { ResetByUserId = _currentUser.UserId, SessionsRevoked = activeTokens.Count });

        await _db.SaveChangesAsync(ct);

        _logger.LogWarning("Password for user {UserId} was reset by {ActorId}.", id, _currentUser.UserId);
    }

    private async Task<List<Role>> ResolveRolesAsync(IReadOnlyList<int> roleIds, CancellationToken ct)
    {
        var distinctIds = roleIds.Distinct().ToList();

        if (distinctIds.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["roleIds"] = ["Assign at least one role."]
            });

        var roles = await _db.Roles.Where(r => distinctIds.Contains(r.RoleId)).ToListAsync(ct);

        // Named explicitly rather than reported as a generic failure, so the caller can tell
        // a typo from a stale client cache.
        var missing = distinctIds.Except(roles.Select(r => r.RoleId)).ToList();

        if (missing.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["roleIds"] = [$"These role ids do not exist: {string.Join(", ", missing)}."]
            });

        var inactive = roles.Where(r => !r.IsActive).Select(r => r.Name).ToList();

        if (inactive.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["roleIds"] = [$"These roles are disabled and cannot be assigned: {string.Join(", ", inactive)}."]
            });

        return roles;
    }

    /// <summary>
    /// Refuses the change if it would leave the system with no active Super Admin.
    ///
    /// This is the safeguard that keeps the application administrable: without it, one
    /// well-intentioned edit ("this person left, take their admin rights away") can leave
    /// nobody able to manage users, roles or settings, with no recovery path short of
    /// editing the database by hand — exactly what §4.1 says should never be necessary.
    /// </summary>
    private async Task EnsureNotRemovingLastSuperAdminAsync(User user, List<Role> newRoles, CancellationToken ct)
    {
        var superAdminRoleId = await _db.Roles
            .Where(r => r.Name == SystemRoles.SuperAdmin)
            .Select(r => r.RoleId)
            .FirstOrDefaultAsync(ct);

        if (superAdminRoleId == 0)
            return;

        var currentlyHasIt = user.UserRoles.Any(ur => ur.RoleId == superAdminRoleId);

        if (!currentlyHasIt)
            return;

        if (newRoles.Any(r => r.RoleId == superAdminRoleId))
            return;

        var otherSuperAdmins = await _db.UserRoles
            .CountAsync(ur => ur.RoleId == superAdminRoleId
                           && ur.UserId != user.UserId
                           && ur.User!.IsActive, ct);

        if (otherSuperAdmins == 0)
            throw new ValidationException(
                "This is the only active Super Admin. Grant the role to another active user before removing it here.");
    }

    private static readonly System.Linq.Expressions.Expression<Func<User, UserResponse>> Projection =
        u => new UserResponse(
            u.UserId,
            u.Name,
            u.Email,
            u.IsActive,
            u.LockedOutUntil != null && u.LockedOutUntil > DateTime.UtcNow,
            u.LockedOutUntil,
            u.LastLoginAt,
            u.UserRoles.Select(ur => new RoleSummary(ur.RoleId, ur.Role!.Name)).ToList(),
            u.CreatedAt,
            u.UpdatedAt);
}
