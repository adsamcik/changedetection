using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChangeDetection.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.Blocks.Extraction;

#region 5a — DeduplicateBlock

/// <summary>
/// Removes duplicate items from a JSON array based on an identity key field.
/// Config: { "identityKey": "url" }
/// </summary>
public class DeduplicateBlock : IPipelineBlock
{
    public string BlockType => "Deduplicate";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Extraction;
    public bool IsCacheable => true;

    public Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("data", out var dataElement))
            return Task.FromResult(BlockResult.Failed("Deduplicate block requires a 'data' input."));

        if (dataElement.ValueKind != JsonValueKind.Array)
            return Task.FromResult(BlockResult.Succeeded(dataElement));

        var identityKey = ReadIdentityKey(context);
        if (string.IsNullOrEmpty(identityKey))
            return Task.FromResult(BlockResult.Succeeded(dataElement));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<JsonElement>();

        foreach (var item in dataElement.EnumerateArray())
        {
            var keyValue = item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty(identityKey, out var kv)
                    ? kv.ToString()
                    : null;

            if (keyValue is null || seen.Add(keyValue))
                unique.Add(item.Clone());
        }

        var removed = dataElement.GetArrayLength() - unique.Count;
        if (removed > 0)
            context.Logger.LogInformation("Deduplicate: removed {Count} duplicates on key '{Key}'", removed, identityKey);

        return Task.FromResult(BlockResult.Succeeded(JsonSerializer.SerializeToElement(unique)));
    }

    private static string? ReadIdentityKey(BlockContext context)
    {
        if (context.PipelineDefinition is not { } pipeline) return null;
        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));
        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config) return null;
        return config.TryGetProperty("identityKey", out var key) && key.ValueKind == JsonValueKind.String
            ? key.GetString()
            : null;
    }
}

#endregion

#region 5b — StripHtmlBlock

/// <summary>
/// Strips HTML tags from specified text fields, returning plain text.
/// Config: { "fields": ["description", "requirements"] }
/// </summary>
public class StripHtmlBlock : IPipelineBlock
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    public string BlockType => "StripHtml";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Extraction;
    public bool IsCacheable => true;

    public Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("data", out var dataElement))
            return Task.FromResult(BlockResult.Failed("StripHtml block requires a 'data' input."));

        var fields = ReadFields(context);
        if (fields.Count == 0)
            return Task.FromResult(BlockResult.Succeeded(dataElement));

        try
        {
            var result = dataElement.ValueKind switch
            {
                JsonValueKind.Array => JsonSerializer.SerializeToElement(
                    dataElement.EnumerateArray().Select(item => CleanItem(item, fields)).ToList()),
                JsonValueKind.Object => CleanItem(dataElement, fields),
                _ => dataElement
            };
            return Task.FromResult(BlockResult.Succeeded(result));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(BlockResult.Failed($"StripHtml error: {ex.Message}"));
        }
    }

    private static JsonElement CleanItem(JsonElement item, List<string> fields)
    {
        if (item.ValueKind != JsonValueKind.Object) return item;

        var dict = new Dictionary<string, object?>();
        foreach (var prop in item.EnumerateObject())
        {
            dict[prop.Name] = fields.Contains(prop.Name, StringComparer.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.String
                    ? StripTags(prop.Value.GetString()!)
                    : prop.Value;
        }

        return JsonSerializer.SerializeToElement(dict);
    }

    private static string StripTags(string html)
    {
        var text = Regex.Replace(html, @"<[^>]+>", " ", RegexOptions.None, RegexTimeout);
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s{2,}", " ", RegexOptions.None, RegexTimeout);
        return text.Trim();
    }

    private static List<string> ReadFields(BlockContext context)
    {
        var result = new List<string>();
        if (context.PipelineDefinition is not { } pipeline) return result;
        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));
        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config) return result;
        if (config.TryGetProperty("fields", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in arr.EnumerateArray())
            {
                if (f.ValueKind == JsonValueKind.String && f.GetString() is { } s)
                    result.Add(s);
            }
        }
        return result;
    }
}

#endregion

#region 5c — TemplateResolveBlock

