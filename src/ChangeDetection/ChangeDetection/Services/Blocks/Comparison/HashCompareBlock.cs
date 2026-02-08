using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.Blocks.Comparison;

/// <summary>
/// Compares content by computing a SHA256 hash and checking against previous output.
/// </summary>
public class HashCompareBlock : IPipelineBlock
{
    public string BlockType => "HashCompare";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "result", Type = PortType.DiffResult }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Analysis;

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("data", out var dataElement))
            return BlockResult.Failed("HashCompare block requires a 'data' input.");

        var ct = context.CancellationToken;
        var content = dataElement.GetRawText();
        var currentHash = ComputeSha256(content);
        context.Logger.LogInformation("HashCompareBlock: Computed hash {Hash}", currentHash);

        var previous = await context.StateStore.GetPreviousOutputAsync(
            context.WatchId.ToString(), context.BlockInstanceId, ct);

        if (previous is null)
        {
            context.Logger.LogInformation("HashCompareBlock: No previous state — first run baseline capture");
            var baseline = JsonSerializer.SerializeToElement(new
            {
                hash = currentHash,
                changed = false
            });
            return BlockResult.BaselineCapture(baseline);
        }

        var previousHash = previous.Value.TryGetProperty("hash", out var hashElem)
            ? hashElem.GetString()
            : null;

        var changed = !string.Equals(currentHash, previousHash, StringComparison.Ordinal);
        context.Logger.LogInformation("HashCompareBlock: Previous hash {PreviousHash}, Changed={Changed}", previousHash, changed);

        var output = JsonSerializer.SerializeToElement(new
        {
            hash = currentHash,
            previousHash,
            changed
        });
        return BlockResult.Succeeded(output);
    }

    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
