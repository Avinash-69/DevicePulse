using System.ComponentModel.DataAnnotations;
using DevicePulse.Api.Models.Common;

namespace DevicePulse.Api.Models;

/// <summary>
/// A single reading. DeviceId is absent on purpose: when a device posts with its own API key,
/// the device identity comes from the credential, not from the body — otherwise one device
/// could submit readings attributed to another.
/// </summary>
public sealed record TelemetryRequest(
    [Range(-273, 1000)] double Temperature,
    [Range(0, 100)] double Battery,
    [Range(-150, 0)] int SignalStrength,

    // <summary>When the device took the reading. Defaults to server time when omitted.</summary>
    DateTime? RecordedAt,

    // Optional dedupe key so a retried POST does not create a second reading (Appendix D.1).
    [MaxLength(100)] string? MessageId);

/// <summary>
/// Used by the operator-facing endpoint and by the simulator, which authenticates as a user
/// holding telemetry.ingest and therefore has to say which device it is reporting for.
/// </summary>
public sealed record TelemetryIngestRequest(
    [Range(1, int.MaxValue)] int DeviceId,
    [Range(-273, 1000)] double Temperature,
    [Range(0, 100)] double Battery,
    [Range(-150, 0)] int SignalStrength,
    DateTime? RecordedAt,
    [MaxLength(100)] string? MessageId);

public sealed record TelemetryResponse(
    long TelemetryId,
    int DeviceId,
    double Temperature,
    double Battery,
    int SignalStrength,
    DateTime RecordedAt,
    DateTime ReceivedAt);

/// <summary>
/// Result of an ingest. <paramref name="Duplicate"/> is true when the reading was recognised as
/// a retry and discarded — the caller still gets 200, because from the device's point of view
/// the reading was successfully delivered, and telling it otherwise would make it retry forever.
/// </summary>
public sealed record TelemetryIngestResponse(
    long TelemetryId,
    int DeviceId,
    DateTime RecordedAt,
    bool Duplicate,
    int AlertsRaised);

public sealed record TelemetryQuery : PagedQuery
{
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
}

/// <summary>One bucket of an aggregated trend series, used by the dashboard and device charts.</summary>
public sealed record TelemetryTrendPoint(
    DateTime BucketStart,
    double AvgTemperature,
    double MinTemperature,
    double MaxTemperature,
    double AvgBattery,
    int ReadingCount);

public sealed record BulkTelemetryRequest(
    [Required, MinLength(1), MaxLength(1000)] IReadOnlyList<TelemetryIngestRequest> Readings);

public sealed record BulkTelemetryResponse(int Accepted, int Duplicates, int Rejected, int AlertsRaised, IReadOnlyList<string> Errors);
