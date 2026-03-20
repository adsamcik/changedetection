using System.Net;
using System.Text;
using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.BlockExecution;
using ChangeDetection.Services.Blocks.Acquisition;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Acquisition;

[Category("Unit")]
public class HttpRequestBlockTests : TestBase
{
    private readonly HttpRequestBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_HttpClientFactory_RetriesTransientStatusAndRecreatesRequest()
    {
        var handler = new SequenceHttpMessageHandler(
            _ => CreateJsonResponse(HttpStatusCode.BadGateway, """{"error":"temporary"}"""),
            _ => CreateJsonResponse(HttpStatusCode.OK, """{"jobs":[{"id":"1"}]}"""));

        var services = CreateFactoryServices(handler);
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("http-1", "HttpRequest", new
        {
            method = "POST",
            headers = new Dictionary<string, string>
            {
                ["Accept"] = "application/json",
                ["X-Test"] = "retry"
            },
            body = """{"query":"scientist"}"""
        });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("http-1")
            .WithInput("url", "https://jobs.example.com/api/search")
            .WithPipelineDefinition(pipeline)
            .WithServices(services)
            .WithLogger(CreateLogger<HttpRequestBlock>())
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue(result.Error);
        handler.RequestCount.ShouldBe(2);
        handler.RequestBodies.ShouldBe(["{\"query\":\"scientist\"}", "{\"query\":\"scientist\"}"]);
        handler.CustomHeaderValues.ShouldBe(["retry", "retry"]);
        handler.AcceptHeaderValues.ShouldBe(["application/json", "application/json"]);
        result.Output!.Value.GetProperty("status").GetInt32().ShouldBe(200);
        result.Output!.Value.GetProperty("json").GetProperty("jobs").GetArrayLength().ShouldBe(1);
    }

    [Test]
    public async Task ExecuteAsync_PinnedClient_RetriesTransientStatus()
    {
        var handler = new SequenceHttpMessageHandler(
            _ => CreateJsonResponse(HttpStatusCode.ServiceUnavailable, """{"error":"retry"}"""),
            _ => CreateJsonResponse(HttpStatusCode.OK, """{"jobs":[{"id":"42"}]}"""));

        var services = CreatePinnedServices(handler);
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("http-1", "HttpRequest", new
        {
            method = "POST",
            headers = new Dictionary<string, string>
            {
                ["Accept"] = "application/json",
                ["X-Test"] = "pinned"
            },
            body = """{"query":"lab"}"""
        });

        var baseContext = new BlockContextBuilder()
            .WithBlockInstanceId("http-1")
            .WithInput("url", "https://example.com/api/jobs")
            .WithPipelineDefinition(pipeline)
            .WithServices(services)
            .WithLogger(CreateLogger<HttpRequestBlock>())
            .Build();

        var context = baseContext.WithDomainPin(DomainPin.FromUserUrl("https://example.com/careers"));

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue(result.Error);
        handler.RequestCount.ShouldBe(2);
        handler.RequestBodies.ShouldBe(["{\"query\":\"lab\"}", "{\"query\":\"lab\"}"]);
        handler.CustomHeaderValues.ShouldBe(["pinned", "pinned"]);
        result.Output!.Value.GetProperty("status").GetInt32().ShouldBe(200);
        result.Output!.Value.GetProperty("json").GetProperty("jobs")[0].GetProperty("id").GetString().ShouldBe("42");
    }

    private ServiceProvider CreateFactoryServices(SequenceHttpMessageHandler handler)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(handler));

        var urlValidator = Substitute.For<IUrlValidator>();
        urlValidator.Validate(Arg.Any<string>()).Returns((string?)null);

        return new ServiceCollection()
            .AddSingleton(httpClientFactory)
            .AddSingleton(urlValidator)
            .BuildServiceProvider();
    }

    private ServiceProvider CreatePinnedServices(SequenceHttpMessageHandler handler)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(handler));

        var urlValidator = Substitute.For<IUrlValidator>();
        urlValidator.Validate(Arg.Any<string>()).Returns((string?)null);

        var domainPinValidator = new DomainPinValidator(CreateLogger<DomainPinValidator>());
        var pinnedClient = new PinnedHttpClient(httpClientFactory, domainPinValidator, CreateLogger<PinnedHttpClient>());

        return new ServiceCollection()
            .AddSingleton(httpClientFactory)
            .AddSingleton(urlValidator)
            .AddSingleton(domainPinValidator)
            .AddSingleton(pinnedClient)
            .BuildServiceProvider();
    }

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, string jsonBody)
        => new(statusCode)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };

    private sealed class SequenceHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new(responses);

        public int RequestCount => RequestBodies.Count;
        public List<string?> RequestBodies { get; } = [];
        public List<string?> CustomHeaderValues { get; } = [];
        public List<string?> AcceptHeaderValues { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            CustomHeaderValues.Add(
                request.Headers.TryGetValues("X-Test", out var headerValues)
                    ? headerValues.Single()
                    : null);
            AcceptHeaderValues.Add(
                request.Headers.Accept.Count > 0
                    ? string.Join(", ", request.Headers.Accept.Select(static value => value.MediaType))
                    : null);

            _responses.Count.ShouldBeGreaterThan(0, "Test handler received more requests than configured.");
            return _responses.Dequeue().Invoke(request);
        }
    }
}

internal static class HttpRequestBlockTestExtensions
{
    public static BlockContext WithDomainPin(this BlockContext context, DomainPin domainPin) => new()
    {
        WatchId = context.WatchId,
        RunTimestamp = context.RunTimestamp,
        BlockInstanceId = context.BlockInstanceId,
        Inputs = context.Inputs,
        CancellationToken = context.CancellationToken,
        Logger = context.Logger,
        StateStore = context.StateStore,
        Page = context.Page,
        Services = context.Services,
        IsFirstRun = context.IsFirstRun,
        IsDryRun = context.IsDryRun,
        PipelineDefinition = context.PipelineDefinition,
        DomainPin = domainPin,
        AllBlockOutputs = context.AllBlockOutputs
    };
}
