using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DevicePulse.Simulator;

/// <summary>Minimal typed client for the endpoints the simulator uses.</summary>
public sealed class DevicePulseApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<DevicePulseApiClient> _logger;

    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _accessTokenExpiresAt = DateTime.MinValue;

    public DevicePulseApiClient(HttpClient http, ILogger<DevicePulseApiClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task SignInAsync(string email, string password, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync("/api/v1/auth/login",
            new { email, password }, Json, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            throw new InvalidOperationException(
                $"Sign-in failed ({(int)response.StatusCode}). {body}");
        }

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(Json, ct)
            ?? throw new InvalidOperationException("Sign-in returned an empty response.");

        StoreTokens(auth);

        if (!auth.User.Permissions.Contains("telemetry.ingest"))
        {
            // Caught here rather than letting every POST come back 403: a clear message at
            // startup beats thousands of identical failures.
            throw new InvalidOperationException(
                $"{auth.User.Email} does not hold telemetry.ingest. Grant it to one of their roles, " +
                "or sign in as a user who has it.");
        }

        _logger.LogInformation("Signed in as {Email} ({Roles}).",
            auth.User.Email, string.Join(", ", auth.User.Roles));
    }

    /// <summary>
    /// Refreshes the access token shortly before it expires. The simulator can run for hours,
    /// which is far longer than an access token lives, so this is what keeps a long load test
    /// from dying partway through with a wall of 401s.
    /// </summary>
    private async Task EnsureFreshTokenAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow < _accessTokenExpiresAt.AddMinutes(-1))
            return;

        if (_refreshToken is null)
            return;

        var response = await _http.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = _refreshToken }, Json, ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Token refresh failed ({Status}); the next request may be rejected.",
                (int)response.StatusCode);
            return;
        }

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(Json, ct);

        if (auth is not null)
        {
            StoreTokens(auth);
            _logger.LogDebug("Access token refreshed.");
        }
    }

    private void StoreTokens(AuthResponse auth)
    {
        _accessToken = auth.AccessToken;
        _refreshToken = auth.RefreshToken;
        _accessTokenExpiresAt = auth.AccessTokenExpiresAt;

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    public async Task<List<DeviceSummary>> GetDevicesAsync(int pageSize, CancellationToken ct)
    {
        await EnsureFreshTokenAsync(ct);

        var devices = new List<DeviceSummary>();
        var page = 1;

        // Paged rather than one huge request: the API caps pageSize, so asking for everything
        // at once would silently return only the first page.
        while (true)
        {
            var result = await _http.GetFromJsonAsync<PagedResult<DeviceSummary>>(
                $"/api/v1/devices?page={page}&pageSize={pageSize}&lifecycleStatus=Active", Json, ct);

            if (result is null || result.Items.Count == 0)
                break;

            devices.AddRange(result.Items);

            if (!result.HasNext)
                break;

            page++;
        }

        return devices;
    }

    public async Task<List<DeviceTypeItem>> GetDeviceTypesAsync(CancellationToken ct)
    {
        await EnsureFreshTokenAsync(ct);

        return await _http.GetFromJsonAsync<List<DeviceTypeItem>>(
            "/api/v1/reference/device-types", Json, ct) ?? [];
    }

    public async Task<List<LocationItem>> GetLocationsAsync(CancellationToken ct)
    {
        await EnsureFreshTokenAsync(ct);

        return await _http.GetFromJsonAsync<List<LocationItem>>(
            "/api/v1/reference/locations", Json, ct) ?? [];
    }

    /// <summary>
    /// Registers a device, tolerating the case where it already exists. A 409 here is an
    /// expected outcome on a re-run, not a failure — the simulator is meant to be restartable.
    /// </summary>
    public async Task<DeviceSummary?> RegisterDeviceAsync(
        string deviceCode, string deviceName, int deviceTypeId, int locationId, CancellationToken ct)
    {
        await EnsureFreshTokenAsync(ct);

        var response = await _http.PostAsJsonAsync("/api/v1/devices",
            new { deviceCode, deviceName, deviceTypeId, locationId }, Json, ct);

        if (response.StatusCode == HttpStatusCode.Conflict)
            return null;

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Could not register {DeviceCode}: {Status} {Body}",
                deviceCode, (int)response.StatusCode, body);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<DeviceSummary>(Json, ct);
    }

    public async Task<BulkResult?> SendBulkAsync(IReadOnlyList<Reading> readings, CancellationToken ct)
    {
        await EnsureFreshTokenAsync(ct);

        var response = await _http.PostAsJsonAsync("/api/v1/telemetry/bulk",
            new { readings }, Json, ct);

        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<BulkResult>(Json, ct);

        await LogFailureAsync(response, $"bulk of {readings.Count} reading(s)", ct);
        return null;
    }

    public async Task<bool> SendSingleAsync(Reading reading, CancellationToken ct)
    {
        await EnsureFreshTokenAsync(ct);

        var response = await _http.PostAsJsonAsync("/api/v1/telemetry/ingest", reading, Json, ct);

        if (response.IsSuccessStatusCode)
            return true;

        await LogFailureAsync(response, $"reading for device {reading.DeviceId}", ct);
        return false;
    }

    private async Task LogFailureAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);

        // 429 is logged at Warning, not Error: being rate-limited means the simulator is
        // pushing harder than the configured ceiling, which during a load test is information
        // rather than a fault.
        var level = response.StatusCode == HttpStatusCode.TooManyRequests
            ? LogLevel.Warning
            : LogLevel.Error;

        _logger.Log(level, "Rejected {What}: {Status} {Body}",
            what, (int)response.StatusCode, Truncate(body, 300));
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}

// Response shapes, kept deliberately minimal — only the fields the simulator reads.
public sealed record AuthResponse(string AccessToken, string RefreshToken, DateTime AccessTokenExpiresAt, AuthUser User);
public sealed record AuthUser(int UserId, string Name, string Email, List<string> Roles, List<string> Permissions);
public sealed record PagedResult<T>(List<T> Items, int Page, int PageSize, int TotalCount, int TotalPages, bool HasNext);
public sealed record DeviceSummary(int DeviceId, string DeviceCode, string DeviceName, int DeviceTypeId, int LocationId);
public sealed record DeviceTypeItem(int DeviceTypeId, string Name, bool IsActive);
public sealed record LocationItem(int LocationId, string Name, bool IsActive);
public sealed record Reading(int DeviceId, double Temperature, double Battery, int SignalStrength, DateTime RecordedAt, string? MessageId);
public sealed record BulkResult(int Accepted, int Duplicates, int Rejected, int AlertsRaised, List<string> Errors);
