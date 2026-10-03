namespace DevicePulse.Api.Entities;

public class Telemetry
{
    public long TelemetryId { get; set; }
    public int DeviceId { get; set; }
    public Device? Device { get; set; }

    public double Temperature { get; set; }
    public double Battery { get; set; }
    public int SignalStrength { get; set; }

    /// <summary>When the device says it took the reading. Supplied by the device, not the server.</summary>
    public DateTime RecordedAt { get; set; }

    /// <summary>When the server actually persisted it. Diverges from RecordedAt for buffered or delayed deliveries.</summary>
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional client-supplied id used to deduplicate retries (Appendix D.1). A device on a
    /// flaky connection will re-POST a reading it already delivered; without this, the retry
    /// becomes a second row and can double-fire an alert rule.
    /// </summary>
    public string? MessageId { get; set; }
}
