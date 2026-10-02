using System.ComponentModel.DataAnnotations;
using DevicePulse.Api.Entities.Enums;
using DevicePulse.Api.Models.Common;

namespace DevicePulse.Api.Models;

public sealed record AlertResponse(
    long AlertId,
    int DeviceId,
    string DeviceName,
    string DeviceCode,
    int? AlertRuleId,
    string? AlertRuleName,
    AlertMetric Metric,
    string Message,
    AlertSeverity Severity,
    AlertStatus Status,
    double TriggeredValue,
    double Threshold,
    DateTime CreatedAt,
    DateTime? AcknowledgedAt,
    string? AcknowledgedBy,
    DateTime? ResolvedAt,
    string? ResolvedBy,
    string? ResolutionNote);

public sealed record AlertQuery : PagedQuery
{
    public AlertStatus? Status { get; init; }
    public AlertSeverity? Severity { get; init; }
    public int? DeviceId { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
}

public sealed record ResolveAlertRequest([MaxLength(1000)] string? ResolutionNote);

public sealed record AlertRuleResponse(
    int AlertRuleId,
    string Name,
    string? Description,
    AlertMetric Metric,
    AlertOperator Operator,
    double Threshold,
    AlertSeverity Severity,
    bool IsEnabled,
    int CooldownSeconds,
    int? DeviceTypeId,
    string? DeviceTypeName,
    // <summary>Rendered condition, e.g. "Temperature > 40 °C" — saves the UI from re-deriving it.</summary>
    string ConditionSummary,
    int TriggeredCount,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string? RowVersion);

public sealed record CreateAlertRuleRequest(
    [Required, MaxLength(150)] string Name,
    [MaxLength(500)] string? Description,
    AlertMetric Metric,
    AlertOperator Operator,
    double Threshold,
    AlertSeverity Severity,
    bool IsEnabled,
    [Range(0, 86_400)] int CooldownSeconds,
    int? DeviceTypeId);

public sealed record UpdateAlertRuleRequest(
    [Required, MaxLength(150)] string Name,
    [MaxLength(500)] string? Description,
    AlertMetric Metric,
    AlertOperator Operator,
    double Threshold,
    AlertSeverity Severity,
    [Range(0, 86_400)] int CooldownSeconds,
    int? DeviceTypeId,
    string? RowVersion);

public sealed record SetAlertRuleStatusRequest(bool IsEnabled);

/// <summary>
/// The closed vocabulary a rule may be built from, served to the UI so the rule editor renders
/// dropdowns rather than free-text fields (Appendix C item 5).
/// </summary>
public sealed record AlertRuleVocabularyResponse(
    IReadOnlyList<AlertRuleVocabularyItem> Metrics,
    IReadOnlyList<AlertRuleVocabularyItem> Operators,
    IReadOnlyList<AlertRuleVocabularyItem> Severities);

public sealed record AlertRuleVocabularyItem(string Value, string Label, string? Unit = null);
