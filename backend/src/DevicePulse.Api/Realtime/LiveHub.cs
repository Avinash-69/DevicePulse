using DevicePulse.Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DevicePulse.Api.Realtime;

/// <summary>
/// The one push channel from the API to the dashboard.
///
/// Server-to-client only: the hub exposes no methods a client can call, so it adds no new way to
/// change anything. Every write still goes through a controller and its permission check.
///
/// What a connection receives is decided once, when it connects, from the permissions in its
/// token: each event family has its own group, and a connection joins only the groups its
/// permissions entitle it to. Dashboard viewers join both, because the dashboard summary they can
/// already fetch carries the same alert messages and device states.
/// </summary>
[Authorize]
public sealed class LiveHub : Hub
{
    public const string Path = "/api/v1/hubs/live";

    public override async Task OnConnectedAsync()
    {
        foreach (var group in GroupsFor(Context.User?.FindAll(AppClaimTypes.Permission).Select(c => c.Value) ?? []))
            await Groups.AddToGroupAsync(Context.ConnectionId, group);

        await base.OnConnectedAsync();
    }

    /// <summary>The groups a holder of these permissions may join. Pure, so it is unit-tested directly.</summary>
    public static IReadOnlyList<string> GroupsFor(IEnumerable<string> permissions)
    {
        var held = permissions.ToHashSet(StringComparer.Ordinal);
        var groups = new List<string>(2);

        if (held.Contains(Permissions.AlertView) || held.Contains(Permissions.DashboardView))
            groups.Add(LiveGroups.Alerts);

        if (held.Contains(Permissions.DeviceView) || held.Contains(Permissions.DashboardView))
            groups.Add(LiveGroups.Devices);

        return groups;
    }
}

public static class LiveGroups
{
    public const string Alerts = "alerts";
    public const string Devices = "devices";
}

/// <summary>Client method names, shared with the SPA's RealtimeService.</summary>
public static class LiveEvents
{
    public const string AlertChanged = "alertChanged";
    public const string DeviceStatusChanged = "deviceStatusChanged";
}
