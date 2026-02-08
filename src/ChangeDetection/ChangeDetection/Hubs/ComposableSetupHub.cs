using System.Runtime.CompilerServices;
using ChangeDetection.Core.Pipeline.Setup;
using Microsoft.AspNetCore.SignalR;

namespace ChangeDetection.Hubs;

/// <summary>
/// SignalR hub that exposes the composable pipeline setup flow to Blazor clients.
/// Streams progress updates during the multi-phase assembly process.
/// </summary>
public class ComposableSetupHub(
    IComposableSetupPipeline setupPipeline,
    ILogger<ComposableSetupHub> logger) : Hub
{
    /// <summary>
    /// Start a new composable pipeline setup session from natural language input.
    /// Streams progress updates through Checkpoint 1 (intent confirmation).
    /// </summary>
    public async IAsyncEnumerable<SetupProgress> StartSetup(
        string userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        logger.LogInformation("Starting composable setup for connection {ConnectionId}", Context.ConnectionId);

        var request = new SetupRequest { UserInput = userInput };

        await foreach (var progress in SafeStream(
            setupPipeline.StartSetupAsync(request, ct), nameof(StartSetup), ct))
        {
            yield return progress;
        }
    }

    /// <summary>
    /// Continue setup after user confirms or refines their intent (Checkpoint 1 response).
    /// Streams progress through pipeline building, dry run, QC, and Checkpoint 2.
    /// </summary>
    public async IAsyncEnumerable<SetupProgress> ConfirmIntent(
        string sessionId,
        bool confirmed,
        string? feedback = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        logger.LogInformation("Checkpoint 1 response for session {SessionId}: confirmed={Confirmed}",
            sessionId, confirmed);

        await foreach (var progress in SafeStream(
            setupPipeline.ConfirmIntentAsync(sessionId, confirmed, feedback, ct), nameof(ConfirmIntent), ct))
        {
            yield return progress;
        }
    }

    /// <summary>
    /// Continue setup after user confirms or refines the pipeline (Checkpoint 2 response).
    /// Streams final saving progress.
    /// </summary>
    public async IAsyncEnumerable<SetupProgress> ConfirmPipeline(
        string sessionId,
        bool confirmed,
        string? feedback = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        logger.LogInformation("Checkpoint 2 response for session {SessionId}: confirmed={Confirmed}",
            sessionId, confirmed);

        await foreach (var progress in SafeStream(
            setupPipeline.ConfirmPipelineAsync(sessionId, confirmed, feedback, ct), nameof(ConfirmPipeline), ct))
        {
            yield return progress;
        }
    }

    /// <summary>
    /// Wraps an async stream with error handling and cancellation logging.
    /// </summary>
    private async IAsyncEnumerable<SetupProgress> SafeStream(
        IAsyncEnumerable<SetupProgress> source,
        string operation,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var enumerator = source.GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                SetupProgress current;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                        yield break;
                    current = enumerator.Current;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    logger.LogInformation("{Operation} cancelled for connection {ConnectionId}",
                        operation, Context.ConnectionId);
                    yield break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error during {Operation} for connection {ConnectionId}",
                        operation, Context.ConnectionId);
                    yield break;
                }

                yield return current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }
}
