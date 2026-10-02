using System.Security.Claims;
using DevicePulse.Api.Authorization;
using DevicePulse.Api.Exceptions;
using DevicePulse.Api.Models;
using DevicePulse.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DevicePulse.Api.Controllers;

/// <summary>
/// Telemetry ingestion (§14).
///
/// Two deliberately distinct entry points, reflecting Appendix C item 3:
///   * <c>POST /api/v1/telemetry</c> — a real device, authenticating with its own API key. The
///     device id comes from the credential, so a device can only ever report as itself.
///   * <c>POST /api/v1/telemetry/ingest</c> — a human or the simulator holding telemetry.ingest,
///     which must name the device it is reporting for.
///
/// Both accept the JWT and the device-key scheme, so the simulator can use either.
/// </summary>
[ApiController]
[Route("api/v1/telemetry")]
[Authorize(AuthenticationSchemes = $"{JwtBearerDefaults.AuthenticationScheme},{DeviceApiKeyDefaults.Scheme}")]
[EnableRateLimiting(RateLimitPolicies.Ingestion)]
public sealed class TelemetryController : ControllerBase
{
    private readonly ITelemetryService _telemetryService;

    public TelemetryController(ITelemetryService telemetryService) => _telemetryService = telemetryService;

    /// <summary>
    /// A device posting its own reading. The body carries no device id on purpose: taking it
    /// from the request would let any device with a valid key submit readings attributed to
    /// another one.
    /// </summary>
    [HttpPost]
    [HasPermission(Permissions.TelemetryIngest)]
    [ProducesResponseType(typeof(TelemetryIngestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TelemetryIngestResponse>> Ingest(TelemetryRequest request, CancellationToken ct)
    {
        var deviceId = ResolveDeviceIdFromCredential();
        return Ok(await _telemetryService.IngestAsync(deviceId, request, ct));
    }

    /// <summary>
    /// Posts a reading on behalf of a named device. Used by the simulator and by operators
    /// testing the pipeline.
    /// </summary>
    [HttpPost("ingest")]
    [HasPermission(Permissions.TelemetryIngest)]
    [ProducesResponseType(typeof(TelemetryIngestResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<TelemetryIngestResponse>> IngestFor(
        TelemetryIngestRequest request, CancellationToken ct)
    {
        EnsureDeviceCredentialMatches(request.DeviceId);

        var reading = new TelemetryRequest(
            request.Temperature, request.Battery, request.SignalStrength, request.RecordedAt, request.MessageId);

        return Ok(await _telemetryService.IngestAsync(request.DeviceId, reading, ct));
    }

    /// <summary>
    /// Batched ingestion. The simulator uses this at higher device counts: one request carrying
    /// 500 readings costs far less than 500 requests, which is the difference between a load
    /// test that measures ingestion and one that measures HTTP overhead (§36).
    /// </summary>
    [HttpPost("bulk")]
    [HasPermission(Permissions.TelemetryIngest)]
    [ProducesResponseType(typeof(BulkTelemetryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<BulkTelemetryResponse>> IngestBulk(
        BulkTelemetryRequest request, CancellationToken ct)
    {
        // A device key is scoped to one device, so it cannot submit a batch spanning several.
        if (IsDeviceCredential())
        {
            var deviceId = ResolveDeviceIdFromCredential();

            if (request.Readings.Any(r => r.DeviceId != deviceId))
                throw new ForbiddenException("A device key may only submit readings for its own device.");
        }

        return Ok(await _telemetryService.IngestBulkAsync(request, ct));
    }

    private bool IsDeviceCredential() =>
        User.Identity?.AuthenticationType == DeviceApiKeyDefaults.Scheme;

    private int ResolveDeviceIdFromCredential()
    {
        var claim = User.FindFirst(DeviceApiKeyDefaults.DeviceIdClaim)?.Value;

        if (int.TryParse(claim, out var deviceId))
            return deviceId;

        // Reached when a user token hits the device-only endpoint. The user is authenticated
        // and permitted to ingest, but there is no device in their credential to attribute the
        // reading to, so they are pointed at the endpoint that takes one explicitly.
        throw new ValidationException(
            "This endpoint requires a device API key. Use POST /api/v1/telemetry/ingest and name the device instead.");
    }

    /// <summary>
    /// Stops a device key from being used to report for a different device, while leaving a
    /// user token free to name any device it has permission to ingest for.
    /// </summary>
    private void EnsureDeviceCredentialMatches(int requestedDeviceId)
    {
        if (!IsDeviceCredential())
            return;

        var deviceId = ResolveDeviceIdFromCredential();

        if (deviceId != requestedDeviceId)
            throw new ForbiddenException("A device key may only submit readings for its own device.");
    }
}
