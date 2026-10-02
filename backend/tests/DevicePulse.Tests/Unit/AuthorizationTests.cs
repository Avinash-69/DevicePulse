using System.Security.Claims;
using DevicePulse.Api.Authorization;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Xunit;

namespace DevicePulse.Tests.Unit;

/// <summary>
/// Authorization is the backend's final word on what a caller may do (§4.4, §59), so the
/// handler and the policy provider are tested directly rather than only through the API.
/// </summary>
public sealed class PermissionAuthorizationHandlerTests
{
    private static AuthorizationHandlerContext ContextFor(string requiredPermission, params string[] heldPermissions)
    {
        var claims = heldPermissions.Select(p => new Claim(AppClaimTypes.Permission, p)).ToList();
        claims.Add(new Claim(AppClaimTypes.UserId, "1"));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestScheme"));

        return new AuthorizationHandlerContext(
            [new PermissionRequirement(requiredPermission)], principal, resource: null);
    }

    [Fact]
    public async Task Access_is_granted_when_the_principal_holds_the_permission()
    {
        var context = ContextFor(Permissions.DeviceCreate, Permissions.DeviceView, Permissions.DeviceCreate);

        await new PermissionAuthorizationHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Access_is_refused_when_the_principal_lacks_the_permission()
    {
        // The case the master reference keeps returning to: the UI hid the delete button, the
        // caller invoked the endpoint anyway, and the backend has to say no.
        var context = ContextFor(Permissions.DeviceRetire, Permissions.DeviceView, Permissions.TelemetryView);

        await new PermissionAuthorizationHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Access_is_refused_when_the_principal_holds_no_permissions_at_all()
    {
        var context = ContextFor(Permissions.DeviceView);

        await new PermissionAuthorizationHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Permission_matching_is_exact_not_a_prefix_match()
    {
        // "device.view" must not satisfy "device.viewer" or vice versa — a prefix match would
        // quietly widen every permission as the catalog grows.
        var context = ContextFor("device.view", "device.viewer");

        await new PermissionAuthorizationHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Permission_matching_is_case_sensitive()
    {
        var context = ContextFor("device.view", "Device.View");

        await new PermissionAuthorizationHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }
}

public sealed class PermissionPolicyProviderTests
{
    private static PermissionPolicyProvider Create() =>
        new(Options.Create(new AuthorizationOptions()));

    [Fact]
    public async Task A_permission_policy_is_produced_on_demand_without_being_registered()
    {
        // This is what lets a new permission ship without a matching AddPolicy call at startup.
        var policy = await Create().GetPolicyAsync($"{PermissionPolicyProvider.Prefix}{Permissions.SettingsManage}");

        policy.Should().NotBeNull();
        policy!.Requirements.OfType<PermissionRequirement>().Single()
            .Permission.Should().Be(Permissions.SettingsManage);
    }

    [Fact]
    public async Task A_permission_policy_also_requires_an_authenticated_user()
    {
        var policy = await Create().GetPolicyAsync($"{PermissionPolicyProvider.Prefix}{Permissions.DeviceView}");

        policy!.Requirements.Should().Contain(r =>
            r.GetType().Name == "DenyAnonymousAuthorizationRequirement");
    }

    [Fact]
    public async Task A_non_permission_policy_name_falls_through_to_the_default_provider()
    {
        var policy = await Create().GetPolicyAsync("SomeOtherPolicy");

        policy.Should().BeNull("an unregistered non-permission policy has no definition");
    }

    [Fact]
    public void The_attribute_builds_the_policy_name_the_provider_expects()
    {
        new HasPermissionAttribute(Permissions.AuditView).Policy
            .Should().Be($"{PermissionPolicyProvider.Prefix}{Permissions.AuditView}");
    }
}

public sealed class PermissionCatalogTests
{
    [Fact]
    public void Every_permission_constant_appears_in_the_catalog()
    {
        // The catalog drives both database seeding and the admin UI. A constant that exists in
        // code but is missing from the list would be enforceable yet unassignable — a permission
        // nobody could ever be granted.
        var constants = typeof(Permissions)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        var catalogued = Permissions.All.Select(p => p.Key).ToHashSet();

        constants.Should().OnlyContain(c => catalogued.Contains(c));
        constants.Should().HaveCount(catalogued.Count);
    }

    [Fact]
    public void Permission_keys_are_unique()
    {
        Permissions.All.Select(p => p.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_permission_has_a_name_a_category_and_a_description()
    {
        // These are what the admin UI renders. A blank one produces an unlabelled checkbox that
        // an administrator has to guess the meaning of.
        Permissions.All.Should().OnlyContain(p =>
            !string.IsNullOrWhiteSpace(p.Name)
            && !string.IsNullOrWhiteSpace(p.Category)
            && !string.IsNullOrWhiteSpace(p.Description));
    }

    [Fact]
    public void Permission_keys_follow_the_dotted_lowercase_convention()
    {
        Permissions.All.Select(p => p.Key)
            .Should().OnlyContain(k => k == k.ToLowerInvariant() && k.Contains('.'));
    }
}
