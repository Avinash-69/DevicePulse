using DevicePulse.Api.Authorization;
using DevicePulse.Api.Data;
using DevicePulse.Api.Entities;
using DevicePulse.Api.Exceptions;
using DevicePulse.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace DevicePulse.Api.Services;

public interface IRoleService
{
    Task<IReadOnlyList<RoleResponse>> GetAllAsync(CancellationToken ct = default);
    Task<RoleResponse> GetByIdAsync(int id, CancellationToken ct = default);
    Task<RoleResponse> CreateAsync(CreateRoleRequest request, CancellationToken ct = default);
    Task<RoleResponse> UpdateAsync(int id, UpdateRoleRequest request, CancellationToken ct = default);
    Task<RoleResponse> SetPermissionsAsync(int id, SetRolePermissionsRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<PermissionResponse>> GetPermissionCatalogAsync(CancellationToken ct = default);
}

/// <summary>
/// Runtime role and permission administration (§12, Appendix A.3).
///
/// This is what makes the RBAC story real rather than decorative: a Super Admin can invent a
/// new role, attach an arbitrary subset of the permission catalog to it, and assign it — all
/// through the UI, with the API enforcing the result centrally. The permission *keys* remain
/// developer-controlled (Appendix C item 4), because a key the backend does not check would be
/// a permission that does nothing.
/// </summary>
public sealed class RoleService : IRoleService
{
    private readonly DevicePulseDbContext _db;
    private readonly IAuditService _audit;
    private readonly ILogger<RoleService> _logger;

    public RoleService(DevicePulseDbContext db, IAuditService audit, ILogger<RoleService> logger)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RoleResponse>> GetAllAsync(CancellationToken ct = default) =>
        await _db.Roles.AsNoTracking()
            .OrderByDescending(r => r.IsSystemRole).ThenBy(r => r.Name)
            .Select(Projection)
            .ToListAsync(ct);

    public async Task<RoleResponse> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var role = await _db.Roles.AsNoTracking()
            .Where(r => r.RoleId == id)
            .Select(Projection)
            .FirstOrDefaultAsync(ct);

        return role ?? throw new NotFoundException(nameof(Role), id);
    }

    public async Task<RoleResponse> CreateAsync(CreateRoleRequest request, CancellationToken ct = default)
    {
        var name = request.Name.Trim();

        if (await _db.Roles.AnyAsync(r => r.Name == name, ct))
            throw new ConflictException($"A role named '{name}' already exists.");

        var role = new Role
        {
            Name = name,
            Description = request.Description?.Trim(),
            IsActive = true,

            // Only the seeder creates system roles. A role made through the API is always a
            // custom one, and therefore always editable and deletable.
            IsSystemRole = false,
            CreatedAt = DateTime.UtcNow
        };

        _db.Roles.Add(role);

        var permissionKeys = request.Permissions ?? [];

        if (permissionKeys.Count > 0)
        {
            var permissions = await ResolvePermissionsAsync(permissionKeys, ct);

            foreach (var permission in permissions)
                role.RolePermissions.Add(new RolePermission { PermissionId = permission.PermissionId });
        }

        _audit.Record(AuditActions.RoleCreated, nameof(Role), null,
            newValue: new { role.Name, role.Description, Permissions = permissionKeys.ToArray() });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Role created: {RoleName} with {PermissionCount} permission(s).", role.Name, permissionKeys.Count);
        return await GetByIdAsync(role.RoleId, ct);
    }

    public async Task<RoleResponse> UpdateAsync(int id, UpdateRoleRequest request, CancellationToken ct = default)
    {
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.RoleId == id, ct)
            ?? throw new NotFoundException(nameof(Role), id);

        var name = request.Name.Trim();

        if (role.IsSystemRole)
        {
            // The seeded roles are referenced by name in code (SystemRoles.SuperAdmin drives
            // the last-admin safeguard, Viewer is the self-registration default). Renaming one
            // would quietly break those lookups, so the name is frozen; the description and the
            // permission set are still editable.
            if (!string.Equals(role.Name, name, StringComparison.Ordinal))
                throw new ValidationException($"'{role.Name}' is a built-in role and cannot be renamed.");

            if (!request.IsActive)
                throw new ValidationException($"'{role.Name}' is a built-in role and cannot be disabled.");
        }

