using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Persistence;
using ChangeDetection.Services.Search;

namespace ChangeDetection.Services;

/// <summary>
/// Server-side implementation of watch service with direct database access.
/// </summary>
public class ServerWatchService : IWatchService
{
    private readonly LiteDbContext _dbContext;
    private readonly IRepository<WatchedSite> _watchRepo;
    private readonly IRepository<ChangeSnapshot> _snapshotRepo;
    private readonly IRepository<ChangeEvent> _eventRepo;
    private readonly IRepository<FieldValueHistory> _fieldValueHistoryRepo;
    private readonly IRepository<NotificationOutboxEntry> _notificationOutboxRepo;
    private readonly IRepository<BlockExecutionSnapshotEntity> _blockSnapshotRepo;
    private readonly IPriceHistoryRepository _priceHistoryRepo;
    private readonly IContentFetcher _fetcher;
    private readonly IContentExtractor _extractor;
    private readonly IDiffService _diffService;
    private readonly IObjectExtractionService _objectExtractionService;
    private readonly IObjectDiffService _objectDiffService;
    private readonly IErrorResolutionService _errorResolutionService;
    private readonly IFilterEvaluationService _filterEvaluationService;
    private readonly IChangeAnalyzer _changeAnalyzer;
    private readonly IContentEnricher _contentEnricher;
    private readonly IDeduplicationService _deduplicationService;
    private readonly IPriceTrackingService _priceTrackingService;
    private readonly IPiiRedactor? _piiRedactor;
    private readonly IRepository<WatchGroup> _groupRepo;
    private readonly IItemTrackingService _itemTrackingService;
    private readonly ILogger<ServerWatchService> _logger;

    public ServerWatchService(
        LiteDbContext dbContext,
        IRepository<WatchedSite> watchRepo,
        IRepository<ChangeSnapshot> snapshotRepo,
        IRepository<ChangeEvent> eventRepo,
        IRepository<FieldValueHistory> fieldValueHistoryRepo,
        IRepository<NotificationOutboxEntry> notificationOutboxRepo,
        IRepository<BlockExecutionSnapshotEntity> blockSnapshotRepo,
        IPriceHistoryRepository priceHistoryRepo,
        IContentFetcher fetcher,
        IContentExtractor extractor,
        IDiffService diffService,
        IObjectExtractionService objectExtractionService,
        IObjectDiffService objectDiffService,
        IErrorResolutionService errorResolutionService,
        IFilterEvaluationService filterEvaluationService,
        IChangeAnalyzer changeAnalyzer,
        IContentEnricher contentEnricher,
        IDeduplicationService deduplicationService,
        IPriceTrackingService priceTrackingService,
        IRepository<WatchGroup> groupRepo,
        IItemTrackingService itemTrackingService,
        ILogger<ServerWatchService> logger,
        IPiiRedactor? piiRedactor = null)
    {
        _dbContext = dbContext;
        _watchRepo = watchRepo;
        _snapshotRepo = snapshotRepo;
        _eventRepo = eventRepo;
        _fieldValueHistoryRepo = fieldValueHistoryRepo;
        _notificationOutboxRepo = notificationOutboxRepo;
        _blockSnapshotRepo = blockSnapshotRepo;
        _priceHistoryRepo = priceHistoryRepo;
        _fetcher = fetcher;
        _extractor = extractor;
        _diffService = diffService;
        _objectExtractionService = objectExtractionService;
        _objectDiffService = objectDiffService;
        _errorResolutionService = errorResolutionService;
        _filterEvaluationService = filterEvaluationService;
        _changeAnalyzer = changeAnalyzer;
        _contentEnricher = contentEnricher;
        _deduplicationService = deduplicationService;
        _priceTrackingService = priceTrackingService;
        _groupRepo = groupRepo;
        _itemTrackingService = itemTrackingService;
        _logger = logger;
        _piiRedactor = piiRedactor;
    }


    public async Task<WatchedSite> CreateWatchAsync(CreateWatchRequest request, CancellationToken ct = default)
    {
        // Validate GroupId references an existing group
        if (request.GroupId.HasValue)
        {
            var group = await _groupRepo.GetByIdAsync(request.GroupId.Value, ct);
            if (group is null)
                throw new ArgumentException($"WatchGroup {request.GroupId.Value} does not exist");
        }

        // Build schedule settings, defaulting to fixed mode with the check interval
        var scheduleSettings = request.ScheduleSettings ?? new CheckScheduleSettings
        {
            Mode = CheckScheduleMode.Fixed,
            BaseInterval = request.CheckInterval ?? TimeSpan.FromMinutes(30)
        };
        
        // If schedule settings provided, sync the base interval with check interval
        if (request.ScheduleSettings != null && request.CheckInterval.HasValue)
        {
            scheduleSettings.BaseInterval = request.CheckInterval.Value;
        }
        
        var watch = new WatchedSite
        {
            Url = request.Url,
            Name = request.Name ?? ExtractNameFromUrl(request.Url),
            CssSelector = request.CssSelector,
            XPathSelector = request.XPathSelector,
            CheckInterval = scheduleSettings.BaseInterval,
            ScheduleSettings = scheduleSettings,
            Tags = request.Tags ?? [],
            IgnorePatterns = request.IgnorePatterns ?? [],
            TagColors = request.TagColors ?? [],
            CategoryId = request.CategoryId,
            Description = request.Description,
            UserIntent = request.UserIntent,
            GroupId = request.GroupId,
            LlmProviderOverride = request.LlmProviderOverride,
            FetchSettings = request.FetchSettings ?? new FetchSettings
            {
                UseJavaScript = request.UseJavaScript
            },
            Notifications = request.Notifications ?? new NotificationSettings(),
            SchemaEnabled = request.SchemaEnabled,
            Schema = request.Schema,
            FilterRules = request.FilterRules ?? [],
            SourceType = request.SourceType,
            SearchConfig = request.SearchConfig
        };

        // For search watches, auto-generate the pipeline definition
        if (watch.SourceType == SourceType.Search && watch.SearchConfig is not null)
        {
            watch.PipelineDefinitionJson = SearchPipelineBuilder.BuildSearchPipelineJson(watch.SearchConfig);
            watch.Name ??= $"Search: {watch.SearchConfig.Query}";
        }

        await _watchRepo.InsertAsync(watch, ct);
        _logger.LogInformation("Created watch {Id} for {Url}", watch.Id, watch.Url);

        // Perform initial check unless explicitly skipped (e.g., batch seeding)
        if (!request.SkipInitialCheck)
        {
            await CheckForChangesAsync(watch.Id, ct);
        }

        return watch;
    }

