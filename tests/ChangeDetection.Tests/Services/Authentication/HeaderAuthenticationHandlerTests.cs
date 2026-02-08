using System.Security.Claims;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.Authentication;

[Category("Unit")]
public class HeaderAuthenticationHandlerTests
{
    private readonly AuthenticationSettings _authSettings;

    public HeaderAuthenticationHandlerTests()
    {
        _authSettings = new AuthenticationSettings
        {
            Mode = AuthenticationMode.SSO,
            UsernameHeader = "Remote-User",
            EmailHeader = "Remote-Email",
            DisplayNameHeader = "Remote-Name",
            GroupsHeader = "Remote-Groups",
            AdminGroup = "changedetection-admins"
        };
    }

    private (HeaderAuthenticationHandler handler, IUserService userService) CreateHandler(DefaultHttpContext httpContext)
    {
        var userService = Substitute.For<IUserService>();

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton(userService);
        var serviceProvider = serviceCollection.BuildServiceProvider();

        var optionsMonitor = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        optionsMonitor.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());

        var loggerFactory = NullLoggerFactory.Instance;

        var handler = new HeaderAuthenticationHandler(
            optionsMonitor,
            loggerFactory,
            System.Text.Encodings.Web.UrlEncoder.Default,
            Options.Create(_authSettings),
            serviceProvider);

        var scheme = new AuthenticationScheme(
            HeaderAuthenticationHandler.SchemeName,
            displayName: null,
            typeof(HeaderAuthenticationHandler));

        handler.InitializeAsync(scheme, httpContext).GetAwaiter().GetResult();

        return (handler, userService);
    }

    [Test]
    public async Task HandleAuthenticate_MissingUsernameHeader_ReturnsNoResult()
    {
        var httpContext = new DefaultHttpContext();
        var (handler, _) = CreateHandler(httpContext);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeFalse();
        result.None.ShouldBeTrue();
    }

    [Test]
    public async Task HandleAuthenticate_ValidHeaders_ReturnsSuccess()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Remote-User"] = "alice";
        httpContext.Request.Headers["Remote-Email"] = "alice@example.com";
        httpContext.Request.Headers["Remote-Name"] = "Alice Smith";
        httpContext.Request.Headers["Remote-Groups"] = "users,editors";

        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "alice",
            Email = "alice@example.com",
            DisplayName = "Alice Smith",
            Groups = ["users", "editors"],
            IsActive = true
        };

        var (handler, userService) = CreateHandler(httpContext);
        userService.GetOrCreateFromSsoAsync(
                "alice", "alice@example.com", "Alice Smith",
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeTrue();
        result.Principal.ShouldNotBeNull();
        result.Principal!.Identity!.IsAuthenticated.ShouldBeTrue();

        result.Principal.FindFirst(ClaimTypes.Name)!.Value.ShouldBe("alice");
        result.Principal.FindFirst(ClaimTypes.Email)!.Value.ShouldBe("alice@example.com");
        result.Principal.FindFirst(HeaderAuthenticationClaims.UserId)!.Value.ShouldBe(userId.ToString());
    }

    [Test]
    public async Task HandleAuthenticate_AdminGroup_SetsAdminClaim()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Remote-User"] = "admin";
        httpContext.Request.Headers["Remote-Groups"] = "changedetection-admins,users";

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "admin",
            Groups = ["changedetection-admins", "users"],
            IsActive = true
        };

        var (handler, userService) = CreateHandler(httpContext);
        userService.GetOrCreateFromSsoAsync(
                "admin", Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeTrue();
        result.Principal!.HasClaim(HeaderAuthenticationClaims.IsAdmin, "true").ShouldBeTrue();
    }

    [Test]
    public async Task HandleAuthenticate_InactiveUser_ReturnsFail()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Remote-User"] = "alice";

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice",
            IsActive = false
        };

        var (handler, userService) = CreateHandler(httpContext);
        userService.GetOrCreateFromSsoAsync(
                "alice", Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Message.ShouldContain("deactivated");
    }

    [Test]
    public async Task HandleAuthenticate_InvalidUsernameFormat_ReturnsFail()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Remote-User"] = "alice/bob"; // slash not allowed

        var (handler, _) = CreateHandler(httpContext);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Message.ShouldContain("Invalid username format");
    }

    [Test]
    public async Task HandleAuthenticate_InvalidEmail_ReturnsFail()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Remote-User"] = "alice";
        httpContext.Request.Headers["Remote-Email"] = "not-an-email";

        var (handler, _) = CreateHandler(httpContext);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Message.ShouldContain("email");
    }

    [Test]
    public async Task HandleAuthenticate_ControlCharactersInUsername_Sanitized()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Remote-User"] = "alice\r\nbob";

        // After sanitization "alicebob" is a valid username
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "alicebob",
            IsActive = true
        };

        var (handler, userService) = CreateHandler(httpContext);
        userService.GetOrCreateFromSsoAsync(
                "alicebob", Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeTrue();
        result.Principal!.FindFirst(ClaimTypes.Name)!.Value.ShouldBe("alicebob");
    }

    [Test]
    public async Task HandleAuthenticate_GroupsParsedFromComma()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Remote-User"] = "alice";
        httpContext.Request.Headers["Remote-Groups"] = "group1, group2, group3";

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice",
            Groups = ["group1", "group2", "group3"],
            IsActive = true
        };

        var (handler, userService) = CreateHandler(httpContext);
        userService.GetOrCreateFromSsoAsync(
                "alice", Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeTrue();
        var groupClaims = result.Principal!.FindAll(HeaderAuthenticationClaims.Group).Select(c => c.Value).ToList();
        groupClaims.ShouldContain("group1");
        groupClaims.ShouldContain("group2");
        groupClaims.ShouldContain("group3");
    }

    [Test]
    public async Task HandleAuthenticate_EmptyUsernameHeader_ReturnsNoResult()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Remote-User"] = "";

        var (handler, _) = CreateHandler(httpContext);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeFalse();
        result.None.ShouldBeTrue();
    }
}
