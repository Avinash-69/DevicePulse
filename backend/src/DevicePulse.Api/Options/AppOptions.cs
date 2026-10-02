using System.ComponentModel.DataAnnotations;

namespace DevicePulse.Api.Options;

/// <summary>
/// Token-issuance settings. Validated at startup with ValidateOnStart (Appendix D.2) so a
/// missing signing key fails the process at boot, loudly, instead of failing the first login
/// at 3am. The key itself never lives in appsettings.json — it comes from user-secrets in
/// development and from the environment/secret store elsewhere.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required(AllowEmptyStrings = false)]
    public string Issuer { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string Audience { get; set; } = string.Empty;

    /// <summary>HMAC-SHA256 signing key. 32+ bytes, because a shorter key weakens the signature.</summary>
    [Required(AllowEmptyStrings = false)]
    [MinLength(32, ErrorMessage = "Jwt:SigningKey must be at least 32 characters.")]
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Deliberately short: permissions are baked into the token, so this bounds how stale they can be.</summary>
    [Range(1, 1440)]
    public int AccessTokenMinutes { get; set; } = 15;

    [Range(1, 90)]
    public int RefreshTokenDays { get; set; } = 7;
}

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    [Range(6, 128)]
    public int PasswordMinLength { get; set; } = 10;

    public bool PasswordRequireUppercase { get; set; } = true;
    public bool PasswordRequireLowercase { get; set; } = true;
    public bool PasswordRequireDigit { get; set; } = true;
    public bool PasswordRequireNonAlphanumeric { get; set; } = true;

    /// <summary>Failed logins before the account locks. Blunts credential stuffing (Appendix D.3).</summary>
    [Range(1, 50)]
    public int MaxFailedLoginAttempts { get; set; } = 5;

    [Range(1, 1440)]
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>Explicit allow-list. Never AllowAnyOrigin once the SPA and API are on different origins.</summary>
    public string[] AllowedCorsOrigins { get; set; } = [];

    /// <summary>
    /// Requests per minute per client IP allowed against /api/v1/auth/*. Deliberately low:
    /// this is the credential-stuffing surface. Configurable rather than hardcoded so the
    /// integration suite can raise it — a test run makes far more login calls in a minute
    /// than any human, and a fixed limit would make the suite fail for the wrong reason.
    /// </summary>
    [Range(1, 100_000)]
    public int AuthRequestsPerMinute { get; set; } = 10;

    /// <summary>
    /// Requests per minute per device (or per IP) allowed against telemetry ingestion.
    /// Generous, because the simulator is meant to be able to push hard (§36) — this exists to
    /// stop a runaway client exhausting the connection pool, not to shape normal traffic.
    /// </summary>
    [Range(1, 10_000_000)]
    public int IngestionRequestsPerMinute { get; set; } = 3_000;
}

public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    [Required, EmailAddress]
    public string SuperAdminEmail { get; set; } = "superadmin@devicepulse.local";

    [Required]
    public string SuperAdminName { get; set; } = "Super Admin";

    /// <summary>
    /// Development convenience only. In any non-development environment this must come from the
    /// environment/secret store, and the seeder refuses to run with a default value there.
    /// </summary>
    public string SuperAdminPassword { get; set; } = string.Empty;

    /// <summary>Whether to create sample device types, locations, devices and telemetry. Off outside development.</summary>
    public bool SeedSampleData { get; set; }
}
