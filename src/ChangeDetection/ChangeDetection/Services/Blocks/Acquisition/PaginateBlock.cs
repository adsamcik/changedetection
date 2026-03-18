using System.Globalization;
using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
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
    public string BlockType => "Paginate";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Infrastructure;

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("html", out var htmlElement))
            return BlockResult.Failed("Paginate block requires an 'html' input.");

        var (firstPageHtml, initialPageUrl) = ReadInput(htmlElement);
        if (string.IsNullOrWhiteSpace(firstPageHtml))
            return BlockResult.Failed("Paginate block received empty or invalid HTML.");

        var config = ReadConfig(context);
        if (config.Mode == PaginationMode.Unknown)
            return BlockResult.Failed($"Paginate block received unsupported mode '{config.RawMode}'.");

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
                        initialPageUrl,
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
            pageCount = pages.Count
        });

    private static (string? html, string? url) ReadInput(JsonElement htmlElement)
    {
        if (htmlElement.ValueKind == JsonValueKind.String)
            return (htmlElement.GetString(), null);

        string? html = null;
        string? url = null;

        if (htmlElement.ValueKind == JsonValueKind.Object)
        {
            if (htmlElement.TryGetProperty("html", out var nestedHtml) && nestedHtml.ValueKind == JsonValueKind.String)
                html = nestedHtml.GetString();

            if (htmlElement.TryGetProperty("url", out var nestedUrl) && nestedUrl.ValueKind == JsonValueKind.String)
                url = nestedUrl.GetString();
        }

        return (html, url);
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

        return result;
    }

    private sealed class PaginateConfig
    {
        public static PaginateConfig Default => new();

        public PaginationMode Mode { get; set; } = PaginationMode.Sequential;
        public string? RawMode { get; set; } = "sequential";
        public string? NextSelector { get; set; }
        public string? UrlPattern { get; set; }
        public int StartPage { get; set; } = 1;
        public int MaxPages { get; set; } = 5;
        public int MaxConcurrency { get; set; } = 3;
        public int DelayMs { get; set; }
    }

    private enum PaginationMode
    {
        Unknown,
        Sequential,
        Parallel
    }
}
