using DevicePulse.Api.Data;
using DevicePulse.Api.Entities.Enums;
using Microsoft.EntityFrameworkCore;

namespace DevicePulse.Api.Services.Configuration;

/// <summary>
/// Caching, typed reader over the SystemSettings table.
///
/// Registered as a singleton with its own short-lived DbContext scope: settings are read on
/// nearly every request (and on every telemetry ingest), so a database round trip per read
/// would be the first thing to show up under load. The cache TTL is itself a setting, and a
/// write invalidates immediately, so the TTL only bounds staleness *between* instances —
/// relevant once the API is scaled out, harmless when it isn't.
///
/// Note this is in-process, not Redis. Per §34 of the master reference, a distributed cache
/// gets introduced when there is a measured need, not pre-emptively.
/// </summary>
public sealed class RuntimeSettings : IRuntimeSettings
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RuntimeSettings> _logger;
    private readonly Lock _gate = new();

    private Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _loadedAtUtc = DateTime.MinValue;

    /// <summary>Compiled-in fallbacks, so a read works even before the database is reachable.</summary>
    private static readonly Dictionary<string, string> Defaults =
        SettingKeys.All.ToDictionary(d => d.Key, d => d.DefaultValue, StringComparer.OrdinalIgnoreCase);

    public RuntimeSettings(IServiceScopeFactory scopeFactory, ILogger<RuntimeSettings> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public int GetInt(string key) =>
        int.TryParse(Raw(key), out var value) ? value : ParseDefault(key, int.Parse);

    public double GetDouble(string key) =>
        double.TryParse(Raw(key), System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : ParseDefault(key, s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture));

    public bool GetBool(string key) =>
        bool.TryParse(Raw(key), out var value) ? value : ParseDefault(key, bool.Parse);

    public string GetString(string key) => Raw(key);

    public void Invalidate()
    {
        lock (_gate)
        {
            _loadedAtUtc = DateTime.MinValue;
        }
    }

    private string Raw(string key)
    {
        EnsureLoaded();

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var value))
                return value;
        }

        // An unknown key is a programming error (the catalog is compiled in), so it is worth a
        // warning — but it must not take a request down.
        if (Defaults.TryGetValue(key, out var fallback))
            return fallback;

        _logger.LogWarning("Setting '{Key}' is not in the catalog and has no default.", key);
        return string.Empty;
    }

    private T ParseDefault<T>(string key, Func<string, T> parse)
    {
        _logger.LogWarning(
            "Setting '{Key}' holds a value that could not be parsed as {Type}; falling back to the compiled-in default.",
            key, typeof(T).Name);

        return parse(Defaults[key]);
    }

    private void EnsureLoaded()
    {
        int ttlSeconds;

        lock (_gate)
        {
            // Read the TTL from whatever is already cached; on a cold start that is the default.
            ttlSeconds = _cache.TryGetValue(SettingKeys.SettingsCacheSeconds, out var raw)
                         && int.TryParse(raw, out var parsed)
                ? parsed
                : int.Parse(Defaults[SettingKeys.SettingsCacheSeconds]);

            if (DateTime.UtcNow - _loadedAtUtc < TimeSpan.FromSeconds(ttlSeconds))
                return;
        }

        Dictionary<string, string> loaded;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DevicePulseDbContext>();

            loaded = db.SystemSettings
                .AsNoTracking()
                .Select(s => new { s.Key, s.Value })
                .ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // Serving slightly stale (or default) settings beats failing the request. The one
            // place this must not silently degrade is a *write*, which goes through
            // SettingsService against the database directly, not through this cache.
            _logger.LogError(ex, "Could not refresh runtime settings; continuing with the current cache.");

            lock (_gate)
            {
                _loadedAtUtc = DateTime.UtcNow;
            }
            return;
        }

        lock (_gate)
        {
            _cache = loaded;
            _loadedAtUtc = DateTime.UtcNow;
        }
    }
}

/// <summary>Strongly-typed accessors for the settings this application actually reads.</summary>
public static class RuntimeSettingsExtensions
{
    public static TimeSpan OfflineTimeout(this IRuntimeSettings settings) =>
        TimeSpan.FromSeconds(settings.GetInt(SettingKeys.OfflineTimeoutSeconds));

    public static TimeSpan OfflineSweepInterval(this IRuntimeSettings settings) =>
        TimeSpan.FromSeconds(settings.GetInt(SettingKeys.OfflineSweepIntervalSeconds));

    public static bool AlertEvaluationEnabled(this IRuntimeSettings settings) =>
        settings.GetBool(SettingKeys.AlertEvaluationEnabled);

    public static bool AutoResolveOfflineAlerts(this IRuntimeSettings settings) =>
        settings.GetBool(SettingKeys.AlertAutoResolveOfflineOnReconnect);

    public static (double Min, double Max) TemperatureBounds(this IRuntimeSettings settings) =>
        (settings.GetDouble(SettingKeys.TelemetryMinTemperature),
         settings.GetDouble(SettingKeys.TelemetryMaxTemperature));

    public static int TrendHours(this IRuntimeSettings settings) =>
        settings.GetInt(SettingKeys.DashboardTrendHours);

    public static int TelemetryRetentionDays(this IRuntimeSettings settings) =>
        settings.GetInt(SettingKeys.TelemetryRetentionDays);
}
