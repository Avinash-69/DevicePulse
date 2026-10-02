using DevicePulse.Api.Entities;
using DevicePulse.Api.Entities.Enums;
using DevicePulse.Api.Services.Alerting;
using FluentAssertions;
using Xunit;

namespace DevicePulse.Tests.Unit;

/// <summary>
/// The rule evaluator is the most important piece of business logic in the application — it
/// decides whether an alert fires — and it is pure, so it can be tested exhaustively with no
/// database, no HTTP and no mocks (§37).
/// </summary>
public sealed class AlertRuleEvaluatorTests
{
    private static AlertRule Rule(
        AlertMetric metric = AlertMetric.Temperature,
        AlertOperator op = AlertOperator.GreaterThan,
        double threshold = 40,
        bool enabled = true,
        int? deviceTypeId = null) => new()
        {
            AlertRuleId = 1,
            Name = "Test rule",
            Metric = metric,
            Operator = op,
            Threshold = threshold,
            Severity = AlertSeverity.High,
            IsEnabled = enabled,
            DeviceTypeId = deviceTypeId
        };

    private static MetricSnapshot Reading(double temperature = 25, double battery = 80, int signal = -60) =>
        new(temperature, battery, signal, null);

    [Theory]
    [InlineData(41, true)]   // above
    [InlineData(40, false)]  // exactly at the threshold is NOT "greater than"
    [InlineData(39, false)]  // below
    public void GreaterThan_fires_only_strictly_above_the_threshold(double temperature, bool expected)
    {
        var triggered = AlertRuleEvaluator.IsTriggered(
            Rule(op: AlertOperator.GreaterThan, threshold: 40), Reading(temperature), out var observed);

        triggered.Should().Be(expected);
        observed.Should().Be(temperature);
    }

    [Theory]
    [InlineData(40, true)]   // boundary included
    [InlineData(40.1, true)]
    [InlineData(39.9, false)]
    public void GreaterThanOrEqual_includes_the_boundary(double temperature, bool expected)
    {
        AlertRuleEvaluator
            .IsTriggered(Rule(op: AlertOperator.GreaterThanOrEqual, threshold: 40), Reading(temperature), out _)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(14, true)]
    [InlineData(15, false)]
    [InlineData(16, false)]
    public void LessThan_fires_only_strictly_below_the_threshold(double battery, bool expected)
    {
        AlertRuleEvaluator
            .IsTriggered(
                Rule(metric: AlertMetric.Battery, op: AlertOperator.LessThan, threshold: 15),
                Reading(battery: battery),
                out _)
            .Should().Be(expected);
    }

    [Fact]
    public void A_disabled_rule_never_fires_however_extreme_the_reading()
    {
        // Disabling is the supported way to silence a noisy rule, so it has to be absolute —
        // a disabled rule that still fired would make the toggle meaningless.
        AlertRuleEvaluator
            .IsTriggered(Rule(threshold: 40, enabled: false), Reading(temperature: 500), out _)
            .Should().BeFalse();
    }

    [Fact]
    public void A_missing_metric_is_not_treated_as_zero()
    {
        // The regression this guards against: a "battery < 15" rule firing for every device
        // whose reading carried no battery value, because null collapsed to 0.
        var snapshotWithNoBattery = new MetricSnapshot(Temperature: 25, Battery: null, SignalStrength: -60, LastSeenAgeSeconds: null);

        AlertRuleEvaluator
            .IsTriggered(
                Rule(metric: AlertMetric.Battery, op: AlertOperator.LessThan, threshold: 15),
                snapshotWithNoBattery,
                out _)
            .Should().BeFalse();
    }

    [Fact]
    public void An_offline_rule_never_fires_against_a_freshly_arrived_reading()
    {
        // A reading that just arrived proves the device is not silent, so LastSeenAgeSeconds is
        // deliberately absent from a reading snapshot.
        AlertRuleEvaluator
            .IsTriggered(
                Rule(metric: AlertMetric.LastSeenAgeSeconds, op: AlertOperator.GreaterThan, threshold: 300),
                MetricSnapshot.FromReading(new Telemetry { Temperature = 25, Battery = 80, SignalStrength = -60 }),
                out _)
            .Should().BeFalse();
    }

