using DevicePulse.Api.Data;
using DevicePulse.Api.Entities;
using DevicePulse.Api.Entities.Enums;
using DevicePulse.Api.Models;
using DevicePulse.Api.Services.Alerting;
using DevicePulse.Api.Services.Configuration;
using Microsoft.EntityFrameworkCore;

namespace DevicePulse.Api.Services;

/// <summary>
/// The batched ingestion path.
///
/// Why this exists separately from <see cref="TelemetryService.IngestAsync"/>: the first version
/// of bulk ingest simply looped over the single-reading method. Measured with the simulator at
/// 200 devices, that gave roughly 120 readings/sec, because each reading cost its own
/// SaveChanges round trip plus a cooldown query per matching rule.
///
/// This version does the same work set-at-a-time:
///   * device rows loaded once for the whole batch,
///   * existing MessageIds fetched in one query instead of one lookup per reading,
///   * alert rules loaded once rather than per reading,
///   * recent alerts for the cooldown window fetched once,
///   * a single SaveChanges for every reading, device update and alert in the batch.
///
/// This is the optimisation Development Rules 7 and 9 permit: driven by a measurement of a real
/// bottleneck, on the path the load tests actually exercise, not by speculation about scale.
/// </summary>
public sealed class TelemetryBulkIngest
{
    private readonly DevicePulseDbContext _db;
    private readonly IRuntimeSettings _settings;
    private readonly ILogger<TelemetryBulkIngest> _logger;

    public TelemetryBulkIngest(
        DevicePulseDbContext db,
        IRuntimeSettings settings,
        ILogger<TelemetryBulkIngest> logger)
    {
        _db = db;
        _settings = settings;
        _logger = logger;
    }

    public async Task<BulkTelemetryResponse> IngestAsync(BulkTelemetryRequest request, CancellationToken ct)
    {
        var accepted = 0;
        var duplicates = 0;
        var rejected = 0;
        var alertsRaised = 0;
        var errors = new List<string>();

        var requestedIds = request.Readings.Select(r => r.DeviceId).Distinct().ToList();

        var devices = await _db.Devices
            .Where(d => requestedIds.Contains(d.DeviceId))
            .ToDictionaryAsync(d => d.DeviceId, ct);

        // Every MessageId in the batch, checked in one query. Doing this per reading was the
        // single largest cost in the naive version.
        var messageIds = request.Readings
            .Where(r => !string.IsNullOrWhiteSpace(r.MessageId))
            .Select(r => r.MessageId!)
            .Distinct()
            .ToList();

        var seenMessageIds = messageIds.Count == 0
            ? []
            : (await _db.Telemetry
                .Where(t => t.MessageId != null
                         && messageIds.Contains(t.MessageId)
                         && requestedIds.Contains(t.DeviceId))
                .Select(t => new { t.DeviceId, t.MessageId })
                .ToListAsync(ct))
              .Select(t => (t.DeviceId, t.MessageId!))
              .ToHashSet();

        var evaluationEnabled = _settings.AlertEvaluationEnabled();
        var (minTemp, maxTemp) = _settings.TemperatureBounds();

        // Rules loaded once for the batch. Offline rules are excluded: a reading that just
        // arrived proves the device is not silent.
        var rules = evaluationEnabled
            ? await _db.AlertRules.AsNoTracking()
                .Where(r => r.IsEnabled && r.Metric != AlertMetric.LastSeenAgeSeconds)
                .ToListAsync(ct)
            : [];

        // The longest cooldown in play bounds how far back the cooldown check has to look, so
        // one query covers every rule instead of one per rule per reading.
        var cooldownState = await LoadCooldownStateAsync(rules, requestedIds, ct);

        foreach (var item in request.Readings.OrderBy(r => r.RecordedAt ?? DateTime.UtcNow))
        {
            if (!devices.TryGetValue(item.DeviceId, out var device))
            {
                rejected++;
                AddError(errors, $"Device {item.DeviceId} does not exist.");
                continue;
            }

            if (device.LifecycleStatus == LifecycleStatus.Retired)
            {
                rejected++;
                AddError(errors, $"Device {device.DeviceCode} is retired and no longer accepts telemetry.");
                continue;
            }

            if (item.Temperature < minTemp || item.Temperature > maxTemp)
            {
                rejected++;
                AddError(errors, $"Device {device.DeviceCode}: temperature must be between {minTemp} and {maxTemp} °C.");
                continue;
            }

            if (item.Battery is < 0 or > 100)
            {
                rejected++;
                AddError(errors, $"Device {device.DeviceCode}: battery must be between 0 and 100 percent.");
                continue;
            }

            if (item.MessageId is not null && !seenMessageIds.Add((item.DeviceId, item.MessageId)))
            {
                // Already in the database, or earlier in this same batch. Either way it is a
                // retry, and the correct response is to accept it without storing it twice.
                duplicates++;
                continue;
            }

            var recordedAt = NormalizeRecordedAt(item.RecordedAt);

            var reading = new Telemetry
            {
                DeviceId = item.DeviceId,
                Temperature = item.Temperature,
                Battery = item.Battery,
                SignalStrength = item.SignalStrength,
                RecordedAt = recordedAt,
                ReceivedAt = DateTime.UtcNow,
                MessageId = item.MessageId
            };

            _db.Telemetry.Add(reading);
            accepted++;

            if (device.LastSeenAt is null || recordedAt > device.LastSeenAt)
                device.LastSeenAt = recordedAt;

            var wasOffline = device.ConnectivityStatus != ConnectivityStatus.Online;
            device.ConnectivityStatus = ConnectivityStatus.Online;

            if (device.LifecycleStatus == LifecycleStatus.Registered)
                device.LifecycleStatus = LifecycleStatus.Active;

            if (wasOffline && _settings.AutoResolveOfflineAlerts())
                await AutoResolveOfflineAsync(device.DeviceId, ct);

            alertsRaised += EvaluateRules(device, reading, rules, cooldownState);
        }

        // One round trip for the whole batch. This is what turns N saves into 1.
        await _db.SaveChangesAsync(ct);

        if (rejected > 0)
        {
            _logger.LogWarning("Bulk ingest rejected {Rejected} of {Total} reading(s).",
                rejected, request.Readings.Count);
        }

        _logger.LogInformation(
            "Bulk ingest: {Accepted} accepted, {Duplicates} duplicate, {Rejected} rejected, {Alerts} alert(s) raised.",
            accepted, duplicates, rejected, alertsRaised);

        return new BulkTelemetryResponse(accepted, duplicates, rejected, alertsRaised, errors);
    }

