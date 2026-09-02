using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Net;
using System.Threading.Channels;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Services.Pipeline;
using ChangeDetection.Services.Search;
using Microsoft.Extensions.Options;

namespace ChangeDetection.Services.GroupWatch;

public interface IGroupWatchDiscoveryService
{
    IAsyncEnumerable<GroupWatchProgress> DiscoverAsync(
        string userInput,
        CancellationToken ct = default);

    IAsyncEnumerable<GroupWatchProgress> CreateWatchesAsync(
        string userInput,
        List<DiscoveredPortal> confirmedPortals,
        CancellationToken ct = default);
}

public sealed class GroupWatchDiscoveryOptions
{
    public int MaxPortalsPerGroup { get; set; } = 15;
    public int MaxSearchResultsPerQuery { get; set; } = 8;
    public int MaxSearchQueries { get; set; } = 4;
    public TimeSpan DefaultCheckInterval { get; set; } = TimeSpan.FromHours(1);
}

public enum GroupWatchPhase
{
    Parsing,
    Searching,
    Filtering,
    PortalsReady,
    CreatingWatches,
    Complete
}

public sealed record DiscoveredPortal(
    string Url,
    string Domain,
    string Reasoning,
    string? Title = null,
    string? PlatformId = null,
    string? SearchQuery = null,
    Guid? ExistingWatchId = null,
    Guid? ExistingGroupId = null,
    string? ExistingGroupName = null,
    string? ExistingWatchName = null);

public sealed record GroupWatchProgress(
    GroupWatchPhase Phase,
    string Message,
    int? CompletedCount,
    int? TotalCount,
    List<DiscoveredPortal>? Portals,
    Guid? GroupId,
    List<Guid>? WatchIds,
    List<SetupNeededPortal>? NeedsSetupPortals = null);

/// <summary>
/// A portal that was created as a watch but needs interactive pipeline setup.
/// </summary>
public sealed record SetupNeededPortal(
    Guid WatchId,
    string Url,
    string Domain);

public sealed record UrlValidationResult(
    bool IsValid,
    string? Reason,
    int StatusCode);

