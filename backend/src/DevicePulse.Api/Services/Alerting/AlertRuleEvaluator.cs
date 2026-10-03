using DevicePulse.Api.Entities;
using DevicePulse.Api.Entities.Enums;

namespace DevicePulse.Api.Services.Alerting;

/// <summary>A snapshot of the values a rule can be evaluated against.</summary>
/// <param name="Temperature">Reading in °C.</param>
/// <param name="Battery">Reading as a percentage.</param>
/// <param name="SignalStrength">Reading in dBm (negative; closer to zero is stronger).</param>
/// <param name="LastSeenAgeSeconds">
/// Seconds since the device last reported. Null when evaluating a freshly-arrived reading,
/// because by definition the device was just seen — the offline worker supplies this instead.
/// </param>
public readonly record struct MetricSnapshot(
    double? Temperature,
    double? Battery,
    int? SignalStrength,
    double? LastSeenAgeSeconds)
{
    public static MetricSnapshot FromReading(Telemetry telemetry) =>
        new(telemetry.Temperature, telemetry.Battery, telemetry.SignalStrength, null);

    public static MetricSnapshot ForOfflineCheck(double lastSeenAgeSeconds) =>
        new(null, null, null, lastSeenAgeSeconds);
}

/// <summary>
/// Decides whether a rule is tripped by a snapshot. Deliberately pure — no DbContext, no
/// clock, no logger — because this is the single most important piece of business logic in the
/// application and it needs to be exhaustively unit-testable (§37).
///
/// The operator and metric vocabularies are closed enums, not parsed expressions
/// (Appendix C item 5): a free-text condition field would be both an injection surface and
/// an untestable maintenance trap.
/// </summary>
public static class AlertRuleEvaluator
{
    /// <summary>
    /// True when the rule's metric is present in the snapshot and the comparison holds.
    /// A missing metric is never a match — an absent reading is not a zero reading.
    /// </summary>
    public static bool IsTriggered(AlertRule rule, MetricSnapshot snapshot, out double observedValue)
    {
        observedValue = 0;

        if (!rule.IsEnabled)
            return false;

        var value = ResolveMetric(rule.Metric, snapshot);
        if (value is null)
            return false;

        observedValue = value.Value;
        return Compare(value.Value, rule.Operator, rule.Threshold);
    }

    public static double? ResolveMetric(AlertMetric metric, MetricSnapshot snapshot) => metric switch
    {
        AlertMetric.Temperature => snapshot.Temperature,
        AlertMetric.Battery => snapshot.Battery,
        AlertMetric.SignalStrength => snapshot.SignalStrength,
        AlertMetric.LastSeenAgeSeconds => snapshot.LastSeenAgeSeconds,
        _ => null
    };

    public static bool Compare(double value, AlertOperator op, double threshold) => op switch
    {
        AlertOperator.GreaterThan => value > threshold,
        AlertOperator.GreaterThanOrEqual => value >= threshold,
        AlertOperator.LessThan => value < threshold,
        AlertOperator.LessThanOrEqual => value <= threshold,

        // Equality on a double is almost never what a user means when they type a threshold,
        // so a tolerance is applied rather than an exact bit comparison.
        AlertOperator.EqualTo => Math.Abs(value - threshold) < Tolerance,
        AlertOperator.NotEqualTo => Math.Abs(value - threshold) >= Tolerance,

        _ => false
    };

    private const double Tolerance = 1e-9;

    /// <summary>
    /// Whether a rule applies to a given device type. A null DeviceTypeId on the rule means
    /// "all types" — scoping is opt-in.
    /// </summary>
    public static bool AppliesTo(AlertRule rule, int deviceTypeId) =>
        rule.DeviceTypeId is null || rule.DeviceTypeId == deviceTypeId;

    /// <summary>
    /// Human-readable description of what tripped, stored on the alert so it stays meaningful
    /// even after the rule is later edited or deleted.
    /// </summary>
    public static string BuildMessage(AlertRule rule, string deviceName, double observedValue)
    {
        var unit = UnitFor(rule.Metric);
        var symbol = SymbolFor(rule.Operator);

        return rule.Metric == AlertMetric.LastSeenAgeSeconds
            ? $"{deviceName} has not reported for {FormatValue(observedValue)}{unit} (rule '{rule.Name}': {symbol} {FormatValue(rule.Threshold)}{unit})."
            : $"{deviceName} reported {rule.Metric} of {FormatValue(observedValue)}{unit} (rule '{rule.Name}': {symbol} {FormatValue(rule.Threshold)}{unit}).";
    }

    private static string FormatValue(double value) =>
        value == Math.Floor(value)
            ? value.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    public static string UnitFor(AlertMetric metric) => metric switch
    {
        AlertMetric.Temperature => "°C",
        AlertMetric.Battery => "%",
        AlertMetric.SignalStrength => " dBm",
        AlertMetric.LastSeenAgeSeconds => "s",
        _ => string.Empty
    };

    public static string SymbolFor(AlertOperator op) => op switch
    {
        AlertOperator.GreaterThan => ">",
        AlertOperator.GreaterThanOrEqual => ">=",
        AlertOperator.LessThan => "<",
        AlertOperator.LessThanOrEqual => "<=",
        AlertOperator.EqualTo => "==",
        AlertOperator.NotEqualTo => "!=",
        _ => "?"
    };
}
