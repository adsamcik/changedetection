using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Core.Pipeline;

/// <summary>
/// Execution context passed to every pipeline block during a run.
/// </summary>
public class BlockContext
{
    public required Guid WatchId { get; init; }
    public required DateTime RunTimestamp { get; init; }
    public required string BlockInstanceId { get; init; }
    public required IReadOnlyDictionary<string, JsonElement> Inputs { get; init; }
    public required CancellationToken CancellationToken { get; init; }
    public required ILogger Logger { get; init; }
    public required IBlockStateStore StateStore { get; init; }

    /// <summary>
    /// Shared Playwright page instance. Null when no browser blocks are in the pipeline.
    /// Typed as object because Core does not reference Microsoft.Playwright;
    /// block implementations cast to IPage.
    /// </summary>
    public object? Page { get; init; }

    public required IServiceProvider Services { get; init; }
    public required bool IsFirstRun { get; init; }
    public bool IsDryRun { get; init; }

    /// <summary>
    /// Reference to the pipeline definition for block config access.
    /// </summary>
    public PipelineDefinition? PipelineDefinition { get; init; }

    /// <summary>
    /// All block outputs from the current run, keyed by block instance ID.
    /// Allows Output and Route blocks to access any upstream block's output.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement?>? AllBlockOutputs { get; init; }
}
