using DevicePulse.Api.Entities.Enums;

namespace DevicePulse.Api.Realtime;

/// <summary>What happened to an alert.</summary>
public enum AlertChange
{
    Raised,
    Acknowledged,
    Resolved
}

/// <summary>
/// An alert was raised, acknowledged or resolved.
///
/// Small on purpose: it says what changed so a screen can decide whether to care, not enough to
/// replace a read. Screens that show aggregates re-fetch them, which keeps the counting logic in
/// one place (the API) instead of re-implementing it in the client from a stream of deltas.
/// </summary>
public sealed record AlertChangedEvent(
    long AlertId,
    int DeviceId,
    AlertChange Change,
    AlertSeverity Severity,
    AlertStatus Status,
    string Message,
    DateTime OccurredAt);

/// <summary>
/// A device's connectivity or lifecycle status changed, or a device was registered.
///
/// Sent on transitions only. LastSeenAt moves on every reading and is not by itself an event,
/// otherwise a 500-device fleet would push 500 messages a second to every open dashboard.
/// </summary>
public sealed record DeviceStatusChangedEvent(
    int DeviceId,
    string DeviceCode,
    string DeviceName,
    ConnectivityStatus ConnectivityStatus,
    LifecycleStatus LifecycleStatus,
    DateTime? LastSeenAt,
    DateTime OccurredAt);
