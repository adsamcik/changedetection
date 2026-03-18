using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.BlockExecution;
using ChangeDetection.Services.Blocks.Comparison;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Comparison;

[Category("Unit")]
public class RelevanceScoreBlockTests : TestBase
{
    private readonly RelevanceScoreBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_ArrayInput_ScoresAndFiltersItems()
    {
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("relevance-1", "RelevanceScore", new
        {
            targetFields = new[] { "title", "description" },
            positiveKeywords = new Dictionary<string, int>
            {
                ["scientist"] = 10,
                ["laboratory"] = 8,
                ["R&D"] = 5
            },
            negativeKeywords = new Dictionary<string, int>
            {
                ["senior"] = -5,
                ["PhD required"] = -15,
                ["Danish required"] = -20
            },
            minScore = 5
        });

        var input = JsonSerializer.SerializeToElement(new[]
        {
            new { title = "Scientist", description = "Laboratory R&D role" },
            new { title = "Senior Scientist", description = "PhD required" },
            new { title = "Laboratory assistant", description = "Sample preparation" }
        });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("relevance-1")
            .WithInput("data", input)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();

        var output = result.Output!.Value;
        output.GetProperty("totalScored").GetInt32().ShouldBe(3);
        output.GetProperty("passedFilter").GetInt32().ShouldBe(2);
        output.GetProperty("topScore").GetInt32().ShouldBe(23);
        output.GetProperty("scoringConfig").GetString().ShouldBe("3 positive, 3 negative keywords");

        var items = output.GetProperty("items");
        items.GetArrayLength().ShouldBe(2);
        items[0].GetProperty("relevanceScore").GetInt32().ShouldBe(23);
        items[1].GetProperty("relevanceScore").GetInt32().ShouldBe(8);
    }

    [Test]
    public async Task ExecuteAsync_SingleObject_WrapsOutputInItemsArray()
    {
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("relevance-1", "RelevanceScore", new
        {
            targetFields = new[] { "title" },
            positiveKeywords = new Dictionary<string, int> { ["scientist"] = 10 },
            negativeKeywords = new Dictionary<string, int>(),
            minScore = 1
        });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("relevance-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { title = "Scientist" }))
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        var items = result.Output!.Value.GetProperty("items");
        items.GetArrayLength().ShouldBe(1);
        items[0].GetProperty("title").GetString().ShouldBe("Scientist");
        items[0].GetProperty("relevanceScore").GetInt32().ShouldBe(10);
    }

    [Test]
    public async Task ExecuteAsync_AllNegativeScores_PreservesActualTopScore()
    {
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("relevance-1", "RelevanceScore", new
        {
            targetFields = new[] { "title" },
            positiveKeywords = new Dictionary<string, int>(),
            negativeKeywords = new Dictionary<string, int>
            {
                ["danish required"] = -20,
                ["phd required"] = -15,
                ["senior"] = -5
            },
            minScore = 100
        });

        var input = JsonSerializer.SerializeToElement(new[]
        {
            new { title = "Danish required" },
            new { title = "PhD required" },
            new { title = "Senior scientist" }
        });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("relevance-1")
            .WithInput("data", input)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        var output = result.Output!.Value;
        output.GetProperty("items").GetArrayLength().ShouldBe(0);
        output.GetProperty("topScore").GetInt32().ShouldBe(-5);
        output.GetProperty("passedFilter").GetInt32().ShouldBe(0);
    }

    [Test]
    public async Task Registry_RegistersRelevanceScoreBlock()
    {
        var registry = new BlockRegistry();

        BlockRegistry.RegisterCoreBlocks(registry);

        registry.IsRegistered("RelevanceScore").ShouldBeTrue();
        registry.GetInputPorts("RelevanceScore").Single().Name.ShouldBe("data");
        registry.GetOutputPorts("RelevanceScore").Single().Name.ShouldBe("result");
        registry.CreateBlock("RelevanceScore", Substitute.For<IServiceProvider>()).ShouldBeOfType<RelevanceScoreBlock>();
        await Task.CompletedTask;
    }
}
