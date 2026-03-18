using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace ChangeDetection.Services.Blocks.Acquisition;

/// <summary>
/// Fetches a page by URL using IContentFetcher and returns the HTML content.
/// </summary>
public class NavigateBlock : IPipelineBlock
{
    public string BlockType => "Navigate";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "url", Type = PortType.Url }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
    [
        new PortDescriptor { Name = "page", Type = PortType.PageReference },
        new PortDescriptor { Name = "html", Type = PortType.HtmlContent }
    ];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Infrastructure;

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("url", out var urlElement))
            return BlockResult.Failed("Navigate block requires a 'url' input.");

        var url = urlElement.ValueKind == JsonValueKind.String
            ? urlElement.GetString()
            : urlElement.TryGetProperty("url", out var nested) ? nested.GetString() : null;

        if (string.IsNullOrWhiteSpace(url))
            return BlockResult.Failed("Navigate block received an empty or invalid URL.");

        if (TryGetCachedHtml(context, out var cachedHtml))
        {
            var cachedOutput = JsonSerializer.SerializeToElement(new
            {
                html = cachedHtml,
                url
            });

            return BlockResult.Succeeded(cachedOutput);
        }

        var urlValidator = context.Services.GetRequiredService<IUrlValidator>();
        var validationError = urlValidator.Validate(url);
        if (validationError is not null)
            return BlockResult.Failed($"URL blocked: {validationError}");

        var fetcher = context.Services.GetRequiredService<IContentFetcher>();

        var fetchOptions = BuildFetchOptions(context);

        context.Logger.LogDebug("NavigateBlock fetching URL: {Url}", url);
        var result = await fetcher.FetchAsync(url, fetchOptions, context.CancellationToken);

        if (!result.IsSuccess)
            return BlockResult.Failed($"Failed to fetch '{url}': {result.ErrorMessage}");

        var output = JsonSerializer.SerializeToElement(new
        {
            html = result.Html ?? string.Empty,
            url
        });

        return BlockResult.Succeeded(output);
    }

    private static bool TryGetCachedHtml(BlockContext context, out string cachedHtml)
    {
        cachedHtml = string.Empty;

        if (!context.IsDryRun || context.PipelineDefinition is not PipelineDefinition pipeline)
            return false;

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config ||
            !config.TryGetProperty("_cachedHtml", out var cached) ||
            cached.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        cachedHtml = cached.GetString() ?? string.Empty;
        return true;
    }

    private static FetchOptions BuildFetchOptions(BlockContext context)
    {
        var options = new FetchOptions();

        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return options;

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return options;

        var useJavaScript = config.TryGetProperty("useJavaScript", out var js) && js.ValueKind == JsonValueKind.True;
        var useLightweight = config.TryGetProperty("useLightweight", out var lightweight) && lightweight.ValueKind == JsonValueKind.True;

        options.Mode = useJavaScript
            ? FetchMode.Browser
            : useLightweight
                ? FetchMode.LightweightHttp
                : FetchMode.Auto;

        options.UseJavaScript = useJavaScript;

        if (config.TryGetProperty("timeout", out var timeout) && timeout.TryGetInt32(out var timeoutMs))
            options.TimeoutSeconds = Math.Max(1, timeoutMs / 1000);

        if (config.TryGetProperty("waitForSelector", out var selector) &&
            selector.ValueKind == JsonValueKind.String)
            options.WaitForSelector = selector.GetString();

        return options;
    }
}
