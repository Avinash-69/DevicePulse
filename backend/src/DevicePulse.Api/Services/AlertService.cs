using DevicePulse.Api.Data;
using DevicePulse.Api.Entities;
using DevicePulse.Api.Entities.Enums;
using DevicePulse.Api.Exceptions;
using DevicePulse.Api.Models;
using DevicePulse.Api.Models.Common;
using DevicePulse.Api.Services.Alerting;
using DevicePulse.Api.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace DevicePulse.Api.Services;

public interface IAlertService
{
    Task<PagedResult<AlertResponse>> QueryAsync(AlertQuery query, CancellationToken ct = default);
    Task<AlertResponse> GetByIdAsync(long id, CancellationToken ct = default);
    Task<AlertResponse> AcknowledgeAsync(long id, CancellationToken ct = default);
    Task<AlertResponse> ResolveAsync(long id, ResolveAlertRequest request, CancellationToken ct = default);
}

public sealed class AlertService : IAlertService
{
    private readonly DevicePulseDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly ILogger<AlertService> _logger;

    public AlertService(
        DevicePulseDbContext db,
        ICurrentUser currentUser,
        IAuditService audit,
        ILogger<AlertService> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _audit = audit;
        _logger = logger;
    }

    public async Task<PagedResult<AlertResponse>> QueryAsync(AlertQuery query, CancellationToken ct = default)
    {
        var q = _db.Alerts.AsNoTracking();

        if (query.Status is not null)
            q = q.Where(a => a.Status == query.Status);

        if (query.Severity is not null)
            q = q.Where(a => a.Severity == query.Severity);

        if (query.DeviceId is not null)
            q = q.Where(a => a.DeviceId == query.DeviceId);

        if (query.From is not null)
            q = q.Where(a => a.CreatedAt >= query.From);

        if (query.To is not null)
            q = q.Where(a => a.CreatedAt <= query.To);

        var total = await q.CountAsync(ct);

        // Severity descending first: when an operator opens the alert list, the thing that
        // matters most belongs at the top, not whatever happened to arrive last.
        var items = await q
            .OrderByDescending(a => a.Severity)
            .ThenByDescending(a => a.CreatedAt)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(Projection)
            .ToListAsync(ct);

        return new PagedResult<AlertResponse>(items, query.Page, query.PageSize, total);
    }

    public async Task<AlertResponse> GetByIdAsync(long id, CancellationToken ct = default)
    {
        var alert = await _db.Alerts.AsNoTracking()
            .Where(a => a.AlertId == id)
            .Select(Projection)
            .FirstOrDefaultAsync(ct);

        return alert ?? throw new NotFoundException(nameof(Alert), id);
    }

    public async Task<AlertResponse> AcknowledgeAsync(long id, CancellationToken ct = default)
    {
        var alert = await _db.Alerts.FirstOrDefaultAsync(a => a.AlertId == id, ct)
            ?? throw new NotFoundException(nameof(Alert), id);

        if (alert.Status == AlertStatus.Resolved)
            throw new ConflictException("This alert is already resolved.");

        if (alert.Status == AlertStatus.Acknowledged)
            throw new ConflictException("This alert has already been acknowledged.");

        alert.Status = AlertStatus.Acknowledged;
        alert.AcknowledgedAt = DateTime.UtcNow;
        alert.AcknowledgedByUserId = _currentUser.UserId;

        _audit.Record(AuditActions.AlertAcknowledged, nameof(Alert), id,
            oldValue: new { Status = AlertStatus.Open },
            newValue: new { Status = AlertStatus.Acknowledged, alert.AcknowledgedByUserId });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Alert {AlertId} acknowledged by user {UserId}.", id, _currentUser.UserId);
        return await GetByIdAsync(id, ct);
    }

    public async Task<AlertResponse> ResolveAsync(long id, ResolveAlertRequest request, CancellationToken ct = default)
    {
        var alert = await _db.Alerts.FirstOrDefaultAsync(a => a.AlertId == id, ct)
            ?? throw new NotFoundException(nameof(Alert), id);

        if (alert.Status == AlertStatus.Resolved)
            throw new ConflictException("This alert is already resolved.");

        var previousStatus = alert.Status;

        // Resolving without acknowledging first is allowed: an operator who fixes the problem
        // outright should not have to click through an intermediate state to say so.
        alert.Status = AlertStatus.Resolved;
        alert.ResolvedAt = DateTime.UtcNow;
        alert.ResolvedByUserId = _currentUser.UserId;
        alert.ResolutionNote = request.ResolutionNote?.Trim();

        _audit.Record(AuditActions.AlertResolved, nameof(Alert), id,
            oldValue: new { Status = previousStatus },
            newValue: new { Status = AlertStatus.Resolved, alert.ResolvedByUserId, alert.ResolutionNote });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Alert {AlertId} resolved by user {UserId}.", id, _currentUser.UserId);
        return await GetByIdAsync(id, ct);
    }

