using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;

namespace DevicePulse.Api.Realtime;

public interface ILivePublisher
{
    /// <summary>Queues committed changes for delivery. Returns immediately and never throws.</summary>
    void Publish(IReadOnlyList<AlertChangedEvent> alerts, IReadOnlyList<DeviceStatusChangedEvent> devices);
}

/// <summary>
/// Delivers committed changes to the hub's groups, off the request path.
///
/// Queued rather than sent inline because SignalR applies backpressure per connection: one
/// dashboard on a slow link would otherwise hold up the ingestion request whose save triggered
/// the push, and ingestion latency is the number this API is measured on.
///
/// The queue is bounded and drops the oldest event when full. By the time an event is queued the
/// change is already in the database, and every client re-fetches when it reconnects, so a lost
/// push costs staleness, not correctness; an unbounded queue behind a stuck client would cost
/// memory instead.
/// </summary>
public sealed class LiveEventDispatcher : BackgroundService, ILivePublisher
{
    private const int Capacity = 10_000;

    private readonly IHubContext<LiveHub> _hub;
    private readonly ILogger<LiveEventDispatcher> _logger;

    private readonly Channel<(string Group, string Method, object Payload)> _queue =
        Channel.CreateBounded<(string, string, object)>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    public LiveEventDispatcher(IHubContext<LiveHub> hub, ILogger<LiveEventDispatcher> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public void Publish(IReadOnlyList<AlertChangedEvent> alerts, IReadOnlyList<DeviceStatusChangedEvent> devices)
    {
        foreach (var alert in alerts)
            _queue.Writer.TryWrite((LiveGroups.Alerts, LiveEvents.AlertChanged, alert));

        foreach (var device in devices)
            _queue.Writer.TryWrite((LiveGroups.Devices, LiveEvents.DeviceStatusChanged, device));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var (group, method, payload) in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await _hub.Clients.Group(group).SendAsync(method, payload, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One failed send must not stop delivery of everything after it.
                    _logger.LogWarning(ex, "Pushing {Method} to live clients failed.", method);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; anything still queued is re-fetched by clients when they reconnect.
        }
    }
}
