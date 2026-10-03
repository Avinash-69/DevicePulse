using DevicePulse.Api.Authorization;
using DevicePulse.Api.Data;
using DevicePulse.Api.Entities;
using DevicePulse.Api.Entities.Enums;
using DevicePulse.Api.Realtime;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DevicePulse.Tests.Unit;

/// <summary>
/// Which saved changes become live events. What matters here is the change-tracking logic —
/// transitions only, nothing for a failed or empty save — so the in-memory provider is enough.
/// Delivery over a real socket is covered by the integration suite.
/// </summary>
public sealed class LiveChangeInterceptorTests
{
    private sealed class RecordingPublisher : ILivePublisher
    {
        public List<AlertChangedEvent> Alerts { get; } = [];
        public List<DeviceStatusChangedEvent> Devices { get; } = [];

        public void Publish(IReadOnlyList<AlertChangedEvent> alerts, IReadOnlyList<DeviceStatusChangedEvent> devices)
        {
            Alerts.AddRange(alerts);
            Devices.AddRange(devices);
        }
    }

    private readonly RecordingPublisher _publisher = new();
    private readonly string _databaseName = Guid.NewGuid().ToString("N");

    private DevicePulseDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DevicePulseDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .AddInterceptors(new LiveChangeInterceptor(_publisher))
            .Options);

    private async Task<Device> SeedDeviceAsync(ConnectivityStatus connectivity = ConnectivityStatus.Online)
    {
        await using var db = CreateContext();

        var device = new Device
        {
            DeviceCode = "LIVE-1",
            DeviceName = "Freezer 1",
            DeviceTypeId = 1,
            LocationId = 1,
            LifecycleStatus = LifecycleStatus.Active,
            ConnectivityStatus = connectivity
        };

        db.Devices.Add(device);
        await db.SaveChangesAsync();

        _publisher.Devices.Clear();
        return device;
    }

    private static Alert NewAlert(int deviceId) => new()
    {
        DeviceId = deviceId,
        Metric = AlertMetric.Temperature,
        Message = "Freezer 1 is at 41 °C.",
        Severity = AlertSeverity.Critical,
        Status = AlertStatus.Open,
        TriggeredValue = 41,
        Threshold = 30
    };

    [Fact]
    public async Task A_raised_alert_is_published_with_the_id_the_database_assigned()
    {
        var device = await SeedDeviceAsync();

        await using var db = CreateContext();
        var alert = NewAlert(device.DeviceId);
        db.Alerts.Add(alert);
        await db.SaveChangesAsync();

        var published = _publisher.Alerts.Should().ContainSingle().Subject;
        published.AlertId.Should().Be(alert.AlertId).And.BeGreaterThan(0);
        published.Change.Should().Be(AlertChange.Raised);
        published.Severity.Should().Be(AlertSeverity.Critical);
        published.DeviceId.Should().Be(device.DeviceId);
    }

    [Fact]
    public async Task Acknowledging_and_resolving_are_published_as_such()
    {
        var device = await SeedDeviceAsync();

        await using var db = CreateContext();
        var alert = NewAlert(device.DeviceId);
        db.Alerts.Add(alert);
        await db.SaveChangesAsync();
        _publisher.Alerts.Clear();

        alert.Status = AlertStatus.Acknowledged;
        await db.SaveChangesAsync();

        alert.Status = AlertStatus.Resolved;
        await db.SaveChangesAsync();

        _publisher.Alerts.Select(a => a.Change).Should().Equal(AlertChange.Acknowledged, AlertChange.Resolved);
    }

    [Fact]
    public async Task An_alert_edit_that_leaves_the_status_alone_is_not_published()
    {
        var device = await SeedDeviceAsync();

        await using var db = CreateContext();
        var alert = NewAlert(device.DeviceId);
        db.Alerts.Add(alert);
        await db.SaveChangesAsync();
        _publisher.Alerts.Clear();

        alert.ResolutionNote = "Still investigating.";
        await db.SaveChangesAsync();

        _publisher.Alerts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_connectivity_transition_is_published()
    {
        var device = await SeedDeviceAsync(ConnectivityStatus.Online);

        await using var db = CreateContext();
        var tracked = await db.Devices.SingleAsync(d => d.DeviceId == device.DeviceId);
        tracked.ConnectivityStatus = ConnectivityStatus.Offline;
        await db.SaveChangesAsync();

        var published = _publisher.Devices.Should().ContainSingle().Subject;
        published.ConnectivityStatus.Should().Be(ConnectivityStatus.Offline);
        published.DeviceCode.Should().Be("LIVE-1");
    }

    [Fact]
    public async Task A_reading_that_only_moves_LastSeenAt_is_not_published()
    {
        // The case that matters under load: an online device reporting every few seconds must
        // not push a message to every dashboard on every reading.
        var device = await SeedDeviceAsync(ConnectivityStatus.Online);

        await using var db = CreateContext();
        var tracked = await db.Devices.SingleAsync(d => d.DeviceId == device.DeviceId);
        tracked.LastSeenAt = DateTime.UtcNow;
        tracked.ConnectivityStatus = ConnectivityStatus.Online;
        await db.SaveChangesAsync();

        _publisher.Devices.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_save_publishes_nothing_and_does_not_leak_into_the_next_one()
    {
        var device = await SeedDeviceAsync();

        await using var db = CreateContext();
        db.Alerts.Add(NewAlert(device.DeviceId));

        // A duplicate key makes the in-memory provider reject the save after the interceptor
        // has captured the pending alert.
        db.Devices.Add(new Device { DeviceId = device.DeviceId, DeviceCode = "DUP", DeviceName = "Dup" });

        var save = () => db.SaveChangesAsync();
        await save.Should().ThrowAsync<Exception>();

        _publisher.Alerts.Should().BeEmpty();

        db.ChangeTracker.Clear();
        await db.SaveChangesAsync();

        _publisher.Alerts.Should().BeEmpty();
    }
}

public sealed class LiveHubGroupTests
{
    [Fact]
    public void Each_permission_joins_only_its_own_group()
    {
        LiveHub.GroupsFor([Permissions.AlertView]).Should().Equal(LiveGroups.Alerts);
        LiveHub.GroupsFor([Permissions.DeviceView]).Should().Equal(LiveGroups.Devices);
    }

    [Fact]
    public void Dashboard_viewers_join_both_groups()
    {
        LiveHub.GroupsFor([Permissions.DashboardView]).Should().Equal(LiveGroups.Alerts, LiveGroups.Devices);
    }

    [Fact]
    public void A_user_with_neither_permission_receives_nothing()
    {
        LiveHub.GroupsFor([Permissions.SettingsView, Permissions.AuditView]).Should().BeEmpty();
    }
}
