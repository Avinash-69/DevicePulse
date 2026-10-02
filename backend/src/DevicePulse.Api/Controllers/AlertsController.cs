using DevicePulse.Api.Authorization;
using DevicePulse.Api.Models;
using DevicePulse.Api.Models.Common;
using DevicePulse.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DevicePulse.Api.Controllers;

/// <summary>Alert inbox and lifecycle: Open to Acknowledged to Resolved (§16).</summary>
[ApiController]
[Route("api/v1/alerts")]
[Authorize]
public sealed class AlertsController : ControllerBase
{
    private readonly IAlertService _alertService;

    public AlertsController(IAlertService alertService) => _alertService = alertService;

    [HttpGet]
    [HasPermission(Permissions.AlertView)]
    [ProducesResponseType(typeof(PagedResult<AlertResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<AlertResponse>>> GetAll(
        [FromQuery] AlertQuery query, CancellationToken ct)
        => Ok(await _alertService.QueryAsync(query, ct));

    [HttpGet("{id:long}")]
    [HasPermission(Permissions.AlertView)]
    [ProducesResponseType(typeof(AlertResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AlertResponse>> GetById(long id, CancellationToken ct)
        => Ok(await _alertService.GetByIdAsync(id, ct));

    /// <summary>Claims the alert: someone has seen it and is dealing with it.</summary>
    [HttpPost("{id:long}/acknowledge")]
    [HasPermission(Permissions.AlertResolve)]
    [ProducesResponseType(typeof(AlertResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AlertResponse>> Acknowledge(long id, CancellationToken ct)
        => Ok(await _alertService.AcknowledgeAsync(id, ct));

    /// <summary>
    /// Closes the alert, optionally with a note. Acknowledging first is not required — an
    /// operator who simply fixed the problem should be able to say so in one step.
    /// </summary>
    [HttpPost("{id:long}/resolve")]
    [HasPermission(Permissions.AlertResolve)]
    [ProducesResponseType(typeof(AlertResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AlertResponse>> Resolve(
        long id, [FromBody] ResolveAlertRequest? request, CancellationToken ct)
        => Ok(await _alertService.ResolveAsync(id, request ?? new ResolveAlertRequest(null), ct));
}

/// <summary>
/// Administration of the configurable alert rules (§10.2).
///
/// This controller is the runtime-configurability claim made concrete: changing when the system
/// raises an alert is an authenticated API call, not a code change and not a redeploy.
/// </summary>
[ApiController]
[Route("api/v1/alert-rules")]
[Authorize]
public sealed class AlertRulesController : ControllerBase
{
    private readonly IAlertRuleService _ruleService;

    public AlertRulesController(IAlertRuleService ruleService) => _ruleService = ruleService;

    [HttpGet]
    [HasPermission(Permissions.AlertRuleView)]
    [ProducesResponseType(typeof(IReadOnlyList<AlertRuleResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AlertRuleResponse>>> GetAll(CancellationToken ct)
        => Ok(await _ruleService.GetAllAsync(ct));

    /// <summary>
    /// The closed set of metrics, operators and severities a rule may use. The rule editor
    /// renders dropdowns from this rather than offering free text (Appendix C item 5).
    /// </summary>
    [HttpGet("vocabulary")]
    [HasPermission(Permissions.AlertRuleView)]
    [ProducesResponseType(typeof(AlertRuleVocabularyResponse), StatusCodes.Status200OK)]
    public ActionResult<AlertRuleVocabularyResponse> GetVocabulary()
        => Ok(_ruleService.GetVocabulary());

    [HttpGet("{id:int}")]
    [HasPermission(Permissions.AlertRuleView)]
    [ProducesResponseType(typeof(AlertRuleResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlertRuleResponse>> GetById(int id, CancellationToken ct)
        => Ok(await _ruleService.GetByIdAsync(id, ct));

    [HttpPost]
    [HasPermission(Permissions.AlertRuleManage)]
    [ProducesResponseType(typeof(AlertRuleResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<AlertRuleResponse>> Create(CreateAlertRuleRequest request, CancellationToken ct)
    {
        var created = await _ruleService.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.AlertRuleId }, created);
    }

    [HttpPut("{id:int}")]
    [HasPermission(Permissions.AlertRuleManage)]
    [ProducesResponseType(typeof(AlertRuleResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlertRuleResponse>> Update(
        int id, UpdateAlertRuleRequest request, CancellationToken ct)
        => Ok(await _ruleService.UpdateAsync(id, request, ct));

    /// <summary>
    /// Enables or disables a rule. The preferred way to stop a noisy rule: disabling keeps the
    /// definition and its history, deleting discards both.
    /// </summary>
    [HttpPatch("{id:int}/status")]
    [HasPermission(Permissions.AlertRuleManage)]
    [ProducesResponseType(typeof(AlertRuleResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlertRuleResponse>> SetStatus(
        int id, SetAlertRuleStatusRequest request, CancellationToken ct)
        => Ok(await _ruleService.SetStatusAsync(id, request.IsEnabled, ct));

    [HttpDelete("{id:int}")]
    [HasPermission(Permissions.AlertRuleManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await _ruleService.DeleteAsync(id, ct);
        return NoContent();
    }
}
