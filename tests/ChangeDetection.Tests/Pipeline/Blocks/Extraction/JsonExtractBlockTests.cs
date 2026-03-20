using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.BlockExecution;
using ChangeDetection.Services.Blocks.Extraction;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Extraction;

[Category("Unit")]
public class JsonExtractBlockTests : TestBase
{
    private readonly JsonExtractBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_WithItemsAndTotal_ExtractsExpectedOutputs()
    {
        const string json = """
            {
              "jobPostings": [
                { "title": "Scientist", "locationsText": "Copenhagen" }
              ],
              "total": 45
            }
            """;

        var config = new
        {
            extractions = new[]
            {
                new { name = "items", jsonpath = "$.jobPostings[*]", type = "array" },
                new { name = "total", jsonpath = "$.total", type = "number" }
            }
        };

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("json-1", "JsonExtract", config);
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("json-1")
            .WithInput("json", (object)json)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();

        var output = result.Output!.Value;
        output.GetProperty("data").ValueKind.ShouldBe(JsonValueKind.Array);
        output.GetProperty("data")[0].GetProperty("title").GetString().ShouldBe("Scientist");
        output.GetProperty("data")[0].GetProperty("locationsText").GetString().ShouldBe("Copenhagen");
        output.GetProperty("total").GetDecimal().ShouldBe(45m);
    }

    [Test]
    public async Task ExecuteAsync_WithBodyPayloadAndStripHtml_SanitizesConfiguredFields()
    {
        const string json = """
            {
              "jobPostings": [
                {
                  "title": "Scientist",
                  "description": "<p>Hello <strong>world</strong></p>",
                  "requirements": "<ul><li>PCR</li><li>Cell culture</li></ul>"
                }
              ]
            }
            """;

        var config = new
        {
            extractions = new[]
            {
                new { name = "items", jsonpath = "$.jobPostings[*]", type = "array" }
            },
            stripHtmlFields = new[] { "description", "requirements" }
        };

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("json-1", "JsonExtract", config);
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("json-1")
            .WithInput("json", new { body = json })
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        var item = result.Output!.Value.GetProperty("data")[0];
        item.GetProperty("description").GetString().ShouldBe("Hello world");
        item.GetProperty("requirements").GetString().ShouldBe("PCR Cell culture");
    }

    [Test]
    public async Task ExecuteAsync_WithInvalidJsonPath_ReturnsFailed()
    {
        var config = new
        {
            extractions = new[]
            {
                new { name = "items", jsonpath = "$..jobPostings[*]", type = "array" }
            }
        };

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("json-1", "JsonExtract", config);
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("json-1")
            .WithInput("json", (object)"{\"jobPostings\":[]}")
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("rejected JSONPath");
    }

    [Test]
    public async Task ExecuteAsync_WithEmptyJson_ReturnsEmptyArrayResult()
    {
        var config = new
        {
            extractions = new[]
            {
                new { name = "items", jsonpath = "$.items[*]", type = "array" }
            }
        };

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("json-1", "JsonExtract", config);
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("json-1")
            .WithInput("json", (object)"{}")
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output!.Value.GetProperty("data").ValueKind.ShouldBe(JsonValueKind.Array);
        result.Output!.Value.GetProperty("data").GetArrayLength().ShouldBe(0);
    }

    [Test]
    public async Task ExecuteAsync_WithJsonExceedingDepthLimit_ReturnsFailed()
    {
        const int depth = 22;
        var nestedJson = Enumerable.Range(0, depth)
            .Reverse()
            .Aggregate("\"value\"", (inner, level) => $$"""{"level{{level}}":{{inner}}}""");

        var config = new
        {
            extractions = new[]
            {
                new { name = "data", jsonpath = "$.level0", type = "value" }
            }
        };

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("json-1", "JsonExtract", config);
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("json-1")
            .WithInput("json", (object)nestedJson)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("could not parse JSON");
    }

    [Test]
    public async Task BlockType_ReturnsJsonExtract()
    {
        _sut.BlockType.ShouldBe("JsonExtract");
        await Task.CompletedTask;
    }

    [Test]
    public async Task CriticalityTier_ReturnsExtraction()
    {
        _sut.CriticalityTier.ShouldBe(BlockCriticalityTier.Extraction);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Ports_MatchExpectedDefinition()
    {
        _sut.InputPorts.Count.ShouldBe(1);
        _sut.InputPorts[0].Name.ShouldBe("json");
        _sut.InputPorts[0].Type.ShouldBe(PortType.PlainText);

        _sut.OutputPorts.Count.ShouldBe(2);
        _sut.OutputPorts[0].Name.ShouldBe("data");
        _sut.OutputPorts[0].Type.ShouldBe(PortType.ExtractedObjects);
        _sut.OutputPorts[1].Name.ShouldBe("total");
        _sut.OutputPorts[1].Type.ShouldBe(PortType.NumericValue);
        _sut.OutputPorts[1].Required.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task RegisterCoreBlocks_RegistersJsonExtract()
    {
        var registry = new BlockRegistry();
        BlockRegistry.RegisterCoreBlocks(registry);

        registry.IsRegistered("JsonExtract").ShouldBeTrue();
        registry.CreateBlock("JsonExtract", new ServiceCollection().BuildServiceProvider())
            .ShouldBeOfType<JsonExtractBlock>();

        await Task.CompletedTask;
    }
}
