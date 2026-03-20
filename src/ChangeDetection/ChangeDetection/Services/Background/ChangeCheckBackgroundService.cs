using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Hubs;
using ChangeDetection.Services.Authentication;
using ChangeDetection.Services.GroupWatch;
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
    private static readonly ConcurrentDictionary<Guid, byte> _runningWatches = new();
    
    private readonly IBackgroundServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChangeCheckBackgroundService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);

    public ChangeCheckBackgroundService(
        IBackgroundServiceScopeFactory scopeFactory,
        ILogger<ChangeCheckBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
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
        if (!_runningWatches.TryAdd(watch.Id, 0))
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

                // Auto-generate basic pipeline for legacy watches without a pipeline definition
                if (string.IsNullOrEmpty(watch.PipelineDefinitionJson))
                {
                    var basicPipeline = GenerateBasicPipeline(watch.Url, watch.CssSelector);
                    watch.PipelineDefinitionJson = PipelineSerializer.Serialize(basicPipeline);
                    var watchRepo = watchScope.ServiceProvider.GetRequiredService<IRepository<WatchedSite>>();
                    watch.UpdatedAt = DateTime.UtcNow;
                    await watchRepo.UpdateAsync(watch, ct);
                    _logger.LogInformation("Auto-generated basic pipeline for legacy watch {WatchId}", watch.Id);
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
            _runningWatches.TryRemove(watch.Id, out _);
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

            var content = result.OutputData.Value.ToString();
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
            watch.ConsecutiveSuccessfulChecks++;
            watch.TotalSuccessfulChecks++;
            watch.LastSuccessfulCheckAt = DateTime.UtcNow;

            // Count extracted items from output (arrays contribute their length, anything else counts as 1)
            if (result.OutputData.HasValue)
            {
                var itemCount = result.OutputData.Value.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? result.OutputData.Value.GetArrayLength()
                    : 1;
                watch.TotalItemsExtracted += itemCount;
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
            // --- Catalog verification tracking (failure path) ---
            watch.ConsecutiveSuccessfulChecks = 0;
            watch.TotalFailedChecks++;
            watch.ConsecutiveFailures++;

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