    [Fact]
    public void An_offline_rule_fires_against_an_offline_check_snapshot()
    {
        AlertRuleEvaluator
            .IsTriggered(
                Rule(metric: AlertMetric.LastSeenAgeSeconds, op: AlertOperator.GreaterThan, threshold: 300),
                MetricSnapshot.ForOfflineCheck(301),
                out var observed)
            .Should().BeTrue();

        observed.Should().Be(301);
    }

    [Theory]
    [InlineData(-101, true)]
    [InlineData(-100, false)]
    [InlineData(-99, false)]
    public void Signal_strength_compares_correctly_despite_being_negative(int signal, bool expected)
    {
        // Signal strength is in dBm, so "weaker" means a more negative number. Getting the
        // sign wrong here would invert the rule entirely.
        AlertRuleEvaluator
            .IsTriggered(
                Rule(metric: AlertMetric.SignalStrength, op: AlertOperator.LessThan, threshold: -100),
                Reading(signal: signal),
                out _)
            .Should().Be(expected);
    }

    [Fact]
    public void Equality_uses_a_tolerance_rather_than_exact_float_comparison()
    {
        // 0.1 + 0.2 is not 0.3 in binary floating point. An exact comparison would make an
        // EqualTo rule fail in a way that looks like a bug in the alerting system.
        AlertRuleEvaluator.Compare(0.1 + 0.2, AlertOperator.EqualTo, 0.3).Should().BeTrue();
    }

    [Fact]
    public void A_rule_with_no_device_type_applies_to_every_device_type()
    {
        AlertRuleEvaluator.AppliesTo(Rule(deviceTypeId: null), deviceTypeId: 7).Should().BeTrue();
    }

    [Fact]
    public void A_scoped_rule_applies_only_to_its_own_device_type()
    {
        AlertRuleEvaluator.AppliesTo(Rule(deviceTypeId: 3), deviceTypeId: 3).Should().BeTrue();
        AlertRuleEvaluator.AppliesTo(Rule(deviceTypeId: 3), deviceTypeId: 4).Should().BeFalse();
    }

    [Fact]
    public void The_message_states_the_device_the_observed_value_and_the_rule()
    {
        // The message is stored on the alert and has to stay meaningful after the rule is
        // edited or deleted, so it must carry all three facts at fire time.
        var message = AlertRuleEvaluator.BuildMessage(
            Rule(threshold: 40), deviceName: "Cold Store 3", observedValue: 47.25);

        message.Should().Contain("Cold Store 3");
        message.Should().Contain("47.25");
        message.Should().Contain("40");
        message.Should().Contain("Test rule");
    }

    [Fact]
    public void The_offline_message_reads_as_a_silence_not_as_a_reading()
    {
        var message = AlertRuleEvaluator.BuildMessage(
            Rule(metric: AlertMetric.LastSeenAgeSeconds, threshold: 300), "Gateway 1", 420);

        message.Should().Contain("has not reported");
        message.Should().NotContain("reported LastSeenAgeSeconds of");
    }

    [Fact]
    public void Every_operator_in_the_enum_has_a_defined_comparison()
    {
        // Guards the closed-vocabulary promise (Appendix C item 5): adding an operator to the
        // enum without implementing it would silently produce a rule that never fires.
        foreach (var op in Enum.GetValues<AlertOperator>())
        {
            var actedOn = AlertRuleEvaluator.Compare(10, op, 5) || AlertRuleEvaluator.Compare(5, op, 10)
                          || AlertRuleEvaluator.Compare(5, op, 5);

            actedOn.Should().BeTrue($"operator {op} should evaluate to true for at least one of 10>5, 5<10, 5==5");
        }
    }

    [Fact]
    public void Every_metric_in_the_enum_resolves_from_a_snapshot()
    {
        var full = new MetricSnapshot(25, 80, -60, 100);

        foreach (var metric in Enum.GetValues<AlertMetric>())
            AlertRuleEvaluator.ResolveMetric(metric, full).Should().NotBeNull($"metric {metric} should be resolvable");
    }
}
