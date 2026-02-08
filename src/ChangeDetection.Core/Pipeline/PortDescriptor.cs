namespace ChangeDetection.Core.Pipeline;

/// <summary>
/// Describes an input or output port on a pipeline block.
/// </summary>
public record PortDescriptor
{
    public required string Name { get; init; }
    public required PortType Type { get; init; }
    public bool Required { get; init; } = true;
    public string? Description { get; init; }
}
