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

// Retained for future WebAssembly or external-client scenarios.
public class GroupWatchHub(
    IGroupWatchDiscoveryService discoveryService,
    IUrlValidator urlValidator,
    ILogger<GroupWatchHub> logger) : Hub
{
    private const string DiscoveryInProgressKey = "DiscoveryInProgress";
    private const string DiscoveredUrlsKey = "DiscoveredUrls";

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

    // Diagnostic: verify hub is reachable
    public string Ping() => "pong";

    public async IAsyncEnumerable<GroupWatchProgress> StartDiscovery(
        string userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        Console.WriteLine($"[GroupWatchHub] StartDiscovery ENTERED with: '{userInput}'");
        logger.LogWarning("StartDiscovery ENTERED for connection {ConnectionId} with input '{Input}'",
            Context.ConnectionId, userInput);

        if (Context.Items.ContainsKey(DiscoveryInProgressKey))
        {
            logger.LogWarning(
                "Rejecting concurrent discovery for connection {ConnectionId}",
                Context.ConnectionId);
            yield return new GroupWatchProgress(
                GroupWatchPhase.Complete,
                "Discovery already in progress. Please wait for it to finish before starting another search.",
                0, 0, null, null, null);
            yield break;
        }

        Context.Items[DiscoveryInProgressKey] = true;

        logger.LogInformation(
            "Starting group watch discovery for connection {ConnectionId}",
            Context.ConnectionId);

        // Discovery service is injected directly (hub is transient, gets scoped DI).
        // Run the actual work on a thread-pool thread via Channel to detach from
        // the SignalR hub pipeline and prevent async iterator deadlocks.
        var channel = Channel.CreateUnbounded<GroupWatchProgress>(
            new UnboundedChannelOptions { SingleWriter = true });

        var producerTask = Task.Run(async () =>
        {
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

                try
                {
                    await channel.Writer.WriteAsync(new GroupWatchProgress(
                        Phase: GroupWatchPhase.Complete,
                        Message: $"Discovery failed: {ex.Message}",
                        CompletedCount: 0,
                        TotalCount: 0,
                        Portals: null,
                        GroupId: null,
                        WatchIds: null), CancellationToken.None);
                }
                catch (Exception writeEx) { logger.LogDebug(writeEx, "Channel already completed when writing error"); }
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, ct);

        try
        {
            await foreach (var progress in channel.Reader.ReadAllAsync(ct))
            {
                logger.LogInformation(
                    "Hub yielding progress to client: Phase={Phase}, Message={Message}",
                    progress.Phase, progress.Message);

                if (progress.Portals is { Count: > 0 })
                {
                    Context.Items[DiscoveredUrlsKey] = new HashSet<string>(
                        progress.Portals.Select(p => p.Url),
                        StringComparer.OrdinalIgnoreCase);
                }

                yield return progress;

                // If the server sent a terminal error (Phase=Complete with error message), stop
                if (progress.Phase == GroupWatchPhase.Complete &&
                    progress.Message?.StartsWith("Discovery failed:") == true)
                    yield break;
            }

            // Ensure the producer finishes (propagate any unobserved exceptions)
            await producerTask;
        }
        finally
        {
            Context.Items.Remove(DiscoveryInProgressKey);
        }
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

        if (!Context.Items.TryGetValue(DiscoveredUrlsKey, out var discoveredObj)
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

        // Discovery service injected via hub constructor — no manual scope needed
        var channel = Channel.CreateUnbounded<GroupWatchProgress>(
            new UnboundedChannelOptions { SingleWriter = true });

        var producerTask = Task.Run(async () =>
        {
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

                try
                {
                    await channel.Writer.WriteAsync(new GroupWatchProgress(
                        Phase: GroupWatchPhase.Complete,
                        Message: $"Discovery failed: {ex.Message}",
                        CompletedCount: 0,
                        TotalCount: 0,
                        Portals: null,
                        GroupId: null,
                        WatchIds: null), CancellationToken.None);
                }
                catch (Exception writeEx) { logger.LogDebug(writeEx, "Channel already completed when writing error"); }
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, ct);

        await foreach (var progress in channel.Reader.ReadAllAsync(ct))
        {
            yield return progress;

            if (progress.Phase == GroupWatchPhase.Complete &&
                progress.Message?.StartsWith("Discovery failed:") == true)
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
}
