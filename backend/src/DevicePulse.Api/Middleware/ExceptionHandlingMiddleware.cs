using System.Net;
using DevicePulse.Api.Data;
using DevicePulse.Api.Entities;
using DevicePulse.Api.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevicePulse.Api.Middleware;

/// <summary>
/// The single place exceptions become HTTP responses (§25).
///
/// Two deliberately different paths:
///   * <see cref="AppException"/> — an expected business outcome (404/409/400/403). Logged at
///     Warning, translated to its own status code, and NOT written to ServiceLogs, because
///     that table is a defect list and filling it with "device 7 not found" makes it useless.
///   * anything else — a bug. Logged at Error with the stack trace, persisted to ServiceLogs,
///     and returned as a bare 500 that reveals nothing about the internals.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    // DbContext arrives per-invocation rather than through the constructor: this middleware is a
    // singleton for the lifetime of the app, and capturing a scoped DbContext in it would be the
    // classic captive-dependency bug.
    public async Task InvokeAsync(HttpContext context, DevicePulseDbContext db)
    {
        try
        {
            await _next(context);
        }
        catch (AppException appEx)
        {
            _logger.LogWarning(
                "{ErrorCode} on {Method} {Path}: {Message}",
                appEx.ErrorCode, context.Request.Method, context.Request.Path, appEx.Message);

            var problem = BuildProblem(context, (int)appEx.StatusCode, appEx.ErrorCode, appEx.Message);

            if (appEx is Exceptions.ValidationException { Errors.Count: > 0 } validationEx)
                problem.Extensions["errors"] = validationEx.Errors;

            await WriteAsync(context, problem);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Reaching here means a service forgot to translate it; treat it as the 409 it is
            // rather than letting a concurrency clash surface as a 500.
            _logger.LogWarning(ex, "Concurrency conflict on {Method} {Path}",
                context.Request.Method, context.Request.Path);

            await WriteAsync(context, BuildProblem(
                context, (int)HttpStatusCode.Conflict, "ConcurrencyConflict",
                "The record was modified by someone else after you loaded it. Reload and try again."));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client hung up. Not an error, and there is nobody left to send a response to.
            _logger.LogInformation("Request cancelled by the client: {Method} {Path}",
                context.Request.Method, context.Request.Path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception on {Method} {Path}",
                context.Request.Method, context.Request.Path);

            await TryPersistServiceLog(context, db, ex);

            var detail = _environment.IsDevelopment()
                ? ex.Message
                : "An unexpected error occurred. Quote the traceId when reporting this.";

            await WriteAsync(context, BuildProblem(
                context, StatusCodes.Status500InternalServerError, "InternalServerError", detail));
        }
    }

    private async Task TryPersistServiceLog(HttpContext context, DevicePulseDbContext db, Exception ex)
    {
        try
        {
            // The failed request may have left tracked entities in a broken state; writing the
            // log through the same context would try to flush those too. Clearing first keeps
            // the log insert independent of whatever went wrong.
            db.ChangeTracker.Clear();

            db.ServiceLogs.Add(new ServiceLog
            {
                Timestamp = DateTime.UtcNow,
                Level = "Error",
                Message = Truncate(ex.Message, 2000) ?? string.Empty,
                ExceptionType = Truncate(ex.GetType().FullName, 200),
                StackTrace = ex.StackTrace,
                Source = Truncate(ex.TargetSite?.DeclaringType?.FullName, 300),
                RequestPath = Truncate(context.Request.Path.Value, 500),
                RequestMethod = context.Request.Method,
                CorrelationId = CorrelationId(context),
                StatusCode = StatusCodes.Status500InternalServerError
            });

            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception logEx)
        {
            // If the database is what is broken, the error handler must not also throw — the
            // client still deserves its 500 and the console log still has the original.
            _logger.LogError(logEx, "Could not persist the ServiceLog entry for this failure.");
        }
    }

    /// <summary>RFC 7807 ProblemDetails, the one error shape every endpoint returns (Appendix D.1).</summary>
    private static ProblemDetails BuildProblem(HttpContext context, int status, string title, string detail) => new()
    {
        Type = $"https://httpstatuses.io/{status}",
        Title = title,
        Status = status,
        Detail = detail,
        Instance = context.Request.Path,
        Extensions = { ["traceId"] = CorrelationId(context) }
    };

    private static async Task WriteAsync(HttpContext context, ProblemDetails problem)
    {
        if (context.Response.HasStarted)
            return;

        context.Response.Clear();
        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;

        // The content type is passed to WriteAsJsonAsync rather than assigned beforehand:
        // WriteAsJsonAsync sets "application/json" itself and would overwrite the assignment,
        // quietly breaking the RFC 7807 content type the error contract promises (Appendix D.1).
        await context.Response.WriteAsJsonAsync(
            problem, options: null, contentType: "application/problem+json", CancellationToken.None);
    }

    private static string CorrelationId(HttpContext context) =>
        context.Items[CorrelationIdMiddleware.ItemKey] as string ?? context.TraceIdentifier;

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
