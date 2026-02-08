using System.Text.Json.Serialization;

namespace ChangeDetection.Core.Pipeline;

/// <summary>
/// Defines a complete pipeline: an ordered list of blocks and the connections between them.
/// Serialized as JSON and stored on WatchedSite.PipelineDefinitionJson.
/// </summary>
public record PipelineDefinition
{
    /// <summary>Schema version for forward compatibility. Currently 1.</summary>
    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    /// <summary>Ordered list of block instances in this pipeline.</summary>
    [JsonPropertyName("blocks")]
    public required IReadOnlyList<BlockDefinition> Blocks { get; init; }

    /// <summary>Wiring between block ports.</summary>
    [JsonPropertyName("connections")]
    public required IReadOnlyList<ConnectionDefinition> Connections { get; init; }

    /// <summary>Optional display and creation metadata.</summary>
    [JsonPropertyName("metadata")]
    public PipelineMetadata? Metadata { get; init; }
}
