using DevicePulse.Api.Data;
using DevicePulse.Api.Entities;
using DevicePulse.Api.Exceptions;
using DevicePulse.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace DevicePulse.Api.Services;

public interface IReferenceDataService
{
    Task<IReadOnlyList<DeviceTypeResponse>> GetDeviceTypesAsync(bool includeInactive, CancellationToken ct = default);
    Task<DeviceTypeResponse> CreateDeviceTypeAsync(CreateDeviceTypeRequest request, CancellationToken ct = default);
    Task<DeviceTypeResponse> UpdateDeviceTypeAsync(int id, UpdateDeviceTypeRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<LocationResponse>> GetLocationsAsync(bool includeInactive, CancellationToken ct = default);
    Task<LocationResponse> CreateLocationAsync(CreateLocationRequest request, CancellationToken ct = default);
    Task<LocationResponse> UpdateLocationAsync(int id, UpdateLocationRequest request, CancellationToken ct = default);
}

/// <summary>
/// Device types and locations — runtime-configurable reference data (§4.2).
///
/// Neither supports deletion. Both are referenced by devices (and types by alert rules), and a
/// delete would either orphan live rows or cascade away real history. Deactivating keeps a type
/// out of the "register a new device" dropdown while leaving existing devices intact, which is
/// what an operator actually wants when a hardware model is discontinued.
/// </summary>
public sealed class ReferenceDataService : IReferenceDataService
{
    private readonly DevicePulseDbContext _db;
    private readonly IAuditService _audit;

    public ReferenceDataService(DevicePulseDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<IReadOnlyList<DeviceTypeResponse>> GetDeviceTypesAsync(
        bool includeInactive, CancellationToken ct = default)
    {
        var q = _db.DeviceTypes.AsNoTracking();

        if (!includeInactive)
            q = q.Where(t => t.IsActive);

        return await q
            .OrderBy(t => t.Name)
            .Select(t => new DeviceTypeResponse(t.DeviceTypeId, t.Name, t.Description, t.IsActive, t.Devices.Count))
            .ToListAsync(ct);
    }

    public async Task<DeviceTypeResponse> CreateDeviceTypeAsync(
        CreateDeviceTypeRequest request, CancellationToken ct = default)
    {
        var name = request.Name.Trim();

        if (await _db.DeviceTypes.AnyAsync(t => t.Name == name, ct))
            throw new ConflictException($"A device type named '{name}' already exists.");

        var type = new DeviceType
        {
            Name = name,
            Description = request.Description?.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _db.DeviceTypes.Add(type);
        _audit.Record(AuditActions.DeviceTypeCreated, nameof(DeviceType), null, newValue: new { type.Name, type.Description });

        await _db.SaveChangesAsync(ct);

        return new DeviceTypeResponse(type.DeviceTypeId, type.Name, type.Description, type.IsActive, 0);
    }

    public async Task<DeviceTypeResponse> UpdateDeviceTypeAsync(
        int id, UpdateDeviceTypeRequest request, CancellationToken ct = default)
    {
        var type = await _db.DeviceTypes.FirstOrDefaultAsync(t => t.DeviceTypeId == id, ct)
            ?? throw new NotFoundException(nameof(DeviceType), id);

        var name = request.Name.Trim();

        if (await _db.DeviceTypes.AnyAsync(t => t.Name == name && t.DeviceTypeId != id, ct))
            throw new ConflictException($"A device type named '{name}' already exists.");

        var deviceCount = await _db.Devices.CountAsync(d => d.DeviceTypeId == id, ct);

        // Deactivation is blocked while devices still use the type, because an inactive type
        // fails the reference-data check in DeviceService — every edit to those devices would
        // start failing validation for a reason the operator never chose.
        if (!request.IsActive && type.IsActive && deviceCount > 0)
            throw new ValidationException(
                $"{deviceCount} device(s) still use this type. Reassign them before deactivating it.");

        var before = new { type.Name, type.Description, type.IsActive };

        type.Name = name;
        type.Description = request.Description?.Trim();
        type.IsActive = request.IsActive;
        type.UpdatedAt = DateTime.UtcNow;

        _audit.Record(AuditActions.DeviceTypeUpdated, nameof(DeviceType), id,
            oldValue: before, newValue: new { type.Name, type.Description, type.IsActive });

        await _db.SaveChangesAsync(ct);

        return new DeviceTypeResponse(type.DeviceTypeId, type.Name, type.Description, type.IsActive, deviceCount);
    }

    public async Task<IReadOnlyList<LocationResponse>> GetLocationsAsync(
        bool includeInactive, CancellationToken ct = default)
    {
        var q = _db.Locations.AsNoTracking();

        if (!includeInactive)
            q = q.Where(l => l.IsActive);

        return await q
            .OrderBy(l => l.Name)
            .Select(l => new LocationResponse(l.LocationId, l.Name, l.Description, l.IsActive, l.Devices.Count))
            .ToListAsync(ct);
    }

    public async Task<LocationResponse> CreateLocationAsync(
        CreateLocationRequest request, CancellationToken ct = default)
    {
        var name = request.Name.Trim();

        if (await _db.Locations.AnyAsync(l => l.Name == name, ct))
            throw new ConflictException($"A location named '{name}' already exists.");

        var location = new Location
        {
            Name = name,
            Description = request.Description?.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _db.Locations.Add(location);
        _audit.Record(AuditActions.LocationCreated, nameof(Location), null, newValue: new { location.Name, location.Description });

        await _db.SaveChangesAsync(ct);

        return new LocationResponse(location.LocationId, location.Name, location.Description, location.IsActive, 0);
    }

    public async Task<LocationResponse> UpdateLocationAsync(
        int id, UpdateLocationRequest request, CancellationToken ct = default)
    {
        var location = await _db.Locations.FirstOrDefaultAsync(l => l.LocationId == id, ct)
            ?? throw new NotFoundException(nameof(Location), id);

        var name = request.Name.Trim();

        if (await _db.Locations.AnyAsync(l => l.Name == name && l.LocationId != id, ct))
            throw new ConflictException($"A location named '{name}' already exists.");

        var deviceCount = await _db.Devices.CountAsync(d => d.LocationId == id, ct);

        if (!request.IsActive && location.IsActive && deviceCount > 0)
            throw new ValidationException(
                $"{deviceCount} device(s) are still assigned to this location. Reassign them before deactivating it.");

        var before = new { location.Name, location.Description, location.IsActive };

        location.Name = name;
        location.Description = request.Description?.Trim();
        location.IsActive = request.IsActive;
        location.UpdatedAt = DateTime.UtcNow;

        _audit.Record(AuditActions.LocationUpdated, nameof(Location), id,
            oldValue: before, newValue: new { location.Name, location.Description, location.IsActive });

        await _db.SaveChangesAsync(ct);

        return new LocationResponse(location.LocationId, location.Name, location.Description, location.IsActive, deviceCount);
    }
}
