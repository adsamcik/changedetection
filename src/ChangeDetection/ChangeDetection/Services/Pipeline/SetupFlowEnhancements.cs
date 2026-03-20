using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.Pipeline;

public enum DetectedContentType
{
    Unknown,
    JobListing,
    ApiJson,
    ProductListing,
    NewsFeed,
    JsShell,
    ErrorPage
}

public record ContentClassification(
    DetectedContentType Type,
    double Confidence,
    string[] Signals,
    string? DetectedPlatform);

public record JsDetectionResult(
    bool IsJsShell,
    string[] Signals,
    bool ShouldRetryWithPlaywright);

public sealed record RelevanceKeyword(string Keyword, int Weight);

public partial class SetupFlowEnhancements(
    ILogger<SetupFlowEnhancements> logger,
    IHttpClientFactory? httpClientFactory = null)
{
    private const string WorkdayJobsRequestBody = """{"appliedFacets":{},"limit":20,"offset":0,"searchText":""}""";
    private static readonly string[] WorkdaySiteIdVariants = ["{siteId}_careers", "{siteId}_external", "External", "Careers"];
    private static readonly IReadOnlyList<RelevanceKeyword> DefaultWorkdayPositiveKeywords =
    [
        new("scientist", 10),
        new("research", 8),
        new("laboratory", 5),
        new("biotech", 5)
    ];

    private static readonly IReadOnlyList<RelevanceKeyword> DefaultWorkdayNegativeKeywords =
    [
        new("director", -15),
        new("VP", -20)
    ];

    private static readonly string[] JobUrlKeywords =
    [
        "/jobs", "/careers", "/vacancies", "/positions", "/openings", "/stellenangebote", "/stillinger"
    ];

    private static readonly string[] JobVocabulary =
    [
        "apply", "deadline", "location", "salary", "full-time", "part-time", "department"
    ];

    // Cookie-related patterns are NOT errors — they're normal pages with consent banners
    // that need Playwright to dismiss. Cookie wall detection is handled in ComposableSetupPipeline.
    private static readonly string[] ErrorPatterns =
    [
        "captcha", "verify you are human", "access denied", "403 forbidden", "rate limit"
    ];

    private static readonly string[] CookieWallPatterns =
    [
        "cookie consent", "cookie policy", "cookie information", "accept all cookies",
        "accept all", "gdpr", "privacy policy"
    ];

    private static readonly string[] ProductUrlKeywords = ["/product", "/products", "/shop", "/store"];
    private static readonly string[] ProductVocabulary =
    [
        "add to cart", "buy now", "price", "sku", "in stock", "out of stock", "variant"
    ];

    private static readonly string[] NewsUrlKeywords = ["/news", "/blog", "/feed", "/rss"];
    private static readonly string[] NewsVocabulary =
    [
        "published", "author", "read more", "rss", "article", "category", "subscribe"
    ];

    public ContentClassification ClassifyContent(string content, string url, string? contentType)
    {
        content ??= string.Empty;
        url ??= string.Empty;
        var trimmed = content.TrimStart();
        var platform = DetectPlatform(url, content);

        if ((contentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) ?? false) ||
            trimmed.StartsWith('{') ||
            trimmed.StartsWith('['))
        {
            return LogClassification(new ContentClassification(
                DetectedContentType.ApiJson,
                0.95,
                [contentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true
                    ? "Content-Type: application/json"
                    : "Content starts with a JSON token"],
                platform));
        }

        var jsDetection = DetectJsShell(content, content.Length);
        if (jsDetection.IsJsShell)
        {
            return LogClassification(new ContentClassification(
                DetectedContentType.JsShell,
                0.85,
                jsDetection.Signals,
                platform));
        }

        var jobSignals = new List<string>();
        var urlMatchesJobs = ContainsAny(url, JobUrlKeywords);
        var jobHits = CountVocabularyHits(content, JobVocabulary, out var matchedJobTerms);
        if (urlMatchesJobs)
            jobSignals.Add("Career URL pattern matched");
        if (jobHits > 0)
            jobSignals.Add($"Job vocabulary hits: {string.Join(", ", matchedJobTerms)}");

        var jobConfidence = urlMatchesJobs && jobHits >= 5
            ? 0.9
            : urlMatchesJobs && jobHits >= 3
                ? 0.7
                : jobHits >= 3
                    ? 0.5
                    : 0.0;

        if (jobConfidence >= 0.7)
        {
            return LogClassification(new ContentClassification(
                DetectedContentType.JobListing,
                jobConfidence,
                jobSignals.ToArray(),
                platform));
        }

        var errorPattern = ErrorPatterns.FirstOrDefault(pattern =>
            content.Contains(pattern, StringComparison.OrdinalIgnoreCase));
        if (errorPattern is not null)
        {
            // Don't classify as error if this is actually a cookie wall page
            var isCookieWall = CookieWallPatterns.Any(p =>
                content.Contains(p, StringComparison.OrdinalIgnoreCase));
            if (!isCookieWall)
            {
                return LogClassification(new ContentClassification(
                    DetectedContentType.ErrorPage,
                    0.9,
                    [$"Error page detected: {errorPattern}"],
                    platform));
            }

            logger.LogDebug("Error pattern '{Pattern}' found but page appears to be a cookie wall, not an error page", errorPattern);
        }

        var productUrlMatch = ContainsAny(url, ProductUrlKeywords);
        var productHits = CountVocabularyHits(content, ProductVocabulary, out var matchedProductTerms);
        if ((productUrlMatch && productHits >= 4) || productHits >= 5)
        {
            return LogClassification(new ContentClassification(
                DetectedContentType.ProductListing,
                productUrlMatch ? 0.8 : 0.72,
                [$"Product signals: {string.Join(", ", matchedProductTerms)}"],
                platform));
        }

        var newsUrlMatch = ContainsAny(url, NewsUrlKeywords);
        var newsHits = CountVocabularyHits(content, NewsVocabulary, out var matchedNewsTerms);
        if ((newsUrlMatch && newsHits >= 4) || content.Contains("<rss", StringComparison.OrdinalIgnoreCase))
        {
            return LogClassification(new ContentClassification(
                DetectedContentType.NewsFeed,
                content.Contains("<rss", StringComparison.OrdinalIgnoreCase) ? 0.9 : 0.75,
                [$"News/feed signals: {string.Join(", ", matchedNewsTerms.DefaultIfEmpty("rss"))}"],
                platform));
        }

        return LogClassification(new ContentClassification(
            DetectedContentType.Unknown,
            0.0,
            ["No high-confidence heuristic matched"],
            platform));
    }

    public JsDetectionResult DetectJsShell(string htmlContent, int contentLength)
    {
        htmlContent ??= string.Empty;
        var signals = new List<string>();
        var visibleText = ExtractVisibleText(htmlContent);
        var scriptTagCount = CountOccurrences(htmlContent, "<script");
        var scriptChars = ScriptContentRegex().Matches(htmlContent).Sum(match => match.Length);
        var ratio = scriptChars / (double)Math.Max(1, visibleText.Length);
        var hasSpaMarker =
            htmlContent.Contains("__NEXT_DATA__", StringComparison.OrdinalIgnoreCase) ||
            htmlContent.Contains("__NUXT__", StringComparison.OrdinalIgnoreCase) ||
            htmlContent.Contains("window.__INITIAL_STATE__", StringComparison.OrdinalIgnoreCase);

        if (contentLength < 5000 && scriptTagCount > 3 && visibleText.Length < 200)
            signals.Add($"SPA shell detected ({scriptTagCount} script tags, {visibleText.Length} visible chars)");
        else if (contentLength < 5000 && ratio > 3.0)
            signals.Add($"High script-to-text ratio detected ({ratio:F1})");

        if (EmptyRootRegex().IsMatch(htmlContent))
            signals.Add("Empty root/app mount detected");

        if (NoScriptRegex().IsMatch(htmlContent))
            signals.Add("Noscript JavaScript-required message detected");

        if (visibleText.Length < 200)
            signals.Add($"Very little visible text detected ({visibleText.Length} chars)");

        if (hasSpaMarker)
            signals.Add("SPA hydration marker detected");

        var isJsShell = hasSpaMarker ||
                        signals.Count(signal => !signal.Contains("Very little visible text", StringComparison.Ordinal)) >= 2 ||
                        (contentLength < 5000 && ratio > 3.0 && visibleText.Length < 200);

        return new JsDetectionResult(
            isJsShell,
            signals.ToArray(),
            isJsShell);
    }

    public async Task<PipelineDefinition?> GetPlatformTemplateAsync(
        string platformId,
        string url,
        IReadOnlyList<RelevanceKeyword>? positiveKeywords = null,
        IReadOnlyList<RelevanceKeyword>? negativeKeywords = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(platformId))
            return null;

        var normalized = platformId.Trim().ToLowerInvariant();
        PipelineDefinition? template;
        switch (normalized)
        {
            case "workday":
            {
                var transformedUrl = TransformWorkdayUrl(url) ?? url;
                var resolvedUrl = await ProbeWorkdayApiAsync(transformedUrl, ct) ?? transformedUrl;
                template = CreateWorkdayTemplate(
                    resolvedUrl,
                    positiveKeywords,
                    negativeKeywords);
                break;
            }
            case "platsbanken":
                template = CreatePlatsbankenTemplate(TransformPlatsbankenUrl(url) ?? url);
                break;
            case "teamtailor":
                template = CreateTeamtailorTemplate(url);
                break;
            case "workable":
                template = CreateWorkableTemplate(url);
                break;
            default:
                template = null;
                break;
        }

        if (template is not null)
            logger.LogDebug("Selected platform template {PlatformId} for {Url}", normalized, url);

        return template;
    }

    /// <summary>
    /// Transforms a Workday career page URL to the corresponding JSON API URL.
    /// Career: https://{subdomain}.{instance}.myworkdayjobs.com/en-US/{siteId}
    /// API:    https://{subdomain}.{instance}.myworkdayjobs.com/wday/cxs/{subdomain}/{siteId}/jobs
    /// </summary>
    public static string? TransformWorkdayUrl(string careerPageUrl)
    {
        if (string.IsNullOrWhiteSpace(careerPageUrl))
            return null;

        var trimmedUrl = careerPageUrl.Trim();

        var apiMatch = WorkdayApiUrlRegex().Match(trimmedUrl);
        if (apiMatch.Success)
        {
            var apiHost = apiMatch.Groups["host"].Value;
            var apiSubdomain = apiMatch.Groups["subdomain"].Value;
            var apiSiteId = apiMatch.Groups["siteId"].Value;
            return BuildWorkdayApiUrl(apiHost, apiSubdomain, apiSiteId);
        }

        var careerMatch = WorkdayCareerUrlRegex().Match(trimmedUrl);
        if (!careerMatch.Success)
            return null;

        var host = careerMatch.Groups["host"].Value;
        var subdomain = careerMatch.Groups["subdomain"].Value;
        var siteId = careerMatch.Groups["siteId"].Value;
        return BuildWorkdayApiUrl(host, subdomain, siteId);
    }

    private async Task<string?> ProbeWorkdayApiAsync(string apiUrl, CancellationToken ct)
    {
        if (httpClientFactory is null)
            return null;

        var apiMatch = WorkdayApiUrlRegex().Match(apiUrl);
        if (!apiMatch.Success)
            return null;

        var host = apiMatch.Groups["host"].Value;
        var subdomain = apiMatch.Groups["subdomain"].Value;
        var siteId = apiMatch.Groups["siteId"].Value;

        var initialStatusCode = await ProbeWorkdayCandidateAsync(apiUrl, ct);
        if (initialStatusCode == HttpStatusCode.OK)
            return apiUrl;

        if (initialStatusCode != HttpStatusCode.NotFound)
        {
            logger.LogDebug(
                "Workday API probe for {Url} returned {StatusCode}; keeping transformed URL",
                apiUrl,
                initialStatusCode?.ToString() ?? "no response");
            return null;
        }

        foreach (var variantTemplate in WorkdaySiteIdVariants)
        {
            var candidateSiteId = variantTemplate.Contains("{siteId}", StringComparison.Ordinal)
                ? variantTemplate.Replace("{siteId}", siteId, StringComparison.Ordinal)
                : variantTemplate;

            var candidateUrl = BuildWorkdayApiUrl(host, subdomain, candidateSiteId);
            var candidateStatusCode = await ProbeWorkdayCandidateAsync(candidateUrl, ct);
            if (candidateStatusCode != HttpStatusCode.OK)
                continue;

            logger.LogInformation(
                "Resolved Workday siteId mismatch for {Url}; using {CandidateUrl}",
                apiUrl,
                candidateUrl);
            return candidateUrl;
        }

        logger.LogWarning(
            "Workday API probe could not resolve a live jobs endpoint for {Url}; falling back to transformed URL",
            apiUrl);
        return null;
    }

    private async Task<HttpStatusCode?> ProbeWorkdayCandidateAsync(string candidateUrl, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            var client = httpClientFactory!.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, candidateUrl);
            request.Headers.Accept.ParseAdd("application/json");
            request.Content = new StringContent(WorkdayJobsRequestBody, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            return response.StatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug("Timed out probing Workday API candidate {Url}", candidateUrl);
            return null;
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "HTTP error probing Workday API candidate {Url}", candidateUrl);
            return null;
        }
    }

    /// <summary>
    /// Transforms a Platsbanken (arbetsformedlingen.se) URL to the JobTech API endpoint.
    /// Frontend: https://arbetsformedlingen.se/platsbanken/annonser?q=scientist
    /// API:      https://jobsearch.api.jobtechdev.se/search?q=scientist&amp;offset=0&amp;limit=100
    /// Already-API URLs (jobtechdev.se) are normalized with offset/limit defaults.
    /// </summary>
    public static string? TransformPlatsbankenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var trimmed = url.Trim().TrimEnd('#');

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            // Already an API URL — normalize offset/limit
            if (uri.Host.Contains("jobtechdev.se", StringComparison.OrdinalIgnoreCase))
            {
                var apiParams = HttpUtility.ParseQueryString(uri.Query);
                apiParams["offset"] ??= "0";
                apiParams["limit"] ??= "100";
                return $"https://jobsearch.api.jobtechdev.se/search?{apiParams}";
            }

            // Frontend arbetsformedlingen.se URL — extract search query
            if (uri.Host.Contains("arbetsformedlingen.se", StringComparison.OrdinalIgnoreCase))
            {
                var frontendParams = HttpUtility.ParseQueryString(uri.Query);
                var query = frontendParams["q"] ?? "";
                var apiQuery = HttpUtility.ParseQueryString(string.Empty);
                if (!string.IsNullOrEmpty(query))
                    apiQuery["q"] = query;
                apiQuery["offset"] = "0";
                apiQuery["limit"] = "100";
                return $"https://jobsearch.api.jobtechdev.se/search?{apiQuery}";
            }
        }

        return null;
    }

    private ContentClassification LogClassification(ContentClassification classification)
    {
        logger.LogDebug(
            "Heuristic classified content as {Type} with confidence {Confidence} on platform {Platform}",
            classification.Type,
            classification.Confidence,
            classification.DetectedPlatform ?? "unknown");
        return classification;
    }

    private static PipelineDefinition CreateWorkdayTemplate(
        string url,
        IReadOnlyList<RelevanceKeyword>? positiveKeywords = null,
        IReadOnlyList<RelevanceKeyword>? negativeKeywords = null)
    {
        var apiBase = url.EndsWith("/jobs", StringComparison.OrdinalIgnoreCase) ? url[..^5] : url;
        var detailUrlTemplate = $"{apiBase}{{{{item.externalPath}}}}";
        var resolvedPositiveKeywords = (positiveKeywords is { Count: > 0 }
            ? positiveKeywords
            : DefaultWorkdayPositiveKeywords)
            .Select(keyword => new { keyword = keyword.Keyword, weight = keyword.Weight })
            .ToArray();
        var resolvedNegativeKeywords = (negativeKeywords is { Count: > 0 }
            ? negativeKeywords
            : DefaultWorkdayNegativeKeywords)
            .Select(keyword => new { keyword = keyword.Keyword, weight = keyword.Weight })
            .ToArray();

        return new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                Block("input-1", "Input", 0, new { url }),
                Block("httprequest-1", "HttpRequest", 1, new
                {
                    method = "POST",
                    headers = new Dictionary<string, string>
                    {
                        ["Accept"] = "application/json",
                        ["Content-Type"] = "application/json"
                    },
                    body = WorkdayJobsRequestBody,
                    timeout = 30000,
                    acceptJsonOnly = true
                }),
                Block("paginate-1", "Paginate", 2, new
                {
                    mode = "offset",
                    parameterLocation = "body",
                    offsetField = "offset",
                    limitField = "limit",
                    limitValue = 20,
                    totalFrom = "$.total",
                    startOffset = 0,
                    maxPages = 5
                }),
                Block("jsonextract-1", "JsonExtract", 3, new
                {
                    extractions = new[]
                    {
                        new { name = "items", jsonpath = "$.pages[*].jobPostings[*]", type = "array" },
                        new { name = "total", jsonpath = "$.pages[0].total", type = "number" }
                    }
                }),
                Block("foreach-1", "ForEachRequest", 4, new
                {
                    request = new
                    {
                        urlTemplate = detailUrlTemplate,
                        method = "GET",
                        headers = new { Accept = "application/json" }
                    },
                    extract = new
                    {
                        format = "json",
                        mappings = new[]
                        {
                            new { source = "$.jobPostingInfo.jobDescription", target = "description" },
                            new { source = "$.jobPostingInfo.additionalInformation", target = "requirements" }
                        }
                    },
                    rateLimit = new { delayMs = 300, maxConcurrent = 2 },
                    maxItems = 50
                }),
                Block("datafilter-1", "DataFilter", 5, new
                {
                    conditions = new[]
                    {
                        new { field = "locationsText", @operator = "contains", value = "Copenhagen" },
                        new { field = "locationsText", @operator = "contains", value = "Denmark" }
                    },
                    mode = "any"
                }),
                Block("relevancescore-1", "RelevanceScore", 6, new
                {
                    targetFields = new[] { "title", "locationsText" },
                    positiveKeywords = resolvedPositiveKeywords,
                    negativeKeywords = resolvedNegativeKeywords,
                    minScore = 0
                }),
                Block("listdiff-1", "ListDiff", 7, new { identityKey = "title", mode = "all_changes" }),
                Block("condition-1", "Condition", 8, new
                {
                    field = "changed",
                    @operator = "equals",
                    value = true
                }),
                Block("notify-1", "Notify", 9, new
                {
                    template = "New job postings detected on {watchName}"
                }),
                Block("output-1", "Output", 10)
            ],
            Connections =
            [
                Connect("input-1", "url", "httprequest-1", "url"),
                Connect("httprequest-1", "response", "paginate-1", "json"),
                Connect("paginate-1", "data", "jsonextract-1", "json"),
                Connect("jsonextract-1", "data", "foreach-1", "items"),
                Connect("foreach-1", "data", "datafilter-1", "data"),
                Connect("datafilter-1", "filtered", "relevancescore-1", "data"),
                Connect("relevancescore-1", "result", "listdiff-1", "data"),
                Connect("listdiff-1", "result", "condition-1", "result"),
                Connect("condition-1", "signal", "notify-1", "signal"),
                Connect("listdiff-1", "result", "notify-1", "data"),
                Connect("listdiff-1", "result", "output-1", "data")
            ],
            Metadata = CreateMetadata("Workday API template", "Monitor Workday job postings", "jobs")
        };
    }

    private static PipelineDefinition CreatePlatsbankenTemplate(string url) =>
        new()
        {
            SchemaVersion = 1,
            Blocks =
            [
                Block("input-1", "Input", 0, new { url }),
                Block("httprequest-1", "HttpRequest", 1, new
                {
                    method = "GET",
                    headers = new Dictionary<string, string>
                    {
                        ["Accept"] = "application/json"
                    },
                    timeout = 30000,
                    acceptJsonOnly = true
                }),
                Block("jsonextract-1", "JsonExtract", 2, new
                {
                    extractions = new[]
                    {
                        new { name = "items", jsonpath = "$.hits[*]", type = "array" }
                    }
                }),
                Block("listdiff-1", "ListDiff", 3, new { identityKey = "id", mode = "all_changes" }),
                Block("condition-1", "Condition", 4, new
                {
                    field = "changed",
                    @operator = "equals",
                    value = true
                }),
                Block("notify-1", "Notify", 5, new
                {
                    template = "New job postings detected on {watchName}"
                }),
                Block("output-1", "Output", 6)
            ],
            Connections =
            [
                Connect("input-1", "url", "httprequest-1", "url"),
                Connect("httprequest-1", "json", "jsonextract-1", "json"),
                Connect("jsonextract-1", "data", "listdiff-1", "data"),
                Connect("listdiff-1", "result", "condition-1", "result"),
                Connect("condition-1", "signal", "notify-1", "signal"),
                Connect("listdiff-1", "result", "notify-1", "data"),
                Connect("listdiff-1", "result", "output-1", "data")
            ],
            Metadata = CreateMetadata("Platsbanken API template", "Monitor Platsbanken listings", "jobs")
        };

    private static PipelineDefinition CreateTeamtailorTemplate(string url) =>
        new()
        {
            SchemaVersion = 1,
            Blocks =
            [
                Block("input-1", "Input", 0, new { url }),
                Block("navigate-1", "Navigate", 1, new
                {
                    useJavaScript = true,
                    timeout = 30000,
                    waitForSelector = "#jobs_list_container li"
                }),
                Block("extractschema-1", "ExtractSchema", 2, new
                {
                    scope = "#jobs_list_container > li",
                    listMode = true,
                    schema = new object[]
                    {
                        new { field = "url", selector = "a[href*='/jobs/']" },
                        new { field = "location", selector = "div.text-md, div + div" }
                    },
                    enableLlmFallback = false
                }),
                Block("listdiff-1", "ListDiff", 3, new { identityKey = "url", mode = "all_changes" }),
                Block("condition-1", "Condition", 4, new
                {
                    field = "changed",
                    @operator = "equals",
                    value = true
                }),
                Block("notify-1", "Notify", 5, new
                {
                    template = "New job postings detected on {watchName}"
                }),
                Block("output-1", "Output", 6)
            ],
            Connections =
            [
                Connect("input-1", "url", "navigate-1", "url"),
                Connect("navigate-1", "html", "extractschema-1", "html"),
                Connect("extractschema-1", "data", "listdiff-1", "data"),
                Connect("listdiff-1", "result", "condition-1", "result"),
                Connect("condition-1", "signal", "notify-1", "signal"),
                Connect("listdiff-1", "result", "notify-1", "data"),
                Connect("listdiff-1", "result", "output-1", "data")
            ],
            Metadata = CreateMetadata("Teamtailor browser template", "Monitor Teamtailor job links", "jobs")
        };

    private static PipelineDefinition CreateWorkableTemplate(string url) =>
        new()
        {
            SchemaVersion = 1,
            Blocks =
            [
                Block("input-1", "Input", 0, new { url }),
                Block("navigate-1", "Navigate", 1, new
                {
                    useJavaScript = true,
                    timeout = 30000,
                    waitForSelector = "main li h3, [data-ui='job-opening'] h3"
                }),
                Block("extractschema-1", "ExtractSchema", 2, new
                {
                    scope = "li a[href], [data-ui='job-opening'] a[href]",
                    listMode = true,
                    schema = new object[]
                    {
                        new { field = "title", selector = "h3, span, *" },
                        new { field = "url", selector = "a[href]" },
                        new { field = "location", selector = ".location, [data-ui='job-location'], span" }
                    },
                    enableLlmFallback = false
                }),
                Block("listdiff-1", "ListDiff", 3, new { identityKey = "url", mode = "all_changes" }),
                Block("condition-1", "Condition", 4, new
                {
                    field = "changed",
                    @operator = "equals",
                    value = true
                }),
                Block("notify-1", "Notify", 5, new
                {
                    template = "New job postings detected on {watchName}"
                }),
                Block("output-1", "Output", 6)
            ],
            Connections =
            [
                Connect("input-1", "url", "navigate-1", "url"),
                Connect("navigate-1", "html", "extractschema-1", "html"),
                Connect("extractschema-1", "data", "listdiff-1", "data"),
                Connect("listdiff-1", "result", "condition-1", "result"),
                Connect("condition-1", "signal", "notify-1", "signal"),
                Connect("listdiff-1", "result", "notify-1", "data"),
                Connect("listdiff-1", "result", "output-1", "data")
            ],
            Metadata = CreateMetadata("Workable browser template", "Monitor Workable job openings", "jobs")
        };

    private static PipelineMetadata CreateMetadata(string title, string intent, string cardType) =>
        new()
        {
            DisplayTitle = title,
            CreatedAt = DateTime.UtcNow,
            UserIntent = intent,
            CardType = cardType,
            EstimatedLlmCallsPerRun = 0
        };

    private static BlockDefinition Block(string id, string type, int position, object? config = null) =>
        new()
        {
            Id = id,
            Type = type,
            Position = position,
            Config = config is null ? null : JsonSerializer.SerializeToElement(config)
        };

    private static ConnectionDefinition Connect(string fromBlockId, string fromPort, string toBlockId, string toPort) =>
        new()
        {
            FromBlockId = fromBlockId,
            FromPort = fromPort,
            ToBlockId = toBlockId,
            ToPort = toPort
        };

    private static string BuildWorkdayApiUrl(string host, string subdomain, string siteId) =>
        $"https://{host}/wday/cxs/{subdomain}/{siteId}/jobs";

    /// <summary>
    /// Detects platform from URL only (no content needed). Used for fast-path platform detection
    /// before fetching or LLM analysis.
    /// </summary>
    public static string? DetectPlatformFromUrl(string url) => DetectPlatform(url, "");

    private static string? DetectPlatform(string url, string content)
    {
        if (url.Contains("myworkdayjobs.com", StringComparison.OrdinalIgnoreCase))
            return "workday";
        if (url.Contains("teamtailor.com", StringComparison.OrdinalIgnoreCase))
            return "teamtailor";
        if (url.Contains("successfactors", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("jobs.sap.com", StringComparison.OrdinalIgnoreCase))
            return "successfactors";
        if (content.Contains("apply.workable.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("workable.com", StringComparison.OrdinalIgnoreCase))
            return "workable";
        if (url.Contains("greenhouse.io", StringComparison.OrdinalIgnoreCase))
            return "greenhouse";
        if (url.Contains("lever.co", StringComparison.OrdinalIgnoreCase))
            return "lever";
        if (url.Contains("jobindex.dk", StringComparison.OrdinalIgnoreCase))
            return "jobindex";
        if (url.Contains("jobtechdev.se", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("arbetsformedlingen.se/platsbanken", StringComparison.OrdinalIgnoreCase))
            return "platsbanken";
        if (url.Contains("oraclecloud.com/hcmUI", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("oraclecloud.com/hcmRestApi", StringComparison.OrdinalIgnoreCase))
            return "oracle_hcm";

        return null;
    }

    private static int CountVocabularyHits(string content, IEnumerable<string> vocabulary, out string[] matchedTerms)
    {
        var matches = vocabulary
            .Where(term => content.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        matchedTerms = matches;
        return matches.Length;
    }

    private static bool ContainsAny(string value, IEnumerable<string> terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static int CountOccurrences(string value, string token)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(token))
            return 0;

        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static string ExtractVisibleText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var withoutScripts = ScriptContentRegex().Replace(html, " ");
        var withoutStyles = StyleContentRegex().Replace(withoutScripts, " ");
        var withoutTags = HtmlTagRegex().Replace(withoutStyles, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return WhitespaceRegex().Replace(decoded, " ").Trim();
    }

    [GeneratedRegex(@"<script\b[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 200)]
    private static partial Regex ScriptContentRegex();

    [GeneratedRegex(@"<style\b[^>]*>.*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 200)]
    private static partial Regex StyleContentRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"<div\b[^>]*id\s*=\s*[""'](?:root|app)[""'][^>]*>\s*</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 200)]
    private static partial Regex EmptyRootRegex();

    [GeneratedRegex(@"<noscript\b[^>]*>.*?enable\s+javascript.*?</noscript>", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 200)]
    private static partial Regex NoScriptRegex();

    [GeneratedRegex(@"https?://(?<host>(?<subdomain>[a-z0-9-]+)\.(?<instance>wd\d+)\.myworkdayjobs\.com)/(?:[a-z]{2}(?:-[A-Z]{2})?/)?(?<siteId>[^/?#]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex WorkdayCareerUrlRegex();

    [GeneratedRegex(@"https?://(?<host>(?<subdomain>[a-z0-9-]+)\.(?<instance>wd\d+)\.myworkdayjobs\.com)/wday/cxs/[^/]+/(?<siteId>[^/?#]+)(?:/jobs)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex WorkdayApiUrlRegex();
}