        if (await _db.Roles.AnyAsync(r => r.Name == name && r.RoleId != id, ct))
            throw new ConflictException($"A role named '{name}' already exists.");

        var before = new { role.Name, role.Description, role.IsActive };

        role.Name = name;
        role.Description = request.Description?.Trim();
        role.IsActive = request.IsActive;
        role.UpdatedAt = DateTime.UtcNow;

        _audit.Record(AuditActions.RoleUpdated, nameof(Role), id,
            oldValue: before,
            newValue: new { role.Name, role.Description, role.IsActive });

        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(id, ct);
    }

    public async Task<RoleResponse> SetPermissionsAsync(int id, SetRolePermissionsRequest request, CancellationToken ct = default)
    {
        var role = await _db.Roles
            .Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(r => r.RoleId == id, ct)
            ?? throw new NotFoundException(nameof(Role), id);

        var requested = request.Permissions.Distinct().ToList();
        var permissions = await ResolvePermissionsAsync(requested, ct);

        var oldKeys = role.RolePermissions.Select(rp => rp.Permission!.Key).OrderBy(k => k).ToArray();
        var newKeys = permissions.Select(p => p.Key).OrderBy(k => k).ToArray();

        if (oldKeys.SequenceEqual(newKeys))
            return await GetByIdAsync(id, ct);

        if (role.Name == SystemRoles.SuperAdmin)
        {
            // Stripping a permission from SuperAdmin is how an administrator accidentally
            // removes their own ability to put it back. That role keeps the full catalog.
            var missing = Permissions.All.Select(p => p.Key).Except(newKeys).ToList();

            if (missing.Count > 0)
                throw new ValidationException(
                    $"The {SystemRoles.SuperAdmin} role must retain every permission. Missing: {string.Join(", ", missing)}.");
        }

        role.RolePermissions.Clear();

        foreach (var permission in permissions)
            role.RolePermissions.Add(new RolePermission { RoleId = id, PermissionId = permission.PermissionId });

        role.UpdatedAt = DateTime.UtcNow;

        _audit.Record(AuditActions.RolePermissionsChanged, nameof(Role), id,
            oldValue: new { role.Name, Permissions = oldKeys },
            newValue: new { role.Name, Permissions = newKeys });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Permissions for role {RoleName} changed: {Added} added, {Removed} removed.",
            role.Name, newKeys.Except(oldKeys).Count(), oldKeys.Except(newKeys).Count());

        // Users holding this role keep their old permissions until their access token is next
        // refreshed. That window is bounded by JwtOptions.AccessTokenMinutes and is the
        // deliberate cost of putting permissions in the token (see PermissionAuthorizationHandler).
        return await GetByIdAsync(id, ct);
    }

    public async Task<IReadOnlyList<PermissionResponse>> GetPermissionCatalogAsync(CancellationToken ct = default) =>
        await _db.Permissions.AsNoTracking()
            .OrderBy(p => p.Category).ThenBy(p => p.Key)
            .Select(p => new PermissionResponse(p.PermissionId, p.Key, p.Name, p.Description, p.Category))
            .ToListAsync(ct);

    private async Task<List<Permission>> ResolvePermissionsAsync(IReadOnlyList<string> keys, CancellationToken ct)
    {
        var permissions = await _db.Permissions.Where(p => keys.Contains(p.Key)).ToListAsync(ct);

        var unknown = keys.Except(permissions.Select(p => p.Key), StringComparer.Ordinal).ToList();

        // A key not in the catalog is rejected rather than stored. Accepting it would create a
        // permission that looks granted in the UI but that no policy will ever match
        // (Appendix C item 4).
        if (unknown.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["permissions"] = [$"These permission keys are not in the catalog: {string.Join(", ", unknown)}."]
            });

        return permissions;
    }

    private static readonly System.Linq.Expressions.Expression<Func<Role, RoleResponse>> Projection =
        r => new RoleResponse(
            r.RoleId,
            r.Name,
            r.Description,
            r.IsActive,
            r.IsSystemRole,
            r.UserRoles.Count,
            r.RolePermissions.Select(rp => rp.Permission!.Key).ToList(),
            r.CreatedAt,
            r.UpdatedAt);
}
