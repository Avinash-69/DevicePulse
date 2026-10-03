using DevicePulse.Api.Entities.Enums;

namespace DevicePulse.Api.Entities;

/// <summary>
/// Configurable business logic, not configuration scalars — which is why this is its own
/// structured table rather than a row in SystemSettings (§10 of the master reference).
/// </summary>
public class AlertRule
{
    public int AlertRuleId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public AlertMetric Metric { get; set; }
    public AlertOperator Operator { get; set; }
    public double Threshold { get; set; }
    public AlertSeverity Severity { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Suppression window after firing. Without it, a device sitting at 41°C and reporting
    /// every 2 seconds generates an alert every 2 seconds — technically correct, operationally useless.
    /// </summary>
    public int CooldownSeconds { get; set; } = 300;

    /// <summary>Null means the rule applies to every device type; set to scope it to one.</summary>
    public int? DeviceTypeId { get; set; }
    public DeviceType? DeviceType { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public byte[]? RowVersion { get; set; }

    public ICollection<Alert> Alerts { get; set; } = new List<Alert>();
}

public class Alert
{
    public long AlertId { get; set; }

    public int DeviceId { get; set; }
    public Device? Device { get; set; }

    /// <summary>Nullable so an alert survives the deletion of the rule that produced it — history must not evaporate.</summary>
    public int? AlertRuleId { get; set; }
    public AlertRule? AlertRule { get; set; }

    public AlertMetric Metric { get; set; }
    public string Message { get; set; } = string.Empty;
    public AlertSeverity Severity { get; set; }
    public AlertStatus Status { get; set; } = AlertStatus.Open;

    /// <summary>The reading that tripped the rule, captured at fire time so the alert is self-explanatory later.</summary>
    public double TriggeredValue { get; set; }
    public double Threshold { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? AcknowledgedAt { get; set; }
    public int? AcknowledgedByUserId { get; set; }
    public User? AcknowledgedByUser { get; set; }

    public DateTime? ResolvedAt { get; set; }
    public int? ResolvedByUserId { get; set; }
    public User? ResolvedByUser { get; set; }
    public string? ResolutionNote { get; set; }
}
