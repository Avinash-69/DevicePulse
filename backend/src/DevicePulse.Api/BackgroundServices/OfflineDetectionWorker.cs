using DevicePulse.Api.Data;
using DevicePulse.Api.Entities.Enums;
using DevicePulse.Api.Services.Alerting;
using DevicePulse.Api.Services.Configuration;
using Microsoft.EntityFrameworkCore;

namespace DevicePulse.Api.BackgroundServices;

/// <summary>
/// Marks devices Offline once they stop reporting, and raises the offline alert rules (§15, §32).
///
/// This has to be a background sweeper rather than something computed on read, because going
/// offline is the absence of an event: no request arrives to trigger the transition. A device
/// that dies silently would otherwise stay "Online" forever on the dashboard.
///
/// Both the timeout and the sweep interval are runtime settings, so an operator can tune the
/// sensitivity without a redeploy — and the interval is re-read every cycle, so a change takes
/// effect on the next pass rather than at the next restart.
/// </summary>
public sealed class OfflineDetectionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OfflineDetectionWorker> _logger;

    public OfflineDetectionWorker(IServiceScopeFactory scopeFactory, ILogger<OfflineDetectionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Offline detection worker started.");

        // A short delay before the first pass: at startup the API is still warming up and the
        // database may not have finished migrating.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(60);

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<IRuntimeSettings>();

                interval = settings.OfflineSweepInterval();
                await SweepAsync(scope.ServiceProvider, settings, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed sweep must not kill the worker: an unhandled exception here would
                // silently stop offline detection for the rest of the process lifetime, and
                // nothing would look broken until someone noticed a dead device still "Online".
                _logger.LogError(ex, "Offline detection sweep failed; retrying on the next interval.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Offline detection worker stopped.");
    }

    private async Task SweepAsync(IServiceProvider services, IRuntimeSettings settings, CancellationToken ct)
    {
        var db = services.GetRequiredService<DevicePulseDbContext>();
        var alertEngine = services.GetRequiredService<IAlertEngine>();

        var timeout = settings.OfflineTimeout();
        var cutoff = DateTime.UtcNow - timeout;

        // Only devices that were Online and have now gone quiet. A device that is already
        // marked Offline is skipped, which is also what stops the offline alert from being
        // re-raised every sweep — the cooldown in AlertEngine is a second line of defence.
        var goneQuiet = await db.Devices
            .Where(d => d.LifecycleStatus != LifecycleStatus.Retired)
            .Where(d => d.ConnectivityStatus == ConnectivityStatus.Online)
            .Where(d => d.LastSeenAt == null || d.LastSeenAt < cutoff)
            .ToListAsync(ct);

        if (goneQuiet.Count == 0)
            return;

        var alertsRaised = 0;

        foreach (var device in goneQuiet)
        {
            device.ConnectivityStatus = ConnectivityStatus.Offline;
            device.UpdatedAt = DateTime.UtcNow;

            var ageSeconds = device.LastSeenAt is null
                ? timeout.TotalSeconds
                : (DateTime.UtcNow - device.LastSeenAt.Value).TotalSeconds;

            alertsRaised += await alertEngine.EvaluateOfflineAsync(device, ageSeconds, ct);
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Offline sweep: {DeviceCount} device(s) marked offline after {Timeout}s of silence, {AlertCount} alert(s) raised.",
            goneQuiet.Count, timeout.TotalSeconds, alertsRaised);
    }
}
