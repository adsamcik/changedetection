using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Services.Search;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Search;

[Category("Unit")]
public class SearchPipelineBuilderTests : TestBase
{
    [Test]
    public async Task BuildSearchPipeline_BasicQuery_CreatesTwoBlocks()
    {
        var config = new SearchConfig { Query = "test query" };

        var pipeline = SearchPipelineBuilder.BuildSearchPipeline(config);

        pipeline.SchemaVersion.ShouldBe(1);
        pipeline.Blocks.Count.ShouldBe(2);
        pipeline.Blocks[0].Type.ShouldBe("Search");
        pipeline.Blocks[1].Type.ShouldBe("TextDiff");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildSearchPipeline_HasConnection_SearchTextToTextDiffCurrent()
    {
        var config = new SearchConfig { Query = "test" };

        var pipeline = SearchPipelineBuilder.BuildSearchPipeline(config);

        pipeline.Connections.Count.ShouldBe(1);
        pipeline.Connections[0].FromBlockId.ShouldBe("search-1");
        pipeline.Connections[0].FromPort.ShouldBe("text");
        pipeline.Connections[0].ToBlockId.ShouldBe("textdiff-1");
        pipeline.Connections[0].ToPort.ShouldBe("current");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildSearchPipeline_ConfigIncludesQuery()
    {
        var config = new SearchConfig { Query = "dotnet 10 release" };

        var pipeline = SearchPipelineBuilder.BuildSearchPipeline(config);

        var searchBlock = pipeline.Blocks[0];
        searchBlock.Config.ShouldNotBeNull();
        var blockConfig = searchBlock.Config.Value;
        blockConfig.TryGetProperty("query", out var query).ShouldBeTrue();
        query.GetString().ShouldBe("dotnet 10 release");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildSearchPipeline_WithProvider_IncludesProviderInConfig()
    {
        var config = new SearchConfig { Query = "test", ProviderId = "brave" };

        var pipeline = SearchPipelineBuilder.BuildSearchPipeline(config);

        var blockConfig = pipeline.Blocks[0].Config!.Value;
        blockConfig.TryGetProperty("provider", out var provider).ShouldBeTrue();
        provider.GetString().ShouldBe("brave");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildSearchPipeline_WithCategory_IncludesCategoryInConfig()
    {
        var config = new SearchConfig { Query = "test", Category = "news" };

        var pipeline = SearchPipelineBuilder.BuildSearchPipeline(config);

        var blockConfig = pipeline.Blocks[0].Config!.Value;
        blockConfig.TryGetProperty("category", out var category).ShouldBeTrue();
        category.GetString().ShouldBe("news");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildSearchPipeline_HasMetadata()
    {
        var config = new SearchConfig { Query = "test query" };

        var pipeline = SearchPipelineBuilder.BuildSearchPipeline(config);

        pipeline.Metadata.ShouldNotBeNull();
        pipeline.Metadata.DisplayTitle.ShouldContain("test query");
        pipeline.Metadata.UserIntent.ShouldNotBeNullOrWhiteSpace();
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildSearchPipelineJson_ReturnsValidJson()
    {
        var config = new SearchConfig { Query = "test", MaxResults = 10 };

        var json = SearchPipelineBuilder.BuildSearchPipelineJson(config);

        json.ShouldNotBeNullOrWhiteSpace();

        // Should be parseable
        var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("schemaVersion", out _).ShouldBeTrue();
        doc.RootElement.TryGetProperty("blocks", out var blocks).ShouldBeTrue();
        blocks.GetArrayLength().ShouldBe(2);
        await Task.CompletedTask;
    }
}
