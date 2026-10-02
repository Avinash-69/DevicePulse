using System.ComponentModel.DataAnnotations;
using DevicePulse.Api.Entities.Enums;
using DevicePulse.Api.Models.Common;

namespace DevicePulse.Api.Models;

public sealed record CreateDeviceRequest(
    [Required, MaxLength(50), RegularExpression("^[A-Za-z0-9_-]+$",
        ErrorMessage = "DeviceCode may contain only letters, digits, hyphens and underscores.")]
    string DeviceCode,

    [Required, MaxLength(200)] string DeviceName,
    [Range(1, int.MaxValue)] int DeviceTypeId,
    [Range(1, int.MaxValue)] int LocationId);

public sealed record UpdateDeviceRequest(
    [Required, MaxLength(200)] string DeviceName,
    [Range(1, int.MaxValue)] int DeviceTypeId,
    [Range(1, int.MaxValue)] int LocationId,
    LifecycleStatus LifecycleStatus,

    // <summary>
    // Base64 RowVersion from the GET that populated the edit form. Sent back so the API can
    // reject an edit based on a stale read instead of silently overwriting someone else's
    // change (Appendix D.1). Optional: omitting it is a last-write-wins update.
    // </summary>
    string? RowVersion);

public sealed record DeviceResponse(
    int DeviceId,
    string DeviceCode,
    string DeviceName,
    int DeviceTypeId,
    string DeviceTypeName,
    int LocationId,
    string LocationName,
    LifecycleStatus LifecycleStatus,
    ConnectivityStatus ConnectivityStatus,
    DateTime? LastSeenAt,
    bool HasApiKey,
    int OpenAlertCount,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string? RowVersion);

/// <summary>Filter/sort inputs for the device list. A record so model binding populates it from the query string.</summary>
public sealed record DeviceQuery : PagedQuery
{
    public string? Search { get; init; }
    public int? DeviceTypeId { get; init; }
    public int? LocationId { get; init; }
    public LifecycleStatus? LifecycleStatus { get; init; }
    public ConnectivityStatus? ConnectivityStatus { get; init; }

    /// <summary>One of: deviceName, deviceCode, lastSeenAt, createdAt. Unknown values fall back to deviceName.</summary>
    public string SortBy { get; init; } = "deviceName";
    public bool SortDescending { get; init; }
}

/// <summary>
/// Returned exactly once, when a key is issued. The plaintext is not stored and cannot be
/// retrieved again — losing it means issuing a new one.
/// </summary>
public sealed record DeviceApiKeyResponse(int DeviceId, string DeviceCode, string ApiKey, DateTime IssuedAt);

public sealed record RetireDeviceRequest([MaxLength(500)] string? Reason);

public sealed record DeviceTypeResponse(int DeviceTypeId, string Name, string? Description, bool IsActive, int DeviceCount);
public sealed record LocationResponse(int LocationId, string Name, string? Description, bool IsActive, int DeviceCount);

public sealed record CreateDeviceTypeRequest(
    [Required, MaxLength(100)] string Name,
    [MaxLength(500)] string? Description);

public sealed record UpdateDeviceTypeRequest(
    [Required, MaxLength(100)] string Name,
    [MaxLength(500)] string? Description,
    bool IsActive);

public sealed record CreateLocationRequest(
    [Required, MaxLength(150)] string Name,
    [MaxLength(500)] string? Description);

public sealed record UpdateLocationRequest(
    [Required, MaxLength(150)] string Name,
    [MaxLength(500)] string? Description,
    bool IsActive);
