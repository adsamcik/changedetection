using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
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

        var firstPageHtml = htmlElement.ValueKind == JsonValueKind.String
            ? htmlElement.GetString()
            : htmlElement.TryGetProperty("html", out var nested) ? nested.GetString() : null;

        if (string.IsNullOrWhiteSpace(firstPageHtml))
            return BlockResult.Failed("Paginate block received empty or invalid HTML.");

        var (nextSelector, maxPages, delayMs) = ReadConfig(context);

        var pages = new List<string> { firstPageHtml };

        // If no Page (Playwright) available or no selector, return single page
        if (context.Page is null || string.IsNullOrWhiteSpace(nextSelector))
        {
            var singleOutput = JsonSerializer.SerializeToElement(new
            {
                pages,
                pageCount = pages.Count
            });
            return BlockResult.Succeeded(singleOutput);
        }

        var effectiveMaxPages = maxPages > 0 ? maxPages : 5;
        var ct = context.CancellationToken;
        var fetcher = context.Services.GetRequiredService<IContentFetcher>();

        try
        {
            dynamic page = context.Page;

            for (var i = 1; i < effectiveMaxPages; i++)
            {
                ct.ThrowIfCancellationRequested();

                // Find next page link
                var nextElement = await page.QuerySelectorAsync(nextSelector);
                if (nextElement is null)
                    break;

                string? href = await nextElement.GetAttributeAsync("href");
                if (string.IsNullOrWhiteSpace(href))
                    break;

                var urlValidator = context.Services.GetRequiredService<IUrlValidator>();
                var validationError = urlValidator.Validate(href);
                if (validationError is not null)
                {
                    context.Logger.LogWarning("Pagination URL blocked: {Error}", validationError);
                    break;
                }

                if (delayMs > 0)
                    await Task.Delay(delayMs, ct);

                var result = await fetcher.FetchAsync(href, new FetchOptions(), ct);
                if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Html))
                    break;

                pages.Add(result.Html);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.LogWarning(ex, "Pagination stopped due to error after {PageCount} pages", pages.Count);
        }

        var output = JsonSerializer.SerializeToElement(new
        {
            pages,
            pageCount = pages.Count
        });
        return BlockResult.Succeeded(output);
    }

    private static (string? nextSelector, int maxPages, int delayMs) ReadConfig(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return (null, 5, 0);

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return (null, 5, 0);

        string? nextSelector = null;
        var maxPages = 5;
        var delayMs = 0;

        if (config.TryGetProperty("nextSelector", out var selElem) && selElem.ValueKind == JsonValueKind.String)
            nextSelector = selElem.GetString();

        if (config.TryGetProperty("maxPages", out var maxElem) && maxElem.TryGetInt32(out var mp))
            maxPages = mp;
        maxPages = Math.Clamp(maxPages, 1, 50);

        if (config.TryGetProperty("delay", out var delayElem) && delayElem.TryGetInt32(out var d))
            delayMs = d;

        return (nextSelector, maxPages, delayMs);
    }
}
