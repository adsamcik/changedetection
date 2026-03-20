using System.Net;
using System.Reflection;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Services.GroupWatch;
using ChangeDetection.Services.Pipeline;
using ChangeDetection.Services.Search;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services;

[Category("Unit")]
public class GroupWatchDiscoveryUrlValidationTests : TestBase
{
    [Test]
    public async Task ValidatePortalUrlAsync_404Response_ReturnsInvalid()
    {
        var sut = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("missing")
        });

        var result = await InvokeValidatePortalUrlAsync(sut, "https://dead.example/jobs");

        result.IsValid.ShouldBeFalse();
        result.StatusCode.ShouldBe(404);
        result.Reason.ShouldContain("no longer available");
    }

    [Test]
    public async Task ValidatePortalUrlAsync_403Captcha_ReturnsInvalid()
    {
        var sut = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("<html><body>Please verify you are human with captcha</body></html>")
        });

        var result = await InvokeValidatePortalUrlAsync(sut, "https://captcha.example/jobs");

        result.IsValid.ShouldBeFalse();
        result.StatusCode.ShouldBe(403);
        result.Reason.ShouldContain("CAPTCHA");
    }

    [Test]
    public async Task ValidatePortalUrlAsync_403LoginRequired_ReturnsValid()
    {
        var sut = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("<html><body>Please sign in to continue</body></html>")
        });

        var result = await InvokeValidatePortalUrlAsync(sut, "https://login.example/jobs");

        result.IsValid.ShouldBeTrue();
        result.StatusCode.ShouldBe(403);
        result.Reason.ShouldBe("Login required");
    }

    private GroupWatchDiscoveryService CreateSut(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(new StubHttpMessageHandler(responder)));

        return new GroupWatchDiscoveryService(
            new MultiProviderSearchService([], CreateLogger<MultiProviderSearchService>()),
            Substitute.For<ILlmProviderChain>(),
            Substitute.For<IWatchGroupService>(),
            Substitute.For<IWatchService>(),
            new SetupFlowEnhancements(CreateLogger<SetupFlowEnhancements>()),
            Substitute.For<IComposableSetupPipeline>(),
            httpClientFactory,
            CreateLogger<GroupWatchDiscoveryService>(),
            Options.Create(new GroupWatchDiscoveryOptions()));
    }

    private static async Task<UrlValidationResult> InvokeValidatePortalUrlAsync(
        GroupWatchDiscoveryService sut,
        string url)
    {
        var method = typeof(GroupWatchDiscoveryService).GetMethod(
            "ValidatePortalUrlAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method.ShouldNotBeNull();

        var task = method.Invoke(sut, [url, CancellationToken.None]) as Task<UrlValidationResult>;
        task.ShouldNotBeNull();

        return await task;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
