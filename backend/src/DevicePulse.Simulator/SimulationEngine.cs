using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DevicePulse.Simulator;

/// <summary>
/// Per-device simulation state. Battery has to carry across rounds — a device whose battery was
/// random each reading would never trip the low-battery rule in a believable way, and the
/// gradual decline is what makes the alert meaningful.
/// </summary>
internal sealed class VirtualDevice
{
    public required int DeviceId { get; init; }
    public required string DeviceCode { get; init; }

    public double Battery { get; set; }
    public double Temperature { get; set; }

    /// <summary>Rounds this device is deliberately staying silent for, simulating a dropout.</summary>
    public int SilentRoundsRemaining { get; set; }

    public long ReadingsSent { get; set; }
}

/// <summary>
/// Generates and submits telemetry for a fleet of virtual devices (§19).
///
/// The values are a random walk rather than independent samples: a real sensor's next reading
/// is close to its last one, and independent draws would produce a sawtooth that neither looks
/// like hardware nor exercises the alert cooldown realistically.
/// </summary>
public sealed class SimulationEngine
{
    private readonly DevicePulseApiClient _api;
    private readonly SimulatorOptions _options;
    private readonly ILogger<SimulationEngine> _logger;
    private readonly Random _random;

    private readonly List<VirtualDevice> _devices = [];

    // Run totals, reported at shutdown. This is the measured evidence §36 and Development
    // Rule 7 ask for — throughput claims have to come from a number, not an impression.
    private long _totalSent;
    private long _totalAccepted;
    private long _totalDuplicates;
    private long _totalRejected;
    private long _totalAlerts;
    private long _totalSkipped;
    private readonly List<double> _roundLatenciesMs = [];

