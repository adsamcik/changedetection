using ChangeDetection.Core.Pipeline;
using Shouldly;

namespace ChangeDetection.Tests.Pipeline;

[Category("Unit")]
public class PipelineSerializerTests
{
    [Test]
    public async Task Deserialize_OversizedJson_ReturnsNull()
    {
        var oversized = new string(' ', PipelineSerializer.MaxJsonSizeBytes + 1);

        var result = PipelineSerializer.Deserialize(oversized);

        result.ShouldBeNull();

        await Task.CompletedTask;
    }

    [Test]
    public async Task Deserialize_EmptyString_ReturnsNull()
    {
        var result = PipelineSerializer.Deserialize("");

        result.ShouldBeNull();

        await Task.CompletedTask;
    }

    [Test]
    public async Task Deserialize_WhitespaceOnly_ReturnsNull()
    {
        var result = PipelineSerializer.Deserialize("   ");

        result.ShouldBeNull();

        await Task.CompletedTask;
    }

    [Test]
    public async Task Deserialize_DeeplyNestedJson_ReturnsNull()
    {
        // Build JSON that exceeds MaxDepth of 64
        var nested = new string('{', 70) + "\"a\":1" + new string('}', 70);

        var result = PipelineSerializer.Deserialize(nested);

        result.ShouldBeNull();

        await Task.CompletedTask;
    }

    [Test]
    public async Task Deserialize_ValidJson_ReturnsDefinition()
    {
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks = [new BlockDefinition { Id = "nav-1", Type = "Navigate", Position = 0 }],
            Connections = []
        };
        var json = PipelineSerializer.Serialize(pipeline);

        var result = PipelineSerializer.Deserialize(json);

        result.ShouldNotBeNull();
        result.SchemaVersion.ShouldBe(1);
        result.Blocks.Count.ShouldBe(1);
        result.Blocks[0].Id.ShouldBe("nav-1");

        await Task.CompletedTask;
    }

    [Test]
    public async Task DefaultOptions_HasMaxDepth64()
    {
        PipelineSerializer.DefaultOptions.MaxDepth.ShouldBe(64);

        await Task.CompletedTask;
    }

    [Test]
    public async Task MaxJsonSizeBytes_Is1MB()
    {
        PipelineSerializer.MaxJsonSizeBytes.ShouldBe(1_048_576);

        await Task.CompletedTask;
    }
}
