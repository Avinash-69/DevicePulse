using System.Text.Json;
using DevicePulse.Api.Data;
using DevicePulse.Api.Entities;
using DevicePulse.Api.Models.Common;
using DevicePulse.Api.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace DevicePulse.Api.Services;

public interface IAuditService
{
    /// <summary>
    /// Stages an audit entry on the current unit of work. It is *not* saved here — the caller's
    /// SaveChangesAsync commits the business change and its audit record in the same transaction,
    /// so the two can never disagree about what happened.
    /// </summary>
    void Record(
        string action,
        string entityType,
        object? entityId,
        object? oldValue = null,
        object? newValue = null,
        int? actingUserId = null,
        string? actingUserEmail = null);

    Task<PagedResult<Models.AuditLogResponse>> QueryAsync(Models.AuditLogQuery query, CancellationToken ct = default);
}

public sealed class AuditService : IAuditService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly DevicePulseDbContext _db;
    private readonly ICurrentUser _currentUser;

    public AuditService(DevicePulseDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    /// <param name="actingUserId">
    /// Explicit actor, for the one case where the actor is not yet on the request principal:
    /// a login is audited before the token exists, so <see cref="ICurrentUser"/> is still
    /// anonymous and would record the entry against nobody.
    /// </param>
    public void Record(
        string action,
        string entityType,
        object? entityId,
        object? oldValue = null,
        object? newValue = null,
        int? actingUserId = null,
        string? actingUserEmail = null)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = actingUserId ?? _currentUser.UserId,
            UserEmail = actingUserEmail ?? _currentUser.Email,
            Action = action,
            EntityType = entityType,
            EntityId = entityId?.ToString(),
            OldValue = Serialize(oldValue),
            NewValue = Serialize(newValue),
            Timestamp = DateTime.UtcNow,
            IpAddress = _currentUser.IpAddress,
            CorrelationId = _currentUser.CorrelationId
        });
    }

    public async Task<PagedResult<Models.AuditLogResponse>> QueryAsync(Models.AuditLogQuery query, CancellationToken ct = default)
    {
        var q = _db.AuditLogs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.EntityType))
            q = q.Where(a => a.EntityType == query.EntityType);

        if (!string.IsNullOrWhiteSpace(query.Action))
            q = q.Where(a => a.Action == query.Action);

        if (query.UserId is not null)
            q = q.Where(a => a.UserId == query.UserId);

        if (query.From is not null)
            q = q.Where(a => a.Timestamp >= query.From);

        if (query.To is not null)
            q = q.Where(a => a.Timestamp <= query.To);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(a => a.Action.Contains(term)
                          || a.EntityType.Contains(term)
                          || (a.UserEmail != null && a.UserEmail.Contains(term)));
        }

        // Count before paging, on the filtered query — the client needs the total to render
        // pager controls, and it must reflect the filters, not the whole table.
        var total = await q.CountAsync(ct);

        var items = await q
            .OrderByDescending(a => a.Timestamp)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(a => new Models.AuditLogResponse(
                a.AuditId, a.UserId, a.UserEmail, a.Action, a.EntityType, a.EntityId,
                a.OldValue, a.NewValue, a.Timestamp, a.IpAddress, a.CorrelationId))
            .ToListAsync(ct);

        return new PagedResult<Models.AuditLogResponse>(items, query.Page, query.PageSize, total);
    }

    /// <summary>
    /// Only the caller's chosen projection is serialised — never a whole entity. That is what
    /// keeps password hashes and tokens out of the audit trail structurally rather than by
    /// remembering to exclude them each time (§17, Appendix D.5).
    /// </summary>
    private static string? Serialize(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value, JsonOptions);
}

/// <summary>Action names used in audit entries. Constants so queries and tests don't rely on free-text strings.</summary>
public static class AuditActions
{
    public const string UserCreated = "User.Created";
    public const string UserUpdated = "User.Updated";
    public const string UserStatusChanged = "User.StatusChanged";
    public const string UserRolesChanged = "User.RolesChanged";
    public const string UserPasswordReset = "User.PasswordReset";

    public const string RoleCreated = "Role.Created";
    public const string RoleUpdated = "Role.Updated";
    public const string RolePermissionsChanged = "Role.PermissionsChanged";

    public const string DeviceCreated = "Device.Created";
    public const string DeviceUpdated = "Device.Updated";
    public const string DeviceRetired = "Device.Retired";
    public const string DeviceApiKeyIssued = "Device.ApiKeyIssued";
    public const string DeviceApiKeyRevoked = "Device.ApiKeyRevoked";

    public const string AlertRuleCreated = "AlertRule.Created";
    public const string AlertRuleUpdated = "AlertRule.Updated";
    public const string AlertRuleStatusChanged = "AlertRule.StatusChanged";
    public const string AlertRuleDeleted = "AlertRule.Deleted";

    public const string AlertAcknowledged = "Alert.Acknowledged";
    public const string AlertResolved = "Alert.Resolved";

    public const string SettingChanged = "Setting.Changed";

    public const string DeviceTypeCreated = "DeviceType.Created";
    public const string DeviceTypeUpdated = "DeviceType.Updated";
    public const string LocationCreated = "Location.Created";
    public const string LocationUpdated = "Location.Updated";

    public const string LoginSucceeded = "Auth.LoginSucceeded";
    public const string LoginFailed = "Auth.LoginFailed";
    public const string AccountLockedOut = "Auth.AccountLockedOut";
}
