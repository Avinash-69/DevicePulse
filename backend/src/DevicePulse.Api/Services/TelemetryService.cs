using DevicePulse.Api.Data;
using DevicePulse.Api.Entities;
using DevicePulse.Api.Entities.Enums;
using DevicePulse.Api.Exceptions;
using DevicePulse.Api.Models;
using DevicePulse.Api.Models.Common;
using DevicePulse.Api.Services.Alerting;
using DevicePulse.Api.Services.Configuration;
using Microsoft.EntityFrameworkCore;

namespace DevicePulse.Api.Services;

public interface ITelemetryService
{
    Task<TelemetryIngestResponse> IngestAsync(int deviceId, TelemetryRequest request, CancellationToken ct = default);
    Task<BulkTelemetryResponse> IngestBulkAsync(BulkTelemetryRequest request, CancellationToken ct = default);
    Task<PagedResult<TelemetryResponse>> GetHistoryAsync(int deviceId, TelemetryQuery query, CancellationToken ct = default);
    Task<TelemetryResponse?> GetLatestAsync(int deviceId, CancellationToken ct = default);
    Task<IReadOnlyList<TelemetryTrendPoint>> GetTrendAsync(int deviceId, int hours, CancellationToken ct = default);
    Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default);
}

public sealed class TelemetryService : ITelemetryService
{
    private readonly DevicePulseDbContext _db;
    private readonly IAlertEngine _alertEngine;
    private readonly TelemetryBulkIngest _bulkIngest;
    private readonly IRuntimeSettings _settings;
    private readonly ILogger<TelemetryService> _logger;

    public TelemetryService(
        DevicePulseDbContext db,
        IAlertEngine alertEngine,
        TelemetryBulkIngest bulkIngest,
        IRuntimeSettings settings,
        ILogger<TelemetryService> logger)
    {
        _db = db;
        _alertEngine = alertEngine;
        _bulkIngest = bulkIngest;
        _settings = settings;
        _logger = logger;
    }