/// <summary>
/// Resolves {{item.field}} templates in config values, adding computed fields to each item.
/// Config: { "computedFields": [{ "name": "fullUrl", "template": "https://example.com{{item.path}}" }] }
/// </summary>
public class TemplateResolveBlock : IPipelineBlock
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    public string BlockType => "TemplateResolve";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Extraction;
    public bool IsCacheable => true;

    public Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("data", out var dataElement))
            return Task.FromResult(BlockResult.Failed("TemplateResolve block requires a 'data' input."));

        if (dataElement.ValueKind != JsonValueKind.Array)
            return Task.FromResult(BlockResult.Succeeded(dataElement));

        var computedFields = ReadComputedFields(context);
        if (computedFields.Count == 0)
            return Task.FromResult(BlockResult.Succeeded(dataElement));

        var items = new List<JsonElement>();
        foreach (var item in dataElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                items.Add(item.Clone());
                continue;
            }

            var dict = new Dictionary<string, object?>();
            foreach (var prop in item.EnumerateObject())
                dict[prop.Name] = prop.Value;

            foreach (var (name, template) in computedFields)
                dict[name] = ResolveTemplate(template, item);

            items.Add(JsonSerializer.SerializeToElement(dict));
        }

        return Task.FromResult(BlockResult.Succeeded(JsonSerializer.SerializeToElement(items)));
    }

    private static string ResolveTemplate(string template, JsonElement item)
    {
        return Regex.Replace(template, @"\{\{item\.([^}]+)\}\}", match =>
        {
            var fieldPath = match.Groups[1].Value;
            var value = ResolveFieldPath(item, fieldPath);
            return value ?? "";
        }, RegexOptions.None, RegexTimeout);
    }

    private static string? ResolveFieldPath(JsonElement element, string path)
    {
        var current = element;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
                return null;
            current = next;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.ToString();
    }

    private static List<(string Name, string Template)> ReadComputedFields(BlockContext context)
    {
        var result = new List<(string, string)>();
        if (context.PipelineDefinition is not { } pipeline) return result;
        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));
        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config) return result;
        if (!config.TryGetProperty("computedFields", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var cf in arr.EnumerateArray())
        {
            if (cf.ValueKind != JsonValueKind.Object) continue;
            if (!cf.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String) continue;
            if (!cf.TryGetProperty("template", out var tplProp) || tplProp.ValueKind != JsonValueKind.String) continue;
            result.Add((nameProp.GetString()!, tplProp.GetString()!));
        }
        return result;
    }
}

#endregion

#region 5d — CookieConsentPatterns

/// <summary>
/// Known cookie-consent button selectors for use with ClickBlock.
/// </summary>
public record ConsentPattern(
    string Name,
    string AcceptSelector,
    string? DeclineSelector,
    string? DetectionSelector);

/// <summary>
/// Static helper providing CSS selectors for common Cookie Management Platforms.
/// </summary>
public static class CookieConsentPatterns
{
    private static readonly IReadOnlyList<ConsentPattern> Patterns =
    [
        new("OneTrust",
            AcceptSelector: "#onetrust-accept-btn-handler",
            DeclineSelector: "#onetrust-reject-all-handler",
            DetectionSelector: "#onetrust-banner-sdk"),

        new("CookieBot",
            AcceptSelector: "#CybotCookiebotDialogBodyLevelButtonLevelOptinAllowAll",
            DeclineSelector: "#CybotCookiebotDialogBodyLevelButtonLevelOptinDeclineAll",
            DetectionSelector: "#CybotCookiebotDialog"),

        new("Generic GDPR",
            AcceptSelector: "button[data-action=\"accept\"], .cookie-accept, .accept-cookies",
            DeclineSelector: "button[data-action=\"reject\"], .cookie-reject, .reject-cookies",
            DetectionSelector: ".cookie-banner, .cookie-consent, .gdpr-banner"),

        new("Workday",
            AcceptSelector: "button[data-automation-id=\"legalNoticeAccept\"]",
            DeclineSelector: null,
            DetectionSelector: "[data-automation-id=\"legalNoticeActions\"]"),

        new("Workable",
            AcceptSelector: "button:has-text(\"Accept all\")",
            DeclineSelector: "button:has-text(\"Reject all\")",
            DetectionSelector: ".cookie-consent-modal, [data-ui=\"cookie-consent\"]")
    ];

    /// <summary>Returns CSS selectors for common CMPs that can be used with ClickBlock.</summary>
    public static IReadOnlyList<ConsentPattern> GetKnownPatterns() => Patterns;
}

#endregion

#region 5e — RateLimitHelper

/// <summary>
/// Reads a standard rateLimitMs config field from block config and applies a delay.
/// </summary>
public static class RateLimitHelper
{
    private const int MinMs = 100;
    private const int MaxMs = 10_000;

    /// <summary>
    /// Gets the configured rate limit in milliseconds, clamped to [100, 10000].
    /// Returns <paramref name="defaultMs"/> when not configured or zero.
    /// </summary>
    public static int GetRateLimitMs(BlockContext context, int defaultMs = 0)
    {
        if (context.PipelineDefinition is not { } pipeline) return defaultMs;
        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));
        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config) return defaultMs;
        if (!config.TryGetProperty("rateLimitMs", out var prop)) return defaultMs;

        var ms = prop.ValueKind == JsonValueKind.Number ? prop.GetInt32() : defaultMs;
        return ms <= 0 ? defaultMs : Math.Clamp(ms, MinMs, MaxMs);
    }

    /// <summary>
    /// Reads rateLimitMs from block config and delays if configured.
    /// Minimum 100 ms, maximum 10 000 ms. Default: 0 (no delay).
    /// </summary>
    public static async Task ApplyRateLimitAsync(BlockContext context, CancellationToken ct = default)
    {
        var ms = GetRateLimitMs(context);
        if (ms > 0)
            await Task.Delay(ms, ct);
    }
}

#endregion
