using System.Runtime.CompilerServices;
using ChangeDetection.Core.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace ChangeDetection.Hubs;

/// <summary>
/// SignalR hub for streaming aggregate watch group setup progress.
/// Orchestrates multiple per-site pipeline setups via IAggregateSetupPipeline.
/// </summary>
public class AggregateSetupHub(
    IAggregateSetupPipeline aggregatePipeline,
    ILogger<AggregateSetupHub> logger) : Hub
{
    /// <summary>
    /// Start setting up an aggregate watch group from a user intent and list of URLs.
    /// Streams progress as each per-site pipeline runs and schemas are aligned.
    /// </summary>
    public async IAsyncEnumerable<AggregateSetupProgress> StartGroupSetup(
        string userIntent,
        List<string> urls,
        string? groupName = null,
        string? fieldHint = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        logger.LogInformation(
            "Starting aggregate setup for connection {ConnectionId}: {UrlCount} URLs, intent: {Intent}",
            Context.ConnectionId, urls.Count, userIntent);

        var request = new AggregateSetupRequest
        {
            UserIntent = userIntent,
            Urls = urls,
            GroupName = groupName,
            FieldHint = fieldHint
        };

        await foreach (var progress in SafeStream(
            aggregatePipeline.SetupGroupStreamingAsync(request, ct), ct))
        {
            yield return progress;
        }
    }

    private async IAsyncEnumerable<AggregateSetupProgress> SafeStream(
        IAsyncEnumerable<AggregateSetupProgress> source,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var enumerator = source.GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                AggregateSetupProgress current;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                        yield break;
                    current = enumerator.Current;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    logger.LogInformation("Aggregate setup cancelled for connection {ConnectionId}",
                        Context.ConnectionId);
                    yield break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error during aggregate setup for connection {ConnectionId}",
                        Context.ConnectionId);
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
