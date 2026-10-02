namespace DevicePulse.Api.Middleware;

/// <summary>
/// Standard security response headers (Appendix D.3).
///
/// This is an API, not a site that serves HTML, so the set is deliberately small: the headers
/// that matter here are the ones that stop a browser from reinterpreting a JSON response as
/// something executable, and stop API responses from being cached where they should not be.
/// HSTS is handled separately by UseHsts, which already knows about the development exemption.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;

            // Stops a browser from MIME-sniffing a response into a type it was not sent as.
            headers["X-Content-Type-Options"] = "nosniff";

            // No part of this API is meant to be framed.
            headers["X-Frame-Options"] = "DENY";

            // Keeps the API origin out of Referer headers sent to third parties.
            headers["Referrer-Policy"] = "no-referrer";

            // A restrictive CSP is cheap here precisely because the API returns no markup:
            // nothing legitimate needs to load, so everything can be denied.
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";

            // Swagger serves its own HTML and assets, so it is exempted from the no-store rule
            // below; API responses themselves must not be written to a shared cache.
            if (!context.Request.Path.StartsWithSegments("/swagger"))
                headers["Cache-Control"] = "no-store, no-cache, must-revalidate";

            return Task.CompletedTask;
        });

        return _next(context);
    }
}
