namespace DevicePulse.Api.Middleware;

/// <summary>
/// Accepts an inbound <c>X-Correlation-Id</c> or mints one, puts it on HttpContext.Items, pushes
/// it into the logging scope and echoes it on the response (Appendix D.4).
///
/// This runs first in the pipeline so that every later component — including the exception
/// handler — sees the same id. The practical payoff: a user reports "it failed", gives you the
/// id from the error response, and one log query returns every line from that request.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "CorrelationId";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);

        context.Items[ItemKey] = correlationId;
        context.TraceIdentifier = correlationId;

        // OnStarting, not a direct header write: the header has to be set before the response
        // begins, and a later component may start the response at any point.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (_logger.BeginScope(new Dictionary<string, object> { [ItemKey] = correlationId }))
        {
            await _next(context);
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var header))
        {
            var candidate = header.ToString();

            // An inbound value is echoed back into logs and responses, so it is length-capped
            // and charset-restricted — an unbounded client-controlled string in a log line is
            // how log injection happens.
            if (candidate.Length is > 0 and <= 64 && candidate.All(IsSafeCorrelationChar))
            {
                return candidate;
            }
        }

        return Guid.NewGuid().ToString("N");
    }

    private static bool IsSafeCorrelationChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_';
}