    public async Task<WatchedSite?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _watchRepo.GetByIdAsync(id, ct);
    }

    public async Task<IEnumerable<WatchedSite>> GetAllAsync(CancellationToken ct = default)
    {
        return await _watchRepo.GetAllAsync(ct);
    }

    public async Task<IEnumerable<WatchedSite>> GetWatchesDueForCheckAsync(CancellationToken ct = default)
    {
        var all = await _watchRepo.GetAllAsync(ct);
        var now = DateTime.UtcNow;

        return all.Where(w => 
            w.IsEnabled && 
            w.Status != WatchStatus.Checking &&
            (w.LastChecked == null || w.LastChecked.Value.Add(w.CheckInterval) <= now));
    }

    public async Task<IEnumerable<WatchedSite>> GetByTagAsync(string tag, CancellationToken ct = default)
    {
        return await _watchRepo.FindAsync(w => w.Tags.Contains(tag), ct);
    }

    public async Task UpdateWatchAsync(WatchedSite watch, CancellationToken ct = default)
    {
        watch.UpdatedAt = DateTime.UtcNow;
        await _watchRepo.UpdateAsync(watch, ct);
    }

    public async Task DeleteWatchAsync(Guid id, CancellationToken ct = default)
    {
        // Collect screenshot paths before deleting snapshots (file I/O happens after commit)
        var snapshots = await _snapshotRepo.FindAsync(s => s.WatchedSiteId == id, ct);
        var screenshotPaths = snapshots
            .SelectMany(s => new[] { s.ScreenshotPath, s.ElementScreenshotPath })
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();

        var watchIdString = id.ToString();

        // Use a transaction to ensure all deletions succeed or fail together
        // This prevents inconsistent state if watch deletion fails after snapshots/events are deleted
        _dbContext.Database.BeginTrans();
        try
        {
            // Delete associated data
            await _snapshotRepo.DeleteManyAsync(s => s.WatchedSiteId == id, ct);
            await _eventRepo.DeleteManyAsync(e => e.WatchedSiteId == id, ct);
            await _priceHistoryRepo.DeleteForWatchAsync(id, ct);
            await _fieldValueHistoryRepo.DeleteManyAsync(f => f.WatchedSiteId == id, ct);
            await _notificationOutboxRepo.DeleteManyAsync(n => n.WatchedSiteId == id, ct);
            await _blockSnapshotRepo.DeleteManyAsync(b => b.WatchId == watchIdString, ct);
            await _watchRepo.DeleteAsync(id, ct);
            
            _dbContext.Database.Commit();
            _logger.LogInformation("Deleted watch {Id} and associated data", id);
        }
        catch
        {
            _dbContext.Database.Rollback();
            throw;
        }

        // Delete screenshot files outside transaction (file I/O can't be rolled back)
        foreach (var path in screenshotPaths)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path!);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete screenshot file {Path}", path);
            }
        }
    }

    public async Task<ChangeEvent?> CheckForChangesAsync(Guid watchId, CancellationToken ct = default)
    {
        var watch = await _watchRepo.GetByIdAsync(watchId, ct);
        if (watch == null)
        {
            _logger.LogWarning("Watch {Id} not found", watchId);
            return null;
        }

        try
        {
            watch.Status = WatchStatus.Checking;
            await _watchRepo.UpdateAsync(watch, ct);

            // Fetch content
            var screenshotSettings = watch.FetchSettings.Screenshot;
            var fetchOptions = new FetchOptions
            {
                UseJavaScript = watch.FetchSettings.UseJavaScript,
                Headers = watch.FetchSettings.Headers,
                ProxyUrl = watch.FetchSettings.ProxyUrl,
                TimeoutSeconds = watch.FetchSettings.TimeoutSeconds,
                UserAgent = watch.FetchSettings.UserAgent,
                WaitForSelector = watch.FetchSettings.WaitForSelector,
                WaitAfterLoadMs = watch.FetchSettings.WaitAfterLoadMs,
                CaptureScreenshot = screenshotSettings.IsEnabled,
                ViewportWidth = watch.FetchSettings.ViewportWidth,
                ViewportHeight = watch.FetchSettings.ViewportHeight,
                ElementSelector = watch.CssSelector ?? watch.XPathSelector,
                ScreenshotSettings = new ScreenshotCaptureOptions
                {
                    CaptureFullPage = screenshotSettings.Mode is ScreenshotMode.FullPage or ScreenshotMode.FullPageAndElement,
                    CaptureViewport = screenshotSettings.Mode == ScreenshotMode.Viewport,
                    CaptureElement = screenshotSettings.CapturesElement,
                    Format = screenshotSettings.Format == ScreenshotFormat.Jpeg ? "jpeg" : "png",
                    JpegQuality = screenshotSettings.JpegQuality,
                    ElementPadding = screenshotSettings.ElementPadding,
                    HighlightElement = screenshotSettings.HighlightElement,
                    HighlightColor = screenshotSettings.HighlightColor,
                    HighlightBorderWidth = screenshotSettings.HighlightBorderWidth
                }
            };

            var fetchResult = await _fetcher.FetchAsync(watch.Url, fetchOptions, ct);

            if (!fetchResult.IsSuccess)
            {
                watch.Status = WatchStatus.Error;
                watch.LastError = fetchResult.ErrorMessage;
                watch.ConsecutiveFailures++;
                watch.LastChecked = DateTime.UtcNow;
                await _watchRepo.UpdateAsync(watch, ct);
                return null;
            }

            // Extract content
            var extractedContent = _extractor.ExtractText(
                fetchResult.Html!,
                watch.CssSelector,
                watch.XPathSelector);
                
            // Check if selector extraction failed (empty result when selector is configured)
            var hasSelector = !string.IsNullOrEmpty(watch.CssSelector) || !string.IsNullOrEmpty(watch.XPathSelector);
            var extractionFailed = hasSelector && string.IsNullOrWhiteSpace(extractedContent);
            
            if (extractionFailed)
            {
                _logger.LogWarning(
                    "Selector extraction returned empty for watch {Id} ({Url})",
                    watch.Id, watch.Url);
                
                // Attempt auto-resolution if enabled and within attempt limits
                if (watch.AutoErrorResolutionEnabled && 
                    watch.AutoResolutionAttempts < watch.MaxAutoResolutionAttempts)
                {
                    var resolved = await TryAutoResolveExtractionErrorAsync(
                        watch, fetchResult.Html!, extractedContent, ct);
                    
                    if (resolved)
                    {
                        // Reload watch and retry extraction with fixed selector
                        watch = await _watchRepo.GetByIdAsync(watchId, ct);
                        if (watch == null) return null;
                        
                        extractedContent = _extractor.ExtractText(
                            fetchResult.Html!,
                            watch.CssSelector,
                            watch.XPathSelector);
                        
                        // If still empty, the fix didn't work
                        if (string.IsNullOrWhiteSpace(extractedContent))
                        {
                            return await HandleExtractionFailureAsync(
                                watch, "Auto-resolution fix did not extract content", ct);
                        }
                        
                        _logger.LogInformation(
                            "Auto-resolution successful for watch {Id}, selector updated",
                            watch.Id);
                    }
                    else
                    {
                        return await HandleExtractionFailureAsync(
                            watch, "Selector no longer matches page content", ct);
                    }
                }
                else if (watch.AutoResolutionAttempts >= watch.MaxAutoResolutionAttempts)
                {
                    return await HandleExtractionFailureAsync(
                        watch, 
                        $"Selector extraction failed - max auto-resolution attempts ({watch.MaxAutoResolutionAttempts}) reached. Manual intervention required.", 
                        ct);
                }
                else
                {
                    return await HandleExtractionFailureAsync(
                        watch, "Selector no longer matches page content", ct);
                }
            }
            
            // Reset resolution attempts on successful extraction
            if (watch.AutoResolutionAttempts > 0)
            {
                watch.AutoResolutionAttempts = 0;
                watch.LastResolutionDiagnosis = null;
            }

            var contentHash = _extractor.ComputeHash(extractedContent);

            // Deduplication step: Check if content is a duplicate before creating snapshot
            var deduplicationResult = await PerformDeduplicationCheckAsync(
                watch, extractedContent, contentHash, ct);
            
            if (deduplicationResult.IsDuplicate)
            {
                _logger.LogDebug(
                    "Content deduplicated for watch {Id}: {Reason}",
                    watch.Id, deduplicationResult.Reason);
                
                // Update watch status without creating new snapshot
                watch.LastChecked = DateTime.UtcNow;
                watch.Status = WatchStatus.Active;
                watch.LastError = null;
                watch.ConsecutiveFailures = 0;
                await _watchRepo.UpdateAsync(watch, ct);
                
                return null; // No change event - content is duplicate
            }

            // Create snapshot
            var snapshot = new ChangeSnapshot
            {
                OwnerId = watch.OwnerId,
                WatchedSiteId = watch.Id,
                Content = extractedContent,
                ContentHash = contentHash,
                HttpStatusCode = fetchResult.HttpStatusCode,
                FetchDurationMs = fetchResult.DurationMs,
                ContentSizeBytes = extractedContent.Length,
                // Store fingerprint for future deduplication comparisons
                ContentFingerprintJson = deduplicationResult.NewFingerprint != null 
                    ? JsonSerializer.Serialize(deduplicationResult.NewFingerprint)
                    : null
            };

            // Handle schema-based object extraction
            List<ExtractedObject>? extractedObjects = null;
            if (watch.SchemaEnabled && watch.Schema != null)
            {
                var extractionResult = await _objectExtractionService.ExtractAsync(
                    fetchResult.Html!, watch.Schema, ct);

                if (extractionResult.Success && extractionResult.Objects != null)
                {
                    extractedObjects = extractionResult.Objects;
                    snapshot.ExtractedObjectsJson = JsonSerializer.Serialize(extractedObjects);
                    snapshot.SchemaVersion = watch.Schema.Version;
                    snapshot.AmbiguousIdentityWarnings = extractionResult.AmbiguousIdentityWarnings;
                    _logger.LogDebug("Extracted {Count} objects for watch {Id}",
                        extractedObjects.Count, watch.Id);
                }
                else
                {
                    snapshot.SchemaDriftDetected = extractionResult.DriftDetected;
                    snapshot.ExtractionError = extractionResult.Error;
                    _logger.LogWarning(
                        "Object extraction failed for watch {Id}: {Error}, DriftDetected: {Drift}",
                        watch.Id, extractionResult.Error, extractionResult.DriftDetected);
                    
                    // Attempt schema drift auto-recovery
                    if (extractionResult.DriftDetected && watch.AutoErrorResolutionEnabled &&
                        watch.AutoResolutionAttempts < watch.MaxAutoResolutionAttempts)
                    {
                        var schemaDriftResolved = await TryAutoResolveSchemaDriftAsync(
                            watch, fetchResult.Html!, extractionResult, ct);
                        
                        if (schemaDriftResolved)
                        {
                            // Reload watch and retry extraction with updated schema
                            var reloadedWatch = await _watchRepo.GetByIdAsync(watchId, ct);
                            if (reloadedWatch?.Schema != null)
                            {
                                watch = reloadedWatch;
                                var retryResult = await _objectExtractionService.ExtractAsync(
                                    fetchResult.Html!, watch.Schema, ct);
                                
                                if (retryResult.Success && retryResult.Objects != null)
                                {
                                    extractedObjects = retryResult.Objects;
                                    snapshot.ExtractedObjectsJson = JsonSerializer.Serialize(extractedObjects);
                                    snapshot.SchemaVersion = watch.Schema.Version;
                                    snapshot.SchemaDriftDetected = false;
                                    snapshot.ExtractionError = null;
                                    _logger.LogInformation("Schema drift auto-recovery successful for watch {Id}", watch.Id);
                                }
                            }
                        }
                    }
                }
            }

            // Save screenshots if captured
            await SaveScreenshotsAsync(snapshot, fetchResult, watch.FetchSettings.Screenshot, ct);

            // Apply PII redaction before enrichment and storage
            if (_piiRedactor is not null)
            {
                var piiResult = _piiRedactor.Redact(snapshot.Content);
                if (piiResult.RedactionsApplied > 0)
                {
                    snapshot.Content = piiResult.RedactedContent;
                    snapshot.PiiRedactionsApplied = piiResult.RedactionsApplied;
                    snapshot.PiiTypesRedacted = string.Join(",", piiResult.RedactedTypes);
                    _logger.LogInformation("Redacted {Count} PII items ({Types}) from watch {Id}",
                        piiResult.RedactionsApplied, snapshot.PiiTypesRedacted, watch.Id);
                }
            }

            // Perform LLM-powered content enrichment if enabled
            if (watch.AnalysisSettings.EnableContentEnrichment)
            {
                await PerformContentEnrichmentAsync(watch, snapshot, ct);
            }

            await _snapshotRepo.InsertAsync(snapshot, ct);

            // Process price tracking if the watch has currency/price fields
            await ProcessPriceTrackingIfEnabledAsync(watch, fetchResult.Html!, ct);

            // Check for changes
            ChangeEvent? changeEvent = null;
            
            if (watch.LastContentHash != null && watch.LastContentHash != contentHash)
            {
                // Find previous snapshot for diff - use efficient ordered query instead of loading all
                var previousSnapshot = await _snapshotRepo.FirstOrDefaultOrderedDescAsync(
                    s => s.WatchedSiteId == watch.Id && s.Id != snapshot.Id,
                    s => s.CapturedAt,
                    ct);

                if (previousSnapshot != null)
                {
                    // Compute text-based diff as fallback/supplement
                    var diff = _diffService.Compare(previousSnapshot.Content, extractedContent);
                    
                    changeEvent = new ChangeEvent
                    {
                        OwnerId = watch.OwnerId,
                        WatchedSiteId = watch.Id,
                        PreviousSnapshotId = previousSnapshot.Id,
                        CurrentSnapshotId = snapshot.Id,
                        DiffSummary = _diffService.GenerateSummary(diff),
                        DiffHtml = _diffService.GenerateDiffHtml(diff),
                        LinesAdded = diff.LinesAdded,
                        LinesRemoved = diff.LinesRemoved,
                        ChangeType = DetermineChangeType(diff),
                        Importance = DetermineImportance(diff)
                    };

                    // Compute object-level diff if schema extraction is enabled
                    if (watch.SchemaEnabled && watch.Schema != null &&
                        extractedObjects != null &&
                        !string.IsNullOrEmpty(previousSnapshot.ExtractedObjectsJson))
                    {
                        var previousObjects = JsonSerializer.Deserialize<List<ExtractedObject>>(
                            previousSnapshot.ExtractedObjectsJson) ?? [];

                        var objectDiff = await _objectDiffService.ComputeDiffAsync(
                            previousObjects, extractedObjects, watch.Schema, ct);

                        // Score importance if enabled in schema settings
                        if (watch.Schema.DiffSettings?.EnableImportanceScoring == true)
                        {
                            objectDiff = await _objectDiffService.ScoreImportanceAsync(
                                objectDiff, watch.Schema, userIntent: watch.UserIntent, ct);
                        }

                        changeEvent.ObjectsDiff = objectDiff;
                        changeEvent.HasAmbiguousIdentities = objectDiff.HasAmbiguousIdentities;

                        _logger.LogDebug(
                            "Object diff for watch {Id}: +{Added} -{Removed} ~{Modified}",
                            watch.Id, objectDiff.AddedItems.Count,
                            objectDiff.RemovedItems.Count, objectDiff.ModifiedItems.Count);
                    }

                    // Perform LLM-powered change analysis if enabled
                    if (watch.AnalysisSettings.EnableChangeAnalysis)
                    {
                        await PerformChangeAnalysisAsync(
                            watch, changeEvent, diff, previousSnapshot.Content, extractedContent, ct);
                    }

                    // Apply filter rules when object diff and rules are available
                    if (changeEvent.ObjectsDiff is not null && watch.FilterRules.Count > 0)
                    {
                        var filterResult = await _filterEvaluationService.EvaluateAsync(
                            changeEvent.ObjectsDiff, watch.FilterRules, ct);

                        changeEvent.AppliedActions = filterResult.Actions;

                        if (filterResult.SuppressNotification)
                        {
                            changeEvent.IsNotified = true; // mark as already notified to suppress
                        }
                    }

                    await _eventRepo.InsertAsync(changeEvent, ct);

                    // Track individual listings for group watches with schema extraction
                    if (watch.GroupId.HasValue && changeEvent.ObjectsDiff is { HasChanges: true })
                    {
                        try
                        {
                            // If filter rules suppressed the notification, pass null dimensions
                            // so the alert policy defaults to Medium (not High). The filter
                            // suppression itself prevents notification — tracking still records
                            // the item but with a lower alert level.
                            var trackDimensions = changeEvent.IsNotified
                                ? null  // Pre-suppressed by filter → no LLM dimensions to route
                                : changeEvent.MatchDimensionsJson;

                            var trackingResult = await _itemTrackingService.ProcessDiffAsync(
                                watch.GroupId.Value,
                                watch.Id,
                                watch.OwnerId,
                                changeEvent.ObjectsDiff,
                                trackDimensions,
                                ExtractRecommendation(changeEvent.RelevanceReason),
                                changeEvent.Id,
                                watch.Schema?.IdentityFieldNames,
                                ct);

                            if (trackingResult.NewItems.Count > 0 || trackingResult.ConfirmedExpired.Count > 0)
                            {
                                _logger.LogInformation(
                                    "Item tracking for watch {WatchId}: {New} new, {Dup} dedup, {PotExp} potentially expired, {ConfExp} confirmed expired",
                                    watch.Id, trackingResult.NewItems.Count, trackingResult.DuplicateItems.Count,
                                    trackingResult.PotentiallyExpired.Count, trackingResult.ConfirmedExpired.Count);
                            }

                            // Expire any items whose deadlines have passed
                            await _itemTrackingService.ExpirePassedDeadlinesAsync(watch.GroupId.Value, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Item tracking failed for watch {WatchId} — non-fatal, continuing", watch.Id);
                        }
                    }
                    
                    // Update adaptive scheduling metrics when a change is detected
                    UpdateAdaptiveMetrics(watch, watch.LastChanged);
                    watch.LastChanged = DateTime.UtcNow;
                    
                    _logger.LogInformation("Change detected for watch {Id}: {Summary}", 
                        watch.Id, changeEvent.SemanticSummary ?? changeEvent.DiffSummary);
                }
            }

            // Update watch status
            watch.LastChecked = DateTime.UtcNow;
            watch.LastContentHash = contentHash;
            watch.Status = WatchStatus.Active;
            watch.LastError = null;
            watch.ConsecutiveFailures = 0;
            
            // Recalculate adaptive interval based on updated metrics
            RecalculateAdaptiveInterval(watch);
            
            await _watchRepo.UpdateAsync(watch, ct);

            return changeEvent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking watch {Id}", watchId);
            
            watch!.Status = WatchStatus.Error;
            watch.LastError = ex.Message;
            watch.ConsecutiveFailures++;
            watch.LastChecked = DateTime.UtcNow;
            await _watchRepo.UpdateAsync(watch, ct);
            
            return null;
        }
    }

    public async Task EnableWatchAsync(Guid id, CancellationToken ct = default)
    {
        var watch = await _watchRepo.GetByIdAsync(id, ct);
        if (watch != null)
        {
            watch.IsEnabled = true;
            watch.Status = WatchStatus.Active;
            watch.UpdatedAt = DateTime.UtcNow;
            await _watchRepo.UpdateAsync(watch, ct);
        }
    }

    public async Task DisableWatchAsync(Guid id, CancellationToken ct = default)
    {
        var watch = await _watchRepo.GetByIdAsync(id, ct);
        if (watch != null)
        {
            watch.IsEnabled = false;
            watch.Status = WatchStatus.Paused;
            watch.UpdatedAt = DateTime.UtcNow;
            await _watchRepo.UpdateAsync(watch, ct);
        }
    }

    private static string ExtractNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Host.Replace("www.", "");
        }
        catch
        {
            return url;
        }
    }

    private static ChangeType DetermineChangeType(DiffResult diff)
    {
        if (diff.LinesAdded > 0 && diff.LinesRemoved == 0)
            return ChangeType.Added;
        if (diff.LinesRemoved > 0 && diff.LinesAdded == 0)
            return ChangeType.Removed;
        if (diff.LinesAdded > 0 && diff.LinesRemoved > 0)
            return ChangeType.Modified;
        return ChangeType.Unknown;
    }

    private static ChangeImportance DetermineImportance(DiffResult diff)
    {
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
    /// Updates the adaptive scheduling metrics after a change is detected.
    /// Uses exponential moving average to smooth out the change interval.
    /// </summary>
    /// <summary>
    /// Extracts the LLM recommendation (APPLY/REVIEW/SKIP) from the relevance reason string.
    /// The scorer prefixes reason with "[RECOMMENDATION] rest of reason".
    /// </summary>
    private static string? ExtractRecommendation(string? relevanceReason)
    {
        if (string.IsNullOrWhiteSpace(relevanceReason)) return null;
        if (!relevanceReason.StartsWith('[')) return null;
        var end = relevanceReason.IndexOf(']');
        return end > 1 ? relevanceReason[1..end] : null;
    }

    private static void UpdateAdaptiveMetrics(WatchedSite watch, DateTime? previousChangeTime)
    {
        if (watch.ScheduleSettings.Mode != CheckScheduleMode.Adaptive)
            return;
            
        var now = DateTime.UtcNow;
        
        // Only update if we have a previous change time to compare
        if (previousChangeTime.HasValue)
        {
            var timeSinceLastChange = now - previousChangeTime.Value;
            
            // Use exponential moving average with alpha = 0.3
            // This gives recent changes more weight while smoothing out noise
            const double alpha = 0.3;
            
            if (watch.AverageChangeInterval.HasValue)
            {
                var currentAvg = watch.AverageChangeInterval.Value.TotalSeconds;
                var newValue = timeSinceLastChange.TotalSeconds;
                var updatedAvg = (alpha * newValue) + ((1 - alpha) * currentAvg);
                watch.AverageChangeInterval = TimeSpan.FromSeconds(updatedAvg);
            }
            else
            {
                // First recorded interval
                watch.AverageChangeInterval = timeSinceLastChange;
            }
        }
    }

    /// <summary>
    /// Processes price tracking if the watch has Currency or Number fields in its schema.
    /// </summary>
    private async Task ProcessPriceTrackingIfEnabledAsync(
        WatchedSite watch,
        string html,
        CancellationToken ct)
    {
        // Check if the watch has a schema with currency/price-type fields
        if (watch.Schema?.Fields == null || watch.Schema.Fields.Count == 0)
            return;

        var hasPriceFields = watch.Schema.Fields.Any(f =>
            f.Type == FieldType.Currency ||
            (f.Type == FieldType.Number && 
             (f.Name.Contains("price", StringComparison.OrdinalIgnoreCase) ||
              f.Name.Contains("cost", StringComparison.OrdinalIgnoreCase) ||
              f.Name.Contains("value", StringComparison.OrdinalIgnoreCase))));

        if (!hasPriceFields)
            return;

        try
        {
            _logger.LogDebug("Processing price tracking for watch {Id}", watch.Id);
            var result = await _priceTrackingService.ProcessPriceCheckAsync(watch, html, ct);

            if (!result.Success)
            {
                _logger.LogWarning("Price tracking failed for watch {Id}: {Error}", watch.Id, result.Error);
                return;
            }

            _logger.LogDebug(
                "Price tracking completed for watch {Id}: Price={Price} {Currency}, Stock={Stock}",
                watch.Id,
                result.CurrentPrice,
                result.Currency,
                result.CurrentStockStatus);

            if (result.HasPriceChange)
            {
                _logger.LogInformation(
                    "Price change detected for watch {Id}: {OldPrice} -> {NewPrice} ({ChangePercent:F2}%)",
                    watch.Id,
                    result.PreviousPrice,
                    result.CurrentPrice,
                    result.ChangePercent);
            }

            if (result.HasStockChange)
            {
                _logger.LogInformation(
                    "Stock status change detected for watch {Id}: {OldStatus} -> {NewStatus}",
                    watch.Id,
                    result.PreviousStockStatus,
                    result.CurrentStockStatus);
            }
        }
        catch (Exception ex)
        {
            // Price tracking failures should not fail the whole check
            _logger.LogError(ex, "Error during price tracking for watch {Id}", watch.Id);
        }
    }

    /// <summary>
    /// Saves screenshots to disk and updates the snapshot with file paths.
    /// </summary>
    private async Task SaveScreenshotsAsync(
        ChangeSnapshot snapshot,
        FetchResult fetchResult,
        ScreenshotSettings settings,
        CancellationToken ct)
    {
        var screenshotsDir = Path.Combine(Directory.GetCurrentDirectory(), "screenshots");
        Directory.CreateDirectory(screenshotsDir);

        var extension = settings.Format == ScreenshotFormat.Jpeg ? "jpg" : "png";

        // Save full page or viewport screenshot
        if (fetchResult.Screenshot != null)
        {
            var screenshotPath = Path.Combine("screenshots", $"{snapshot.Id}.{extension}");
            var fullPath = Path.Combine(Directory.GetCurrentDirectory(), screenshotPath);
            await File.WriteAllBytesAsync(fullPath, fetchResult.Screenshot, ct);
            snapshot.ScreenshotPath = screenshotPath;
            _logger.LogDebug("Saved page screenshot: {Path} ({Size} bytes)", screenshotPath, fetchResult.Screenshot.Length);
        }

        // Save element screenshot
        if (fetchResult.ElementScreenshot != null)
        {
            var elementScreenshotPath = Path.Combine("screenshots", $"{snapshot.Id}_element.{extension}");
            var fullPath = Path.Combine(Directory.GetCurrentDirectory(), elementScreenshotPath);
            await File.WriteAllBytesAsync(fullPath, fetchResult.ElementScreenshot, ct);
            snapshot.ElementScreenshotPath = elementScreenshotPath;
            _logger.LogDebug("Saved element screenshot: {Path} ({Size} bytes)", elementScreenshotPath, fetchResult.ElementScreenshot.Length);
        }

        // Save element bounding box
        if (fetchResult.ElementBoundingBox != null)
        {
            snapshot.ElementBoundingBoxJson = JsonSerializer.Serialize(new
            {
                x = fetchResult.ElementBoundingBox.X,
                y = fetchResult.ElementBoundingBox.Y,
                width = fetchResult.ElementBoundingBox.Width,
                height = fetchResult.ElementBoundingBox.Height
            });
        }
    }

    /// <summary>
    /// Performs LLM-powered content enrichment on a snapshot.
    /// </summary>
    private async Task PerformContentEnrichmentAsync(
        WatchedSite watch,
        ChangeSnapshot snapshot,
        CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("Performing content enrichment for watch {Id}", watch.Id);

            var request = new ContentEnrichmentRequest
            {
                Content = snapshot.Content,
                Url = watch.Url,
                Title = watch.Name,
                UserIntent = watch.UserIntent,
                WatchId = watch.Id,
                ExtractEntities = watch.AnalysisSettings.ExtractEntities,
                IdentifyTopics = true,
                AnalyzeSentiment = watch.AnalysisSettings.AnalyzeSentiment,
                ExtractStructuredData = true,
                GenerateSummary = true
            };

            var result = await _contentEnricher.EnrichContentAsync(request, ct);

            if (result.IsSuccess)
            {
                snapshot.HasLlmEnrichment = true;
                snapshot.ContentSummary = result.Summary;
                snapshot.ContentType = result.ContentType;
                snapshot.Language = result.Language;
                snapshot.EnrichmentConfidence = result.Confidence;

                if (result.Entities.Count > 0)
                    snapshot.EntitiesJson = JsonSerializer.Serialize(result.Entities);
                if (result.Topics.Count > 0)
                    snapshot.TopicsJson = JsonSerializer.Serialize(result.Topics);
                if (result.Sentiment != null)
                    snapshot.SentimentJson = JsonSerializer.Serialize(result.Sentiment);
                if (result.StructuredData.Count > 0)
                    snapshot.StructuredDataJson = JsonSerializer.Serialize(result.StructuredData);
                if (result.KeyPhrases.Count > 0)
                    snapshot.KeyPhrasesJson = JsonSerializer.Serialize(result.KeyPhrases);

                _logger.LogDebug(
                    "Content enrichment completed for watch {Id}: {Entities} entities, {Topics} topics",
                    watch.Id, result.Entities.Count, result.Topics.Count);
            }
            else
            {
                _logger.LogWarning(
                    "Content enrichment failed for watch {Id}: {Error}",
                    watch.Id, result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Content enrichment error for watch {Id}", watch.Id);
            // Don't fail the whole check just because enrichment failed
        }
    }

    /// <summary>
    /// Performs deduplication check to prevent creating duplicate snapshots.
    /// Uses hash-based and optional semantic fingerprint comparison.
    /// </summary>
    private async Task<DeduplicationResult> PerformDeduplicationCheckAsync(
        WatchedSite watch,
        string extractedContent,
        string contentHash,
        CancellationToken ct)
    {
        // Skip deduplication if this is the first check (no previous content)
        if (string.IsNullOrEmpty(watch.LastContentHash))
        {
            _logger.LogDebug("First check for watch {Id}, skipping deduplication", watch.Id);
            
            // Generate fingerprint if semantic deduplication is enabled
            ContentFingerprint? fingerprint = null;
            if (watch.AnalysisSettings.EnableSemanticDeduplication)
            {
                fingerprint = await _deduplicationService.GenerateFingerprintAsync(
                    extractedContent, ct);
            }
            
            return DeduplicationResult.NotDuplicate(fingerprint);
        }

        // Get previous fingerprint from the last snapshot for semantic comparison
        ContentFingerprint? previousFingerprint = null;
        if (watch.AnalysisSettings.EnableSemanticDeduplication)
        {
            var previousSnapshot = await _snapshotRepo.FirstOrDefaultOrderedDescAsync(
                s => s.WatchedSiteId == watch.Id,
                s => s.CapturedAt,
                ct);

            if (previousSnapshot?.ContentFingerprintJson != null)
            {
                try
                {
                    previousFingerprint = JsonSerializer.Deserialize<ContentFingerprint>(
                        previousSnapshot.ContentFingerprintJson);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to deserialize previous fingerprint for watch {Id}",
                        watch.Id);
                }
            }
        }

        var request = new DeduplicationRequest
        {
            NewContent = extractedContent,
            NewContentHash = contentHash,
            PreviousContentHash = watch.LastContentHash,
            PreviousFingerprint = previousFingerprint,
            WatchId = watch.Id,
            SimilarityThreshold = watch.AnalysisSettings.SemanticSimilarityThreshold,
            UseSemanticComparison = watch.AnalysisSettings.EnableSemanticDeduplication
        };

        return await _deduplicationService.CheckForDuplicateAsync(request, ct);
    }

    /// <summary>
    /// Performs LLM-powered change analysis on a detected change.
    /// </summary>
    private async Task PerformChangeAnalysisAsync(
        WatchedSite watch,
        ChangeEvent changeEvent,
        DiffResult diff,
        string previousContent,
        string currentContent,
        CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("Performing change analysis for watch {Id}", watch.Id);

            // Build diff content for analysis
            var (diffContent, _) = BuildDiffContentForAnalysis(diff);

            // Resolve analysis profile from watch group (if any)
            string? analysisProfileJson = null;
            if (watch.GroupId.HasValue)
            {
                var group = await _groupRepo.GetByIdAsync(watch.GroupId.Value, ct);
                analysisProfileJson = group?.AnalysisProfileJson;
            }

            var request = new ChangeAnalysisRequest
            {
                DiffContent = diffContent,
                PreviousContent = previousContent,
                CurrentContent = currentContent,
                Url = watch.Url,
                WatchName = watch.Name,
                UserIntent = watch.UserIntent,
                AnalysisProfileJson = analysisProfileJson,
                Tags = watch.Tags,
                WatchId = watch.Id,
                LinesAdded = diff.LinesAdded,
                LinesRemoved = diff.LinesRemoved,
                ExtractEntities = watch.AnalysisSettings.ExtractEntities,
                AnalyzeSentiment = watch.AnalysisSettings.AnalyzeSentiment,
                CategorizeChange = watch.AnalysisSettings.CategorizeChanges
            };

            var result = await _changeAnalyzer.AnalyzeChangeAsync(request, ct);

            if (result.IsSuccess)
            {
                changeEvent.HasLlmAnalysis = true;
                changeEvent.SemanticSummary = result.SemanticSummary;
                changeEvent.BriefSummary = result.BriefSummary;
                changeEvent.RelevanceScore = result.RelevanceScore;
                changeEvent.RelevanceReason = result.RelevanceReason;
                changeEvent.AnalysisConfidence = result.Confidence;
                changeEvent.MatchDimensionsJson = result.MatchDimensionsJson;

                if (result.Categories.Count > 0)
                    changeEvent.CategoriesJson = JsonSerializer.Serialize(result.Categories);
                if (!string.IsNullOrWhiteSpace(result.ExtractedEntitiesJson))
                    changeEvent.ExtractedEntitiesJson = result.ExtractedEntitiesJson;
                else if (result.ExtractedEntities.Count > 0)
                    changeEvent.ExtractedEntitiesJson = JsonSerializer.Serialize(result.ExtractedEntities);
                if (result.KeyFacts.Count > 0)
                    changeEvent.KeyFactsJson = JsonSerializer.Serialize(result.KeyFacts);
                if (result.Sentiment != null)
                    changeEvent.SentimentJson = JsonSerializer.Serialize(result.Sentiment);
                if (result.SuggestedActions.Count > 0)
                    changeEvent.SuggestedActionsJson = JsonSerializer.Serialize(result.SuggestedActions);

                // Optionally override importance based on relevance
                if (result.RelevanceScore < 0.3f)
                    changeEvent.Importance = ChangeImportance.Low;
                else if (result.RelevanceScore > 0.8f)
                    changeEvent.Importance = ChangeImportance.High;

                _logger.LogDebug(
                    "Change analysis completed for watch {Id}: relevance={Relevance:P0}, categories={Categories}",
                    watch.Id, result.RelevanceScore, result.Categories.Count);

                // Perform anomaly detection if enabled and we have enough history
                if (watch.AnalysisSettings.DetectAnomalies)
                {
                    await PerformAnomalyDetectionAsync(watch, changeEvent, request, ct);
                }
            }
            else
            {
                _logger.LogWarning(
                    "Change analysis failed for watch {Id}: {Error}",
                    watch.Id, result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Change analysis error for watch {Id}", watch.Id);
            // Don't fail the whole check just because analysis failed
        }
    }

    /// <summary>
    /// Performs anomaly detection by comparing against historical changes.
    /// </summary>
    private async Task PerformAnomalyDetectionAsync(
        WatchedSite watch,
        ChangeEvent changeEvent,
        ChangeAnalysisRequest analysisRequest,
        CancellationToken ct)
    {
        try
        {
            // Get historical changes for pattern comparison
            var historicalEvents = await _eventRepo.FindAsync(
                e => e.WatchedSiteId == watch.Id && e.Id != changeEvent.Id, ct);

            var historicalChanges = historicalEvents
                .OrderByDescending(e => e.DetectedAt)
                .Take(20)
                .Select(e => new HistoricalChange
                {
                    DetectedAt = e.DetectedAt,
                    Summary = e.SemanticSummary ?? e.DiffSummary,
                    LinesChanged = e.LinesAdded + e.LinesRemoved,
                    Categories = !string.IsNullOrEmpty(e.CategoriesJson)
                        ? JsonSerializer.Deserialize<List<ChangeCategory>>(e.CategoriesJson)?
                            .Select(c => c.Name).ToList() ?? []
                        : []
                })
                .ToList();

            if (historicalChanges.Count < 3)
            {
                _logger.LogDebug(
                    "Skipping anomaly detection for watch {Id}: insufficient history ({Count} changes)",
                    watch.Id, historicalChanges.Count);
                return;
            }

            var anomalyRequest = new AnomalyDetectionRequest
            {
                CurrentChange = analysisRequest,
                HistoricalChanges = historicalChanges,
                AverageChangeInterval = watch.AverageChangeInterval,
                TypicalChangeSize = historicalChanges.Count > 0
                    ? (int)historicalChanges.Average(h => h.LinesChanged)
                    : null
            };

            var result = await _changeAnalyzer.DetectAnomaliesAsync(anomalyRequest, ct);

            changeEvent.HasAnomalies = result.HasAnomalies;
            changeEvent.AnomalyScore = result.AnomalyScore;

            if (result.Anomalies.Count > 0)
            {
                changeEvent.AnomaliesJson = JsonSerializer.Serialize(result.Anomalies);

                // Boost importance if significant anomalies detected
                if (result.AnomalyScore > 0.7f)
                {
                    changeEvent.Importance = ChangeImportance.High;
                }

                _logger.LogInformation(
                    "Anomalies detected for watch {Id}: score={Score:P0}, count={Count}",
                    watch.Id, result.AnomalyScore, result.Anomalies.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Anomaly detection error for watch {Id}", watch.Id);
        }
    }

    /// <summary>
    /// Builds a text representation of the diff for LLM analysis.
    /// </summary>
    private static (string Content, bool WasTruncated) BuildDiffContentForAnalysis(DiffResult diff)
    {
        const int maxLines = 500;
        var changedLines = diff.Lines
            .Where(l => l.Type != DiffLineType.Unchanged)
            .ToList();

        var wasTruncated = changedLines.Count > maxLines;
        var sb = new System.Text.StringBuilder();

        foreach (var line in changedLines.Take(maxLines))
        {
            var prefix = line.Type switch
            {
                DiffLineType.Inserted => "+ ",
                DiffLineType.Deleted => "- ",
                DiffLineType.Modified => "~ ",
                _ => "  "
            };

            sb.AppendLine($"{prefix}{line.Text}");
        }

        if (wasTruncated)
        {
            sb.AppendLine($"... [{changedLines.Count - maxLines} more changed lines truncated]");
        }

        return (sb.ToString(), wasTruncated);
    }
    
    /// <summary>
    /// Recalculates the check interval based on adaptive scheduling settings.
    /// The check interval is set to (average change interval / frequency multiplier),
    /// clamped between min and max intervals.
    /// </summary>
    private static void RecalculateAdaptiveInterval(WatchedSite watch)
    {
        if (watch.ScheduleSettings.Mode != CheckScheduleMode.Adaptive)
            return;
            
        if (!watch.AverageChangeInterval.HasValue)
        {
            // Not enough data yet, use base interval
            watch.CheckInterval = watch.ScheduleSettings.BaseInterval;
            return;
        }
        
        var settings = watch.ScheduleSettings;
        var avgChangeSeconds = watch.AverageChangeInterval.Value.TotalSeconds;
        
        // Calculate target interval: check N times as often as content changes
        // E.g., if content changes every 6 hours on average, and multiplier is 3,
        // we should check every 2 hours
        var targetIntervalSeconds = avgChangeSeconds / Math.Max(1.0, settings.FrequencyMultiplier);
        var targetInterval = TimeSpan.FromSeconds(targetIntervalSeconds);
        
        // Clamp to configured bounds
        var clampedInterval = targetInterval;
        if (clampedInterval < settings.MinInterval)
            clampedInterval = settings.MinInterval;
        if (clampedInterval > settings.MaxInterval)
            clampedInterval = settings.MaxInterval;
            
        // Only update if the interval has changed meaningfully (>5% difference)
        var previousInterval = watch.CheckInterval;
        var percentChange = Math.Abs((clampedInterval - previousInterval).TotalSeconds) / previousInterval.TotalSeconds;
        
        if (percentChange > 0.05)
        {
            watch.CheckInterval = clampedInterval;
            watch.LastIntervalAdjustment = DateTime.UtcNow;
        }
    }
    
    /// <summary>
    /// Attempts to auto-resolve an extraction error using LLM.
    /// </summary>
    private async Task<bool> TryAutoResolveExtractionErrorAsync(
        WatchedSite watch,
        string currentHtml,
        string extractedContent,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Attempting auto-resolution for watch {Id} ({Url}), attempt {Attempt} of {Max}",
            watch.Id, watch.Url, watch.AutoResolutionAttempts + 1, watch.MaxAutoResolutionAttempts);
        
        try
        {
            // Get previous successful content for comparison - use efficient ordered query
            var previousSnapshot = await _snapshotRepo.FirstOrDefaultOrderedDescAsync(
                s => s.WatchedSiteId == watch.Id && !string.IsNullOrEmpty(s.Content),
                s => s.CapturedAt,
                ct);
            
            var context = new ErrorResolutionContext
            {
                Watch = watch,
                CurrentHtml = currentHtml,
                ErrorMessage = "Selector returned empty content",
                ErrorType = ErrorType.SelectorNoMatch,
                PreviousContent = previousSnapshot?.Content,
                ConsecutiveFailures = watch.ConsecutiveFailures
            };
            
            var result = await _errorResolutionService.TryResolveAsync(context, ct);
            
            // Update watch with resolution attempt info
            watch.AutoResolutionAttempts++;
            watch.LastResolutionAttempt = DateTime.UtcNow;
            watch.LastResolutionDiagnosis = result.Diagnosis;
            
            if (result.IsResolved && result.AutoFixApplied)
            {
                // Store selector history for potential rollback
                watch.SelectorHistory.Add(new SelectorHistoryEntry
                {
                    ChangedAt = DateTime.UtcNow,
                    PreviousCssSelector = watch.CssSelector,
                    PreviousXPathSelector = watch.XPathSelector,
                    ChangeReason = "Auto-resolution",
                    Diagnosis = result.Diagnosis,
                    Confidence = result.Confidence
                });
                
                // Apply the fix
                if (!string.IsNullOrEmpty(result.NewCssSelector))
                {
                    watch.CssSelector = result.NewCssSelector;
                    watch.XPathSelector = null; // Clear XPath if CSS is now used
                }
                else if (!string.IsNullOrEmpty(result.NewXPathSelector))
                {
                    watch.XPathSelector = result.NewXPathSelector;
                    watch.CssSelector = null; // Clear CSS if XPath is now used
                }
                
                _logger.LogInformation(
                    "Auto-resolution applied for watch {Id}: {Diagnosis}, new selector: {Selector}",
                    watch.Id, result.Diagnosis, 
                    result.NewCssSelector ?? result.NewXPathSelector);
                
                await _watchRepo.UpdateAsync(watch, ct);
                return true;
            }
            else if (result.IsResolved && result.RequiresUserApproval)
            {
                // Store the proposed fix but don't apply it
                _logger.LogInformation(
                    "Auto-resolution found fix for watch {Id} but requires user approval: {Diagnosis}",
                    watch.Id, result.Diagnosis);
                
                watch.LastError = $"Website changed: {result.Diagnosis}. " +
                    $"Proposed fix available (confidence: {result.Confidence:P0}). " +
                    $"Review and approve the selector change.";
                
                await _watchRepo.UpdateAsync(watch, ct);
                return false;
            }
            else
            {
                _logger.LogWarning(
                    "Auto-resolution failed for watch {Id}: {Diagnosis}",
                    watch.Id, result.Diagnosis);
                
                await _watchRepo.UpdateAsync(watch, ct);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during auto-resolution for watch {Id}", watch.Id);
            watch.AutoResolutionAttempts++;
            watch.LastResolutionAttempt = DateTime.UtcNow;
            watch.LastResolutionDiagnosis = $"Resolution error: {ex.Message}";
            await _watchRepo.UpdateAsync(watch, ct);
            return false;
        }
    }
    
    /// <summary>
    /// Attempts to auto-resolve schema drift using LLM to find a corrected item selector.
    /// </summary>
    private async Task<bool> TryAutoResolveSchemaDriftAsync(
        WatchedSite watch,
        string currentHtml,
        ObjectExtractionResult extractionResult,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Attempting schema drift auto-recovery for watch {Id} ({Url}), attempt {Attempt} of {Max}",
            watch.Id, watch.Url, watch.AutoResolutionAttempts + 1, watch.MaxAutoResolutionAttempts);
        
        try
        {
            var previousSnapshot = await _snapshotRepo.FirstOrDefaultOrderedDescAsync(
                s => s.WatchedSiteId == watch.Id && !string.IsNullOrEmpty(s.ExtractedObjectsJson),
                s => s.CapturedAt,
                ct);
            
            var context = new ErrorResolutionContext
            {
                Watch = watch,
                CurrentHtml = currentHtml,
                ErrorMessage = extractionResult.Error ?? "Schema extraction failed due to structure drift",
                ErrorType = ErrorType.SchemaDrift,
                PreviousContent = previousSnapshot?.ExtractedObjectsJson,
                ConsecutiveFailures = watch.ConsecutiveFailures
            };
            
            var result = await _errorResolutionService.TryResolveAsync(context, ct);
            
            watch.AutoResolutionAttempts++;
            watch.LastResolutionAttempt = DateTime.UtcNow;
            watch.LastResolutionDiagnosis = result.Diagnosis;
            
            var newItemSelector = result.NewItemSelector ?? result.NewCssSelector;
            
            if (result.IsResolved && result.AutoFixApplied && !string.IsNullOrEmpty(newItemSelector))
            {
                // Store selector history for potential rollback
                watch.SelectorHistory.Add(new SelectorHistoryEntry
                {
                    ChangedAt = DateTime.UtcNow,
                    PreviousCssSelector = watch.Schema!.ItemSelector,
                    ChangeReason = "Schema drift auto-recovery",
                    Diagnosis = result.Diagnosis,
                    Confidence = result.Confidence
                });
                
                // Apply the fix to the schema
                watch.Schema!.ItemSelector = newItemSelector;
                watch.Schema.Version++;
                
                _logger.LogInformation(
                    "Schema drift auto-recovery applied for watch {Id}: {Diagnosis}, new ItemSelector: {Selector}",
                    watch.Id, result.Diagnosis, newItemSelector);
                
                await _watchRepo.UpdateAsync(watch, ct);
                return true;
            }
            else if (result.IsResolved && result.RequiresUserApproval)
            {
                _logger.LogInformation(
                    "Schema drift recovery found fix for watch {Id} but requires user approval: {Diagnosis}",
                    watch.Id, result.Diagnosis);
                
                watch.LastError = $"Schema drift detected: {result.Diagnosis}. " +
                    $"Proposed fix available (confidence: {result.Confidence:P0}). " +
                    $"Review and approve the schema change.";
                
                await _watchRepo.UpdateAsync(watch, ct);
                return false;
            }
            else
            {
                _logger.LogWarning(
                    "Schema drift auto-recovery failed for watch {Id}: {Diagnosis}",
                    watch.Id, result.Diagnosis);
                
                await _watchRepo.UpdateAsync(watch, ct);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during schema drift auto-recovery for watch {Id}", watch.Id);
            watch.AutoResolutionAttempts++;
            watch.LastResolutionAttempt = DateTime.UtcNow;
            watch.LastResolutionDiagnosis = $"Schema drift recovery error: {ex.Message}";
            await _watchRepo.UpdateAsync(watch, ct);
            return false;
        }
    }
    
    /// <summary>
    /// Handles extraction failure by updating watch status.
    /// </summary>
    private async Task<ChangeEvent?> HandleExtractionFailureAsync(
        WatchedSite watch,
        string errorMessage,
        CancellationToken ct)
    {
        watch.Status = WatchStatus.Error;
        watch.LastError = errorMessage;
        watch.ConsecutiveFailures++;
        watch.LastChecked = DateTime.UtcNow;
        await _watchRepo.UpdateAsync(watch, ct);
        
        _logger.LogWarning(
            "Extraction failure for watch {Id} ({Url}): {Error}",
            watch.Id, watch.Url, errorMessage);
        
        return null;
    }
}
