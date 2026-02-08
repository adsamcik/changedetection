namespace ChangeDetection.Core.Pipeline;

/// <summary>
/// Registry that maps block type strings to their port descriptors (for validation)
/// and to factory functions (for execution).
/// </summary>
public interface IBlockRegistry
{
    bool IsRegistered(string blockType);
    IReadOnlyList<PortDescriptor> GetInputPorts(string blockType);
    IReadOnlyList<PortDescriptor> GetOutputPorts(string blockType);
    IPipelineBlock CreateBlock(string blockType, IServiceProvider services);
    IReadOnlyList<string> RegisteredBlockTypes { get; }
}
