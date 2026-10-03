using DevicePulse.Api.Data;
using DevicePulse.Api.Entities;
using DevicePulse.Api.Entities.Enums;
using DevicePulse.Api.Exceptions;
using DevicePulse.Api.Models;
using DevicePulse.Api.Models.Common;
using DevicePulse.Api.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace DevicePulse.Api.Services;

public interface IDeviceService
{
    Task<PagedResult<DeviceResponse>> QueryAsync(DeviceQuery query, CancellationToken ct = default);
    Task<DeviceResponse> GetByIdAsync(int id, CancellationToken ct = default);
    Task<DeviceResponse> CreateAsync(CreateDeviceRequest request, CancellationToken ct = default);
    Task<DeviceResponse> UpdateAsync(int id, UpdateDeviceRequest request, CancellationToken ct = default);
    Task RetireAsync(int id, RetireDeviceRequest request, CancellationToken ct = default);
    Task<DeviceApiKeyResponse> IssueApiKeyAsync(int id, CancellationToken ct = default);
    Task RevokeApiKeyAsync(int id, CancellationToken ct = default);
}

public sealed class DeviceService : IDeviceService
{
    private readonly DevicePulseDbContext _db;
    private readonly IAuditService _audit;
    private readonly IDeviceApiKeyService _apiKeys;
    private readonly ILogger<DeviceService> _logger;

    public DeviceService(
        DevicePulseDbContext db,
        IAuditService audit,
        IDeviceApiKeyService apiKeys,
        ILogger<DeviceService> logger)
    {
        _db = db;
        _audit = audit;
        _apiKeys = apiKeys;
        _logger = logger;
    }

    public async Task<PagedResult<DeviceResponse>> QueryAsync(DeviceQuery query, CancellationToken ct = default)
    {
        var q = _db.Devices.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(d => d.DeviceName.Contains(term)
                          || d.DeviceCode.Contains(term)
                          || d.Location!.Name.Contains(term)
                          || d.DeviceType!.Name.Contains(term));
        }

        if (query.DeviceTypeId is not null)
            q = q.Where(d => d.DeviceTypeId == query.DeviceTypeId);

        if (query.LocationId is not null)
            q = q.Where(d => d.LocationId == query.LocationId);

        if (query.LifecycleStatus is not null)
            q = q.Where(d => d.LifecycleStatus == query.LifecycleStatus);
        else
            // Retired devices are kept forever but are noise in the default list, so they are
            // hidden unless explicitly asked for (Appendix C item 2).
            q = q.Where(d => d.LifecycleStatus != LifecycleStatus.Retired);

        if (query.ConnectivityStatus is not null)
            q = q.Where(d => d.ConnectivityStatus == query.ConnectivityStatus);

        var total = await q.CountAsync(ct);

        // Sort column comes from a closed whitelist, not from interpolating the client's string
        // into the query — an unvalidated sort field is a classic injection and index-thrash hole.
        q = ApplySort(q, query.SortBy, query.SortDescending);

        var items = await q
            .Skip(query.Skip).Take(query.PageSize)
            .Select(d => new DeviceResponse(
                d.DeviceId, d.DeviceCode, d.DeviceName,
                d.DeviceTypeId, d.DeviceType!.Name,
                d.LocationId, d.Location!.Name,
                d.LifecycleStatus, d.ConnectivityStatus, d.LastSeenAt,
                d.ApiKeyHash != null,
                d.Alerts.Count(a => a.Status != AlertStatus.Resolved),
                d.CreatedAt, d.UpdatedAt,
                d.RowVersion == null ? null : Convert.ToBase64String(d.RowVersion)))
            .ToListAsync(ct);

