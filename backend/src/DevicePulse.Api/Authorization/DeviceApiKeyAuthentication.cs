using System.Security.Claims;
using System.Text.Encodings.Web;
using DevicePulse.Api.Data;
using DevicePulse.Api.Entities.Enums;
using DevicePulse.Api.Services.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevicePulse.Api.Authorization;

public static class DeviceApiKeyDefaults
{
    public const string Scheme = "DeviceApiKey";
    public const string HeaderName = "X-Device-Key";

    /// <summary>Claim holding the authenticated device's id, read by the ingestion endpoint.</summary>
    public const string DeviceIdClaim = "device_id";
    public const string DeviceCodeClaim = "device_code";
}

public sealed class DeviceApiKeyOptions : AuthenticationSchemeOptions;

/// <summary>
/// Authenticates a device by the API key it presents in <c>X-Device-Key</c>.
///
/// A separate authentication scheme rather than a shared one (Appendix C item 3): a device is
/// not a user, it has no roles and no session, and letting the two share a token type would
/// mean a stolen device key could be used wherever a user token is accepted.
///
/// A successfully authenticated device gets exactly one permission — telemetry.ingest — so it
/// can post its own readings and nothing else.
/// </summary>
public sealed class DeviceApiKeyAuthenticationHandler : AuthenticationHandler<DeviceApiKeyOptions>
{
    private readonly DevicePulseDbContext _db;
    private readonly IDeviceApiKeyService _apiKeys;

    public DeviceApiKeyAuthenticationHandler(
        IOptionsMonitor<DeviceApiKeyOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        DevicePulseDbContext db,
        IDeviceApiKeyService apiKeys)
        : base(options, logger, encoder)
    {
        _db = db;
        _apiKeys = apiKeys;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(DeviceApiKeyDefaults.HeaderName, out var header))
        {
            // NoResult, not Fail: the request simply did not attempt this scheme, and failing
            // here would turn every ordinary user request into a logged authentication error.
            return AuthenticateResult.NoResult();
        }

        var presentedKey = header.ToString();

        if (string.IsNullOrWhiteSpace(presentedKey))
            return AuthenticateResult.Fail("The device key header was empty.");

        // Looked up by hash, so the plaintext key never has to exist in the database and a
        // database leak yields nothing usable.
        var hash = _apiKeys.ComputeHash(presentedKey);

        var device = await _db.Devices.AsNoTracking()
            .Where(d => d.ApiKeyHash == hash)
            .Select(d => new { d.DeviceId, d.DeviceCode, d.LifecycleStatus })
            .FirstOrDefaultAsync();

        if (device is null)
        {
            Logger.LogWarning("An unrecognised device key was presented from {RemoteIp}.",
                Context.Connection.RemoteIpAddress);

            // The same message whether the key is unknown or malformed — distinguishing them
            // would help an attacker work out which part they got wrong.
            return AuthenticateResult.Fail("The device key is not valid.");
        }

        if (device.LifecycleStatus == LifecycleStatus.Retired)
        {
            Logger.LogWarning("Device {DeviceCode} presented a key but is retired.", device.DeviceCode);
            return AuthenticateResult.Fail("This device is retired.");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, device.DeviceCode),
            new Claim(ClaimTypes.Name, device.DeviceCode),
            new Claim(DeviceApiKeyDefaults.DeviceIdClaim, device.DeviceId.ToString()),
            new Claim(DeviceApiKeyDefaults.DeviceCodeClaim, device.DeviceCode),

            // The only thing a device is ever allowed to do.
            new Claim(AppClaimTypes.Permission, Permissions.TelemetryIngest)
        };

        var identity = new ClaimsIdentity(claims, DeviceApiKeyDefaults.Scheme);
        var principal = new ClaimsPrincipal(identity);

        return AuthenticateResult.Success(new AuthenticationTicket(principal, DeviceApiKeyDefaults.Scheme));
    }
}
