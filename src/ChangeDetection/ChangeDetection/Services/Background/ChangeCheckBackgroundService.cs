using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Hubs;
using ChangeDetection.Services.Authentication;
using ChangeDetection.Services.BlockExecution;
using ChangeDetection.Services.GroupWatch;
using ChangeDetection.Services.Pipeline;
using Microsoft.AspNetCore.SignalR;

namespace ChangeDetection.Services.Background;

/// <summary>
/// Background service that periodically checks watches for changes.
/// Processes all watches across all users using BackgroundServiceUserContext.
/// Concurrency is controlled via MaxConcurrentChecks setting.
/// </summary>
public class ChangeCheckBackgroundService : BackgroundService
{
    private const int DefaultMaxConcurrentChecks = 5;
    
    private readonly IBackgroundServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChangeCheckBackgroundService> _logger;
    private readonly IWatchExecutionLock _executionLock;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);

    public ChangeCheckBackgroundService(
        IBackgroundServiceScopeFactory scopeFactory,
        ILogger<ChangeCheckBackgroundService> logger,
        IWatchExecutionLock executionLock)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _executionLock = executionLock;
    }
    
    /// <summary>
    /// Gets the SignalR group name for a watch owner.
    /// </summary>
    private static string GetDashboardGroup(Guid ownerId)
    {
        // Single-user mode (Guid.Empty) uses global dashboard
        return ownerId == Guid.Empty 
            ? "dashboard" 
            : $"dashboard-{ownerId}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Change check background service starting...");

        using var timer = new PeriodicTimer(_checkInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckPendingWatchesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in change check background service");
            }
        }
    }

    private async Task CheckPendingWatchesAsync(CancellationToken ct)
    {
        // Use background service scope to get admin-level access to all watches
        using var scope = _scopeFactory.CreateBackgroundScope();
        
        var watchService = scope.ServiceProvider.GetRequiredService<IWatchService>();
        var settingsRepo = scope.ServiceProvider.GetRequiredService<IRepository<AppSettings>>();
        var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<ChangeDetectionHub>>();

        var pendingWatches = await watchService.GetWatchesDueForCheckAsync(ct);
        var watchList = pendingWatches.ToList();

        if (watchList.Count == 0)
        {
            return;
        }

        // Load concurrency settings
        var allSettings = await settingsRepo.GetAllAsync(ct);
        var settings = allSettings.FirstOrDefault();
        var maxConcurrent = settings?.MaxConcurrentChecks ?? DefaultMaxConcurrentChecks;
        
        // Clamp to reasonable bounds
        maxConcurrent = Math.Max(1, Math.Min(maxConcurrent, 50));

        _logger.LogInformation(
            "Checking {Count} watches for changes (max concurrent: {MaxConcurrent})", 
            watchList.Count, 
            maxConcurrent);

        // Use semaphore to control concurrency
        using var semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        
        var tasks = watchList.Select(async watch =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                await CheckSingleWatchAsync(scope.ServiceProvider, watch, hubContext, ct);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        // Wait for all checks to complete (or cancel)
        await Task.WhenAll(tasks);
    }

    private async Task CheckSingleWatchAsync(
        IServiceProvider serviceProvider,
        WatchedSite watch,
        IHubContext<ChangeDetectionHub> hubContext,
        CancellationToken ct)
    {
        if (!_executionLock.TryAcquire(watch.Id))
        {
            _logger.LogDebug("Skipping watch {WatchId} — already being checked", watch.Id);
            return;
        }
        try
        {
            // Each watch check gets its own scoped services to avoid thread safety issues
            using var watchScope = serviceProvider.CreateScope();
            var watchService = watchScope.ServiceProvider.GetRequiredService<IWatchService>();
            var notificationService = watchScope.ServiceProvider.GetRequiredService<INotificationService>();
            var eventRepo = watchScope.ServiceProvider.GetRequiredService<IRepository<ChangeEvent>>();
        
            // Determine which SignalR group to broadcast to based on watch owner
            var dashboardGroup = GetDashboardGroup(watch.OwnerId);

            try
            {
                // Broadcast that we're checking this watch
                await hubContext.Clients.Group(dashboardGroup).SendAsync("WatchStatusChanged", new
                {
                    WatchId = watch.Id,
                    WatchName = watch.Name ?? watch.Url,
                    Status = "Checking",
                    LastError = (string?)null,
                    LastCheck = watch.LastChecked
                }, ct);

                // Skip watches that need interactive pipeline setup — they can't be checked
                // until the user configures them via the /setup flow.
                if (watch.NeedsPipelineSetup)
                {
                    _logger.LogDebug(
                        "Skipping watch {WatchId} ({Url}) — needs interactive pipeline setup",
                        watch.Id, watch.Url);
                    return;
                }

                // Auto-generate pipeline for watches without a pipeline definition.
                // Use platform-specific template when the URL matches a known platform (e.g. Workday),
                // otherwise fall back to a basic Navigate+Hash pipeline.
                if (string.IsNullOrEmpty(watch.PipelineDefinitionJson))
                {
                    PipelineDefinition? pipeline = null;
                    var detectedPlatform = SetupFlowEnhancements.DetectPlatformFromUrl(watch.Url);
                    if (detectedPlatform is not null)
                    {
                        var setupFlow = watchScope.ServiceProvider.GetRequiredService<SetupFlowEnhancements>();
                        pipeline = await setupFlow.GetPlatformTemplateAsync(detectedPlatform, watch.Url, ct: ct);
                        if (pipeline is not null)
                        {
                            _logger.LogInformation(
                                "Auto-generated {Platform} platform pipeline for watch {WatchId}",
                                detectedPlatform, watch.Id);
                        }
                    }

                    // For group watches with no detected platform, attempt headless LLM pipeline building.
                    // Only fall back to NeedsPipelineSetup if headless building fails.
                    if (pipeline is null && watch.GroupId.HasValue)
                    {
                        pipeline = await TryBuildLlmPipelineAsync(watchScope.ServiceProvider, watch, ct);

                        if (pipeline is null)
                        {
                            // Guard 4: Provide specific failure reason to the user
                            var isMaxedOut = watch.HeadlessBuildAttempts >= 2;
                            var errorMessage = isMaxedOut
                                ? "Manual setup required — could not automatically identify listings on this page after multiple attempts"
                                : "Could not automatically identify job listings on this page";

                            _logger.LogWarning(
                                "Watch {WatchId} ({Url}) has no pipeline and headless LLM build failed (attempt {Attempt}) — marking as needing setup",
                                watch.Id, watch.Url, watch.HeadlessBuildAttempts);
                            watch.NeedsPipelineSetup = true;
                            watch.LastError = errorMessage;
                            var watchRepo = watchScope.ServiceProvider.GetRequiredService<IRepository<WatchedSite>>();
                            watch.UpdatedAt = DateTime.UtcNow;
                            await watchRepo.UpdateAsync(watch, ct);

                            await hubContext.Clients.Group(dashboardGroup).SendAsync("WatchStatusChanged", new
                            {
                                WatchId = watch.Id,
                                WatchName = watch.Name ?? watch.Url,
                                Status = "NeedsSetup",
                                LastError = errorMessage,
                                LastCheck = DateTime.UtcNow
                            }, ct);

                            return;
                        }
                    }

                    pipeline ??= GenerateBasicPipeline(watch.Url, watch.CssSelector);

                    watch.PipelineDefinitionJson = PipelineSerializer.Serialize(pipeline);
                    var repo = watchScope.ServiceProvider.GetRequiredService<IRepository<WatchedSite>>();
                    watch.UpdatedAt = DateTime.UtcNow;
                    await repo.UpdateAsync(watch, ct);
                }

                await CheckWithPipelineExecutorAsync(watchScope.ServiceProvider, watch, hubContext, dashboardGroup, ct);

                // If this watch belongs to a group, recompute aggregates and evaluate alerts
                if (watch.GroupId.HasValue)
                {
                    await TryEvaluateGroupAggregateAsync(watchScope.ServiceProvider, watch, hubContext, dashboardGroup, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Graceful shutdown, don't log as error
                _logger.LogDebug("Watch check cancelled for {WatchId}", watch.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking watch {WatchId} ({Url})", watch.Id, watch.Url);
            
                // Broadcast error status
                try
                {
                    await hubContext.Clients.Group(dashboardGroup).SendAsync("WatchStatusChanged", new
                    {
                        WatchId = watch.Id,
                        WatchName = watch.Name ?? watch.Url,
                        Status = "Error",
                        LastError = ex.Message,
                        LastCheck = DateTime.UtcNow
                    }, CancellationToken.None); // Use None to ensure error status is broadcast even on cancellation
                }
                catch (Exception broadcastEx)
                {
                    _logger.LogWarning(broadcastEx, "Failed to broadcast error status for watch {WatchId}", watch.Id);
                }
            }
        }
        finally
        {
            _executionLock.Release(watch.Id);
        }
    }

    internal static bool ShouldNotify(NotificationSettings settings, ChangeEvent change, float? minRelevance = null)
    {
        // Check if any notification channel is enabled
        var hasChannel = settings.EmailEnabled || settings.WebhookEnabled || settings.DiscordEnabled;
        
        // Check if change importance meets threshold
        var meetsThreshold = change.Importance >= settings.MinimumImportance;

        // Check if change relevance meets the LLM-derived threshold (when both are set)
        var meetsRelevance = minRelevance is null || change.RelevanceScore is null
            || change.RelevanceScore >= minRelevance;

        return hasChannel && meetsThreshold && meetsRelevance;
    }

    /// <summary>
    /// Generates a basic pipeline definition for legacy watches that don't have one.
    /// Creates: Input → Navigate → ExtractSchema/HashCompare → Output
    /// </summary>
    private static PipelineDefinition GenerateBasicPipeline(string url, string? cssSelector)
    {
        var blocks = new List<BlockDefinition>
        {
            new()
            {
                Id = "input-1",
                Type = "Input",
                Position = 0,
                Config = JsonSerializer.SerializeToElement(new { url })
            },
            new()
            {
                Id = "navigate-1",
                Type = "Navigate",
                Position = 1,
                Config = JsonSerializer.SerializeToElement(new { useJavaScript = true, timeout = 30000 })
            }
        };

        var connections = new List<ConnectionDefinition>
        {
            new()
            {
                FromBlockId = "input-1",
                FromPort = "url",
                ToBlockId = "navigate-1",
                ToPort = "url"
            }
        };

        if (!string.IsNullOrEmpty(cssSelector))
        {
            // CSS selector present → ExtractSchema with selector, then HashCompare
            blocks.Add(new BlockDefinition
            {
                Id = "extract-1",
                Type = "ExtractSchema",
                Position = 2,
                Config = JsonSerializer.SerializeToElement(new { selector = cssSelector })
            });
            blocks.Add(new BlockDefinition
            {
                Id = "hashcompare-1",
                Type = "HashCompare",
                Position = 3
            });
            blocks.Add(new BlockDefinition { Id = "output-1", Type = "Output", Position = 4 });

            connections.Add(new() { FromBlockId = "navigate-1", FromPort = "html", ToBlockId = "extract-1", ToPort = "html" });
            connections.Add(new() { FromBlockId = "extract-1", FromPort = "data", ToBlockId = "hashcompare-1", ToPort = "data" });
            connections.Add(new() { FromBlockId = "hashcompare-1", FromPort = "result", ToBlockId = "output-1", ToPort = "data" });
        }
        else
        {
            // No selector → HashCompare the full page HTML
            blocks.Add(new BlockDefinition
            {
                Id = "hashcompare-1",
                Type = "HashCompare",
                Position = 2
            });
            blocks.Add(new BlockDefinition { Id = "output-1", Type = "Output", Position = 3 });

            connections.Add(new() { FromBlockId = "navigate-1", FromPort = "html", ToBlockId = "hashcompare-1", ToPort = "data" });
            connections.Add(new() { FromBlockId = "hashcompare-1", FromPort = "result", ToBlockId = "output-1", ToPort = "data" });
        }

        return new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks = blocks,
            Connections = connections,
            Metadata = new PipelineMetadata
            {
                DisplayTitle = "Auto-generated basic monitor",
                CreatedAt = DateTime.UtcNow,
                UserIntent = "Basic page monitoring (auto-migrated from legacy watch)",
                EstimatedLlmCallsPerRun = 0
            }
        };
    }

    /// <summary>
    /// Attempts to build a pipeline headlessly via the LLM composable setup pipeline.
    /// Returns the pipeline if successful, or null if the LLM is unavailable or building fails.
    /// On failure the watch should be flagged for interactive setup.
    /// </summary>
    private async Task<PipelineDefinition?> TryBuildLlmPipelineAsync(
        IServiceProvider sp,
        WatchedSite watch,
        CancellationToken ct)
    {
        // Guard 5: Don't retry endlessly — after 2 failed headless build attempts, give up
        const int maxHeadlessBuildAttempts = 2;
        if (watch.HeadlessBuildAttempts >= maxHeadlessBuildAttempts)
        {
            _logger.LogDebug(
                "Skipping headless LLM build for watch {WatchId} — already failed {Attempts} times (max {Max})",
                watch.Id, watch.HeadlessBuildAttempts, maxHeadlessBuildAttempts);
            return null;
        }

        IComposableSetupPipeline? composable;
        try
        {
            composable = sp.GetService<IComposableSetupPipeline>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "IComposableSetupPipeline not available — skipping headless build for {WatchId}", watch.Id);
            return null;
        }

        if (composable is null)
        {
            _logger.LogDebug("IComposableSetupPipeline not registered — skipping headless build for {WatchId}", watch.Id);
            return null;
        }

        try
        {
            _logger.LogInformation(
                "Attempting headless LLM pipeline build for watch {WatchId} ({Url}), attempt {Attempt}",
                watch.Id, watch.Url, watch.HeadlessBuildAttempts + 1);

            var pipeline = await composable.BuildPipelineHeadlessAsync(
                watch.Url, watch.UserIntent, ct);

            if (pipeline is not null)
            {
                _logger.LogInformation(
                    "Headless LLM pipeline built successfully for watch {WatchId}: {BlockCount} blocks",
                    watch.Id, pipeline.Blocks.Count);

                watch.PipelineDefinitionJson = PipelineSerializer.Serialize(pipeline);
                watch.NeedsPipelineSetup = false;
                watch.HeadlessBuildAttempts = 0; // Reset on success
                watch.LastError = null;
                var watchRepo = sp.GetRequiredService<IRepository<WatchedSite>>();
                watch.UpdatedAt = DateTime.UtcNow;
                await watchRepo.UpdateAsync(watch, ct);
            }
            else
            {
                // Null return (guards rejected the pipeline) — increment attempt counter
                watch.HeadlessBuildAttempts++;
                var watchRepo = sp.GetRequiredService<IRepository<WatchedSite>>();
                watch.UpdatedAt = DateTime.UtcNow;
                await watchRepo.UpdateAsync(watch, ct);
            }

            return pipeline;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Headless LLM pipeline build failed for watch {WatchId} ({Url}), attempt {Attempt}",
                watch.Id, watch.Url, watch.HeadlessBuildAttempts + 1);

            // Increment attempt counter on exception too
            watch.HeadlessBuildAttempts++;
            var watchRepo = sp.GetRequiredService<IRepository<WatchedSite>>();
            watch.UpdatedAt = DateTime.UtcNow;
            await watchRepo.UpdateAsync(watch, ct);

            return null;
        }
    }

    /// <summary>
    /// Runs the composable pipeline executor for watches with a PipelineDefinitionJson.
    /// </summary>
    private async Task CheckWithPipelineExecutorAsync(
        IServiceProvider sp,
        WatchedSite watch,
        IHubContext<ChangeDetectionHub> hubContext,
        string dashboardGroup,
        CancellationToken ct)
    {
        var pipelineExecutor = sp.GetRequiredService<IPipelineExecutor>();
        var stateStore = sp.GetRequiredService<IBlockStateStore>();
        var watchRepo = sp.GetRequiredService<IRepository<WatchedSite>>();

        var definition = PipelineSerializer.Deserialize(watch.PipelineDefinitionJson!);
        if (definition is null)
        {
            _logger.LogWarning("Watch {WatchId} has invalid PipelineDefinitionJson, skipping", watch.Id);
            await hubContext.Clients.Group(dashboardGroup).SendAsync("WatchStatusChanged", new
            {
                WatchId = watch.Id,
                WatchName = watch.Name ?? watch.Url,
                Status = "Error",
                LastError = "Invalid pipeline definition",
                LastCheck = DateTime.UtcNow
            }, ct);
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        _logger.LogDebug("Executing composable pipeline for watch {WatchId}", watch.Id);
        var result = await pipelineExecutor.ExecuteAsync(definition, watch.Id, stateStore, page: null, timeoutCts.Token);

        // Persist pipeline run summary for observability
        var summaryStore = sp.GetRequiredService<IPipelineRunSummaryStore>();
        var runSummary = BlockExecution.PipelineRunSummaryBuilder.Build(watch.Id.ToString(), result, definition);
        await summaryStore.SaveAsync(runSummary, ct);

        // Update watch status
        watch.LastChecked = DateTime.UtcNow;
        watch.LastError = result.Success ? null : result.Error;

        if (result.IsDegraded)
        {
            _logger.LogWarning(
                "Pipeline execution for watch {WatchId} completed in DEGRADED state — some blocks may have failed non-critically",
                watch.Id);
        }

        _logger.LogInformation(
            "Pipeline execution for watch {WatchId} completed: Success={Success}, Blocks={BlockCount}, Degraded={Degraded}",
            watch.Id, result.Success, result.BlockResults.Count, result.IsDegraded);

        // --- Persist snapshot & detect changes from pipeline output ---
        ChangeEvent? changeEvent = null;

        if (result.Success && result.OutputData.HasValue)
        {
            var snapshotRepo = sp.GetRequiredService<IRepository<ChangeSnapshot>>();
            var eventRepo = sp.GetRequiredService<IRepository<ChangeEvent>>();
            var diffService = sp.GetRequiredService<IDiffService>();

            // Validate extraction quality for list-mode pipelines (group watches extracting job listings).
            // Filters out navigation items, metadata fragments, and other garbage that generic CSS selectors pick up.
            // Skip validation for known platform templates — their output format is verified by design
            // (e.g., Workday API returns structured JSON that doesn't match HTML-scrape heuristics).
            var outputData = result.OutputData.Value;
            var isKnownPlatform = SetupFlowEnhancements.DetectPlatformFromUrl(watch.Url) is not null;
            if (watch.GroupId.HasValue && outputData.ValueKind == JsonValueKind.Array && !isKnownPlatform)
            {
                var (filteredOutput, totalItems, rejectedCount) = ValidateExtractionQuality(outputData, _logger, watch.Id);

                if (filteredOutput.HasValue)
                    outputData = filteredOutput.Value;

                // If ALL items were rejected, the pipeline is extracting garbage — mark as degraded
                // so the next check can try to rebuild the pipeline via LLM
                if (totalItems > 0 && rejectedCount == totalItems)
                {
                    _logger.LogWarning(
                        "Watch {WatchId} extraction produced only garbage ({Total} items rejected) — marking as NeedsPipelineRebuild",
                        watch.Id, totalItems);
                    watch.ConsecutiveSuccessfulChecks = 0;
                    watch.CatalogStatus = CatalogVerificationStatus.Degraded;
                    watch.PipelineDefinitionJson = null; // Clear pipeline so it gets rebuilt on next check
                    await watchRepo.UpdateAsync(watch, ct);

                    await hubContext.Clients.Group(dashboardGroup).SendAsync("WatchStatusChanged", new
                    {
                        WatchId = watch.Id,
                        WatchName = watch.Name ?? watch.Url,
                        Status = "Degraded",
                        LastError = $"Extraction quality check failed: all {totalItems} extracted items were navigation/metadata, not job listings. Pipeline will be rebuilt on next check.",
                        LastCheck = DateTime.UtcNow
                    }, ct);
                    return;
                }
            }

            var content = outputData.ToString();
            var contentHash = ComputeSha256Hash(content);

            var snapshot = new ChangeSnapshot
            {
                OwnerId = watch.OwnerId,
                WatchedSiteId = watch.Id,
                Content = content,
                ContentHash = contentHash,
                FetchDurationMs = result.ExecutionDurationMs,
                ContentSizeBytes = Encoding.UTF8.GetByteCount(content)
            };

            // Populate ExtractedObjectsJson from ListDiff block output if available
            var listDiffEntry = result.BlockResults
                .FirstOrDefault(kvp => kvp.Key.StartsWith("listdiff", StringComparison.OrdinalIgnoreCase));
            if (listDiffEntry.Value?.Output is { } listDiffOutput)
            {
                snapshot.ExtractedObjectsJson = listDiffOutput.ToString();
            }

            await snapshotRepo.InsertAsync(snapshot, ct);

            // Detect changes: not a baseline run AND hash differs from last known
            if (!result.WasBaseline && watch.LastContentHash != null && watch.LastContentHash != contentHash)
            {
                var previousSnapshot = await snapshotRepo.FirstOrDefaultOrderedDescAsync(
                    s => s.WatchedSiteId == watch.Id && s.Id != snapshot.Id,
                    s => s.CapturedAt,
                    ct);

                if (previousSnapshot != null)
                {
                    var diff = diffService.Compare(previousSnapshot.Content, content);

                    var importance = DetermineImportanceForPipeline(diff, listDiffEntry.Value?.Output);

                    changeEvent = new ChangeEvent
                    {
                        OwnerId = watch.OwnerId,
                        WatchedSiteId = watch.Id,
                        PreviousSnapshotId = previousSnapshot.Id,
                        CurrentSnapshotId = snapshot.Id,
                        DiffSummary = diffService.GenerateSummary(diff),
                        DiffHtml = diffService.GenerateDiffHtml(diff),
                        LinesAdded = diff.LinesAdded,
                        LinesRemoved = diff.LinesRemoved,
                        ChangeType = DetermineChangeTypeFromDiff(diff),
                        Importance = importance
                    };

                    // Attach object-level diff from ListDiff block if present
                    if (listDiffEntry.Value?.Output is { } diffOutput)
                    {
                        try
                        {
                            var objectDiff = ParseListDiffToObjectDiff(diffOutput);
                            if (objectDiff != null)
                            {
                                changeEvent.ObjectsDiff = objectDiff;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to parse ListDiff output as ObjectDiffResult for watch {WatchId}", watch.Id);
                        }
                    }

                    await eventRepo.InsertAsync(changeEvent, ct);

                    watch.LastChanged = DateTime.UtcNow;

                    _logger.LogInformation(
                        "Pipeline change detected for watch {WatchId}: +{Added} -{Removed}, importance={Importance}",
                        watch.Id, diff.LinesAdded, diff.LinesRemoved, importance);

                    // Send notification if configured
                    if (ShouldNotify(watch.Notifications, changeEvent, watch.AnalysisSettings.MinRelevanceForNotification))
                    {
                        try
                        {
                            var notificationService = sp.GetRequiredService<INotificationService>();
                            await notificationService.SendNotificationAsync(watch, changeEvent, ct: ct);
                            changeEvent.IsNotified = true;
                            changeEvent.NotifiedAt = DateTime.UtcNow;
                            await eventRepo.UpdateAsync(changeEvent, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to send notification for pipeline watch {WatchId}", watch.Id);
                        }
                    }
                }
            }

            // Update watch tracking fields
            watch.LastContentHash = contentHash;
            watch.LatestSuccessfulHtml = content;
            watch.ConsecutiveFailures = 0;
            watch.Status = WatchStatus.Active;

            // Reset check interval to base (undo any failure backoff)
            watch.CheckInterval = watch.ScheduleSettings.BaseInterval;

            if (watch.GroupId.HasValue)
            {
                var portalDiscoveryAnalyzer = sp.GetRequiredService<IPortalDiscoveryAnalyzer>();
                var portalSuggestionService = sp.GetRequiredService<IPortalSuggestionService>();
                var suggestions = await portalDiscoveryAnalyzer.AnalyzeForNewPortalsAsync(
                    watch.Id,
                    result.OutputData.Value,
                    ct);

                if (suggestions.Count > 0)
                {
                    var storedCount = await portalSuggestionService.StoreSuggestionsAsync(
                        watch.GroupId.Value,
                        suggestions,
                        ct);

                    if (storedCount > 0)
                    {
                        _logger.LogInformation(
                            "Discovered {Count} new portal suggestions from watch {WatchId}",
                            storedCount,
                            watch.Id);
                    }
                }
            }

            // --- Catalog verification tracking ---

            // Count extracted items, treating empty results ({} or {"items":[],...}) as zero
            var extractedItemCount = result.OutputData.HasValue
                ? CountExtractedItems(result.OutputData.Value)
                : 0;

            if (extractedItemCount > 0)
            {
                watch.ConsecutiveSuccessfulChecks++;
                watch.TotalSuccessfulChecks++;
                watch.LastSuccessfulCheckAt = DateTime.UtcNow;
                watch.TotalItemsExtracted += extractedItemCount;
            }
            else
            {
                // Pipeline succeeded but returned no real data — don't count as verified success.
                // This catches cases like empty {} or {"items":[],"changed":false} appearing healthy.
                _logger.LogWarning(
                    "Watch {WatchId} pipeline succeeded but returned empty data, resetting consecutive success counter",
                    watch.Id);
                watch.ConsecutiveSuccessfulChecks = 0;
            }

            // Promote to Verified after 3+ consecutive successes with extracted items
            if (watch.ConsecutiveSuccessfulChecks >= 3 && watch.TotalItemsExtracted > 0
                && watch.CatalogStatus != CatalogVerificationStatus.Verified)
            {
                watch.CatalogStatus = CatalogVerificationStatus.Verified;
                _logger.LogInformation("Watch {WatchId} promoted to Verified catalog status", watch.Id);
            }

            // Recover from Degraded back to Verified
            if (watch.CatalogStatus == CatalogVerificationStatus.Degraded
                && watch.ConsecutiveSuccessfulChecks >= 3 && watch.TotalItemsExtracted > 0)
            {
                watch.CatalogStatus = CatalogVerificationStatus.Verified;
                _logger.LogInformation("Watch {WatchId} recovered from Degraded to Verified catalog status", watch.Id);
            }
        }
        else if (!result.Success)
        {
            // --- Determine failure category from block results ---
            var failureCategory = FailureCategory.Unknown;
            foreach (var br in result.BlockResults.Values)
            {
                if (!br.Success && br.Category != FailureCategory.None)
                {
                    failureCategory = br.Category;
                    break;
                }
            }

            // --- Catalog verification tracking (failure path) ---
            watch.ConsecutiveSuccessfulChecks = 0;
            watch.TotalFailedChecks++;
            watch.ConsecutiveFailures++;

            // --- Exponential backoff for failing watches (res-3) ---
            // Transient failures get escalating check intervals; permanent failures skip auto-healing
            if (failureCategory != FailureCategory.Permanent)
            {
                var backoffMultiplier = Math.Min(Math.Pow(2, watch.ConsecutiveFailures - 1), 32);
                var baseCheckInterval = watch.ScheduleSettings.BaseInterval;
                watch.CheckInterval = TimeSpan.FromTicks((long)(baseCheckInterval.Ticks * backoffMultiplier));

                _logger.LogInformation(
                    "Watch {WatchId} transient failure #{Failures} — next check in {Interval} ({Multiplier}x backoff)",
                    watch.Id, watch.ConsecutiveFailures, watch.CheckInterval, backoffMultiplier);
            }
            else
            {
                _logger.LogWarning(
                    "Watch {WatchId} permanent failure — skipping auto-healing backoff. Error: {Error}",
                    watch.Id, result.Error);
            }

            // Degrade previously verified watches after 3 consecutive failures
            if (watch.CatalogStatus == CatalogVerificationStatus.Verified && watch.ConsecutiveFailures >= 3)
            {
                watch.CatalogStatus = CatalogVerificationStatus.Degraded;
                _logger.LogWarning("Watch {WatchId} degraded from Verified catalog status after {Failures} consecutive failures",
                    watch.Id, watch.ConsecutiveFailures);
            }

            // Mark as Failed if never verified and 5+ total failures
            if (watch.TotalFailedChecks >= 5 && watch.CatalogStatus is CatalogVerificationStatus.Unverified or CatalogVerificationStatus.Degraded)
            {
                watch.CatalogStatus = CatalogVerificationStatus.Failed;
                _logger.LogWarning("Watch {WatchId} marked as Failed catalog status ({TotalFailed} total failures)",
                    watch.Id, watch.TotalFailedChecks);
            }
        }

        await watchRepo.UpdateAsync(watch, ct);

        await hubContext.Clients.Group(dashboardGroup).SendAsync("WatchStatusChanged", new
        {
            WatchId = watch.Id,
            WatchName = watch.Name ?? watch.Url,
            Status = result.Success ? "Idle" : "Error",
            LastError = result.Error,
            LastCheck = watch.LastChecked
        }, ct);

        // Broadcast change event to dashboard if a change was detected
        if (changeEvent != null)
        {
            await hubContext.Clients.Group(dashboardGroup).SendAsync("ChangeDetected", new
            {
                WatchId = watch.Id,
                WatchName = watch.Name ?? watch.Url,
                ChangeId = changeEvent.Id,
                Summary = changeEvent.BriefSummary ?? changeEvent.DiffSummary,
                DetectedAt = changeEvent.DetectedAt,
                Importance = changeEvent.Importance.ToString(),
                LinesAdded = changeEvent.LinesAdded,
                LinesRemoved = changeEvent.LinesRemoved
            }, ct);
        }
    }

    /// <summary>
    /// Known navigation/boilerplate labels that should never appear as job listing titles.
    /// Case-insensitive exact match.
    /// </summary>
    private static readonly HashSet<string> NavigationLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "home", "about", "about us", "contact", "contact us", "login", "log in", "sign in",
        "register", "search", "menu", "close", "open", "back", "next", "previous",
        "accept", "decline", "cookie", "cookies", "privacy", "terms", "legal",
        "do business", "live & work", "news", "events", "faq", "help",
        "submit", "apply", "cancel", "ok", "yes", "no", "more", "less",
        "share", "print", "download", "upload", "subscribe", "unsubscribe"
    };

    /// <summary>
    /// Validates the quality of extracted items from a pipeline run.
    /// Returns only items that look like real job listings, filtering out navigation elements,
    /// metadata fragments, and other garbage that generic CSS selectors pick up.
    /// </summary>
    /// <param name="outputData">The raw pipeline output (expected to be a JSON array of objects with title/url fields).</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="watchId">Watch ID for log context.</param>
    /// <returns>
    /// A tuple of (validItems, totalItems, rejectedCount). When all items are rejected,
    /// the caller should mark the watch as degraded.
    /// </returns>
    internal static (JsonElement? filteredOutput, int totalItems, int rejectedCount) ValidateExtractionQuality(
        JsonElement outputData, ILogger logger, Guid watchId)
    {
        // Only validate arrays (list-mode extraction output)
        if (outputData.ValueKind != JsonValueKind.Array)
            return (outputData, 0, 0);

        var totalItems = outputData.GetArrayLength();
        if (totalItems == 0)
            return (outputData, 0, 0);

        var validItems = new List<JsonElement>();
        var rejectedCount = 0;

        foreach (var item in outputData.EnumerateArray())
        {
            if (IsValidExtractedItem(item))
            {
                validItems.Add(item.Clone());
            }
            else
            {
                rejectedCount++;
            }
        }

        if (rejectedCount > 0)
        {
            logger.LogInformation(
                "Watch {WatchId} extraction quality filter: {Rejected}/{Total} items rejected as non-job-listing content",
                watchId, rejectedCount, totalItems);
        }

        // If everything was rejected, return empty array
        if (validItems.Count == 0)
        {
            logger.LogWarning(
                "Watch {WatchId} extraction quality filter rejected ALL {Total} items — pipeline is extracting garbage (nav items, metadata, etc.)",
                watchId, totalItems);

            var emptyArray = JsonDocument.Parse("[]").RootElement.Clone();
            return (emptyArray, totalItems, rejectedCount);
        }

        // Rebuild the JSON array with only valid items
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var item in validItems)
            {
                item.WriteTo(writer);
            }
            writer.WriteEndArray();
        }

        var filteredDoc = JsonDocument.Parse(stream.ToArray());
        return (filteredDoc.RootElement.Clone(), totalItems, rejectedCount);
    }

    /// <summary>
    /// Checks whether a single extracted item looks like a real job listing
    /// rather than a navigation element, metadata fragment, or other garbage.
    /// </summary>
    private static bool IsValidExtractedItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return false;

        // Extract title — the primary quality signal
        string? title = null;
        if (item.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
            title = titleEl.GetString()?.Trim();

        if (string.IsNullOrWhiteSpace(title))
            return false;

        // Reject titles that are too short (digits, abbreviations) or too long (HTML fragments)
        if (title.Length < 5 || title.Length > 200)
            return false;

        // Reject known navigation/boilerplate labels
        if (NavigationLabels.Contains(title))
            return false;

        // Reject ALL CAPS single words — typically nav items like "ABOUT", "CONTACT", "NEWS"
        if (!title.Contains(' ') && title == title.ToUpperInvariant() && title.Length < 30)
            return false;

        // Reject titles that are pure numbers or very short numeric strings (page counts, IDs)
        if (int.TryParse(title, out _) || (title.Length <= 3 && title.All(c => char.IsDigit(c) || c == '.')))
            return false;

        // Reject titles that look like hostnames/domains (e.g. "antibiotika.ssi.dk")
        if (title.Count(c => c == '.') >= 2 && !title.Contains(' '))
            return false;

        // Validate URL if present — should look like a job posting URL, not a category/nav page
        if (item.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
        {
            var url = urlEl.GetString();
            if (!string.IsNullOrWhiteSpace(url))
            {
                // Reject fragment-only URLs (e.g. "#", "#section")
                if (url.StartsWith('#') || url == "/")
                    return false;

                // Reject javascript: pseudo-URLs
                if (url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Counts the number of meaningful extracted items in pipeline output.
    /// Returns 0 for empty objects (<c>{}</c>), empty arrays, or objects whose
    /// <c>items</c> array is empty (e.g. <c>{"items":[],"changed":false}</c>).
    /// </summary>
    private static int CountExtractedItems(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array)
            return data.GetArrayLength();

        if (data.ValueKind == JsonValueKind.Object)
        {
            // Empty object {} = no items
            if (!data.EnumerateObject().Any())
                return 0;

            // Object with an items array: count from that array
            if (data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                return items.GetArrayLength();

            // Non-empty object without items array = 1 item
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Computes a SHA-256 hash of the given content string.
    /// </summary>
    private static string ComputeSha256Hash(string content)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hashBytes);
    }

    /// <summary>
    /// Determines the <see cref="ChangeType"/> from a text diff result.
    /// Mirrors the logic in ServerWatchService.DetermineChangeType.
    /// </summary>
    private static ChangeType DetermineChangeTypeFromDiff(DiffResult diff)
    {
        if (diff.LinesAdded > 0 && diff.LinesRemoved == 0)
            return ChangeType.Added;
        if (diff.LinesRemoved > 0 && diff.LinesAdded == 0)
            return ChangeType.Removed;
        if (diff.LinesAdded > 0 && diff.LinesRemoved > 0)
            return ChangeType.Modified;
        return ChangeType.Unknown;
    }

    /// <summary>
    /// Determines change importance for pipeline results.
    /// Uses text diff percentage as base, upgrades to High if ListDiff shows object additions/removals.
    /// </summary>
    private static ChangeImportance DetermineImportanceForPipeline(DiffResult diff, JsonElement? listDiffOutput)
    {
        // Check for object-level additions/removals from ListDiff block
        if (listDiffOutput.HasValue)
        {
            try
            {
                if (listDiffOutput.Value.TryGetProperty("added", out var added) && added.GetArrayLength() > 0)
                    return ChangeImportance.High;
                if (listDiffOutput.Value.TryGetProperty("removed", out var removed) && removed.GetArrayLength() > 0)
                    return ChangeImportance.High;
            }
            catch
            {
                // Malformed output — fall through to text-based heuristic
            }
        }

        // Text-based heuristic (mirrors ServerWatchService.DetermineImportance)
        var totalChanges = diff.LinesAdded + diff.LinesRemoved;
        var totalLines = diff.LinesAdded + diff.LinesRemoved + diff.LinesUnchanged;

        if (totalLines == 0) return ChangeImportance.Low;

        var changePercentage = (double)totalChanges / totalLines * 100;
        return changePercentage switch
        {
            > 50 => ChangeImportance.High,
            > 20 => ChangeImportance.Medium,
            _ => ChangeImportance.Low
        };
    }

    /// <summary>
    /// Parses ListDiff block output into an <see cref="ObjectDiffResult"/>.
    /// Expects a JSON element with "added", "removed", and "modified" arrays.
    /// </summary>
    private static ObjectDiffResult? ParseListDiffToObjectDiff(JsonElement diffOutput)
    {
        var result = new ObjectDiffResult();
        var hasData = false;

        if (diffOutput.TryGetProperty("added", out var added))
        {
            foreach (var item in added.EnumerateArray())
            {
                result.AddedItems.Add(DeserializeToExtractedObject(item));
            }
            hasData |= result.AddedItems.Count > 0;
        }

        if (diffOutput.TryGetProperty("removed", out var removed))
        {
            foreach (var item in removed.EnumerateArray())
            {
                result.RemovedItems.Add(DeserializeToExtractedObject(item));
            }
            hasData |= result.RemovedItems.Count > 0;
        }

        return hasData ? result : null;
    }

    /// <summary>
    /// Converts a JSON element from a ListDiff array into an <see cref="ExtractedObject"/>.
    /// Maps all JSON properties as string fields.
    /// </summary>
    private static ExtractedObject DeserializeToExtractedObject(JsonElement element)
    {
        var obj = new ExtractedObject();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                obj.Fields[prop.Name] = prop.Value.ToString();
            }
        }
        return obj;
    }

    /// <summary>
    /// After a member watch check, recomputes the group aggregate and evaluates cross-site alerts.
    /// Broadcasts AggregateUpdated and AggregateAlertTriggered events via SignalR.
    /// </summary>
    private async Task TryEvaluateGroupAggregateAsync(
        IServiceProvider sp,
        WatchedSite watch,
        IHubContext<ChangeDetectionHub> hubContext,
        string dashboardGroup,
        CancellationToken ct)
    {
        try
        {
            var groupService = sp.GetRequiredService<IWatchGroupService>();
            var groupId = watch.GroupId!.Value;

            var group = await groupService.GetByIdAsync(groupId, ct);
            if (group is null)
            {
                _logger.LogDebug("Watch {WatchId} references group {GroupId} that no longer exists", watch.Id, groupId);
                return;
            }

            var snapshot = await groupService.ComputeAggregateAsync(groupId, ct);

            // Broadcast aggregate update to group subscribers and dashboard
            var updateEvent = new AggregateUpdatedEvent(
                GroupId: groupId,
                GroupName: group.Name,
                TriggerWatchId: watch.Id,
                MemberCount: snapshot.Members.Count,
                ErrorCount: snapshot.Members.Count(m => m.HasErrors),
                ComputedAt: snapshot.ComputedAt);

            await Task.WhenAll(
                hubContext.Clients.Group($"group-{groupId}").SendAsync("AggregateUpdated", updateEvent, ct),
                hubContext.Clients.Group(dashboardGroup).SendAsync("AggregateUpdated", updateEvent, ct));

            // Evaluate aggregate alerts
            if (group.AggregateAlerts.Any(a => a.IsEnabled))
            {
                var alertResult = await groupService.EvaluateAggregateAlertsAsync(groupId, ct);
                foreach (var triggered in alertResult.TriggeredAlerts)
                {
                    var alertEvent = new AggregateAlertTriggeredEvent(
                        GroupId: groupId,
                        GroupName: group.Name,
                        AlertId: triggered.AlertId,
                        FieldName: triggered.FieldName,
                        AggregatedValue: triggered.AggregatedValue,
                        ThresholdValue: triggered.ThresholdValue,
                        Message: triggered.Message,
                        Importance: triggered.Importance.ToString());

                    await Task.WhenAll(
                        hubContext.Clients.Group($"group-{groupId}").SendAsync("AggregateAlertTriggered", alertEvent, ct),
                        hubContext.Clients.Group(dashboardGroup).SendAsync("AggregateAlertTriggered", alertEvent, ct));
                }

                if (alertResult.TriggeredAlerts.Count > 0)
                {
                    _logger.LogInformation(
                        "Group {GroupId} ({GroupName}): {AlertCount} aggregate alert(s) triggered after watch {WatchId} check",
                        groupId, group.Name, alertResult.TriggeredAlerts.Count, watch.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to evaluate group aggregate for watch {WatchId} in group {GroupId}",
                watch.Id, watch.GroupId);
        }
    }
}
