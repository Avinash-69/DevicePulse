using DevicePulse.Api.Entities.Enums;

namespace DevicePulse.Api.Entities;

public class Device
{
    public int DeviceId { get; set; }
    public string DeviceCode { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;

    public int DeviceTypeId { get; set; }
    public DeviceType? DeviceType { get; set; }

    public int LocationId { get; set; }
    public Location? Location { get; set; }

    // Two separate fields, deliberately (Appendix C item 1).
    public LifecycleStatus LifecycleStatus { get; set; } = LifecycleStatus.Registered;
    public ConnectivityStatus ConnectivityStatus { get; set; } = ConnectivityStatus.Unknown;

    public DateTime? LastSeenAt { get; set; }

    /// <summary>
    /// Devices authenticate with their own credential, never with a human user's JWT
    /// (Appendix C item 3). Stored as a hash: the plaintext key is shown exactly once,
    /// at provisioning time, and cannot be recovered afterwards.
    /// </summary>
    public string? ApiKeyHash { get; set; }
    public DateTime? ApiKeyIssuedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    /// <summary>Optimistic concurrency token (Appendix D.1) — two admins editing the same device don't silently clobber each other.</summary>
    public byte[]? RowVersion { get; set; }

    public ICollection<Telemetry> Telemetries { get; set; } = new List<Telemetry>();
    public ICollection<Alert> Alerts { get; set; } = new List<Alert>();
}

public class DeviceType
{
    public int DeviceTypeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<Device> Devices { get; set; } = new List<Device>();
}

public class Location
{
    public int LocationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<Device> Devices { get; set; } = new List<Device>();
}
