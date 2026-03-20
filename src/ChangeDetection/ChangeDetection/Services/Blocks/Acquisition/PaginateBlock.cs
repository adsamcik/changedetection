using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.BlockExecution;
using Fizzler.Systems.HtmlAgilityPack;
using HtmlAgilityPack;
using Microsoft.Extensions.DependencyInjection;

namespace ChangeDetection.Services.Blocks.Acquisition;

/// <summary>
/// Navigates through paginated content by following next-page links.
/// Collects HTML from all pages into an array.
/// </summary>
public class PaginateBlock : IPipelineBlock
{
    private const int MaxResponseBytes = 5 * 1024 * 1024;
    private static readonly Regex SafeTotalPathRegex = new(
        @"^\$(\.[a-zA-Z_][a-zA-Z0-9_]*)+$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(2));

    public string BlockType => "Paginate";

    public IReadOnlyList<PortDescriptor> InputPorts =>
    [
        new PortDescriptor { Name = "html", Type = PortType.HtmlContent, Required = false },
        new PortDescriptor { Name = "json", Type = PortType.PlainText, Required = false }
    ];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Infrastructure;

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        var input = ReadInput(context);
        if (input is null)
            return BlockResult.Failed("Paginate block requires either an 'html' or 'json' input.");

        var config = ReadConfig(context);
        if (config.Mode == PaginationMode.Unknown)
            return BlockResult.Failed($"Paginate block received unsupported mode '{config.RawMode}'.");

        if (config.Mode == PaginationMode.Offset)
        {
            var configError = ValidateOffsetConfig(config);
            if (configError is not null)
                return BlockResult.Failed(configError);

            if (!LooksLikeJson(input.Payload))
                return BlockResult.Failed("Offset mode requires the first response to contain JSON.");

            var offsetPages = new List<string> { input.Payload };
            try
            {
                await FetchOffsetPagesAsync(
                    offsetPages,
                    input,
                    config,
                    context,
                    context.CancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return BlockResult.Failed(ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.Logger.LogWarning(ex, "Offset pagination stopped due to error after {PageCount} pages", offsetPages.Count);
            }

            return BlockResult.Succeeded(CreateOutput(offsetPages));
        }

        var firstPageHtml = input.Payload;
        if (string.IsNullOrWhiteSpace(firstPageHtml))
            return BlockResult.Failed("Paginate block received empty or invalid HTML.");

        if (config.Mode == PaginationMode.Parallel && string.IsNullOrWhiteSpace(config.UrlPattern))
            return BlockResult.Failed("Parallel mode requires 'urlPattern'.");

        var pages = config.Mode == PaginationMode.Parallel
            ? []
            : new List<string> { firstPageHtml };

        if (config.Mode == PaginationMode.Sequential && string.IsNullOrWhiteSpace(config.NextSelector))
            return BlockResult.Succeeded(CreateOutput(pages));

        var ct = context.CancellationToken;

        try
        {
            switch (config.Mode)
            {
                case PaginationMode.Sequential:
                {
                    var fetcher = context.Services.GetService(typeof(IContentFetcher)) as IContentFetcher;
                    var urlValidator = context.Services.GetService(typeof(IUrlValidator)) as IUrlValidator;
                    if (fetcher is null || urlValidator is null)
                        return BlockResult.Succeeded(CreateOutput(pages));

                    await FetchSequentialPagesAsync(
                        pages,
                        input.Url,
                        config.NextSelector!,
                        config.MaxPages,
                        TimeSpan.FromMilliseconds(config.DelayMs),
                        fetcher,
                        urlValidator,
                        context,
                        ct);
                    break;
                }
                case PaginationMode.Parallel:
                {
                    var fetcher = context.Services.GetRequiredService<IContentFetcher>();
                    var urlValidator = context.Services.GetRequiredService<IUrlValidator>();
                    await FetchParallelPagesAsync(
                        pages,
                        config.UrlPattern!,
                        config.StartPage,
                        config.MaxPages,
                        config.MaxConcurrency,
                        TimeSpan.FromMilliseconds(config.DelayMs),
                        fetcher,
                        urlValidator,
                        ct);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.LogWarning(ex, "Pagination stopped due to error after {PageCount} pages", pages.Count);
        }

        return BlockResult.Succeeded(CreateOutput(pages));
    }

    private static string? ValidateOffsetConfig(PaginateConfig config)
    {
        if (config.ParameterLocation == OffsetParameterLocation.Unknown)
            return $"Offset mode received unsupported parameterLocation '{config.RawParameterLocation}'.";

        if (string.IsNullOrWhiteSpace(config.OffsetField))
            return "Offset mode requires 'offsetField'.";

        if (config.LimitValue <= 0)
            return "Offset mode requires 'limitValue' greater than 0.";

        if (string.IsNullOrWhiteSpace(config.TotalFrom))
            return "Offset mode requires 'totalFrom'.";

        return null;
    }

    private static async Task FetchOffsetPagesAsync(
        List<string> pages,
        PaginateInput input,
        PaginateConfig config,
        BlockContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.OffsetField))
            throw new InvalidOperationException("Offset mode requires 'offsetField'.");

        if (config.LimitValue <= 0)
            throw new InvalidOperationException("Offset mode requires 'limitValue' greater than 0.");

        if (string.IsNullOrWhiteSpace(config.TotalFrom))
            throw new InvalidOperationException("Offset mode requires 'totalFrom'.");

        if (!TryReadIntegerFromJsonPath(pages[0], config.TotalFrom!, out var total))
            throw new InvalidOperationException($"Offset mode could not read total from '{config.TotalFrom}'.");

        var remaining = Math.Max(0, total - config.StartOffset);
        var totalPages = Math.Clamp(
            remaining == 0 ? 1 : (int)Math.Ceiling(remaining / (double)config.LimitValue),
            1,
            config.MaxPages);

        if (totalPages <= 1)
            return;

        if (config.ParameterLocation == OffsetParameterLocation.Query &&
            string.IsNullOrWhiteSpace(input.Url))
        {
            throw new InvalidOperationException("Offset query mode requires the input to include a source URL.");
        }

        if (config.ParameterLocation == OffsetParameterLocation.Body &&
            string.IsNullOrWhiteSpace(input.Url))
        {
            throw new InvalidOperationException("Offset body mode requires the input to include a source URL.");
        }

        if (config.ParameterLocation == OffsetParameterLocation.Body &&
            string.IsNullOrWhiteSpace(input.RequestBody))
        {
            throw new InvalidOperationException("Offset body mode requires the input to include 'requestBody'.");
        }

        var previousHash = ComputeHash(pages[0]);

        for (var pageIndex = 1; pageIndex < totalPages; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var offset = config.StartOffset + (pageIndex * config.LimitValue);
            if (offset >= total)
                break;

            var request = config.ParameterLocation switch
            {
                OffsetParameterLocation.Query => new OffsetRequest(
                    HttpMethod.Get,
                    UpdateQueryString(
                        input.Url!,
                        config.OffsetField!,
                        offset,
                        config.LimitField,
                        config.LimitValue),
                    null),
                OffsetParameterLocation.Body => new OffsetRequest(
                    HttpMethod.Post,
                    input.Url!,
                    UpdateJsonBody(
                        input.RequestBody!,
                        config.OffsetField!,
                        offset,
                        config.LimitField,
                        config.LimitValue)),
                _ => throw new InvalidOperationException(
                    $"Offset mode received unsupported parameterLocation '{config.RawParameterLocation}'.")
            };

            await ValidateRequestUrlAsync(request.Url, context, cancellationToken);

            if (TimeSpan.FromMilliseconds(config.DelayMs) > TimeSpan.Zero)
                await Task.Delay(TimeSpan.FromMilliseconds(config.DelayMs), cancellationToken);

            var responseBody = await SendRequestAsync(request, context, cancellationToken);
            if (string.IsNullOrWhiteSpace(responseBody))
                break;

            var currentHash = ComputeHash(responseBody);
            if (string.Equals(previousHash, currentHash, StringComparison.Ordinal))
            {
                context.Logger.LogWarning(
                    "Offset pagination stopped because page {PageIndex} duplicated the previous response",
                    pageIndex + 1);
                break;
            }

            pages.Add(responseBody);
            previousHash = currentHash;

            if (TryCountItems(responseBody, out var itemCount) && itemCount < config.LimitValue)
                break;
        }
    }

    private static async Task FetchSequentialPagesAsync(
        List<string> pages,
        string? initialPageUrl,
        string nextSelector,
        int maxPages,
        TimeSpan delayBetweenRequests,
        IContentFetcher fetcher,
        IUrlValidator urlValidator,
        BlockContext context,
        CancellationToken cancellationToken)
    {
        var currentPageUrl = initialPageUrl;
        var visitedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(currentPageUrl))
            visitedUrls.Add(currentPageUrl);

        for (var pageIndex = 1; pageIndex < maxPages; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var href = TryGetNextHref(pages[^1], nextSelector);
            if (string.IsNullOrWhiteSpace(href))
                break;

            var nextUrl = ResolveUrl(currentPageUrl, href);
            if (string.IsNullOrWhiteSpace(nextUrl))
            {
                context.Logger.LogWarning("Pagination stopped because href '{Href}' could not be resolved against '{CurrentPageUrl}'", href, currentPageUrl);
                break;
            }

            if (!visitedUrls.Add(nextUrl))
            {
                context.Logger.LogWarning("Pagination stopped because URL '{NextUrl}' was already visited", nextUrl);
                break;
            }

            var validationError = urlValidator.Validate(nextUrl);
            if (validationError is not null)
            {
                context.Logger.LogWarning("Pagination URL blocked: {Error}", validationError);
                break;
            }

            if (delayBetweenRequests > TimeSpan.Zero)
                await Task.Delay(delayBetweenRequests, cancellationToken);

            var result = await fetcher.FetchAsync(nextUrl, new FetchOptions(), cancellationToken);
            if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Html))
                break;

            pages.Add(result.Html);
            currentPageUrl = nextUrl;
        }
    }

    private static async Task FetchParallelPagesAsync(
        List<string> pages,
        string urlPattern,
        int startPage,
        int maxPages,
        int maxConcurrency,
        TimeSpan delayBetweenRequests,
        IContentFetcher fetcher,
        IUrlValidator urlValidator,
        CancellationToken cancellationToken)
    {
        var urls = Enumerable.Range(startPage, maxPages)
            .Select(pageNumber => urlPattern.Replace("{page}", pageNumber.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            .ToList();

        foreach (var url in urls)
        {
            var validationError = urlValidator.Validate(url);
            if (validationError is not null)
                throw new InvalidOperationException($"Pagination URL blocked: {validationError}");
        }

        var results = new string?[urls.Count];
        using var concurrencyGate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        using var requestStartGate = new SemaphoreSlim(1, 1);
        var lastRequestStartedAt = DateTimeOffset.MinValue;
        var firstFailureIndex = int.MaxValue;

        var tasks = urls.Select(async (url, index) =>
        {
            if (Volatile.Read(ref firstFailureIndex) < index)
                return;

            await concurrencyGate.WaitAsync(cancellationToken);
            try
            {
                if (Volatile.Read(ref firstFailureIndex) < index)
                    return;

                await WaitForNextRequestSlotAsync(
                    requestStartGate,
                    delayBetweenRequests,
                    () => lastRequestStartedAt,
                    startedAt => lastRequestStartedAt = startedAt,
                    cancellationToken);

                var result = await fetcher.FetchAsync(url, new FetchOptions(), cancellationToken);
                if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Html))
                {
                    UpdateFirstFailureIndex(ref firstFailureIndex, index);
                    return;
                }

                results[index] = result.Html;
            }
            finally
            {
                concurrencyGate.Release();
            }
        });

        await Task.WhenAll(tasks);

        var pageCount = firstFailureIndex == int.MaxValue ? results.Length : firstFailureIndex;
        pages.AddRange(results.Take(pageCount).OfType<string>());
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

    private static void UpdateFirstFailureIndex(ref int firstFailureIndex, int failureIndex)
    {
        while (true)
        {
            var snapshot = firstFailureIndex;
            if (failureIndex >= snapshot)
                return;

            if (Interlocked.CompareExchange(ref firstFailureIndex, failureIndex, snapshot) == snapshot)
                return;
        }
    }

    private static string? TryGetNextHref(string html, string nextSelector)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var href = document.DocumentNode
            .QuerySelectorAll(nextSelector)
            .FirstOrDefault()?
            .GetAttributeValue("href", string.Empty);

        return string.IsNullOrWhiteSpace(href) ? null : href;
    }

    private static string? ResolveUrl(string? currentPageUrl, string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var absoluteUri))
            return absoluteUri.ToString();

        if (string.IsNullOrWhiteSpace(currentPageUrl))
            return null;

        return Uri.TryCreate(new Uri(currentPageUrl), href, out var resolvedUri)
            ? resolvedUri.ToString()
            : null;
    }

    private static JsonElement CreateOutput(List<string> pages) =>
        JsonSerializer.SerializeToElement(new
        {
            pages,
            pageCount = pages.Count,
            combined = string.Join(Environment.NewLine, pages)
        });

    private static PaginateInput? ReadInput(BlockContext context)
    {
        if (context.Inputs.TryGetValue("json", out var jsonElement))
        {
            var parsed = ParseInput(jsonElement, preferJsonPayload: true);
            if (parsed is not null)
                return parsed;
        }

        if (!context.Inputs.TryGetValue("html", out var htmlElement))
            return null;

        return ParseInput(htmlElement, preferJsonPayload: false);
    }

    private static PaginateInput? ParseInput(JsonElement inputElement, bool preferJsonPayload)
    {
        if (inputElement.ValueKind == JsonValueKind.String)
        {
            var rawPayload = inputElement.GetString();
            return string.IsNullOrWhiteSpace(rawPayload)
                ? null
                : new PaginateInput(rawPayload, null, null);
        }

        string? payload = null;
        string? url = null;
        string? requestBody = null;

        if (inputElement.ValueKind == JsonValueKind.Object)
        {
            if (preferJsonPayload)
            {
                payload = TryGetNestedPayload(inputElement, "json")
                    ?? TryGetNestedPayload(inputElement, "body")
                    ?? TryGetNestedPayload(inputElement, "html");
            }
            else
            {
                payload = TryGetNestedPayload(inputElement, "html")
                    ?? TryGetNestedPayload(inputElement, "body")
                    ?? TryGetNestedPayload(inputElement, "json");
            }

            if (inputElement.TryGetProperty("url", out var nestedUrl) && nestedUrl.ValueKind == JsonValueKind.String)
                url = nestedUrl.GetString();

            requestBody = TryGetNestedPayload(inputElement, "requestBody");
            if (requestBody is null &&
                inputElement.TryGetProperty("request", out var requestElement) &&
                requestElement.ValueKind == JsonValueKind.Object)
            {
                requestBody = TryGetNestedPayload(requestElement, "body");

                if (string.IsNullOrWhiteSpace(url) &&
                    requestElement.TryGetProperty("url", out var requestUrl) &&
                    requestUrl.ValueKind == JsonValueKind.String)
                {
                    url = requestUrl.GetString();
                }
            }
        }

        if (string.IsNullOrWhiteSpace(payload))
            return null;

        return new PaginateInput(payload, url, requestBody);
    }

    private static string? TryGetNestedPayload(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Object or JsonValueKind.Array => property.GetRawText(),
            _ => null
        };
    }

    private static PaginateConfig ReadConfig(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return PaginateConfig.Default;

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return PaginateConfig.Default;

        var result = PaginateConfig.Default;

        if (config.TryGetProperty("mode", out var modeElem) && modeElem.ValueKind == JsonValueKind.String)
        {
            result.RawMode = modeElem.GetString();
            result.Mode = result.RawMode?.Equals("parallel", StringComparison.OrdinalIgnoreCase) == true
                ? PaginationMode.Parallel
                : result.RawMode?.Equals("sequential", StringComparison.OrdinalIgnoreCase) == true
                    ? PaginationMode.Sequential
                    : result.RawMode?.Equals("offset", StringComparison.OrdinalIgnoreCase) == true
                        ? PaginationMode.Offset
                        : PaginationMode.Unknown;
        }

        if (config.TryGetProperty("nextSelector", out var selectorElem) && selectorElem.ValueKind == JsonValueKind.String)
            result.NextSelector = selectorElem.GetString();

        if (config.TryGetProperty("urlPattern", out var patternElem) && patternElem.ValueKind == JsonValueKind.String)
            result.UrlPattern = patternElem.GetString();

        if (config.TryGetProperty("startPage", out var startElem) && startElem.TryGetInt32(out var startPage))
            result.StartPage = Math.Clamp(startPage, 1, 10000);

        if (config.TryGetProperty("maxPages", out var maxElem) && maxElem.TryGetInt32(out var maxPages))
            result.MaxPages = Math.Clamp(maxPages, 1, 50);

        if (config.TryGetProperty("maxConcurrency", out var concurrencyElem) && concurrencyElem.TryGetInt32(out var configuredConcurrency))
            result.MaxConcurrency = Math.Clamp(configuredConcurrency, 1, 50);

        if (config.TryGetProperty("delay", out var delayElem) && delayElem.TryGetInt32(out var delayMs))
            result.DelayMs = Math.Max(0, delayMs);

        if (config.TryGetProperty("parameterLocation", out var locationElem) && locationElem.ValueKind == JsonValueKind.String)
        {
            result.RawParameterLocation = locationElem.GetString();
            result.ParameterLocation = result.RawParameterLocation?.Equals("body", StringComparison.OrdinalIgnoreCase) == true
                ? OffsetParameterLocation.Body
                : result.RawParameterLocation?.Equals("query", StringComparison.OrdinalIgnoreCase) == true
                    ? OffsetParameterLocation.Query
                    : OffsetParameterLocation.Unknown;
        }

        if (config.TryGetProperty("offsetField", out var offsetFieldElem) && offsetFieldElem.ValueKind == JsonValueKind.String)
            result.OffsetField = offsetFieldElem.GetString();

        if (config.TryGetProperty("limitField", out var limitFieldElem) && limitFieldElem.ValueKind == JsonValueKind.String)
            result.LimitField = limitFieldElem.GetString();

        if (config.TryGetProperty("limitValue", out var limitValueElem) && limitValueElem.TryGetInt32(out var limitValue))
            result.LimitValue = Math.Clamp(limitValue, 1, 10_000);

        if (config.TryGetProperty("totalFrom", out var totalFromElem) && totalFromElem.ValueKind == JsonValueKind.String)
            result.TotalFrom = totalFromElem.GetString();

        if (config.TryGetProperty("startOffset", out var startOffsetElem) && startOffsetElem.TryGetInt32(out var startOffset))
            result.StartOffset = Math.Max(0, startOffset);

        return result;
    }

    private static bool LooksLikeJson(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var trimmed = payload.AsSpan().TrimStart();
        return !trimmed.IsEmpty && (trimmed[0] == '{' || trimmed[0] == '[');
    }

    private static async Task ValidateRequestUrlAsync(
        string url,
        BlockContext context,
        CancellationToken cancellationToken)
    {
        var urlValidator = context.Services.GetRequiredService<IUrlValidator>();
        var ssrfError = urlValidator.Validate(url);
        if (ssrfError is not null)
            throw new InvalidOperationException($"Pagination URL blocked by SSRF check: {ssrfError}");

        if (context.DomainPin is not { } pin)
            return;

        var pinValidator = context.Services.GetRequiredService<DomainPinValidator>();
        var pinError = await pinValidator.ValidateWithDnsResolution(url, pin, cancellationToken);
        if (pinError is not null)
            throw new InvalidOperationException($"Pagination URL blocked by domain pin: {pinError}");
    }

    private static async Task<string?> SendRequestAsync(
        OffsetRequest request,
        BlockContext context,
        CancellationToken cancellationToken)
    {
        var pinnedClient = context.Services.GetService<PinnedHttpClient>();
        if (pinnedClient is not null && context.DomainPin is { } pin)
            return await SendViaPinnedClientAsync(pinnedClient, pin, request, context, cancellationToken);

        var httpClientFactory = context.Services.GetRequiredService<IHttpClientFactory>();
        return await SendViaHttpClientFactoryAsync(httpClientFactory, request, context, cancellationToken);
    }

    private static async Task<string?> SendViaPinnedClientAsync(
        PinnedHttpClient pinnedClient,
        DomainPin pin,
        OffsetRequest request,
        BlockContext context,
        CancellationToken cancellationToken)
    {
        var budget = context.Services.GetService<ExecutionBudget>() ?? new ExecutionBudget();
        using var content = request.Body is null
            ? null
            : new StringContent(request.Body, Encoding.UTF8, "application/json");

        using var response = await pinnedClient.SendAsync(
            request.Url,
            pin,
            budget,
            request.Method,
            content,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accept"] = "application/json",
                ["Accept-Encoding"] = "identity"
            },
            cancellationToken);

        return await ReadResponseWithSizeLimit(response, cancellationToken);
    }

    private static async Task<string?> SendViaHttpClientFactoryAsync(
        IHttpClientFactory httpClientFactory,
        OffsetRequest request,
        BlockContext context,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("PaginateBlock");

        using var message = new HttpRequestMessage(request.Method, request.Url);
        message.Headers.Accept.Clear();
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.AcceptEncoding.Clear();
        message.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

        if (request.Body is not null)
            message.Content = new StringContent(request.Body, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
            return null;

        return await ReadResponseWithSizeLimit(response, cancellationToken);
    }

    private static async Task<string?> ReadResponseWithSizeLimit(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > MaxResponseBytes)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 8192,
            leaveOpen: true);
        var buffer = new char[8192];
        var builder = new StringBuilder();
        long totalBytes = 0;

        while (true)
        {
            var charsRead = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (charsRead == 0)
                break;

            totalBytes += Encoding.UTF8.GetByteCount(buffer, 0, charsRead);
            if (totalBytes > MaxResponseBytes)
                return null;

            builder.Append(buffer, 0, charsRead);
        }

        return builder.ToString();
    }

    private static bool TryReadIntegerFromJsonPath(string rawJson, string path, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(path) || !SafeTotalPathRegex.IsMatch(path))
            return false;

        using var document = JsonDocument.Parse(rawJson, new JsonDocumentOptions
        {
            MaxDepth = 20,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });

        JsonElement current = document.RootElement;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return false;
        }

        return TryReadInt(current, out value);
    }

    private static bool TryReadInt(JsonElement element, out int value)
    {
        value = 0;
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetInt32(out value);

        if (element.ValueKind == JsonValueKind.String)
            return int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        return false;
    }

    private static bool TryCountItems(string rawJson, out int count)
    {
        count = 0;

        using var document = JsonDocument.Parse(rawJson, new JsonDocumentOptions
        {
            MaxDepth = 20,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });

        return TryCountItems(document.RootElement, out count);
    }

    private static bool TryCountItems(JsonElement element, out int count)
    {
        count = 0;
        if (element.ValueKind == JsonValueKind.Array)
        {
            count = element.GetArrayLength();
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var preferredProperties = new[]
        {
            "items", "results", "data", "jobs", "jobAdvertisements", "documents"
        };

        foreach (var propertyName in preferredProperties)
        {
            if (element.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.Array)
            {
                count = property.GetArrayLength();
                return true;
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                count = property.Value.GetArrayLength();
                return true;
            }
        }

        return false;
    }

    private static string UpdateJsonBody(
        string requestBody,
        string offsetField,
        int offset,
        string? limitField,
        int limitValue)
    {
        var node = JsonNode.Parse(requestBody) as JsonObject
            ?? throw new InvalidOperationException("Offset body pagination requires a JSON object request body.");

        node[offsetField] = offset;
        if (!string.IsNullOrWhiteSpace(limitField))
            node[limitField] = limitValue;

        return node.ToJsonString();
    }

    private static string UpdateQueryString(
        string url,
        string offsetField,
        int offset,
        string? limitField,
        int limitValue)
    {
        var uri = new Uri(url, UriKind.Absolute);
        var query = ParseQuery(uri.Query);
        query[offsetField] = offset.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(limitField))
            query[limitField] = limitValue.ToString(CultureInfo.InvariantCulture);

        var builder = new UriBuilder(uri)
        {
            Query = BuildQuery(query)
        };

        return builder.Uri.ToString();
    }

    private static Dictionary<string, string> ParseQuery(string queryString)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(queryString))
            return result;

        var trimmed = queryString.TrimStart('?');
        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex < 0)
            {
                result[Uri.UnescapeDataString(pair)] = string.Empty;
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separatorIndex]);
            var value = Uri.UnescapeDataString(pair[(separatorIndex + 1)..]);
            result[key] = value;
        }

        return result;
    }

    private static string BuildQuery(Dictionary<string, string> query)
    {
        if (query.Count == 0)
            return string.Empty;

        return string.Join("&", query.Select(kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
    }

    private static string ComputeHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }

    private sealed class PaginateConfig
    {
        public static PaginateConfig Default => new();

        public PaginationMode Mode { get; set; } = PaginationMode.Sequential;
        public string? RawMode { get; set; } = "sequential";
        public string? NextSelector { get; set; }
        public string? UrlPattern { get; set; }
        public OffsetParameterLocation ParameterLocation { get; set; } = OffsetParameterLocation.Query;
        public string? RawParameterLocation { get; set; } = "query";
        public string? OffsetField { get; set; }
        public string? LimitField { get; set; }
        public int LimitValue { get; set; }
        public string? TotalFrom { get; set; } = "$.total";
        public int StartOffset { get; set; }
        public int StartPage { get; set; } = 1;
        public int MaxPages { get; set; } = 5;
        public int MaxConcurrency { get; set; } = 3;
        public int DelayMs { get; set; }
    }

    private sealed record PaginateInput(string Payload, string? Url, string? RequestBody);

    private sealed record OffsetRequest(HttpMethod Method, string Url, string? Body);

    private enum PaginationMode
    {
        Unknown,
        Sequential,
        Parallel,
        Offset
    }

    private enum OffsetParameterLocation
    {
        Unknown,
        Query,
        Body
    }
}
