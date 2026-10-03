using System.Security.Claims;
using DevicePulse.Api.Authorization;

namespace DevicePulse.Api.Services.Security;

/// <summary>
/// Read-only view of who is making the current request. Services depend on this rather than on
/// IHttpContextAccessor directly, which keeps them unit-testable and keeps claim-parsing in one place.
/// </summary>
public interface ICurrentUser
{
    int? UserId { get; }
    string? Email { get; }
    string? Name { get; }
    bool IsAuthenticated { get; }
    IReadOnlySet<string> Permissions { get; }
    string? IpAddress { get; }
    string? CorrelationId { get; }
    bool HasPermission(string permission);
}

public sealed class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public CurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public int? UserId =>
        int.TryParse(Principal?.FindFirst(AppClaimTypes.UserId)?.Value, out var id) ? id : null;

    public string? Email => Principal?.FindFirst(ClaimTypes.Email)?.Value;

    public string? Name => Principal?.FindFirst(ClaimTypes.Name)?.Value;

    public IReadOnlySet<string> Permissions =>
        Principal?.FindAll(AppClaimTypes.Permission).Select(c => c.Value).ToHashSet(StringComparer.Ordinal)
        ?? new HashSet<string>();

    public string? IpAddress =>
        _accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? CorrelationId =>
        _accessor.HttpContext?.Items[Middleware.CorrelationIdMiddleware.ItemKey] as string;

    public bool HasPermission(string permission) => Permissions.Contains(permission);
}
