using DevicePulse.Api.Authorization;
using DevicePulse.Api.Models;
using DevicePulse.Api.Models.Common;
using DevicePulse.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DevicePulse.Api.Controllers;

/// <summary>User administration (§12).</summary>
[ApiController]
[Route("api/v1/admin/users")]
[Authorize]
public sealed class UsersController : ControllerBase
{
    private readonly IUserAdminService _users;

    public UsersController(IUserAdminService users) => _users = users;

    [HttpGet]
    [HasPermission(Permissions.UserView)]
    [ProducesResponseType(typeof(PagedResult<UserResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<UserResponse>>> GetAll([FromQuery] UserQuery query, CancellationToken ct)
        => Ok(await _users.QueryAsync(query, ct));

    [HttpGet("{id:int}")]
    [HasPermission(Permissions.UserView)]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<UserResponse>> GetById(int id, CancellationToken ct)
        => Ok(await _users.GetByIdAsync(id, ct));

    /// <summary>
    /// Creates a user with an administrator-chosen password and role set. This is the
    /// server-side flow from §12: the privileged operation happens here, never in the browser.
    /// </summary>
    [HttpPost]
    [HasPermission(Permissions.UserCreate)]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserResponse>> Create(CreateUserRequest request, CancellationToken ct)
    {
        var created = await _users.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.UserId }, created);
    }

    [HttpPut("{id:int}")]
    [HasPermission(Permissions.UserUpdate)]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<UserResponse>> Update(int id, UpdateUserRequest request, CancellationToken ct)
        => Ok(await _users.UpdateAsync(id, request, ct));

    /// <summary>
    /// Activates or deactivates an account. Deactivation also revokes the user's live sessions,
    /// so it takes effect immediately rather than when their tokens happen to expire.
    /// </summary>
    [HttpPatch("{id:int}/status")]
    [HasPermission(Permissions.UserDisable)]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<UserResponse>> SetStatus(int id, SetUserStatusRequest request, CancellationToken ct)
        => Ok(await _users.SetStatusAsync(id, request.IsActive, ct));

    [HttpPost("{id:int}/reset-password")]
    [HasPermission(Permissions.UserUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ResetPassword(int id, ResetUserPasswordRequest request, CancellationToken ct)
    {
        await _users.ResetPasswordAsync(id, request, ct);
        return NoContent();
    }
}

/// <summary>Role and permission administration (§12, Appendix A.3).</summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize]
public sealed class RolesController : ControllerBase
{
    private readonly IRoleService _roles;

    public RolesController(IRoleService roles) => _roles = roles;

    [HttpGet("roles")]
    [HasPermission(Permissions.RoleView)]
    [ProducesResponseType(typeof(IReadOnlyList<RoleResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RoleResponse>>> GetRoles(CancellationToken ct)
        => Ok(await _roles.GetAllAsync(ct));

    [HttpGet("roles/{id:int}")]
    [HasPermission(Permissions.RoleView)]
    [ProducesResponseType(typeof(RoleResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RoleResponse>> GetRole(int id, CancellationToken ct)
        => Ok(await _roles.GetByIdAsync(id, ct));

    [HttpPost("roles")]
    [HasPermission(Permissions.RoleManage)]
    [ProducesResponseType(typeof(RoleResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<RoleResponse>> CreateRole(CreateRoleRequest request, CancellationToken ct)
    {
        var created = await _roles.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetRole), new { id = created.RoleId }, created);
    }

    [HttpPut("roles/{id:int}")]
    [HasPermission(Permissions.RoleManage)]
    [ProducesResponseType(typeof(RoleResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RoleResponse>> UpdateRole(int id, UpdateRoleRequest request, CancellationToken ct)
        => Ok(await _roles.UpdateAsync(id, request, ct));

    /// <summary>
    /// Replaces the role's permission set. Takes the complete intended set rather than a
    /// delta, which makes the call idempotent and avoids a whole class of partial-update bugs.
    /// </summary>
    [HttpPut("roles/{id:int}/permissions")]
    [HasPermission(Permissions.PermissionManage)]
    [ProducesResponseType(typeof(RoleResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RoleResponse>> SetRolePermissions(
        int id, SetRolePermissionsRequest request, CancellationToken ct)
        => Ok(await _roles.SetPermissionsAsync(id, request, ct));

    /// <summary>
    /// The permission catalog. Read-only by design: permission keys are defined in code as
    /// features are built, and only their assignment is configurable (Appendix C item 4).
    /// </summary>
    [HttpGet("permissions")]
    [HasPermission(Permissions.PermissionView)]
    [ProducesResponseType(typeof(IReadOnlyList<PermissionResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PermissionResponse>>> GetPermissions(CancellationToken ct)
        => Ok(await _roles.GetPermissionCatalogAsync(ct));
}

/// <summary>Runtime business settings and their change history (§10.1, §18).</summary>
[ApiController]
[Route("api/v1/settings")]
[Authorize]
public sealed class SettingsController : ControllerBase
{
    private readonly ISettingsService _settings;

    public SettingsController(ISettingsService settings) => _settings = settings;

    [HttpGet]
    [HasPermission(Permissions.SettingsView)]
    [ProducesResponseType(typeof(IReadOnlyList<SettingResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SettingResponse>>> GetAll(
        [FromQuery] string? category, CancellationToken ct)
        => Ok(await _settings.GetAllAsync(category, ct));

    [HttpGet("categories")]
    [HasPermission(Permissions.SettingsView)]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<string>>> GetCategories(CancellationToken ct)
        => Ok(await _settings.GetCategoriesAsync(ct));

    [HttpGet("{key}")]
    [HasPermission(Permissions.SettingsView)]
    [ProducesResponseType(typeof(SettingResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SettingResponse>> GetByKey(string key, CancellationToken ct)
        => Ok(await _settings.GetByKeyAsync(key, ct));

    /// <summary>
    /// Changes a setting. Validated against the setting's declared type and bounds, versioned
    /// into history, audited, and applied without a restart — the whole point of §4.1.
    /// </summary>
    [HttpPut("{key}")]
    [HasPermission(Permissions.SettingsManage)]
    [ProducesResponseType(typeof(SettingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SettingResponse>> Update(
        string key, UpdateSettingRequest request, CancellationToken ct)
        => Ok(await _settings.UpdateAsync(key, request, ct));

    /// <summary>The value timeline for one setting: who changed it, when, from what, and why (§18).</summary>
    [HttpGet("{key}/history")]
    [HasPermission(Permissions.SettingsManage)]
    [ProducesResponseType(typeof(IReadOnlyList<SettingHistoryResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SettingHistoryResponse>>> GetHistory(string key, CancellationToken ct)
        => Ok(await _settings.GetHistoryAsync(key, ct));
}

/// <summary>Device types and locations (§4.2).</summary>
[ApiController]
[Route("api/v1/reference")]
[Authorize]
public sealed class ReferenceDataController : ControllerBase
{
    private readonly IReferenceDataService _reference;

    public ReferenceDataController(IReferenceDataService reference) => _reference = reference;

    [HttpGet("device-types")]
    [HasPermission(Permissions.ReferenceDataView)]
    [ProducesResponseType(typeof(IReadOnlyList<DeviceTypeResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DeviceTypeResponse>>> GetDeviceTypes(
        [FromQuery] bool includeInactive, CancellationToken ct)
        => Ok(await _reference.GetDeviceTypesAsync(includeInactive, ct));

    [HttpPost("device-types")]
    [HasPermission(Permissions.ReferenceDataManage)]
    [ProducesResponseType(typeof(DeviceTypeResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<DeviceTypeResponse>> CreateDeviceType(
        CreateDeviceTypeRequest request, CancellationToken ct)
    {
        var created = await _reference.CreateDeviceTypeAsync(request, ct);
        return Created($"/api/v1/reference/device-types/{created.DeviceTypeId}", created);
    }

    [HttpPut("device-types/{id:int}")]
    [HasPermission(Permissions.ReferenceDataManage)]
    [ProducesResponseType(typeof(DeviceTypeResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DeviceTypeResponse>> UpdateDeviceType(
        int id, UpdateDeviceTypeRequest request, CancellationToken ct)
        => Ok(await _reference.UpdateDeviceTypeAsync(id, request, ct));

    [HttpGet("locations")]
    [HasPermission(Permissions.ReferenceDataView)]
    [ProducesResponseType(typeof(IReadOnlyList<LocationResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<LocationResponse>>> GetLocations(
        [FromQuery] bool includeInactive, CancellationToken ct)
        => Ok(await _reference.GetLocationsAsync(includeInactive, ct));

    [HttpPost("locations")]
    [HasPermission(Permissions.ReferenceDataManage)]
    [ProducesResponseType(typeof(LocationResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<LocationResponse>> CreateLocation(
        CreateLocationRequest request, CancellationToken ct)
    {
        var created = await _reference.CreateLocationAsync(request, ct);
        return Created($"/api/v1/reference/locations/{created.LocationId}", created);
    }

    [HttpPut("locations/{id:int}")]
    [HasPermission(Permissions.ReferenceDataManage)]
    [ProducesResponseType(typeof(LocationResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LocationResponse>> UpdateLocation(
        int id, UpdateLocationRequest request, CancellationToken ct)
        => Ok(await _reference.UpdateLocationAsync(id, request, ct));
}

/// <summary>The administrative audit trail (§17).</summary>
[ApiController]
[Route("api/v1/audit")]
[Authorize]
public sealed class AuditController : ControllerBase
{
    private readonly IAuditService _audit;

    public AuditController(IAuditService audit) => _audit = audit;

    /// <summary>
    /// Read-only, and intentionally so: an audit trail an administrator can edit is not an
    /// audit trail. There is no write, update or delete endpoint here at all — entries are
    /// only ever created as a side effect of the action they describe, and removed by the
    /// retention worker.
    /// </summary>
    [HttpGet]
    [HasPermission(Permissions.AuditView)]
    [ProducesResponseType(typeof(PagedResult<AuditLogResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<AuditLogResponse>>> Query(
        [FromQuery] AuditLogQuery query, CancellationToken ct)
        => Ok(await _audit.QueryAsync(query, ct));
}

/// <summary>The monitoring dashboard (§30).</summary>
[ApiController]
[Route("api/v1/dashboard")]
[Authorize]
public sealed class DashboardController : ControllerBase
{
    private readonly IDashboardService _dashboard;

    public DashboardController(IDashboardService dashboard) => _dashboard = dashboard;

    [HttpGet("summary")]
    [HasPermission(Permissions.DashboardView)]
    [ProducesResponseType(typeof(DashboardSummaryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DashboardSummaryResponse>> GetSummary(CancellationToken ct)
        => Ok(await _dashboard.GetSummaryAsync(ct));
}
