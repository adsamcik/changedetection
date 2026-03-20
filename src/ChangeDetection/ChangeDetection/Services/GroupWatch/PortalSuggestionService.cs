using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Pipeline;

namespace ChangeDetection.Services.GroupWatch;

public interface IPortalSuggestionService
{
    Task<int> StoreSuggestionsAsync(Guid groupId, IEnumerable<PortalSuggestion> suggestions, CancellationToken ct = default);
    Task<List<PortalSuggestionEntity>> GetPendingForGroupAsync(Guid groupId, CancellationToken ct = default);
    Task<WatchedSite?> AcceptAsync(Guid groupId, Guid suggestionId, CancellationToken ct = default);
    Task<bool> DismissAsync(Guid groupId, Guid suggestionId, CancellationToken ct = default);
}

public class PortalSuggestionService(
    IRepository<PortalSuggestionEntity> suggestionRepo,
    IRepository<WatchedSite> watchRepo,
    IWatchService watchService,
    IWatchGroupService watchGroupService,
    SetupFlowEnhancements setupFlowEnhancements,
    ILogger<PortalSuggestionService> logger) : IPortalSuggestionService
{
    public async Task<int> StoreSuggestionsAsync(
        Guid groupId,
        IEnumerable<PortalSuggestion> suggestions,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(suggestions);

        var pendingSuggestions = (await suggestionRepo.FindAsync(
            s => s.GroupId == groupId && s.Status == SuggestionStatus.Pending,
            ct)).ToList();

        var existingUrls = pendingSuggestions
            .Select(s => s.Url)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sourceWatchOwners = new Dictionary<Guid, Guid>();
        foreach (var sourceWatchId in suggestions.Select(s => s.SourceWatchId).Distinct())
        {
            var sourceWatch = await watchRepo.GetByIdAsync(sourceWatchId, ct);
            if (sourceWatch is not null)
                sourceWatchOwners[sourceWatchId] = sourceWatch.OwnerId;
        }

        var toInsert = new List<PortalSuggestionEntity>();
        foreach (var suggestion in suggestions)
        {
            if (existingUrls.Contains(suggestion.Url))
                continue;

            sourceWatchOwners.TryGetValue(suggestion.SourceWatchId, out var ownerId);
            toInsert.Add(new PortalSuggestionEntity
            {
                OwnerId = ownerId,
                Url = suggestion.Url,
                Domain = suggestion.Domain,
                DetectedPlatform = suggestion.DetectedPlatform,
                Reason = suggestion.Reason,
                SourceWatchId = suggestion.SourceWatchId,
                GroupId = groupId
            });
            existingUrls.Add(suggestion.Url);
        }

        if (toInsert.Count == 0)
            return 0;

        await suggestionRepo.InsertManyAsync(toInsert, ct);
        return toInsert.Count;
    }

    public async Task<List<PortalSuggestionEntity>> GetPendingForGroupAsync(Guid groupId, CancellationToken ct = default)
    {
        return (await suggestionRepo.FindAsync(
                suggestion => suggestion.GroupId == groupId && suggestion.Status == SuggestionStatus.Pending,
                ct))
            .OrderByDescending(suggestion => suggestion.CreatedAt)
            .ToList();
    }

    public async Task<WatchedSite?> AcceptAsync(Guid groupId, Guid suggestionId, CancellationToken ct = default)
    {
        var suggestion = await suggestionRepo.GetByIdAsync(suggestionId, ct);
        if (suggestion is null ||
            suggestion.GroupId != groupId ||
            suggestion.Status != SuggestionStatus.Pending)
        {
            return null;
        }

        var group = await watchGroupService.GetByIdAsync(groupId, ct);
        if (group is null)
            return null;

        var detectedPlatform = suggestion.DetectedPlatform
                               ?? SetupFlowEnhancements.DetectPlatformFromUrl(suggestion.Url);
        PipelineDefinition? pipeline = null;
        if (!string.IsNullOrWhiteSpace(detectedPlatform))
        {
            pipeline = await setupFlowEnhancements.GetPlatformTemplateAsync(
                detectedPlatform,
                suggestion.Url,
                ct: ct);
        }

        var sourceWatch = await watchRepo.GetByIdAsync(suggestion.SourceWatchId, ct);
        var useJavaScript = pipeline is not null && DetectUseJavaScript(pipeline);

        var watch = await watchService.CreateWatchAsync(new CreateWatchRequest
        {
            Url = suggestion.Url,
            Name = BuildWatchName(suggestion, sourceWatch),
            Description = BuildDescription(suggestion, sourceWatch),
            GroupId = groupId,
            UserIntent = group.UserIntent,
            Tags = BuildWatchTags(group, suggestion),
            UseJavaScript = useJavaScript,
            SkipInitialCheck = true
        }, ct);

        if (pipeline is not null)
        {
            watch.PipelineDefinitionJson = PipelineSerializer.Serialize(pipeline);
            watch.FetchSettings ??= new FetchSettings();
            watch.FetchSettings.UseJavaScript = useJavaScript;
            await watchService.UpdateWatchAsync(watch, ct);
        }

        suggestion.Status = SuggestionStatus.Accepted;
        await suggestionRepo.UpdateAsync(suggestion, ct);

        logger.LogInformation(
            "Accepted portal suggestion {SuggestionId} into group {GroupId} as watch {WatchId}",
            suggestionId,
            groupId,
            watch.Id);

        return watch;
    }

    public async Task<bool> DismissAsync(Guid groupId, Guid suggestionId, CancellationToken ct = default)
    {
        var suggestion = await suggestionRepo.GetByIdAsync(suggestionId, ct);
        if (suggestion is null ||
            suggestion.GroupId != groupId ||
            suggestion.Status != SuggestionStatus.Pending)
        {
            return false;
        }

        suggestion.Status = SuggestionStatus.Dismissed;
        await suggestionRepo.UpdateAsync(suggestion, ct);
        return true;
    }

    private static string BuildWatchName(PortalSuggestionEntity suggestion, WatchedSite? sourceWatch)
    {
        if (!string.IsNullOrWhiteSpace(suggestion.DetectedPlatform))
            return $"{suggestion.DetectedPlatform} portal — {suggestion.Domain}";

        return sourceWatch?.Name is { Length: > 0 }
            ? $"{sourceWatch.Name} discovery — {suggestion.Domain}"
            : suggestion.Domain;
    }

    private static string BuildDescription(PortalSuggestionEntity suggestion, WatchedSite? sourceWatch)
    {
        var sourceLabel = sourceWatch?.Name ?? sourceWatch?.Url ?? suggestion.SourceWatchId.ToString();
        return $"Auto-discovered from {sourceLabel}. {suggestion.Reason}";
    }

    private static List<string> BuildWatchTags(WatchGroup group, PortalSuggestionEntity suggestion)
    {
        var tags = new List<string> { "jobs", "portal-discovery" };
        tags.AddRange(group.Tags);

        if (!string.IsNullOrWhiteSpace(suggestion.DetectedPlatform))
            tags.Add(suggestion.DetectedPlatform);

        return tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool DetectUseJavaScript(PipelineDefinition pipeline) =>
        pipeline.Blocks.Any(block =>
            string.Equals(block.Type, "Navigate", StringComparison.OrdinalIgnoreCase) &&
            block.Config is { } config &&
            config.TryGetProperty("useJavaScript", out var useJavaScriptElement) &&
            useJavaScriptElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            useJavaScriptElement.GetBoolean());
}
