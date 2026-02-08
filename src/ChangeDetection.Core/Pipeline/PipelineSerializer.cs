using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Core.Pipeline;

/// <summary>
/// Static helper for JSON serialization/deserialization of <see cref="PipelineDefinition"/>.
/// </summary>
public static class PipelineSerializer
{
    /// <summary>Maximum allowed JSON size in bytes (1 MB).</summary>
    public const int MaxJsonSizeBytes = 1_048_576;

    /// <summary>Shared options: camelCase, indented, nulls omitted, depth-limited.</summary>
    public static JsonSerializerOptions DefaultOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 64
    };

    /// <summary>Serialize a pipeline definition to a JSON string.</summary>
    public static string Serialize(PipelineDefinition definition) =>
        JsonSerializer.Serialize(definition, DefaultOptions);

    /// <summary>Deserialize a JSON string to a pipeline definition. Returns null on invalid/oversized JSON.</summary>
    public static PipelineDefinition? Deserialize(string json, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            logger?.LogWarning("Pipeline JSON is null or empty");
            return null;
        }

        if (json.Length > MaxJsonSizeBytes)
        {
            logger?.LogWarning("Pipeline JSON exceeds maximum size ({Size} > {Max} bytes)", json.Length, MaxJsonSizeBytes);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PipelineDefinition>(json, DefaultOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            logger?.LogWarning(ex, "Failed to deserialize pipeline JSON");
            return null;
        }
    }
}
