using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.BlockExecution;
using ChangeDetection.Services.Blocks.Extraction;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Integration;

[Category("Integration")]
public class PlatsbankenPipelineFixtureTests : TestBase
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Pipeline", "Blocks", "Integration", "Fixtures");

    private readonly JsonExtractBlock _sut = new();

    private static string LoadFixture(string name)
        => File.ReadAllText(Path.Combine(FixtureDir, name));

    [Test]
    public async Task PlatsbankenFixture_ExtractsCorrectHitCount()
    {
        var json = LoadFixture("platsbanken_response.json");
        var config = new
        {
            extractions = new[]
            {
                new { name = "items", jsonpath = "$.hits[*]", type = "array" },
                new { name = "total", jsonpath = "$.total.value", type = "number" }
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

        var output = result.Output!.Value;
        output.GetProperty("data").GetArrayLength().ShouldBe(2);
        output.GetProperty("total").GetDecimal().ShouldBe(2m);
    }

    [Test]
    public async Task PlatsbankenFixture_EachHitHasRequiredFields()
    {
        var json = LoadFixture("platsbanken_response.json");
        var config = new
        {
            extractions = new[]
            {
                new { name = "items", jsonpath = "$.hits[*]", type = "array" }
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
        var items = result.Output!.Value.GetProperty("data");

        foreach (var item in items.EnumerateArray())
        {
            item.TryGetProperty("headline", out _).ShouldBeTrue();
            item.TryGetProperty("workplace_address", out _).ShouldBeTrue();
            item.TryGetProperty("application_details", out _).ShouldBeTrue();

            item.GetProperty("workplace_address").TryGetProperty("municipality", out _).ShouldBeTrue();
            item.GetProperty("application_details").TryGetProperty("url", out _).ShouldBeTrue();
        }
    }
}