    /// <summary>
    /// The most recent fire time per (device, rule), for every rule that has a cooldown. Loaded
    /// once and then maintained in memory as the batch is processed, so cooldown is enforced
    /// both against history and within the batch itself.
    /// </summary>
    private async Task<Dictionary<(int DeviceId, int RuleId), DateTime>> LoadCooldownStateAsync(
        List<AlertRule> rules, List<int> deviceIds, CancellationToken ct)
    {
        var ruleIds = rules.Where(r => r.CooldownSeconds > 0).Select(r => r.AlertRuleId).ToList();

        if (ruleIds.Count == 0)
            return [];

        var longestCooldown = rules.Where(r => r.CooldownSeconds > 0).Max(r => r.CooldownSeconds);
        var since = DateTime.UtcNow.AddSeconds(-longestCooldown);

        var rows = await _db.Alerts.AsNoTracking()
            .Where(a => a.AlertRuleId != null
                     && ruleIds.Contains(a.AlertRuleId.Value)
                     && deviceIds.Contains(a.DeviceId)
                     && a.CreatedAt >= since)
            .GroupBy(a => new { a.DeviceId, a.AlertRuleId })
            .Select(g => new { g.Key.DeviceId, g.Key.AlertRuleId, LastFired = g.Max(a => a.CreatedAt) })
            .ToListAsync(ct);

        return rows.ToDictionary(r => (r.DeviceId, r.AlertRuleId!.Value), r => r.LastFired);
    }

    private int EvaluateRules(
        Device device,
        Telemetry reading,
        List<AlertRule> rules,
        Dictionary<(int DeviceId, int RuleId), DateTime> cooldownState)
    {
        if (rules.Count == 0)
            return 0;

        var snapshot = MetricSnapshot.FromReading(reading);
        var raised = 0;
        var now = DateTime.UtcNow;

        foreach (var rule in rules)
        {
            if (!AlertRuleEvaluator.AppliesTo(rule, device.DeviceTypeId))
                continue;

            if (!AlertRuleEvaluator.IsTriggered(rule, snapshot, out var observedValue))
                continue;

            var key = (device.DeviceId, rule.AlertRuleId);

            if (rule.CooldownSeconds > 0
                && cooldownState.TryGetValue(key, out var lastFired)
                && (now - lastFired).TotalSeconds < rule.CooldownSeconds)
            {
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
                TriggeredValue = observedValue,
                Threshold = rule.Threshold,
                CreatedAt = now
            });

            // Recorded immediately so later readings in this same batch see the cooldown,
            // even though nothing has been saved yet.
            cooldownState[key] = now;
            raised++;
        }

        return raised;
    }

    private async Task AutoResolveOfflineAsync(int deviceId, CancellationToken ct)
    {
        var open = await _db.Alerts
            .Where(a => a.DeviceId == deviceId
                     && a.Metric == AlertMetric.LastSeenAgeSeconds
                     && a.Status != AlertStatus.Resolved)
            .ToListAsync(ct);

        foreach (var alert in open)
        {
            alert.Status = AlertStatus.Resolved;
            alert.ResolvedAt = DateTime.UtcNow;
            alert.ResolutionNote = "Resolved automatically: the device started reporting again.";
        }
    }

    private static void AddError(List<string> errors, string message)
    {
        // Capped so a batch of a thousand bad readings returns a usable summary rather than a
        // thousand-line response body.
        if (errors.Count < 20 && !errors.Contains(message))
            errors.Add(message);
    }

    private DateTime NormalizeRecordedAt(DateTime? recordedAt)
    {
        if (recordedAt is null)
            return DateTime.UtcNow;

        var value = recordedAt.Value.Kind switch
        {
            DateTimeKind.Utc => recordedAt.Value,
            DateTimeKind.Local => recordedAt.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(recordedAt.Value, DateTimeKind.Utc)
        };

        var now = DateTime.UtcNow;

        return value > now.AddMinutes(5) ? now : value;
    }
}
