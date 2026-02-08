namespace ChangeDetection.Core.Pipeline;

/// <summary>
/// Determines error handling behavior for a block type.
/// Infrastructure failures abort; Delivery failures use outbox retry.
/// </summary>
public enum BlockCriticalityTier
{
    /// <summary>Navigate, Wait, Click — abort run on failure.</summary>
    Infrastructure,

    /// <summary>Filter, ExtractSchema, LlmExtract — retry 2x then abort.</summary>
    Extraction,

    /// <summary>Compare blocks, Condition, LlmEvaluate — skip with degraded flag.</summary>
    Analysis,

    /// <summary>Notify — outbox/retry.</summary>
    Delivery
}
