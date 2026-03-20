using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.BlockExecution;
using ChangeDetection.Services.Blocks.Extraction;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Integration;

[Category("Integration")]
public class WorkdayPipelineFixtureTests : TestBase
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Pipeline", "Blocks", "Integration", "Fixtures");

    private readonly JsonExtractBlock _sut = new();

    private static string LoadFixture(string name)
        => File.ReadAllText(Path.Combine(FixtureDir, name));

    [Test]
    public async Task WorkdayFixture_ExtractsCorrectItemCount()
    {
        var json = LoadFixture("workday_response.json");
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

        var output = result.Output!.Value;
        var items = output.GetProperty("data");
        items.GetArrayLength().ShouldBe(3);
        output.GetProperty("total").GetDecimal().ShouldBe(3m);
    }

    [Test]
    public async Task WorkdayFixture_EachItemHasRequiredFields()
    {
        var json = LoadFixture("workday_response.json");
        var config = new
        {
            extractions = new[]
            {
                new { name = "items", jsonpath = "$.jobPostings[*]", type = "array" }
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
            item.TryGetProperty("title", out _).ShouldBeTrue();
            item.TryGetProperty("locationsText", out _).ShouldBeTrue();
            item.TryGetProperty("externalPath", out _).ShouldBeTrue();
        }
    }

    [Test]
    public async Task WorkdayFixture_ItemValuesMatchExpected()
    {
        var json = LoadFixture("workday_response.json");
        var config = new
        {
            extractions = new[]
            {
                new { name = "items", jsonpath = "$.jobPostings[*]", type = "array" }
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

        items[0].GetProperty("title").GetString().ShouldBe("Research Scientist");
        items[0].GetProperty("locationsText").GetString().ShouldBe("Copenhagen, Denmark");
        items[0].GetProperty("externalPath").GetString().ShouldBe("/job/R-001");

        items[1].GetProperty("title").GetString().ShouldBe("QC Analyst");
        items[2].GetProperty("locationsText").GetString().ShouldBe("Hillerød, Denmark");
    }
}
