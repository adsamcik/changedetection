using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ChangeDetection.Core.Interfaces;
using HtmlAgilityPack;

namespace ChangeDetection.Services.Content;

/// <summary>
/// Service for extracting and processing HTML content.
/// </summary>
public partial class ContentExtractor : IContentExtractor
{
    public string ExtractText(string html, string? cssSelector = null, string? xpathSelector = null)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        HtmlNode? targetNode = doc.DocumentNode;

        // Apply XPath selector if provided
        if (!string.IsNullOrEmpty(xpathSelector))
        {
            targetNode = doc.DocumentNode.SelectSingleNode(xpathSelector);
        }
        // Apply CSS selector if provided (convert to XPath)
        else if (!string.IsNullOrEmpty(cssSelector))
        {
            var xpath = CssToXPath(cssSelector);
            targetNode = doc.DocumentNode.SelectSingleNode(xpath);
        }

        if (targetNode == null)
        {
            return string.Empty;
        }

        // Remove script and style elements
        var nodesToRemove = targetNode.SelectNodes("//script|//style|//noscript");
        if (nodesToRemove != null)
        {
            foreach (var script in nodesToRemove)
            {
                script.Remove();
            }
        }

        // Extract text with block element awareness for proper formatting
        var sb = new StringBuilder();
        ExtractFormattedText(targetNode, sb);
        
        var text = sb.ToString();
        
        // Clean up excessive blank lines (more than 2 consecutive)
        text = ExcessiveNewlinesRegex().Replace(text, "\n\n");
        text = text.Trim();

        return text;
    }
    
    /// <summary>
    /// Recursively extracts text from HTML nodes, adding line breaks for block elements.
    /// </summary>
    private static void ExtractFormattedText(HtmlNode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child.NodeType)
            {
                case HtmlNodeType.Text:
                    var text = child.InnerText;
                    // Normalize inline whitespace but preserve the content
                    text = WhitespaceRegex().Replace(text, " ");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        sb.Append(text);
                    }
                    break;
                    
                case HtmlNodeType.Element:
                    var tagName = child.Name.ToLowerInvariant();
                    
                    // Skip hidden elements
                    if (tagName is "script" or "style" or "noscript" or "template")
                    {
                        continue;
                    }
                    
                    // Handle line break elements
                    if (tagName == "br")
                    {
                        sb.AppendLine();
                        continue;
                    }
                    
                    // Block elements that should start on a new line
                    var isBlockElement = tagName is "p" or "div" or "section" or "article" 
                        or "header" or "footer" or "nav" or "aside" or "main"
                        or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
                        or "ul" or "ol" or "li" or "dl" or "dt" or "dd"
                        or "table" or "tr" or "blockquote" or "pre" or "hr"
                        or "form" or "fieldset" or "address" or "figure" or "figcaption";
                    
                    if (isBlockElement && sb.Length > 0 && !EndsWithNewline(sb))
                    {
                        sb.AppendLine();
                    }
                    
                    // Recurse into children
                    ExtractFormattedText(child, sb);
                    
                    // Add trailing newline for block elements
                    if (isBlockElement && sb.Length > 0 && !EndsWithNewline(sb))
                    {
                        sb.AppendLine();
                    }
                    break;
            }
        }
    }
    
    private static bool EndsWithNewline(StringBuilder sb)
    {
        if (sb.Length == 0) return true;
        var lastChar = sb[sb.Length - 1];
        return lastChar == '\n' || lastChar == '\r';
    }

    public string? ExtractHtml(string html, string? cssSelector = null, string? xpathSelector = null)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        HtmlNode? targetNode = doc.DocumentNode;

        if (!string.IsNullOrEmpty(xpathSelector))
        {
            targetNode = doc.DocumentNode.SelectSingleNode(xpathSelector);
        }
        else if (!string.IsNullOrEmpty(cssSelector))
        {
            var xpath = CssToXPath(cssSelector);
            targetNode = doc.DocumentNode.SelectSingleNode(xpath);
        }

        return targetNode?.InnerHtml;
    }

    public string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    public string? ExtractTitle(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        return titleNode?.InnerText?.Trim();
    }

    public string CleanHtml(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Remove script, style, and comment nodes
        var nodesToRemove = doc.DocumentNode.SelectNodes("//script|//style|//noscript|//comment()");
        if (nodesToRemove != null)
        {
            foreach (var node in nodesToRemove.ToList())
            {
                node.Remove();
            }
        }

        // Remove all attributes except essential ones
        var allNodes = doc.DocumentNode.SelectNodes("//*");
        if (allNodes != null)
        {
            foreach (var node in allNodes)
            {
                var attributesToRemove = node.Attributes
                    .Where(a => !IsEssentialAttribute(a.Name))
                    .ToList();
                
                foreach (var attr in attributesToRemove)
                {
                    node.Attributes.Remove(attr);
                }
            }
        }

        return doc.DocumentNode.OuterHtml;
    }

    private static bool IsEssentialAttribute(string name)
    {
        return name is "href" or "src" or "alt" or "title" or "id" or "class";
    }

    /// <summary>
    /// Basic CSS to XPath conversion for common selectors.
    /// </summary>
    private static string CssToXPath(string css)
    {
        // Handle ID selector
        if (css.StartsWith('#'))
        {
            return $"//*[@id='{css[1..]}']";
        }

        // Handle class selector
        if (css.StartsWith('.'))
        {
            return $"//*[contains(@class, '{css[1..]}')]";
        }

        // Handle element selector
        if (ElementNameRegex().IsMatch(css))
        {
            return $"//{css}";
        }

        // Handle element.class
        var elementClassMatch = ElementClassRegex().Match(css);
        if (elementClassMatch.Success)
        {
            var element = elementClassMatch.Groups[1].Value;
            var className = elementClassMatch.Groups[2].Value;
            return $"//{element}[contains(@class, '{className}')]";
        }

        // Handle element#id
        var elementIdMatch = ElementIdRegex().Match(css);
        if (elementIdMatch.Success)
        {
            var element = elementIdMatch.Groups[1].Value;
            var id = elementIdMatch.Groups[2].Value;
            return $"//{element}[@id='{id}']";
        }

        // Handle descendant selector (space)
        if (css.Contains(' '))
        {
            var parts = css.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var xpathParts = parts.Select(CssToXPath);
            return string.Join("", xpathParts);
        }

        // Fallback: treat as element name
        return $"//{css}";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
    
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessiveNewlinesRegex();

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9]*$")]
    private static partial Regex ElementNameRegex();

    [GeneratedRegex(@"^([a-zA-Z][a-zA-Z0-9]*)\.([a-zA-Z][a-zA-Z0-9_-]*)$")]
    private static partial Regex ElementClassRegex();

    [GeneratedRegex(@"^([a-zA-Z][a-zA-Z0-9]*)#([a-zA-Z][a-zA-Z0-9_-]*)$")]
    private static partial Regex ElementIdRegex();
}
