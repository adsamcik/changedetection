using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for computing diffs between sets of extracted objects.
/// Supports configurable granularity and LLM-based importance scoring.
/// </summary>
public interface IObjectDiffService
{
    /// <summary>
    /// Computes the diff between previous and current extracted objects.
    /// </summary>
    /// <param name="previousObjects">Objects from the previous snapshot.</param>
    /// <param name="currentObjects">Objects from the current snapshot.</param>
    /// <param name="schema">The schema used for extraction (contains identity fields and diff settings).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Diff result with added, removed, and modified items.</returns>
    Task<ObjectDiffResult> ComputeDiffAsync(
        IReadOnlyList<ExtractedObject> previousObjects,
        IReadOnlyList<ExtractedObject> currentObjects,
        ExtractionSchema schema,
        CancellationToken ct = default);

    /// <summary>
    /// Scores the importance of changes using LLM.
    /// </summary>
    /// <param name="diffResult">The diff result to score.</param>
    /// <param name="schema">The schema for context.</param>
    /// <param name="userIntent">Optional user intent for relevance scoring.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Diff result with LLM importance scores populated.</returns>
    Task<ObjectDiffResult> ScoreImportanceAsync(
        ObjectDiffResult diffResult,
        ExtractionSchema schema,
        string? userIntent = null,
        CancellationToken ct = default);
}
