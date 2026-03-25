using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Pipeline;

/// <summary>
/// Persists and retrieves the most recent pipeline run summary per watch.
/// </summary>
public interface IPipelineRunSummaryStore
{
    /// <summary>Save or overwrite the latest run summary for a watch.</summary>
    Task SaveAsync(PipelineRunSummaryEntity summary, CancellationToken ct = default);

    /// <summary>Get the latest run summary for a single watch, or null.</summary>
    Task<PipelineRunSummaryEntity?> GetAsync(string watchId, CancellationToken ct = default);

    /// <summary>Get the latest run summaries for multiple watches in one call.</summary>
    Task<IReadOnlyDictionary<string, PipelineRunSummaryEntity>> GetBatchAsync(
        IEnumerable<string> watchIds, CancellationToken ct = default);
}
