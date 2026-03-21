namespace ChangeDetection.Core.Pipeline.Setup;

/// <summary>
/// Orchestrates the setup of a composable block-based pipeline from natural language input.
/// Streams progress updates during the multi-phase assembly process.
/// </summary>
public interface IComposableSetupPipeline
{
    /// <summary>Start a new setup session from natural language input.</summary>
    IAsyncEnumerable<SetupProgress> StartSetupAsync(
        SetupRequest request,
        CancellationToken ct = default);

    /// <summary>Continue setup after user confirms checkpoint 1 (parsed intent).</summary>
    IAsyncEnumerable<SetupProgress> ConfirmIntentAsync(
        string sessionId,
        bool confirmed,
        string? feedback = null,
        CancellationToken ct = default);

    /// <summary>Continue setup after user confirms checkpoint 2 (assembled pipeline).</summary>
    IAsyncEnumerable<SetupProgress> ConfirmPipelineAsync(
        string sessionId,
        bool confirmed,
        string? feedback = null,
        CancellationToken ct = default);

    /// <summary>
    /// Builds a pipeline headlessly (no user interaction, no SignalR).
    /// Auto-confirms all checkpoints. Returns the pipeline if dry run passes, null if it fails.
    /// Intended for background service use where no UI client is connected.
    /// </summary>
    Task<PipelineDefinition?> BuildPipelineHeadlessAsync(
        string url,
        string? userIntent,
        CancellationToken ct = default);
}
