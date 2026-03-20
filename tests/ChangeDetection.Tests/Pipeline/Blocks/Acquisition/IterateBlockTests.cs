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
public class IterateBlockTests : TestBase
{
    private readonly IterateBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_WithValues_MergesAndDeduplicatesResults()
    {
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://jobs.example.com/search?q=scientist"] =
                """{"hits":[{"id":"1","title":"Scientist"},{"id":"2","title":"Lab Tech"}]}""",
            ["https://jobs.example.com/search?q=laboratory"] =
                """{"hits":[{"id":"2","title":"Lab Tech"},{"id":"3","title":"Research Associate"}]}"""
        };

        var services = CreateServices(responses);
        var config = new
        {
            values = new[] { "scientist", "laboratory" },
            request = new
            {
                urlTemplate = "https://jobs.example.com/search?q={{value}}",
                method = "GET",
                headers = new { Accept = "application/json" }
            },
            extract = new
            {
                jsonpath = "$.hits[*]",
                type = "array"
            },
            deduplicateKey = "id"
        };

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("iterate-1", "Iterate", config);
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("iterate-1")
            .WithPipelineDefinition(pipeline)
            .WithServices(services)
            .WithLogger(CreateLogger<IterateBlock>())
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue(result.Error);
        var data = result.Output!.Value.GetProperty("data");
        data.ValueKind.ShouldBe(JsonValueKind.Array);
        data.GetArrayLength().ShouldBe(3);
        data[0].GetProperty("id").GetString().ShouldBe("1");
        data[1].GetProperty("id").GetString().ShouldBe("2");
        data[2].GetProperty("id").GetString().ShouldBe("3");
    }

    [Test]
    public async Task ExecuteAsync_WithInvalidJsonPath_ReturnsFailed()
    {
        var services = CreateServices(new Dictionary<string, string>());
        var config = new
        {
            values = new[] { "scientist" },
            request = new
            {
                urlTemplate = "https://jobs.example.com/search?q={{value}}"
            },
            extract = new
            {
                jsonpath = "$..hits[*]",
                type = "array"
            }
        };

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("iterate-1", "Iterate", config);
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("iterate-1")
            .WithPipelineDefinition(pipeline)
            .WithServices(services)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("rejected JSONPath");
    }

    [Test]
    public async Task RegisterCoreBlocks_RegistersIterate()
    {
        var registry = new BlockRegistry();
        BlockRegistry.RegisterCoreBlocks(registry);

        registry.IsRegistered("Iterate").ShouldBeTrue();
        registry.CreateBlock("Iterate", new ServiceCollection().BuildServiceProvider())
            .ShouldBeOfType<IterateBlock>();

        await Task.CompletedTask;
    }

    [Test]
    public async Task Ports_MatchExpectedDefinition()
    {
        _sut.InputPorts.ShouldBeEmpty();
        _sut.OutputPorts.Count.ShouldBe(1);
        _sut.OutputPorts[0].Name.ShouldBe("data");
        _sut.OutputPorts[0].Type.ShouldBe(PortType.ExtractedObjects);
        _sut.CriticalityTier.ShouldBe(BlockCriticalityTier.Acquisition);
        _sut.BlockType.ShouldBe("Iterate");

        await Task.CompletedTask;
    }

    private ServiceProvider CreateServices(IReadOnlyDictionary<string, string> responses)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(new StubHttpMessageHandler(responses)));

        var urlValidator = Substitute.For<IUrlValidator>();
        urlValidator.Validate(Arg.Any<string>()).Returns((string?)null);

        return new ServiceCollection()
            .AddSingleton(httpClientFactory)
            .AddSingleton(urlValidator)
            .BuildServiceProvider();
    }

    private sealed class StubHttpMessageHandler(IReadOnlyDictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (!responses.TryGetValue(url, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"hits":[]}""", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