    /// <summary>
    /// Shared projection so the list and the single-item read cannot drift apart. Expressed as
    /// an expression tree rather than a method so EF can translate it into SQL.
    /// </summary>
    private static readonly System.Linq.Expressions.Expression<Func<Alert, AlertResponse>> Projection =
        a => new AlertResponse(
            a.AlertId,
            a.DeviceId,
            a.Device!.DeviceName,
            a.Device.DeviceCode,
            a.AlertRuleId,
            a.AlertRule != null ? a.AlertRule.Name : null,
            a.Metric,
            a.Message,
            a.Severity,
            a.Status,
            a.TriggeredValue,
            a.Threshold,
            a.CreatedAt,
            a.AcknowledgedAt,
            a.AcknowledgedByUser != null ? a.AcknowledgedByUser.Name : null,
            a.ResolvedAt,
            a.ResolvedByUser != null ? a.ResolvedByUser.Name : null,
            a.ResolutionNote);
}

public interface IAlertRuleService
{
    Task<IReadOnlyList<AlertRuleResponse>> GetAllAsync(CancellationToken ct = default);
    Task<AlertRuleResponse> GetByIdAsync(int id, CancellationToken ct = default);
    Task<AlertRuleResponse> CreateAsync(CreateAlertRuleRequest request, CancellationToken ct = default);
    Task<AlertRuleResponse> UpdateAsync(int id, UpdateAlertRuleRequest request, CancellationToken ct = default);
    Task<AlertRuleResponse> SetStatusAsync(int id, bool isEnabled, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    AlertRuleVocabularyResponse GetVocabulary();
}

/// <summary>
/// Administration of the configurable business rules (§10.2, §16).
///
/// This is the service that delivers the master reference's headline claim: the business says
/// "battery under 15% should raise a Medium alert" and an administrator expresses that through
/// the UI, with no code change and no redeploy.
/// </summary>
public sealed class AlertRuleService : IAlertRuleService
{
    private readonly DevicePulseDbContext _db;
    private readonly IAuditService _audit;
    private readonly ILogger<AlertRuleService> _logger;

    public AlertRuleService(DevicePulseDbContext db, IAuditService audit, ILogger<AlertRuleService> logger)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AlertRuleResponse>> GetAllAsync(CancellationToken ct = default)
    {
        // Ordered after materialising rather than in SQL: the sort keys live on the projected
        // RuleRow, which EF cannot translate into an ORDER BY. That is fine here because the
        // rule set is inherently small -- it is hand-authored configuration, not data -- so
        // there is no page to push down and nothing is being over-fetched.
        var rows = await QueryRows().ToListAsync(ct);

        return rows
            .OrderBy(r => r.Rule.Metric)
            .ThenBy(r => r.Rule.Name, StringComparer.OrdinalIgnoreCase)
            .Select(Map)
            .ToList();
    }

    public async Task<AlertRuleResponse> GetByIdAsync(int id, CancellationToken ct = default)
    {
        // Filtered before the projection. Applying Where to the projected RuleRow instead would
        // put the predicate on a constructed object, which EF cannot translate into SQL.
        var row = await QueryRows(r => r.AlertRuleId == id).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException(nameof(AlertRule), id);

        return Map(row);
    }

    public async Task<AlertRuleResponse> CreateAsync(CreateAlertRuleRequest request, CancellationToken ct = default)
    {
        var name = request.Name.Trim();

        if (await _db.AlertRules.AnyAsync(r => r.Name == name, ct))
            throw new ConflictException($"An alert rule named '{name}' already exists.");

        Validate(request.Metric, request.Operator, request.Threshold);
        await EnsureDeviceTypeAsync(request.DeviceTypeId, ct);

        var rule = new AlertRule
        {
            Name = name,
            Description = request.Description?.Trim(),
            Metric = request.Metric,
            Operator = request.Operator,
            Threshold = request.Threshold,
            Severity = request.Severity,
            IsEnabled = request.IsEnabled,
            CooldownSeconds = request.CooldownSeconds,
            DeviceTypeId = request.DeviceTypeId,
            CreatedAt = DateTime.UtcNow
        };

        _db.AlertRules.Add(rule);

        _audit.Record(AuditActions.AlertRuleCreated, nameof(AlertRule), null,
            newValue: new { rule.Name, rule.Metric, rule.Operator, rule.Threshold, rule.Severity, rule.IsEnabled, rule.CooldownSeconds });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Alert rule created: {RuleName} ({Metric} {Operator} {Threshold}).",
            rule.Name, rule.Metric, rule.Operator, rule.Threshold);

