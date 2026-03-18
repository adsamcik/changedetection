using System.Diagnostics;
using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace ChangeDetection.Services.Blocks.Advanced;

/// <summary>
/// Enriches input data by fetching additional content from a URL field and merging extracted data.
/// </summary>
public class EnrichBlock : IPipelineBlock
{
    public string BlockType => "Enrich";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Extraction;

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("data", out var dataElement))
            return BlockResult.Failed("Enrich block requires a 'data' input.");

        var (urlField, extractFields, maxItems, maxConcurrency, delayBetweenRequests) = ReadConfig(context);
        if (string.IsNullOrWhiteSpace(urlField))
            return BlockResult.Failed("Enrich block requires 'urlField' in config.");

        if (dataElement.ValueKind is not JsonValueKind.Array and not JsonValueKind.Object)
            return BlockResult.Failed("Enrich block expects a JSON object or array input.");

        var stopwatch = Stopwatch.StartNew();
        var sourceItems = dataElement.ValueKind == JsonValueKind.Array
            ? dataElement.EnumerateArray().Take(maxItems).Select(x => x.Clone()).ToList()
            : [dataElement.Clone()];

        var urlValidator = context.Services.GetRequiredService<IUrlValidator>();
        IContentFetcher fetcher;
        try
        {
            fetcher = context.Services.GetRequiredService<IContentFetcher>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BlockResult.Failed($"IContentFetcher not available: {ex.Message}");
        }

        var results = await EnrichItemsAsync(
            sourceItems,
            urlField,
            extractFields,
            urlValidator,
            fetcher,
            maxConcurrency,
            delayBetweenRequests,
            context.CancellationToken);

        stopwatch.Stop();
        LogEnrichmentThroughput(context, results.Length, stopwatch.Elapsed, dataElement.ValueKind == JsonValueKind.Array ? maxConcurrency : 1);

        var output = dataElement.ValueKind == JsonValueKind.Array
            ? JsonSerializer.SerializeToElement(results)
            : results[0];

        return BlockResult.Succeeded(output);
    }

    /// <summary>
    /// Basic text extraction from HTML for a named field.
    /// Looks for elements with id, class, or data-field attributes matching the field name.
    /// Falls back to null if not found.
    /// </summary>
    private static string? ExtractFieldFromHtml(string html, string fieldName)
    {
        // Simple pattern: look for id="fieldName" or class="fieldName" in HTML
        var patterns = new[]
        {
            $"id=\"{fieldName}\"",
            $"class=\"{fieldName}\"",
            $"data-field=\"{fieldName}\""
        };

        foreach (var pattern in patterns)
        {
            var idx = html.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            // Find the closing > of this tag
            var tagClose = html.IndexOf('>', idx);
            if (tagClose < 0) continue;

            // Find the next < (start of closing tag or next element)
            var contentStart = tagClose + 1;
            var contentEnd = html.IndexOf('<', contentStart);
            if (contentEnd < 0) contentEnd = html.Length;

            var content = html[contentStart..contentEnd].Trim();
            if (content.Length > 0)
                return content;
        }

        return null;
    }

    /// <summary>
    /// Enriches a single array item by fetching its URL and extracting fields.
    /// Returns the enriched item with _enrichError on failure instead of throwing.
    /// </summary>
    private static async Task<JsonElement> EnrichItemAsync(
        JsonElement item,
        string urlField,
        List<string>? extractFields,
        IUrlValidator urlValidator,
        IContentFetcher fetcher,
        CancellationToken ct)
    {
        var dict = new Dictionary<string, JsonElement>();
        if (item.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in item.EnumerateObject())
                dict[prop.Name] = prop.Value.Clone();
        }

        string? url = null;
        if (item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty(urlField, out var urlElem) &&
            urlElem.ValueKind == JsonValueKind.String)
        {
            url = urlElem.GetString();
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            dict["_enrichError"] = JsonSerializer.SerializeToElement($"No URL found in field '{urlField}'.");
            return JsonSerializer.SerializeToElement(dict);
        }

        var validationError = urlValidator.Validate(url);
        if (validationError is not null)
        {
            dict["_enrichError"] = JsonSerializer.SerializeToElement($"URL blocked: {validationError}");
            return JsonSerializer.SerializeToElement(dict);
        }

        FetchResult fetchResult;
        try
        {
            fetchResult = await fetcher.FetchAsync(url, new FetchOptions(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            dict["_enrichError"] = JsonSerializer.SerializeToElement($"Fetch failed: {ex.Message}");
            return JsonSerializer.SerializeToElement(dict);
        }

        if (!fetchResult.IsSuccess || string.IsNullOrWhiteSpace(fetchResult.Html))
        {
            dict["_enrichError"] = JsonSerializer.SerializeToElement(
                $"Fetch failed for '{url}': HTTP {fetchResult.HttpStatusCode}");
            return JsonSerializer.SerializeToElement(dict);
        }

        if (extractFields is { Count: > 0 })
        {
            foreach (var field in extractFields)
            {
                var extracted = ExtractFieldFromHtml(fetchResult.Html, field);
                if (extracted is not null)
                    dict[field] = JsonSerializer.SerializeToElement(extracted);
            }
        }
        else
        {
            dict["_enrichedHtml"] = JsonSerializer.SerializeToElement(fetchResult.Html);
        }

        return JsonSerializer.SerializeToElement(dict);
    }

    private static async Task<JsonElement[]> EnrichItemsAsync(
        IReadOnlyList<JsonElement> sourceItems,
        string urlField,
        List<string>? extractFields,
        IUrlValidator urlValidator,
        IContentFetcher fetcher,
        int maxConcurrency,
        TimeSpan delayBetweenRequests,
        CancellationToken cancellationToken)
    {
        var results = new JsonElement[sourceItems.Count];
        using var concurrencyGate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        using var requestStartGate = new SemaphoreSlim(1, 1);
        var lastRequestStartedAt = DateTimeOffset.MinValue;

        var tasks = sourceItems.Select(async (item, index) =>
        {
            await concurrencyGate.WaitAsync(cancellationToken);
            try
            {
                await WaitForNextRequestSlotAsync(
                    requestStartGate,
                    delayBetweenRequests,
                    () => lastRequestStartedAt,
                    startedAt => lastRequestStartedAt = startedAt,
                    cancellationToken);

                results[index] = await EnrichItemAsync(
                    item,
                    urlField,
                    extractFields,
                    urlValidator,
                    fetcher,
                    cancellationToken);
            }
            finally
            {
                concurrencyGate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
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

    private static void LogEnrichmentThroughput(
        BlockContext context,
        int itemsProcessed,
        TimeSpan elapsed,
        int maxConcurrency)
    {
        var itemsPerSecond = elapsed.TotalSeconds <= 0
            ? itemsProcessed
            : itemsProcessed / elapsed.TotalSeconds;

        context.Logger.LogInformation(
            "EnrichBlock: enriched {ItemsProcessed} item(s) in {ElapsedMilliseconds} ms ({ItemsPerSecond:F2} items/sec) with concurrency {MaxConcurrency}",
            itemsProcessed,
            elapsed.TotalMilliseconds,
            itemsPerSecond,
            maxConcurrency);
    }

    private static (string? urlField, List<string>? extractFields, int maxItems, int maxConcurrency, TimeSpan delayBetweenRequests) ReadConfig(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return (null, null, 10, 5, TimeSpan.FromMilliseconds(200));

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return (null, null, 10, 5, TimeSpan.FromMilliseconds(200));

        string? urlField = null;
        List<string>? extractFields = null;

        if (config.TryGetProperty("urlField", out var urlElem) && urlElem.ValueKind == JsonValueKind.String)
            urlField = urlElem.GetString();

        if (config.TryGetProperty("extractFields", out var fieldsElem) && fieldsElem.ValueKind == JsonValueKind.Array)
        {
            extractFields = [];
            foreach (var item in fieldsElem.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    extractFields.Add(item.GetString()!);
            }
        }

        var maxItems = 10;
        if (config.TryGetProperty("maxItems", out var maxElem) && maxElem.TryGetInt32(out var mi))
            maxItems = Math.Clamp(mi, 1, 1000);

        var maxConcurrency = 5;
        if (config.TryGetProperty("maxConcurrency", out var concurrencyElem) && concurrencyElem.TryGetInt32(out var configuredConcurrency))
            maxConcurrency = Math.Clamp(configuredConcurrency, 1, 50);

        var delayBetweenRequests = TimeSpan.FromMilliseconds(200);
        if (config.TryGetProperty("delayBetweenRequests", out var delayElem) && delayElem.TryGetInt32(out var configuredDelay))
            delayBetweenRequests = TimeSpan.FromMilliseconds(Math.Max(0, configuredDelay));

        return (urlField, extractFields, maxItems, maxConcurrency, delayBetweenRequests);
    }
}
