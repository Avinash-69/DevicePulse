using System.Globalization;
using DevicePulse.Api.Data;
using DevicePulse.Api.Entities;
using DevicePulse.Api.Entities.Enums;
using DevicePulse.Api.Exceptions;
using DevicePulse.Api.Models;
using DevicePulse.Api.Services.Configuration;
using DevicePulse.Api.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace DevicePulse.Api.Services;

public interface ISettingsService
{
    Task<IReadOnlyList<SettingResponse>> GetAllAsync(string? category, CancellationToken ct = default);
    Task<SettingResponse> GetByKeyAsync(string key, CancellationToken ct = default);
    Task<SettingResponse> UpdateAsync(string key, UpdateSettingRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<SettingHistoryResponse>> GetHistoryAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken ct = default);
}

/// <summary>
/// The write path for runtime business configuration — the feature the master reference calls
/// its core pillar (Appendix A.1): an administrator changes a threshold in the UI and the
/// application behaves differently immediately, with no code change, no SQL console and no redeploy.
///
/// Three things make that safe rather than reckless:
///   * the value is validated against the setting's declared type and bounds before it is accepted,
///   * the previous value is appended to history rather than overwritten (§18),
///   * the change is audited (§17) and the read cache is invalidated so it takes effect at once.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly DevicePulseDbContext _db;
    private readonly IRuntimeSettings _runtimeSettings;
    private readonly IAuditService _audit;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<SettingsService> _logger;

    public SettingsService(
        DevicePulseDbContext db,
        IRuntimeSettings runtimeSettings,
        IAuditService audit,
        ICurrentUser currentUser,
        ILogger<SettingsService> logger)
    {
        _db = db;
        _runtimeSettings = runtimeSettings;
        _audit = audit;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SettingResponse>> GetAllAsync(string? category, CancellationToken ct = default)
    {
        var q = _db.SystemSettings.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(category))
            q = q.Where(s => s.Category == category);

        var rows = await q
            .OrderBy(s => s.Category).ThenBy(s => s.Key)
            .Select(s => new { Setting = s, UpdatedBy = s.UpdatedByUser != null ? s.UpdatedByUser.Name : null })
            .ToListAsync(ct);

        return rows.Select(r => Map(r.Setting, r.UpdatedBy)).ToList();
    }

    public async Task<SettingResponse> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        var row = await _db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => new { Setting = s, UpdatedBy = s.UpdatedByUser != null ? s.UpdatedByUser.Name : null })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException($"Setting '{key}' was not found.");

        return Map(row.Setting, row.UpdatedBy);
    }

    public async Task<SettingResponse> UpdateAsync(string key, UpdateSettingRequest request, CancellationToken ct = default)
    {
        var setting = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, ct)
            ?? throw new NotFoundException($"Setting '{key}' was not found.");

        if (!setting.IsEditable)
            throw new ForbiddenException($"Setting '{key}' is not editable at runtime.");

        // Normalised and bounds-checked against the setting's own declared metadata. This is
        // what Development Rule 6 buys: because the row knows it is an Integer with a min and a
        // max, a bad value is rejected here instead of exploding later in whatever code reads it.
        var newValue = ValidateAndNormalize(setting, request.Value);

        if (string.Equals(setting.Value, newValue, StringComparison.Ordinal))
        {
            // A no-op edit should not manufacture a history row — that would bury the real
            // changes in noise.
            return await GetByKeyAsync(key, ct);
        }

        if (!string.IsNullOrWhiteSpace(request.RowVersion))
        {
            try
            {
                _db.Entry(setting).Property(s => s.RowVersion).OriginalValue =
                    Convert.FromBase64String(request.RowVersion);
            }
            catch (FormatException)
            {
                throw new ValidationException("The supplied rowVersion is not valid base64.");
            }
        }

        var oldValue = setting.Value;

        setting.Value = newValue;
        setting.Version += 1;
        setting.UpdatedAt = DateTime.UtcNow;
        setting.UpdatedByUserId = _currentUser.UserId;

        _db.SettingHistory.Add(new SettingHistory
        {
            SettingId = setting.SettingId,
            Key = setting.Key,
            Version = setting.Version,
            OldValue = oldValue,
            NewValue = newValue,
            ChangedByUserId = _currentUser.UserId,
            ChangedAt = DateTime.UtcNow,
            ChangeReason = request.ChangeReason?.Trim()
        });

        _audit.Record(AuditActions.SettingChanged, nameof(SystemSetting), setting.Key,
            oldValue: new { Value = oldValue, Version = setting.Version - 1 },
            newValue: new { Value = newValue, setting.Version, request.ChangeReason });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyException(
                $"Setting '{key}' was changed by someone else after you opened it. Reload to see the current value.");
        }

        // Dropped immediately rather than waiting for the TTL to lapse: an administrator who
        // changes the offline timeout expects the next sweep to use it, not the one after.
        _runtimeSettings.Invalidate();

        _logger.LogInformation(
            "Setting {Key} changed from {OldValue} to {NewValue} by user {UserId} (version {Version}).",
            key, oldValue, newValue, _currentUser.UserId, setting.Version);

        return await GetByKeyAsync(key, ct);
    }

    public async Task<IReadOnlyList<SettingHistoryResponse>> GetHistoryAsync(string key, CancellationToken ct = default)
    {
        if (!await _db.SystemSettings.AnyAsync(s => s.Key == key, ct))
            throw new NotFoundException($"Setting '{key}' was not found.");

        return await _db.SettingHistory.AsNoTracking()
            .Where(h => h.Key == key)
            .OrderByDescending(h => h.Version)
            .Select(h => new SettingHistoryResponse(
                h.SettingHistoryId, h.Key, h.Version, h.OldValue, h.NewValue,
                h.ChangedByUser != null ? h.ChangedByUser.Name : null,
                h.ChangedAt, h.ChangeReason))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken ct = default) =>
        await _db.SystemSettings.AsNoTracking()
            .Select(s => s.Category).Distinct().OrderBy(c => c)
            .ToListAsync(ct);

    /// <summary>
    /// Parses and range-checks the incoming value against the setting's declared ValueType.
    /// Returns the canonical string form that gets stored, so the database never holds
    /// "TRUE", "true" and "1" for the same boolean setting.
    /// </summary>
    private static string ValidateAndNormalize(SystemSetting setting, string rawValue)
    {
        var value = rawValue?.Trim() ?? string.Empty;

        if (value.Length == 0)
            throw Invalid(setting.Key, "A value is required.");

        switch (setting.ValueType)
        {
            case SettingValueType.Integer:
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    throw Invalid(setting.Key, "Enter a whole number.");

                EnsureInRange(setting, parsed);
                return parsed.ToString(CultureInfo.InvariantCulture);
            }

            case SettingValueType.Decimal:
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    throw Invalid(setting.Key, "Enter a number.");

                EnsureInRange(setting, parsed);
                return parsed.ToString("0.####", CultureInfo.InvariantCulture);
            }

            case SettingValueType.Boolean:
            {
                if (!bool.TryParse(value, out var parsed))
                    throw Invalid(setting.Key, "Enter true or false.");

                return parsed ? "true" : "false";
            }

            case SettingValueType.Enum:
            {
                var allowed = (setting.AllowedValues ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                // Matched case-insensitively but stored in the catalog's own casing, so later
                // comparisons against the canonical value hold.
                var match = allowed.FirstOrDefault(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));

                if (match is null)
                    throw Invalid(setting.Key, $"Must be one of: {string.Join(", ", allowed)}.");

                return match;
            }

            default:
                if (value.Length > 1000)
                    throw Invalid(setting.Key, "Must be 1000 characters or fewer.");

                return value;
        }
    }

    private static void EnsureInRange(SystemSetting setting, double value)
    {
        if (setting.MinValue is not null && value < setting.MinValue)
            throw Invalid(setting.Key, $"Must be at least {setting.MinValue}{UnitSuffix(setting)}.");

        if (setting.MaxValue is not null && value > setting.MaxValue)
            throw Invalid(setting.Key, $"Must be at most {setting.MaxValue}{UnitSuffix(setting)}.");
    }

    private static string UnitSuffix(SystemSetting setting) =>
        string.IsNullOrWhiteSpace(setting.Unit) ? string.Empty : $" {setting.Unit}";

    private static ValidationException Invalid(string key, string message) =>
        new(new Dictionary<string, string[]> { [key] = [message] });

    private static SettingResponse Map(SystemSetting s, string? updatedBy) => new(
        s.SettingId,
        s.Key,
        s.Value,
        s.ValueType,
        s.Category,
        s.Description,
        s.Unit,
        s.IsEditable,
        s.MinValue,
        s.MaxValue,
        string.IsNullOrWhiteSpace(s.AllowedValues)
            ? null
            : s.AllowedValues.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        s.Version,
        updatedBy,
        s.UpdatedAt,
        s.RowVersion == null ? null : Convert.ToBase64String(s.RowVersion));
}
