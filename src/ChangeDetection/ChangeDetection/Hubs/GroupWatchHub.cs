using System.Runtime.CompilerServices;
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
    ILogger<GroupWatchHub> logger) : Hub
{
    public async IAsyncEnumerable<GroupWatchProgress> StartDiscovery(
        string userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        logger.LogInformation(
            "Starting group watch discovery for connection {ConnectionId}",
            Context.ConnectionId);

        await using var scope = scopeFactory.CreateAsyncScope();
        var discoveryService = scope.ServiceProvider.GetRequiredService<IGroupWatchDiscoveryService>();

        await foreach (var progress in SafeStream(
                           discoveryService.DiscoverAsync(userInput, ct),
                           nameof(StartDiscovery),
                           ct))
        {
            yield return progress;
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

        var discoveredPortals = portals.Select(p => new DiscoveredPortal(
            p.Url,
            ExtractDomain(p.Url),
            p.Reasoning ?? "",
            p.Name,
            p.PlatformId)).ToList();

        await using var scope = scopeFactory.CreateAsyncScope();
        var discoveryService = scope.ServiceProvider.GetRequiredService<IGroupWatchDiscoveryService>();

        await foreach (var progress in SafeStream(
                           discoveryService.CreateWatchesAsync(userInput, discoveredPortals, ct),
                           nameof(ConfirmPortals),
                           ct))
        {
            yield return progress;
        }
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