        return await GetByIdAsync(rule.AlertRuleId, ct);
    }

    public async Task<AlertRuleResponse> UpdateAsync(int id, UpdateAlertRuleRequest request, CancellationToken ct = default)
    {
        var rule = await _db.AlertRules.FirstOrDefaultAsync(r => r.AlertRuleId == id, ct)
            ?? throw new NotFoundException(nameof(AlertRule), id);

        var name = request.Name.Trim();

        if (await _db.AlertRules.AnyAsync(r => r.Name == name && r.AlertRuleId != id, ct))
            throw new ConflictException($"An alert rule named '{name}' already exists.");

        Validate(request.Metric, request.Operator, request.Threshold);
        await EnsureDeviceTypeAsync(request.DeviceTypeId, ct);

        if (!string.IsNullOrWhiteSpace(request.RowVersion))
        {
            try
            {
                _db.Entry(rule).Property(r => r.RowVersion).OriginalValue = Convert.FromBase64String(request.RowVersion);
            }
            catch (FormatException)
            {
                throw new ValidationException("The supplied rowVersion is not valid base64.");
            }
        }

        // Captured before mutation so the audit entry can show the old and new threshold
        // side by side — the single most useful thing to see when an alert storm starts.
        var before = new
        {
            rule.Name, rule.Metric, rule.Operator, rule.Threshold,
            rule.Severity, rule.CooldownSeconds, rule.DeviceTypeId
        };

        rule.Name = name;
        rule.Description = request.Description?.Trim();
        rule.Metric = request.Metric;
        rule.Operator = request.Operator;
        rule.Threshold = request.Threshold;
        rule.Severity = request.Severity;
        rule.CooldownSeconds = request.CooldownSeconds;
        rule.DeviceTypeId = request.DeviceTypeId;
        rule.UpdatedAt = DateTime.UtcNow;

        _audit.Record(AuditActions.AlertRuleUpdated, nameof(AlertRule), id,
            oldValue: before,
            newValue: new { rule.Name, rule.Metric, rule.Operator, rule.Threshold, rule.Severity, rule.CooldownSeconds, rule.DeviceTypeId });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyException(
                "Someone else changed this rule after you opened it. Reload to see their changes, then reapply yours.");
        }

