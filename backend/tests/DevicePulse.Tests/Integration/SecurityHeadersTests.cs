using FluentAssertions;
using Xunit;

namespace DevicePulse.Tests.Integration;

/// <summary>
/// The security headers are applied by middleware to every response, which makes them easy to
/// get wrong in a way nothing notices: a Content-Security-Policy is enforced by the browser and
/// ignored by every HTTP client, so a policy that breaks a page still returns 200 to curl and to
/// these tests unless they assert on the header itself.
///
/// That is exactly what happened: the API's deny-all policy was applied to the Swagger UI too,
/// which blocked its stylesheet and both of its scripts and left a blank white page. These tests
/// pin down both halves of the split so the strict policy cannot creep back over Swagger, and
/// the permissive one cannot leak onto the API.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SecurityHeadersTests
{
    private readonly DevicePulseApiFactory _factory;

    public SecurityHeadersTests(DevicePulseApiFactory factory) => _factory = factory;

    private void SkipIfNoDatabase() => Assert.SkipWhen(!_factory.DatabaseAvailable, _factory.SkipReason ?? "No database.");

    private static string Csp(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Content-Security-Policy", out var values)
            ? string.Join(' ', values)
            : string.Empty;

    [Fact]
    public async Task An_api_response_denies_every_content_source()
    {
        SkipIfNoDatabase();

        var response = await _factory.CreateClient().GetAsync("/health/live");

        Csp(response).Should().Contain("default-src 'none'",
            "the API only ever returns JSON, so nothing legitimate needs to load");
    }

    [Fact]
    public async Task An_api_response_is_never_stored_in_a_shared_cache()
    {
        SkipIfNoDatabase();

        var response = await _factory.CreateClient().GetAsync("/health/live");

        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Theory]
    [InlineData("X-Content-Type-Options", "nosniff")]
    [InlineData("X-Frame-Options", "DENY")]
    [InlineData("Referrer-Policy", "no-referrer")]
    public async Task The_standard_hardening_headers_are_present(string header, string expected)
    {
        SkipIfNoDatabase();

        var response = await _factory.CreateClient().GetAsync("/health/live");

        response.Headers.GetValues(header).Should().Contain(expected);
    }

    [Fact]
    public async Task The_swagger_ui_is_allowed_to_load_its_own_scripts_and_styles()
    {
        SkipIfNoDatabase();

        // The regression this guards: under "default-src 'none'" the page is served as a 200
        // with correct HTML and renders completely blank, because the browser refuses the
        // stylesheet and both scripts it asks for.
        var response = await _factory.CreateClient().GetAsync("/swagger/index.html");
        response.EnsureSuccessStatusCode();

        var policy = Csp(response);

        policy.Should().NotContain("default-src 'none'",
            "that policy blocks the very assets the Swagger page is made of");
        policy.Should().Contain("script-src 'self'");
        policy.Should().Contain("style-src 'self'");
    }

    [Fact]
    public async Task The_swagger_document_and_its_assets_are_reachable()
    {
        SkipIfNoDatabase();

        var client = _factory.CreateClient();

        foreach (var path in new[] { "/swagger/v1/swagger.json", "/swagger/swagger-ui.css", "/swagger/swagger-ui-bundle.js" })
        {
            var response = await client.GetAsync(path);
            response.IsSuccessStatusCode.Should().BeTrue($"{path} is required for the UI to render");
        }
    }
}
