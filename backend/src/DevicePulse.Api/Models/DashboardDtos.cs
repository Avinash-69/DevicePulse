using DevicePulse.Api.Entities.Enums;

namespace DevicePulse.Api.Models;

/// <summary>
/// Everything the dashboard needs, in one response. Deliberately a single round trip rather
/// than six: the widgets are always rendered together, and six parallel requests from the SPA
/// would mean six connection-pool slots and six independent reads of the same tables.
/// </summary>
public sealed record DashboardSummaryResponse(
    DeviceCounts Devices,
    AlertCounts Alerts,
    IReadOnlyList<SeverityBucket> AlertsBySeverity,
    IReadOnlyList<LocationBucket> DevicesByLocation,
    IReadOnlyList<DeviceTypeBucket> DevicesByType,
    IReadOnlyList<TelemetryTrendPoint> TemperatureTrend,
    IReadOnlyList<AlertResponse> RecentAlerts,
    IReadOnlyList<DeviceHealthRow> DevicesNeedingAttention,
    int TrendHours,
    DateTime GeneratedAt);

public sealed record DeviceCounts(int Total, int Online, int Offline, int Unknown, int Retired, int Active);

public sealed record AlertCounts(int Open, int Acknowledged, int Critical, int ResolvedToday);

public sealed record SeverityBucket(AlertSeverity Severity, int Count);
public sealed record LocationBucket(string Location, int Total, int Online, int Offline);
public sealed record DeviceTypeBucket(string DeviceType, int Count);

/// <summary>A device that is offline or has an unresolved alert — the "what do I look at first" list.</summary>
public sealed record DeviceHealthRow(
    int DeviceId,
    string DeviceCode,
    string DeviceName,
    string LocationName,
    ConnectivityStatus ConnectivityStatus,
    DateTime? LastSeenAt,
    double? LastBattery,
    double? LastTemperature,
    int OpenAlertCount,
    AlertSeverity? HighestOpenSeverity);