        return new PagedResult<DeviceResponse>(items, query.Page, query.PageSize, total);
    }

    private static IQueryable<Device> ApplySort(IQueryable<Device> q, string? sortBy, bool descending) =>
        (sortBy?.ToLowerInvariant()) switch
        {
            "devicecode" => descending ? q.OrderByDescending(d => d.DeviceCode) : q.OrderBy(d => d.DeviceCode),
            "lastseenat" => descending ? q.OrderByDescending(d => d.LastSeenAt) : q.OrderBy(d => d.LastSeenAt),
            "createdat" => descending ? q.OrderByDescending(d => d.CreatedAt) : q.OrderBy(d => d.CreatedAt),
            "connectivitystatus" => descending ? q.OrderByDescending(d => d.ConnectivityStatus) : q.OrderBy(d => d.ConnectivityStatus),

            // Paging without a deterministic order can return the same row on two pages, so an
            // unrecognised sort field falls back to a stable column rather than to no order.
            _ => descending ? q.OrderByDescending(d => d.DeviceName) : q.OrderBy(d => d.DeviceName)
        };

    public async Task<DeviceResponse> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var device = await _db.Devices.AsNoTracking()
            .Where(d => d.DeviceId == id)
            .Select(d => new DeviceResponse(
                d.DeviceId, d.DeviceCode, d.DeviceName,
                d.DeviceTypeId, d.DeviceType!.Name,
                d.LocationId, d.Location!.Name,
                d.LifecycleStatus, d.ConnectivityStatus, d.LastSeenAt,
                d.ApiKeyHash != null,
                d.Alerts.Count(a => a.Status != AlertStatus.Resolved),
                d.CreatedAt, d.UpdatedAt,
                d.RowVersion == null ? null : Convert.ToBase64String(d.RowVersion)))
            .FirstOrDefaultAsync(ct);

        return device ?? throw new NotFoundException(nameof(Device), id);
    }

    public async Task<DeviceResponse> CreateAsync(CreateDeviceRequest request, CancellationToken ct = default)
    {
        var code = request.DeviceCode.Trim();

        if (await _db.Devices.AnyAsync(d => d.DeviceCode == code, ct))
            throw new ConflictException($"A device with code '{code}' already exists.");

        await EnsureReferenceDataAsync(request.DeviceTypeId, request.LocationId, ct);

        var device = new Device
        {
            DeviceCode = code,
            DeviceName = request.DeviceName.Trim(),
            DeviceTypeId = request.DeviceTypeId,
            LocationId = request.LocationId,

            // A brand-new device has not reported yet, so its connectivity is genuinely
            // Unknown — not Offline, which would imply we had seen it and lost it.
            LifecycleStatus = LifecycleStatus.Registered,
            ConnectivityStatus = ConnectivityStatus.Unknown,
            CreatedAt = DateTime.UtcNow
        };

        _db.Devices.Add(device);

        _audit.Record(AuditActions.DeviceCreated, nameof(Device), null,
            newValue: new { device.DeviceCode, device.DeviceName, device.DeviceTypeId, device.LocationId });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The check above and this insert are not one atomic operation, so two concurrent
            // registrations of the same code can both get past it. The unique index is the real
            // guard; this turns its raw failure into the 409 the caller expects.
            //
            // Re-queried in the catch body, not a `when` filter: await is not permitted there.
            _db.ChangeTracker.Clear();

            if (await _db.Devices.AnyAsync(d => d.DeviceCode == code, ct))
            {
                _logger.LogWarning(
                    "Concurrent registration of device code {DeviceCode} was rejected by the unique index.", code);

                throw new ConflictException($"A device with code '{code}' already exists.");
            }

            throw;
        }

        _logger.LogInformation("Device registered: {DeviceCode} ({DeviceName})", device.DeviceCode, device.DeviceName);
        return await GetByIdAsync(device.DeviceId, ct);
    }

    public async Task<DeviceResponse> UpdateAsync(int id, UpdateDeviceRequest request, CancellationToken ct = default)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == id, ct)
            ?? throw new NotFoundException(nameof(Device), id);

        if (device.LifecycleStatus == LifecycleStatus.Retired && request.LifecycleStatus == LifecycleStatus.Retired)
            throw new ValidationException("This device is retired. Reactivate it before editing.");

        await EnsureReferenceDataAsync(request.DeviceTypeId, request.LocationId, ct);

        ApplyConcurrencyToken(device, request.RowVersion);

        var before = new
        {
            device.DeviceName,
            device.DeviceTypeId,
            device.LocationId,
            device.LifecycleStatus
        };

        device.DeviceName = request.DeviceName.Trim();
        device.DeviceTypeId = request.DeviceTypeId;
        device.LocationId = request.LocationId;
        device.LifecycleStatus = request.LifecycleStatus;
        device.UpdatedAt = DateTime.UtcNow;

        _audit.Record(AuditActions.DeviceUpdated, nameof(Device), id,
            oldValue: before,
            newValue: new { device.DeviceName, device.DeviceTypeId, device.LocationId, device.LifecycleStatus });

        await SaveWithConcurrencyCheckAsync(ct);

        return await GetByIdAsync(id, ct);
    }

    /// <summary>
    /// Retirement, not deletion (Appendix C item 2). A decommissioned device still has to
    /// explain its own history: its telemetry and the alerts it raised stay queryable, and a
    /// hard delete would cascade both away.
    /// </summary>
    public async Task RetireAsync(int id, RetireDeviceRequest request, CancellationToken ct = default)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == id, ct)
            ?? throw new NotFoundException(nameof(Device), id);

        if (device.LifecycleStatus == LifecycleStatus.Retired)
            throw new ConflictException("This device is already retired.");

        var before = new { device.LifecycleStatus, device.ConnectivityStatus };

        device.LifecycleStatus = LifecycleStatus.Retired;
        device.ConnectivityStatus = ConnectivityStatus.Unknown;
        device.UpdatedAt = DateTime.UtcNow;

        // The credential is destroyed with the retirement. A retired device that could still
        // post telemetry would keep resurrecting itself in the dashboard.
        device.ApiKeyHash = null;
        device.ApiKeyIssuedAt = null;

        // Its open alerts are closed too — nobody is going to act on an alert for hardware
        // that has been taken out of service.
        var openAlerts = await _db.Alerts
            .Where(a => a.DeviceId == id && a.Status != AlertStatus.Resolved)
            .ToListAsync(ct);

        foreach (var alert in openAlerts)
        {
            alert.Status = AlertStatus.Resolved;
            alert.ResolvedAt = DateTime.UtcNow;
            alert.ResolutionNote = "Closed automatically: the device was retired.";
        }

        _audit.Record(AuditActions.DeviceRetired, nameof(Device), id,
            oldValue: before,
            newValue: new { LifecycleStatus = LifecycleStatus.Retired, request.Reason, ClosedAlerts = openAlerts.Count });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Device {DeviceCode} retired; {AlertCount} open alert(s) closed.",
            device.DeviceCode, openAlerts.Count);
    }

    public async Task<DeviceApiKeyResponse> IssueApiKeyAsync(int id, CancellationToken ct = default)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == id, ct)
            ?? throw new NotFoundException(nameof(Device), id);

        if (device.LifecycleStatus == LifecycleStatus.Retired)
            throw new ValidationException("A retired device cannot be issued an ingestion key.");

        var (plainTextKey, hash) = _apiKeys.Generate(device.DeviceCode);

        // Issuing replaces any previous key, which is also how a key is rotated after a
        // suspected leak. Only the hash is kept, so the plaintext below is the single copy
        // that will ever exist.
        device.ApiKeyHash = hash;
        device.ApiKeyIssuedAt = DateTime.UtcNow;
        device.UpdatedAt = DateTime.UtcNow;

        if (device.LifecycleStatus == LifecycleStatus.Registered)
            device.LifecycleStatus = LifecycleStatus.Active;

        _audit.Record(AuditActions.DeviceApiKeyIssued, nameof(Device), id,
            newValue: new { device.DeviceCode, device.ApiKeyIssuedAt });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Ingestion key issued for device {DeviceCode}.", device.DeviceCode);

        return new DeviceApiKeyResponse(device.DeviceId, device.DeviceCode, plainTextKey, device.ApiKeyIssuedAt!.Value);
    }

    public async Task RevokeApiKeyAsync(int id, CancellationToken ct = default)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == id, ct)
            ?? throw new NotFoundException(nameof(Device), id);

        if (device.ApiKeyHash is null)
            throw new ConflictException("This device has no ingestion key to revoke.");

        device.ApiKeyHash = null;
        device.ApiKeyIssuedAt = null;
        device.UpdatedAt = DateTime.UtcNow;

        _audit.Record(AuditActions.DeviceApiKeyRevoked, nameof(Device), id, newValue: new { device.DeviceCode });

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Ingestion key revoked for device {DeviceCode}.", device.DeviceCode);
    }

    /// <summary>
    /// Device type and location are validated here rather than being left to the foreign key:
    /// a FK violation surfaces as an opaque 500, while this produces a message that names the
    /// field the caller got wrong.
    /// </summary>
    private async Task EnsureReferenceDataAsync(int deviceTypeId, int locationId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();

        if (!await _db.DeviceTypes.AnyAsync(t => t.DeviceTypeId == deviceTypeId && t.IsActive, ct))
            errors["deviceTypeId"] = ["That device type does not exist or is inactive."];

        if (!await _db.Locations.AnyAsync(l => l.LocationId == locationId && l.IsActive, ct))
            errors["locationId"] = ["That location does not exist or is inactive."];

        if (errors.Count > 0)
            throw new ValidationException(errors);
    }

    /// <summary>
    /// Attaches the client's RowVersion so EF can detect a stale edit (Appendix D.1). Omitting
    /// it is allowed and means last-write-wins, which is the right default for a single-admin
    /// setup; the Angular client always sends it.
    /// </summary>
    private void ApplyConcurrencyToken(Device device, string? rowVersion)
    {
        if (string.IsNullOrWhiteSpace(rowVersion))
            return;

        try
        {
            _db.Entry(device).Property(d => d.RowVersion).OriginalValue = Convert.FromBase64String(rowVersion);
        }
        catch (FormatException)
        {
            throw new ValidationException("The supplied rowVersion is not valid base64.");
        }
    }

    private async Task SaveWithConcurrencyCheckAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyException(
                "Someone else changed this device after you opened it. Reload to see their changes, then reapply yours.");
        }
    }
}
