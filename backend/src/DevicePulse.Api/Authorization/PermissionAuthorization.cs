using Microsoft.AspNetCore.Authorization;

namespace DevicePulse.Api.Authorization;

/// <summary>Claim types DevicePulse issues into its own access tokens.</summary>
public static class AppClaimTypes
{
    public const string Permission = "permission";
    public const string UserId = "uid";
}

public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(string permission) => Permission = permission;
    public string Permission { get; }
}

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        // Permissions are flattened into the access token at login, so the common path costs
        // no database round trip. The trade-off: a permission change takes effect on the user's
        // next token refresh rather than instantly. That is why access tokens are short-lived
        // (see JwtOptions.AccessTokenMinutes) — the window is bounded and deliberate.
        var hasPermission = context.User.Claims.Any(
            c => c.Type == AppClaimTypes.Permission && c.Value == requirement.Permission);

        if (hasPermission)
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Supplies a policy on demand for any "permission:xyz" name, so a new permission needs no
/// matching AddPolicy call at startup. Falls back to the default provider for built-in policies.
/// </summary>
public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    public const string Prefix = "permission:";

    private readonly DefaultAuthorizationPolicyProvider _fallback;

    public PermissionPolicyProvider(Microsoft.Extensions.Options.IOptions<AuthorizationOptions> options)
        => _fallback = new DefaultAuthorizationPolicyProvider(options);

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return _fallback.GetPolicyAsync(policyName);

        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(policyName[Prefix.Length..]))
            .Build();

        return Task.FromResult<AuthorizationPolicy?>(policy);
    }
}

/// <summary>
/// Declarative permission check on a controller or action:
/// <c>[HasPermission(Permissions.DeviceCreate)]</c>.
/// This is the enforcement point referenced throughout the master reference — the frontend
/// hiding a button is UX, this is the control (§4.4, §29).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class HasPermissionAttribute : AuthorizeAttribute
{
    public HasPermissionAttribute(string permission)
        => Policy = $"{PermissionPolicyProvider.Prefix}{permission}";
}
