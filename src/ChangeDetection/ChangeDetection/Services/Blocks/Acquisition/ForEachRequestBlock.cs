using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.BlockExecution;
using Fizzler.Systems.HtmlAgilityPack;
using HtmlAgilityPack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.Blocks.Acquisition;

/// <summary>
/// Iterates over an array of items, makes an HTTP request per item (with templated URL),
/// extracts fields from each response, and merges them back into the items.
/// Enables the "list → detail" fetch pattern used by most API-based job scrapers.
/// </summary>
public class ForEachRequestBlock : IPipelineBlock
{
    private const int MaxResponseBytes = 5 * 1024 * 1024; // 5 MB
    private const int DefaultTimeoutMs = 30_000;
    private const int MaxTimeoutMs = 60_000;
    private const int DefaultDelayMs = 500;
    private const int MinDelayMs = 100;
    private const int DefaultMaxConcurrent = 1;
    private const int MaxMaxConcurrent = 5;
    private const int DefaultMaxItems = 100;
    private const int HardMaxItems = 500;

    private static readonly Regex TemplateTokenRegex = new(
        @"\{\{item\.([a-zA-Z_][a-zA-Z0-9_.]*)\}\}",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(2));

    public string BlockType => "ForEachRequest";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "items", Type = PortType.ExtractedObjects }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Extraction;

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        // --- 1. Read items from input ---
        if (!context.Inputs.TryGetValue("items", out var itemsElement))
            return BlockResult.Failed("ForEachRequest block requires an 'items' input.");

        if (itemsElement.ValueKind != JsonValueKind.Array)
            return BlockResult.Failed("ForEachRequest block expects a JSON array on the 'items' input.");

        // --- 2. Read config ---
        var config = ReadConfig(context);
        if (string.IsNullOrWhiteSpace(config.UrlTemplate))
            return BlockResult.Failed("ForEachRequest block requires 'request.urlTemplate' in config.");

        // --- 3. Truncate items to maxItems ---
        var sourceItems = itemsElement.EnumerateArray()
            .Take(config.MaxItems)
            .Select(x => x.Clone())
            .ToList();

        if (sourceItems.Count == 0)
            return BlockResult.Succeeded(JsonSerializer.SerializeToElement(Array.Empty<object>()));

        context.Logger.LogInformation(
            "ForEachRequestBlock: processing {Count} items (maxItems={Max}, delay={Delay}ms, concurrency={Concurrency})",
            sourceItems.Count, config.MaxItems, config.DelayMs, config.MaxConcurrent);

        // --- 4. Process items with rate limiting ---
        var stopwatch = Stopwatch.StartNew();
        var results = await ProcessItemsAsync(sourceItems, config, context);
        stopwatch.Stop();

        var itemsPerSecond = stopwatch.Elapsed.TotalSeconds <= 0
            ? results.Length
            : results.Length / stopwatch.Elapsed.TotalSeconds;

        context.Logger.LogInformation(
            "ForEachRequestBlock: enriched {Count} item(s) in {Elapsed} ms ({Rate:F2} items/sec)",
            results.Length, stopwatch.ElapsedMilliseconds, itemsPerSecond);

        return BlockResult.Succeeded(JsonSerializer.SerializeToElement(results));
    }

    private async Task<JsonElement[]> ProcessItemsAsync(
        IReadOnlyList<JsonElement> sourceItems,
        ForEachConfig config,
        BlockContext context)
    {
        var results = new JsonElement[sourceItems.Count];
        using var concurrencyGate = new SemaphoreSlim(config.MaxConcurrent, config.MaxConcurrent);
        using var requestStartGate = new SemaphoreSlim(1, 1);
        var lastRequestStartedAt = DateTimeOffset.MinValue;

        var tasks = sourceItems.Select(async (item, index) =>
        {
            await concurrencyGate.WaitAsync(context.CancellationToken);
            try
            {
                await WaitForNextRequestSlotAsync(
                    requestStartGate,
                    TimeSpan.FromMilliseconds(config.DelayMs),
                    () => lastRequestStartedAt,
                    ts => lastRequestStartedAt = ts,
                    context.CancellationToken);

                results[index] = await ProcessSingleItemAsync(item, config, context);
            }
            finally
            {
                concurrencyGate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    private async Task<JsonElement> ProcessSingleItemAsync(
        JsonElement item,
        ForEachConfig config,
        BlockContext context)
    {
        var dict = CloneItemToDictionary(item);

        // a. Resolve URL template
        string resolvedUrl;
        try
        {
            resolvedUrl = ResolveUrlTemplate(config.UrlTemplate!, item);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning("ForEachRequestBlock: template resolution failed: {Error}", ex.Message);
            dict["_fetchError"] = JsonSerializer.SerializeToElement($"Template resolution failed: {ex.Message}");
            return JsonSerializer.SerializeToElement(dict);
        }

        // b. Validate resolved URL against domain pin + SSRF
        var validationError = await ValidateUrl(resolvedUrl, context);
        if (validationError is not null)
        {
            context.Logger.LogWarning("ForEachRequestBlock: URL blocked for item: {Error}", validationError);
            dict["_fetchError"] = JsonSerializer.SerializeToElement(validationError);
            return JsonSerializer.SerializeToElement(dict);
        }

        // c. Make HTTP request
        string responseBody;
        try
        {
            responseBody = await FetchUrlAsync(resolvedUrl, config, context);
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            dict["_fetchError"] = JsonSerializer.SerializeToElement($"Request to '{resolvedUrl}' timed out.");
            return JsonSerializer.SerializeToElement(dict);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.LogWarning(ex, "ForEachRequestBlock: request failed for {Url}", resolvedUrl);
            dict["_fetchError"] = JsonSerializer.SerializeToElement($"Request failed: {ex.Message}");
            return JsonSerializer.SerializeToElement(dict);
        }

        // d. Parse response and extract fields
        try
        {
            ExtractAndMergeFields(responseBody, config, dict, context.Logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.LogWarning(ex, "ForEachRequestBlock: extraction failed for {Url}", resolvedUrl);
            dict["_fetchError"] = JsonSerializer.SerializeToElement($"Extraction failed: {ex.Message}");
        }

        return JsonSerializer.SerializeToElement(dict);
    }

    #region URL Template Resolution

    /// <summary>
    /// Resolves {{item.fieldName}} and {{item.nested.field}} placeholders from the current item.
    /// </summary>
    private static string ResolveUrlTemplate(string template, JsonElement item)
    {
        return TemplateTokenRegex.Replace(template, match =>
        {
            var fieldPath = match.Groups[1].Value;
            var value = ResolveItemField(item, fieldPath);
            return value is not null
                ? Uri.EscapeDataString(value)
                : throw new InvalidOperationException(
                    $"Field '{fieldPath}' not found or is null in item.");
        });
    }

    /// <summary>
    /// Resolves a dot-notation path (e.g. "nested.field") from a JSON element.
    /// </summary>
    private static string? ResolveItemField(JsonElement item, string fieldPath)
    {
        var current = item;
        foreach (var segment in fieldPath.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => current.GetRawText()
        };
    }

    #endregion

    #region Security Validation

    private static async Task<string?> ValidateUrl(string url, BlockContext context)
    {
        // Domain pin validation
        if (context.DomainPin is { } pin)
        {
            var pinValidator = context.Services.GetRequiredService<DomainPinValidator>();
            var pinError = await pinValidator.ValidateWithDnsResolution(url, pin, context.CancellationToken);
            if (pinError is not null)
                return $"Domain pin blocked: {pinError}";
        }

        // SSRF validation
        var urlValidator = context.Services.GetRequiredService<IUrlValidator>();
        var ssrfError = urlValidator.Validate(url);
        if (ssrfError is not null)
            return $"URL blocked by SSRF check: {ssrfError}";

        return null;
    }

    #endregion

    #region HTTP Request

    private async Task<string> FetchUrlAsync(
        string url,
        ForEachConfig config,
        BlockContext context)
    {
        // Try PinnedHttpClient first, fall back to IHttpClientFactory
        var pinnedClient = context.Services.GetService<PinnedHttpClient>();
        if (pinnedClient is not null && context.DomainPin is { } domainPin)
        {
            return await FetchViaPinnedClient(pinnedClient, domainPin, url, config, context);
        }

        return await FetchViaHttpClientFactory(url, config, context);
    }

    private static async Task<string> FetchViaPinnedClient(
        PinnedHttpClient pinnedClient,
        DomainPin pin,
        string url,
        ForEachConfig config,
        BlockContext context)
    {
        var budget = context.Services.GetService<ExecutionBudget>() ?? new ExecutionBudget();
        var method = config.Method == "POST" ? HttpMethod.Post : HttpMethod.Get;

        HttpContent? content = null;
        if (config.Method == "POST" && config.Body is not null)
            content = new StringContent(config.Body, Encoding.UTF8, "application/json");

        using var response = await pinnedClient.SendAsync(
            url, pin, budget, method, content, config.Headers, context.CancellationToken);

        return await response.Content.ReadAsStringAsync(context.CancellationToken);
    }

    private static async Task<string> FetchViaHttpClientFactory(
        string url,
        ForEachConfig config,
        BlockContext context)
    {
        var httpClientFactory = context.Services.GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient("ForEachRequestBlock");
        client.Timeout = Timeout.InfiniteTimeSpan;

        var method = config.Method == "POST" ? HttpMethod.Post : HttpMethod.Get;
        using var request = new HttpRequestMessage(method, url);

        // Prevent zip bombs
        request.Headers.AcceptEncoding.Clear();
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

        if (config.Method == "POST" && config.Body is not null)
            request.Content = new StringContent(config.Body, Encoding.UTF8, "application/json");

        if (config.Headers is not null)
        {
            foreach (var (name, value) in config.Headers)
            {
                if (string.Equals(name, "Accept-Encoding", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!request.Headers.TryAddWithoutValidation(name, value))
                    request.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        timeoutCts.CancelAfter(DefaultTimeoutMs);

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

        // Read response with size limit
        return await ReadResponseWithSizeLimit(response, context.CancellationToken)
               ?? throw new InvalidOperationException(
                   $"Response from '{url}' exceeded {MaxResponseBytes:N0} byte size limit.");
    }

    private static async Task<string?> ReadResponseWithSizeLimit(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > MaxResponseBytes)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 8192, leaveOpen: true);

        var buffer = new char[8192];
        var sb = new StringBuilder();
        long totalBytes = 0;

        while (true)
        {
            var charsRead = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (charsRead == 0)
                break;

            totalBytes += Encoding.UTF8.GetByteCount(buffer, 0, charsRead);
            if (totalBytes > MaxResponseBytes)
                return null;

            sb.Append(buffer, 0, charsRead);
        }

        return sb.ToString();
    }

    #endregion

    #region Extraction

    private static void ExtractAndMergeFields(
        string responseBody,
        ForEachConfig config,
        Dictionary<string, JsonElement> dict,
        ILogger? logger = null)
    {
        if (config.Mappings is not { Count: > 0 })
            return;

        if (string.Equals(config.ExtractFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            ExtractFromJson(responseBody, config.Mappings, dict);
        }
        else if (string.Equals(config.ExtractFormat, "html", StringComparison.OrdinalIgnoreCase))
        {
            ExtractFromHtml(responseBody, config.Mappings, dict, logger);
        }
    }

    /// <summary>
    /// Extracts fields from a JSON response using simple JSONPath (property traversal only).
    /// E.g. "$.jobPostingInfo.jobDescription" → root["jobPostingInfo"]["jobDescription"]
    /// </summary>
    private static void ExtractFromJson(
        string responseBody,
        IReadOnlyList<ExtractionMapping> mappings,
        Dictionary<string, JsonElement> dict)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        foreach (var mapping in mappings)
        {
            var value = NavigateSimpleJsonPath(root, mapping.Source);
            if (value is not null)
            {
                dict[mapping.Target] = value.Value.Clone();
            }
        }
    }

    /// <summary>
    /// Safe JSON property traversal: "$.field.subfield" → root.GetProperty("field").GetProperty("subfield").
    /// No wildcards, no array indexing — just property access.
    /// </summary>
    private static JsonElement? NavigateSimpleJsonPath(JsonElement root, string path)
    {
        // Strip leading "$." if present
        var normalizedPath = path.StartsWith("$.", StringComparison.Ordinal)
            ? path[2..]
            : path.StartsWith("$", StringComparison.Ordinal)
                ? path[1..]
                : path;

        if (string.IsNullOrEmpty(normalizedPath))
            return root;

        var current = root;
        foreach (var segment in normalizedPath.Split('.'))
        {
            if (string.IsNullOrEmpty(segment))
                continue;

            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>
    /// Extracts fields from an HTML response using CSS selectors.
    /// E.g. ".job-description" → InnerText of first matching element.
    /// </summary>
    private static void ExtractFromHtml(
        string responseBody,
        IReadOnlyList<ExtractionMapping> mappings,
        Dictionary<string, JsonElement> dict,
        ILogger? logger = null)
    {
        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(responseBody);

        foreach (var mapping in mappings)
        {
            try
            {
                var node = htmlDoc.DocumentNode
                    .QuerySelectorAll(mapping.Source)
                    .FirstOrDefault();

                if (node is not null)
                {
                    var text = HtmlEntity.DeEntitize(node.InnerText).Trim();
                    dict[mapping.Target] = JsonSerializer.SerializeToElement(text);
                }
            }
                        catch (Exception ex)
            {
                // Invalid CSS selector — skip this mapping
                logger?.LogDebug(ex, "Invalid CSS selector in ForEachRequestBlock HTML extraction: {Selector}", mapping.Source);
            }
        }
    }

    #endregion

    #region Helpers

    private static Dictionary<string, JsonElement> CloneItemToDictionary(JsonElement item)
    {
        var dict = new Dictionary<string, JsonElement>();
        if (item.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in item.EnumerateObject())
                dict[prop.Name] = prop.Value.Clone();
        }

        return dict;
    }

    private static async Task WaitForNextRequestSlotAsync(
        SemaphoreSlim requestStartGate,
        TimeSpan delayBetweenRequests,
        Func<DateTimeOffset> getLastRequestStartedAt,
        Action<DateTimeOffset> setLastRequestStartedAt,
        CancellationToken cancellationToken)
    {
        if (delayBetweenRequests <= TimeSpan.Zero)
            return;

        await requestStartGate.WaitAsync(cancellationToken);
        try
        {
            var nextAllowedStart = getLastRequestStartedAt() + delayBetweenRequests;
            var wait = nextAllowedStart - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken);

            setLastRequestStartedAt(DateTimeOffset.UtcNow);
        }
        finally
        {
            requestStartGate.Release();
        }
    }

    #endregion

    #region Config

    private static ForEachConfig ReadConfig(BlockContext context)
    {
        var config = new ForEachConfig();

        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return config;

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } root)
            return config;

        // request section
        if (root.TryGetProperty("request", out var request) && request.ValueKind == JsonValueKind.Object)
        {
            if (request.TryGetProperty("urlTemplate", out var urlTemplate) &&
                urlTemplate.ValueKind == JsonValueKind.String)
                config.UrlTemplate = urlTemplate.GetString();

            if (request.TryGetProperty("method", out var method) &&
                method.ValueKind == JsonValueKind.String)
            {
                var m = method.GetString()?.Trim().ToUpperInvariant();
                config.Method = m is "POST" ? "POST" : "GET";
            }

            if (request.TryGetProperty("headers", out var headers) &&
                headers.ValueKind == JsonValueKind.Object)
            {
                config.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in headers.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        config.Headers[prop.Name] = prop.Value.GetString()!;
                }
            }

            // Body is literal from config only — never from items (security)
            if (request.TryGetProperty("body", out var body) &&
                body.ValueKind == JsonValueKind.String)
                config.Body = body.GetString();
        }

        // extract section
        if (root.TryGetProperty("extract", out var extract) && extract.ValueKind == JsonValueKind.Object)
        {
            if (extract.TryGetProperty("format", out var format) &&
                format.ValueKind == JsonValueKind.String)
                config.ExtractFormat = format.GetString()?.ToLowerInvariant() ?? "json";

            if (extract.TryGetProperty("mappings", out var mappings) &&
                mappings.ValueKind == JsonValueKind.Array)
            {
                config.Mappings = [];
                foreach (var mapping in mappings.EnumerateArray())
                {
                    if (mapping.ValueKind != JsonValueKind.Object)
                        continue;

                    var source = mapping.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String
                        ? s.GetString()
                        : null;
                    var target = mapping.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString()
                        : null;

                    if (!string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(target))
                        config.Mappings.Add(new ExtractionMapping(source, target));
                }
            }
        }

        // rateLimit section
        if (root.TryGetProperty("rateLimit", out var rateLimit) && rateLimit.ValueKind == JsonValueKind.Object)
        {
            if (rateLimit.TryGetProperty("delayMs", out var delay) && delay.TryGetInt32(out var delayMs))
                config.DelayMs = Math.Max(MinDelayMs, delayMs);

            if (rateLimit.TryGetProperty("maxConcurrent", out var concurrent) &&
                concurrent.TryGetInt32(out var maxConcurrent))
                config.MaxConcurrent = Math.Clamp(maxConcurrent, 1, MaxMaxConcurrent);
        }

        // maxItems
        if (root.TryGetProperty("maxItems", out var maxItems) && maxItems.TryGetInt32(out var mi))
            config.MaxItems = Math.Clamp(mi, 1, HardMaxItems);

        return config;
    }

    private sealed class ForEachConfig
    {
        public string? UrlTemplate { get; set; }
        public string Method { get; set; } = "GET";
        public Dictionary<string, string>? Headers { get; set; }
        public string? Body { get; set; }
        public string ExtractFormat { get; set; } = "json";
        public List<ExtractionMapping>? Mappings { get; set; }
        public int DelayMs { get; set; } = DefaultDelayMs;
        public int MaxConcurrent { get; set; } = DefaultMaxConcurrent;
        public int MaxItems { get; set; } = DefaultMaxItems;
    }

    private sealed record ExtractionMapping(string Source, string Target);

    #endregion
}
