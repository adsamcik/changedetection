using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Extraction;
using Microsoft.Extensions.Logging;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Extraction;

[Category("Unit")]
public class VolatilityFilterBlockTests : TestBase
{
    private readonly VolatilityFilterBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_RegexReplacement_ReplacesMatchingStringValues()
    {
        var data = new
        {
            title = "Widget",
            summary = "Price updated: 12345"
        };

        var config = new
        {
            replacement = "[redacted]",
            stripPatterns = new[]
            {
                new { name = "digits", pattern = "\\d+" }
            }
        };

        var result = await _sut.ExecuteAsync(BuildContext("vf-1", data, config));

        result.Success.ShouldBeTrue();
        result.Output!.Value.GetProperty("title").GetString().ShouldBe("Widget");
        result.Output!.Value.GetProperty("summary").GetString().ShouldBe("Price updated: [redacted]");
    }

    [Test]
    public async Task ExecuteAsync_NestedObjectsAndArrays_AreProcessedRecursively()
    {
        var data = new
        {
            section = new
            {
                lastUpdated = "2026-03-18T09:30:00Z",
                entries = new object[]
                {
                    new { text = "Epoch 1710768000" },
                    new { text = "SQL 2026-03-18 10:45:59" }
                }
            }
        };

        var config = new { stripTimestamps = true };

        var result = await _sut.ExecuteAsync(BuildContext("vf-1", data, config));

        result.Success.ShouldBeTrue();
        var section = result.Output!.Value.GetProperty("section");
        section.GetProperty("lastUpdated").GetString().ShouldBe(string.Empty);
        var entries = section.GetProperty("entries").EnumerateArray().ToList();
        entries[0].GetProperty("text").GetString().ShouldBe("Epoch ");
        entries[1].GetProperty("text").GetString().ShouldBe("SQL ");
    }

    [Test]
    public async Task ExecuteAsync_PreservesPropertyOrder()
    {
        var data = JsonDocument.Parse("""{"first":"A1","second":"B2","third":"C3"}""").RootElement.Clone();
        var config = new
        {
            replacement = string.Empty,
            stripPatterns = new[]
            {
                new { name = "digits", pattern = "\\d" }
            }
        };

        var result = await _sut.ExecuteAsync(BuildContext("vf-1", data, config));

        result.Success.ShouldBeTrue();
        result.Output!.Value.GetRawText().ShouldBe("""{"first":"A","second":"B","third":"C"}""");
    }

    [Test]
    public async Task ExecuteAsync_BadRegex_LogsWarningAndSkipsPattern()
    {
        var logger = CreateLogger<VolatilityFilterBlock>();
        var data = new { text = "Keep 12345" };
        var config = new
        {
            replacement = string.Empty,
            stripPatterns = new[]
            {
                new { name = "bad", pattern = "(" },
                new { name = "good", pattern = "\\d+" }
            }
        };

        var result = await _sut.ExecuteAsync(BuildContext("vf-1", data, config, logger));

        result.Success.ShouldBeTrue();
        result.Output!.Value.GetProperty("text").GetString().ShouldBe("Keep ");

        var logs = LogCollector.GetSnapshot();
        logs.ShouldContain(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && entry.Message.Contains("bad", StringComparison.OrdinalIgnoreCase)
            && entry.Message.Contains("skipped", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task ExecuteAsync_StripTimestamps_StripsIsoUnixAndSqlFormats()
    {
        var data = new
        {
            iso = "2026-03-18T09:30:00Z",
            unix = "1710768000123",
            sql = "2026-03-18 10:45:59"
        };

        var result = await _sut.ExecuteAsync(BuildContext("vf-1", data, new { stripTimestamps = true }));

        result.Success.ShouldBeTrue();
        result.Output!.Value.GetProperty("iso").GetString().ShouldBe(string.Empty);
        result.Output!.Value.GetProperty("unix").GetString().ShouldBe(string.Empty);
        result.Output!.Value.GetProperty("sql").GetString().ShouldBe(string.Empty);
    }

    [Test]
    public async Task ExecuteAsync_NullAndEmptyInput_AreHandled()
    {
        var nullResult = await _sut.ExecuteAsync(BuildContext("vf-null", null!, new { stripTimestamps = true }));
        var emptyResult = await _sut.ExecuteAsync(BuildContext("vf-empty", Array.Empty<object>(), new { stripTimestamps = true }));

        nullResult.Success.ShouldBeTrue();
        nullResult.Output!.Value.ValueKind.ShouldBe(JsonValueKind.Null);

        emptyResult.Success.ShouldBeTrue();
        emptyResult.Output!.Value.ValueKind.ShouldBe(JsonValueKind.Array);
        emptyResult.Output!.Value.GetArrayLength().ShouldBe(0);
    }

    [Test]
    public async Task ExecuteAsync_NonStringValues_PassThroughUnchanged()
    {
        var data = new
        {
            count = 42,
            active = true,
            missing = (string?)null,
            nested = new { amount = 19.5m }
        };

        var config = new
        {
            replacement = string.Empty,
            stripPatterns = new[]
            {
                new { name = "digits", pattern = "\\d+" }
            }
        };

        var result = await _sut.ExecuteAsync(BuildContext("vf-1", data, config));

        result.Success.ShouldBeTrue();
        var output = result.Output!.Value;
        output.GetProperty("count").GetInt32().ShouldBe(42);
        output.GetProperty("active").GetBoolean().ShouldBeTrue();
        output.GetProperty("missing").ValueKind.ShouldBe(JsonValueKind.Null);
        output.GetProperty("nested").GetProperty("amount").GetDecimal().ShouldBe(19.5m);
    }

    [Test]
    public async Task PortsAndMetadata_MatchExpectedDefinition()
    {
        _sut.BlockType.ShouldBe("VolatilityFilter");
        _sut.CriticalityTier.ShouldBe(BlockCriticalityTier.Extraction);
        _sut.InputPorts.Count.ShouldBe(1);
        _sut.InputPorts[0].Name.ShouldBe("data");
        _sut.InputPorts[0].Type.ShouldBe(PortType.ExtractedObjects);
        _sut.OutputPorts.Count.ShouldBe(1);
        _sut.OutputPorts[0].Name.ShouldBe("data");
        _sut.OutputPorts[0].Type.ShouldBe(PortType.ExtractedObjects);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExecuteAsync_MissingDataInput_ReturnsFailed()
    {
        var result = await _sut.ExecuteAsync(new BlockContextBuilder()
            .WithBlockInstanceId("vf-1")
            .Build());

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("'data' input");
    }

    private BlockContext BuildContext(string blockId, object data, object config, ILogger? logger = null)
    {
        var builder = new BlockContextBuilder()
            .WithBlockInstanceId(blockId)
            .WithInput("data", data)
            .WithPipelineDefinition(BlockContextBuilder.CreateSingleBlockPipeline(blockId, "VolatilityFilter", config));

        if (logger is not null)
            builder.WithLogger(logger);

        return builder.Build();
    }
}
