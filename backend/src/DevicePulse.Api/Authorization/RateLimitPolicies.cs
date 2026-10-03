namespace DevicePulse.Api.Authorization;

/// <summary>
/// Named rate-limit policies (Appendix D.3).
///
/// Two different policies because the two surfaces have opposite shapes: a human signing in
/// should make a handful of requests a minute, while a thousand simulated devices reporting
/// every two seconds is thousands of requests a minute and entirely legitimate. One shared
/// limit would either leave the login endpoint wide open or throttle normal ingestion.
///
/// ASP.NET Core's built-in middleware is enough at this scale — no API gateway, consistent
/// with §42 and Development Rule 4.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>
    /// Applied to /api/v1/auth/*. Tight, and partitioned by client IP: this is the brute-force
    /// and credential-stuffing surface. Complements the per-account lockout in AuthService —
    /// lockout protects one account, this protects the endpoint from being swept across many.
    /// </summary>
    public const string Authentication = "auth";

    /// <summary>
    /// Applied to telemetry ingestion. Generous, because the simulator is supposed to be able
    /// to push hard (§36) — it exists to stop a runaway client from exhausting the connection
    /// pool, not to shape normal traffic.
    /// </summary>
    public const string Ingestion = "ingestion";
}
