using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace ChangeDetection.Services.Blocks.Comparison;

/// <summary>
/// Compares two plain-text inputs using IDiffService and outputs a diff result.
/// </summary>
public class TextDiffBlock : IPipelineBlock
{
    public string BlockType => "TextDiff";

    public IReadOnlyList<PortDescriptor> InputPorts =>
    [
        new PortDescriptor { Name = "current", Type = PortType.PlainText },
        new PortDescriptor { Name = "previous", Type = PortType.PlainText }
    ];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "result", Type = PortType.DiffResult }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Analysis;

    public Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("current", out var currentElement))
            return Task.FromResult(BlockResult.Failed("TextDiff block requires a 'current' input."));

        if (!context.Inputs.TryGetValue("previous", out var previousElement))
            return Task.FromResult(BlockResult.Failed("TextDiff block requires a 'previous' input."));

        var current = currentElement.ValueKind == JsonValueKind.String
            ? currentElement.GetString() ?? ""
            : currentElement.GetRawText();

        var previous = previousElement.ValueKind == JsonValueKind.String
            ? previousElement.GetString() ?? ""
            : previousElement.GetRawText();

        IDiffService diffService;
        try
        {
            diffService = context.Services.GetRequiredService<IDiffService>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(BlockResult.Failed($"IDiffService not available: {ex.Message}"));
        }

        DiffResult diff;
        try
        {
            diff = diffService.Compare(previous, current);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(BlockResult.Failed($"Diff comparison failed: {ex.Message}"));
        }

        var summary = diffService.GenerateSummary(diff);

        var output = JsonSerializer.SerializeToElement(new
        {
            hasChanges = diff.HasChanges,
            linesAdded = diff.LinesAdded,
            linesRemoved = diff.LinesRemoved,
            linesUnchanged = diff.LinesUnchanged,
            summary
        });

        return Task.FromResult(BlockResult.Succeeded(output));
    }
}
