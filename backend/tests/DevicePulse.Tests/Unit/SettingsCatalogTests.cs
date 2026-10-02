using DevicePulse.Api.Entities.Enums;
using DevicePulse.Api.Services.Configuration;
using FluentAssertions;
using Xunit;

namespace DevicePulse.Tests.Unit;

/// <summary>
/// The settings catalog is what keeps runtime configuration typed rather than an untyped EAV
/// dump (§10.1, Development Rule 6). These tests assert that discipline holds as keys are added.
/// </summary>
public sealed class SettingsCatalogTests
{
    [Fact]
    public void Setting_keys_are_unique()
    {
        SettingKeys.All.Select(s => s.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_setting_constant_appears_in_the_catalog()
    {
        // A constant missing from the catalog never gets seeded, so reads of it would silently
        // fall back to a compiled-in default and the admin UI would not show it at all.
        var constants = typeof(SettingKeys)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        var catalogued = SettingKeys.All.Select(s => s.Key).ToHashSet();

        constants.Should().OnlyContain(c => catalogued.Contains(c));
        constants.Should().HaveCount(catalogued.Count);
    }

    [Fact]
    public void Every_default_value_parses_as_its_declared_type()
    {
        // The default is what the application falls back to when the database is unreachable.
        // A default that does not parse would turn a transient outage into a crash.
        foreach (var setting in SettingKeys.All)
        {
            switch (setting.ValueType)
            {
                case SettingValueType.Integer:
                    int.TryParse(setting.DefaultValue, out _)
                        .Should().BeTrue($"{setting.Key} is declared Integer but its default is '{setting.DefaultValue}'");
                    break;

                case SettingValueType.Decimal:
                    double.TryParse(setting.DefaultValue, System.Globalization.CultureInfo.InvariantCulture, out _)
                        .Should().BeTrue($"{setting.Key} is declared Decimal but its default is '{setting.DefaultValue}'");
                    break;

                case SettingValueType.Boolean:
                    bool.TryParse(setting.DefaultValue, out _)
                        .Should().BeTrue($"{setting.Key} is declared Boolean but its default is '{setting.DefaultValue}'");
                    break;

                case SettingValueType.Enum:
                    setting.AllowedValues.Should().NotBeNullOrWhiteSpace($"{setting.Key} is an Enum and must declare AllowedValues");

                    setting.AllowedValues!
                        .Split(',', StringSplitOptions.TrimEntries)
                        .Should().Contain(setting.DefaultValue,
                            $"{setting.Key} default '{setting.DefaultValue}' must be one of its allowed values");
                    break;
            }
        }
    }

    [Fact]
    public void Every_default_value_sits_inside_its_declared_bounds()
    {
        // A default outside its own min/max would be rejected the moment an administrator tried
        // to re-save it, which is a confusing thing to discover in production.
        foreach (var setting in SettingKeys.All)
        {
            if (!double.TryParse(setting.DefaultValue, System.Globalization.CultureInfo.InvariantCulture, out var value))
                continue;

            if (setting.MinValue is not null)
                value.Should().BeGreaterThanOrEqualTo(setting.MinValue.Value, $"{setting.Key} default must respect its own minimum");

            if (setting.MaxValue is not null)
                value.Should().BeLessThanOrEqualTo(setting.MaxValue.Value, $"{setting.Key} default must respect its own maximum");
        }
    }

    [Fact]
    public void Numeric_settings_declare_bounds_so_the_admin_UI_can_validate()
    {
        // Bounds are what let the UI render a constrained control and let the backend reject a
        // nonsensical value. A numeric setting without them is the EAV mess §10 warns about.
        var unbounded = SettingKeys.All
            .Where(s => s.ValueType is SettingValueType.Integer or SettingValueType.Decimal)
            .Where(s => s.MinValue is null || s.MaxValue is null)
            .Select(s => s.Key)
            .ToList();

        unbounded.Should().BeEmpty();
    }

    [Fact]
    public void Min_is_never_above_max()
    {
        SettingKeys.All
            .Where(s => s.MinValue is not null && s.MaxValue is not null)
            .Should().OnlyContain(s => s.MinValue!.Value <= s.MaxValue!.Value);
    }

    [Fact]
    public void Every_setting_has_a_category_and_a_description()
    {
        SettingKeys.All.Should().OnlyContain(s =>
            !string.IsNullOrWhiteSpace(s.Category) && !string.IsNullOrWhiteSpace(s.Description));
    }

    [Fact]
    public void The_temperature_validation_bounds_are_ordered_sensibly()
    {
        var min = SettingKeys.All.Single(s => s.Key == SettingKeys.TelemetryMinTemperature);
        var max = SettingKeys.All.Single(s => s.Key == SettingKeys.TelemetryMaxTemperature);

        double.Parse(min.DefaultValue).Should().BeLessThan(double.Parse(max.DefaultValue));
    }
}