public partial class GroupWatchDiscoveryService(
    MultiProviderSearchService multiSearch,
    ILlmProviderChain llmProviderChain,
    IWatchGroupService watchGroupService,
    IWatchService watchService,
    SetupFlowEnhancements setupFlowEnhancements,
    IComposableSetupPipeline composableSetupPipeline,
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory serviceScopeFactory,
    IWatchExecutionLock watchExecutionLock,
    ILogger<GroupWatchDiscoveryService> logger,
    IOptions<GroupWatchDiscoveryOptions>? options = null) : IGroupWatchDiscoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly TimeSpan IntentParsingTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PortalClassificationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PipelineBuildTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WarmupBuildTimeout = TimeSpan.FromSeconds(60);
    private const int PipelineBuildConcurrency = 4;

    private readonly GroupWatchDiscoveryOptions _options = options?.Value ?? new GroupWatchDiscoveryOptions();

    public async IAsyncEnumerable<GroupWatchProgress> DiscoverAsync(
        string userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userInput);

        logger.LogInformation("Starting group watch discovery for intent: {Intent}", userInput);

        // Phase 1: Parse intent via LLM — extract location, roles, field, and search queries
        yield return CreateProgress(
            GroupWatchPhase.Parsing,
            "Understanding your request...",
            completedCount: 0,
            totalCount: null);

        StructuredDiscoveryIntent? parsedIntent = null;
        GroupWatchProgress? failureProgress = null;
        try
        {
            using var intentTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            intentTimeout.CancelAfter(IntentParsingTimeout);
            parsedIntent = await ParseIntentAsync(userInput, intentTimeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Intent parsing timed out for discovery request: {Intent}", userInput);
            failureProgress = CreateProgress(
                GroupWatchPhase.Complete,
                "Intent parsing timed out. Please try again.",
                completedCount: 0,
                totalCount: 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Intent parsing failed for: {Intent}", userInput);
            failureProgress = CreateProgress(
                GroupWatchPhase.Complete,
                $"Failed to understand request: {ex.Message}",
                completedCount: 0,
                totalCount: 0);
        }

        if (failureProgress is not null)
        {
            yield return failureProgress;
            yield break;
        }

        logger.LogInformation(
            "Parsed intent: Location={Location}, Roles={Roles}, Field={Field}, Queries={QueryCount}",
            parsedIntent!.Location,
            string.Join(", ", parsedIntent.RoleTypes),
            parsedIntent.Field,
            parsedIntent.SearchQueries.Count);

        yield return CreateProgress(
            GroupWatchPhase.Parsing,
            $"Understood: {BuildIntentSummary(parsedIntent)}",
            completedCount: 1,
            totalCount: null);

        // Phase 2: Web search (PRIMARY) + catalog supplement → LLM classification
        yield return CreateProgress(
            GroupWatchPhase.Searching,
            "Searching for career portals...",
            completedCount: 0,
            totalCount: null);

        List<DiscoveredPortal>? portals = null;
        failureProgress = null;

        var progressChannel = Channel.CreateUnbounded<GroupWatchProgress>();
        var discoveryTask = Task.Run(async () =>
        {
            try
            {
                return await DiscoverPortalsViaSearchAsync(parsedIntent, userInput, ct, progressChannel.Writer);
            }
            finally
            {
                progressChannel.Writer.TryComplete();
            }
        }, ct);

        // Ensure channel completes even if Task.Run rejects the work item (e.g. already-cancelled token)
        _ = discoveryTask.ContinueWith(
            _ => progressChannel.Writer.TryComplete(),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        await foreach (var intermediateProgress in progressChannel.Reader.ReadAllAsync(ct))
        {
            yield return intermediateProgress;
        }

        try
        {
            portals = await discoveryTask;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Portal discovery failed for intent: {Intent}", userInput);
            failureProgress = CreateProgress(
                GroupWatchPhase.Complete,
                $"Failed to discover portals: {ex.Message}",
                completedCount: 0,
                totalCount: 0);
        }

        if (failureProgress is not null)
        {
            yield return failureProgress;
            yield break;
        }

        logger.LogInformation("Discovery produced {Count} portal(s)", portals!.Count);

        yield return CreateProgress(
            GroupWatchPhase.Searching,
            $"Found {portals.Count} career portal(s).",
            completedCount: portals.Count,
            totalCount: portals.Count);

        // Phase 3: Deduplicate and filter existing watches
        var deduplicatedPortals = DeduplicateByDomain(portals)
            .Take(Math.Max(1, _options.MaxPortalsPerGroup))
            .ToList();

        var (newPortals, alreadyWatched) = await FilterExistingWatchesAsync(deduplicatedPortals, ct);
        foreach (var existingPortal in alreadyWatched)
        {
            yield return CreateProgress(
                GroupWatchPhase.Filtering,
                BuildExistingWatchMessage(existingPortal),
                completedCount: newPortals.Count,
                totalCount: deduplicatedPortals.Count);
        }

        var filteredPortals = newPortals;
        var discoverySummaryPortals = alreadyWatched.Count > 0
            ? [.. filteredPortals, .. alreadyWatched]
            : filteredPortals;

        yield return CreateProgress(
            GroupWatchPhase.Filtering,
            alreadyWatched.Count > 0
                ? $"Selected {filteredPortals.Count} new portal(s) for watch creation. Skipped {alreadyWatched.Count} already monitored portal(s)."
                : $"Selected {filteredPortals.Count} portal(s) for watch creation.",
            completedCount: filteredPortals.Count,
            totalCount: deduplicatedPortals.Count,
            portals: discoverySummaryPortals);

        if (filteredPortals.Count == 0)
        {
            yield return CreateProgress(
                GroupWatchPhase.Complete,
                alreadyWatched.Count > 0
                    ? $"All discovered portals are already being monitored. Skipped {alreadyWatched.Count} duplicate portal(s)."
                    : "No suitable career portals were found for that request.",
                completedCount: 0,
                totalCount: deduplicatedPortals.Count,
                portals: discoverySummaryPortals);
            yield break;
        }

        // Emit PortalsReady and stop — watch creation happens via CreateWatchesAsync
        // after the user confirms which portals to include
        yield return CreateProgress(
            GroupWatchPhase.PortalsReady,
            $"Found {filteredPortals.Count} portal(s). Waiting for confirmation.",
            completedCount: filteredPortals.Count,
            totalCount: filteredPortals.Count,
            portals: filteredPortals);
    }

    public async IAsyncEnumerable<GroupWatchProgress> CreateWatchesAsync(
        string userInput,
        List<DiscoveredPortal> confirmedPortals,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userInput);
        ArgumentNullException.ThrowIfNull(confirmedPortals);

        if (confirmedPortals.Count == 0)
        {
            yield return CreateProgress(
                GroupWatchPhase.Complete,
                "No portals were selected.",
                completedCount: 0,
                totalCount: 0);
            yield break;
        }

        logger.LogInformation(
            "Creating watches for {PortalCount} confirmed portals. Intent: {Intent}",
            confirmedPortals.Count,
            userInput);

        var portals = DeduplicateByDomain(confirmedPortals);

        // Re-parse intent from the original user input
        StructuredDiscoveryIntent parsedIntent;
        GroupWatchProgress? parseFailure = null;
        try
        {
            using var intentTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            intentTimeout.CancelAfter(IntentParsingTimeout);
            parsedIntent = await ParseIntentAsync(userInput, intentTimeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Intent re-parsing timed out during watch creation: {Intent}", userInput);
            parseFailure = CreateProgress(
                GroupWatchPhase.Complete,
                "Intent parsing timed out. Please try again.",
                completedCount: 0,
                totalCount: 0);
            parsedIntent = new StructuredDiscoveryIntent();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to re-parse intent for watch creation: {Intent}", userInput);
            parseFailure = CreateProgress(
                GroupWatchPhase.Complete,
                $"Failed to parse intent: {ex.Message}",
                completedCount: 0,
                totalCount: 0);
            parsedIntent = new StructuredDiscoveryIntent();
        }

        if (parseFailure is not null)
        {
            yield return parseFailure;
            yield break;
        }

        var group = await watchGroupService.CreateGroupAsync(new WatchGroupCreateRequest
        {
            Name = BuildGroupName(parsedIntent),
            Description = $"Auto-discovered career portals for: {userInput}",
            UserIntent = userInput,
            AnalysisProfileJson = JsonSerializer.Serialize(parsedIntent, JsonOptions),
            Tags = BuildTags(parsedIntent)
        }, ct);

        logger.LogInformation(
            "Created watch group {GroupId} for discovery intent {Intent}",
            group.Id,
            userInput);

        var positiveKeywords = BuildPositiveKeywords(parsedIntent);
        var negativeKeywords = BuildNegativeKeywords(parsedIntent);
        var watchIds = new List<Guid>();
        var needsSetupPortals = new List<SetupNeededPortal>();

        yield return CreateProgress(
            GroupWatchPhase.CreatingWatches,
            $"Created group '{group.Name}'. Building watches...",
            completedCount: 0,
            totalCount: portals.Count,
            portals: portals,
            groupId: group.Id,
            watchIds: watchIds);

        var preparedPortals = await PreparePortalBuildsAsync(
            portals,
            userInput,
            parsedIntent,
            positiveKeywords,
            negativeKeywords,
            ct);

        for (var i = 0; i < portals.Count; i++)
        {
            var preparedPortal = preparedPortals[i];
            var portal = preparedPortal.Portal;

            // Signal that we're starting this portal so the UI updates immediately
            yield return CreateProgress(
                GroupWatchPhase.CreatingWatches,
                $"Setting up {portal.Domain}...",
                completedCount: i,
                totalCount: portals.Count,
                groupId: group.Id,
                watchIds: [.. watchIds]);

            var validation = preparedPortal.Validation;

            if (validation is { IsValid: false })
            {
                logger.LogWarning(
                    "Skipping portal {PortalUrl} due to failed validation: {Reason} (HTTP {StatusCode})",
                    portal.Url,
                    validation.Reason ?? "URL validation failed",
                    validation.StatusCode);

                yield return CreateProgress(
                    GroupWatchPhase.CreatingWatches,
                    $"⚠️ Skipped {portal.Domain}: {validation.Reason ?? "URL validation failed"}",
                    completedCount: i + 1,
                    totalCount: portals.Count,
                    groupId: group.Id,
                    watchIds: [.. watchIds]);
                continue;
            }

            // Build pipeline (with per-portal timeout) and create watch
            GroupWatchProgress? watchProgress = null;
            try
            {
                if (preparedPortal.Error is not null)
                    throw preparedPortal.Error;

                var pipeline = preparedPortal.Pipeline;

                var watchId = await CreateWatchForPortalAsync(
                    portal,
                    userInput,
                    parsedIntent,
                    group.Id,
                    pipeline,
                    ct);

                watchIds.Add(watchId);

                string pipelineNote;
                if (pipeline is not null)
                {
                    pipelineNote = "";
                }
                else
                {
                    pipelineNote = " (building pipeline in background)";
                    needsSetupPortals.Add(new SetupNeededPortal(watchId, portal.Url, portal.Domain));

                    // Fire-and-forget: immediately queue headless pipeline build
                    // instead of waiting up to 1 min for the background service cycle
                    var warmupWatchId = watchId;
                    var warmupUrl = portal.Url;
                    var warmupIntent = userInput;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            logger.LogInformation(
                                "Warm-up: starting immediate headless build for watch {WatchId} ({Url})",
                                warmupWatchId, warmupUrl);
                            
                            using var scope = serviceScopeFactory.CreateScope();
                            var sp = scope.ServiceProvider;
                            var composable = sp.GetService<IComposableSetupPipeline>();
                            if (composable is null) return;

                            if (watchExecutionLock.IsRunning(warmupWatchId))
                            {
                                logger.LogDebug(
                                    "Warm-up: skipping immediate headless build for watch {WatchId} because another execution already holds the lock",
                                    warmupWatchId);
                                return;
                            }

                            if (!watchExecutionLock.TryAcquire(warmupWatchId))
                            {
                                logger.LogDebug(
                                    "Warm-up: skipping immediate headless build for watch {WatchId} because the execution lock could not be acquired",
                                    warmupWatchId);
                                return;
                            }

                            try
                            {
                                var watchRepo = sp.GetRequiredService<IRepository<WatchedSite>>();
                                var watch = await watchRepo.GetByIdAsync(warmupWatchId, ct);
                                if (watch is null || !string.IsNullOrEmpty(watch.PipelineDefinitionJson))
                                    return;

                                logger.LogInformation(
                                    "Warm-up: attempting immediate headless pipeline build for watch {WatchId} ({Url})",
                                    warmupWatchId, warmupUrl);

                                using var warmupTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                warmupTimeout.CancelAfter(WarmupBuildTimeout);
                                var pipeline = await composable.BuildPipelineHeadlessAsync(
                                    warmupUrl, warmupIntent, warmupTimeout.Token);

                                if (pipeline is not null)
                                {
                                    watch.PipelineDefinitionJson = PipelineSerializer.Serialize(pipeline);
                                    watch.NeedsPipelineSetup = false;
                                    watch.HeadlessBuildAttempts = 0;
                                    watch.LastError = null;
                                    watch.UpdatedAt = DateTime.UtcNow;
                                    await watchRepo.UpdateAsync(watch, ct);

                                    logger.LogInformation(
                                        "Warm-up: headless pipeline built successfully for watch {WatchId}: {BlockCount} blocks",
                                        warmupWatchId, pipeline.Blocks.Count);
                                }
                                else
                                {
                                    watch.HeadlessBuildAttempts++;
                                    watch.UpdatedAt = DateTime.UtcNow;
                                    await watchRepo.UpdateAsync(watch, ct);
                                    
                                    logger.LogInformation(
                                        "Warm-up: headless pipeline build returned null for watch {WatchId}",
                                        warmupWatchId);
                                }
                            }
                            finally
                            {
                                watchExecutionLock.Release(warmupWatchId);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            logger.LogWarning(
                                "Warm-up: headless pipeline build cancelled/timed out for watch {WatchId} — will retry on next background cycle",
                                warmupWatchId);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex,
                                "Warm-up: headless pipeline build failed for watch {WatchId} — will retry on next background cycle",
                                warmupWatchId);
                        }
                    }, ct);
                }

                logger.LogInformation(
                    "Created watch {WatchId} for portal {PortalUrl} in group {GroupId}{Note}",
                    watchId,
                    portal.Url,
                    group.Id,
                    pipelineNote);

                watchProgress = CreateProgress(
                    GroupWatchPhase.CreatingWatches,
                    $"✅ Created watch {i + 1}/{portals.Count}: {portal.Domain}{pipelineNote}",
                    completedCount: i + 1,
                    totalCount: portals.Count,
                    groupId: group.Id,
                    watchIds: [.. watchIds]);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to create watch for portal {PortalUrl}", portal.Url);
                watchProgress = CreateProgress(
                    GroupWatchPhase.CreatingWatches,
                    $"⚠️ Failed {portal.Domain}: {ex.Message}",
                    completedCount: i + 1,
                    totalCount: portals.Count,
                    groupId: group.Id,
                    watchIds: [.. watchIds]);
            }

            if (watchProgress is not null)
                yield return watchProgress;
        }

        var autoCount = watchIds.Count - needsSetupPortals.Count;
        logger.LogInformation(
            "Group watch discovery completed for group {GroupId}. Created {CreatedCount}/{PortalCount} watch(es) ({AutoCount} auto, {SetupCount} need setup)",
            group.Id,
            watchIds.Count,
            portals.Count,
            autoCount,
            needsSetupPortals.Count);

        yield return CreateProgress(
            GroupWatchPhase.Complete,
            $"Created {watchIds.Count} watch(es) from {portals.Count} confirmed portal(s).",
            completedCount: watchIds.Count,
            totalCount: portals.Count,
            portals: portals,
            groupId: group.Id,
            watchIds: [.. watchIds],
            needsSetupPortals: needsSetupPortals.Count > 0 ? needsSetupPortals : null);
    }

    /// <summary>
    /// Parallel discovery: web search and catalog run concurrently as equal sources.
    /// Results are merged, deduped by domain, then LLM classifies the combined set.
    /// </summary>
}
