using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using Shouldly;

namespace ChangeDetection.Tests.Pipeline.Blocks;

[Category("Unit")]
public class PipelineDefinitionTests : TestBase
{
    private static PipelineDefinition CreateSimplePipeline() => new()
    {
        SchemaVersion = 1,
        Blocks =
        [
            new BlockDefinition { Id = "navigate-1", Type = "Navigate", Position = 0 },
            new BlockDefinition { Id = "extract-1", Type = "ExtractSchema", Position = 1 }
        ],
        Connections =
        [
            new ConnectionDefinition
            {
                FromBlockId = "navigate-1",
                FromPort = "htmlContent",
                ToBlockId = "extract-1",
                ToPort = "htmlInput"
            }
        ]
    };

    [Test]
    public async Task Serialize_ValidPipeline_ProducesCamelCaseJson()
    {
        var pipeline = CreateSimplePipeline();

        var json = PipelineSerializer.Serialize(pipeline);
        Log(json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.TryGetProperty("schemaVersion", out _).ShouldBeTrue();
        root.TryGetProperty("blocks", out var blocks).ShouldBeTrue();
        blocks.GetArrayLength().ShouldBe(2);
        root.TryGetProperty("connections", out var connections).ShouldBeTrue();
        connections.GetArrayLength().ShouldBe(1);

        await Task.CompletedTask;
    }

    [Test]
    public async Task Deserialize_ValidJson_ReturnsPipelineDefinition()
    {
        var original = CreateSimplePipeline();
        var json = PipelineSerializer.Serialize(original);

        var deserialized = PipelineSerializer.Deserialize(json);

        deserialized.ShouldNotBeNull();
        deserialized.SchemaVersion.ShouldBe(1);
        deserialized.Blocks.Count.ShouldBe(2);
        deserialized.Blocks[0].Id.ShouldBe("navigate-1");
        deserialized.Blocks[0].Type.ShouldBe("Navigate");
        deserialized.Connections.Count.ShouldBe(1);
        deserialized.Connections[0].FromBlockId.ShouldBe("navigate-1");
        deserialized.Connections[0].ToPort.ShouldBe("htmlInput");

        await Task.CompletedTask;
    }

    [Test]
    public async Task RoundTrip_ComplexPipeline_PreservesAllData()
    {
        var metadata = new PipelineMetadata
        {
            DisplayTitle = "Price Tracker",
            CreatedAt = new DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc),
            UserIntent = "Track price drops on product page",
            EstimatedLlmCallsPerRun = 3,
            CardType = "price"
        };

        var configJson = JsonSerializer.Deserialize<JsonElement>("""{"selector": ".price", "threshold": 50}""");

        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "navigate-1", Type = "Navigate", Position = 0 },
                new BlockDefinition { Id = "extract-1", Type = "ExtractSchema", Position = 1, Config = configJson },
                new BlockDefinition { Id = "filter-1", Type = "Filter", Position = 2 },
                new BlockDefinition { Id = "notify-1", Type = "Notify", Position = 3 }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "navigate-1", FromPort = "htmlContent", ToBlockId = "extract-1", ToPort = "htmlInput" },
                new ConnectionDefinition { FromBlockId = "extract-1", FromPort = "extracted", ToBlockId = "filter-1", ToPort = "input" },
                new ConnectionDefinition { FromBlockId = "filter-1", FromPort = "passed", ToBlockId = "notify-1", ToPort = "trigger" }
            ],
            Metadata = metadata
        };

        var json = PipelineSerializer.Serialize(pipeline);
        Log(json);
        var deserialized = PipelineSerializer.Deserialize(json);

        deserialized.ShouldNotBeNull();
        deserialized.SchemaVersion.ShouldBe(1);
        deserialized.Blocks.Count.ShouldBe(4);
        deserialized.Connections.Count.ShouldBe(3);
        deserialized.Metadata.ShouldNotBeNull();
        deserialized.Metadata.DisplayTitle.ShouldBe("Price Tracker");
        deserialized.Metadata.CreatedAt.ShouldBe(new DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc));
        deserialized.Metadata.UserIntent.ShouldBe("Track price drops on product page");
        deserialized.Metadata.EstimatedLlmCallsPerRun.ShouldBe(3);
        deserialized.Metadata.CardType.ShouldBe("price");

        // Verify config round-tripped
        deserialized.Blocks[1].Config.ShouldNotBeNull();
        deserialized.Blocks[1].Config!.Value.GetProperty("selector").GetString().ShouldBe(".price");
        deserialized.Blocks[1].Config!.Value.GetProperty("threshold").GetInt32().ShouldBe(50);

        await Task.CompletedTask;
    }

    [Test]
    public async Task Deserialize_InvalidJson_ReturnsNull()
    {
        var result = PipelineSerializer.Deserialize("not valid json {{{");

        result.ShouldBeNull();

        await Task.CompletedTask;
    }

    [Test]
    public async Task BlockDefinition_WithConfig_SerializesJsonElement()
    {
        var configJson = JsonSerializer.Deserialize<JsonElement>("""{"url": "https://example.com", "timeout": 30}""");
        var block = new BlockDefinition { Id = "nav-1", Type = "Navigate", Config = configJson };

        var json = JsonSerializer.Serialize(block, PipelineSerializer.DefaultOptions);
        Log(json);
        var deserialized = JsonSerializer.Deserialize<BlockDefinition>(json, PipelineSerializer.DefaultOptions);

        deserialized.ShouldNotBeNull();
        deserialized.Config.ShouldNotBeNull();
        deserialized.Config!.Value.GetProperty("url").GetString().ShouldBe("https://example.com");
        deserialized.Config!.Value.GetProperty("timeout").GetInt32().ShouldBe(30);

        await Task.CompletedTask;
    }

    [Test]
    public async Task PipelineDefinition_WithMetadata_SerializesCorrectly()
    {
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks = [new BlockDefinition { Id = "block-1", Type = "Navigate" }],
            Connections = [],
            Metadata = new PipelineMetadata
            {
                DisplayTitle = "My Pipeline",
                CardType = "content"
            }
        };

        var json = PipelineSerializer.Serialize(pipeline);
        Log(json);

        using var doc = JsonDocument.Parse(json);
        var meta = doc.RootElement.GetProperty("metadata");
        meta.GetProperty("displayTitle").GetString().ShouldBe("My Pipeline");
        meta.GetProperty("cardType").GetString().ShouldBe("content");
        meta.TryGetProperty("createdAt", out _).ShouldBeFalse("Null properties should be omitted");
        meta.TryGetProperty("userIntent", out _).ShouldBeFalse("Null properties should be omitted");

        var deserialized = PipelineSerializer.Deserialize(json);
        deserialized.ShouldNotBeNull();
        deserialized.Metadata.ShouldNotBeNull();
        deserialized.Metadata.DisplayTitle.ShouldBe("My Pipeline");
        deserialized.Metadata.CardType.ShouldBe("content");
        deserialized.Metadata.CreatedAt.ShouldBeNull();
        deserialized.Metadata.UserIntent.ShouldBeNull();

        await Task.CompletedTask;
    }

    [Test]
    public async Task FindBlockInstanceId_MatchingType_ReturnsBlockId()
    {
        var pipeline = CreateSimplePipeline();

        pipeline.FindBlockInstanceId("navigate").ShouldBe("navigate-1");
        pipeline.FindBlockInstanceId("ExtractSchema").ShouldBe("extract-1");

        await Task.CompletedTask;
    }

    [Test]
    public async Task FindBlockInstanceId_MissingType_ReturnsNull()
    {
        var pipeline = CreateSimplePipeline();

        pipeline.FindBlockInstanceId("Filter").ShouldBeNull();

        await Task.CompletedTask;
    }
}
