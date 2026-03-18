using System.Net;
using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.BlockExecution;
using ChangeDetection.Services.Blocks.Advanced;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Advanced;

[Category("Unit")]
public class LinkValidateBlockTests : TestBase
{
    private readonly LinkValidateBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_MarksDeadLink_WhenHttpStatusIs404()
    {
        var context = CreateContext(
            new { url = "https://jobs.example.com/posting/1", title = "Scientist" },
            CreateClientFactory(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://jobs.example.com/posting/1")
            }));

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output!.Value.GetProperty("url_valid").GetBoolean().ShouldBeFalse();
        result.Output!.Value.GetProperty("url_status").GetString().ShouldBe("dead_link");
        result.Output!.Value.GetProperty("hasDeadLinks").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_MarksDeadListing_WhenBodyContainsDeathSignal()
    {
        var context = CreateContext(
            new { applyUrl = "https://jobs.example.com/apply/2" },
            CreateClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>This job is no longer available</body></html>"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://jobs.example.com/apply/2")
            }),
            urlFields: ["applyUrl"]);

        var result = await _sut.ExecuteAsync(context);

        result.Output!.Value.GetProperty("applyUrl_valid").GetBoolean().ShouldBeFalse();
        result.Output!.Value.GetProperty("applyUrl_status").GetString().ShouldBe("dead_listing");
        result.Output!.Value.GetProperty("hasDeadLinks").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_MarksSuspiciousRedirect_WhenFinalUrlLooksGeneric()
    {
        var context = CreateContext(
            new { applyUrl = "https://aggregator.example.com/jobs/123" },
            CreateClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>Careers portal</body></html>"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://company.example.com/careers")
            }),
            urlFields: ["applyUrl"]);

        var result = await _sut.ExecuteAsync(context);

        result.Output!.Value.GetProperty("applyUrl_valid").GetBoolean().ShouldBeFalse();
        result.Output!.Value.GetProperty("applyUrl_status").GetString().ShouldBe("redirect");
        result.Output!.Value.GetProperty("hasDeadLinks").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_UsesNamedClient_WhenRedirectsDisabled()
    {
        var factory = CreateClientFactory(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>Live</body></html>"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://company.example.com/login")
            });

        var context = CreateContext(
            new { applyUrl = "https://aggregator.example.com/jobs/123" },
            factory,
            urlFields: ["applyUrl"],
            followRedirects: false);

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        factory.Received(1).CreateClient("LinkValidate-NoRedirect");
    }

    [Test]
    public async Task ExecuteAsync_LimitsBodyRead_ToPreventHugeResponses()
    {
        var hugeBody = new string('a', 70_000) + " page not found";
        var context = CreateContext(
            new { url = "https://jobs.example.com/posting/3" },
            CreateClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(hugeBody),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://jobs.example.com/posting/3")
            }));

        var result = await _sut.ExecuteAsync(context);

        result.Output!.Value.GetProperty("url_status").GetString().ShouldBe("live");
        result.Output!.Value.GetProperty("url_valid").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_WrapsArrayResults_AndAggregatesDeadLinks()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>Live posting</body></html>"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://jobs.example.com/1")
            },
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://jobs.example.com/2")
            }
        ]);

        var context = CreateContext(
            new[]
            {
                new { url = "https://jobs.example.com/1", title = "Live" },
                new { url = "https://jobs.example.com/2", title = "Dead" }
            },
            CreateClientFactory(_ => responses.Dequeue()));

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output!.Value.GetProperty("hasDeadLinks").GetBoolean().ShouldBeTrue();
        result.Output!.Value.GetProperty("items").GetArrayLength().ShouldBe(2);
        result.Output!.Value.GetProperty("items")[0].GetProperty("url_status").GetString().ShouldBe("live");
        result.Output!.Value.GetProperty("items")[1].GetProperty("url_status").GetString().ShouldBe("dead_link");
    }

    [Test]
    public async Task RegisterCoreBlocks_IncludesLinkValidate()
    {
        var registry = new BlockRegistry();

        BlockRegistry.RegisterCoreBlocks(registry);

        registry.IsRegistered("LinkValidate").ShouldBeTrue();
        registry.GetInputPorts("LinkValidate").Single().Name.ShouldBe("data");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BlockMetadata_MatchesDefinition()
    {
        _sut.BlockType.ShouldBe("LinkValidate");
        _sut.CriticalityTier.ShouldBe(BlockCriticalityTier.Analysis);
        _sut.InputPorts.Single().Name.ShouldBe("data");
        _sut.OutputPorts.Single().Name.ShouldBe("data");
        await Task.CompletedTask;
    }

    private static BlockContext CreateContext(
        object data,
        IHttpClientFactory httpClientFactory,
        string[]? urlFields = null,
        string? language = "en",
        bool followRedirects = true)
    {
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("validate-1", "LinkValidate", new
        {
            urlFields = urlFields ?? new[] { "url" },
            language,
            followRedirects
        });

        var validator = Substitute.For<IUrlValidator>();
        validator.Validate(Arg.Any<string>()).Returns((string?)null);

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IHttpClientFactory)).Returns(httpClientFactory);
        services.GetService(typeof(IUrlValidator)).Returns(validator);

        return new BlockContextBuilder()
            .WithBlockInstanceId("validate-1")
            .WithInput("data", JsonSerializer.SerializeToElement(data))
            .WithServices(services)
            .WithPipelineDefinition(pipeline)
            .Build();
    }

    private static IHttpClientFactory CreateClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var client = new HttpClient(new StubHttpMessageHandler(responder));
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        return factory;
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
