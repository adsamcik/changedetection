using System.Text.Json;
using System.Text.RegularExpressions;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace ChangeDetection.Services.Blocks.Extraction;

/// <summary>
/// Filters HTML content using CSS selectors, XPath expressions, or regex patterns.
/// </summary>
public class FilterBlock : IPipelineBlock
{
    public string BlockType => "Filter";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Extraction;
    public bool IsCacheable => true;

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        if (!context.Inputs.TryGetValue("html", out var htmlElement))
            return BlockResult.Failed("Filter block requires an 'html' input.");

        var html = htmlElement.ValueKind == JsonValueKind.String
            ? htmlElement.GetString()
            : htmlElement.TryGetProperty("html", out var nested) ? nested.GetString() : null;

        if (string.IsNullOrWhiteSpace(html))
            return BlockResult.Failed("Filter block received empty or invalid HTML.");

        var (css, xpath, regex) = ReadConfig(context);

        if (css is null && xpath is null && regex is null)
        {
            context.Logger.LogInformation("FilterBlock: No selector configured, passing through content ({Length} chars)", html.Length);
            var passthrough = JsonSerializer.SerializeToElement(html);
            return BlockResult.Succeeded(passthrough);
        }

        var selectorType = css is not null ? "CSS" : xpath is not null ? "XPath" : "Regex";
        var selectorValue = css ?? xpath ?? regex;
        context.Logger.LogInformation("FilterBlock: Applying {SelectorType} selector '{Selector}'", selectorType, selectorValue);

        string? filtered = null;

        if (css is not null)
        {
            var extractor = context.Services.GetRequiredService<IContentExtractor>();
            filtered = extractor.ExtractHtml(html, cssSelector: css);
        }
        else if (xpath is not null)
        {
            var extractor = context.Services.GetRequiredService<IContentExtractor>();
            filtered = extractor.ExtractHtml(html, xpathSelector: xpath);
        }
        else if (regex is not null)
        {
            var match = SafeRegex.TryMatch(html, regex, RegexOptions.Singleline);
            filtered = match?.Value;
        }

        if (string.IsNullOrEmpty(filtered))
        {
            context.Logger.LogWarning("FilterBlock: {SelectorType} selector '{Selector}' matched no content", selectorType, selectorValue);
            return BlockResult.Failed("Selector matched no content.");
        }

        context.Logger.LogInformation("FilterBlock: Extracted {Length} chars of content", filtered.Length);

        var output = JsonSerializer.SerializeToElement(filtered);
        return await Task.FromResult(BlockResult.Succeeded(output));
    }

    private static (string? css, string? xpath, string? regex) ReadConfig(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return (null, null, null);

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return (null, null, null);

        string? css = null, xpath = null, regex = null;

        if (config.TryGetProperty("css", out var cssElem) && cssElem.ValueKind == JsonValueKind.String)
            css = cssElem.GetString();
        else if (config.TryGetProperty("cssSelector", out var cssSelectorElem) && cssSelectorElem.ValueKind == JsonValueKind.String)
            css = cssSelectorElem.GetString();

        if (config.TryGetProperty("xpath", out var xpathElem) && xpathElem.ValueKind == JsonValueKind.String)
            xpath = xpathElem.GetString();
        else if (config.TryGetProperty("xpathSelector", out var xpathSelectorElem) && xpathSelectorElem.ValueKind == JsonValueKind.String)
            xpath = xpathSelectorElem.GetString();

        if (config.TryGetProperty("regex", out var regexElem) && regexElem.ValueKind == JsonValueKind.String)
            regex = regexElem.GetString();

        return (css, xpath, regex);
    }
}
