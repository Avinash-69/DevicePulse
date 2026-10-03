using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DevicePulse.Tests.Integration;

[Collection(ApiCollection.Name)]
public sealed class AuthenticationTests
{
    private readonly DevicePulseApiFactory _factory;

    public AuthenticationTests(DevicePulseApiFactory factory) => _factory = factory;

    private void SkipIfNoDatabase() => Assert.SkipWhen(!_factory.DatabaseAvailable, _factory.SkipReason ?? "No database.");

    [Fact]
    public async Task The_seeded_super_admin_can_sign_in_and_receives_every_permission()
    {
        SkipIfNoDatabase();

        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = DevicePulseApiFactory.SuperAdminEmail,
            password = DevicePulseApiFactory.SuperAdminPassword
        }, DevicePulseApiFactory.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var auth = await response.Content.ReadFromJsonAsync<DevicePulseApiFactory.LoginResult>(DevicePulseApiFactory.Json);

        auth!.AccessToken.Should().NotBeNullOrWhiteSpace();
        auth.RefreshToken.Should().NotBeNullOrWhiteSpace();
        auth.User.Roles.Should().Contain("SuperAdmin");

        auth.User.Permissions.Should().BeEquivalentTo(
            DevicePulse.Api.Authorization.Permissions.All.Select(p => p.Key),
            "the Super Admin role is seeded with the whole catalog");
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_address_give_the_same_answer()
    {
        SkipIfNoDatabase();

        var client = _factory.CreateClient();

        var wrongPassword = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = DevicePulseApiFactory.SuperAdminEmail,
            password = "definitely-not-the-password"
        }, DevicePulseApiFactory.Json);

        var unknownUser = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "nobody-here@devicepulse.test",
            password = "definitely-not-the-password"
        }, DevicePulseApiFactory.Json);

        wrongPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknownUser.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Identical responses, so the login form cannot be used to discover which addresses
        // have accounts.
        var first = await wrongPassword.Content.ReadAsStringAsync();
        var second = await unknownUser.Content.ReadAsStringAsync();

        ExtractDetail(first).Should().Be(ExtractDetail(second));
    }

    [Fact]
    public async Task An_unauthenticated_request_to_a_protected_endpoint_is_rejected()
    {
        SkipIfNoDatabase();

        var response = await _factory.CreateClient().GetAsync("/api/v1/devices");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_garbage_bearer_token_is_rejected()
    {
        SkipIfNoDatabase();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "not.a.real.token");

        var response = await client.GetAsync("/api/v1/devices");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Registration_grants_only_the_read_only_role()
    {
        SkipIfNoDatabase();

        var client = _factory.CreateClient();
        var email = $"selfreg-{Guid.NewGuid():N}@devicepulse.test";

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            name = "Self Registered",
            email,
            password = "SelfService#2026"
        }, DevicePulseApiFactory.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var auth = await response.Content.ReadFromJsonAsync<DevicePulseApiFactory.LoginResult>(DevicePulseApiFactory.Json);

        // A public endpoint that could mint an administrator would make the whole RBAC model
        // decorative, so this is the assertion that matters most in this file.
        auth!.User.Roles.Should().BeEquivalentTo(["Viewer"]);
        auth.User.Permissions.Should().NotContain(DevicePulse.Api.Authorization.Permissions.UserCreate);
        auth.User.Permissions.Should().NotContain(DevicePulse.Api.Authorization.Permissions.SettingsManage);
    }

    [Fact]
    public async Task Registration_rejects_a_weak_password()
    {
        SkipIfNoDatabase();

        var response = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new
        {
            name = "Weak Password",
            email = $"weak-{Guid.NewGuid():N}@devicepulse.test",
            password = "abc"
        }, DevicePulseApiFactory.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Password");
    }

    [Fact]
    public async Task Registration_refuses_a_duplicate_email_address()
    {
        SkipIfNoDatabase();

        var client = _factory.CreateClient();
        var email = $"dupe-{Guid.NewGuid():N}@devicepulse.test";

        var body = new { name = "First", email, password = "FirstUser#2026" };

        (await client.PostAsJsonAsync("/api/v1/auth/register", body, DevicePulseApiFactory.Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.PostAsJsonAsync("/api/v1/auth/register", body, DevicePulseApiFactory.Json);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_refresh_token_can_be_exchanged_once_and_is_then_revoked()
    {
        SkipIfNoDatabase();

        var client = _factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = DevicePulseApiFactory.SuperAdminEmail,
            password = DevicePulseApiFactory.SuperAdminPassword
        }, DevicePulseApiFactory.Json);

        var auth = await login.Content.ReadFromJsonAsync<DevicePulseApiFactory.LoginResult>(DevicePulseApiFactory.Json);

        var first = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = auth!.RefreshToken }, DevicePulseApiFactory.Json);

        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // The second attempt with the same token must fail: rotation is what limits a captured
        // refresh token to a single use.
        var replay = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = auth.RefreshToken }, DevicePulseApiFactory.Json);

        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Replaying_a_revoked_refresh_token_revokes_the_whole_family()
    {
        SkipIfNoDatabase();

        var client = _factory.CreateClient();
        var email = $"replay-{Guid.NewGuid():N}@devicepulse.test";

        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { name = "Replay Victim", email, password = "Replay#2026aa" }, DevicePulseApiFactory.Json);

        var auth = await register.Content.ReadFromJsonAsync<DevicePulseApiFactory.LoginResult>(DevicePulseApiFactory.Json);

        // Rotate once, so the original is revoked and a successor exists.
        var rotated = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = auth!.RefreshToken }, DevicePulseApiFactory.Json);

        var successor = await rotated.Content.ReadFromJsonAsync<DevicePulseApiFactory.LoginResult>(DevicePulseApiFactory.Json);

        // Replaying the revoked original is treated as a possible theft, so everything the user
        // holds is invalidated — including the successor that was still perfectly valid.
        await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = auth.RefreshToken }, DevicePulseApiFactory.Json);

        var successorAfterReplay = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = successor!.RefreshToken }, DevicePulseApiFactory.Json);

        successorAfterReplay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logging_out_revokes_the_refresh_token()
    {
        SkipIfNoDatabase();

        var client = _factory.CreateClient();
        var email = $"logout-{Guid.NewGuid():N}@devicepulse.test";

        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { name = "Logout User", email, password = "Logout#2026aa" }, DevicePulseApiFactory.Json);

        var auth = await register.Content.ReadFromJsonAsync<DevicePulseApiFactory.LoginResult>(DevicePulseApiFactory.Json);

        var logout = await client.PostAsJsonAsync("/api/v1/auth/logout",
            new { refreshToken = auth!.RefreshToken }, DevicePulseApiFactory.Json);

        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterLogout = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = auth.RefreshToken }, DevicePulseApiFactory.Json);

        afterLogout.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logging_out_twice_is_not_an_error()
    {
        SkipIfNoDatabase();

        var client = _factory.CreateClient();

        // The caller wanted the session gone and it is gone. Reporting a failure would force
        // every client to handle a non-problem.
        var response = await client.PostAsJsonAsync("/api/v1/auth/logout",
            new { refreshToken = "a-token-that-never-existed" }, DevicePulseApiFactory.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_and_a_correct_password_then_still_fails()
    {
        SkipIfNoDatabase();

        var client = _factory.CreateClient();
        var email = $"lockout-{Guid.NewGuid():N}@devicepulse.test";
        const string password = "Lockout#2026aa";

        await client.PostAsJsonAsync("/api/v1/auth/register",
            new { name = "Lockout User", email, password }, DevicePulseApiFactory.Json);

        // The default policy is five failures.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await client.PostAsJsonAsync("/api/v1/auth/login",
                new { email, password = "wrong-password" }, DevicePulseApiFactory.Json);
        }

        var afterLockout = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email, password }, DevicePulseApiFactory.Json);

        afterLockout.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The lockout is disclosed, unlike a bad password: the user needs to know that waiting
        // will help, and an attacker who caused it already knows.
        (await afterLockout.Content.ReadAsStringAsync()).Should().Contain("locked");

        var lockedUntil = await _factory.ExecuteDbAsync(db => db.Users
            .Where(u => u.Email == email)
            .Select(u => u.LockedOutUntil)
            .FirstAsync());

        lockedUntil.Should().NotBeNull().And.BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task A_password_is_never_stored_in_recoverable_form()
    {
        SkipIfNoDatabase();

        var client = _factory.CreateClient();
        var email = $"hash-{Guid.NewGuid():N}@devicepulse.test";
        const string password = "Hashing#2026aa";

        await client.PostAsJsonAsync("/api/v1/auth/register",
            new { name = "Hash Check", email, password }, DevicePulseApiFactory.Json);

        var storedHash = await _factory.ExecuteDbAsync(db => db.Users
            .Where(u => u.Email == email)
            .Select(u => u.PasswordHash)
            .FirstAsync());

        storedHash.Should().NotContain(password);
        storedHash.Should().StartWith("PBKDF2-SHA256.");
    }

    [Fact]
    public async Task Changing_a_password_ends_every_existing_session()
    {
        SkipIfNoDatabase();

        var anonymous = _factory.CreateClient();
        var email = $"pwchange-{Guid.NewGuid():N}@devicepulse.test";

        var register = await anonymous.PostAsJsonAsync("/api/v1/auth/register",
            new { name = "Password Changer", email, password = "Original#2026a" }, DevicePulseApiFactory.Json);

        var auth = await register.Content.ReadFromJsonAsync<DevicePulseApiFactory.LoginResult>(DevicePulseApiFactory.Json);

        var authed = _factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new("Bearer", auth!.AccessToken);

        var change = await authed.PostAsJsonAsync("/api/v1/auth/change-password", new
        {
            currentPassword = "Original#2026a",
            newPassword = "Replacement#2026a"
        }, DevicePulseApiFactory.Json);

        change.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // A password change usually means the old one is considered compromised, so leaving a
        // live refresh token behind would defeat the point.
        var oldRefresh = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = auth.RefreshToken }, DevicePulseApiFactory.Json);

        oldRefresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var newLogin = await anonymous.PostAsJsonAsync("/api/v1/auth/login",
            new { email, password = "Replacement#2026a" }, DevicePulseApiFactory.Json);

        newLogin.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Changing_a_password_requires_the_current_one()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/auth/change-password", new
        {
            currentPassword = "not-the-current-password",
            newPassword = "Replacement#2026a"
        }, DevicePulseApiFactory.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Every_error_response_carries_a_trace_id_and_the_problem_json_content_type()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();
        var response = await client.GetAsync("/api/v1/devices/99999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("traceId");

        // The trace id is echoed as a header too, which is what makes it quotable in a bug
        // report (Appendix D.4).
        response.Headers.Should().ContainKey("X-Correlation-Id");
    }

    [Fact]
    public async Task An_inbound_correlation_id_is_honoured_and_echoed_back()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();
        const string correlationId = "my-own-trace-id-123";

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/devices");
        request.Headers.Add("X-Correlation-Id", correlationId);

        var response = await client.SendAsync(request);

        response.Headers.GetValues("X-Correlation-Id").Should().Contain(correlationId);
    }

    [Fact]
    public async Task A_hostile_correlation_id_is_replaced_rather_than_echoed()
    {
        SkipIfNoDatabase();

        var client = await _factory.CreateSuperAdminClientAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/devices");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "bad id with spaces and <script>");

        var response = await client.SendAsync(request);

        // An unbounded client-controlled value flowing into log lines is how log injection
        // happens, so anything outside the allowed charset is discarded.
        response.Headers.GetValues("X-Correlation-Id").Single()
            .Should().NotContain("<script>").And.MatchRegex("^[A-Za-z0-9_-]+$");
    }

    [Fact]
    public async Task The_health_endpoints_are_reachable_without_authentication()
    {
        SkipIfNoDatabase();

        var client = _factory.CreateClient();

        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static string ExtractDetail(string problemJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(problemJson);
        return document.RootElement.GetProperty("detail").GetString() ?? string.Empty;
    }
}
