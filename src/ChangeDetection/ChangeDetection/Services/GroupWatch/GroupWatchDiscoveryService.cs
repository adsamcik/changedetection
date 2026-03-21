using System.Runtime.CompilerServices;
using System.Net;
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

public class GroupWatchDiscoveryService(
    MultiProviderSearchService multiSearch,
    ILlmProviderChain llmProviderChain,
    IWatchGroupService watchGroupService,
    IWatchService watchService,
    SetupFlowEnhancements setupFlowEnhancements,
    IComposableSetupPipeline composableSetupPipeline,
    IHttpClientFactory httpClientFactory,
    ILogger<GroupWatchDiscoveryService> logger,
    IOptions<GroupWatchDiscoveryOptions>? options = null) : IGroupWatchDiscoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly GroupWatchDiscoveryOptions _options = options?.Value ?? new GroupWatchDiscoveryOptions();

    public async IAsyncEnumerable<GroupWatchProgress> DiscoverAsync(
        string userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userInput);

        logger.LogInformation("Starting group watch discovery for intent: {Intent}", userInput);

        // Phase 1: Ask LLM to understand intent AND suggest career portals in one call
        yield return CreateProgress(
            GroupWatchPhase.Parsing,
            "Understanding your request and finding relevant career portals...",
            completedCount: 0,
            totalCount: null);

        LlmDiscoveryResult? llmResult = null;
        GroupWatchProgress? failureProgress = null;
        try
        {
            llmResult = await DiscoverPortalsViaLlmAsync(userInput, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "LLM discovery failed for intent: {Intent}", userInput);
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

        var parsedIntent = llmResult!.Intent;

        logger.LogInformation(
            "LLM discovered {PortalCount} portals. Location={Location}, Roles={Roles}, Field={Field}",
            llmResult.Portals.Count,
            parsedIntent.Location,
            string.Join(", ", parsedIntent.RoleTypes),
            parsedIntent.Field);

        // Phase 2: Optionally augment with web search if providers are configured
        var portals = llmResult.Portals;

        var hasSearchProviders = multiSearch.HasAvailableProviders();
        if (hasSearchProviders)
        {
            yield return CreateProgress(
                GroupWatchPhase.Searching,
                $"Found {portals.Count} portals from knowledge. Searching for more...",
                completedCount: 0,
                totalCount: null);

            List<DiscoveredPortal>? additionalPortals = null;
            try
            {
                var searchQueries = BuildSearchQueries(parsedIntent, userInput);
                // Timeout web search augmentation — LLM catalog results are sufficient if search is slow
                using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                searchCts.CancelAfter(TimeSpan.FromSeconds(20));
                additionalPortals = await SearchForAdditionalPortalsAsync(searchQueries, searchCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogInformation("Web search augmentation timed out after 20s, continuing with catalog results");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Web search augmentation failed, continuing with LLM results only");
            }

            if (additionalPortals is { Count: > 0 })
            {
                logger.LogInformation("Web search found {Count} additional portal(s)", additionalPortals.Count);
                portals = [.. portals, .. additionalPortals];
            }

            yield return CreateProgress(
                GroupWatchPhase.Searching,
                $"Search complete. {portals.Count} total portals found.",
                completedCount: portals.Count,
                totalCount: portals.Count);
        }
        else
        {
            yield return CreateProgress(
                GroupWatchPhase.Searching,
                $"Found {portals.Count} career portals from AI knowledge.",
                completedCount: portals.Count,
                totalCount: portals.Count);
        }

        // Phase 3: Deduplicate and validate
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

        portals = newPortals;
        var discoverySummaryPortals = alreadyWatched.Count > 0
            ? [.. portals, .. alreadyWatched]
            : portals;

        yield return CreateProgress(
            GroupWatchPhase.Filtering,
            alreadyWatched.Count > 0
                ? $"Selected {portals.Count} new portal(s) for watch creation. Skipped {alreadyWatched.Count} already monitored portal(s)."
                : $"Selected {portals.Count} portal(s) for watch creation.",
            completedCount: portals.Count,
            totalCount: deduplicatedPortals.Count,
            portals: discoverySummaryPortals);

        if (portals.Count == 0)
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
            $"Found {portals.Count} portal(s). Waiting for confirmation.",
            completedCount: portals.Count,
            totalCount: portals.Count,
            portals: portals);
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
            parsedIntent = await ParseIntentAsync(userInput, ct);
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

        for (var i = 0; i < portals.Count; i++)
        {
            var portal = portals[i];

            // Signal that we're starting this portal so the UI updates immediately
            yield return CreateProgress(
                GroupWatchPhase.CreatingWatches,
                $"Setting up {portal.Domain}...",
                completedCount: i,
                totalCount: portals.Count,
                groupId: group.Id,
                watchIds: [.. watchIds]);

            // Validate URL before attempting watch creation
            UrlValidationResult? validation = null;
            try
            {
                validation = await ValidatePortalUrlAsync(portal.Url, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "URL validation threw for {PortalUrl}", portal.Url);
                validation = new UrlValidationResult(false, $"Validation error: {ex.Message}", 0);
            }

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
                PipelineDefinition? pipeline = null;

                using var portalTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                portalTimeout.CancelAfter(TimeSpan.FromSeconds(10));

                try
                {
                    pipeline = await BuildPipelineForPortalAsync(
                        portal,
                        userInput,
                        parsedIntent,
                        positiveKeywords,
                        negativeKeywords,
                        portalTimeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(
                        "Pipeline building timed out for {PortalUrl} — creating watch without pipeline for lazy generation",
                        portal.Url);
                    pipeline = null;
                }

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
                    pipelineNote = " (needs individual setup)";
                    needsSetupPortals.Add(new SetupNeededPortal(watchId, portal.Url, portal.Domain));
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

    private sealed record LlmDiscoveryResult(StructuredDiscoveryIntent Intent, List<DiscoveredPortal> Portals);

    /// <summary>
    /// Grounded discovery: LLM parses intent, then selects from a verified catalog of real URLs.
    /// The LLM NEVER generates URLs - it only reasons about which catalog entries match the intent.
    /// </summary>
    private async Task<LlmDiscoveryResult> DiscoverPortalsViaLlmAsync(string userInput, CancellationToken ct)
    {
        var catalog = await LoadGroundedCatalogAsync(ct);
        logger.LogInformation("Loaded grounded catalog with {Count} verified portals", catalog.Count);

        var catalogSummary = string.Join("\n", catalog.Select((p, i) =>
            $"[{i}] {p.Name} | {p.Url} | location: {string.Join(", ", p.LocationKeywords)} | platform: {p.PlatformId ?? "html"} | tags: {string.Join(", ", p.Tags)}"));

        var prompt = $$"""
            You are selecting career portals from a VERIFIED CATALOG for job monitoring.

            User request: "{{userInput}}"

            VERIFIED PORTAL CATALOG (these URLs are confirmed real and working):
            {{catalogSummary}}

             Your tasks:
             1. Parse the user's intent into location, roleTypes, and field
             2. Select the catalog entries (by index number) that match the user's intent
             3. Explain WHY each selected portal is relevant

            CRITICAL RULES:
            - You may ONLY select portals from the catalog above using their [index] number
             - Do NOT invent, modify, or guess any URLs
             - Select portals where the location keywords overlap with the user's requested location
             - Prefer portals tagged for the user's field/domain
             - Include general job boards for the region (e.g. Jobindex for Denmark, Jobs.cz for Czech Republic)
             - Also include matching company ATS portals (especially Workday and Teamtailor) when their location keywords match; do not return only generic job boards
             - For Denmark/Copenhagen requests, prefer a mix of Jobindex plus relevant Copenhagen/Denmark company portals from the catalog

             Return JSON:
             {
              "location": "parsed location",
              "roleTypes": ["parsed role 1"],
              "field": "parsed field",
              "searchQueries": ["web search query if catalog coverage is thin"],
              "selectedIndices": [0, 3, 7],
              "reasoning": { "0": "why this portal", "3": "why this portal" }
            }

            Respond ONLY with JSON.
            """;

        var response = await llmProviderChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            ExpectJson = true,
            Temperature = 0.1f,
            MaxTokens = 1500,
            UsageType = LlmUsageType.WatchSetup,
            PreferLargeModel = true
        }, ct);

        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Content))
            throw new InvalidOperationException($"LLM selection failed: {response.ErrorMessage ?? "empty response"}");

        var content = StripMarkdownFences(response.Content);
        var selection = DeserializeOrThrow<CatalogSelectionResponse>(content, "CatalogSelectionResponse");

        var intent = new StructuredDiscoveryIntent
        {
            Location = selection.Location?.Trim() ?? "",
            RoleTypes = (selection.RoleTypes ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToList(),
            Field = selection.Field?.Trim() ?? "",
            SearchQueries = (selection.SearchQueries ?? []).Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).ToList()
        };

        var reasoning = selection.Reasoning ?? new Dictionary<string, string>();
        var portals = (selection.SelectedIndices ?? [])
            .Where(idx => idx >= 0 && idx < catalog.Count)
            .Distinct()
            .Select(idx =>
            {
                var entry = catalog[idx];
                reasoning.TryGetValue(idx.ToString(), out var reason);
                return new DiscoveredPortal(
                    entry.Url,
                    NormalizeDomain(entry.Url),
                    reason?.Trim() ?? $"Matched from verified catalog: {entry.Name}",
                    entry.Name,
                    entry.PlatformId);
            })
            .ToList();

        logger.LogInformation("LLM selected {Count}/{CatalogSize} portals from grounded catalog", portals.Count, catalog.Count);
        return new LlmDiscoveryResult(intent, portals);
    }

    private sealed record CatalogSelectionResponse
    {
        public string? Location { get; init; }
        public List<string>? RoleTypes { get; init; }
        public string? Field { get; init; }
        public List<string>? SearchQueries { get; init; }
        public List<int>? SelectedIndices { get; init; }
        public Dictionary<string, string>? Reasoning { get; init; }
    }

    private sealed record CatalogEntry(string Name, string Url, string? PlatformId, List<string> LocationKeywords, List<string> Tags);

    private async Task<List<CatalogEntry>> LoadGroundedCatalogAsync(CancellationToken ct)
    {
        var entries = new List<CatalogEntry>();
        var seenDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Source 0: Existing healthy watches (highest priority — real verified data)
        try
        {
            var watches = await watchService.GetAllAsync(ct);
            foreach (var watch in watches
                .Where(w => w.CatalogStatus != CatalogVerificationStatus.Failed)
                .OrderByDescending(w => w.CatalogStatus == CatalogVerificationStatus.Verified)
                .ThenByDescending(w => w.TotalSuccessfulChecks))
            {
                if (string.IsNullOrWhiteSpace(watch.Url)) continue;
                if (string.IsNullOrWhiteSpace(watch.PipelineDefinitionJson)) continue;
                if (watch.Status is WatchStatus.Error) continue;
                if (!watch.IsEnabled) continue;

                var domain = NormalizeDomain(watch.Url);
                if (!seenDomains.Add(domain)) continue;

                var tags = watch.Tags.Count > 0
                    ? watch.Tags.Select(t => t.ToLowerInvariant()).ToList()
                    : ExtractDomainTags(domain);

                var locationKeywords = tags.Where(IsLocationTag).ToList();
                if (locationKeywords.Count == 0 && !string.IsNullOrWhiteSpace(watch.UserIntent))
                    locationKeywords = ExtractLocationFromIntent(watch.UserIntent);

                var name = !string.IsNullOrWhiteSpace(watch.Name) ? watch.Name : domain;
                var platformId = SetupFlowEnhancements.DetectPlatformFromUrl(watch.Url);

                entries.Add(new CatalogEntry(name, watch.Url, platformId, locationKeywords, tags));
            }

            if (entries.Count > 0)
                logger.LogInformation("Loaded {Count} catalog entries from existing watches", entries.Count);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to load catalog entries from existing watches"); }

        // Source 1: job-watch-portals.json (curated portal template)
        var portalDefs = JobWatch.JobWatchSeeder.GetAllPortalDefinitions();
        foreach (var portal in portalDefs)
        {
            var domain = NormalizeDomain(portal.Url);
            if (!seenDomains.Add(domain)) continue;

            entries.Add(new CatalogEntry(
                portal.Name, portal.Url,
                SetupFlowEnhancements.DetectPlatformFromUrl(portal.Url),
                portal.ExtraTags.Where(IsLocationTag).ToList(),
                portal.ExtraTags));
        }

        // Source 2: sites.json from the Python scraper (if available on disk)
        try
        {
            var sitesPath = FindSitesJson();
            if (sitesPath is not null)
            {
                var sitesJson = File.ReadAllText(sitesPath);
                using var doc = JsonDocument.Parse(sitesJson);
                foreach (var category in doc.RootElement.EnumerateObject())
                {
                    if (category.Value.ValueKind != JsonValueKind.Array) continue;
                    foreach (var site in category.Value.EnumerateArray())
                    {
                        var entry = ParseSiteEntry(category.Name, site);
                        if (entry is null) continue;
                        var domain = NormalizeDomain(entry.Url);
                        if (seenDomains.Add(domain))
                            entries.Add(entry);
                    }
                }
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to load sites.json"); }

        return entries;
    }

    /// <summary>
    /// Extracts simple tags from a domain name (e.g., "jobs.cz" → ["czech", "jobs"]).
    /// </summary>
    private static List<string> ExtractDomainTags(string domain)
    {
        var tags = new List<string>();
        var tld = domain.Split('.').LastOrDefault()?.ToLowerInvariant();
        if (tld is "cz") tags.Add("czech");
        else if (tld is "dk") tags.Add("denmark");
        else if (tld is "de") tags.Add("germany");
        else if (tld is "se") tags.Add("sweden");
        else if (tld is "nl") tags.Add("netherlands");
        else if (tld is "at") tags.Add("austria");
        else if (tld is "be") tags.Add("belgium");
        return tags;
    }

    /// <summary>
    /// Extracts location keywords from a user intent string by matching known location terms.
    /// </summary>
    private static List<string> ExtractLocationFromIntent(string intent)
    {
        var lower = intent.ToLowerInvariant();
        var locations = new List<string>();
        ReadOnlySpan<string> known = ["denmark", "copenhagen", "czech", "prague", "sweden", "germany", "netherlands", "austria", "belgium"];
        foreach (var loc in known)
        {
            if (lower.Contains(loc, StringComparison.Ordinal))
                locations.Add(loc);
        }
        return locations;
    }

    private static CatalogEntry? ParseSiteEntry(string category, JsonElement site) => category switch
    {
        "workday" => ParseWorkdaySite(site),
        "teamtailor" => ParseTeamtailorSite(site),
        _ => ParseGenericSite(category, site)
    };

    private static CatalogEntry? ParseWorkdaySite(JsonElement site)
    {
        var company = site.TryGetProperty("company", out var c) ? c.GetString() : null;
        var subdomain = site.TryGetProperty("subdomain", out var s) ? s.GetString() : null;
        var instance = site.TryGetProperty("instance", out var i) ? i.GetString() : null;
        var siteId = site.TryGetProperty("site_id", out var si) ? si.GetString() : null;
        if (string.IsNullOrWhiteSpace(subdomain) || string.IsNullOrWhiteSpace(instance) || string.IsNullOrWhiteSpace(siteId))
            return null;
        var url = $"https://{subdomain}.{instance}.myworkdayjobs.com/en-US/{siteId}";
        var locs = GetLocationKeywords(site);
        return new CatalogEntry($"{company ?? subdomain} (Workday)", url, "workday", locs, ["workday", .. locs.Select(l => l.ToLowerInvariant())]);
    }

    private static CatalogEntry? ParseTeamtailorSite(JsonElement site)
    {
        var company = site.TryGetProperty("company", out var c) ? c.GetString() : null;
        var slug = site.TryGetProperty("slug", out var s) ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(slug)) return null;
        var url = $"https://{slug}.teamtailor.com/jobs";
        var locs = GetLocationKeywords(site);
        return new CatalogEntry($"{company ?? slug} (Teamtailor)", url, "teamtailor", locs, ["teamtailor", .. locs.Select(l => l.ToLowerInvariant())]);
    }

    private static CatalogEntry? ParseGenericSite(string category, JsonElement site)
    {
        var name = site.TryGetProperty("name", out var n) ? n.GetString() : null;
        var url = site.TryGetProperty("url", out var u) ? u.GetString() :
                  site.TryGetProperty("base_url", out var bu) ? bu.GetString() : null;
        if (string.IsNullOrWhiteSpace(url)) return null;
        var locs = GetLocationKeywords(site);
        return new CatalogEntry(name ?? category, url, null, locs, [category, .. locs.Select(l => l.ToLowerInvariant())]);
    }

    private static List<string> GetLocationKeywords(JsonElement site)
    {
        if (!site.TryGetProperty("location_keywords", out var kw) || kw.ValueKind != JsonValueKind.Array) return [];
        return kw.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
    }

    private static bool IsLocationTag(string tag) =>
        tag.Equals("denmark", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("czech", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("sweden", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("copenhagen", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("prague", StringComparison.OrdinalIgnoreCase);

    private static string? FindSitesJson()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "tools", "job-scanner", "sites.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "job-scanner", "sites.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Proton Drive", "adsamcik", "My files", "Sdílnice", "Marťa Práce", "tools", "job-scanner", "sites.json")
        };
        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);
    }

    private async Task<List<DiscoveredPortal>> SearchForAdditionalPortalsAsync(List<string> searchQueries, CancellationToken ct)
    {
        var collectedResults = new List<SearchResultEnvelope>();
        foreach (var query in searchQueries.Take(_options.MaxSearchQueries))
        {
            try
            {
                var resultSet = await multiSearch.SearchAllAsync(new SearchQuery { Query = query, MaxResults = _options.MaxSearchResultsPerQuery }, ct: ct);
                collectedResults.AddRange(resultSet.MergedResults
                    .Where(r => Uri.TryCreate(r.Url, UriKind.Absolute, out _))
                    .Select(r => new SearchResultEnvelope(query, r)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Web search failed for query: {Query}", query);
            }
        }
        return FallbackPortalSelection(collectedResults);
    }

    private async Task<StructuredDiscoveryIntent> ParseIntentAsync(string userInput, CancellationToken ct)
    {
        var prompt = $$"""
            You extract structured search inputs for a grouped job-watch discovery workflow.

            User request:
            "{{userInput}}"

            Return JSON with this exact shape:
            {
              "location": "city/region/country string or empty string",
              "roleTypes": ["role name 1", "role name 2"],
              "field": "domain/discipline string or empty string",
              "searchQueries": [
                "query 1",
                "query 2",
                "query 3",
                "query 4"
              ]
            }

            Rules:
            - Infer concise, reusable search queries for finding career portals, careers pages, hiring hubs, and job boards.
            - Focus on portal/listing discovery, not individual job postings.
            - Keep roleTypes normalized (e.g. "research assistant", not sentence fragments).
            - If a field is absent, use an empty string.
            - Generate between 3 and 6 searchQueries.
            - Prefer English queries unless the user clearly asked in another language.

            Respond ONLY with JSON.
            """;

        var response = await llmProviderChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            ExpectJson = true,
            Temperature = 0.1f,
            MaxTokens = 700,
            UsageType = LlmUsageType.WatchSetup,
            PreferLargeModel = true
        }, ct);

        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Content))
        {
            throw new InvalidOperationException(
                $"Intent parsing failed: {response.ErrorMessage ?? "empty response"}");
        }

        var parsed = DeserializeOrThrow<StructuredDiscoveryIntent>(response.Content, nameof(StructuredDiscoveryIntent));

        return parsed with
        {
            SearchQueries = (parsed.SearchQueries ?? [])
                .Where(query => !string.IsNullOrWhiteSpace(query))
                .Select(query => query.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            RoleTypes = (parsed.RoleTypes ?? [])
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .Select(role => role.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Location = parsed.Location.Trim(),
            Field = parsed.Field.Trim()
        };
    }

    private async Task<List<DiscoveredPortal>> FilterAndRankPortalsAsync(
        StructuredDiscoveryIntent parsedIntent,
        string userInput,
        IReadOnlyList<SearchResultEnvelope> results,
        CancellationToken ct)
    {
        var compactResults = results
            .Take(60)
            .Select((entry, index) => new
            {
                index = index + 1,
                query = entry.SearchQuery,
                url = entry.Result.Url,
                title = entry.Result.Title,
                snippet = Truncate(entry.Result.Snippet, 220)
            })
            .ToList();

        var prompt = $$"""
            You are selecting the best career portals for an automated watch creation workflow.

            User intent: {{userInput}}
            Location: {{parsedIntent.Location}}
            Role types: {{string.Join(", ", parsedIntent.RoleTypes)}}
            Field: {{parsedIntent.Field}}

            Search results:
            {{JsonSerializer.Serialize(compactResults)}}

            Identify URLs that are actual career portals, job-board result pages, or company career listing pages.

            Exclude:
            - individual job postings
            - news articles
            - press releases
            - general company homepages unless they are clearly the careers listing page
            - recruiter profile pages

            Return ONLY a JSON array. Each item must have:
            {
              "url": "https://...",
              "reasoning": "brief explanation",
              "title": "best human-readable title if known"
            }

            Prefer the strongest portal/listing pages first. Return at most {{Math.Max(1, _options.MaxPortalsPerGroup)}} items.
            """;

        var response = await llmProviderChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            ExpectJson = true,
            Temperature = 0.1f,
            MaxTokens = 1200,
            UsageType = LlmUsageType.ContentAnalysis,
            PreferLargeModel = true
        }, ct);

        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Content))
        {
            throw new InvalidOperationException(
                $"Portal filtering failed: {response.ErrorMessage ?? "empty response"}");
        }

        var candidates = DeserializeOrThrow<List<PortalSelection>>(response.Content, nameof(PortalSelection))
            ?? [];

        return candidates
            .Where(candidate => Uri.TryCreate(candidate.Url, UriKind.Absolute, out _))
            .Select(candidate =>
            {
                var domain = NormalizeDomain(candidate.Url);
                var matchedResult = results.FirstOrDefault(r =>
                    string.Equals(r.Result.Url, candidate.Url, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeDomain(r.Result.Url), domain, StringComparison.OrdinalIgnoreCase));

                return new DiscoveredPortal(
                    candidate.Url,
                    domain,
                    string.IsNullOrWhiteSpace(candidate.Reasoning) ? "Approved as a career portal." : candidate.Reasoning.Trim(),
                    candidate.Title ?? matchedResult?.Result.Title,
                    SetupFlowEnhancements.DetectPlatformFromUrl(candidate.Url),
                    matchedResult?.SearchQuery);
            })
            .ToList();
    }

    private async Task<PipelineDefinition?> BuildPipelineForPortalAsync(
        DiscoveredPortal portal,
        string userInput,
        StructuredDiscoveryIntent parsedIntent,
        IReadOnlyList<RelevanceKeyword> positiveKeywords,
        IReadOnlyList<RelevanceKeyword> negativeKeywords,
        CancellationToken ct)
    {
        var detectedPlatform = SetupFlowEnhancements.DetectPlatformFromUrl(portal.Url);
        if (!string.IsNullOrWhiteSpace(detectedPlatform))
        {
            logger.LogInformation(
                "Using deterministic platform template {PlatformId} for {Url}",
                detectedPlatform,
                portal.Url);

            var template = await setupFlowEnhancements.GetPlatformTemplateAsync(
                detectedPlatform,
                portal.Url,
                positiveKeywords,
                negativeKeywords,
                ct);

            if (template is null)
            {
                throw new InvalidOperationException(
                    $"No pipeline template is available for detected platform '{detectedPlatform}'.");
            }

            return CustomizeTemplatePipeline(template, portal, parsedIntent);
        }

        // Unknown platform — no template available. The watch will be created with
        // NeedsPipelineSetup=true and the user will need to configure it interactively.
        logger.LogInformation(
            "Unknown platform for {Url} — will mark as needing interactive pipeline setup",
            portal.Url);
        return null;
    }

    /// <summary>
    /// Creates a generic job listing pipeline for career pages without a known platform template.
    /// Uses broad CSS selectors to extract clickable links with titles from any page — no LLM needed.
    /// </summary>
    private static PipelineDefinition CreateGenericJobListingPipeline(string url)
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
                Config = JsonSerializer.SerializeToElement(new
                {
                    useJavaScript = true,
                    waitForSelector = "a[href]",
                    timeout = 30000
                })
            },
            new()
            {
                Id = "extractschema-1",
                Type = "ExtractSchema",
                Position = 2,
                Config = JsonSerializer.SerializeToElement(new
                {
                    scope = "main a[href], article a[href], .jobs a[href], ul a[href], li a[href]",
                    listMode = true,
                    schema = new object[]
                    {
                        new { field = "title", selector = "*" },
                        new { field = "url", selector = "a[href]" }
                    },
                    enableLlmFallback = false
                })
            },
            new()
            {
                Id = "listdiff-1",
                Type = "ListDiff",
                Position = 3,
                Config = JsonSerializer.SerializeToElement(new
                {
                    identityKey = "url",
                    mode = "all_changes"
                })
            },
            new()
            {
                Id = "output-1",
                Type = "Output",
                Position = 4
            }
        };

        var connections = new List<ConnectionDefinition>
        {
            new() { FromBlockId = "input-1", FromPort = "url", ToBlockId = "navigate-1", ToPort = "url" },
            new() { FromBlockId = "navigate-1", FromPort = "html", ToBlockId = "extractschema-1", ToPort = "html" },
            new() { FromBlockId = "extractschema-1", FromPort = "data", ToBlockId = "listdiff-1", ToPort = "data" },
            new() { FromBlockId = "listdiff-1", FromPort = "result", ToBlockId = "output-1", ToPort = "data" }
        };

        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

        return new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks = blocks,
            Connections = connections,
            Metadata = new PipelineMetadata
            {
                DisplayTitle = $"Monitor {host} job listings",
                CreatedAt = DateTime.UtcNow,
                UserIntent = "Generic job listing extraction (auto-generated for unknown platform)",
                EstimatedLlmCallsPerRun = 0,
                CardType = "list"
            }
        };
    }

    private async Task<UrlValidationResult> ValidatePortalUrlAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new UrlValidationResult(false, "Invalid URL", 0);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            var client = httpClientFactory.CreateClient();
            using var response = await client.GetAsync(uri, timeoutCts.Token);
            var statusCode = (int)response.StatusCode;

            if (statusCode is >= 200 and < 400)
                return new UrlValidationResult(true, null, statusCode);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var responseBody = await ReadResponseBodyAsync(response, timeoutCts.Token);
                if (response.StatusCode == HttpStatusCode.Forbidden &&
                    ContainsCaptchaWall(responseBody))
                {
                    return new UrlValidationResult(false, "CAPTCHA wall detected", statusCode);
                }

                return new UrlValidationResult(true, "Login required", statusCode);
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return new UrlValidationResult(false, "URL is no longer available", statusCode);

            if (statusCode >= 500)
                return new UrlValidationResult(false, "Server error", statusCode);

            return new UrlValidationResult(false, $"Unexpected HTTP {statusCode}", statusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new UrlValidationResult(false, "Validation timed out", 0);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "HTTP error validating portal URL {PortalUrl}", url);
            return new UrlValidationResult(false, ex.Message, 0);
        }
    }

    private static async Task<string> ReadResponseBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool ContainsCaptchaWall(string responseBody) =>
        !string.IsNullOrWhiteSpace(responseBody) &&
        (responseBody.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
         responseBody.Contains("verify", StringComparison.OrdinalIgnoreCase));

    private async Task<PipelineDefinition> BuildPipelineWithComposableFlowAsync(
        DiscoveredPortal portal,
        string userInput,
        IReadOnlyList<RelevanceKeyword> positiveKeywords,
        IReadOnlyList<RelevanceKeyword> negativeKeywords,
        CancellationToken ct)
    {
        var fullInput = $"{userInput} {portal.Url}";
        string? sessionId = null;
        string? lastMessage = null;

        await foreach (var progress in composableSetupPipeline.StartSetupAsync(
                           new SetupRequest { UserInput = fullInput }, ct))
        {
            lastMessage = progress.Error ?? progress.Message;

            if (progress.Type == SetupProgressType.Failed)
            {
                throw new InvalidOperationException(progress.Error ?? progress.Message);
            }

            if (progress.Type == SetupProgressType.CheckpointReached &&
                progress.Phase == SetupPhase.Checkpoint1 &&
                !string.IsNullOrWhiteSpace(progress.SessionId))
            {
                sessionId = progress.SessionId;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException(lastMessage ?? "Composable setup did not reach checkpoint 1.");
        }

        PipelineDefinition? pipeline = null;

        try
        {
            await foreach (var progress in composableSetupPipeline.ConfirmIntentAsync(sessionId, true, null, ct))
            {
                lastMessage = progress.Error ?? progress.Message;

                if (progress.Type == SetupProgressType.Failed)
                {
                    throw new InvalidOperationException(progress.Error ?? progress.Message);
                }

                if (progress.Type == SetupProgressType.CheckpointReached &&
                    progress.Phase == SetupPhase.Checkpoint2 &&
                    progress.Proposal?.Pipeline is not null)
                {
                    pipeline = ApplyRelevanceKeywords(progress.Proposal.Pipeline, positiveKeywords, negativeKeywords);
                    break;
                }
            }

            if (pipeline is null)
            {
                throw new InvalidOperationException(lastMessage ?? "Composable setup did not produce a pipeline proposal.");
            }

            return pipeline;
        }
        finally
        {
            try
            {
                await foreach (var _ in composableSetupPipeline.ConfirmPipelineAsync(
                                   sessionId,
                                   confirmed: false,
                                   feedback: "Automated group watch discovery captured the generated pipeline.",
                                   ct))
                {
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Cleanup of composable setup session {SessionId} failed", sessionId);
            }
        }
    }

    private async Task<Guid> CreateWatchForPortalAsync(
        DiscoveredPortal portal,
        string userInput,
        StructuredDiscoveryIntent parsedIntent,
        Guid groupId,
        PipelineDefinition? pipeline,
        CancellationToken ct)
    {
        var useJavaScript = pipeline is not null && DetectUseJavaScript(pipeline);
        var request = new CreateWatchRequest
        {
            Url = portal.Url,
            Name = portal.Title ?? portal.Domain,
            Description = portal.Reasoning,
            UserIntent = userInput,
            GroupId = groupId,
            CheckInterval = ParseFrequencyOrDefault(null, _options.DefaultCheckInterval),
            UseJavaScript = useJavaScript,
            Tags = BuildWatchTags(parsedIntent, portal),
            // When no pipeline is provided, skip initial check — the watch needs setup first
            SkipInitialCheck = pipeline is not null
        };

        var createdWatch = await watchService.CreateWatchAsync(request, ct);

        if (pipeline is not null)
        {
            createdWatch.PipelineDefinitionJson = PipelineSerializer.Serialize(pipeline);
        }
        else
        {
            // No template available — mark the watch as needing interactive pipeline setup.
            // The background service will skip this watch until the user configures it.
            createdWatch.NeedsPipelineSetup = true;
        }

        createdWatch.FetchSettings ??= new FetchSettings();
        createdWatch.FetchSettings.UseJavaScript = useJavaScript;

        await watchService.UpdateWatchAsync(createdWatch, ct);
        return createdWatch.Id;
    }

    private static PipelineDefinition CustomizeTemplatePipeline(
        PipelineDefinition pipeline,
        DiscoveredPortal portal,
        StructuredDiscoveryIntent parsedIntent)
    {
        var blocks = pipeline.Blocks
            .Select(block =>
            {
                if (block.Config is null)
                    return block;

                if (string.Equals(block.Type, "DataFilter", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(parsedIntent.Location))
                {
                    return block with
                    {
                        Config = ReplaceLocationConditions(block.Config.Value, parsedIntent.Location)
                    };
                }

                return block;
            })
            .ToList();

        return pipeline with
        {
            Blocks = blocks,
            Metadata = pipeline.Metadata is null
                ? new PipelineMetadata
                {
                    DisplayTitle = portal.Title ?? portal.Domain,
                    UserIntent = BuildIntentSummary(parsedIntent),
                    CreatedAt = DateTime.UtcNow
                }
                : pipeline.Metadata with
                {
                    DisplayTitle = pipeline.Metadata.DisplayTitle ?? portal.Title ?? portal.Domain,
                    UserIntent = pipeline.Metadata.UserIntent ?? BuildIntentSummary(parsedIntent)
                }
        };
    }

    private static PipelineDefinition ApplyRelevanceKeywords(
        PipelineDefinition pipeline,
        IReadOnlyList<RelevanceKeyword> positiveKeywords,
        IReadOnlyList<RelevanceKeyword> negativeKeywords)
        => PipelineKeywordPatcher.ApplyRelevanceKeywords(pipeline, positiveKeywords, negativeKeywords);

    private static JsonElement ReplaceLocationConditions(JsonElement existingConfig, string location)
    {
        var config = JsonNode.Parse(existingConfig.GetRawText()) as JsonObject ?? new JsonObject();

        // Determine the field name from the first existing condition (default to "locationsText")
        var fieldName = "locationsText";
        if (config["conditions"]?.AsArray() is { Count: > 0 } existing &&
            existing[0] is JsonObject first &&
            first["field"]?.GetValue<string>() is { } f)
        {
            fieldName = f;
        }

        // Build location terms: the raw location, its parts if comma-separated, and derived country
        var terms = DeriveLocationFilterTerms(location);

        var replacementConditions = new JsonArray();
        foreach (var term in terms)
        {
            replacementConditions.Add(new JsonObject
            {
                ["field"] = fieldName,
                ["operator"] = "contains",
                ["value"] = term
            });
        }

        config["conditions"] = replacementConditions;
        config["mode"] = "any";
        return JsonSerializer.SerializeToElement(config, JsonOptions);
    }

    /// <summary>
    /// Derives a set of location filter terms from a location string.
    /// E.g. "Copenhagen" → ["Copenhagen", "Denmark"], "Kastrup, Denmark" → ["Kastrup", "Denmark"].
    /// </summary>
    internal static List<string> DeriveLocationFilterTerms(string location)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Split comma-separated parts (e.g. "Kastrup, Denmark")
        var parts = location.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (!string.IsNullOrWhiteSpace(part))
                terms.Add(part);
        }

        // If there was no comma, add the raw location
        if (parts.Length <= 1)
            terms.Add(location.Trim());

        // Derive country from each part using known city → country mappings
        foreach (var part in parts)
        {
            if (CityToCountry.TryGetValue(part.Trim(), out var country))
                terms.Add(country);
        }

        // Also try the full location string as a city lookup
        if (CityToCountry.TryGetValue(location.Trim(), out var fullCountry))
            terms.Add(fullCountry);

        return terms.ToList();
    }

    /// <summary>
    /// Maps well-known biotech hub cities to their country names for location filtering.
    /// </summary>
    private static readonly Dictionary<string, string> CityToCountry = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Copenhagen"] = "Denmark", ["Kastrup"] = "Denmark", ["Lyngby"] = "Denmark",
        ["Bagsværd"] = "Denmark", ["Hillerød"] = "Denmark", ["Kalundborg"] = "Denmark",
        ["Munich"] = "Germany", ["Berlin"] = "Germany", ["Hamburg"] = "Germany",
        ["Frankfurt"] = "Germany", ["Heidelberg"] = "Germany", ["Tübingen"] = "Germany",
        ["Vienna"] = "Austria", ["Graz"] = "Austria",
        ["Amsterdam"] = "Netherlands", ["Leiden"] = "Netherlands", ["Rotterdam"] = "Netherlands",
        ["Brussels"] = "Belgium", ["Ghent"] = "Belgium", ["Mechelen"] = "Belgium",
        ["Basel"] = "Switzerland", ["Zurich"] = "Switzerland",
        ["Stockholm"] = "Sweden", ["Malmö"] = "Sweden", ["Lund"] = "Sweden", ["Gothenburg"] = "Sweden",
        ["Paris"] = "France", ["Lyon"] = "France", ["Strasbourg"] = "France",
        ["Helsinki"] = "Finland", ["Turku"] = "Finland",
        ["Oslo"] = "Norway", ["Bergen"] = "Norway",
        ["Prague"] = "Czech Republic", ["Brno"] = "Czech Republic",
        ["London"] = "United Kingdom", ["Cambridge"] = "United Kingdom", ["Oxford"] = "United Kingdom",
        ["Dublin"] = "Ireland",
        ["Luxembourg"] = "Luxembourg",
    };

    private static readonly Dictionary<string, string> CountryToAbbreviation = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Denmark"] = "DK",
        ["Germany"] = "DE",
        ["Austria"] = "AT",
        ["Netherlands"] = "NL",
        ["Belgium"] = "BE",
        ["Switzerland"] = "CH",
        ["Sweden"] = "SE",
        ["France"] = "FR",
        ["Finland"] = "FI",
        ["Norway"] = "NO",
        ["Czech Republic"] = "CZ",
        ["United Kingdom"] = "UK",
        ["Ireland"] = "IE",
        ["Luxembourg"] = "LU",
    };

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

    private static bool DetectUseJavaScript(PipelineDefinition pipeline)
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
            catch
            {
                // Ignore malformed block config; watch still gets saved with pipeline JSON.
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