    public SimulationEngine(DevicePulseApiClient api, SimulatorOptions options, ILogger<SimulationEngine> logger)
    {
        _api = api;
        _options = options;
        _logger = logger;

        // Seeded, so two runs with the same configuration generate identical data and a
        // throughput difference can only come from the system under test.
        _random = new Random(options.RandomSeed);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await _api.SignInAsync(_options.Email, _options.Password, ct);
        await PrepareFleetAsync(ct);

        if (_devices.Count == 0)
        {
            _logger.LogError("No devices available to simulate. Enable AutoProvisionDevices or register some first.");
            return;
        }

        _logger.LogInformation(
            "Simulating {DeviceCount} device(s) every {Interval}s | burst={Burst} | seed={Seed}",
            _devices.Count, _options.IntervalSeconds, _options.BurstMode, _options.RandomSeed);

        var round = 0;
        var stopwatch = Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
        {
            round++;

            var roundTimer = Stopwatch.StartNew();
            await RunRoundAsync(round, ct);
            roundTimer.Stop();

            _roundLatenciesMs.Add(roundTimer.Elapsed.TotalMilliseconds);

            if (_options.Rounds > 0 && round >= _options.Rounds)
            {
                _logger.LogInformation("Completed the configured {Rounds} round(s).", _options.Rounds);
                break;
            }

            // The round's own duration is subtracted from the wait, so the reporting interval
            // stays honest instead of drifting by however long each round took.
            var remaining = TimeSpan.FromSeconds(_options.IntervalSeconds) - roundTimer.Elapsed;

            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(remaining, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            else
            {
                _logger.LogWarning(
                    "Round {Round} took {Elapsed:F0}ms, longer than the {Interval}s interval — the API is the bottleneck, not the schedule.",
                    round, roundTimer.Elapsed.TotalMilliseconds, _options.IntervalSeconds);
            }
        }

        stopwatch.Stop();
        ReportSummary(round, stopwatch.Elapsed);
    }

    /// <summary>
    /// Reuses existing devices and registers more only if the target count is not met. Running
    /// the simulator twice must not create a second fleet.
    /// </summary>
    private async Task PrepareFleetAsync(CancellationToken ct)
    {
        var existing = await _api.GetDevicesAsync(pageSize: 200, ct);

        _logger.LogInformation("Found {Count} active device(s) already registered.", existing.Count);

        var usable = existing.Take(_options.DeviceCount).ToList();

        if (usable.Count < _options.DeviceCount && _options.AutoProvisionDevices)
        {
            var types = await _api.GetDeviceTypesAsync(ct);
            var locations = await _api.GetLocationsAsync(ct);

            if (types.Count == 0 || locations.Count == 0)
            {
                _logger.LogError(
                    "Cannot auto-provision: the API has no active device types or locations. Create at least one of each.");
            }
            else
            {
                var needed = _options.DeviceCount - usable.Count;
                _logger.LogInformation("Registering {Needed} additional device(s).", needed);

                for (var i = 1; i <= needed; i++)
                {
                    var type = types[_random.Next(types.Count)];
                    var location = locations[_random.Next(locations.Count)];

                    // Suffixed with the round-trip index and a short random tag so repeated runs
                    // with different counts do not collide on an already-taken code.
                    var code = $"{_options.DeviceCodePrefix}-{existing.Count + i:D4}";

                    var created = await _api.RegisterDeviceAsync(
                        code, $"Simulated {type.Name} {existing.Count + i:D4}",
                        type.DeviceTypeId, location.LocationId, ct);

                    if (created is not null)
                        usable.Add(created);
                }
            }
        }

        foreach (var device in usable)
        {
            _devices.Add(new VirtualDevice
            {
                DeviceId = device.DeviceId,
                DeviceCode = device.DeviceCode,
                Battery = _options.BatteryStart,

                // Started mid-range so the first reading is not an outlier.
                Temperature = (_options.TemperatureMin + _options.TemperatureMax) / 2
            });
        }
    }

    private async Task RunRoundAsync(int round, CancellationToken ct)
    {
        var readings = new List<Reading>(_devices.Count);
        var skipped = 0;

        foreach (var device in _devices)
        {
            if (device.SilentRoundsRemaining > 0)
            {
                device.SilentRoundsRemaining--;
                skipped++;
                continue;
            }

            if (_random.NextDouble() < _options.FailureProbability)
            {
                // Silent for several rounds, not just one: a single missed reading would never
                // exceed the offline timeout, so the offline rule would never be exercised.
                device.SilentRoundsRemaining = _random.Next(3, 10);
                skipped++;
                continue;
            }

            readings.Add(NextReading(device, round));
        }

        _totalSkipped += skipped;

        if (readings.Count == 0)
        {
            _logger.LogInformation("Round {Round}: every device was silent this round.", round);
            return;
        }

        _totalSent += readings.Count;

        if (_options.BurstMode)
            await SendInBatchesAsync(readings, round, ct);
        else
            await SendIndividuallyAsync(readings, round, ct);
    }

    private Reading NextReading(VirtualDevice device, int round)
    {
        // Random walk: the next temperature stays near the last, bounded by the configured range.
        var drift = (_random.NextDouble() - 0.5) * 2.0;
        device.Temperature = Math.Clamp(
            device.Temperature + drift, _options.TemperatureMin, _options.TemperatureMax);

        var temperature = device.Temperature;

        if (_random.NextDouble() < _options.AnomalyProbability)
        {
            // A spike well above the configured ceiling, to trip the high and critical rules.
            // Deliberately not clamped to TemperatureMax — an anomaly that stayed inside the
            // normal band would not test anything.
            temperature = _options.TemperatureMax + _random.NextDouble() * 20;
        }

        // Monotonic drain, floored at 1 rather than 0: a device reporting exactly 0% has
        // usually stopped reporting altogether.
        device.Battery = Math.Max(1, device.Battery - _options.BatteryDrainPerReading);

        var signal = _random.Next(_options.SignalStrengthMin, _options.SignalStrengthMax + 1);

        device.ReadingsSent++;

        // Deterministic and unique per device per reading, so a retry of the same reading
        // carries the same id and the API can recognise it (Appendix D.1).
        var messageId = _options.SendMessageIds
            ? $"sim-{_options.RandomSeed}-{device.DeviceId}-{round}"
            : null;

        return new Reading(
            device.DeviceId,
            Math.Round(temperature, 2),
            Math.Round(device.Battery, 2),
            signal,
            DateTime.UtcNow,
            messageId);
    }

    private async Task SendInBatchesAsync(List<Reading> readings, int round, CancellationToken ct)
    {
        var accepted = 0;
        var duplicates = 0;
        var rejected = 0;
        var alerts = 0;

        foreach (var batch in readings.Chunk(_options.BurstSize))
        {
            var result = await _api.SendBulkAsync(batch, ct);

            if (result is null)
            {
                rejected += batch.Length;
                continue;
            }

            accepted += result.Accepted;
            duplicates += result.Duplicates;
            rejected += result.Rejected;
            alerts += result.AlertsRaised;

            foreach (var error in result.Errors.Take(3))
                _logger.LogWarning("Round {Round}: {Error}", round, error);
        }

        _totalAccepted += accepted;
        _totalDuplicates += duplicates;
        _totalRejected += rejected;
        _totalAlerts += alerts;

        _logger.LogInformation(
            "Round {Round}: sent {Sent} | accepted {Accepted} | duplicate {Duplicates} | rejected {Rejected} | alerts {Alerts}",
            round, readings.Count, accepted, duplicates, rejected, alerts);
    }

    private async Task SendIndividuallyAsync(List<Reading> readings, int round, CancellationToken ct)
    {
        var accepted = 0;

        foreach (var reading in readings)
        {
            if (await _api.SendSingleAsync(reading, ct))
                accepted++;
            else
                _totalRejected++;
        }

        _totalAccepted += accepted;

        _logger.LogInformation("Round {Round}: sent {Sent} individually | accepted {Accepted}",
            round, readings.Count, accepted);
    }

    /// <summary>
    /// Prints the measured run totals. This exists so performance claims can be backed by
    /// numbers from an actual run (Development Rule 7).
    /// </summary>
    private void ReportSummary(int rounds, TimeSpan elapsed)
    {
        var throughput = elapsed.TotalSeconds > 0 ? _totalAccepted / elapsed.TotalSeconds : 0;

        _roundLatenciesMs.Sort();

        var p50 = Percentile(50);
        var p95 = Percentile(95);

        _logger.LogInformation(
            """

            ── Simulation summary ──────────────────────────────
              devices simulated : {Devices}
              rounds completed  : {Rounds}
              elapsed           : {Elapsed:hh\:mm\:ss}
              readings sent     : {Sent}
              accepted          : {Accepted}
              duplicates        : {Duplicates}
              rejected          : {Rejected}
              alerts raised     : {Alerts}
              device-rounds skipped (simulated dropouts) : {Skipped}
              throughput        : {Throughput:F1} readings/sec
              round latency     : p50 {P50:F0}ms | p95 {P95:F0}ms
            ────────────────────────────────────────────────────
            """,
            _devices.Count, rounds, elapsed, _totalSent, _totalAccepted, _totalDuplicates,
            _totalRejected, _totalAlerts, _totalSkipped, throughput, p50, p95);
    }

    private double Percentile(int percentile)
    {
        if (_roundLatenciesMs.Count == 0)
            return 0;

        var index = (int)Math.Ceiling(percentile / 100.0 * _roundLatenciesMs.Count) - 1;
        return _roundLatenciesMs[Math.Clamp(index, 0, _roundLatenciesMs.Count - 1)];
    }
}
