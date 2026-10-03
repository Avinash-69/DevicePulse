namespace DevicePulse.Api.Entities;

public class User
{
    public int UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    // Login throttling / lockout (Appendix D.3). Counters live on the user row so the
    // behaviour survives an app restart — an in-memory counter would reset on deploy.
    public int FailedLoginAttempts { get; set; }
    public DateTime? LockedOutUntil { get; set; }
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// Populated in Phase 2 when identity moves to Keycloak: the Keycloak subject id.
    /// Null for locally-authenticated users. The column exists now so the Phase 2
    /// migration is additive rather than a schema rewrite.
    /// </summary>
    public string? ExternalIdentityId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}

public class Role
{
    public int RoleId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// A system role (SuperAdmin, Admin, Operator, Viewer) cannot be renamed or deleted —
    /// the seeded SuperAdmin role in particular must never be removable, or the application
    /// can be locked out of its own administration.
    /// </summary>
    public bool IsSystemRole { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}

/// <summary>
/// The permission catalog is developer-controlled (Appendix C item 4): rows here are seeded
/// from <see cref="Authorization.Permissions"/> in code as features are built. What a Super Admin
/// configures at runtime is which roles hold which of these existing permissions.
/// </summary>
public class Permission
{
    public int PermissionId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = string.Empty;

    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}

public class UserRole
{
    public int UserId { get; set; }
    public User? User { get; set; }
    public int RoleId { get; set; }
    public Role? Role { get; set; }
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
}

public class RolePermission
{
    public int RoleId { get; set; }
    public Role? Role { get; set; }
    public int PermissionId { get; set; }
    public Permission? Permission { get; set; }
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Refresh tokens are persisted (not stateless) so they can be revoked — a stateless
/// refresh token cannot be taken away from an attacker before it expires.
/// Only a hash of the token is stored, for the same reason passwords are hashed:
/// a database leak must not hand out usable credentials.
/// </summary>
public class RefreshToken
{
    public int RefreshTokenId { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedByIp { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }

    /// <summary>Set when this token was rotated, pointing at its successor — makes replay of an old token detectable.</summary>
    public int? ReplacedByTokenId { get; set; }

    public bool IsActive => RevokedAt is null && DateTime.UtcNow < ExpiresAt;
}
