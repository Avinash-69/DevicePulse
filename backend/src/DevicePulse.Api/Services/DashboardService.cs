using DevicePulse.Api.Data;
using DevicePulse.Api.Entities.Enums;
using DevicePulse.Api.Models;
using DevicePulse.Api.Services.Configuration;
using Microsoft.EntityFrameworkCore;

namespace DevicePulse.Api.Services;

public interface IDashboardService
{
    Task<DashboardSummaryResponse> GetSummaryAsync(CancellationToken ct = default);
}

/// <summary>
/// Builds the monitoring dashboard (§30).
///
/// Everything is aggregated in SQL and returned in one response. The alternative — the SPA
/// firing six requests and counting rows client-side — is the single most common way a
/// dashboard becomes the slowest page in an application.
/// </summary>
public sealed class DashboardService : IDashboardService
{
    private const int RecentAlertLimit = 10;
    private const int AttentionListLimit = 10;

    private readonly DevicePulseDbContext _db;
    private readonly IRuntimeSettings _settings;

    public DashboardService(DevicePulseDbContext db, IRuntimeSettings settings)
    {
        _db = db;
        _settings = settings;
    }

    public async Task<DashboardSummaryResponse> GetSummaryAsync(CancellationToken ct = default)
    {
        var trendHours = _settings.TrendHours();
        var trendFrom = DateTime.UtcNow.AddHours(-trendHours);
        var todayStart = DateTime.UtcNow.Date;

        // Retired devices are excluded from every count: they are kept for history, and
        // including them would make "total devices" disagree with the device list the operator
        // is looking at (Appendix C item 2).
        var liveDevices = _db.Devices.AsNoTracking().Where(d => d.LifecycleStatus != LifecycleStatus.Retired);

        // One grouped query for all the connectivity counters rather than five COUNT(*) round
        // trips over the same table.
        var connectivityCounts = await liveDevices
            .GroupBy(d => d.ConnectivityStatus)
            .Select(g => new ConnectivityCount(g.Key, g.Count()))
            .ToListAsync(ct);

        var lifecycleCounts = await _db.Devices.AsNoTracking()
            .GroupBy(d => d.LifecycleStatus)
            .Select(g => new LifecycleCount(g.Key, g.Count()))
            .ToListAsync(ct);

        var deviceCounts = new DeviceCounts(
            Total: connectivityCounts.Sum(c => c.Count),
            Online: Count(connectivityCounts, ConnectivityStatus.Online),
            Offline: Count(connectivityCounts, ConnectivityStatus.Offline),
            Unknown: Count(connectivityCounts, ConnectivityStatus.Unknown),
            Retired: lifecycleCounts.FirstOrDefault(c => c.Status == LifecycleStatus.Retired)?.Count ?? 0,
            Active: lifecycleCounts.FirstOrDefault(c => c.Status == LifecycleStatus.Active)?.Count ?? 0);

        var alertStatusCounts = await _db.Alerts.AsNoTracking()
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var criticalOpen = await _db.Alerts.AsNoTracking()
            .CountAsync(a => a.Status != AlertStatus.Resolved && a.Severity == AlertSeverity.Critical, ct);

        var resolvedToday = await _db.Alerts.AsNoTracking()
            .CountAsync(a => a.ResolvedAt != null && a.ResolvedAt >= todayStart, ct);

        var alertCounts = new AlertCounts(
            Open: alertStatusCounts.FirstOrDefault(c => c.Status == AlertStatus.Open)?.Count ?? 0,
            Acknowledged: alertStatusCounts.FirstOrDefault(c => c.Status == AlertStatus.Acknowledged)?.Count ?? 0,
            Critical: criticalOpen,
            ResolvedToday: resolvedToday);

        // Only unresolved alerts are bucketed by severity: the chart answers "what needs
        // attention now", and folding in months of resolved history would flatten it into noise.
        var bySeverity = await _db.Alerts.AsNoTracking()
            .Where(a => a.Status != AlertStatus.Resolved)
            .GroupBy(a => a.Severity)
            .Select(g => new SeverityBucket(g.Key, g.Count()))
            .ToListAsync(ct);

        // Grouped on the foreign key rather than on the joined name, and the conditional counts
        // are expressed as SUM(CASE ...) via Sum(... ? 1 : 0). Grouping by Location.Name forces a
        // join into the grouping key, which SQL Server cannot combine with several conditional
        // counts -- EF gives up and refuses to translate it. The names are attached afterwards
        // from a small lookup, which is one extra trivial query instead of a client-side group.
        var locationRows = await liveDevices
            .GroupBy(d => d.LocationId)
            .Select(g => new
            {
                LocationId = g.Key,
                Total = g.Count(),
                Online = g.Sum(d => d.ConnectivityStatus == ConnectivityStatus.Online ? 1 : 0),
                Offline = g.Sum(d => d.ConnectivityStatus == ConnectivityStatus.Offline ? 1 : 0)
            })
            .ToListAsync(ct);

        var locationNames = await _db.Locations.AsNoTracking()
            .Select(l => new { l.LocationId, l.Name })
            .ToDictionaryAsync(l => l.LocationId, l => l.Name, ct);

        var byLocation = locationRows
            .Select(r => new LocationBucket(
                locationNames.TryGetValue(r.LocationId, out var name) ? name : "Unknown",
                r.Total, r.Online, r.Offline))
            .OrderByDescending(b => b.Total)
            .ToList();

        var typeRows = await liveDevices
            .GroupBy(d => d.DeviceTypeId)
            .Select(g => new { DeviceTypeId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var typeNames = await _db.DeviceTypes.AsNoTracking()
            .Select(t => new { t.DeviceTypeId, t.Name })
            .ToDictionaryAsync(t => t.DeviceTypeId, t => t.Name, ct);

        var byType = typeRows
            .Select(r => new DeviceTypeBucket(
                typeNames.TryGetValue(r.DeviceTypeId, out var name) ? name : "Unknown",
                r.Count))
            .OrderByDescending(b => b.Count)
            .ToList();

        var trend = await BuildFleetTrendAsync(trendFrom, ct);

        var recentAlerts = await _db.Alerts.AsNoTracking()
            .OrderByDescending(a => a.CreatedAt)
            .Take(RecentAlertLimit)
            .Select(a => new AlertResponse(
                a.AlertId, a.DeviceId, a.Device!.DeviceName, a.Device.DeviceCode,
                a.AlertRuleId, a.AlertRule != null ? a.AlertRule.Name : null,
                a.Metric, a.Message, a.Severity, a.Status, a.TriggeredValue, a.Threshold,
                a.CreatedAt, a.AcknowledgedAt,
                a.AcknowledgedByUser != null ? a.AcknowledgedByUser.Name : null,
                a.ResolvedAt,
                a.ResolvedByUser != null ? a.ResolvedByUser.Name : null,
                a.ResolutionNote))
            .ToListAsync(ct);

        var attention = await BuildAttentionListAsync(ct);

        return new DashboardSummaryResponse(
            deviceCounts,
            alertCounts,
            bySeverity.OrderByDescending(b => b.Severity).ToList(),
            byLocation,
            byType,
            trend,
            recentAlerts,
            attention,
            trendHours,
            DateTime.UtcNow);
    }

    /// <summary>
    /// Fleet-wide hourly temperature trend. Grouped on the date parts rather than on a
    /// constructed DateTime because EF can translate that into SQL, whereas building a
    /// DateTime inside the GroupBy key forces the whole table into memory first.
    /// </summary>
    private async Task<List<TelemetryTrendPoint>> BuildFleetTrendAsync(DateTime from, CancellationToken ct)
    {
        var buckets = await _db.Telemetry.AsNoTracking()
            .Where(t => t.RecordedAt >= from)
            .GroupBy(t => new { t.RecordedAt.Year, t.RecordedAt.Month, t.RecordedAt.Day, t.RecordedAt.Hour })
            .Select(g => new
            {
                g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Hour,
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

    /// <summary>
    /// The "what do I look at first" list: devices that are offline or carrying unresolved
    /// alerts, worst first. This is the widget that makes the dashboard operationally useful
    /// rather than merely decorative.
    /// </summary>
    private async Task<List<DeviceHealthRow>> BuildAttentionListAsync(CancellationToken ct)
    {
        var rows = await _db.Devices.AsNoTracking()
            .Where(d => d.LifecycleStatus != LifecycleStatus.Retired)
            .Where(d => d.ConnectivityStatus == ConnectivityStatus.Offline
                     || d.Alerts.Any(a => a.Status != AlertStatus.Resolved))
            .Select(d => new
            {
                d.DeviceId,
                d.DeviceCode,
                d.DeviceName,
                LocationName = d.Location!.Name,
                d.ConnectivityStatus,
                d.LastSeenAt,
                OpenAlertCount = d.Alerts.Count(a => a.Status != AlertStatus.Resolved),

                // Highest unresolved severity drives the ordering. Computed as a nullable so a
                // device that is merely offline (no alerts) still appears, just lower down.
                HighestSeverity = d.Alerts
                    .Where(a => a.Status != AlertStatus.Resolved)
                    .Max(a => (AlertSeverity?)a.Severity),

                // Correlated subquery for the latest reading. Covered by the
                // (DeviceId, RecordedAt DESC) index, so each one is an index seek, and the list
                // is capped at ten rows.
                Latest = d.Telemetries
                    .OrderByDescending(t => t.RecordedAt)
                    .Select(t => new { t.Temperature, t.Battery })
                    .FirstOrDefault()
            })
            .OrderByDescending(d => d.HighestSeverity)
            .ThenByDescending(d => d.OpenAlertCount)
            .ThenBy(d => d.LastSeenAt)
            .Take(AttentionListLimit)
            .ToListAsync(ct);

        return rows
            .Select(d => new DeviceHealthRow(
                d.DeviceId, d.DeviceCode, d.DeviceName, d.LocationName,
                d.ConnectivityStatus, d.LastSeenAt,
                d.Latest?.Battery, d.Latest?.Temperature,
                d.OpenAlertCount, d.HighestSeverity))
            .ToList();
    }

    private sealed record ConnectivityCount(ConnectivityStatus Status, int Count);

    private sealed record LifecycleCount(LifecycleStatus Status, int Count);

    private static int Count(List<ConnectivityCount> counts, ConnectivityStatus status) =>
        counts.FirstOrDefault(c => c.Status == status)?.Count ?? 0;
}
