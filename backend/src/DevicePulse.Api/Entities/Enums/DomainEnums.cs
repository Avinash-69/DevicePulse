namespace DevicePulse.Api.Entities.Enums;

/// <summary>
/// Where a device sits administratively. Deliberately separate from
/// <see cref="ConnectivityStatus"/> — see Appendix C item 1 of the master reference:
/// "Registered/Active/Retired" is a lifecycle decision a human makes, while
/// "Online/Offline" is an observation the system derives from telemetry.
/// </summary>
public enum LifecycleStatus
{
    Registered = 0,
    Active = 1,
    Inactive = 2,
    Retired = 3
}

/// <summary>Whether the device is currently reachable, derived from LastSeenAt vs. the configured offline timeout.</summary>
public enum ConnectivityStatus
{
    Unknown = 0,
    Online = 1,
    Offline = 2
}

/// <summary>The fixed, validated vocabulary of metrics an alert rule may evaluate (Appendix C item 5).</summary>
public enum AlertMetric
{
    Temperature = 0,
    Battery = 1,
    SignalStrength = 2,
    /// <summary>Special case: evaluated by the offline-detection worker against Device.LastSeenAt, not against a telemetry reading.</summary>
    LastSeenAgeSeconds = 3
}

/// <summary>The fixed, validated vocabulary of comparison operators. No free-text expressions, no eval (Appendix C item 5).</summary>
public enum AlertOperator
{
    GreaterThan = 0,
    GreaterThanOrEqual = 1,
    LessThan = 2,
    LessThanOrEqual = 3,
    EqualTo = 4,
    NotEqualTo = 5
}

public enum AlertSeverity
{
    Info = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

public enum AlertStatus
{
    Open = 0,
    Acknowledged = 1,
    Resolved = 2
}

/// <summary>Declared type of a <see cref="SystemSetting"/> value, so the admin UI can render the right control and the backend can validate.</summary>
public enum SettingValueType
{
    String = 0,
    Integer = 1,
    Decimal = 2,
    Boolean = 3,
    Enum = 4
}
