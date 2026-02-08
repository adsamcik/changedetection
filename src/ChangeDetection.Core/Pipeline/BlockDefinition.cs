using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChangeDetection.Core.Pipeline;

/// <summary>
/// Represents a single block instance in a pipeline definition.
/// </summary>
public record BlockDefinition
{
    /// <summary>Unique instance ID within the pipeline (e.g., "navigate-1", "filter-1").</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Block type discriminator (e.g., "Navigate", "Filter", "ExtractSchema").</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Block-specific configuration as arbitrary JSON. The block implementation interprets this.</summary>
    [JsonPropertyName("config")]
    public JsonElement? Config { get; init; }

    /// <summary>Display order hint for UI layout.</summary>
    [JsonPropertyName("position")]
    public int? Position { get; init; }
}
