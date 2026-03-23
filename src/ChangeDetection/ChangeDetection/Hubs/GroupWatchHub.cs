using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.AgentInteraction;
using ChangeDetection.Services.GroupWatch;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace ChangeDetection.Hubs;

public sealed record ConfirmedPortalDto(
    string Url,
    string? Name,
    string? PlatformId,
    string? Reasoning);

public class GroupWatchHub(
    IServiceScopeFactory scopeFactory,
    IUrlValidator urlValidator,
    ILogger<GroupWatchHub> logger) : Hub
{
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var expiredQuestions = AskUserService.CleanupConnection(Context.ConnectionId);
        if (expiredQuestions > 0)
        {
            logger.LogInformation(
                "Expired {ExpiredQuestionCount} pending question(s) for disconnected connection {ConnectionId}",
                expiredQuestions,
                Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async IAsyncEnumerable<GroupWatchProgress> StartDiscovery(
        string userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        logger.LogInformation(
            "Starting group watch discovery for connection {ConnectionId}",
            Context.ConnectionId);

        // Run the discovery on a thread-pool thread and pipe results through a Channel.
        // This detaches the async iterator from the SignalR hub pipeline, preventing
        // deadlocks where the iterator's MoveNextAsync continuation and the inner
        // awaits (DB semaphore, LLM calls) block each other.
        var channel = Channel.CreateUnbounded<GroupWatchProgress>(
            new UnboundedChannelOptions { SingleWriter = true });

        var producerTask = Task.Run(async () =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var discoveryService = scope.ServiceProvider
                .GetRequiredService<IGroupWatchDiscoveryService>();

            try
            {
                await foreach (var progress in discoveryService.DiscoverAsync(userInput, ct))
                {
                    await channel.Writer.WriteAsync(progress, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Client disconnected — normal
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Error during {Operation} for connection {ConnectionId}",
                    nameof(StartDiscovery), Context.ConnectionId);

                var errorProgress = new GroupWatchProgress(
                    Phase: GroupWatchPhase.Parsing,
                    Message: $"Discovery failed: {ex.Message}",
                    CompletedCount: null,
                    TotalCount: null,
                    Portals: null,
                    GroupId: null,
                    WatchIds: null);

                await channel.Writer.WriteAsync(errorProgress, CancellationToken.None);
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, ct);

        await foreach (var progress in channel.Reader.ReadAllAsync(ct))
        {
            if (progress.Portals is { Count: > 0 })
            {
                Context.Items["DiscoveredUrls"] = new HashSet<string>(
                    progress.Portals.Select(p => p.Url),
                    StringComparer.OrdinalIgnoreCase);
            }

            yield return progress;

            // If we just yielded an error, stop
            if (progress.Message?.StartsWith("Discovery failed:") == true)
                yield break;
        }

        // Ensure the producer finishes (propagate any unobserved exceptions)
        await producerTask;
    }

    public async IAsyncEnumerable<GroupWatchProgress> ConfirmPortals(
        string userInput,
        List<ConfirmedPortalDto> portals,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        logger.LogInformation(
            "Confirming {PortalCount} portals for connection {ConnectionId}",
            portals.Count,
            Context.ConnectionId);

        if (!Context.Items.TryGetValue("DiscoveredUrls", out var discoveredObj)
            || discoveredObj is not HashSet<string> discoveredUrls)
        {
            logger.LogWarning(
                "Rejecting portal confirmation without discovery state for connection {ConnectionId}",
                Context.ConnectionId);
            yield return new GroupWatchProgress(
                GroupWatchPhase.Complete,
                "Portal confirmation expired. Please run discovery again.",
                0, 0, null, null, null);
            yield break;
        }

        var originalCount = portals.Count;
        portals = portals
            .Where(p => discoveredUrls.Contains(p.Url))
            .ToList();

        if (portals.Count < originalCount)
        {
            logger.LogWarning(
                "Rejected {RejectedCount} portal(s) not found in discovery results for connection {ConnectionId}",
                originalCount - portals.Count,
                Context.ConnectionId);
        }

        if (portals.Count == 0)
        {
            yield return new GroupWatchProgress(
                GroupWatchPhase.Complete,
                "All selected portals were rejected. Please run discovery again.",
                0, 0, null, null, null);
            yield break;
        }

        foreach (var portal in portals)
        {
            var validationError = urlValidator.Validate(portal.Url);
            if (validationError is null)
                continue;

            logger.LogWarning(
                "Rejecting invalid confirmed portal {Url} for connection {ConnectionId}: {Error}",
                portal.Url,
                Context.ConnectionId,
                validationError);
            yield return new GroupWatchProgress(
                GroupWatchPhase.Complete,
                $"Portal confirmation rejected: {validationError}",
                0, 0, null, null, null);
            yield break;
        }

        var discoveredPortals = portals.Select(p => new DiscoveredPortal(
            p.Url,
            ExtractDomain(p.Url),
            p.Reasoning ?? "",
            p.Name,
            p.PlatformId)).ToList();

        // Same Channel pattern as StartDiscovery — detach from SignalR pipeline
        var channel = Channel.CreateUnbounded<GroupWatchProgress>(
            new UnboundedChannelOptions { SingleWriter = true });

        var producerTask = Task.Run(async () =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var discoveryService = scope.ServiceProvider
                .GetRequiredService<IGroupWatchDiscoveryService>();

            try
            {
                await foreach (var progress in discoveryService.CreateWatchesAsync(
                                   userInput, discoveredPortals, ct))
                {
                    await channel.Writer.WriteAsync(progress, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Client disconnected — normal
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Error during {Operation} for connection {ConnectionId}",
                    nameof(ConfirmPortals), Context.ConnectionId);

                var errorProgress = new GroupWatchProgress(
                    Phase: GroupWatchPhase.Parsing,
                    Message: $"Discovery failed: {ex.Message}",
                    CompletedCount: null,
                    TotalCount: null,
                    Portals: null,
                    GroupId: null,
                    WatchIds: null);

                await channel.Writer.WriteAsync(errorProgress, CancellationToken.None);
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, ct);

        await foreach (var progress in channel.Reader.ReadAllAsync(ct))
        {
            yield return progress;

            if (progress.Message?.StartsWith("Discovery failed:") == true)
                yield break;
        }

        await producerTask;
    }

    private static string ExtractDomain(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;
        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }

    private async IAsyncEnumerable<GroupWatchProgress> SafeStream(
        IAsyncEnumerable<GroupWatchProgress> source,
        string operation,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var enumerator = source.GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                GroupWatchProgress current;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                        yield break;

                    current = enumerator.Current;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    logger.LogInformation(
                        "{Operation} cancelled for connection {ConnectionId}",
                        operation,
                        Context.ConnectionId);
                    yield break;
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Error during {Operation} for connection {ConnectionId}",
                        operation,
                        Context.ConnectionId);

                    // Capture error — cannot yield inside catch in C#
                    current = new GroupWatchProgress(
                        Phase: GroupWatchPhase.Parsing,
                        Message: $"Discovery failed: {ex.Message}",
                        CompletedCount: null,
                        TotalCount: null,
                        Portals: null,
                        GroupId: null,
                        WatchIds: null);
                }

                yield return current;

                // If we just yielded an error, stop
                if (current.Message?.StartsWith("Discovery failed:") == true)
                    yield break;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }
}
