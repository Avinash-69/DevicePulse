using DevicePulse.Api.Exceptions;
using DevicePulse.Api.Options;
using DevicePulse.Api.Services.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DevicePulse.Tests.Unit;

public sealed class PasswordHasherTests
{
    private static PasswordHasher Create(SecurityOptions? options = null) =>
        new(Options.Create(options ?? new SecurityOptions()));

    [Fact]
    public void A_hash_verifies_against_its_own_password()
    {
        var hasher = Create();
        var hash = hasher.Hash("Correct#Horse9");

        hasher.Verify("Correct#Horse9", hash).Should().BeTrue();
    }

    [Fact]
    public void A_hash_does_not_verify_against_a_different_password()
    {
        var hasher = Create();
        var hash = hasher.Hash("Correct#Horse9");

        hasher.Verify("correct#horse9", hash).Should().BeFalse("verification must be case-sensitive");
        hasher.Verify("Correct#Horse8", hash).Should().BeFalse();
        hasher.Verify("", hash).Should().BeFalse();
    }

    [Fact]
    public void The_same_password_hashes_differently_every_time()
    {
        // Each hash carries its own random salt. Identical hashes for identical passwords would
        // let an attacker see which users share a password straight from the table.
        var hasher = Create();

        var first = hasher.Hash("Correct#Horse9");
        var second = hasher.Hash("Correct#Horse9");

        first.Should().NotBe(second);
        hasher.Verify("Correct#Horse9", first).Should().BeTrue();
        hasher.Verify("Correct#Horse9", second).Should().BeTrue();
    }

    [Fact]
    public void The_stored_hash_embeds_its_algorithm_and_iteration_count()
    {
        // Embedded so the iteration count can be raised later without invalidating every
        // existing hash — old hashes keep verifying with the count they were created under.
        var parts = Create().Hash("Correct#Horse9").Split('.');

        parts.Should().HaveCount(4);
        parts[0].Should().Be("PBKDF2-SHA256");
        int.Parse(parts[1]).Should().BeGreaterThanOrEqualTo(100_000);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-real-hash")]
    [InlineData("PBKDF2-SHA256.notanumber.c2FsdA==.aGFzaA==")]
    [InlineData("PBKDF2-SHA256.1000.!!!notbase64!!!.aGFzaA==")]
    [InlineData("OTHER-ALGO.1000.c2FsdA==.aGFzaA==")]
    public void A_malformed_stored_hash_returns_false_rather_than_throwing(string storedHash)
    {
        // A corrupt row must fail the login, not take down the endpoint with an exception.
        Create().Verify("Correct#Horse9", storedHash).Should().BeFalse();
    }

    [Theory]
    [InlineData("short", "at least")]
    [InlineData("alllowercase1!", "uppercase")]
    [InlineData("ALLUPPERCASE1!", "lowercase")]
    [InlineData("NoDigitsHere!!", "digit")]
    [InlineData("NoSymbols12345", "non-alphanumeric")]
    public void The_policy_rejects_a_weak_password_and_says_why(string password, string expectedReason)
    {
        var act = () => Create().ValidatePolicy(password);

        act.Should().Throw<ValidationException>()
            .Which.Errors["password"][0].Should().Contain(expectedReason);
    }

    [Fact]
    public void The_policy_accepts_a_password_that_meets_every_requirement()
    {
        var act = () => Create().ValidatePolicy("Correct#Horse9");
        act.Should().NotThrow();
    }

    [Fact]
    public void The_policy_is_driven_by_configuration_not_hardcoded()
    {
        var relaxed = new SecurityOptions
        {
            PasswordMinLength = 4,
            PasswordRequireUppercase = false,
            PasswordRequireDigit = false,
            PasswordRequireNonAlphanumeric = false
        };

        var act = () => Create(relaxed).ValidatePolicy("abcd");
        act.Should().NotThrow();
    }

    [Fact]
    public void All_policy_failures_are_reported_together()
    {
        // One round trip telling the user everything that is wrong beats five rejections that
        // each reveal one more requirement.
        var act = () => Create().ValidatePolicy("abc");

        act.Should().Throw<ValidationException>()
            .Which.Errors["password"][0].Should().ContainAll("at least", "uppercase", "digit", "non-alphanumeric");
    }
}

public sealed class DeviceApiKeyServiceTests
{
    private readonly DeviceApiKeyService _service = new();

    [Fact]
    public void A_generated_key_hashes_to_the_stored_hash()
    {
        var (key, hash) = _service.Generate("DP-0001");

        _service.ComputeHash(key).Should().Be(hash);
    }

    [Fact]
    public void A_generated_key_embeds_its_device_code_for_traceability()
    {
        // So a key found in a log or a config file can be traced to its device without
        // reversing the hash.
        var (key, _) = _service.Generate("DP-0042");

        key.Should().StartWith("dpk_DP-0042_");
    }

    [Fact]
    public void Two_keys_for_the_same_device_are_different()
    {
        var (first, _) = _service.Generate("DP-0001");
        var (second, _) = _service.Generate("DP-0001");

        first.Should().NotBe(second, "re-issuing a key must rotate it, not reproduce it");
    }

    [Fact]
    public void A_key_is_url_safe_so_it_survives_headers_and_config_files()
    {
        var (key, _) = _service.Generate("DP-0001");

        key.Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }

    [Fact]
    public void A_key_carries_enough_entropy_to_resist_guessing()
    {
        var (key, _) = _service.Generate("DP-1");

        // 32 random bytes, base64url-encoded, is roughly 43 characters before the prefix.
        key.Length.Should().BeGreaterThan(40);
    }
}
