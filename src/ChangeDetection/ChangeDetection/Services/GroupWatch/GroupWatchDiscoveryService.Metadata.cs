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

public partial class GroupWatchDiscoveryService
{
    private List<string> BuildSearchQueries(StructuredDiscoveryIntent parsedIntent, string userInput)
    {
        var queries = parsedIntent.SearchQueries
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Select(query => query.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, _options.MaxSearchQueries))
            .ToList();

        if (queries.Count > 0)
            return queries;

        var fallback = $"{string.Join(' ', parsedIntent.RoleTypes.DefaultIfEmpty("jobs"))} {parsedIntent.Field} {parsedIntent.Location} careers";
        return [string.IsNullOrWhiteSpace(fallback) ? $"{userInput} careers" : fallback.Trim()];
    }

    private static List<RelevanceKeyword> BuildPositiveKeywords(StructuredDiscoveryIntent parsedIntent)
    {
        var keywords = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var roleWeight = 12;
        foreach (var roleType in parsedIntent.RoleTypes.Where(role => !string.IsNullOrWhiteSpace(role)))
        {
            keywords[roleType] = Math.Max(keywords.GetValueOrDefault(roleType), Math.Max(6, roleWeight));
            roleWeight--;
        }

        if (!string.IsNullOrWhiteSpace(parsedIntent.Field))
            keywords[parsedIntent.Field] = Math.Max(keywords.GetValueOrDefault(parsedIntent.Field), 8);

        if (!string.IsNullOrWhiteSpace(parsedIntent.Location))
        {
            keywords[parsedIntent.Location] = Math.Max(keywords.GetValueOrDefault(parsedIntent.Location), 20);

            foreach (var term in DeriveLocationFilterTerms(parsedIntent.Location)
                .Where(term => !string.Equals(term, parsedIntent.Location, StringComparison.OrdinalIgnoreCase)))
            {
                var weight = string.Equals(term, "Denmark", StringComparison.OrdinalIgnoreCase) ? 15 : 12;
                keywords[term] = Math.Max(keywords.GetValueOrDefault(term), weight);

                if (CountryToAbbreviation.TryGetValue(term, out var abbreviation))
                    keywords[abbreviation] = Math.Max(keywords.GetValueOrDefault(abbreviation), 10);
            }
        }

        return keywords
            .Select(pair => new RelevanceKeyword(pair.Key, pair.Value))
            .OrderByDescending(keyword => keyword.Weight)
            .ToList();
    }

    private static List<RelevanceKeyword> BuildNegativeKeywords(StructuredDiscoveryIntent parsedIntent)
    {
        var negatives = new[]
        {
            "director",
            "vice president",
            "senior manager",
            "principal investigator"
        };

        return negatives
            .Where(keyword => !parsedIntent.RoleTypes.Any(role =>
                role.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .Select(keyword => new RelevanceKeyword(keyword, -12))
            .ToList();
    }

    private static List<string> BuildTags(StructuredDiscoveryIntent parsedIntent)
    {
        var tags = new List<string> { "group-watch", "jobs" };

        if (!string.IsNullOrWhiteSpace(parsedIntent.Location))
            tags.Add(parsedIntent.Location);

        if (!string.IsNullOrWhiteSpace(parsedIntent.Field))
            tags.Add(parsedIntent.Field);

        tags.AddRange(parsedIntent.RoleTypes);

        return tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(NormalizeTag)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> BuildWatchTags(StructuredDiscoveryIntent parsedIntent, DiscoveredPortal portal)
    {
        var tags = BuildTags(parsedIntent);
        tags.Add(NormalizeTag(portal.Domain));
        if (!string.IsNullOrWhiteSpace(portal.PlatformId))
            tags.Add(NormalizeTag(portal.PlatformId));

        return tags
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildGroupName(StructuredDiscoveryIntent parsedIntent)
    {
        var parts = new List<string>();
        if (parsedIntent.RoleTypes.Count > 0)
            parts.Add(string.Join(", ", parsedIntent.RoleTypes));
        if (!string.IsNullOrWhiteSpace(parsedIntent.Field))
            parts.Add(parsedIntent.Field);
        if (!string.IsNullOrWhiteSpace(parsedIntent.Location))
            parts.Add(parsedIntent.Location);

        return parts.Count == 0
            ? "Discovered career portal watch group"
            : $"Career portals: {string.Join(" — ", parts)}";
    }

    private static string BuildIntentSummary(StructuredDiscoveryIntent parsedIntent)
    {
        var parts = new List<string>();
        if (parsedIntent.RoleTypes.Count > 0)
            parts.Add(string.Join(", ", parsedIntent.RoleTypes));
        if (!string.IsNullOrWhiteSpace(parsedIntent.Field))
            parts.Add(parsedIntent.Field);
        if (!string.IsNullOrWhiteSpace(parsedIntent.Location))
            parts.Add(parsedIntent.Location);

        return parts.Count == 0 ? "job discovery" : string.Join(" ", parts);
    }

    private async Task<(List<DiscoveredPortal> NewPortals, List<DiscoveredPortal> ExistingPortals)> FilterExistingWatchesAsync(
        IReadOnlyCollection<DiscoveredPortal> candidates,
        CancellationToken ct)
    {
        if (candidates.Count == 0)
            return ([], []);

        var existingWatches = (await watchService.GetAllAsync(ct))
            .Where(watch => !string.IsNullOrWhiteSpace(watch.Url))
            .ToList();

        if (existingWatches.Count == 0)
            return ([.. candidates], []);

        var groupIds = existingWatches
            .Where(watch => watch.GroupId.HasValue)
            .Select(watch => watch.GroupId!.Value)
            .Distinct()
            .ToList();

        var groupNamesById = new Dictionary<Guid, string>();
        if (groupIds.Count > 0)
        {
            try
            {
                groupNamesById = (await watchGroupService.GetAllAsync(ct))
                    .Where(group => groupIds.Contains(group.Id))
                    .ToDictionary(group => group.Id, group => group.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Failed to resolve existing watch group names during portal deduplication");
            }
        }

        var existingByDomain = existingWatches
            .Select(watch => new { Watch = watch, Domain = NormalizeDomain(watch.Url) })
            .GroupBy(entry => entry.Domain, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Watch, StringComparer.OrdinalIgnoreCase);

        var newPortals = new List<DiscoveredPortal>();
        var existingPortals = new List<DiscoveredPortal>();

        foreach (var candidate in candidates)
        {
            var domain = NormalizeDomain(candidate.Url);
            if (!existingByDomain.TryGetValue(domain, out var existingWatch))
            {
                newPortals.Add(candidate);
                continue;
            }

            groupNamesById.TryGetValue(existingWatch.GroupId ?? Guid.Empty, out var existingGroupName);
            existingPortals.Add(candidate with
            {
                ExistingWatchId = existingWatch.Id,
                ExistingGroupId = existingWatch.GroupId,
                ExistingGroupName = existingGroupName,
                ExistingWatchName = existingWatch.Name
            });
        }

        return (newPortals, existingPortals);
    }

    private static string BuildExistingWatchMessage(DiscoveredPortal portal)
    {
        if (!string.IsNullOrWhiteSpace(portal.ExistingGroupName))
            return $"Skipped {portal.Domain} — already being monitored in group '{portal.ExistingGroupName}'";

        if (!string.IsNullOrWhiteSpace(portal.ExistingWatchName))
            return $"Skipped {portal.Domain} — already being monitored by watch '{portal.ExistingWatchName}'";

        return $"Skipped {portal.Domain} — already being monitored";
    }

    private static List<DiscoveredPortal> DeduplicateByDomain(IEnumerable<DiscoveredPortal> portals)
    {
        return portals
            .GroupBy(portal => portal.Domain, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static List<DiscoveredPortal> FallbackPortalSelection(IReadOnlyList<SearchResultEnvelope> results)
    {
        return results
            .Where(result =>
                result.Result.Url.Contains("/careers", StringComparison.OrdinalIgnoreCase) ||
                result.Result.Url.Contains("/jobs", StringComparison.OrdinalIgnoreCase) ||
                result.Result.Url.Contains("/vacancies", StringComparison.OrdinalIgnoreCase))
            .Select(result => new DiscoveredPortal(
                result.Result.Url,
                NormalizeDomain(result.Result.Url),
                "Heuristic fallback: URL looks like a career portal.",
                result.Result.Title,
                SetupFlowEnhancements.DetectPlatformFromUrl(result.Result.Url),
                result.SearchQuery))
            .GroupBy(portal => portal.Domain, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private bool DetectUseJavaScript(PipelineDefinition pipeline)
    {
        foreach (var block in pipeline.Blocks)
        {
            if (!string.Equals(block.Type, "Navigate", StringComparison.OrdinalIgnoreCase) || block.Config is null)
                continue;

            try
            {
                var config = JsonSerializer.Deserialize<NavigateConfig>(block.Config.Value.GetRawText(), JsonOptions);
                if (config?.UseJavaScript == true)
                    return true;
            }
                        catch (Exception ex)
            {
                // Ignore malformed block config; watch still gets saved with pipeline JSON.
                logger.LogDebug(ex, "Malformed block config in DetectUseJavaScript");
            }
        }

        return false;
    }

    private static string NormalizeDomain(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url.Trim().ToLowerInvariant();

        var host = uri.Host.Trim().ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }

    private static string NormalizeTag(string value)
        => value.Trim().ToLowerInvariant().Replace(' ', '-');

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : $"{trimmed[..maxLength]}...";
    }

    private static TimeSpan ParseFrequencyOrDefault(string? frequency, TimeSpan defaultValue)
    {
        if (string.IsNullOrWhiteSpace(frequency))
            return defaultValue;

        var normalized = frequency.Trim().ToLowerInvariant();

        if (normalized.EndsWith('m') && int.TryParse(normalized[..^1], out var minutes))
            return TimeSpan.FromMinutes(Math.Max(1, minutes));

        if (normalized.EndsWith('h') && int.TryParse(normalized[..^1], out var hours))
            return TimeSpan.FromHours(Math.Max(1, hours));

        if (normalized.EndsWith('d') && int.TryParse(normalized[..^1], out var days))
            return TimeSpan.FromDays(Math.Max(1, days));

        return defaultValue;
    }

    private static T DeserializeOrThrow<T>(string json, string typeName)
    {
        try
        {
            var cleanedJson = StripMarkdownFences(json);
            var result = JsonSerializer.Deserialize<T>(cleanedJson, JsonOptions);
            return result ?? throw new InvalidOperationException($"{typeName} deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize {typeName} from LLM response: {ex.Message}",
                ex);
        }
    }

    private static string StripMarkdownFences(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstNewLine = trimmed.IndexOf('\n');
        if (firstNewLine < 0)
            return trimmed.Trim('`');

        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence <= firstNewLine)
            return trimmed[(firstNewLine + 1)..];

        return trimmed[(firstNewLine + 1)..lastFence].Trim();
    }

    private static GroupWatchProgress CreateProgress(
        GroupWatchPhase phase,
        string message,
        int? completedCount,
        int? totalCount,
        List<DiscoveredPortal>? portals = null,
        Guid? groupId = null,
        List<Guid>? watchIds = null,
        List<SetupNeededPortal>? needsSetupPortals = null)
        => new(
            phase,
            message,
            completedCount,
            totalCount,
            portals,
            groupId,
            watchIds,
            needsSetupPortals);

    private sealed record StructuredDiscoveryIntent
    {
        public string Location { get; init; } = string.Empty;
        public List<string> RoleTypes { get; init; } = [];
        public string Field { get; init; } = string.Empty;
        public List<string> SearchQueries { get; init; } = [];
    }

    private sealed record PortalSelection
    {
        public string Url { get; init; } = string.Empty;
        public string Reasoning { get; init; } = string.Empty;
        public string? Title { get; init; }
    }

    private sealed record SearchResultEnvelope(string SearchQuery, MergedSearchResult Result);

    private sealed record NavigateConfig
    {
        public bool UseJavaScript { get; init; }
    }
}
