using System.Net;

namespace DevicePulse.Api.Exceptions;

/// <summary>
/// Base for *expected* failures — a missing device, a duplicate code, an invalid threshold.
/// These carry the status code they should surface as, so the global handler can translate
/// them without a type-switch and without anything leaking from the internals.
/// Anything that is *not* an AppException is, by definition, a bug.
/// </summary>
public abstract class AppException : Exception
{
    protected AppException(string message, HttpStatusCode statusCode) : base(message)
        => StatusCode = statusCode;

    public HttpStatusCode StatusCode { get; }

    /// <summary>Short machine-readable discriminator surfaced as ProblemDetails.title.</summary>
    public abstract string ErrorCode { get; }
}

public sealed class NotFoundException : AppException
{
    public NotFoundException(string message) : base(message, HttpStatusCode.NotFound) { }

    public NotFoundException(string entity, object key)
        : base($"{entity} with id '{key}' was not found.", HttpStatusCode.NotFound) { }

    public override string ErrorCode => "NotFound";
}

public sealed class ConflictException : AppException
{
    public ConflictException(string message) : base(message, HttpStatusCode.Conflict) { }
    public override string ErrorCode => "Conflict";
}

/// <summary>A request that is well-formed but asks for something the business rules forbid.</summary>
public sealed class ValidationException : AppException
{
    public ValidationException(string message) : base(message, HttpStatusCode.BadRequest)
        => Errors = new Dictionary<string, string[]>();

    public ValidationException(IDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.", HttpStatusCode.BadRequest)
        => Errors = errors;

    public IDictionary<string, string[]> Errors { get; }
    public override string ErrorCode => "ValidationFailed";
}

/// <summary>Bad credentials, locked account, expired or revoked token.</summary>
public sealed class AuthenticationException : AppException
{
    public AuthenticationException(string message) : base(message, HttpStatusCode.Unauthorized) { }
    public override string ErrorCode => "AuthenticationFailed";
}

/// <summary>Authenticated, but not permitted. Distinct from AuthenticationException on purpose: 403, not 401.</summary>
public sealed class ForbiddenException : AppException
{
    public ForbiddenException(string message) : base(message, HttpStatusCode.Forbidden) { }
    public override string ErrorCode => "Forbidden";
}

/// <summary>Raised when an optimistic-concurrency token no longer matches (Appendix D.1).</summary>
public sealed class ConcurrencyException : AppException
{
    public ConcurrencyException(string message) : base(message, HttpStatusCode.Conflict) { }
    public override string ErrorCode => "ConcurrencyConflict";
}

public sealed class TooManyRequestsException : AppException
{
    public TooManyRequestsException(string message) : base(message, HttpStatusCode.TooManyRequests) { }
    public override string ErrorCode => "TooManyRequests";
}
