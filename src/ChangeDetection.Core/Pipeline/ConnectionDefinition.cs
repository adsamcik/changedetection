using System.Text.Json.Serialization;

namespace ChangeDetection.Core.Pipeline;

/// <summary>
/// Represents a connection between two block ports in a pipeline definition.
/// </summary>
public record ConnectionDefinition
{
    /// <summary>Source block instance ID.</summary>
    [JsonPropertyName("fromBlockId")]
    public required string FromBlockId { get; init; }

    /// <summary>Source output port name.</summary>
    [JsonPropertyName("fromPort")]
    public required string FromPort { get; init; }

    /// <summary>Target block instance ID.</summary>
    [JsonPropertyName("toBlockId")]
    public required string ToBlockId { get; init; }

    /// <summary>Target input port name.</summary>
    [JsonPropertyName("toPort")]
    public required string ToPort { get; init; }
}