    public async Task<TelemetryIngestResponse> IngestAsync(
        int deviceId, TelemetryRequest request, CancellationToken ct = default)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct)
            ?? throw new NotFoundException(nameof(Device), deviceId);

        if (device.LifecycleStatus == LifecycleStatus.Retired)
            throw new ValidationException("This device is retired and no longer accepts telemetry.");

        var recordedAt = NormalizeRecordedAt(request.RecordedAt);
        ValidateReading(request.Temperature, request.Battery);

        // Idempotency (Appendix D.1): a device on a flaky link re-POSTs a reading it already
        // delivered. Returning the existing row — rather than inserting a second one — is what
        // keeps a retry from double-counting and double-firing an alert rule.
        if (!string.IsNullOrWhiteSpace(request.MessageId))
        {
            var existing = await _db.Telemetry.AsNoTracking()
                .FirstOrDefaultAsync(t => t.DeviceId == deviceId && t.MessageId == request.MessageId, ct);

            if (existing is not null)
            {
                _logger.LogDebug(
                    "Duplicate reading {MessageId} for device {DeviceCode} ignored.",
                    request.MessageId, device.DeviceCode);

                return new TelemetryIngestResponse(existing.TelemetryId, deviceId, existing.RecordedAt, Duplicate: true, AlertsRaised: 0);
            }
        }

        var reading = new Telemetry
        {
            DeviceId = deviceId,
            Temperature = request.Temperature,
            Battery = request.Battery,
            SignalStrength = request.SignalStrength,
            RecordedAt = recordedAt,
            ReceivedAt = DateTime.UtcNow,
            MessageId = request.MessageId
        };

        _db.Telemetry.Add(reading);

        var wasOffline = device.ConnectivityStatus != ConnectivityStatus.Online;

        // LastSeenAt only ever moves forward. A delayed or buffered delivery that is older than
        // what we already have must not drag the device back in time and make it look stale
        // (Appendix D.4: out-of-order delivery is expected, not exceptional).
        if (device.LastSeenAt is null || recordedAt > device.LastSeenAt)
            device.LastSeenAt = recordedAt;

        device.ConnectivityStatus = ConnectivityStatus.Online;

        // A device that reports is in service, whatever its paperwork said.
        if (device.LifecycleStatus == LifecycleStatus.Registered)
            device.LifecycleStatus = LifecycleStatus.Active;

        var alertsRaised = await _alertEngine.EvaluateReadingAsync(device, reading, ct);

        if (wasOffline)
            await _alertEngine.AutoResolveOfflineAlertsAsync(deviceId, ct);

        try
        {
            // One SaveChanges for the reading, the device state change and any alerts: they are
            // one logical event and must not be able to half-commit.
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateMessageId(ex))
        {
            // Two retries of the same reading arriving at once both miss the lookup above. The
            // filtered unique index catches it, and the correct answer is still "accepted".
            _db.ChangeTracker.Clear();

            var existing = await _db.Telemetry.AsNoTracking()
                .FirstOrDefaultAsync(t => t.DeviceId == deviceId && t.MessageId == request.MessageId, ct);

            return new TelemetryIngestResponse(
                existing?.TelemetryId ?? 0, deviceId, recordedAt, Duplicate: true, AlertsRaised: 0);
        }

        return new TelemetryIngestResponse(reading.TelemetryId, deviceId, reading.RecordedAt, Duplicate: false, alertsRaised);
    }

    /// <summary>
    /// Delegates to <see cref="TelemetryBulkIngest"/>, which does the work set-at-a-time.
    /// See that class for why the obvious "loop over IngestAsync" version was replaced.
    /// </summary>
    public Task<BulkTelemetryResponse> IngestBulkAsync(BulkTelemetryRequest request, CancellationToken ct = default) =>
        _bulkIngest.IngestAsync(request, ct);

    public async Task<PagedResult<TelemetryResponse>> GetHistoryAsync(
        int deviceId, TelemetryQuery query, CancellationToken ct = default)
    {
        if (!await _db.Devices.AnyAsync(d => d.DeviceId == deviceId, ct))
            throw new NotFoundException(nameof(Device), deviceId);

        var q = _db.Telemetry.AsNoTracking().Where(t => t.DeviceId == deviceId);

        if (query.From is not null)
            q = q.Where(t => t.RecordedAt >= query.From);

        if (query.To is not null)
            q = q.Where(t => t.RecordedAt <= query.To);

        var total = await q.CountAsync(ct);

        var items = await q
            .OrderByDescending(t => t.RecordedAt)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(t => new TelemetryResponse(
                t.TelemetryId, t.DeviceId, t.Temperature, t.Battery, t.SignalStrength, t.RecordedAt, t.ReceivedAt))
            .ToListAsync(ct);

        return new PagedResult<TelemetryResponse>(items, query.Page, query.PageSize, total);
    }

    /// <summary>
    /// Null, not an exception, when a device has never reported. A newly-registered device
    /// having no readings is a normal state, and a 404 here would force every caller to
    /// treat the expected case as an error.
    /// </summary>
    public async Task<TelemetryResponse?> GetLatestAsync(int deviceId, CancellationToken ct = default)
    {
        if (!await _db.Devices.AnyAsync(d => d.DeviceId == deviceId, ct))
            throw new NotFoundException(nameof(Device), deviceId);

        return await _db.Telemetry.AsNoTracking()
            .Where(t => t.DeviceId == deviceId)
            .OrderByDescending(t => t.RecordedAt)
            .Select(t => new TelemetryResponse(
                t.TelemetryId, t.DeviceId, t.Temperature, t.Battery, t.SignalStrength, t.RecordedAt, t.ReceivedAt))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<TelemetryTrendPoint>> GetTrendAsync(
        int deviceId, int hours, CancellationToken ct = default)
    {
        if (!await _db.Devices.AnyAsync(d => d.DeviceId == deviceId, ct))
            throw new NotFoundException(nameof(Device), deviceId);

        var from = DateTime.UtcNow.AddHours(-Math.Clamp(hours, 1, 720));

        // Aggregated in the database, not in memory: a day of 2-second readings is ~43k rows
        // per device, and shipping all of them to the API to average them would be the first
        // thing to fall over under the simulator's load tests (§36).
        var buckets = await _db.Telemetry.AsNoTracking()
            .Where(t => t.DeviceId == deviceId && t.RecordedAt >= from)
            .GroupBy(t => new { t.RecordedAt.Year, t.RecordedAt.Month, t.RecordedAt.Day, t.RecordedAt.Hour })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                g.Key.Day,
                g.Key.Hour,
                AvgTemperature = g.Average(t => t.Temperature),
                MinTemperature = g.Min(t => t.Temperature),
                MaxTemperature = g.Max(t => t.Temperature),
                AvgBattery = g.Average(t => t.Battery),
                Count = g.Count()
            })
            .ToListAsync(ct);

        return buckets
            .Select(b => new TelemetryTrendPoint(
                new DateTime(b.Year, b.Month, b.Day, b.Hour, 0, 0, DateTimeKind.Utc),
                Math.Round(b.AvgTemperature, 2),
                Math.Round(b.MinTemperature, 2),
                Math.Round(b.MaxTemperature, 2),
                Math.Round(b.AvgBattery, 2),
                b.Count))
            .OrderBy(b => b.BucketStart)
            .ToList();
    }

    public async Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        // ExecuteDeleteAsync issues one DELETE rather than loading every expired row into the
        // change tracker first — the difference between a bounded statement and an OOM on a
        // large table.
        return await _db.Telemetry.Where(t => t.RecordedAt < cutoffUtc).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Plausibility bounds come from runtime settings, so an operator can widen them for an
    /// unusual deployment without a redeploy (§4.1). This is a second, independent check:
    /// the DTO has its own absolute Range attributes, and neither trusts the client.
    /// </summary>
    private void ValidateReading(double temperature, double battery)
    {
        var (min, max) = _settings.TemperatureBounds();
        var errors = new Dictionary<string, string[]>();

        if (temperature < min || temperature > max)
            errors["temperature"] = [$"Temperature must be between {min} and {max} °C."];

        if (battery is < 0 or > 100)
            errors["battery"] = ["Battery must be between 0 and 100 percent."];

        if (errors.Count > 0)
            throw new ValidationException(errors);
    }

    /// <summary>
    /// A device clock can be wrong. A reading stamped in the future would sort above genuinely
    /// current data forever, so anything beyond a small tolerance is pulled back to server time.
    /// </summary>
    private DateTime NormalizeRecordedAt(DateTime? recordedAt)
    {
        if (recordedAt is null)
            return DateTime.UtcNow;

        var value = recordedAt.Value.Kind switch
        {
            DateTimeKind.Utc => recordedAt.Value,
            DateTimeKind.Local => recordedAt.Value.ToUniversalTime(),

            // An unspecified kind is treated as UTC rather than as server-local: clients are
            // documented to send UTC, and guessing local would shift every reading by the
            // server's offset.
            _ => DateTime.SpecifyKind(recordedAt.Value, DateTimeKind.Utc)
        };

        var now = DateTime.UtcNow;

        if (value > now.AddMinutes(5))
        {
            _logger.LogWarning("A reading was stamped {RecordedAt}, in the future; using server time instead.", value);
            return now;
        }

        return value;
    }

    /// <summary>
    /// Recognises the filtered unique index on (DeviceId, MessageId) firing. Matched on the
    /// index name rather than a SQL error number so it stays readable and does not accidentally
    /// swallow a different constraint violation.
    /// </summary>
    private static bool IsDuplicateMessageId(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("UX_Telemetry_DeviceId_MessageId", StringComparison.OrdinalIgnoreCase) == true;
}
