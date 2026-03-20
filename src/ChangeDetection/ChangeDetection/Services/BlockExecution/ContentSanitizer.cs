using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChangeDetection.Services.BlockExecution;

public record SanitizedContent(
    string Content,
    int OriginalLength,
    int CleanedLength,
    int SuspicionScore,
    bool FlaggedForReview,
    IReadOnlyList<RedactionEvent> Redactions
);

public record RedactionEvent(
    string Type,
    string Snippet
);

/// <summary>
/// Allowlist sanitizer for fetched HTML/JSON before any LLM prompt construction.
/// </summary>
public class ContentSanitizer
{
    private const int MaxOutputLength = 100_000;
    private const int MaxJsonDepth = 20;
    private const int MaxJsonNodes = 10_000;
    private const int MaxArrayItems = 200;
    private const int MaxObjectFields = 200;
    private const int MaxHrefLength = 500;
    private const int MaxKeyLength = 80;
    private const int ReviewThreshold = 50;

    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "div", "section", "article", "main", "nav", "header", "footer", "aside",
        "h1", "h2", "h3", "h4", "h5", "h6",
        "p", "span", "strong", "em", "br", "blockquote",
        "ul", "ol", "li", "dl", "dt", "dd",
        "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption",
        "a", "time", "address", "figure", "figcaption"
    };

    private static readonly HashSet<string> BannedContentTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "svg", "math", "iframe", "embed", "object",
        "form", "textarea", "select", "button", "noscript", "template"
    };

    private static readonly HashSet<string> BannedVoidTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "input", "link", "meta"
    };

    private static readonly HashSet<string> GlobalAllowedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "class", "id"
    };

    private static readonly HashSet<string> AnchorAllowedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "class", "id"
    };

    private static readonly HashSet<string> TimeAllowedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "datetime", "class", "id"
    };

    private static readonly HashSet<string> TableCellAllowedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "colspan", "rowspan", "class", "id"
    };

    private static readonly Regex ZeroWidthRegex = new(@"[\u200B\u200C\u200D\u2060\uFEFF]", RegexOptions.Compiled);
    private static readonly Regex HtmlTagRegex = new(@"<!--.*?-->|<![^>]*>|<[^>]+>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex HtmlAttributeRegex = new(
        @"(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s""'=<>`]+))",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex HtmlTagNameRegex = new(
        @"^<\s*(?<close>/)?\s*(?<name>[A-Za-z][A-Za-z0-9:-]*)\b(?<attrs>[^>]*)/?>$",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex HiddenPairedElementRegex = new(
        @"<(?<tag>[A-Za-z][A-Za-z0-9:-]*)\b[^>]*\bstyle\s*=\s*(?:""(?<style>[^""]*)""|'(?<style>[^']*)')[^>]*>.*?</\k<tag>\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex HiddenSingleElementRegex = new(
        @"<(?<tag>[A-Za-z][A-Za-z0-9:-]*)\b[^>]*\bstyle\s*=\s*(?:""(?<style>[^""]*)""|'(?<style>[^']*)')[^>]*?/?>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex SuspiciousJsonKeyRegex = new(
        "instruction|system|prompt|tool|http|request|header|cookie|script|eval",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ValidJsonKeyRegex = new(
        @"^[A-Za-z0-9._-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex[] InjectionPatterns =
    [
        new(@"ignore\s+(all\s+)?previous\s+(instructions|prompts|rules)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"you\s+are\s+(now\s+)?a", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"new\s+instructions?\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"system\s*:\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\bprompt\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"as\s+an?\s+(ai|assistant|language\s+model)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"<\|[^|]*\|>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\[INST\]|\[/INST\]", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"add\s+(a\s+)?block\s+(that|to|which)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"send\s+(data|results?|output)\s+to", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"forward\s+(to|all)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"fetch\s+(url|from)\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"navigate\s+to\s", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"exfiltrat", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
    ];

    public SanitizedContent SanitizeHtml(string rawHtml)
    {
        var state = new SanitizationState(rawHtml?.Length ?? 0);
        if (string.IsNullOrEmpty(rawHtml))
            return state.ToResult(string.Empty);

        var working = ZeroWidthRegex.Replace(rawHtml, string.Empty);
        working = StripHiddenElements(working, state);
        working = StripBannedTags(working, state);
        working = SanitizeHtmlTags(working, state);
        working = ZeroWidthRegex.Replace(working, string.Empty);
        working = RedactHtmlVisibleText(working, state);
        working = Truncate(working);

        return state.ToResult(working);
    }

    public SanitizedContent SanitizeJson(string rawJson)
    {
        var state = new SanitizationState(rawJson?.Length ?? 0);
        if (string.IsNullOrEmpty(rawJson))
            return state.ToResult(string.Empty);

        var normalized = ZeroWidthRegex.Replace(rawJson, string.Empty);

        try
        {
            using var document = JsonDocument.Parse(normalized);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            {
                WriteSanitizedJson(writer, document.RootElement, state, depth: 0);
            }

            var cleaned = Encoding.UTF8.GetString(stream.ToArray());
            cleaned = Truncate(cleaned);
            return state.ToResult(cleaned);
        }
        catch (JsonException)
        {
            var fallback = SanitizePlainText(normalized, state);
            return state.ToResult(fallback);
        }
    }

    public SanitizedContent Sanitize(string content, string contentType)
    {
        if (LooksLikeJson(contentType, content))
            return SanitizeJson(content);

        if (contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase))
        {
            var state = new SanitizationState(content?.Length ?? 0);
            return state.ToResult(SanitizePlainText(content ?? string.Empty, state));
        }

        return SanitizeHtml(content);
    }

    private static string StripHiddenElements(string html, SanitizationState state)
    {
        var withoutPaired = HiddenPairedElementRegex.Replace(html, match =>
        {
            var style = match.Groups["style"].Value;
            if (!IsHiddenStyle(style))
                return match.Value;

            state.AddRedaction("hidden_element", match.Value, scoreDelta: 5);
            return string.Empty;
        });

        return HiddenSingleElementRegex.Replace(withoutPaired, match =>
        {
            var style = match.Groups["style"].Value;
            if (!IsHiddenStyle(style))
                return match.Value;

            state.AddRedaction("hidden_element", match.Value, scoreDelta: 5);
            return string.Empty;
        });
    }

    private static bool IsHiddenStyle(string style)
    {
        if (string.IsNullOrWhiteSpace(style))
            return false;

        return style.Contains("display:none", StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(style, @"display\s*:\s*none", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               Regex.IsMatch(style, @"visibility\s*:\s*hidden", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               Regex.IsMatch(style, @"font-size\s*:\s*0(?:px|em|rem|%|pt)?(?:\s*;|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               Regex.IsMatch(style, @"color\s*:\s*transparent", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               Regex.IsMatch(style, @"opacity\s*:\s*0(?:\.0+)?(?:\s*;|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               Regex.IsMatch(style, @"position\s*:\s*absolute[^;]*;\s*(?:left|right|top|bottom)\s*:\s*-\d{3,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               Regex.IsMatch(style, @"position\s*:\s*absolute.*?(?:left|right|top|bottom)\s*:\s*-\d{3,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string StripBannedTags(string html, SanitizationState state)
    {
        var working = html;

        foreach (var tag in BannedContentTags)
        {
            var pairedPattern = $@"<\s*{Regex.Escape(tag)}\b[^>]*>.*?<\s*/\s*{Regex.Escape(tag)}\s*>";
            working = Regex.Replace(
                working,
                pairedPattern,
                match =>
                {
                    state.AddRedaction("banned_tag", match.Value, scoreDelta: tag is "script" or "style" ? 2 : 0);
                    return string.Empty;
                },
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

            var singlePattern = $@"<\s*{Regex.Escape(tag)}\b[^>]*?/?>";
            working = Regex.Replace(
                working,
                singlePattern,
                match =>
                {
                    state.AddRedaction("banned_tag", match.Value, scoreDelta: tag is "script" or "style" ? 2 : 0);
                    return string.Empty;
                },
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        }

        foreach (var tag in BannedVoidTags)
        {
            var pattern = $@"<\s*{Regex.Escape(tag)}\b[^>]*?/?>";
            working = Regex.Replace(
                working,
                pattern,
                match =>
                {
                    state.AddRedaction("banned_tag", match.Value);
                    return string.Empty;
                },
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        }

        return working;
    }

    private static string SanitizeHtmlTags(string html, SanitizationState state)
    {
        return HtmlTagRegex.Replace(html, match =>
        {
            var token = match.Value;

            if (token.StartsWith("<!--", StringComparison.Ordinal) || token.StartsWith("<!", StringComparison.Ordinal))
                return string.Empty;

            var tagMatch = HtmlTagNameRegex.Match(token);
            if (!tagMatch.Success)
                return string.Empty;

            var isClosing = tagMatch.Groups["close"].Success;
            var tagName = tagMatch.Groups["name"].Value;

            if (!AllowedTags.Contains(tagName))
            {
                state.AddRedaction("banned_tag", token);
                return string.Empty;
            }

            var normalizedTag = tagName.ToLowerInvariant();
            if (isClosing)
                return $"</{normalizedTag}>";

            if (string.Equals(normalizedTag, "br", StringComparison.Ordinal))
                return "<br>";

            var sanitizedAttributes = SanitizeAttributes(normalizedTag, tagMatch.Groups["attrs"].Value, state);
            return sanitizedAttributes.Length == 0
                ? $"<{normalizedTag}>"
                : $"<{normalizedTag} {sanitizedAttributes}>";
        });
    }

    private static string SanitizeAttributes(string tagName, string rawAttributes, SanitizationState state)
    {
        if (string.IsNullOrWhiteSpace(rawAttributes))
            return string.Empty;

        var allowedAttributes = GetAllowedAttributes(tagName);
        var sanitized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match attributeMatch in HtmlAttributeRegex.Matches(rawAttributes))
        {
            var attributeName = attributeMatch.Groups["name"].Value.ToLowerInvariant();
            var value = ZeroWidthRegex.Replace(attributeMatch.Groups["value"].Value, string.Empty).Trim();

            if (!allowedAttributes.Contains(attributeName) || IsAlwaysBlockedAttribute(attributeName))
            {
                state.AddRedaction("banned_attr", $"{attributeName}={value}");
                continue;
            }

            if (!seen.Add(attributeName))
                continue;

            if (attributeName == "href")
            {
                if (!TrySanitizeHref(value, out var href))
                {
                    state.AddRedaction("banned_attr", $"{attributeName}={value}");
                    continue;
                }

                sanitized.Add($@"href=""{WebUtility.HtmlEncode(href)}""");
                continue;
            }

            if (attributeName is "colspan" or "rowspan")
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var span) || span <= 0)
                {
                    state.AddRedaction("banned_attr", $"{attributeName}={value}");
                    continue;
                }

                sanitized.Add($@"{attributeName}=""{span.ToString(CultureInfo.InvariantCulture)}""");
                continue;
            }

            if (attributeName == "datetime")
            {
                if (value.Length == 0 || value.Length > 100)
                {
                    state.AddRedaction("banned_attr", $"{attributeName}={value}");
                    continue;
                }

                sanitized.Add($@"datetime=""{WebUtility.HtmlEncode(value)}""");
                continue;
            }

            sanitized.Add($@"{attributeName}=""{WebUtility.HtmlEncode(value)}""");
        }

        return string.Join(' ', sanitized);
    }

    private static HashSet<string> GetAllowedAttributes(string tagName) =>
        tagName switch
        {
            "a" => AnchorAllowedAttributes,
            "time" => TimeAllowedAttributes,
            "td" or "th" => TableCellAllowedAttributes,
            _ => GlobalAllowedAttributes
        };

    private static bool IsAlwaysBlockedAttribute(string attributeName) =>
        attributeName.StartsWith("data-", StringComparison.OrdinalIgnoreCase) ||
        attributeName.StartsWith("aria-", StringComparison.OrdinalIgnoreCase) ||
        attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(attributeName, "style", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(attributeName, "title", StringComparison.OrdinalIgnoreCase);

    private static bool TrySanitizeHref(string href, out string sanitizedHref)
    {
        sanitizedHref = string.Empty;
        if (string.IsNullOrWhiteSpace(href) || href.Length > MaxHrefLength)
            return false;

        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        sanitizedHref = uri.ToString();
        return sanitizedHref.Length <= MaxHrefLength;
    }

    private static string RedactHtmlVisibleText(string html, SanitizationState state)
    {
        var builder = new StringBuilder(html.Length);
        var lastIndex = 0;

        foreach (Match tagMatch in HtmlTagRegex.Matches(html))
        {
            if (tagMatch.Index > lastIndex)
            {
                var textSegment = html[lastIndex..tagMatch.Index];
                builder.Append(RedactText(textSegment, state));
            }

            builder.Append(tagMatch.Value);
            lastIndex = tagMatch.Index + tagMatch.Length;
        }

        if (lastIndex < html.Length)
            builder.Append(RedactText(html[lastIndex..], state));

        return builder.ToString();
    }

    private static string RedactText(string text, SanitizationState state)
    {
        var working = ZeroWidthRegex.Replace(text ?? string.Empty, string.Empty);
        foreach (var pattern in InjectionPatterns)
        {
            working = pattern.Replace(working, match =>
            {
                state.AddRedaction("injection_pattern", match.Value, scoreDelta: 20);
                return "[REDACTED]";
            });
        }

        return working;
    }

    private static string SanitizePlainText(string text, SanitizationState state)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var cleaned = ZeroWidthRegex.Replace(text, string.Empty);
        cleaned = new string(cleaned.Where(ch => !char.IsControl(ch) || ch is '\n' or '\r' or '\t').ToArray());
        cleaned = RedactText(cleaned, state);
        return Truncate(cleaned);
    }

    private static void WriteSanitizedJson(Utf8JsonWriter writer, JsonElement element, SanitizationState state, int depth)
    {
        if (depth >= MaxJsonDepth || !state.TryConsumeNode())
        {
            writer.WriteNullValue();
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var fieldCount = 0;
                foreach (var property in element.EnumerateObject())
                {
                    if (fieldCount >= MaxObjectFields)
                        break;

                    if (!IsAllowedJsonKey(property.Name, state))
                        continue;

                    writer.WritePropertyName(property.Name);
                    WriteSanitizedJson(writer, property.Value, state, depth + 1);
                    fieldCount++;
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (index >= MaxArrayItems)
                        break;

                    WriteSanitizedJson(writer, item, state, depth + 1);
                    index++;
                }
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(RedactText(element.GetString() ?? string.Empty, state));
                break;

            case JsonValueKind.Number:
                element.WriteTo(writer);
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(element.GetBoolean());
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static bool IsAllowedJsonKey(string key, SanitizationState state)
    {
        if (string.IsNullOrWhiteSpace(key) ||
            key.Length > MaxKeyLength ||
            !ValidJsonKeyRegex.IsMatch(key) ||
            SuspiciousJsonKeyRegex.IsMatch(key))
        {
            state.AddRedaction("suspicious_key", key);
            return false;
        }

        return true;
    }

    private static bool LooksLikeJson(string contentType, string content)
    {
        if (!string.IsNullOrWhiteSpace(contentType) &&
            contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var trimmed = content?.TrimStart();
        return !string.IsNullOrEmpty(trimmed) &&
               (trimmed.StartsWith('{') || trimmed.StartsWith('['));
    }

    private static string Truncate(string content) =>
        string.IsNullOrEmpty(content) || content.Length <= MaxOutputLength
            ? content
            : content[..MaxOutputLength];

    private sealed class SanitizationState(int originalLength)
    {
        private readonly List<RedactionEvent> _redactions = [];

        public int OriginalLength { get; } = originalLength;
        public int SuspicionScore { get; private set; }
        public int NodeCount { get; private set; }

        public bool TryConsumeNode()
        {
            NodeCount++;
            return NodeCount <= MaxJsonNodes;
        }

        public void AddRedaction(string type, string snippet, int scoreDelta = 0)
        {
            if (scoreDelta > 0)
                SuspicionScore += scoreDelta;

            _redactions.Add(new RedactionEvent(type, CreateSnippet(snippet)));
        }

        public SanitizedContent ToResult(string content) =>
            new(
                content,
                OriginalLength,
                content.Length,
                SuspicionScore,
                SuspicionScore > ReviewThreshold,
                _redactions);

        private static string CreateSnippet(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return normalized.Length <= 80 ? normalized : normalized[..80];
        }
    }
}
