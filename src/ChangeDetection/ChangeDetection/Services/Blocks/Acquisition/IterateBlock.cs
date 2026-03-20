using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.BlockExecution;
using Microsoft.Extensions.DependencyInjection;

namespace ChangeDetection.Services.Blocks.Acquisition;

/// <summary>
/// Fans out a configured list of values into one HTTP request per value and merges the extracted results.
/// </summary>
public class IterateBlock : IPipelineBlock
{
    private const int MaxResponseBytes = 5 * 1024 * 1024; // 5 MB
    private const int DefaultTimeoutMs = 30_000;
    private const int MaxTimeoutMs = 60_000;
    private const int DefaultDelayMs = 300;
    private const int MinDelayMs = 100;
    private const int DefaultMaxConcurrent = 3;
    private const int MaxMaxConcurrent = 50;
    private const int DefaultMaxValues = 50;
    private const int HardMaxValues = 100;
    private const int MaxJsonPathLength = 200;
    private const int MaxArrayItems = 1000;
    private static readonly TimeSpan EvaluationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex SafeJsonPathRegex = new(
        @"^\$(\.[a-zA-Z_][a-zA-Z0-9_]*|\[\d+\]|\[\*\]|\[\d+:\d+\])*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        RegexTimeout);

    public string BlockType => "Iterate";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "url", Type = PortType.Url, Required = false }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
    [
        new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }
    ];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Acquisition;

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        var config = ReadConfig(context);

        if (config.Values.Count == 0)
            return BuildSuccess(Array.Empty<JsonElement>());

        if (string.IsNullOrWhiteSpace(config.Request.UrlTemplate))
            return BlockResult.Failed("Iterate block requires 'request.urlTemplate' in config.");

        if (string.IsNullOrWhiteSpace(config.Extract.JsonPath))
            return BlockResult.Failed("Iterate block requires 'extract.jsonpath' in config.");

        var jsonPathError = ValidateJsonPath(config.Extract.JsonPath);
        if (jsonPathError is not null)
            return BlockResult.Failed($"Iterate block rejected JSONPath: {jsonPathError}");

        var values = config.Values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(config.MaxValues)
            .ToList();

        if (values.Count == 0)
            return BuildSuccess(Array.Empty<JsonElement>());

        context.Logger.LogInformation(
            "IterateBlock: processing {Count} value(s) (maxValues={MaxValues}, delay={Delay}ms, concurrency={Concurrency})",
            values.Count, config.MaxValues, config.RateLimit.DelayMs, config.RateLimit.MaxConcurrent);

        var stopwatch = Stopwatch.StartNew();
        var extractedPerValue = await ProcessValuesAsync(values, config, context);
        stopwatch.Stop();

        var merged = extractedPerValue.SelectMany(items => items).ToList();
        var deduplicated = Deduplicate(merged, config.DeduplicateKey);

        context.Logger.LogInformation(
            "IterateBlock: extracted {ExtractedCount} item(s) from {ValueCount} value(s) in {Elapsed} ms",
            deduplicated.Count, values.Count, stopwatch.ElapsedMilliseconds);

        return BuildSuccess(deduplicated);
    }

    private async Task<IReadOnlyList<JsonElement>[]> ProcessValuesAsync(
        IReadOnlyList<string> values,
        IterateConfig config,
        BlockContext context)
    {
        var results = new IReadOnlyList<JsonElement>[values.Count];
        using var concurrencyGate = new SemaphoreSlim(config.RateLimit.MaxConcurrent, config.RateLimit.MaxConcurrent);
        using var requestStartGate = new SemaphoreSlim(1, 1);
        var lastRequestStartedAt = DateTimeOffset.MinValue;

        var tasks = values.Select(async (value, index) =>
        {
            await concurrencyGate.WaitAsync(context.CancellationToken);
            try
            {
                await WaitForNextRequestSlotAsync(
                    requestStartGate,
                    TimeSpan.FromMilliseconds(config.RateLimit.DelayMs),
                    () => lastRequestStartedAt,
                    ts => lastRequestStartedAt = ts,
                    context.CancellationToken);

                results[index] = await ProcessSingleValueAsync(value, config, context);
            }
            finally
            {
                concurrencyGate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    private async Task<IReadOnlyList<JsonElement>> ProcessSingleValueAsync(
        string value,
        IterateConfig config,
        BlockContext context)
    {
        var resolvedUrl = ResolveTemplate(config.Request.UrlTemplate!, value);
        var validationError = await ValidateUrlAsync(resolvedUrl, context);
        if (validationError is not null)
        {
            context.Logger.LogWarning(
                "IterateBlock skipped value '{Value}' because URL validation failed: {Error}",
                value, validationError);
            return [];
        }

        var resolvedBody = config.Request.BodyTemplate is { Length: > 0 }
            ? ResolveTemplate(config.Request.BodyTemplate, value)
            : null;

        string responseBody;
        try
        {
            responseBody = await FetchAsync(resolvedUrl, resolvedBody, config.Request, context);
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            context.Logger.LogWarning("IterateBlock request timed out for value '{Value}'", value);
            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.LogWarning(ex, "IterateBlock request failed for value '{Value}'", value);
            return [];
        }

        try
        {
            return ExtractItems(responseBody, config.Extract, context.CancellationToken);
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            context.Logger.LogWarning("IterateBlock extraction timed out for value '{Value}'", value);
            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.LogWarning(ex, "IterateBlock extraction failed for value '{Value}'", value);
            return [];
        }
    }

    private static string ResolveTemplate(string template, string value) =>
        template.Replace("{{value}}", Uri.EscapeDataString(value), StringComparison.Ordinal);

    private static async Task<string?> ValidateUrlAsync(string url, BlockContext context)
    {
        if (context.DomainPin is { } pin)
        {
            var pinValidator = context.Services.GetRequiredService<DomainPinValidator>();
            var pinError = await pinValidator.ValidateWithDnsResolution(url, pin, context.CancellationToken);
            if (pinError is not null)
                return $"Domain pin blocked: {pinError}";
        }

        var urlValidator = context.Services.GetRequiredService<IUrlValidator>();
        var ssrfError = urlValidator.Validate(url);
        if (ssrfError is not null)
            return $"URL blocked by SSRF check: {ssrfError}";

        return null;
    }

    private async Task<string> FetchAsync(
        string url,
        string? body,
        RequestConfig config,
        BlockContext context)
    {
        var pinnedClient = context.Services.GetService<PinnedHttpClient>();
        if (pinnedClient is not null && context.DomainPin is { } domainPin)
            return await FetchViaPinnedClientAsync(pinnedClient, domainPin, url, body, config, context);

        return await FetchViaHttpClientFactoryAsync(url, body, config, context);
    }

    private static async Task<string> FetchViaPinnedClientAsync(
        PinnedHttpClient pinnedClient,
        DomainPin pin,
        string url,
        string? body,
        RequestConfig config,
        BlockContext context)
    {
        var budget = context.Services.GetService<ExecutionBudget>() ?? new ExecutionBudget();
        HttpContent? content = null;
        if (body is not null)
            content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await pinnedClient.SendAsync(
            url,
            pin,
            budget,
            config.Method,
            content,
            config.Headers,
            context.CancellationToken);

        return await response.Content.ReadAsStringAsync(context.CancellationToken);
    }

    private static async Task<string> FetchViaHttpClientFactoryAsync(
        string url,
        string? body,
        RequestConfig config,
        BlockContext context)
    {
        var httpClientFactory = context.Services.GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient("IterateBlock");
        client.Timeout = Timeout.InfiniteTimeSpan;

        using var request = new HttpRequestMessage(config.Method, url);
        request.Headers.AcceptEncoding.Clear();
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        ApplyHeaders(request, config.Headers);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        timeoutCts.CancelAfter(config.TimeoutMs);

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeoutCts.Token);

        return await ReadResponseWithSizeLimit(response, context.CancellationToken)
               ?? throw new InvalidOperationException(
                   $"Response from '{url}' exceeded {MaxResponseBytes:N0} byte size limit.");
    }

    private static void ApplyHeaders(HttpRequestMessage request, Dictionary<string, string>? headers)
    {
        if (headers is null)
            return;

        foreach (var (name, value) in headers)
        {
            if (string.Equals(name, "Accept-Encoding", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(name, "Accept", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(value));
                continue;
            }

            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content?.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static async Task<string?> ReadResponseWithSizeLimit(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > MaxResponseBytes)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 8192,
            leaveOpen: true);

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

    private static IReadOnlyList<JsonElement> ExtractItems(
        string responseBody,
        ExtractConfig config,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(responseBody, new JsonDocumentOptions
        {
            MaxDepth = 20,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });

        using var evaluationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        evaluationCts.CancelAfter(EvaluationTimeout);

        var extracted = EvaluateSimpleJsonPath(document.RootElement, config.JsonPath!, evaluationCts.Token);
        var normalized = NormalizeExtractionValue(extracted, config.Type);

        return normalized.ValueKind == JsonValueKind.Array
            ? normalized.EnumerateArray().Select(item => item.Clone()).ToArray()
            : normalized.ValueKind == JsonValueKind.Null
                ? []
                : [normalized.Clone()];
    }

    private static string? ValidateJsonPath(string jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath))
            return "path cannot be empty.";

        if (jsonPath.Length > MaxJsonPathLength)
            return $"path exceeds the {MaxJsonPathLength} character limit.";

        if (jsonPath.Contains("..", StringComparison.Ordinal) ||
            jsonPath.Contains("?(", StringComparison.Ordinal) ||
            jsonPath.Contains('@', StringComparison.Ordinal))
        {
            return "path contains blocked JSONPath operators.";
        }

        return SafeJsonPathRegex.IsMatch(jsonPath)
            ? null
            : "path is outside the supported JSONPath subset.";
    }

    private static JsonElement EvaluateSimpleJsonPath(
        JsonElement root,
        string path,
        CancellationToken cancellationToken)
    {
        var tokens = ParsePath(path);
        var current = new List<JsonElement> { root };
        var producesMany = false;

        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var next = new List<JsonElement>();

            switch (token.Kind)
            {
                case PathTokenKind.Field:
                    foreach (var node in current)
                    {
                        if (node.ValueKind == JsonValueKind.Object &&
                            node.TryGetProperty(token.FieldName!, out var property))
                        {
                            next.Add(property);
                        }
                    }
                    break;

                case PathTokenKind.Index:
                    foreach (var node in current)
                    {
                        if (TryGetArrayElement(node, token.Start, out var item))
                            next.Add(item);
                    }
                    break;

                case PathTokenKind.Wildcard:
                    producesMany = true;
                    foreach (var node in current)
                    {
                        if (node.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (var item in node.EnumerateArray())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            next.Add(item);
                            if (next.Count > MaxArrayItems)
                                throw new InvalidOperationException(
                                    $"Iterate block exceeded the maximum of {MaxArrayItems} extracted array items.");
                        }
                    }
                    break;

                case PathTokenKind.Slice:
                    producesMany = true;
                    foreach (var node in current)
                    {
                        if (node.ValueKind != JsonValueKind.Array)
                            continue;

                        var index = 0;
                        foreach (var item in node.EnumerateArray())
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (index >= token.Start && index < token.End)
                            {
                                next.Add(item);
                                if (next.Count > MaxArrayItems)
                                    throw new InvalidOperationException(
                                        $"Iterate block exceeded the maximum of {MaxArrayItems} extracted array items.");
                            }

                            index++;
                            if (index >= token.End)
                                break;
                        }
                    }
                    break;
            }

            current = next;
        }

        if (current.Count == 0)
            return CreateNullElement();

        if (!producesMany && current.Count == 1)
            return current[0].Clone();

        var results = current.Select(item => item.Clone()).ToArray();
        return JsonSerializer.SerializeToElement(results);
    }

    private static IReadOnlyList<PathToken> ParsePath(string path)
    {
        var tokens = new List<PathToken>();
        var index = 1; // Skip '$'

        while (index < path.Length)
        {
            if (path[index] == '.')
            {
                index++;
                var start = index;
                while (index < path.Length && (char.IsLetterOrDigit(path[index]) || path[index] == '_'))
                    index++;

                tokens.Add(PathToken.Field(path[start..index]));
                continue;
            }

            if (path[index] == '[')
            {
                var closeIndex = path.IndexOf(']', index);
                var content = path[(index + 1)..closeIndex];

                if (content == "*")
                {
                    tokens.Add(PathToken.Wildcard());
                }
                else if (content.Contains(':', StringComparison.Ordinal))
                {
                    var parts = content.Split(':', 2);
                    tokens.Add(PathToken.Slice(int.Parse(parts[0]), int.Parse(parts[1])));
                }
                else
                {
                    tokens.Add(PathToken.Index(int.Parse(content)));
                }

                index = closeIndex + 1;
                continue;
            }

            throw new InvalidOperationException($"Unsupported JSONPath token near '{path[index..]}'");
        }

        return tokens;
    }

    private static bool TryGetArrayElement(JsonElement node, int index, out JsonElement element)
    {
        element = default;
        if (node.ValueKind != JsonValueKind.Array || index < 0)
            return false;

        var currentIndex = 0;
        foreach (var item in node.EnumerateArray())
        {
            if (currentIndex == index)
            {
                element = item;
                return true;
            }

            currentIndex++;
        }

        return false;
    }

    private static JsonElement NormalizeExtractionValue(JsonElement value, string type) =>
        type.ToLowerInvariant() switch
        {
            "array" => NormalizeArrayValue(value),
            _ => value.Clone()
        };

    private static JsonElement NormalizeArrayValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return JsonSerializer.SerializeToElement(Array.Empty<object>());

        if (value.ValueKind == JsonValueKind.Array)
            return value.Clone();

        return JsonSerializer.SerializeToElement(new[] { value.Clone() });
    }

    private static JsonElement CreateNullElement() =>
        JsonSerializer.SerializeToElement<string?>(null);

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

    private static List<JsonElement> Deduplicate(
        IReadOnlyList<JsonElement> items,
        string? deduplicateKey)
    {
        if (string.IsNullOrWhiteSpace(deduplicateKey))
            return items.Select(item => item.Clone()).ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduplicated = new List<JsonElement>(items.Count);

        foreach (var item in items)
        {
            if (!TryGetDeduplicationKey(item, deduplicateKey, out var key) ||
                seen.Add(key))
            {
                deduplicated.Add(item.Clone());
            }
        }

        return deduplicated;
    }

    private static bool TryGetDeduplicationKey(JsonElement item, string keyName, out string key)
    {
        key = string.Empty;
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty(keyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        key = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => property.GetRawText()
        };

        return !string.IsNullOrWhiteSpace(key);
    }

    private static BlockResult BuildSuccess(IReadOnlyList<JsonElement> items)
    {
        var output = new Dictionary<string, object?>
        {
            ["data"] = items
        };

        return BlockResult.Succeeded(JsonSerializer.SerializeToElement(output));
    }

    private static IterateConfig ReadConfig(BlockContext context)
    {
        var config = new IterateConfig();

        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return config;

        var blockDef = pipeline.Blocks.FirstOrDefault(
            block => string.Equals(block.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } root)
            return config;

        if (root.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.String)
                    config.Values.Add(value.GetString() ?? string.Empty);
            }
        }

        if (root.TryGetProperty("request", out var request) && request.ValueKind == JsonValueKind.Object)
        {
            if (request.TryGetProperty("urlTemplate", out var urlTemplate) &&
                urlTemplate.ValueKind == JsonValueKind.String)
            {
                config.Request.UrlTemplate = urlTemplate.GetString();
            }

            if (request.TryGetProperty("method", out var method) &&
                method.ValueKind == JsonValueKind.String)
            {
                var methodValue = method.GetString()?.Trim().ToUpperInvariant();
                config.Request.Method = methodValue == "POST" ? HttpMethod.Post : HttpMethod.Get;
            }

            if (request.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
            {
                config.Request.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in headers.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        config.Request.Headers[prop.Name] = prop.Value.GetString()!;
                }
            }

            if (request.TryGetProperty("bodyTemplate", out var bodyTemplate) &&
                bodyTemplate.ValueKind == JsonValueKind.String)
            {
                config.Request.BodyTemplate = bodyTemplate.GetString();
            }

            if (request.TryGetProperty("timeout", out var timeout) && timeout.TryGetInt32(out var timeoutMs))
                config.Request.TimeoutMs = Math.Clamp(timeoutMs, 1000, MaxTimeoutMs);
        }

        if (root.TryGetProperty("extract", out var extract) && extract.ValueKind == JsonValueKind.Object)
        {
            if (extract.TryGetProperty("jsonpath", out var jsonPath) &&
                jsonPath.ValueKind == JsonValueKind.String)
            {
                config.Extract.JsonPath = jsonPath.GetString();
            }

            if (extract.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String)
            {
                config.Extract.Type = type.GetString() ?? "array";
            }
        }

        if (root.TryGetProperty("deduplicateKey", out var deduplicateKey) &&
            deduplicateKey.ValueKind == JsonValueKind.String)
        {
            config.DeduplicateKey = deduplicateKey.GetString();
        }

        if (root.TryGetProperty("rateLimit", out var rateLimit) && rateLimit.ValueKind == JsonValueKind.Object)
        {
            if (rateLimit.TryGetProperty("delayMs", out var delay) && delay.TryGetInt32(out var delayMs))
                config.RateLimit.DelayMs = Math.Max(MinDelayMs, delayMs);

            if (rateLimit.TryGetProperty("maxConcurrent", out var maxConcurrent) &&
                maxConcurrent.TryGetInt32(out var concurrent))
            {
                config.RateLimit.MaxConcurrent = Math.Clamp(concurrent, 1, MaxMaxConcurrent);
            }
        }

        if (root.TryGetProperty("maxValues", out var maxValues) && maxValues.TryGetInt32(out var configuredMaxValues))
            config.MaxValues = Math.Clamp(configuredMaxValues, 1, HardMaxValues);

        return config;
    }

    private sealed class IterateConfig
    {
        public List<string> Values { get; } = [];
        public RequestConfig Request { get; } = new();
        public ExtractConfig Extract { get; } = new();
        public RateLimitConfig RateLimit { get; } = new();
        public string? DeduplicateKey { get; set; }
        public int MaxValues { get; set; } = DefaultMaxValues;
    }

    private sealed class RequestConfig
    {
        public string? UrlTemplate { get; set; }
        public HttpMethod Method { get; set; } = HttpMethod.Get;
        public Dictionary<string, string>? Headers { get; set; }
        public string? BodyTemplate { get; set; }
        public int TimeoutMs { get; set; } = DefaultTimeoutMs;
    }

    private sealed class ExtractConfig
    {
        public string? JsonPath { get; set; }
        public string Type { get; set; } = "array";
    }

    private sealed class RateLimitConfig
    {
        public int DelayMs { get; set; } = DefaultDelayMs;
        public int MaxConcurrent { get; set; } = DefaultMaxConcurrent;
    }

    private enum PathTokenKind
    {
        Field,
        Index,
        Wildcard,
        Slice
    }

    private sealed record PathToken(PathTokenKind Kind, string? FieldName = null, int Start = 0, int End = 0)
    {
        public static PathToken Field(string fieldName) => new(PathTokenKind.Field, fieldName);
        public static PathToken Index(int index) => new(PathTokenKind.Index, Start: index);
        public static PathToken Wildcard() => new(PathTokenKind.Wildcard);
        public static PathToken Slice(int start, int end) => new(PathTokenKind.Slice, Start: start, End: end);
    }
}
