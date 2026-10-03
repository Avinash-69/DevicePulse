using System.Net;
using System.Net.Http.Json;
using DevicePulse.Api.Authorization;
using DevicePulse.Api.Entities.Enums;
using DevicePulse.Api.Models;
using DevicePulse.Api.Models.Common;
using DevicePulse.Api.Services.Configuration;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DevicePulse.Tests.Integration;

/// <summary>
/// The two claims the master reference treats as central: the backend is the final security
/// authority (§4.4, §29, §59), and supported business behaviour is configurable at runtime
/// without a code change or a redeploy (§4.1, Appendix A.1).
///
/// Both are asserted over real HTTP, because a unit test of the handler cannot prove that the
/// attribute was actually applied to the endpoint.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuthorizationAndConfigurationTests
{
    private readonly DevicePulseApiFactory _factory;

    public AuthorizationAndConfigurationTests(DevicePulseApiFactory factory) => _factory = factory;

    private void SkipIfNoDatabase() => Assert.SkipWhen(!_factory.DatabaseAvailable, _factory.SkipReason ?? "No database.");

    private static readonly System.Text.Json.JsonSerializerOptions Json = DevicePulseApiFactory.Json;

    /// <summary>Creates a user in the named seeded role and returns a client authenticated as them.</summary>
    private async Task<HttpClient> CreateClientInRoleAsync(string roleName)
    {
        var admin = await _factory.CreateSuperAdminClientAsync();

        var roles = await admin.GetFromJsonAsync<List<RoleResponse>>("/api/v1/admin/roles", Json);
        var role = roles!.Single(r => r.Name == roleName);

        var email = $"{roleName.ToLowerInvariant()}-{Guid.NewGuid():N}@devicepulse.test";
        const string password = "RoleUser#2026aa";

        var created = await admin.PostAsJsonAsync("/api/v1/admin/users", new
        {
            name = $"{roleName} User",
            email,
            password,
            roleIds = new[] { role.RoleId }
        }, Json);

        created.StatusCode.Should().Be(HttpStatusCode.Created);

        return await _factory.CreateAuthenticatedClientAsync(email, password);
    }

    // ---------------------------------------------------------------- authorization

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Operator")]
    public async Task A_non_admin_cannot_register_a_device_however_they_call_the_API(string roleName)
    {
        SkipIfNoDatabase();

        var client = await CreateClientInRoleAsync(roleName);

        var response = await client.PostAsJsonAsync("/api/v1/devices", new
        {
            deviceCode = $"NOPE-{Guid.NewGuid():N}"[..20],
            deviceName = "Should not exist",
            deviceTypeId = 1,
            locationId = 1
        }, Json);

        // This is the scenario §29 spells out: the UI never showed them the button, they called
        // the endpoint directly anyway, and the backend refuses.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Operator")]
    public async Task A_non_admin_cannot_change_a_runtime_setting(string roleName)
    {
        SkipIfNoDatabase();

        var client = await CreateClientInRoleAsync(roleName);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/settings/{SettingKeys.OfflineTimeoutSeconds}",
            new { value = "999", changeReason = "Should be refused." }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Operator")]
    public async Task A_non_admin_cannot_read_the_user_list_or_the_audit_trail(string roleName)
    {
        SkipIfNoDatabase();

        var client = await CreateClientInRoleAsync(roleName);

        (await client.GetAsync("/api/v1/admin/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/v1/audit")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_viewer_cannot_resolve_an_alert_but_an_operator_can()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();

        // Produce a real alert to act on.
        var (deviceTypeId, locationId) = await _factory.EnsureReferenceDataAsync(admin);

        var device = await (await admin.PostAsJsonAsync("/api/v1/devices", new
        {
            deviceCode = $"ALRT-{Guid.NewGuid():N}"[..20],
            deviceName = "Alert Source",
            deviceTypeId,
            locationId
        }, Json)).Content.ReadFromJsonAsync<DeviceResponse>(Json);

        await admin.PostAsJsonAsync("/api/v1/telemetry/ingest", new
        {
            deviceId = device!.DeviceId,
            temperature = 80.0,
            battery = 50.0,
            signalStrength = -60,
            messageId = $"authz-{Guid.NewGuid():N}"
        }, Json);

        var alerts = await admin.GetFromJsonAsync<PagedResult<AlertResponse>>(
            $"/api/v1/alerts?deviceId={device.DeviceId}", Json);

        alerts!.Items.Should().NotBeEmpty();
        var alertId = alerts.Items[0].AlertId;

        var viewer = await CreateClientInRoleAsync("Viewer");
        var operatorClient = await CreateClientInRoleAsync("Operator");

        // A Viewer can see it but not act on it; an Operator can do both. That distinction is
        // the whole reason permissions are finer-grained than roles.
        (await viewer.GetAsync($"/api/v1/alerts/{alertId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await viewer.PostAsJsonAsync($"/api/v1/alerts/{alertId}/resolve", new { resolutionNote = "nope" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await operatorClient.PostAsJsonAsync($"/api/v1/alerts/{alertId}/resolve",
                new { resolutionNote = "Cleared the blocked air intake." }, Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Granting_a_permission_to_a_role_takes_effect_on_the_next_sign_in()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();

        // A brand-new role with a single permission, created entirely through the API — the
        // runtime RBAC administration story from Appendix A.3.
        var roleName = $"Custom-{Guid.NewGuid():N}"[..20];

        var role = await (await admin.PostAsJsonAsync("/api/v1/admin/roles", new
        {
            name = roleName,
            description = "Created at runtime by the test suite.",
            permissions = new[] { Permissions.DashboardView }
        }, Json)).Content.ReadFromJsonAsync<RoleResponse>(Json);

        role!.IsSystemRole.Should().BeFalse();
        role.Permissions.Should().BeEquivalentTo([Permissions.DashboardView]);

        var email = $"custom-{Guid.NewGuid():N}@devicepulse.test";
        const string password = "CustomRole#2026a";

        await admin.PostAsJsonAsync("/api/v1/admin/users", new
        {
            name = "Custom Role User",
            email,
            password,
            roleIds = new[] { role.RoleId }
        }, Json);

        var user = await _factory.CreateAuthenticatedClientAsync(email, password);

        (await user.GetAsync("/api/v1/dashboard/summary")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await user.GetAsync("/api/v1/devices")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Now add device.view at runtime, with no deployment.
        await admin.PutAsJsonAsync($"/api/v1/admin/roles/{role.RoleId}/permissions", new
        {
            permissions = new[] { Permissions.DashboardView, Permissions.DeviceView }
        }, Json);

        // The existing token still carries the old claim set — that is the deliberate trade-off
        // of baking permissions into the token, and why access tokens are short-lived.
        (await user.GetAsync("/api/v1/devices")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // A fresh sign-in picks up the new permission.
        var refreshed = await _factory.CreateAuthenticatedClientAsync(email, password);
        (await refreshed.GetAsync("/api/v1/devices")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_permission_key_outside_the_catalog_is_refused()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();

        var response = await admin.PostAsJsonAsync("/api/v1/admin/roles", new
        {
            name = $"Bogus-{Guid.NewGuid():N}"[..20],
            description = "Tries to invent a permission.",
            permissions = new[] { "devicepulse.become.god" }
        }, Json);

        // Accepting it would create a permission that looks granted in the UI but that no
        // policy will ever match (Appendix C item 4).
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("catalog");
    }

    [Fact]
    public async Task The_super_admin_role_cannot_be_stripped_of_permissions()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();

        var roles = await admin.GetFromJsonAsync<List<RoleResponse>>("/api/v1/admin/roles", Json);
        var superAdmin = roles!.Single(r => r.Name == SystemRoles.SuperAdmin);

        var response = await admin.PutAsJsonAsync($"/api/v1/admin/roles/{superAdmin.RoleId}/permissions", new
        {
            permissions = new[] { Permissions.DashboardView }
        }, Json);

        // Otherwise one edit could remove the ability to undo that edit.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_built_in_role_cannot_be_renamed_or_disabled()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();

        var roles = await admin.GetFromJsonAsync<List<RoleResponse>>("/api/v1/admin/roles", Json);
        var viewer = roles!.Single(r => r.Name == "Viewer");

        var renamed = await admin.PutAsJsonAsync($"/api/v1/admin/roles/{viewer.RoleId}", new
        {
            name = "Renamed Viewer",
            description = viewer.Description,
            isActive = true
        }, Json);

        // Viewer is referenced by name in code as the self-registration default; renaming it
        // would quietly break that lookup.
        renamed.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var disabled = await admin.PutAsJsonAsync($"/api/v1/admin/roles/{viewer.RoleId}", new
        {
            name = "Viewer",
            description = viewer.Description,
            isActive = false
        }, Json);

        disabled.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_last_super_admin_cannot_be_demoted_or_deactivated()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();

        var users = await admin.GetFromJsonAsync<PagedResult<UserResponse>>(
            "/api/v1/admin/users?pageSize=200", Json);

        var superAdmins = users!.Items.Where(u => u.Roles.Any(r => r.Name == SystemRoles.SuperAdmin)).ToList();
        superAdmins.Should().HaveCount(1, "the seeded Super Admin should be the only one in a fresh database");

        var only = superAdmins[0];

        var roles = await admin.GetFromJsonAsync<List<RoleResponse>>("/api/v1/admin/roles", Json);
        var viewerRoleId = roles!.Single(r => r.Name == "Viewer").RoleId;

        // Demoting the only Super Admin would leave nobody able to manage users, roles or
        // settings — with no recovery short of editing the database by hand, which §4.1 says
        // should never be necessary.
        var demote = await admin.PutAsJsonAsync($"/api/v1/admin/users/{only.UserId}", new
        {
            name = only.Name,
            email = only.Email,
            roleIds = new[] { viewerRoleId }
        }, Json);

        demote.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await demote.Content.ReadAsStringAsync()).Should().Contain("only active Super Admin");

        var deactivate = await admin.PatchAsJsonAsync(
            $"/api/v1/admin/users/{only.UserId}/status", new { isActive = false }, Json);

        deactivate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Deactivating_a_user_ends_their_sessions_immediately()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();

        var roles = await admin.GetFromJsonAsync<List<RoleResponse>>("/api/v1/admin/roles", Json);
        var viewerRoleId = roles!.Single(r => r.Name == "Viewer").RoleId;

        var email = $"deactivate-{Guid.NewGuid():N}@devicepulse.test";
        const string password = "Deactivate#2026a";

        var created = await (await admin.PostAsJsonAsync("/api/v1/admin/users", new
        {
            name = "To Be Deactivated",
            email,
            password,
            roleIds = new[] { viewerRoleId }
        }, Json)).Content.ReadFromJsonAsync<UserResponse>(Json);

        var anonymous = _factory.CreateClient();

        var login = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password }, Json);
        var auth = await login.Content.ReadFromJsonAsync<DevicePulseApiFactory.LoginResult>(Json);

        await admin.PatchAsJsonAsync($"/api/v1/admin/users/{created!.UserId}/status", new { isActive = false }, Json);

        // Leaving a valid refresh token behind would let a disabled account keep working until
        // its tokens happened to expire.
        var refresh = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = auth!.RefreshToken }, Json);

        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var relogin = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password }, Json);
        relogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---------------------------------------------------------------- runtime configuration

    [Fact]
    public async Task Changing_an_alert_rule_threshold_changes_behaviour_with_no_restart()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();

        var (deviceTypeId, locationId) = await _factory.EnsureReferenceDataAsync(admin);

        var device = await (await admin.PostAsJsonAsync("/api/v1/devices", new
        {
            deviceCode = $"CFG-{Guid.NewGuid():N}"[..20],
            deviceName = "Config Test Device",
            deviceTypeId,
            locationId
        }, Json)).Content.ReadFromJsonAsync<DeviceResponse>(Json);

        // A rule of its own, scoped to this test, with no cooldown so each reading is judged
        // on its own merits.
        var rule = await (await admin.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            name = $"Test threshold {Guid.NewGuid():N}"[..30],
            description = "Created by the runtime-configuration test.",
            metric = "Temperature",
            @operator = "GreaterThan",
            threshold = 30.0,
            severity = "High",
            isEnabled = true,
            cooldownSeconds = 0,
            deviceTypeId
        }, Json)).Content.ReadFromJsonAsync<AlertRuleResponse>(Json);

        async Task<int> IngestAsync(double temperature)
        {
            var response = await admin.PostAsJsonAsync("/api/v1/telemetry/ingest", new
            {
                deviceId = device!.DeviceId,
                temperature,
                battery = 90.0,
                signalStrength = -60,
                messageId = $"cfg-{Guid.NewGuid():N}"
            }, Json);

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var result = await response.Content.ReadFromJsonAsync<TelemetryIngestResponse>(Json);
            return result!.AlertsRaised;
        }

        // 35 °C is above the current threshold of 30, so the rule fires.
        (await IngestAsync(35)).Should().BeGreaterThan(0);

        // An administrator raises the threshold to 50 through the API. No code change, no
        // redeploy, no SQL console — this is the project's central claim (Appendix A.1).
        var updated = await admin.PutAsJsonAsync($"/api/v1/alert-rules/{rule!.AlertRuleId}", new
        {
            name = rule.Name,
            description = "Threshold raised during a heat wave.",
            metric = "Temperature",
            @operator = "GreaterThan",
            threshold = 50.0,
            severity = "High",
            cooldownSeconds = 0,
            deviceTypeId,
            rowVersion = rule.RowVersion
        }, Json);

        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        // The very same 35 °C reading is now unremarkable.
        (await IngestAsync(35)).Should().Be(0, "the new threshold must apply immediately");

        // And 55 °C still trips it.
        (await IngestAsync(55)).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Disabling_a_rule_stops_it_firing_without_deleting_it()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();

        var (deviceTypeId, locationId) = await _factory.EnsureReferenceDataAsync(admin);

        var device = await (await admin.PostAsJsonAsync("/api/v1/devices", new
        {
            deviceCode = $"DIS-{Guid.NewGuid():N}"[..20],
            deviceName = "Disable Test Device",
            deviceTypeId,
            locationId
        }, Json)).Content.ReadFromJsonAsync<DeviceResponse>(Json);

        var rule = await (await admin.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            name = $"Disable me {Guid.NewGuid():N}"[..28],
            metric = "Battery",
            @operator = "LessThan",
            threshold = 95.0,
            severity = "Medium",
            isEnabled = true,
            cooldownSeconds = 0,
            deviceTypeId
        }, Json)).Content.ReadFromJsonAsync<AlertRuleResponse>(Json);

        // Battery 50 trips the test's own rule (< 95) but not the seeded global "Low battery"
        // rule (< 15), so AlertsRaised reflects only the rule under test.
        async Task<int> IngestAsync() =>
            (await (await admin.PostAsJsonAsync("/api/v1/telemetry/ingest", new
            {
                deviceId = device!.DeviceId,
                temperature = 22.0,
                battery = 50.0,
                signalStrength = -60,
                messageId = $"dis-{Guid.NewGuid():N}"
            }, Json)).Content.ReadFromJsonAsync<TelemetryIngestResponse>(Json))!.AlertsRaised;

        (await IngestAsync()).Should().BeGreaterThan(0);

        await admin.PatchAsJsonAsync($"/api/v1/alert-rules/{rule!.AlertRuleId}/status", new { isEnabled = false }, Json);

        (await IngestAsync()).Should().Be(0);

        // Still there, still inspectable — which is why disabling is the operational choice the
        // UI steers toward over deleting.
        var after = await admin.GetFromJsonAsync<AlertRuleResponse>($"/api/v1/alert-rules/{rule.AlertRuleId}", Json);
        after!.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task A_setting_change_is_validated_versioned_audited_and_applied()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();

        var before = await admin.GetFromJsonAsync<SettingResponse>(
            $"/api/v1/settings/{SettingKeys.OfflineTimeoutSeconds}", Json);

        before!.ValueType.Should().Be(SettingValueType.Integer);
        before.MinValue.Should().NotBeNull();
        before.MaxValue.Should().NotBeNull();
        before.Unit.Should().Be("seconds");

        // Below the declared minimum — rejected by the backend using the setting's own metadata,
        // which is what Development Rule 6 is for.
        var tooLow = await admin.PutAsJsonAsync($"/api/v1/settings/{SettingKeys.OfflineTimeoutSeconds}",
            new { value = "1", changeReason = "Far too low." }, Json);

        tooLow.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Not a number at all.
        var notANumber = await admin.PutAsJsonAsync($"/api/v1/settings/{SettingKeys.OfflineTimeoutSeconds}",
            new { value = "soon", changeReason = "Not a number." }, Json);

        notANumber.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var newValue = (before.Value == "120" ? 180 : 120).ToString();

        var accepted = await admin.PutAsJsonAsync($"/api/v1/settings/{SettingKeys.OfflineTimeoutSeconds}",
            new { value = newValue, changeReason = "Tightened for faster offline detection." }, Json);

        accepted.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await accepted.Content.ReadFromJsonAsync<SettingResponse>(Json);
        after!.Value.Should().Be(newValue);
        after.Version.Should().Be(before.Version + 1);
        after.UpdatedBy.Should().NotBeNullOrWhiteSpace();

        // History, not a blind overwrite (§18).
        var history = await admin.GetFromJsonAsync<List<SettingHistoryResponse>>(
            $"/api/v1/settings/{SettingKeys.OfflineTimeoutSeconds}/history", Json);

        history.Should().NotBeNull().And.NotBeEmpty();

        var newest = history![0];
        newest.NewValue.Should().Be(newValue);
        newest.OldValue.Should().Be(before.Value);
        newest.ChangeReason.Should().Contain("faster offline detection");
        newest.ChangedBy.Should().NotBeNullOrWhiteSpace();

        // And audited (§17).
        var audit = await admin.GetFromJsonAsync<PagedResult<AuditLogResponse>>(
            "/api/v1/audit?entityType=SystemSetting&pageSize=20", Json);

        audit!.Items.Should().Contain(a =>
            a.Action == "Setting.Changed"
            && a.EntityId == SettingKeys.OfflineTimeoutSeconds
            && a.NewValue != null && a.NewValue.Contains(newValue));
    }

    [Fact]
    public async Task A_boolean_setting_is_normalised_to_a_canonical_form()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();

        var response = await admin.PutAsJsonAsync($"/api/v1/settings/{SettingKeys.AlertAutoResolveOfflineOnReconnect}",
            new { value = "TRUE", changeReason = "Mixed case on purpose." }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var setting = await response.Content.ReadFromJsonAsync<SettingResponse>(Json);

        // Stored canonically so the database never holds "TRUE", "true" and "1" for the same
        // boolean setting.
        setting!.Value.Should().Be("true");
    }

    [Fact]
    public async Task An_enum_setting_accepts_only_its_declared_values()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();

        var setting = await admin.GetFromJsonAsync<SettingResponse>(
            $"/api/v1/settings/{SettingKeys.TemperatureUnit}", Json);

        setting!.AllowedValues.Should().BeEquivalentTo(["Celsius", "Fahrenheit"]);

        var rejected = await admin.PutAsJsonAsync($"/api/v1/settings/{SettingKeys.TemperatureUnit}",
            new { value = "Kelvin", changeReason = "Not supported." }, Json);

        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Matched case-insensitively but stored in the catalog's own casing.
        var accepted = await admin.PutAsJsonAsync($"/api/v1/settings/{SettingKeys.TemperatureUnit}",
            new { value = "fahrenheit", changeReason = "Switching units." }, Json);

        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await accepted.Content.ReadFromJsonAsync<SettingResponse>(Json))!.Value.Should().Be("Fahrenheit");
    }

    [Fact]
    public async Task Re_saving_the_same_value_does_not_create_a_history_entry()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();
        var key = SettingKeys.DashboardTrendHours;

        var current = await admin.GetFromJsonAsync<SettingResponse>($"/api/v1/settings/{key}", Json);

        var response = await admin.PutAsJsonAsync($"/api/v1/settings/{key}",
            new { value = current!.Value, changeReason = "No actual change." }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await response.Content.ReadFromJsonAsync<SettingResponse>(Json);

        // A no-op edit must not manufacture a version, or the real changes get buried in noise.
        after!.Version.Should().Be(current.Version);
    }

    [Fact]
    public async Task An_unknown_setting_key_is_a_404()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();

        (await admin.GetAsync("/api/v1/settings/not.a.real.setting"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_alert_rule_vocabulary_matches_what_the_backend_will_accept()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();

        var vocabulary = await admin.GetFromJsonAsync<AlertRuleVocabularyResponse>(
            "/api/v1/alert-rules/vocabulary", Json);

        // Served from the enums themselves, so the rule editor cannot offer an option the API
        // would then reject (Appendix C item 5).
        vocabulary!.Metrics.Select(m => m.Value)
            .Should().BeEquivalentTo(Enum.GetNames<AlertMetric>());

        vocabulary.Operators.Should().HaveCount(Enum.GetValues<AlertOperator>().Length);
        vocabulary.Severities.Select(s => s.Value).Should().BeEquivalentTo(Enum.GetNames<AlertSeverity>());
    }

    [Fact]
    public async Task Alert_acknowledgement_and_resolution_follow_the_documented_lifecycle()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();

        var (deviceTypeId, locationId) = await _factory.EnsureReferenceDataAsync(admin);

        var device = await (await admin.PostAsJsonAsync("/api/v1/devices", new
        {
            deviceCode = $"LIFE-{Guid.NewGuid():N}"[..20],
            deviceName = "Lifecycle Device",
            deviceTypeId,
            locationId
        }, Json)).Content.ReadFromJsonAsync<DeviceResponse>(Json);

        await admin.PostAsJsonAsync("/api/v1/telemetry/ingest", new
        {
            deviceId = device!.DeviceId,
            temperature = 90.0,
            battery = 50.0,
            signalStrength = -60,
            messageId = $"life-{Guid.NewGuid():N}"
        }, Json);

        var alerts = await admin.GetFromJsonAsync<PagedResult<AlertResponse>>(
            $"/api/v1/alerts?deviceId={device.DeviceId}", Json);

        var alert = alerts!.Items.First();
        alert.Status.Should().Be(AlertStatus.Open);

        var acknowledged = await (await admin.PostAsync($"/api/v1/alerts/{alert.AlertId}/acknowledge", null))
            .Content.ReadFromJsonAsync<AlertResponse>(Json);

        acknowledged!.Status.Should().Be(AlertStatus.Acknowledged);
        acknowledged.AcknowledgedBy.Should().NotBeNullOrWhiteSpace();

        // Acknowledging twice is a conflict, not a silent no-op.
        (await admin.PostAsync($"/api/v1/alerts/{alert.AlertId}/acknowledge", null))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        var resolved = await (await admin.PostAsJsonAsync($"/api/v1/alerts/{alert.AlertId}/resolve",
                new { resolutionNote = "Replaced the faulty sensor." }, Json))
            .Content.ReadFromJsonAsync<AlertResponse>(Json);

        resolved!.Status.Should().Be(AlertStatus.Resolved);
        resolved.ResolvedBy.Should().NotBeNullOrWhiteSpace();
        resolved.ResolutionNote.Should().Be("Replaced the faulty sensor.");

        (await admin.PostAsJsonAsync($"/api/v1/alerts/{alert.AlertId}/resolve", new { resolutionNote = "again" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task An_alert_can_be_resolved_without_being_acknowledged_first()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();

        var (deviceTypeId, locationId) = await _factory.EnsureReferenceDataAsync(admin);

        var device = await (await admin.PostAsJsonAsync("/api/v1/devices", new
        {
            deviceCode = $"SKIP-{Guid.NewGuid():N}"[..20],
            deviceName = "Skip Ack Device",
            deviceTypeId,
            locationId
        }, Json)).Content.ReadFromJsonAsync<DeviceResponse>(Json);

        await admin.PostAsJsonAsync("/api/v1/telemetry/ingest", new
        {
            deviceId = device!.DeviceId,
            temperature = 95.0,
            battery = 50.0,
            signalStrength = -60,
            messageId = $"skip-{Guid.NewGuid():N}"
        }, Json);

        var alert = (await admin.GetFromJsonAsync<PagedResult<AlertResponse>>(
            $"/api/v1/alerts?deviceId={device.DeviceId}", Json))!.Items.First();

        // An operator who simply fixed the problem should not have to click through an
        // intermediate state to say so.
        var resolved = await admin.PostAsJsonAsync($"/api/v1/alerts/{alert.AlertId}/resolve",
            new { resolutionNote = "Fixed outright." }, Json);

        resolved.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_cooldown_suppresses_a_repeat_alert_from_the_same_rule()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();

        var (deviceTypeId, locationId) = await _factory.EnsureReferenceDataAsync(admin);

        var device = await (await admin.PostAsJsonAsync("/api/v1/devices", new
        {
            deviceCode = $"COOL-{Guid.NewGuid():N}"[..20],
            deviceName = "Cooldown Device",
            deviceTypeId,
            locationId
        }, Json)).Content.ReadFromJsonAsync<DeviceResponse>(Json);

        var ruleName = $"Cooldown {Guid.NewGuid():N}"[..28];

        await admin.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            name = ruleName,
            metric = "SignalStrength",
            @operator = "LessThan",
            threshold = -90.0,
            severity = "Low",
            isEnabled = true,
            cooldownSeconds = 3600,
            deviceTypeId
        }, Json);

        async Task<int> IngestAsync() =>
            (await (await admin.PostAsJsonAsync("/api/v1/telemetry/ingest", new
            {
                deviceId = device!.DeviceId,
                temperature = 22.0,
                battery = 90.0,
                signalStrength = -120,
                messageId = $"cool-{Guid.NewGuid():N}"
            }, Json)).Content.ReadFromJsonAsync<TelemetryIngestResponse>(Json))!.AlertsRaised;

        var firstRound = await IngestAsync();
        var secondRound = await IngestAsync();

        firstRound.Should().BeGreaterThan(0);

        // Without the cooldown, a device parked below its threshold and reporting every two
        // seconds would produce an alert every two seconds (§16).
        var alertsFromThisRule = await _factory.ExecuteDbAsync(db => db.Alerts
            .CountAsync(a => a.DeviceId == device!.DeviceId && a.AlertRule!.Name == ruleName));

        alertsFromThisRule.Should().Be(1, "the second reading fell inside the cooldown window");
        secondRound.Should().BeLessThan(firstRound);
    }

    [Fact]
    public async Task The_dashboard_summary_reports_coherent_totals()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();

        var summary = await admin.GetFromJsonAsync<DashboardSummaryResponse>("/api/v1/dashboard/summary", Json);

        summary.Should().NotBeNull();

        // The connectivity buckets must account for every non-retired device, or the dashboard
        // disagrees with the device list the operator is looking at.
        (summary!.Devices.Online + summary.Devices.Offline + summary.Devices.Unknown)
            .Should().Be(summary.Devices.Total);

        summary.DevicesByLocation.Sum(l => l.Total).Should().Be(summary.Devices.Total);
        summary.DevicesByType.Sum(t => t.Count).Should().Be(summary.Devices.Total);
        summary.TrendHours.Should().BeGreaterThan(0);
        summary.GeneratedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task The_audit_trail_is_read_only_over_HTTP()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();

        // An audit trail an administrator can edit is not an audit trail, so there is no write
        // endpoint at all — not even for a Super Admin.
        (await admin.PostAsJsonAsync("/api/v1/audit", new { action = "Forged.Entry" }, Json))
            .StatusCode.Should().BeOneOf(HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotFound);

        (await admin.DeleteAsync("/api/v1/audit/1"))
            .StatusCode.Should().BeOneOf(HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_audit_trail_never_records_a_password_or_a_token()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();

        var roles = await admin.GetFromJsonAsync<List<RoleResponse>>("/api/v1/admin/roles", Json);
        var viewerRoleId = roles!.Single(r => r.Name == "Viewer").RoleId;

        const string password = "SecretValue#2026a";
        var email = $"audit-{Guid.NewGuid():N}@devicepulse.test";

        var created = await (await admin.PostAsJsonAsync("/api/v1/admin/users", new
        {
            name = "Audit Subject",
            email,
            password,
            roleIds = new[] { viewerRoleId }
        }, Json)).Content.ReadFromJsonAsync<UserResponse>(Json);

        await admin.PostAsJsonAsync($"/api/v1/admin/users/{created!.UserId}/reset-password",
            new { newPassword = "AnotherSecret#2026a" }, Json);

        var audit = await admin.GetFromJsonAsync<PagedResult<AuditLogResponse>>(
            "/api/v1/audit?pageSize=200", Json);

        var serialised = System.Text.Json.JsonSerializer.Serialize(audit, Json);

        // Only the caller's chosen projection is ever serialised into an audit entry, never a
        // whole entity — which keeps secrets out structurally rather than by remembering to
        // exclude them each time (§17, Appendix D.5).
        serialised.Should().NotContain(password);
        serialised.Should().NotContain("AnotherSecret");
        serialised.Should().NotContain("PBKDF2");

        // The fact of the reset is still recorded.
        audit!.Items.Should().Contain(a => a.Action == "User.PasswordReset");
    }

    [Fact]
    public async Task Reference_data_cannot_be_deactivated_while_devices_still_use_it()
    {
        SkipIfNoDatabase();

        var admin = await _factory.CreateSuperAdminClientAsync();

        var type = await (await admin.PostAsJsonAsync("/api/v1/reference/device-types", new
        {
            name = $"Doomed Type {Guid.NewGuid():N}"[..30],
            description = "Will have a device attached."
        }, Json)).Content.ReadFromJsonAsync<DeviceTypeResponse>(Json);

        var (_, locationId) = await _factory.EnsureReferenceDataAsync(admin);

        await admin.PostAsJsonAsync("/api/v1/devices", new
        {
            deviceCode = $"REF-{Guid.NewGuid():N}"[..20],
            deviceName = "Attached Device",
            deviceTypeId = type!.DeviceTypeId,
            locationId
        }, Json);

        var deactivate = await admin.PutAsJsonAsync($"/api/v1/reference/device-types/{type.DeviceTypeId}", new
        {
            name = type.Name,
            description = type.Description,
            isActive = false
        }, Json);

        // An inactive type fails the reference-data check on every edit to those devices, so
        // deactivating it would break them for a reason the operator never chose.
        deactivate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await deactivate.Content.ReadAsStringAsync()).Should().Contain("Reassign");
    }
}
