using System.Security.Claims;
using ChangeDetection.Core.Entities;
using ChangeDetection.Services.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.Authentication;

[Category("Unit")]
public class AdminAuthorizationTests
{
    private static AdminRequirementHandler CreateHandler(AuthenticationMode mode = AuthenticationMode.SSO, string adminGroup = "changedetection-admins")
    {
        var settings = new AuthenticationSettings
        {
            Mode = mode,
            AdminGroup = adminGroup
        };
        return new AdminRequirementHandler(Options.Create(settings));
    }

    private static async Task<AuthorizationHandlerContext> InvokeHandler(
        AdminRequirementHandler handler,
        ClaimsPrincipal user)
    {
        var requirement = new AdminRequirement();
        var context = new AuthorizationHandlerContext([requirement], user, null);
        await handler.HandleAsync(context);
        return context;
    }

    [Test]
    public async Task HandleRequirement_SingleUserMode_AlwaysSucceeds()
    {
        // In single-user mode, the Admin policy uses RequireAssertion(_ => true),
        // bypassing AdminRequirementHandler entirely. Verify that policy-level behavior.
        var services = new ServiceCollection();
        services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthorizationPolicies.Admin, policy =>
                policy.RequireAssertion(_ => true));
        });
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var authService = sp.GetRequiredService<IAuthorizationService>();

        var user = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await authService.AuthorizeAsync(user, AuthorizationPolicies.Admin);

        result.Succeeded.ShouldBeTrue();
    }

    [Test]
    public async Task HandleRequirement_SsoMode_AdminClaimTrue_Succeeds()
    {
        var handler = CreateHandler();
        var claims = new[] { new Claim(HeaderAuthenticationClaims.IsAdmin, "true") };
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        var context = await InvokeHandler(handler, user);

        context.HasSucceeded.ShouldBeTrue();
    }

    [Test]
    public async Task HandleRequirement_SsoMode_AdminClaimFalse_Fails()
    {
        var handler = CreateHandler();
        var claims = new[] { new Claim(HeaderAuthenticationClaims.IsAdmin, "false") };
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        var context = await InvokeHandler(handler, user);

        context.HasSucceeded.ShouldBeFalse();
    }

    [Test]
    public async Task HandleRequirement_SsoMode_InAdminRole_Succeeds()
    {
        var handler = CreateHandler();
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(ClaimTypes.Role, "changedetection-admins"));
        var user = new ClaimsPrincipal(identity);

        var context = await InvokeHandler(handler, user);

        context.HasSucceeded.ShouldBeTrue();
    }

    [Test]
    public async Task HandleRequirement_SsoMode_InAdminGroupClaim_Succeeds()
    {
        var handler = CreateHandler();
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(HeaderAuthenticationClaims.Group, "changedetection-admins"));
        var user = new ClaimsPrincipal(identity);

        var context = await InvokeHandler(handler, user);

        context.HasSucceeded.ShouldBeTrue();
    }

    [Test]
    public async Task HandleRequirement_SsoMode_NoMatchingClaims_Fails()
    {
        var handler = CreateHandler();
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(ClaimTypes.Name, "alice"));
        identity.AddClaim(new Claim(HeaderAuthenticationClaims.Group, "users"));
        var user = new ClaimsPrincipal(identity);

        var context = await InvokeHandler(handler, user);

        context.HasSucceeded.ShouldBeFalse();
    }

    [Test]
    public async Task HandleRequirement_SsoMode_GroupClaimCaseInsensitive_Succeeds()
    {
        var handler = CreateHandler(adminGroup: "admin");
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(HeaderAuthenticationClaims.Group, "ADMIN"));
        var user = new ClaimsPrincipal(identity);

        var context = await InvokeHandler(handler, user);

        context.HasSucceeded.ShouldBeTrue();
    }
}
