using System.Text.Json.Serialization;

namespace ChangeDetection.Core.Pipeline;

/// <summary>
/// Optional metadata for a pipeline definition: display info, creation context, cost estimates.
/// </summary>
public record PipelineMetadata
{
    /// <summary>Human-readable pipeline name.</summary>
    [JsonPropertyName("displayTitle")]
    public string? DisplayTitle { get; init; }

    /// <summary>When the pipeline was assembled.</summary>
    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; init; }

    /// <summary>Original user request that produced this pipeline.</summary>
    [JsonPropertyName("userIntent")]
    public string? UserIntent { get; init; }

    /// <summary>Estimated number of LLM calls per pipeline run.</summary>
    [JsonPropertyName("estimatedLlmCallsPerRun")]
    public int? EstimatedLlmCallsPerRun { get; init; }

    /// <summary>Card type hint: "price", "list", "content", "multiSignal".</summary>
    [JsonPropertyName("cardType")]
    public string? CardType { get; init; }
}
