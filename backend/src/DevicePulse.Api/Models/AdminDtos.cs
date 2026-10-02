using System.ComponentModel.DataAnnotations;
using DevicePulse.Api.Entities.Enums;
using DevicePulse.Api.Models.Common;

namespace DevicePulse.Api.Models;

// ---------- Users ----------

public sealed record UserResponse(
    int UserId,
    string Name,
    string Email,
    bool IsActive,
    bool IsLockedOut,
    DateTime? LockedOutUntil,
    DateTime? LastLoginAt,
    IReadOnlyList<RoleSummary> Roles,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record RoleSummary(int RoleId, string Name);

public sealed record CreateUserRequest(
    [Required, MaxLength(200)] string Name,
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MaxLength(256)] string Password,
    [Required, MinLength(1)] IReadOnlyList<int> RoleIds);

public sealed record UpdateUserRequest(
    [Required, MaxLength(200)] string Name,
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MinLength(1)] IReadOnlyList<int> RoleIds);

public sealed record SetUserStatusRequest(bool IsActive);

public sealed record ResetUserPasswordRequest([Required, MaxLength(256)] string NewPassword);

public sealed record UserQuery : PagedQuery
{
    public string? Search { get; init; }
    public bool? IsActive { get; init; }
    public int? RoleId { get; init; }
}

// ---------- Roles & permissions ----------

public sealed record RoleResponse(
    int RoleId,
    string Name,
    string? Description,
    bool IsActive,
    bool IsSystemRole,
    int UserCount,
    IReadOnlyList<string> Permissions,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record CreateRoleRequest(
    [Required, MaxLength(100)] string Name,
    [MaxLength(500)] string? Description,
    IReadOnlyList<string>? Permissions);

public sealed record UpdateRoleRequest(
    [Required, MaxLength(100)] string Name,
    [MaxLength(500)] string? Description,
    bool IsActive);

public sealed record SetRolePermissionsRequest(
    [Required] IReadOnlyList<string> Permissions);

public sealed record PermissionResponse(int PermissionId, string Key, string Name, string? Description, string Category);

// ---------- Settings ----------

public sealed record SettingResponse(
    int SettingId,
    string Key,
    string Value,
    SettingValueType ValueType,
    string Category,
    string? Description,
    string? Unit,
    bool IsEditable,
    double? MinValue,
    double? MaxValue,
    IReadOnlyList<string>? AllowedValues,
    int Version,
    string? UpdatedBy,
    DateTime? UpdatedAt,
    string? RowVersion);

public sealed record UpdateSettingRequest(
    [Required] string Value,
    [MaxLength(500)] string? ChangeReason,
    string? RowVersion);

public sealed record SettingHistoryResponse(
    int SettingHistoryId,
    string Key,
    int Version,
    string? OldValue,
    string NewValue,
    string? ChangedBy,
    DateTime ChangedAt,
    string? ChangeReason);

// ---------- Audit ----------

public sealed record AuditLogResponse(
    long AuditId,
    int? UserId,
    string? UserEmail,
    string Action,
    string EntityType,
    string? EntityId,
    string? OldValue,
    string? NewValue,
    DateTime Timestamp,
    string? IpAddress,
    string? CorrelationId);

public sealed record AuditLogQuery : PagedQuery
{
    public string? EntityType { get; init; }
    public string? Action { get; init; }
    public int? UserId { get; init; }
    public string? Search { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
}
