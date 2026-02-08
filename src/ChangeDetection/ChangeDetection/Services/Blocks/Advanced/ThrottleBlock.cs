using System.Text.Json;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.Blocks.Advanced;

/// <summary>
/// Rate-limits data flow by enforcing a minimum cooldown between pass-throughs.
/// </summary>
public class ThrottleBlock : IPipelineBlock
{
    public string BlockType => "Throttle";

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
                return BlockResult.Failed("Throttle block requires a 'data' input.");

            var cooldownStr = ReadConfig(context);
            var cooldown = string.IsNullOrWhiteSpace(cooldownStr)
                ? TimeSpan.FromHours(1)
                : ParseDuration(cooldownStr) ?? TimeSpan.FromHours(1);

            var ct = context.CancellationToken;

            // Check last pass-through timestamp
            var previous = await context.StateStore.GetPreviousOutputAsync(
                context.WatchId.ToString(), context.BlockInstanceId, ct);

            if (previous is { ValueKind: JsonValueKind.Object } prev &&
                prev.TryGetProperty("_throttleTimestamp", out var tsElem) &&
                tsElem.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(tsElem.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var lastTimestamp))
            {
                var elapsed = context.RunTimestamp - lastTimestamp;
                if (elapsed < cooldown)
                {
                    var remaining = cooldown - elapsed;
                    return BlockResult.Skip($"Throttle cooldown not elapsed ({remaining:hh\\:mm\\:ss} remaining).");
                }
            }

            // Cooldown elapsed or first run — save state and pass data through
            var stateToSave = JsonSerializer.SerializeToElement(new
            {
                _throttleTimestamp = context.RunTimestamp.ToString("O")
            });

            await context.StateStore.SaveOutputAsync(
                context.WatchId.ToString(), context.BlockInstanceId, stateToSave, ct);

            return BlockResult.Succeeded(dataElement);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BlockResult.Failed($"Throttle block failed: {ex.Message}");
        }
    }

    internal static TimeSpan? ParseDuration(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length < 2)
            return null;

        var unit = input[^1];
        if (!int.TryParse(input[..^1], out var amount) || amount < 0)
            return null;

        return unit switch
        {
            's' => TimeSpan.FromSeconds(amount),
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            _ => null
        };
    }

    private static string? ReadConfig(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return null;

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return null;

        if (config.TryGetProperty("cooldown", out var cooldownElem) && cooldownElem.ValueKind == JsonValueKind.String)
            return cooldownElem.GetString();

        return null;
    }
}
