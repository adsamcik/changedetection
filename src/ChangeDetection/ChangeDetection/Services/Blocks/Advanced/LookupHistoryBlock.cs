using System.Text.Json;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.Blocks.Advanced;

/// <summary>
/// Looks up historical values for a specified field using IBlockStateStore.
/// Accumulates history across runs (capped at 100 entries).
/// </summary>
public class LookupHistoryBlock : IPipelineBlock
{
    private const int MaxHistoryEntries = 100;

    public string BlockType => "LookupHistory";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Analysis;

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        try
        {
            if (!context.Inputs.TryGetValue("data", out var dataElement))
                return BlockResult.Failed("LookupHistory block requires a 'data' input.");

            var (field, periodStr) = ReadConfig(context);
            if (string.IsNullOrWhiteSpace(field))
                return BlockResult.Failed("LookupHistory block requires 'field' in config.");

            var period = string.IsNullOrWhiteSpace(periodStr)
                ? TimeSpan.FromDays(7)
                : ThrottleBlock.ParseDuration(periodStr) ?? TimeSpan.FromDays(7);

            var ct = context.CancellationToken;

            // Extract current field value
            JsonElement? currentValue = null;
            if (dataElement.ValueKind == JsonValueKind.Object && dataElement.TryGetProperty(field, out var fieldElem))
                currentValue = fieldElem;

            // Retrieve previous output (contains accumulated history)
            var previous = await context.StateStore.GetPreviousOutputAsync(
                context.WatchId.ToString(), context.BlockInstanceId, ct);

            var historyEntries = new List<JsonElement>();
            var cutoff = context.RunTimestamp - period;

            if (previous is { ValueKind: JsonValueKind.Object } prev &&
                prev.TryGetProperty("history", out var historyArray) &&
                historyArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in historyArray.EnumerateArray())
                {
                    // Filter entries within period
                    if (entry.TryGetProperty("timestamp", out var tsElem) &&
                        tsElem.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(tsElem.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts) &&
                        ts >= cutoff)
                    {
                        historyEntries.Add(entry.Clone());
                    }
                }
            }

            // Append current value to history
            if (currentValue.HasValue)
            {
                var newEntry = JsonSerializer.SerializeToElement(new
                {
                    timestamp = context.RunTimestamp.ToString("O"),
                    value = currentValue.Value
                });
                historyEntries.Add(newEntry);

                // Cap history size
                if (historyEntries.Count > MaxHistoryEntries)
                    historyEntries.RemoveRange(0, historyEntries.Count - MaxHistoryEntries);
            }

            var output = JsonSerializer.SerializeToElement(new
            {
                current = currentValue ?? JsonSerializer.SerializeToElement((object?)null),
                field,
                history = historyEntries
            });

            await context.StateStore.SaveOutputAsync(
                context.WatchId.ToString(), context.BlockInstanceId, output, ct);

            return BlockResult.Succeeded(output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BlockResult.Failed($"LookupHistory block failed: {ex.Message}");
        }
    }

    private static (string? field, string? period) ReadConfig(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return (null, null);

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return (null, null);

        string? field = null, period = null;

        if (config.TryGetProperty("field", out var fieldElem) && fieldElem.ValueKind == JsonValueKind.String)
            field = fieldElem.GetString();

        if (config.TryGetProperty("period", out var periodElem) && periodElem.ValueKind == JsonValueKind.String)
            period = periodElem.GetString();

        return (field, period);
    }
}