        return await GetByIdAsync(id, ct);
    }

    public async Task<AlertRuleResponse> SetStatusAsync(int id, bool isEnabled, CancellationToken ct = default)
    {
        var rule = await _db.AlertRules.FirstOrDefaultAsync(r => r.AlertRuleId == id, ct)
            ?? throw new NotFoundException(nameof(AlertRule), id);

        if (rule.IsEnabled == isEnabled)
            return await GetByIdAsync(id, ct);

        rule.IsEnabled = isEnabled;
        rule.UpdatedAt = DateTime.UtcNow;

        _audit.Record(AuditActions.AlertRuleStatusChanged, nameof(AlertRule), id,
            oldValue: new { IsEnabled = !isEnabled },
            newValue: new { IsEnabled = isEnabled });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Alert rule {RuleName} {State}.", rule.Name, isEnabled ? "enabled" : "disabled");
        return await GetByIdAsync(id, ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var rule = await _db.AlertRules.FirstOrDefaultAsync(r => r.AlertRuleId == id, ct)
            ?? throw new NotFoundException(nameof(AlertRule), id);

        // The alerts this rule raised survive it: Alert.AlertRuleId is SetNull on delete, and
        // each alert already carries its own copy of the message, value and threshold.
        // Disabling is still the better operational choice, which the UI steers toward.
        _audit.Record(AuditActions.AlertRuleDeleted, nameof(AlertRule), id,
            oldValue: new { rule.Name, rule.Metric, rule.Operator, rule.Threshold, rule.Severity });

        _db.AlertRules.Remove(rule);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Alert rule deleted: {RuleName}.", rule.Name);
    }

    /// <summary>
    /// The closed vocabulary the rule editor is allowed to offer (Appendix C item 5). Served
    /// from the enums themselves so the UI cannot drift out of sync with what the backend
    /// will actually accept.
    /// </summary>
    public AlertRuleVocabularyResponse GetVocabulary() => new(
        Metrics: Enum.GetValues<AlertMetric>()
            .Select(m => new AlertRuleVocabularyItem(m.ToString(), MetricLabel(m), AlertRuleEvaluator.UnitFor(m)))
            .ToList(),
        Operators: Enum.GetValues<AlertOperator>()
            .Select(o => new AlertRuleVocabularyItem(o.ToString(), AlertRuleEvaluator.SymbolFor(o)))
            .ToList(),
        Severities: Enum.GetValues<AlertSeverity>()
            .Select(s => new AlertRuleVocabularyItem(s.ToString(), s.ToString()))
            .ToList());

    private static string MetricLabel(AlertMetric metric) => metric switch
    {
        AlertMetric.Temperature => "Temperature",
        AlertMetric.Battery => "Battery level",
        AlertMetric.SignalStrength => "Signal strength",
        AlertMetric.LastSeenAgeSeconds => "Time since last report",
        _ => metric.ToString()
    };

    /// <summary>
    /// Range checks per metric. A threshold of 500% battery or -400 °C would be accepted by the
    /// type system and then never fire (or always fire), which is worse than a rejection —
    /// the administrator would believe the rule was protecting them.
    /// </summary>
    private static void Validate(AlertMetric metric, AlertOperator op, double threshold)
    {
        var errors = new Dictionary<string, string[]>();

        var (min, max) = metric switch
        {
            AlertMetric.Temperature => (-273d, 1000d),
            AlertMetric.Battery => (0d, 100d),
            AlertMetric.SignalStrength => (-150d, 0d),
            AlertMetric.LastSeenAgeSeconds => (1d, 2_592_000d), // one second to 30 days
            _ => (double.MinValue, double.MaxValue)
        };

        if (threshold < min || threshold > max)
            errors["threshold"] = [$"For {metric}, the threshold must be between {min} and {max}."];

        // An equality test against a continuously-varying float will essentially never be true.
        // Allowing it would create a rule that silently does nothing.
        if (op is AlertOperator.EqualTo or AlertOperator.NotEqualTo
            && metric is AlertMetric.Temperature or AlertMetric.Battery)
        {
            errors["operator"] =
                [$"An exact equality test is not meaningful for {metric}, which varies continuously. Use a threshold comparison instead."];
        }

        if (errors.Count > 0)
            throw new ValidationException(errors);
    }

    private async Task EnsureDeviceTypeAsync(int? deviceTypeId, CancellationToken ct)
    {
        if (deviceTypeId is null)
            return;

        if (!await _db.DeviceTypes.AnyAsync(t => t.DeviceTypeId == deviceTypeId, ct))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["deviceTypeId"] = ["That device type does not exist."]
            });
    }

    /// <summary>
    /// Only the columns the response needs are pulled back, plus the two values that require a
    /// join. The DTO itself is assembled in memory because ConditionSummary is built from the
    /// enum values, and string-formatting an enum is not something SQL Server can be asked to do.
    /// </summary>
    private IQueryable<RuleRow> QueryRows(
        System.Linq.Expressions.Expression<Func<AlertRule, bool>>? predicate = null)
    {
        var query = _db.AlertRules.AsNoTracking();

        // Any filter is applied to the entity, before projecting — see GetByIdAsync.
        if (predicate is not null)
            query = query.Where(predicate);

        return query.Select(r => new RuleRow(r, r.DeviceType != null ? r.DeviceType.Name : null, r.Alerts.Count));
    }

    private sealed record RuleRow(AlertRule Rule, string? DeviceTypeName, int TriggeredCount);

    private static AlertRuleResponse Map(RuleRow row)
    {
        var r = row.Rule;

        var condition = r.Metric == AlertMetric.LastSeenAgeSeconds
            ? $"No report for {AlertRuleEvaluator.SymbolFor(r.Operator)} {r.Threshold:0.##}s"
            : $"{r.Metric} {AlertRuleEvaluator.SymbolFor(r.Operator)} {r.Threshold:0.##}{AlertRuleEvaluator.UnitFor(r.Metric)}";

        return new AlertRuleResponse(
            r.AlertRuleId,
            r.Name,
            r.Description,
            r.Metric,
            r.Operator,
            r.Threshold,
            r.Severity,
            r.IsEnabled,
            r.CooldownSeconds,
            r.DeviceTypeId,
            row.DeviceTypeName,
            condition,
            row.TriggeredCount,
            r.CreatedAt,
            r.UpdatedAt,
            r.RowVersion == null ? null : Convert.ToBase64String(r.RowVersion));
    }
}
