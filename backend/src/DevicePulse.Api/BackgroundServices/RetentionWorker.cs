using DevicePulse.Api.Data;
using DevicePulse.Api.Services.Configuration;
using Microsoft.EntityFrameworkCore;

namespace DevicePulse.Api.BackgroundServices;

/// <summary>
/// Enforces the configured data-retention windows (§45).
///
/// Telemetry is the table that grows without bound — a hundred devices reporting every two
/// seconds is roughly four million rows a day — so something has to delete it. Both windows
/// are runtime settings, and the sweep runs daily because retention is measured in days;
/// running it more often would be pure load for no benefit.
/// </summary>
public sealed class RetentionWorker : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RetentionWorker> _logger;

    public RetentionWorker(IServiceScopeFactory scopeFactory, ILogger<RetentionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Retention worker started.");

        // Deliberately not run immediately at startup: a bulk delete competing with the
        // application's own warm-up is the wrong first thing to do on a deploy.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await PurgeAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retention sweep failed; retrying at the next interval.");
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Retention worker stopped.");
    }

    private async Task PurgeAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<DevicePulseDbContext>();
        var settings = services.GetRequiredService<IRuntimeSettings>();

        var telemetryCutoff = DateTime.UtcNow.AddDays(-settings.TelemetryRetentionDays());
        var auditCutoff = DateTime.UtcNow.AddDays(-settings.GetInt(SettingKeys.AuditRetentionDays));

        // ExecuteDeleteAsync emits a single DELETE instead of loading every expired row into
        // the change tracker — the difference between a bounded statement and exhausting memory
        // on a large table.
        var telemetryDeleted = await db.Telemetry
            .Where(t => t.RecordedAt < telemetryCutoff)
            .ExecuteDeleteAsync(ct);

        var auditDeleted = await db.AuditLogs
            .Where(a => a.Timestamp < auditCutoff)
            .ExecuteDeleteAsync(ct);

        // ServiceLogs follow the audit window. They are diagnostic data, not business records,
        // and an unbounded error table is its own production problem.
        var serviceLogsDeleted = await db.ServiceLogs
            .Where(l => l.Timestamp < auditCutoff)
            .ExecuteDeleteAsync(ct);

        // Expired refresh tokens serve no purpose once they are past their expiry — they cannot
        // be used, and keeping them only grows the table the refresh lookup scans.
        var tokensDeleted = await db.RefreshTokens
            .Where(t => t.ExpiresAt < DateTime.UtcNow.AddDays(-30))
            .ExecuteDeleteAsync(ct);

        if (telemetryDeleted + auditDeleted + serviceLogsDeleted + tokensDeleted > 0)
        {
            _logger.LogInformation(
                "Retention sweep removed {Telemetry} reading(s), {Audit} audit row(s), {ServiceLogs} service log(s) and {Tokens} expired token(s).",
                telemetryDeleted, auditDeleted, serviceLogsDeleted, tokensDeleted);
        }
    }
}
