using DevicePulse.Api.Entities.Enums;

namespace DevicePulse.Api.Entities;

/// <summary>
/// A scalar operational/business setting. Typed on purpose (§10.1, Development Rule 6):
/// ValueType + MinValue/MaxValue + Unit are what let the backend validate an edit and let
/// the admin UI render a number input, a toggle or a dropdown instead of a raw text box.
/// This is what keeps the table from degenerating into an untyped EAV dumping ground.
/// </summary>
public class SystemSetting
{
    public int SettingId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public SettingValueType ValueType { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Unit { get; set; }

    /// <summary>False for settings the application exposes read-only — visible in the admin UI, not editable there.</summary>
    public bool IsEditable { get; set; } = true;

    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }

    /// <summary>Comma-separated allowed values, used when ValueType is Enum.</summary>
    public string? AllowedValues { get; set; }

    /// <summary>Monotonic version counter, incremented on every accepted edit. Mirrors the newest SettingHistory row.</summary>
    public int Version { get; set; } = 1;

    public int? UpdatedByUserId { get; set; }
    public User? UpdatedByUser { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public byte[]? RowVersion { get; set; }

    public ICollection<SettingHistory> History { get; set; } = new List<SettingHistory>();
}

/// <summary>
/// Configuration history (§18): every accepted setting change appends a row rather than
/// blindly overwriting, so "who moved the temperature threshold to 45 and when" has an answer.
/// Kept separate from AuditLog because this one is queried per-key to render a value timeline.
/// </summary>
public class SettingHistory
{
    public int SettingHistoryId { get; set; }
    public int SettingId { get; set; }
    public SystemSetting? Setting { get; set; }

    public string Key { get; set; } = string.Empty;
    public int Version { get; set; }
    public string? OldValue { get; set; }
    public string NewValue { get; set; } = string.Empty;

    public int? ChangedByUserId { get; set; }
    public User? ChangedByUser { get; set; }
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
    public string? ChangeReason { get; set; }
}

/// <summary>
/// Who changed what, when, from what, to what (§17). Deliberately stores values as text
/// rather than typed columns — it has to cover every entity type uniformly.
/// Secrets and password hashes are never written here.
/// </summary>
public class AuditLog
{
    public long AuditId { get; set; }

    public int? UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Denormalised on purpose: the audit trail must stay readable even if the user row is later renamed or removed.</summary>
    public string? UserEmail { get; set; }

    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string? IpAddress { get; set; }
    public string? CorrelationId { get; set; }
}

/// <summary>
/// Persisted record of unexpected failures. Distinct from AuditLog (which records intended
/// administrative changes) — this table only ever holds defects.
/// </summary>
public class ServiceLog
{
    public long ServiceLogId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Level { get; set; } = "Error";
    public string Message { get; set; } = string.Empty;
    public string? ExceptionType { get; set; }
    public string? StackTrace { get; set; }
    public string? Source { get; set; }
    public string? RequestPath { get; set; }
    public string? RequestMethod { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
}
