using System.Text;
using System.Text.RegularExpressions;
using ChangeDetection.Core.Interfaces;
using HtmlAgilityPack;

namespace ChangeDetection.Services.Content;

/// <summary>
/// Compacts HTML DOM while preserving selector-relevant structure.
/// Implements D2Snap-inspired downsampling but retains tag names and attributes
/// needed for CSS/XPath selector generation.
/// </summary>
public partial class DomCompactor(ILogger<DomCompactor> logger) : IDomCompactor
{
    // Regex patterns for utility class detection
    private static readonly HashSet<string> TailwindPrefixes =
    [
        // Layout
        "flex", "grid", "block", "inline", "hidden", "container",
        // Spacing
        "p-", "px-", "py-", "pt-", "pb-", "pl-", "pr-", "m-", "mx-", "my-", "mt-", "mb-", "ml-", "mr-",
        "space-x-", "space-y-", "gap-",
        // Sizing
        "w-", "h-", "min-w-", "min-h-", "max-w-", "max-h-",
        // Typography
        "text-", "font-", "leading-", "tracking-", "uppercase", "lowercase", "capitalize",
        // Colors/Background
        "bg-", "text-gray-", "text-white", "text-black",
        // Borders
        "border", "border-", "rounded", "rounded-",
        // Effects
        "shadow", "shadow-", "opacity-", "blur-",
        // Position
        "absolute", "relative", "fixed", "sticky", "top-", "bottom-", "left-", "right-", "inset-",
        "z-",
        // Flexbox
        "justify-", "items-", "content-", "self-", "flex-",
        // Grid
        "col-", "row-", "grid-",
        // Transforms
        "transform", "rotate-", "scale-", "translate-",
        // Transitions
        "transition", "duration-", "ease-", "delay-",
        // Responsive
        "sm:", "md:", "lg:", "xl:", "2xl:",
        // States
        "hover:", "focus:", "active:", "disabled:", "group-",
        // Dark mode
        "dark:"
    ];

    private static readonly HashSet<string> BootstrapPrefixes =
    [
        "col-", "row", "container", "container-", "d-", "align-", "justify-",
        "text-", "bg-", "border-", "rounded-", "shadow-", "p-", "m-", "px-", "py-",
        "pt-", "pb-", "ps-", "pe-", "mx-", "my-", "mt-", "mb-", "ms-", "me-",
        "btn", "btn-", "form-", "input-", "card", "card-", "nav", "nav-", "navbar",
        "modal", "modal-", "dropdown", "dropdown-", "list-", "table-"
    ];

