namespace ChangeDetection.Core.Pipeline;

/// <summary>
/// Core interface for all pipeline blocks. Each block declares its ports,
/// criticality tier, and execution logic.
/// </summary>
public interface IPipelineBlock
{
    /// <summary>Block type identifier, e.g. "Navigate", "Filter", "ExtractSchema".</summary>
    string BlockType { get; }

    IReadOnlyList<PortDescriptor> InputPorts { get; }
    IReadOnlyList<PortDescriptor> OutputPorts { get; }

    /// <summary>Determines error handling behavior for this block type.</summary>
    BlockCriticalityTier CriticalityTier { get; }

    Task<BlockResult> ExecuteAsync(BlockContext context);
}
