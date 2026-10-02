using DevicePulse.Api.Data;
using DevicePulse.Api.Entities;
using DevicePulse.Api.Entities.Enums;
using DevicePulse.Api.Services.Configuration;
using Microsoft.EntityFrameworkCore;

namespace DevicePulse.Api.Services.Alerting;

public interface IAlertEngine
{
    /// <summary>
    /// Evaluates every enabled rule against a newly-arrived reading and stages any resulting
    /// alerts on the caller's unit of work. Returns the number raised.
    /// Does not call SaveChanges — the reading and its alerts must commit together.
    /// </summary>
    Task<int> EvaluateReadingAsync(Device device, Telemetry reading, CancellationToken ct = default);

    /// <summary>Evaluates the offline rules for a device the sweeper has found silent.</summary>
    Task<int> EvaluateOfflineAsync(Device device, double lastSeenAgeSeconds, CancellationToken ct = default);

    /// <summary>Closes a device's open offline alerts after it starts reporting again.</summary>
    Task<int> AutoResolveOfflineAlertsAsync(int deviceId, CancellationToken ct = default);
}

/// <summary>
/// Applies the runtime-configured rules to live data.
///
/// The decision logic itself lives in <see cref="AlertRuleEvaluator"/>, which is pure and
/// unit-tested; this class only deals with the parts that need the database — loading rules,
/// enforcing cooldown, and writing alerts.
/// </summary>
public sealed class AlertEngine : IAlertEngine
{
    private readonly DevicePulseDbContext _db;
    private readonly IRuntimeSettings _settings;
    private readonly ILogger<AlertEngine> _logger;

    public AlertEngine(DevicePulseDbContext db, IRuntimeSettings settings, ILogger<AlertEngine> logger)
    {
        _db = db;
        _settings = settings;
        _logger = logger;
    }

    public async Task<int> EvaluateReadingAsync(Device device, Telemetry reading, CancellationToken ct = default)
    {
        if (!_settings.AlertEvaluationEnabled())
            return 0;

        // LastSeenAgeSeconds rules are excluded here by construction: a reading that just
        // arrived means the device was seen a moment ago, so an offline rule can never be true
        // in this context. Those belong to the sweeper.
        var rules = await LoadRulesAsync(
            device.DeviceTypeId,
            excludeMetric: AlertMetric.LastSeenAgeSeconds,
            ct: ct);

        if (rules.Count == 0)
            return 0;

        var snapshot = MetricSnapshot.FromReading(reading);
        return await RaiseMatchingAsync(device, rules, snapshot, ct);
    }

    public async Task<int> EvaluateOfflineAsync(Device device, double lastSeenAgeSeconds, CancellationToken ct = default)
    {
        if (!_settings.AlertEvaluationEnabled())
            return 0;

        var rules = await LoadRulesAsync(device.DeviceTypeId, onlyMetric: AlertMetric.LastSeenAgeSeconds, ct: ct);

        if (rules.Count == 0)
            return 0;

        var snapshot = MetricSnapshot.ForOfflineCheck(lastSeenAgeSeconds);
        return await RaiseMatchingAsync(device, rules, snapshot, ct);
    }

    public async Task<int> AutoResolveOfflineAlertsAsync(int deviceId, CancellationToken ct = default)
    {
        if (!_settings.AutoResolveOfflineAlerts())
            return 0;

        var open = await _db.Alerts
            .Where(a => a.DeviceId == deviceId
                     && a.Metric == AlertMetric.LastSeenAgeSeconds
                     && a.Status != AlertStatus.Resolved)
            .ToListAsync(ct);

        foreach (var alert in open)
        {
            alert.Status = AlertStatus.Resolved;
            alert.ResolvedAt = DateTime.UtcNow;

            // ResolvedByUserId stays null on purpose: no human did this, and attributing it to
            // one would make the audit trail lie.
            alert.ResolutionNote = "Resolved automatically: the device started reporting again.";
        }

        if (open.Count > 0)
            _logger.LogInformation("Auto-resolved {Count} offline alert(s) for device {DeviceId}.", open.Count, deviceId);

        return open.Count;
    }

    private async Task<List<AlertRule>> LoadRulesAsync(
        int deviceTypeId,
        AlertMetric? excludeMetric = null,
        AlertMetric? onlyMetric = null,
        CancellationToken ct = default)
    {
        var q = _db.AlertRules.AsNoTracking().Where(r => r.IsEnabled);

        // A rule with no DeviceTypeId applies to everything; one with a type applies only to
        // that type (AlertRuleEvaluator.AppliesTo documents the same contract).
        q = q.Where(r => r.DeviceTypeId == null || r.DeviceTypeId == deviceTypeId);

        if (excludeMetric is not null)
            q = q.Where(r => r.Metric != excludeMetric);

        if (onlyMetric is not null)
            q = q.Where(r => r.Metric == onlyMetric);

        return await q.ToListAsync(ct);
    }

    private async Task<int> RaiseMatchingAsync(
        Device device, List<AlertRule> rules, MetricSnapshot snapshot, CancellationToken ct)
    {
        var raised = 0;

        foreach (var rule in rules)
        {
            if (!AlertRuleEvaluator.IsTriggered(rule, snapshot, out var observedValue))
                continue;

            if (await IsInCooldownAsync(device.DeviceId, rule, ct))
            {
                _logger.LogDebug(
                    "Rule {RuleName} matched for device {DeviceCode} but is within its {Cooldown}s cooldown.",
                    rule.Name, device.DeviceCode, rule.CooldownSeconds);
                continue;
            }

            _db.Alerts.Add(new Alert
            {
                DeviceId = device.DeviceId,
                AlertRuleId = rule.AlertRuleId,
                Metric = rule.Metric,
                Message = AlertRuleEvaluator.BuildMessage(rule, device.DeviceName, observedValue),
                Severity = rule.Severity,
                Status = AlertStatus.Open,

                // The observed value and the threshold are copied onto the alert so it still
                // explains itself after the rule has been edited or deleted.
                TriggeredValue = observedValue,
                Threshold = rule.Threshold,
                CreatedAt = DateTime.UtcNow
            });

            raised++;

            _logger.LogInformation(
                "Alert raised on device {DeviceCode} by rule {RuleName}: {Metric}={Value} (threshold {Threshold}).",
                device.DeviceCode, rule.Name, rule.Metric, observedValue, rule.Threshold);
        }

        return raised;
    }

    /// <summary>
    /// Cooldown check (§16). Without it, a device parked above its threshold and reporting every
    /// two seconds produces an alert every two seconds — each one technically correct and
    /// collectively useless.
    ///
    /// The query includes alerts staged in this unit of work but not yet saved, so a batch
    /// ingest of several readings cannot slip multiple alerts for the same rule past the window.
    /// </summary>
    private async Task<bool> IsInCooldownAsync(int deviceId, AlertRule rule, CancellationToken ct)
    {
        if (rule.CooldownSeconds <= 0)
            return false;

        var cutoff = DateTime.UtcNow.AddSeconds(-rule.CooldownSeconds);

        var pending = _db.ChangeTracker.Entries<Alert>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .Any(a => a.DeviceId == deviceId && a.AlertRuleId == rule.AlertRuleId && a.CreatedAt >= cutoff);

        if (pending)
            return true;

        return await _db.Alerts
            .AsNoTracking()
            .AnyAsync(a => a.DeviceId == deviceId
                        && a.AlertRuleId == rule.AlertRuleId
                        && a.CreatedAt >= cutoff, ct);
    }
}
