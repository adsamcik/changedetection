using System.Diagnostics;
using System.Net;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Search;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.Search;

[Category("Unit")]
public class LlmSearchProviderTests : TestBase
{
    [Test]
    public async Task IsAvailable_Always_ReturnsTrue()
    {
        var sut = CreateSut(static _ => new HttpResponseMessage(HttpStatusCode.OK));

        sut.IsAvailable.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task SearchAsync_ValidSuggestionsPassHttpValidation()
    {
        var llm = CreateLlm("""
            [
              {"url":"https://careers.example.com/jobs","title":"Careers","reasoning":"direct careers page"}
            ]
            """);

        HttpMethod? firstMethod = null;
        var sut = CreateSut(
            request =>
            {
                firstMethod ??= request.Method;
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            llm);

        var result = await sut.SearchAsync(new SearchQuery { Query = "scientist jobs", MaxResults = 5 });

        result.IsSuccess.ShouldBeTrue();
        result.Results.Count.ShouldBe(1);
        result.Results[0].Url.ShouldBe("https://careers.example.com/jobs");
        result.Results[0].Engine.ShouldBe("llm");
        firstMethod.ShouldBe(HttpMethod.Head);
    }

    [Test]
    public async Task SearchAsync_Hallucinated404Urls_AreFilteredOut()
    {
        var llm = CreateLlm("""
            [
              {"url":"https://careers.example.com/jobs","title":"Careers","reasoning":"direct careers page"},
              {"url":"https://hallucinated.example.com/missing","title":"Missing","reasoning":"looks plausible"}
            ]
            """);

        var sut = CreateSut(
            request => new HttpResponseMessage(
                request.RequestUri!.Host.Contains("hallucinated", StringComparison.OrdinalIgnoreCase)
                    ? HttpStatusCode.NotFound
                    : HttpStatusCode.OK),
            llm);

        var result = await sut.SearchAsync(new SearchQuery { Query = "scientist jobs", MaxResults = 5 });

        result.IsSuccess.ShouldBeTrue();
        result.Results.Count.ShouldBe(1);
        result.Results[0].Url.ShouldBe("https://careers.example.com/jobs");
        result.Results.ShouldNotContain(r => r.Url.Contains("hallucinated", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task SearchAsync_WhenValidationBudgetIsHit_ReturnsWithinBudgetWithNoVerifiedResults()
    {
        var suggestions = string.Join(
            ",",
            Enumerable.Range(1, 20)
                .Select(i => $$"""{"url":"https://slow{{i}}.example.com/jobs","title":"Slow {{i}}","reasoning":"slow"}"""));

        var llm = CreateLlm($"[{suggestions}]");
        var sut = CreateSut(
            async request =>
            {
                await Task.Delay(TimeSpan.FromMinutes(1), request.GetCancellationToken());
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            llm);

        var stopwatch = Stopwatch.StartNew();
        var result = await sut.SearchAsync(new SearchQuery { Query = "slow validation", MaxResults = 20 });
        stopwatch.Stop();

        result.IsSuccess.ShouldBeTrue();
        result.Results.ShouldBeEmpty();
        stopwatch.Elapsed.ShouldBeGreaterThan(TimeSpan.FromSeconds(7));
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
    }

    private LlmSearchProvider CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        ILlmProviderChain? llm = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(new StubHttpMessageHandler(responder)));

        return CreateSut(factory, llm);
    }

    private LlmSearchProvider CreateSut(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder,
        ILlmProviderChain? llm = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(new AsyncStubHttpMessageHandler(responder)));

        return CreateSut(factory, llm);
    }

    private LlmSearchProvider CreateSut(IHttpClientFactory httpClientFactory, ILlmProviderChain? llm = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(llm ?? CreateLlm("[]"));
        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var urlValidator = Substitute.For<IUrlValidator>();
        urlValidator.Validate(Arg.Any<string>()).Returns((string?)null);

        return new LlmSearchProvider(
            scopeFactory,
            httpClientFactory,
            urlValidator,
            CreateLogger<LlmSearchProvider>());
    }

    private static ILlmProviderChain CreateLlm(string content)
    {
        var llm = Substitute.For<ILlmProviderChain>();
        llm.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = content
            });
        return llm;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.SetCancellationToken(cancellationToken);
            var response = responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class AsyncStubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.SetCancellationToken(cancellationToken);
            var response = await responder(request);
            response.RequestMessage ??= request;
            return response;
        }
    }
}

internal static class HttpRequestMessageTestExtensions
{
    private static readonly HttpRequestOptionsKey<CancellationToken> CancellationTokenKey = new("ChangeDetection.Tests.CancellationToken");

    public static void SetCancellationToken(this HttpRequestMessage request, CancellationToken cancellationToken)
        => request.Options.Set(CancellationTokenKey, cancellationToken);

    public static CancellationToken GetCancellationToken(this HttpRequestMessage request)
        => request.Options.TryGetValue(CancellationTokenKey, out CancellationToken token) ? token : CancellationToken.None;
}
