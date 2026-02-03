using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ChangeDetection.Core.Interfaces;
using Fizzler.Systems.HtmlAgilityPack;
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
        // Apply CSS selector if provided
        else if (!string.IsNullOrEmpty(cssSelector))
        {
            try
            {
                // Use the same CSS selector engine as selector validation (supports >, +, etc.)
                targetNode = doc.DocumentNode.QuerySelectorAll(cssSelector).FirstOrDefault();
            }
            catch
            {
                targetNode = null;
            }
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

        var text = targetNode.InnerText;
        
        // Normalize whitespace
        text = WhitespaceRegex().Replace(text, " ");
        text = text.Trim();

        return text;
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
            try
            {
                targetNode = doc.DocumentNode.QuerySelectorAll(cssSelector).FirstOrDefault();
            }
            catch
            {
                targetNode = null;
            }
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

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9]*$")]
    private static partial Regex ElementNameRegex();

    [GeneratedRegex(@"^([a-zA-Z][a-zA-Z0-9]*)\.([a-zA-Z][a-zA-Z0-9_-]*)$")]
    private static partial Regex ElementClassRegex();

    [GeneratedRegex(@"^([a-zA-Z][a-zA-Z0-9]*)#([a-zA-Z][a-zA-Z0-9_-]*)$")]
    private static partial Regex ElementIdRegex();
}
