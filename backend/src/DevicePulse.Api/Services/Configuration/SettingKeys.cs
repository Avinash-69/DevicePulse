using DevicePulse.Api.Entities.Enums;

namespace DevicePulse.Api.Services.Configuration;

/// <summary>
/// The catalog of runtime business settings. Like the permission catalog, the *set* of keys is
/// developer-controlled — a key only exists here if some code actually reads it. What is
/// runtime-configurable is each key's *value*, edited through the admin UI (§4.2).
/// This list also drives seeding, so a new setting ships with its type, bounds and default.
/// </summary>
public static class SettingKeys
{
    public const string OfflineTimeoutSeconds = "telemetry.offline.timeout.seconds";
    public const string OfflineSweepIntervalSeconds = "telemetry.offline.sweep.interval.seconds";
    public const string TelemetryRetentionDays = "telemetry.retention.days";
    public const string TelemetryMaxTemperature = "telemetry.validation.max.temperature";
    public const string TelemetryMinTemperature = "telemetry.validation.min.temperature";
    public const string DashboardDefaultPageSize = "dashboard.default.page.size";
    public const string DashboardTrendHours = "dashboard.trend.hours";
    public const string TemperatureUnit = "telemetry.temperature.unit";
    public const string AlertEvaluationEnabled = "alerts.evaluation.enabled";
    public const string AlertAutoResolveOfflineOnReconnect = "alerts.offline.autoresolve";
    public const string AuditRetentionDays = "audit.retention.days";
    public const string SettingsCacheSeconds = "settings.cache.seconds";

    public sealed record Definition(
        string Key,
        string DefaultValue,
        SettingValueType ValueType,
        string Category,
        string Description,
        string? Unit = null,
        double? MinValue = null,
        double? MaxValue = null,
        string? AllowedValues = null,
        bool IsEditable = true);

    public static readonly IReadOnlyList<Definition> All =
    [
        new(OfflineTimeoutSeconds, "300", SettingValueType.Integer, "Telemetry",
            "How long a device may go without reporting before it is marked Offline.",
            "seconds", 30, 86_400),

        new(OfflineSweepIntervalSeconds, "60", SettingValueType.Integer, "Telemetry",
            "How often the offline-detection worker re-checks every device.",
            "seconds", 10, 3_600),

        new(TelemetryRetentionDays, "90", SettingValueType.Integer, "Telemetry",
            "How long raw telemetry readings are kept before being purged.",
            "days", 1, 3_650),

        new(TelemetryMinTemperature, "-50", SettingValueType.Decimal, "Telemetry",
            "Readings below this are rejected as implausible sensor faults.",
            "°C", -273, 100),

        new(TelemetryMaxTemperature, "150", SettingValueType.Decimal, "Telemetry",
            "Readings above this are rejected as implausible sensor faults.",
            "°C", 0, 1_000),

        new(DashboardDefaultPageSize, "20", SettingValueType.Integer, "Dashboard",
            "Default number of rows per page in list views.",
            "rows", 5, 200),

        new(DashboardTrendHours, "24", SettingValueType.Integer, "Dashboard",
            "Time window covered by the dashboard trend charts.",
            "hours", 1, 720),

        new(TemperatureUnit, "Celsius", SettingValueType.Enum, "Display",
            "Unit used when presenting temperatures in the UI.",
            AllowedValues: "Celsius,Fahrenheit"),

        new(AlertEvaluationEnabled, "true", SettingValueType.Boolean, "Alerts",
            "Master switch for rule evaluation. Turning this off stops new alerts without deleting any rule."),

        new(AlertAutoResolveOfflineOnReconnect, "true", SettingValueType.Boolean, "Alerts",
            "Automatically resolve a device's open offline alert when it starts reporting again."),

        new(AuditRetentionDays, "365", SettingValueType.Integer, "Administration",
            "How long audit log entries are kept.",
            "days", 30, 3_650),

        new(SettingsCacheSeconds, "30", SettingValueType.Integer, "Administration",
            "How long settings are cached in memory before being re-read. A write always invalidates immediately; this only bounds staleness across instances.",
            "seconds", 0, 3_600)
    ];
}
