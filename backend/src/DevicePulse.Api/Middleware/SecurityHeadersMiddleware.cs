namespace DevicePulse.Api.Middleware;

/// <summary>
/// Standard security response headers (Appendix D.3).
///
/// This is an API, not a site that serves HTML, so the set is deliberately small: the headers
/// that matter here are the ones that stop a browser from reinterpreting a JSON response as
/// something executable, and stop API responses from being cached where they should not be.
/// HSTS is handled separately by UseHsts, which already knows about the development exemption.
/// </summary>
/// <remarks>
/// The Swagger UI is the one exception, and it has to be made explicitly. It is real HTML that
/// loads its own stylesheet and two large scripts, so the API's <c>default-src 'none'</c> policy
/// blocks every one of them and the page renders blank &#8212; served correctly with a 200, and
/// empty in the browser. The symptom is invisible to anything that is not a browser, because a
/// Content-Security-Policy is enforced by the client and ignored by tools like curl.
/// </remarks>
public sealed class SecurityHeadersMiddleware
{
    /// <summary>
    /// What the Swagger UI needs in order to render: its own assets, plus inline styles and
    /// scripts, which swagger-ui injects as it builds the page. Scoped to Development only, so
    /// the API itself is never served under a policy this permissive.
    /// </summary>
    private const string SwaggerContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self' data:; " +
        "connect-src 'self'; " +
        "frame-ancestors 'none'";

    /// <summary>Denies everything, which is correct for a response that is only ever JSON.</summary>
    private const string ApiContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";

    private readonly RequestDelegate _next;
    private readonly bool _isDevelopment;

    public SecurityHeadersMiddleware(RequestDelegate next, IWebHostEnvironment environment)
    {
        _next = next;
        _isDevelopment = environment.IsDevelopment();
    }

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

            // Swagger serves its own HTML and assets, so it is exempted both from the deny-all
            // CSP and from the no-store rule; API responses themselves must not be written to a
            // shared cache. The exemption is Development-only and path-scoped, because Swagger
            // is only mapped there.
            var isSwagger = _isDevelopment && context.Request.Path.StartsWithSegments("/swagger");

            // A restrictive CSP is cheap on the API itself precisely because it returns no
            // markup: nothing legitimate needs to load, so everything can be denied.
            headers["Content-Security-Policy"] =
                isSwagger ? SwaggerContentSecurityPolicy : ApiContentSecurityPolicy;

            if (!isSwagger)
                headers["Cache-Control"] = "no-store, no-cache, must-revalidate";

            return Task.CompletedTask;
        });

        return _next(context);
    }
}
