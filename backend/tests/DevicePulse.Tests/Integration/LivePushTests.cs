using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DevicePulse.Api.Entities.Enums;
using DevicePulse.Api.Models;
using DevicePulse.Api.Realtime;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevicePulse.Tests.Integration;

/// <summary>
/// The live channel end to end: a real hub connection over the test server, fed by real saves.
///
/// The authenticated connection goes over WebSockets with the token in the query string, because
/// that is the path a browser takes and the one the API has to special-case.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LivePushTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private readonly DevicePulseApiFactory _factory;

    public LivePushTests(DevicePulseApiFactory factory) => _factory = factory;

    private void SkipIfNoDatabase() => Assert.SkipWhen(!_factory.DatabaseAvailable, _factory.SkipReason ?? "No database.");

    private static readonly System.Text.Json.JsonSerializerOptions Json = DevicePulseApiFactory.Json;

    private HubConnection BuildConnection(string accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, LiveHub.Path), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, ct) =>
                {
                    var uri = new UriBuilder(context.Uri) { Query = $"access_token={Uri.EscapeDataString(accessToken)}" };
                    return await _factory.Server.CreateWebSocketClient().ConnectAsync(uri.Uri, ct);
                };
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

    private static TaskCompletionSource<T> Expect<T>(HubConnection connection, string method, Func<T, bool> match)
    {
        var received = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.On<T>(method, payload =>
        {
            if (match(payload))
                received.TrySetResult(payload);
        });

        return received;
    }

    [Fact]
    public async Task A_connection_without_a_token_is_refused()
    {
        SkipIfNoDatabase();

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, LiveHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        var start = () => connection.StartAsync();

        (await start.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Raising_and_acknowledging_an_alert_and_coming_online_are_pushed()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();
        var device = await _factory.RegisterDeviceAsync(admin, "LIVE");

        // A rule of its own with no cooldown, so the reading below is certain to fire it. Critical,
        // so its alert can be told apart from any seeded rule the same reading also trips.
        var rule = await (await admin.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            name = $"Live push {Guid.NewGuid():N}"[..30],
            description = "Created by the live push test.",
            metric = "Temperature",
            @operator = "GreaterThan",
            threshold = 30.0,
            severity = "Critical",
            isEnabled = true,
            cooldownSeconds = 0,
            deviceTypeId = device.DeviceTypeId
        }, Json)).Content.ReadFromJsonAsync<AlertRuleResponse>(Json);

        await using var connection = BuildConnection(admin.DefaultRequestHeaders.Authorization!.Parameter!);

        var cameOnline = Expect<DeviceStatusChangedEvent>(connection, LiveEvents.DeviceStatusChanged,
            e => e.DeviceId == device.DeviceId && e.ConnectivityStatus == ConnectivityStatus.Online);

        var raised = Expect<AlertChangedEvent>(connection, LiveEvents.AlertChanged,
            e => e.DeviceId == device.DeviceId && e.Change == AlertChange.Raised && e.Severity == AlertSeverity.Critical);

        var acknowledged = Expect<AlertChangedEvent>(connection, LiveEvents.AlertChanged,
            e => e.DeviceId == device.DeviceId && e.Change == AlertChange.Acknowledged && e.Severity == AlertSeverity.Critical);

        await connection.StartAsync();

        try
        {
            var ingest = await admin.PostAsJsonAsync("/api/v1/telemetry/ingest", new
            {
                deviceId = device.DeviceId,
                temperature = 41.0,
                battery = 90.0,
                signalStrength = -60,
                messageId = $"live-{Guid.NewGuid():N}"
            }, Json);

            ingest.EnsureSuccessStatusCode();

            var online = await cameOnline.Task.WaitAsync(Wait);
            online.LifecycleStatus.Should().Be(LifecycleStatus.Active);

            var alert = await raised.Task.WaitAsync(Wait);
            alert.AlertId.Should().BeGreaterThan(0);
            alert.Status.Should().Be(AlertStatus.Open);

            (await admin.PostAsync($"/api/v1/alerts/{alert.AlertId}/acknowledge", null)).EnsureSuccessStatusCode();

            (await acknowledged.Task.WaitAsync(Wait)).AlertId.Should().Be(alert.AlertId);
        }
        finally
        {
            // The rule is shared state; left enabled it would fire for other tests' devices.
            await admin.PatchAsJsonAsync($"/api/v1/alert-rules/{rule!.AlertRuleId}/status", new { isEnabled = false }, Json);
        }
    }
}
