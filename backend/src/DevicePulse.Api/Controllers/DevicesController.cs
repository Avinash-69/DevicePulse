using DevicePulse.Api.Authorization;
using DevicePulse.Api.Models;
using DevicePulse.Api.Models.Common;
using DevicePulse.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DevicePulse.Api.Controllers;

/// <summary>
/// Device registration and administration (§13).
///
/// Every action carries its own permission requirement. This is the enforcement the master
/// reference keeps returning to (§4.4, §29): the Angular client hides buttons the user cannot
/// use, and these attributes are what actually stops a hand-rolled HTTP call.
/// </summary>
[ApiController]
[Route("api/v1/devices")]
[Authorize]
public sealed class DevicesController : ControllerBase
{
    private readonly IDeviceService _deviceService;
    private readonly ITelemetryService _telemetryService;

    public DevicesController(IDeviceService deviceService, ITelemetryService telemetryService)
    {
        _deviceService = deviceService;
        _telemetryService = telemetryService;
    }

    /// <summary>Paged, filterable, sortable device list. Retired devices are excluded unless asked for.</summary>
    [HttpGet]
    [HasPermission(Permissions.DeviceView)]
    [ProducesResponseType(typeof(PagedResult<DeviceResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<DeviceResponse>>> GetAll(
        [FromQuery] DeviceQuery query, CancellationToken ct)
        => Ok(await _deviceService.QueryAsync(query, ct));

    [HttpGet("{id:int}")]
    [HasPermission(Permissions.DeviceView)]
    [ProducesResponseType(typeof(DeviceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceResponse>> GetById(int id, CancellationToken ct)
        => Ok(await _deviceService.GetByIdAsync(id, ct));

    [HttpPost]
    [HasPermission(Permissions.DeviceCreate)]
    [ProducesResponseType(typeof(DeviceResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeviceResponse>> Create(CreateDeviceRequest request, CancellationToken ct)
    {
        var created = await _deviceService.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.DeviceId }, created);
    }

    [HttpPut("{id:int}")]
    [HasPermission(Permissions.DeviceUpdate)]
    [ProducesResponseType(typeof(DeviceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeviceResponse>> Update(int id, UpdateDeviceRequest request, CancellationToken ct)
        => Ok(await _deviceService.UpdateAsync(id, request, ct));

    /// <summary>
    /// Retires a device. Mapped to DELETE because that is what a REST client expects for
    /// "remove this from my fleet", but the implementation is a soft retire — the telemetry and
    /// alert history of a decommissioned device stay queryable (Appendix C item 2).
    /// </summary>
    [HttpDelete("{id:int}")]
    [HasPermission(Permissions.DeviceRetire)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Retire(int id, [FromBody] RetireDeviceRequest? request, CancellationToken ct)
    {
        await _deviceService.RetireAsync(id, request ?? new RetireDeviceRequest(null), ct);
        return NoContent();
    }

    /// <summary>
    /// Issues an ingestion key for the device. The plaintext key is in the response and is not
    /// stored anywhere — it cannot be retrieved again, only replaced.
    /// </summary>
    [HttpPost("{id:int}/api-key")]
    [HasPermission(Permissions.DeviceManageCredentials)]
    [ProducesResponseType(typeof(DeviceApiKeyResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DeviceApiKeyResponse>> IssueApiKey(int id, CancellationToken ct)
        => Ok(await _deviceService.IssueApiKeyAsync(id, ct));

    [HttpDelete("{id:int}/api-key")]
    [HasPermission(Permissions.DeviceManageCredentials)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RevokeApiKey(int id, CancellationToken ct)
    {
        await _deviceService.RevokeApiKeyAsync(id, ct);
        return NoContent();
    }

    // ---- Telemetry read paths, nested under the device they belong to ----

    [HttpGet("{id:int}/telemetry")]
    [HasPermission(Permissions.TelemetryView)]
    [ProducesResponseType(typeof(PagedResult<TelemetryResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<TelemetryResponse>>> GetTelemetry(
        int id, [FromQuery] TelemetryQuery query, CancellationToken ct)
        => Ok(await _telemetryService.GetHistoryAsync(id, query, ct));

    /// <summary>
    /// The device's most recent reading, or 204 when it has never reported. A newly-registered
    /// device having no telemetry is a normal state, not a 404.
    /// </summary>
    [HttpGet("{id:int}/telemetry/latest")]
    [HasPermission(Permissions.TelemetryView)]
    [ProducesResponseType(typeof(TelemetryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<TelemetryResponse>> GetLatestTelemetry(int id, CancellationToken ct)
    {
        var latest = await _telemetryService.GetLatestAsync(id, ct);
        return latest is null ? NoContent() : Ok(latest);
    }

    /// <summary>Hourly aggregated readings for the device detail chart.</summary>
    [HttpGet("{id:int}/telemetry/trend")]
    [HasPermission(Permissions.TelemetryView)]
    [ProducesResponseType(typeof(IReadOnlyList<TelemetryTrendPoint>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TelemetryTrendPoint>>> GetTelemetryTrend(
        int id, [FromQuery] int hours, CancellationToken ct)
        => Ok(await _telemetryService.GetTrendAsync(id, hours <= 0 ? 24 : hours, ct));
}
