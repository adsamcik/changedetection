using System.Net;
using System.Reflection;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Services;
using ChangeDetection.Services.GroupWatch;
using ChangeDetection.Services.Pipeline;
using ChangeDetection.Services.Search;
using Microsoft.Extensions.DependencyInjection;
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

    [Test]
    public async Task DiscoverAsync_WhenPortalValidationThrows_ExcludesPortalFromResults()
    {
        var provider = Substitute.For<ISearchProvider>();
        provider.ProviderId.Returns("stub");
        provider.DisplayName.Returns("Stub");
        provider.IsAvailable.Returns(true);
        provider.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResultSet
            {
                ProviderId = "stub",
                Query = "biotech careers prague",
                Results =
                [
                    new SearchResult
                    {
                        Url = "https://portal.example/jobs",
                        Title = "Portal",
                        Position = 1
                    }
                ]
            });

        var llmChain = Substitute.For<ILlmProviderChain>();
        llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                new LlmResponse
                {
                    IsSuccess = true,
                    Content = """{"location":"Prague","roleTypes":["scientist"],"field":"biotech","searchQueries":["biotech careers prague"]}"""
                },
                new LlmResponse
                {
                    IsSuccess = true,
                    Content = """[{"url":"https://portal.example/jobs","title":"Portal","reasoning":"career portal"}]"""
                });

        var sut = CreateSut(
            _ => throw new InvalidOperationException("boom"),
            llmChain,
            [provider]);

        var progress = new List<GroupWatchProgress>();
        await foreach (var update in sut.DiscoverAsync("find biotech careers in Prague"))
        {
            progress.Add(update);
        }

        progress.ShouldNotBeEmpty();
        progress[^1].Phase.ShouldBe(GroupWatchPhase.Complete);
        progress[^1].Message.ShouldContain("No suitable career portals");
        progress[^1].Portals.ShouldNotBeNull();
        progress[^1].Portals.ShouldBeEmpty();
    }

    private GroupWatchDiscoveryService CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        ILlmProviderChain? llmChain = null,
        IEnumerable<ISearchProvider>? providers = null)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(new StubHttpMessageHandler(responder)));

        var watchService = Substitute.For<IWatchService>();
        watchService.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WatchedSite>());

        return new GroupWatchDiscoveryService(
            new MultiProviderSearchService(providers ?? [], CreateLogger<MultiProviderSearchService>()),
            llmChain ?? Substitute.For<ILlmProviderChain>(),
            Substitute.For<IWatchGroupService>(),
            watchService,
            new SetupFlowEnhancements(CreateLogger<SetupFlowEnhancements>()),
            Substitute.For<IComposableSetupPipeline>(),
            httpClientFactory,
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<IWatchExecutionLock>(),
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
