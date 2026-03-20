using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.BlockExecution;
using Microsoft.Extensions.DependencyInjection;

namespace ChangeDetection.Services.Blocks.Acquisition;

/// <summary>
/// Makes an HTTP request (GET or POST) and returns the response body as text, parsed JSON, and/or HTML.
/// Designed for API-backed job boards (Workday, Platsbanken, etc.) where data is fetched via REST endpoints.
/// </summary>
public class HttpRequestBlock : IPipelineBlock
{
    private const int MaxResponseBytes = 5 * 1024 * 1024; // 5 MB
    private const int DefaultTimeoutMs = 30_000;
    private const int MaxTimeoutMs = 60_000;
    private static readonly int[] RetryDelaysMs = [0, 1000, 3000];
    private static readonly HashSet<int> RetriableStatusCodes = [400, 429, 500, 502, 503, 504];

    public string BlockType => "HttpRequest";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "url", Type = PortType.Url }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
    [
        new PortDescriptor { Name = "body", Type = PortType.PlainText },
        new PortDescriptor { Name = "json", Type = PortType.ExtractedObjects, Required = false },
        new PortDescriptor { Name = "html", Type = PortType.HtmlContent, Required = false },
        new PortDescriptor { Name = "status", Type = PortType.NumericValue },
        new PortDescriptor { Name = "response", Type = PortType.ExtractedObjects, Required = false }
    ];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Infrastructure;

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        // --- 1. Read URL from inputs ---
        if (!context.Inputs.TryGetValue("url", out var urlElement))
            return BlockResult.Failed("HttpRequest block requires a 'url' input.");

        var url = urlElement.ValueKind == JsonValueKind.String
            ? urlElement.GetString()
            : urlElement.TryGetProperty("url", out var nested) ? nested.GetString() : null;

        if (string.IsNullOrWhiteSpace(url))
            return BlockResult.Failed("HttpRequest block received an empty or invalid URL.");

        // --- 2. Read config ---
        var (method, headers, body, timeoutMs, acceptJsonOnly) = ReadConfig(context);

        // --- 3. Security: Domain pin validation ---
        if (context.DomainPin is { } pin)
        {
            var pinValidator = context.Services.GetRequiredService<DomainPinValidator>();
            var pinError = await pinValidator.ValidateWithDnsResolution(url, pin, context.CancellationToken);
            if (pinError is not null)
                return BlockResult.Failed($"Domain pin blocked: {pinError}");

            if (method == HttpMethod.Post)
            {
                context.Logger.LogWarning(
                    "HttpRequestBlock: POST request to {Url} under domain pin '{Pin}'. " +
                    "Approval is enforced at pipeline setup time (PipelineSecurityValidator).",
                    url, pin.PrimaryDomain);
            }
        }

        // --- 4. Security: SSRF validation ---
        var urlValidator = context.Services.GetRequiredService<IUrlValidator>();
        var ssrfError = urlValidator.Validate(url);
        if (ssrfError is not null)
            return BlockResult.Failed($"URL blocked by SSRF check: {ssrfError}");

        // --- 5. Try PinnedHttpClient first, fall back to IHttpClientFactory ---
        var pinnedClient = context.Services.GetService<PinnedHttpClient>();
        if (pinnedClient is not null && context.DomainPin is { } domainPin)
        {
            return await ExecuteViaPinnedClient(
                pinnedClient, domainPin, url, method, headers, body, acceptJsonOnly, context);
        }

        return await ExecuteViaHttpClientFactory(
            context, url, method, headers, body, timeoutMs, acceptJsonOnly);
    }

    private async Task<BlockResult> ExecuteViaPinnedClient(
        PinnedHttpClient pinnedClient,
        DomainPin pin,
        string url,
        HttpMethod method,
        Dictionary<string, string>? headers,
        string? body,
        bool acceptJsonOnly,
        BlockContext context)
    {
        var budget = context.Services.GetService<ExecutionBudget>() ?? new ExecutionBudget();

        context.Logger.LogDebug("HttpRequestBlock sending {Method} to {Url} via PinnedHttpClient", method, url);

        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < RetryDelaysMs.Length; attempt++)
        {
            try
            {
                response = await pinnedClient.SendAsync(
                    url, pin, budget, method, CreateRequestContent(body), headers, context.CancellationToken);

                if (!IsRetriableStatusCode(response.StatusCode) || attempt == RetryDelaysMs.Length - 1)
                    break;

                context.Logger.LogWarning(
                    "HttpRequestBlock: Retrying {Url} (attempt {Attempt}, status was {Status})",
                    url, attempt + 2, response.StatusCode);
                response.Dispose();
                response = null;
                await Task.Delay(RetryDelaysMs[attempt + 1], context.CancellationToken);
            }
            catch (HttpRequestException ex)
            {
                if (attempt == RetryDelaysMs.Length - 1)
                    return BlockResult.Failed($"HTTP request to '{url}' failed after {RetryDelaysMs.Length} attempts: {ex.Message}");

                context.Logger.LogWarning(
                    ex,
                    "HttpRequestBlock: Retrying {Url} (attempt {Attempt}) after request exception",
                    url, attempt + 2);
                await Task.Delay(RetryDelaysMs[attempt + 1], context.CancellationToken);
            }
            catch (ResourceExhaustedException ex)
            {
                return BlockResult.Failed($"Request resource limit exceeded: {ex.Message}");
            }
            catch (SecurityViolationException ex)
            {
                return BlockResult.Failed($"Security violation: {ex.Message}");
            }
        }

        if (response is null)
            return BlockResult.Failed($"HTTP request to '{url}' failed without a response.");

        using (response)
        {
            var responseBody = await response.Content.ReadAsStringAsync(context.CancellationToken);
            return BuildOutput(responseBody, (int)response.StatusCode, response.Content.Headers, acceptJsonOnly, url, body);
        }
    }

    private async Task<BlockResult> ExecuteViaHttpClientFactory(
        BlockContext context,
        string url,
        HttpMethod method,
        Dictionary<string, string>? headers,
        string? body,
        int timeoutMs,
        bool acceptJsonOnly)
    {
        var httpClientFactory = context.Services.GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient("HttpRequestBlock");
        client.Timeout = Timeout.InfiniteTimeSpan;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        context.Logger.LogDebug("HttpRequestBlock sending {Method} to {Url} via IHttpClientFactory", method, url);

        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < RetryDelaysMs.Length; attempt++)
        {
            using var request = CreateRequestMessage(method, url, headers, body);

            try
            {
                response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

                if (!IsRetriableStatusCode(response.StatusCode) || attempt == RetryDelaysMs.Length - 1)
                    break;

                context.Logger.LogWarning(
                    "HttpRequestBlock: Retrying {Url} (attempt {Attempt}, status was {Status})",
                    url, attempt + 2, response.StatusCode);
                response.Dispose();
                response = null;
                await Task.Delay(RetryDelaysMs[attempt + 1], context.CancellationToken);
            }
            catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
            {
                if (attempt == RetryDelaysMs.Length - 1)
                    return BlockResult.Failed($"HTTP request to '{url}' timed out after {RetryDelaysMs.Length} attempts.");

                context.Logger.LogWarning(
                    "HttpRequestBlock: Retrying {Url} (attempt {Attempt}) after timeout",
                    url, attempt + 2);
                await Task.Delay(RetryDelaysMs[attempt + 1], context.CancellationToken);
            }
            catch (HttpRequestException ex)
            {
                if (attempt == RetryDelaysMs.Length - 1)
                    return BlockResult.Failed($"HTTP request to '{url}' failed after {RetryDelaysMs.Length} attempts: {ex.Message}");

                context.Logger.LogWarning(
                    ex,
                    "HttpRequestBlock: Retrying {Url} (attempt {Attempt}) after request exception",
                    url, attempt + 2);
                await Task.Delay(RetryDelaysMs[attempt + 1], context.CancellationToken);
            }
        }

        if (response is null)
            return BlockResult.Failed($"HTTP request to '{url}' failed without a response.");

        using (response)
        {
            // Read response with size limit
            var responseBody = await ReadResponseWithSizeLimit(response, context.CancellationToken);
            if (responseBody is null)
                return BlockResult.Failed($"Response from '{url}' exceeded {MaxResponseBytes:N0} byte size limit.");

            return BuildOutput(responseBody, (int)response.StatusCode, response.Content.Headers, acceptJsonOnly, url, body);
        }
    }

    private static HttpRequestMessage CreateRequestMessage(
        HttpMethod method,
        string url,
        Dictionary<string, string>? headers,
        string? body)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Content = CreateRequestContent(body)
        };

        // Prevent zip bombs
        request.Headers.AcceptEncoding.Clear();
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                // Skip Accept-Encoding (we force identity above)
                if (string.Equals(name, "Accept-Encoding", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Handle Accept header explicitly (typed header, not content header)
                if (string.Equals(name, "Accept", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.Accept.Clear();
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(value));
                    continue;
                }

                // Content-Type on content (not request) headers
                if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    // Already set via StringContent constructor; skip to avoid duplication
                    continue;
                }

                // All other headers
                if (!request.Headers.TryAddWithoutValidation(name, value))
                    request.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return request;
    }

    private static HttpContent? CreateRequestContent(string? body)
        => body is null ? null : new StringContent(body, Encoding.UTF8, "application/json");

    private static bool IsRetriableStatusCode(System.Net.HttpStatusCode statusCode)
        => RetriableStatusCodes.Contains((int)statusCode);

    private static BlockResult BuildOutput(
        string responseBody,
        int statusCode,
        HttpContentHeaders contentHeaders,
        bool acceptJsonOnly,
        string? requestUrl = null,
        string? requestBody = null)
    {
        var contentType = contentHeaders.ContentType?.MediaType ?? string.Empty;

        // Try JSON parse
        JsonElement? jsonOutput = null;
        if (IsJsonContentType(contentType) || TryParseJson(responseBody, out jsonOutput))
        {
            // jsonOutput already set if TryParseJson succeeded
            if (jsonOutput is null)
                TryParseJson(responseBody, out jsonOutput);
        }

        if (acceptJsonOnly && jsonOutput is null)
            return BlockResult.Failed("Response is not valid JSON and acceptJsonOnly is enabled.");

        // Check for HTML
        string? htmlOutput = null;
        if (IsHtmlContentType(contentType) ||
            responseBody.TrimStart().StartsWith('<') &&
            responseBody.Contains("</", StringComparison.OrdinalIgnoreCase))
        {
            htmlOutput = responseBody;
        }

        var output = new Dictionary<string, object?>
        {
            ["body"] = responseBody,
            ["json"] = jsonOutput,
            ["html"] = htmlOutput,
            ["status"] = statusCode,
            ["url"] = requestUrl,
            ["requestBody"] = requestBody,
            // "response" is the full composite — used by PaginateBlock offset mode
            // which needs the JSON payload + URL + requestBody in one object
            ["response"] = new Dictionary<string, object?>
            {
                ["json"] = jsonOutput ?? (object?)responseBody,
                ["body"] = responseBody,
                ["url"] = requestUrl,
                ["requestBody"] = requestBody
            }
        };

        return BlockResult.Succeeded(JsonSerializer.SerializeToElement(output));
    }

    private static async Task<string?> ReadResponseWithSizeLimit(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        // Check Content-Length header first
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

            // Approximate byte count (UTF-8 chars ≈ bytes for ASCII/Latin, conservative for CJK)
            totalBytes += Encoding.UTF8.GetByteCount(buffer, 0, charsRead);
            if (totalBytes > MaxResponseBytes)
                return null;

            sb.Append(buffer, 0, charsRead);
        }

        return sb.ToString();
    }

    private (HttpMethod Method, Dictionary<string, string>? Headers, string? Body, int TimeoutMs, bool AcceptJsonOnly)
        ReadConfig(BlockContext context)
    {
        var method = HttpMethod.Get;
        Dictionary<string, string>? headers = null;
        string? body = null;
        var timeoutMs = DefaultTimeoutMs;
        var acceptJsonOnly = false;

        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return (method, headers, body, timeoutMs, acceptJsonOnly);

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return (method, headers, body, timeoutMs, acceptJsonOnly);

        // Method
        if (config.TryGetProperty("method", out var methodProp) &&
            methodProp.ValueKind == JsonValueKind.String)
        {
            var methodStr = methodProp.GetString()?.Trim().ToUpperInvariant();
            method = methodStr switch
            {
                "POST" => HttpMethod.Post,
                "GET" => HttpMethod.Get,
                _ => HttpMethod.Get
            };
        }

        // Headers
        if (config.TryGetProperty("headers", out var headersProp) &&
            headersProp.ValueKind == JsonValueKind.Object)
        {
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in headersProp.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    headers[prop.Name] = prop.Value.GetString()!;
            }
        }

        // Body (literal string from config only — never from upstream inputs)
        if (config.TryGetProperty("body", out var bodyProp) &&
            bodyProp.ValueKind == JsonValueKind.String)
        {
            body = bodyProp.GetString();
        }

        // Timeout (capped at MaxTimeoutMs)
        if (config.TryGetProperty("timeout", out var timeoutProp) && timeoutProp.TryGetInt32(out var configTimeout))
        {
            timeoutMs = Math.Clamp(configTimeout, 1000, MaxTimeoutMs);
        }

        // AcceptJsonOnly
        if (config.TryGetProperty("acceptJsonOnly", out var jsonOnlyProp) &&
            jsonOnlyProp.ValueKind == JsonValueKind.True)
        {
            acceptJsonOnly = true;
        }

        return (method, headers, body, timeoutMs, acceptJsonOnly);
    }

    private static bool IsJsonContentType(string contentType) =>
        contentType.Contains("json", StringComparison.OrdinalIgnoreCase);

    private static bool IsHtmlContentType(string contentType) =>
        contentType.Contains("html", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseJson(string text, out JsonElement? element)
    {
        element = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(text);
            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
