using System.Net;
using System.Net.Http.Json;
using DevicePulse.Api.Entities.Enums;
using DevicePulse.Api.Models;
using DevicePulse.Api.Models.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DevicePulse.Tests.Integration;

/// <summary>
/// Device registration, the lifecycle rules, and the telemetry ingestion path end to end.
///
/// These are the tests that justify using a real SQL Server: idempotency depends on a filtered
/// unique index, retire-not-delete depends on real cascade behaviour, and the concurrency
/// checks depend on an actual rowversion column. None of that exists in a fake provider.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DeviceAndTelemetryTests
{
    private readonly DevicePulseApiFactory _factory;

    public DeviceAndTelemetryTests(DevicePulseApiFactory factory) => _factory = factory;

    private void SkipIfNoDatabase() => Assert.SkipWhen(!_factory.DatabaseAvailable, _factory.SkipReason ?? "No database.");

    private static readonly System.Text.Json.JsonSerializerOptions Json = DevicePulseApiFactory.Json;

    /// <summary>Creates the reference data a device needs, returning ids usable for registration.</summary>
    private async Task<(int DeviceTypeId, int LocationId)> EnsureReferenceDataAsync(HttpClient client)
    {
        var types = await client.GetFromJsonAsync<List<DeviceTypeResponse>>("/api/v1/reference/device-types", Json);
        var locations = await client.GetFromJsonAsync<List<LocationResponse>>("/api/v1/reference/locations", Json);

        var typeId = types is { Count: > 0 }
            ? types[0].DeviceTypeId
            : (await (await client.PostAsJsonAsync("/api/v1/reference/device-types",
                    new { name = $"Type {Guid.NewGuid():N}", description = "Created by the test suite." }, Json))
                .Content.ReadFromJsonAsync<DeviceTypeResponse>(Json))!.DeviceTypeId;

        var locationId = locations is { Count: > 0 }
            ? locations[0].LocationId
            : (await (await client.PostAsJsonAsync("/api/v1/reference/locations",
                    new { name = $"Location {Guid.NewGuid():N}", description = "Created by the test suite." }, Json))
                .Content.ReadFromJsonAsync<LocationResponse>(Json))!.LocationId;

        return (typeId, locationId);
    }

    private async Task<DeviceResponse> RegisterDeviceAsync(HttpClient client, string? code = null)
    {
        var (typeId, locationId) = await EnsureReferenceDataAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/devices", new
        {
            deviceCode = code ?? $"IT-{Guid.NewGuid():N}"[..20],
            deviceName = "Integration Test Device",
            deviceTypeId = typeId,
            locationId
        }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<DeviceResponse>(Json))!;
    }

    [Fact]
    public async Task A_new_device_starts_Registered_with_Unknown_connectivity()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();
        var device = await RegisterDeviceAsync(client);

        device.LifecycleStatus.Should().Be(LifecycleStatus.Registered);

        // Unknown, not Offline: we have never seen this device, which is different from having
        // seen it and lost it (Appendix C item 1).
        device.ConnectivityStatus.Should().Be(ConnectivityStatus.Unknown);
        device.LastSeenAt.Should().BeNull();
        device.HasApiKey.Should().BeFalse();
    }

    [Fact]
    public async Task A_duplicate_device_code_is_refused()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();
        var code = $"DUP-{Guid.NewGuid():N}"[..20];

        await RegisterDeviceAsync(client, code);

        var (typeId, locationId) = await EnsureReferenceDataAsync(client);

        var second = await client.PostAsJsonAsync("/api/v1/devices", new
        {
            deviceCode = code,
            deviceName = "Duplicate",
            deviceTypeId = typeId,
            locationId
        }, Json);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Registering_against_a_nonexistent_device_type_names_the_offending_field()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();
        var (_, locationId) = await EnsureReferenceDataAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/devices", new
        {
            deviceCode = $"BAD-{Guid.NewGuid():N}"[..20],
            deviceName = "Bad Reference",
            deviceTypeId = 999_999,
            locationId
        }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Validated in the service rather than left to the foreign key, so the caller learns
        // which field was wrong instead of getting an opaque 500.
        (await response.Content.ReadAsStringAsync()).Should().Contain("deviceTypeId");
    }

    [Fact]
    public async Task A_device_code_with_illegal_characters_is_rejected()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();
        var (typeId, locationId) = await EnsureReferenceDataAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/devices", new
        {
            deviceCode = "bad code/with slashes",
            deviceName = "Illegal Code",
            deviceTypeId = typeId,
            locationId
        }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Retiring_a_device_preserves_its_telemetry_and_closes_its_alerts()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();
        var device = await RegisterDeviceAsync(client);

        // A hot reading, so the device has both telemetry and an open alert to lose.
        await client.PostAsJsonAsync("/api/v1/telemetry/ingest", new
        {
            deviceId = device.DeviceId,
            temperature = 70.0,
            battery = 50.0,
            signalStrength = -60,
            messageId = $"retire-{Guid.NewGuid():N}"
        }, Json);

        var retire = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/devices/{device.DeviceId}")
        {
            Content = JsonContent.Create(new { reason = "Decommissioned during the test." }, options: Json)
        });

        retire.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await client.GetFromJsonAsync<DeviceResponse>($"/api/v1/devices/{device.DeviceId}", Json);
        after!.LifecycleStatus.Should().Be(LifecycleStatus.Retired);

        // The whole point of retire-over-delete (Appendix C item 2): the history survives.
        var telemetryCount = await _factory.ExecuteDbAsync(db =>
            db.Telemetry.CountAsync(t => t.DeviceId == device.DeviceId));

        telemetryCount.Should().BeGreaterThan(0, "a retired device must keep its readings");

        var openAlerts = await _factory.ExecuteDbAsync(db =>
            db.Alerts.CountAsync(a => a.DeviceId == device.DeviceId && a.Status != AlertStatus.Resolved));

        openAlerts.Should().Be(0, "nobody will act on an alert for hardware that is out of service");
    }

    [Fact]
    public async Task A_retired_device_is_hidden_from_the_default_list_but_findable_on_request()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();
        var device = await RegisterDeviceAsync(client);

        await client.DeleteAsync($"/api/v1/devices/{device.DeviceId}");

        var defaultList = await client.GetFromJsonAsync<PagedResult<DeviceResponse>>(
            "/api/v1/devices?pageSize=200", Json);

        defaultList!.Items.Should().NotContain(d => d.DeviceId == device.DeviceId);

        var retiredList = await client.GetFromJsonAsync<PagedResult<DeviceResponse>>(
            "/api/v1/devices?pageSize=200&lifecycleStatus=Retired", Json);

        retiredList!.Items.Should().Contain(d => d.DeviceId == device.DeviceId);
    }

    [Fact]
    public async Task A_retired_device_cannot_be_retired_twice()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();
        var device = await RegisterDeviceAsync(client);

        await client.DeleteAsync($"/api/v1/devices/{device.DeviceId}");

        (await client.DeleteAsync($"/api/v1/devices/{device.DeviceId}"))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_retired_device_no_longer_accepts_telemetry()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();
        var device = await RegisterDeviceAsync(client);

        await client.DeleteAsync($"/api/v1/devices/{device.DeviceId}");

        var response = await client.PostAsJsonAsync("/api/v1/telemetry/ingest", new
        {
            deviceId = device.DeviceId,
            temperature = 25.0,
            battery = 80.0,
            signalStrength = -60
        }, Json);

        // Otherwise a retired device would keep resurrecting itself in the dashboard.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_stale_rowVersion_is_rejected_instead_of_silently_overwriting()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();
        var device = await RegisterDeviceAsync(client);

        var staleRowVersion = device.RowVersion;
        staleRowVersion.Should().NotBeNullOrWhiteSpace("the API must hand out a concurrency token");

        var update = new
        {
            deviceName = "First Editor Wins",
            deviceTypeId = device.DeviceTypeId,
            locationId = device.LocationId,
            lifecycleStatus = "Active",
            rowVersion = staleRowVersion
        };

        // First edit succeeds and bumps the row version.
        (await client.PutAsJsonAsync($"/api/v1/devices/{device.DeviceId}", update, Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Second edit still carries the original token, i.e. it was made from a stale read.
        var second = await client.PutAsJsonAsync($"/api/v1/devices/{device.DeviceId}",
            update with { deviceName = "Second Editor Clobbers" }, Json);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict, "a stale edit must not overwrite a newer one");

        var final = await client.GetFromJsonAsync<DeviceResponse>($"/api/v1/devices/{device.DeviceId}", Json);
        final!.DeviceName.Should().Be("First Editor Wins");
    }

    [Fact]
    public async Task Ingesting_a_reading_brings_the_device_Online_and_sets_LastSeenAt()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();
        var device = await RegisterDeviceAsync(client);

        await client.PostAsJsonAsync("/api/v1/telemetry/ingest", new
        {
            deviceId = device.DeviceId,
            temperature = 22.5,
            battery = 95.0,
            signalStrength = -55,
            messageId = $"online-{Guid.NewGuid():N}"
        }, Json);

        var after = await client.GetFromJsonAsync<DeviceResponse>($"/api/v1/devices/{device.DeviceId}", Json);

        after!.ConnectivityStatus.Should().Be(ConnectivityStatus.Online);
        after.LastSeenAt.Should().NotBeNull();

        // A device that reports is in service, whatever its paperwork said.
        after.LifecycleStatus.Should().Be(LifecycleStatus.Active);
    }

    [Fact]
    public async Task The_same_messageId_twice_is_recognised_as_a_retry()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();
        var device = await RegisterDeviceAsync(client);

        var reading = new
        {
            deviceId = device.DeviceId,
            temperature = 26.0,
            battery = 90.0,
            signalStrength = -58,
            messageId = $"dedupe-{Guid.NewGuid():N}"
        };

        var first = await client.PostAsJsonAsync("/api/v1/telemetry/ingest", reading, Json);
        var retry = await client.PostAsJsonAsync("/api/v1/telemetry/ingest", reading, Json);

        var firstResult = await first.Content.ReadFromJsonAsync<TelemetryIngestResponse>(Json);
        var retryResult = await retry.Content.ReadFromJsonAsync<TelemetryIngestResponse>(Json);

        firstResult!.Duplicate.Should().BeFalse();

        // 200 and Duplicate=true, not an error: from the device's point of view the reading was
        // delivered, and telling it otherwise would make it retry forever (Appendix D.1).
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        retryResult!.Duplicate.Should().BeTrue();
        retryResult.TelemetryId.Should().Be(firstResult.TelemetryId);

        var stored = await _factory.ExecuteDbAsync(db =>
            db.Telemetry.CountAsync(t => t.DeviceId == device.DeviceId && t.MessageId == reading.messageId));

        stored.Should().Be(1, "a retry must not become a second row");
    }

    [Fact]
    public async Task A_reading_stamped_in_the_future_is_pulled_back_to_server_time()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();
        var device = await RegisterDeviceAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/telemetry/ingest", new
        {
            deviceId = device.DeviceId,
            temperature = 24.0,
            battery = 88.0,
            signalStrength = -60,

            // A device with a badly-set clock. Left alone, this reading would sort above
            // genuinely current data forever.
            recordedAt = DateTime.UtcNow.AddYears(5),
            messageId = $"future-{Guid.NewGuid():N}"
        }, Json);

        var result = await response.Content.ReadFromJsonAsync<TelemetryIngestResponse>(Json);

        result!.RecordedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(6));
    }

    [Fact]
    public async Task An_out_of_order_reading_does_not_drag_LastSeenAt_backwards()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();
        var device = await RegisterDeviceAsync(client);

        var now = DateTime.UtcNow;

        await client.PostAsJsonAsync("/api/v1/telemetry/ingest", new
        {
            deviceId = device.DeviceId,
            temperature = 24.0, battery = 88.0, signalStrength = -60,
            recordedAt = now,
            messageId = $"ooo-new-{Guid.NewGuid():N}"
        }, Json);

        // A buffered delivery arriving late. It must be stored, but it must not make the device
        // look staler than it is (Appendix D.4).
        await client.PostAsJsonAsync("/api/v1/telemetry/ingest", new
        {
            deviceId = device.DeviceId,
            temperature = 23.0, battery = 89.0, signalStrength = -61,
            recordedAt = now.AddHours(-2),
            messageId = $"ooo-old-{Guid.NewGuid():N}"
        }, Json);

        var after = await client.GetFromJsonAsync<DeviceResponse>($"/api/v1/devices/{device.DeviceId}", Json);

        after!.LastSeenAt.Should().BeCloseTo(now, TimeSpan.FromSeconds(5));

        var count = await _factory.ExecuteDbAsync(db => db.Telemetry.CountAsync(t => t.DeviceId == device.DeviceId));
        count.Should().Be(2, "the late reading is still stored, just not treated as the newest");
    }

    [Fact]
    public async Task Latest_telemetry_returns_no_content_for_a_device_that_never_reported()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();
        var device = await RegisterDeviceAsync(client);

        var response = await client.GetAsync($"/api/v1/devices/{device.DeviceId}/telemetry/latest");

        // A newly-registered device having no readings is a normal state, not a 404 that every
        // caller would have to special-case.
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Telemetry_history_for_an_unknown_device_is_a_404()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();

        (await client.GetAsync("/api/v1/devices/99999999/telemetry"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_implausible_reading_is_rejected_against_the_runtime_configured_bounds()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();
        var device = await RegisterDeviceAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/telemetry/ingest", new
        {
            deviceId = device.DeviceId,
            temperature = 400.0,
            battery = 50.0,
            signalStrength = -60
        }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("temperature");
    }

    [Fact]
    public async Task A_bulk_batch_is_accepted_deduplicated_and_partially_rejected_as_appropriate()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();
        var device = await RegisterDeviceAsync(client);

        var sharedMessageId = $"bulk-dupe-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/api/v1/telemetry/bulk", new
        {
            readings = new object[]
            {
                new { deviceId = device.DeviceId, temperature = 21.0, battery = 90.0, signalStrength = -60, messageId = $"bulk-{Guid.NewGuid():N}" },
                new { deviceId = device.DeviceId, temperature = 22.0, battery = 89.0, signalStrength = -61, messageId = sharedMessageId },

                // The same id again inside the one batch — must be caught without a round trip.
                new { deviceId = device.DeviceId, temperature = 22.0, battery = 89.0, signalStrength = -61, messageId = sharedMessageId },

                // A device that does not exist: one bad reading must not reject the good ones.
                new { deviceId = 99_999_999, temperature = 23.0, battery = 88.0, signalStrength = -62, messageId = $"bulk-{Guid.NewGuid():N}" }
            }
        }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<BulkTelemetryResponse>(Json);

        result!.Accepted.Should().Be(2);
        result.Duplicates.Should().Be(1);
        result.Rejected.Should().Be(1);
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task An_issued_device_key_can_ingest_and_only_for_its_own_device()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();
        var deviceA = await RegisterDeviceAsync(admin);
        var deviceB = await RegisterDeviceAsync(admin);

        var keyResponse = await admin.PostAsync($"/api/v1/devices/{deviceA.DeviceId}/api-key", null);
        keyResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var key = await keyResponse.Content.ReadFromJsonAsync<DeviceApiKeyResponse>(Json);
        key!.ApiKey.Should().StartWith("dpk_");

        var deviceClient = _factory.CreateClient();
        deviceClient.DefaultRequestHeaders.Add("X-Device-Key", key.ApiKey);

        // Its own reading, with no device id in the body — identity comes from the credential.
        var own = await deviceClient.PostAsJsonAsync("/api/v1/telemetry", new
        {
            temperature = 23.0,
            battery = 80.0,
            signalStrength = -60,
            messageId = $"devkey-{Guid.NewGuid():N}"
        }, Json);

        own.StatusCode.Should().Be(HttpStatusCode.OK);

        // Reporting for a different device must be refused, or one compromised key would let an
        // attacker fabricate readings across the whole fleet.
        var impersonation = await deviceClient.PostAsJsonAsync("/api/v1/telemetry/ingest", new
        {
            deviceId = deviceB.DeviceId,
            temperature = 23.0,
            battery = 80.0,
            signalStrength = -60
        }, Json);

        impersonation.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_device_key_cannot_reach_the_management_endpoints()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();
        var device = await RegisterDeviceAsync(admin);

        var key = await (await admin.PostAsync($"/api/v1/devices/{device.DeviceId}/api-key", null))
            .Content.ReadFromJsonAsync<DeviceApiKeyResponse>(Json);

        var deviceClient = _factory.CreateClient();
        deviceClient.DefaultRequestHeaders.Add("X-Device-Key", key!.ApiKey);

        // A device holds exactly one permission. It is not a user and must not be able to act
        // like one (Appendix C item 3).
        (await deviceClient.GetAsync("/api/v1/devices")).StatusCode
            .Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);

        (await deviceClient.GetAsync("/api/v1/admin/users")).StatusCode
            .Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_revoked_device_key_stops_working()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();
        var device = await RegisterDeviceAsync(admin);

        var key = await (await admin.PostAsync($"/api/v1/devices/{device.DeviceId}/api-key", null))
            .Content.ReadFromJsonAsync<DeviceApiKeyResponse>(Json);

        (await admin.DeleteAsync($"/api/v1/devices/{device.DeviceId}/api-key"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var deviceClient = _factory.CreateClient();
        deviceClient.DefaultRequestHeaders.Add("X-Device-Key", key!.ApiKey);

        var response = await deviceClient.PostAsJsonAsync("/api/v1/telemetry", new
        {
            temperature = 23.0, battery = 80.0, signalStrength = -60
        }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unrecognised_device_key_is_rejected()
    {
        SkipIfNoDatabase();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Key", "dpk_DP-9999_totally-made-up-key-value");

        var response = await client.PostAsJsonAsync("/api/v1/telemetry", new
        {
            temperature = 23.0, battery = 80.0, signalStrength = -60
        }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_device_list_is_paged_filtered_and_sorted_consistently()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();

        for (var i = 0; i < 3; i++)
            await RegisterDeviceAsync(client);

        var firstPage = await client.GetFromJsonAsync<PagedResult<DeviceResponse>>(
            "/api/v1/devices?page=1&pageSize=2&sortBy=deviceCode", Json);

        firstPage!.Items.Should().HaveCount(2);
        firstPage.Page.Should().Be(1);
        firstPage.PageSize.Should().Be(2);
        firstPage.TotalCount.Should().BeGreaterThanOrEqualTo(3);
        firstPage.TotalPages.Should().Be((int)Math.Ceiling(firstPage.TotalCount / 2.0));
        firstPage.HasPrevious.Should().BeFalse();
        firstPage.HasNext.Should().BeTrue();

        var secondPage = await client.GetFromJsonAsync<PagedResult<DeviceResponse>>(
            "/api/v1/devices?page=2&pageSize=2&sortBy=deviceCode", Json);

        // Deterministic ordering, so no row appears on two pages.
        secondPage!.Items.Select(d => d.DeviceId)
            .Should().NotIntersectWith(firstPage.Items.Select(d => d.DeviceId));
    }

    [Fact]
    public async Task An_oversized_pageSize_is_clamped_rather_than_rejected()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();

        var result = await client.GetFromJsonAsync<PagedResult<DeviceResponse>>(
            "/api/v1/devices?page=1&pageSize=100000", Json);

        // Clamped: the caller gets the maximum, but never gets to ask the database for everything.
        result!.PageSize.Should().BeLessThanOrEqualTo(200);
    }

    [Fact]
    public async Task An_unknown_sort_field_falls_back_to_a_stable_order_instead_of_failing()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();

        var response = await client.GetAsync("/api/v1/devices?sortBy=' OR 1=1--");

        // The sort column comes from a closed whitelist, so a hostile value is ignored rather
        // than interpolated into SQL.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
