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
    private async Task<List<DiscoveredPortal>> DiscoverPortalsViaSearchAsync(
        StructuredDiscoveryIntent parsedIntent,
        string userInput,
        CancellationToken ct,
        ChannelWriter<GroupWatchProgress>? progressWriter = null)
    {
        var searchQueries = BuildSearchQueries(parsedIntent, userInput);

        // Run both sources in parallel — neither is primary
        var webSearchTask = RunWebSearchAsync(searchQueries, ct);
        var catalogTask = RunCatalogMatchAsync(parsedIntent, ct);

        await Task.WhenAll(webSearchTask, catalogTask);

        var webResults = webSearchTask.Result;
        var catalogResults = catalogTask.Result;

        logger.LogInformation(
            "Discovery sources: {WebCount} from web search, {CatalogCount} from catalog",
            webResults.Count, catalogResults.Count);

        if (progressWriter is not null)
            await progressWriter.WriteAsync(CreateProgress(
                GroupWatchPhase.Searching,
                $"Found {webResults.Count} web results and {catalogResults.Count} catalog matches. Classifying...",
                completedCount: webResults.Count + catalogResults.Count,
                totalCount: null), ct);

        // Merge both sources into one flat list, dedup by domain
        var merged = new List<SearchResultEnvelope>();
        var seenDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in webResults.Concat(catalogResults))
        {
            var domain = NormalizeDomain(result.Result.Url);
            if (seenDomains.Add(domain))
                merged.Add(result);
        }

        if (merged.Count == 0)
        {
            logger.LogWarning("No results from any source for intent: {Intent}", userInput);
            return [];
        }

        // LLM classification — evaluate each URL: is it a career portal?
        if (progressWriter is not null)
            await progressWriter.WriteAsync(CreateProgress(
                GroupWatchPhase.Searching,
                $"Classifying {merged.Count} candidates via AI...",
                completedCount: 0,
                totalCount: merged.Count), ct);

        List<DiscoveredPortal> classifiedPortals;
        try
        {
            using var classificationTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            classificationTimeout.CancelAfter(PortalClassificationTimeout);
            classifiedPortals = await ClassifyPortalsViaLlmAsync(parsedIntent, userInput, merged, classificationTimeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Portal classification timed out for intent: {Intent}. Falling back to heuristics.", userInput);
            classifiedPortals = FallbackPortalSelection(merged);
        }

        if (progressWriter is not null)
            await progressWriter.WriteAsync(CreateProgress(
                GroupWatchPhase.Searching,
                $"Classified {classifiedPortals.Count} portals. Validating URLs...",
                completedCount: classifiedPortals.Count,
                totalCount: merged.Count), ct);

        // URL validation — parallel with concurrency limit (8 concurrent, 5s timeout each)
        var validationSemaphore = new SemaphoreSlim(8, 8);
        var validatedCount = 0;
        var totalToValidate = classifiedPortals.Count;

        var validationTasks = classifiedPortals.Select(async portal =>
        {
            await validationSemaphore.WaitAsync(ct);
            try
            {
                var validation = await ValidatePortalUrlAsync(portal.Url, ct);
                var current = Interlocked.Increment(ref validatedCount);

                if (progressWriter is not null)
                    await progressWriter.WriteAsync(CreateProgress(
                        GroupWatchPhase.Searching,
                        $"Validating portal {current}/{totalToValidate}...",
                        completedCount: current,
                        totalCount: totalToValidate), ct);

                if (validation.IsValid)
                    return portal;

                logger.LogInformation("Dropped portal {Url}: {Reason}", portal.Url, validation.Reason);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref validatedCount);
                logger.LogWarning(ex, "Rejected portal {Url} due to validation error", portal.Url);
                return null;
            }
            finally
            {
                validationSemaphore.Release();
            }
        }).ToList();

        var validationResults = await Task.WhenAll(validationTasks);
        var validatedPortals = validationResults.Where(p => p is not null).Select(p => p!).ToList();

        logger.LogInformation("Validated {Valid}/{Total} portals", validatedPortals.Count, classifiedPortals.Count);
        return validatedPortals;
    }

    private async Task<List<PreparedPortalBuildResult>> PreparePortalBuildsAsync(
        IReadOnlyList<DiscoveredPortal> portals,
        string userInput,
        StructuredDiscoveryIntent parsedIntent,
        IReadOnlyList<RelevanceKeyword> positiveKeywords,
        IReadOnlyList<RelevanceKeyword> negativeKeywords,
        CancellationToken ct)
    {
        using var pipelineBuildLimiter = new SemaphoreSlim(PipelineBuildConcurrency, PipelineBuildConcurrency);
        var preparedResults = new ConcurrentBag<PreparedPortalBuildResult>();

        var tasks = portals
            .Select((portal, index) => PreparePortalBuildAsync(
                index,
                portal,
                userInput,
                parsedIntent,
                positiveKeywords,
                negativeKeywords,
                pipelineBuildLimiter,
                preparedResults,
                ct))
            .ToList();

        await Task.WhenAll(tasks);

        return preparedResults
            .OrderBy(result => result.Index)
            .ToList();
    }

    private async Task PreparePortalBuildAsync(
        int index,
        DiscoveredPortal portal,
        string userInput,
        StructuredDiscoveryIntent parsedIntent,
        IReadOnlyList<RelevanceKeyword> positiveKeywords,
        IReadOnlyList<RelevanceKeyword> negativeKeywords,
        SemaphoreSlim pipelineBuildLimiter,
        ConcurrentBag<PreparedPortalBuildResult> preparedResults,
        CancellationToken ct)
    {
        UrlValidationResult validation;
        try
        {
            validation = await ValidatePortalUrlAsync(portal.Url, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "URL validation threw for {PortalUrl}", portal.Url);
            preparedResults.Add(new PreparedPortalBuildResult(
                index,
                portal,
                new UrlValidationResult(false, $"Validation error: {ex.Message}", 0),
                null,
                null));
            return;
        }

        if (!validation.IsValid)
        {
            preparedResults.Add(new PreparedPortalBuildResult(index, portal, validation, null, null));
            return;
        }

        await pipelineBuildLimiter.WaitAsync(ct);
        try
        {
            PipelineDefinition? pipeline = null;

            try
            {
                using var portalTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                portalTimeout.CancelAfter(PipelineBuildTimeout);
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
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                preparedResults.Add(new PreparedPortalBuildResult(index, portal, validation, null, ex));
                return;
            }

            preparedResults.Add(new PreparedPortalBuildResult(index, portal, validation, pipeline, null));
        }
        finally
        {
            pipelineBuildLimiter.Release();
        }
    }

    private async Task<List<SearchResultEnvelope>> RunWebSearchAsync(
        List<string> searchQueries, CancellationToken ct)
    {
        var results = new List<SearchResultEnvelope>();

        using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        searchCts.CancelAfter(TimeSpan.FromSeconds(30));

        foreach (var query in searchQueries.Take(Math.Max(1, _options.MaxSearchQueries)))
        {
            try
            {
                var resultSet = await multiSearch.SearchAllAsync(
                    new SearchQuery { Query = query, MaxResults = _options.MaxSearchResultsPerQuery },
                    ct: searchCts.Token);
                results.AddRange(resultSet.MergedResults
                    .Where(r => Uri.TryCreate(r.Url, UriKind.Absolute, out _))
                    .Select(r => new SearchResultEnvelope(query, r)));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogInformation("Web search timed out for query: {Query}, continuing with results so far", query);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Web search failed for query: {Query}", query);
            }
        }

        return results;
    }

    private async Task<List<SearchResultEnvelope>> RunCatalogMatchAsync(
        StructuredDiscoveryIntent parsedIntent, CancellationToken ct)
    {
        var catalog = await LoadGroundedCatalogAsync(ct);
        var matches = FilterCatalogByIntent(catalog, parsedIntent);

        return matches.Select(entry => new SearchResultEnvelope(
            "catalog",
            new MergedSearchResult
            {
                Url = entry.Url,
                Title = entry.Name,
                Snippet = $"Known portal: {string.Join(", ", entry.Tags)}",
                BestPosition = 0,
                ProviderIds = ["catalog"]
            })).ToList();
    }

    /// <summary>
    /// Filters catalog entries by matching location and field/tags against the parsed intent.
    /// </summary>
    private static List<CatalogEntry> FilterCatalogByIntent(List<CatalogEntry> catalog, StructuredDiscoveryIntent intent)
    {
        if (string.IsNullOrWhiteSpace(intent.Location) && string.IsNullOrWhiteSpace(intent.Field) && intent.RoleTypes.Count == 0)
            return catalog; // no filtering criteria → return all

        var locationTerms = string.IsNullOrWhiteSpace(intent.Location)
            ? []
            : DeriveLocationFilterTerms(intent.Location);

        return catalog.Where(entry =>
        {
            // If we have location terms, at least one must match
            if (locationTerms.Count > 0)
            {
                var locationMatch = entry.LocationKeywords.Any(kw =>
                    locationTerms.Any(lt => ContainsWholeTerm(kw, lt))) ||
                    entry.Tags.Any(tag =>
                    locationTerms.Any(lt => ContainsWholeTerm(tag, lt)));

                if (!locationMatch)
                    return false;
            }

            return true;
        }).ToList();
    }

    /// <summary>
    /// LLM evaluates a flat list of URLs (from search + catalog) and classifies each as career portal or not.
    /// </summary>
    private async Task<List<DiscoveredPortal>> ClassifyPortalsViaLlmAsync(
        StructuredDiscoveryIntent parsedIntent,
        string userInput,
        IReadOnlyList<SearchResultEnvelope> results,
        CancellationToken ct)
    {
        var compactResults = results
            .GroupBy(r => NormalizeDomain(r.Result.Url), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(60)
            .Select((entry, index) => new
            {
                index = index + 1,
                url = entry.Result.Url,
                title = entry.Result.Title,
                snippet = Truncate(entry.Result.Snippet, 220),
                source = entry.SearchQuery
            })
            .ToList();

        var prompt = $$"""
            You are classifying URLs to find actual career portals for job monitoring.

            User intent: "{{userInput}}"
            Location: {{parsedIntent.Location}}
            Role types: {{string.Join(", ", parsedIntent.RoleTypes)}}
            Field: {{parsedIntent.Field}}

            Candidate URLs (from web search and known catalogs):
            {{JsonSerializer.Serialize(compactResults)}}

            For EACH URL, decide: is this an actual career portal, job board results page,
            or company careers listing page relevant to the user's intent?

            INCLUDE:
            - Job board search/results pages (e.g., StepStone, Indeed, LinkedIn Jobs, Jobindex)
            - Company career pages listing open positions
            - University vacancy pages
            - Government/public job portals
            - Specialized industry job boards

            EXCLUDE:
            - Individual job postings (single position pages)
            - News articles or press releases
            - General company homepages (not the careers section)
            - Recruiter profile pages
            - Blog posts or guides about job searching
            - Social media pages

            Return a JSON array of APPROVED portals only:
            [
              {
                "url": "https://...",
                "title": "human-readable name",
                "reasoning": "brief explanation of why this is a career portal"
              }
            ]

            Return at most {{Math.Max(1, _options.MaxPortalsPerGroup)}} items, best portals first.
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
        {
            logger.LogWarning("LLM classification failed: {Error}, falling back to heuristic selection",
                response.ErrorMessage ?? "empty response");
            return FallbackPortalSelection(results);
        }

        List<PortalSelection> candidates;
        try
        {
            candidates = DeserializeOrThrow<List<PortalSelection>>(response.Content, nameof(PortalSelection));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse LLM classification response, falling back to heuristic");
            return FallbackPortalSelection(results);
        }

        return candidates
            .Where(c => Uri.TryCreate(c.Url, UriKind.Absolute, out _))
            .Select(c =>
            {
                var domain = NormalizeDomain(c.Url);
                var matchedResult = results.FirstOrDefault(r =>
                    string.Equals(r.Result.Url, c.Url, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeDomain(r.Result.Url), domain, StringComparison.OrdinalIgnoreCase));

                return new DiscoveredPortal(
                    c.Url,
                    domain,
                    string.IsNullOrWhiteSpace(c.Reasoning) ? "Classified as career portal." : c.Reasoning.Trim(),
                    c.Title ?? matchedResult?.Result.Title,
                    SetupFlowEnhancements.DetectPlatformFromUrl(c.Url),
                    matchedResult?.SearchQuery);
            })
            .ToList();
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
        else if (tld is "ch") tags.Add("switzerland");
        else if (tld is "fr") tags.Add("france");
        else if (tld is "fi") tags.Add("finland");
        else if (tld is "no") tags.Add("norway");
        else if (tld is "uk" or "ac") tags.Add("uk");
        else if (tld is "eu") tags.Add("europe");
        return tags;
    }

    /// <summary>
    /// Extracts location keywords from a user intent string by matching known location terms.
    /// </summary>
    private static List<string> ExtractLocationFromIntent(string intent)
    {
        var locations = new List<string>();
        ReadOnlySpan<string> known =
        [
            "denmark", "copenhagen", "czech", "prague",
            "sweden", "stockholm", "malmö", "lund",
            "germany", "berlin", "munich", "hamburg", "frankfurt", "heidelberg",
            "netherlands", "amsterdam", "leiden", "rotterdam",
            "austria", "vienna", "graz",
            "belgium", "brussels", "ghent",
            "switzerland", "basel", "zurich",
            "france", "paris", "lyon",
            "finland", "helsinki",
            "norway", "oslo",
            "uk", "united kingdom", "london", "cambridge", "oxford",
            "europe"
        ];
        foreach (var loc in known)
        {
            if (ContainsWholeTerm(intent, loc))
                locations.Add(loc);
        }
        return locations.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

    private static bool IsLocationTag(string tag)
    {
        ReadOnlySpan<string> locationTags =
        [
            "denmark", "copenhagen", "czech", "prague", "sweden", "stockholm", "malmö", "lund",
            "germany", "berlin", "munich", "hamburg", "frankfurt", "heidelberg",
            "netherlands", "amsterdam", "leiden", "rotterdam",
            "austria", "vienna", "graz",
            "belgium", "brussels", "ghent", "flanders",
            "switzerland", "basel", "zurich",
            "france", "paris", "lyon", "strasbourg",
            "finland", "helsinki", "turku",
            "norway", "oslo", "bergen",
            "uk", "london", "cambridge", "oxford",
            "europe", "eu", "nordic", "scandinavia", "dach", "international"
        ];

        foreach (var loc in locationTags)
        {
            if (ContainsWholeTerm(tag, loc))
                return true;
        }
        return false;
    }

    private static bool ContainsWholeTerm(string text, string term)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(term))
            return false;

        return Regex.IsMatch(
            text,
            $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

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

}
