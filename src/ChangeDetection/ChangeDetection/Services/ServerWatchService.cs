using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services;

/// <summary>
/// Server-side implementation of watch service with direct database access.
/// </summary>
public class ServerWatchService : IWatchService
{
    private readonly IRepository<WatchedSite> _watchRepo;
    private readonly IRepository<ChangeSnapshot> _snapshotRepo;
    private readonly IRepository<ChangeEvent> _eventRepo;
    private readonly IContentFetcher _fetcher;
    private readonly IContentExtractor _extractor;
    private readonly IDiffService _diffService;
    private readonly ILogger<ServerWatchService> _logger;

    public ServerWatchService(
        IRepository<WatchedSite> watchRepo,
        IRepository<ChangeSnapshot> snapshotRepo,
        IRepository<ChangeEvent> eventRepo,
        IContentFetcher fetcher,
        IContentExtractor extractor,
        IDiffService diffService,
        ILogger<ServerWatchService> logger)
    {
        _watchRepo = watchRepo;
        _snapshotRepo = snapshotRepo;
        _eventRepo = eventRepo;
        _fetcher = fetcher;
        _extractor = extractor;
        _diffService = diffService;
        _logger = logger;
    }

    public async Task<WatchedSite> CreateWatchAsync(CreateWatchRequest request, CancellationToken ct = default)
    {
        var watch = new WatchedSite
        {
            Url = request.Url,
            Name = request.Name ?? ExtractNameFromUrl(request.Url),
            CssSelector = request.CssSelector,
            XPathSelector = request.XPathSelector,
            CheckInterval = request.CheckInterval ?? TimeSpan.FromMinutes(30),
            Tags = request.Tags ?? [],
            Description = request.Description,
            LlmProviderOverride = request.LlmProviderOverride,
            FetchSettings = request.FetchSettings ?? new FetchSettings
            {
                UseJavaScript = request.UseJavaScript
            },
            Notifications = request.Notifications ?? new NotificationSettings()
        };

        await _watchRepo.InsertAsync(watch, ct);
        _logger.LogInformation("Created watch {Id} for {Url}", watch.Id, watch.Url);

        // Perform initial check
        await CheckForChangesAsync(watch.Id, ct);

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
        // Delete associated snapshots and events
        await _snapshotRepo.DeleteManyAsync(s => s.WatchedSiteId == id, ct);
        await _eventRepo.DeleteManyAsync(e => e.WatchedSiteId == id, ct);
        await _watchRepo.DeleteAsync(id, ct);
        
        _logger.LogInformation("Deleted watch {Id} and associated data", id);
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
            var fetchOptions = new FetchOptions
            {
                UseJavaScript = watch.FetchSettings.UseJavaScript,
                Headers = watch.FetchSettings.Headers,
                ProxyUrl = watch.FetchSettings.ProxyUrl,
                TimeoutSeconds = watch.FetchSettings.TimeoutSeconds,
                UserAgent = watch.FetchSettings.UserAgent,
                WaitForSelector = watch.FetchSettings.WaitForSelector,
                WaitAfterLoadMs = watch.FetchSettings.WaitAfterLoadMs,
                CaptureScreenshot = watch.FetchSettings.CaptureScreenshot,
                ViewportWidth = watch.FetchSettings.ViewportWidth,
                ViewportHeight = watch.FetchSettings.ViewportHeight
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

            var contentHash = _extractor.ComputeHash(extractedContent);

            // Create snapshot
            var snapshot = new ChangeSnapshot
            {
                WatchedSiteId = watch.Id,
                Content = extractedContent,
                ContentHash = contentHash,
                HttpStatusCode = fetchResult.HttpStatusCode,
                FetchDurationMs = fetchResult.DurationMs,
                ContentSizeBytes = extractedContent.Length
            };

            // Save screenshot if captured
            if (fetchResult.Screenshot != null)
            {
                var screenshotPath = Path.Combine("screenshots", $"{snapshot.Id}.png");
                var fullPath = Path.Combine(Directory.GetCurrentDirectory(), screenshotPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllBytesAsync(fullPath, fetchResult.Screenshot, ct);
                snapshot.ScreenshotPath = screenshotPath;
            }

            await _snapshotRepo.InsertAsync(snapshot, ct);

            // Check for changes
            ChangeEvent? changeEvent = null;
            
            if (watch.LastContentHash != null && watch.LastContentHash != contentHash)
            {
                // Find previous snapshot for diff
                var previousSnapshots = await _snapshotRepo.FindAsync(
                    s => s.WatchedSiteId == watch.Id && s.Id != snapshot.Id, ct);
                var previousSnapshot = previousSnapshots
                    .OrderByDescending(s => s.CapturedAt)
                    .FirstOrDefault();

                if (previousSnapshot != null)
                {
                    var diff = _diffService.Compare(previousSnapshot.Content, extractedContent);
                    
                    changeEvent = new ChangeEvent
                    {
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

                    await _eventRepo.InsertAsync(changeEvent, ct);
                    watch.LastChanged = DateTime.UtcNow;
                    
                    _logger.LogInformation("Change detected for watch {Id}: {Summary}", 
                        watch.Id, changeEvent.DiffSummary);
                }
            }

            // Update watch status
            watch.LastChecked = DateTime.UtcNow;
            watch.LastContentHash = contentHash;
            watch.Status = WatchStatus.Active;
            watch.LastError = null;
            watch.ConsecutiveFailures = 0;
            await _watchRepo.UpdateAsync(watch, ct);

            return changeEvent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking watch {Id}", watchId);
            
            watch.Status = WatchStatus.Error;
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
}