    public DomCompactionResult Compact(string html, DomCompactorOptions? options = null)
    {
        options ??= DomCompactorOptions.Default;
        var originalSize = html.Length;

        if (string.IsNullOrWhiteSpace(html))
        {
            return new DomCompactionResult
            {
                Html = html,
                OriginalSize = originalSize,
                CompactedSize = originalSize
            };
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var stats = new CompactionStats();

        // Process the document
        ProcessNode(doc.DocumentNode, options, stats, 0);

        // Serialize back to HTML
        var result = SerializeCompact(doc.DocumentNode);

        logger.LogDebug(
            "DOM compacted: {Original} -> {Compacted} chars ({Ratio:P0}), removed {Removed} elements, collapsed {Collapsed} wrappers",
            originalSize, result.Length, (float)result.Length / originalSize, stats.ElementsRemoved, stats.WrappersCollapsed);

        return new DomCompactionResult
        {
            Html = result,
            OriginalSize = originalSize,
            CompactedSize = result.Length,
            ElementsRemoved = stats.ElementsRemoved,
            WrappersCollapsed = stats.WrappersCollapsed
        };
    }

    public DomCompactionResult CompactToTokenBudget(string html, int targetTokens, int maxIterations = 5)
    {
        // Rough estimate: 4 chars per token
        var targetChars = targetTokens * 4;

        var options = DomCompactorOptions.Default;
        DomCompactionResult result = Compact(html, options);

        for (var i = 0; i < maxIterations && result.CompactedSize > targetChars; i++)
        {
            // Progressively increase aggressiveness
            options = options with
            {
                MaxTextLength = Math.Max(10, options.MaxTextLength - 10),
                MaxClassesPerElement = Math.Max(1, options.MaxClassesPerElement - 1),
                MaxDepth = options.MaxDepth == 0 ? 10 : Math.Max(3, options.MaxDepth - 2)
            };

            result = Compact(html, options);
            logger.LogDebug("Compaction iteration {Iteration}: {Size} chars (target: {Target})", 
                i + 1, result.CompactedSize, targetChars);
        }

        return result;
    }

    private void ProcessNode(HtmlNode node, DomCompactorOptions options, CompactionStats stats, int depth)
    {
        if (node.NodeType == HtmlNodeType.Document)
        {
            ProcessChildren(node, options, stats, depth);
            return;
        }

        if (node.NodeType == HtmlNodeType.Text)
        {
            ProcessTextNode(node, options);
            return;
        }

        if (node.NodeType == HtmlNodeType.Comment)
        {
            node.Remove();
            return;
        }

        if (node.NodeType != HtmlNodeType.Element)
            return;

        var tagName = node.Name.ToLowerInvariant();

        // Remove unwanted tags entirely
        if (options.RemoveTags.Contains(tagName))
        {
            stats.ElementsRemoved++;
            node.Remove();
            return;
        }

        // Check depth limit
        if (options.MaxDepth > 0 && depth > options.MaxDepth)
        {
            // Summarize deep content
            var textContent = GetTruncatedText(node.InnerText, options.MaxTextLength);
            if (!string.IsNullOrWhiteSpace(textContent))
            {
                var textNode = node.OwnerDocument.CreateTextNode(textContent);
                node.ParentNode?.InsertBefore(textNode, node);
            }
            stats.ElementsRemoved++;
            node.Remove();
            return;
        }

        // Process attributes
        ProcessAttributes(node, options);

        // Process children first
        ProcessChildren(node, options, stats, depth + 1);

        // Collapse empty wrappers after children are processed
        if (options.CollapseEmptyWrappers && ShouldCollapseWrapper(node))
        {
            CollapseWrapper(node);
            stats.WrappersCollapsed++;
        }
    }

    private void ProcessChildren(HtmlNode node, DomCompactorOptions options, CompactionStats stats, int depth)
    {
        // Create a copy of child nodes since we may modify the collection
        var children = node.ChildNodes.ToList();
        foreach (var child in children)
        {
            ProcessNode(child, options, stats, depth);
        }
    }

    private void ProcessTextNode(HtmlNode node, DomCompactorOptions options)
    {
        var text = node.InnerText;

        // Normalize whitespace
        text = WhitespaceRegex().Replace(text, " ").Trim();

        if (string.IsNullOrWhiteSpace(text))
        {
            node.Remove();
            return;
        }

        // Truncate if too long
        if (text.Length > options.MaxTextLength)
        {
            text = GetTruncatedText(text, options.MaxTextLength);
        }

        // Update the text
        if (node.ParentNode != null)
        {
            var newNode = node.OwnerDocument.CreateTextNode(text);
            node.ParentNode.ReplaceChild(newNode, node);
        }
    }

    private void ProcessAttributes(HtmlNode node, DomCompactorOptions options)
    {
        var attributesToRemove = new List<string>();

        foreach (var attr in node.Attributes)
        {
            var name = attr.Name.ToLowerInvariant();

            // Check if this attribute should be preserved
            if (options.PreserveAttributes.Contains(name))
            {
                // Special handling for class attribute
                if (name == "class")
                {
                    attr.Value = FilterClasses(attr.Value, options);
                    if (string.IsNullOrWhiteSpace(attr.Value))
                    {
                        attributesToRemove.Add(attr.Name);
                    }
                }
                continue;
            }

            // Check prefixes
            if (options.PreserveAttributePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // Remove unneeded attributes
            attributesToRemove.Add(attr.Name);
        }

        foreach (var attrName in attributesToRemove)
        {
            node.Attributes.Remove(attrName);
        }
    }

    private string FilterClasses(string classValue, DomCompactorOptions options)
    {
        if (string.IsNullOrWhiteSpace(classValue))
            return string.Empty;

        var classes = classValue.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        IEnumerable<string> filteredClasses = classes;

        if (options.FilterUtilityClasses)
        {
            filteredClasses = classes.Where(c => !IsUtilityClass(c));
        }

        // Take only the first N meaningful classes
        var result = filteredClasses.Take(options.MaxClassesPerElement).ToList();

        return string.Join(" ", result);
    }

    private static bool IsUtilityClass(string className)
    {
        var lower = className.ToLowerInvariant();

        // Check Tailwind patterns
        foreach (var prefix in TailwindPrefixes)
        {
            if (lower.StartsWith(prefix, StringComparison.Ordinal) || lower == prefix.TrimEnd('-'))
                return true;
        }

        // Check Bootstrap patterns
        foreach (var prefix in BootstrapPrefixes)
        {
            if (lower.StartsWith(prefix, StringComparison.Ordinal) || lower == prefix.TrimEnd('-'))
                return true;
        }

        // Common utility patterns
        if (UtilityClassPattern().IsMatch(className))
            return true;

        return false;
    }

    private static bool ShouldCollapseWrapper(HtmlNode node)
    {
        // Only collapse divs and spans
        var tagName = node.Name.ToLowerInvariant();
        if (tagName is not ("div" or "span"))
            return false;

        // Don't collapse if it has an ID or meaningful classes
        if (node.Attributes["id"] != null)
            return false;

        var classValue = node.GetAttributeValue("class", "");
        if (!string.IsNullOrWhiteSpace(classValue))
            return false;

        // Don't collapse if it has data attributes
        if (node.Attributes.Any(a => a.Name.StartsWith("data-", StringComparison.OrdinalIgnoreCase)))
            return false;

        // Collapse if it has exactly one element child (text nodes don't count)
        var elementChildren = node.ChildNodes.Where(c => c.NodeType == HtmlNodeType.Element).ToList();
        return elementChildren.Count == 1;
    }

    private static void CollapseWrapper(HtmlNode wrapper)
    {
        var parent = wrapper.ParentNode;
        if (parent == null) return;

        // Move all children to before the wrapper
        foreach (var child in wrapper.ChildNodes.ToList())
        {
            parent.InsertBefore(child, wrapper);
        }

        // Remove the wrapper
        wrapper.Remove();
    }

    private static string GetTruncatedText(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text;

        // Try to truncate at word boundary
        var truncated = text[..maxLength];
        var lastSpace = truncated.LastIndexOf(' ');
        if (lastSpace > maxLength / 2)
        {
            truncated = truncated[..lastSpace];
        }

        return truncated + "...";
    }

    private static string SerializeCompact(HtmlNode node)
    {
        var sb = new StringBuilder();
        SerializeNode(node, sb);
        return sb.ToString().Trim();
    }

    private static void SerializeNode(HtmlNode node, StringBuilder sb)
    {
        switch (node.NodeType)
        {
            case HtmlNodeType.Document:
                foreach (var child in node.ChildNodes)
                    SerializeNode(child, sb);
                break;

            case HtmlNodeType.Text:
                var text = node.InnerText;
                if (!string.IsNullOrWhiteSpace(text))
                    sb.Append(text);
                break;

            case HtmlNodeType.Element:
                // Skip html/body wrapper if it's the only content
                var tagName = node.Name.ToLowerInvariant();
                if (tagName is "html" or "body" && node.Attributes.Count == 0)
                {
                    foreach (var child in node.ChildNodes)
                        SerializeNode(child, sb);
                    return;
                }

                sb.Append('<').Append(node.Name);

                // Add attributes
                foreach (var attr in node.Attributes)
                {
                    sb.Append(' ').Append(attr.Name).Append("=\"").Append(attr.Value).Append('"');
                }

                // Self-closing or with content
                if (IsSelfClosing(tagName) && !node.HasChildNodes)
                {
                    sb.Append("/>");
                }
                else
                {
                    sb.Append('>');
                    foreach (var child in node.ChildNodes)
                        SerializeNode(child, sb);
                    sb.Append("</").Append(node.Name).Append('>');
                }
                break;
        }
    }

    private static bool IsSelfClosing(string tagName) =>
        tagName is "br" or "hr" or "img" or "input" or "meta" or "link" or "area" or "base" or "col" or "embed" or "param" or "source" or "track" or "wbr";

    private class CompactionStats
    {
        public int ElementsRemoved { get; set; }
        public int WrappersCollapsed { get; set; }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^(xs|sm|md|lg|xl|xxl|\d+)$")]
    private static partial Regex UtilityClassPattern();
}
