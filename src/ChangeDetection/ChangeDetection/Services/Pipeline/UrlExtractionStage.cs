using System.Text.RegularExpressions;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Pipeline;

/// <summary>
/// Stage 1: Extracts URLs from user input using regex and simple parsing.
/// No LLM needed - pure text processing for reliability.
/// </summary>
public partial class UrlExtractionStage
{
    /// <summary>
    /// Extracts all URLs from the user input.
    /// </summary>
    public List<ExtractedUrl> Extract(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return [];

        var results = new List<ExtractedUrl>();
        // Track normalized URLs to avoid duplicates (e.g., www vs non-www)
        var seenNormalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Track match positions to avoid overlapping matches
        var matchedRanges = new List<(int Start, int End)>();

        // Extract full URLs (http/https) first - these take priority
        foreach (Match match in FullUrlRegex().Matches(input))
        {
            var url = match.Value;
            // Clean trailing punctuation that might be sentence-ending
            url = CleanTrailingPunctuation(url);
            var normalized = NormalizeUrl(url);
            
            if (seenNormalized.Add(NormalizeForDedup(normalized)))
            {
                matchedRanges.Add((match.Index, match.Index + match.Length));
                var context = ExtractContext(input, match.Index, match.Length);
                results.Add(new ExtractedUrl
                {
                    Url = url,
                    NormalizedUrl = normalized,
                    Context = context,
                    IsValid = IsValidUrl(url)
                });
            }
        }

        // Extract domain-only patterns (e.g., example.com/path)
        // Skip patterns that overlap with already matched full URLs
        foreach (Match match in DomainPatternRegex().Matches(input))
        {
            // Check if this match overlaps with an already matched full URL
            var matchStart = match.Index;
            var matchEnd = match.Index + match.Length;
            if (matchedRanges.Any(r => matchStart >= r.Start && matchEnd <= r.End))
                continue;

            var url = match.Value;
            url = CleanTrailingPunctuation(url);
            var normalized = $"https://{url}";
            
            if (seenNormalized.Add(NormalizeForDedup(normalized)))
            {
                var context = ExtractContext(input, match.Index, match.Length);
                results.Add(new ExtractedUrl
                {
                    Url = url,
                    NormalizedUrl = normalized,
                    Context = context,
                    IsValid = IsValidUrl(normalized)
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Normalizes a URL for deduplication - removes www prefix and trailing slashes.
    /// </summary>
    private static string NormalizeForDedup(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        // Parse and remove www
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www."))
                host = host[4..];
            
            // Rebuild without www and with normalized path
            var path = uri.AbsolutePath.TrimEnd('/');
            return $"{uri.Scheme}://{host}{path}{uri.Query}";
        }

        return url.ToLowerInvariant();
    }

    /// <summary>
    /// Determines the most likely target URL from extracted URLs based on context.
    /// </summary>
    public ExtractedUrl? SelectPrimaryUrl(List<ExtractedUrl> urls, string originalInput)
    {
        if (urls.Count == 0)
            return null;

        if (urls.Count == 1)
            return urls[0];

        // Prioritize URLs that appear at the start of input
        var firstUrl = urls.FirstOrDefault(u => originalInput.TrimStart().StartsWith(u.Url, StringComparison.OrdinalIgnoreCase)
                                                || originalInput.TrimStart().StartsWith(u.NormalizedUrl, StringComparison.OrdinalIgnoreCase));
        if (firstUrl != null)
            return firstUrl;

        // Prioritize URLs with more specific paths
        var withPath = urls.Where(u => Uri.TryCreate(u.NormalizedUrl, UriKind.Absolute, out var uri) && uri.PathAndQuery.Length > 1)
                          .OrderByDescending(u => u.NormalizedUrl.Length)
                          .FirstOrDefault();
        if (withPath != null)
            return withPath;

        // Return the first valid URL
        return urls.FirstOrDefault(u => u.IsValid) ?? urls[0];
    }

    /// <summary>
    /// Extracts the user's natural language intent from input, removing the URL(s).
    /// For example: "https://example.com I want to watch prices" -> "I want to watch prices"
    /// </summary>
    public string ExtractUserIntent(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var result = input;

        // Remove all full URLs
        result = FullUrlRegex().Replace(result, " ");
        
        // Remove domain patterns that look like URLs
        result = DomainPatternRegex().Replace(result, match =>
        {
            // Only remove if it looks like a URL (has TLD)
            var value = match.Value;
            if (value.Contains('.') && !value.Contains(' '))
                return " ";
            return match.Value;
        });

        // Clean up extra whitespace
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ").Trim();

        return result;
    }

    private static string NormalizeUrl(string url)
    {
        // Add scheme if missing
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = $"https://{url}";
        }

        // Parse and normalize
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // Rebuild with lowercase host
            var builder = new UriBuilder(uri)
            {
                Host = uri.Host.ToLowerInvariant()
            };
            
            // Remove default ports
            if ((uri.Scheme == "http" && uri.Port == 80) ||
                (uri.Scheme == "https" && uri.Port == 443))
            {
                builder.Port = -1;
            }

            return builder.Uri.ToString();
        }

        return url;
    }

    private static bool IsValidUrl(string url)
    {
        if (!Uri.TryCreate(url.StartsWith("http") ? url : $"https://{url}", UriKind.Absolute, out var uri))
            return false;

        // Must be HTTP or HTTPS
        if (uri.Scheme != "http" && uri.Scheme != "https")
            return false;

        // Host must have a dot (not localhost-only unless explicitly localhost)
        if (!uri.Host.Contains('.') && uri.Host != "localhost")
            return false;

        return true;
    }

    private static string CleanTrailingPunctuation(string url)
    {
        // Remove trailing punctuation that's likely sentence-ending, not URL part
        while (url.Length > 0)
        {
            var last = url[^1];
            if (last == '.' || last == ',' || last == ';' || last == ':' || last == '!' || last == '?')
            {
                // But keep if it's part of a valid URL pattern
                if (last == '.' && url.Length > 1 && char.IsLetterOrDigit(url[^2]))
                {
                    // Could be end of TLD, check if what follows would be valid
                    break;
                }
                url = url[..^1];
            }
            else if (last == ')' && !url.Contains('('))
            {
                // Unmatched closing paren
                url = url[..^1];
            }
            else
            {
                break;
            }
        }
        return url;
    }

    private static string ExtractContext(string input, int matchIndex, int matchLength)
    {
        const int contextChars = 50;
        
        var start = Math.Max(0, matchIndex - contextChars);
        var end = Math.Min(input.Length, matchIndex + matchLength + contextChars);
        
        var before = input[start..matchIndex].Trim();
        var after = input[(matchIndex + matchLength)..end].Trim();
        
        // Get just the relevant surrounding words
        var beforeWords = before.Split(' ', StringSplitOptions.RemoveEmptyEntries).TakeLast(5);
        // Capture a bit more of the trailing intent so we don't lose key nouns like "events"
        var afterWords = after.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(10);
        
        return string.Join(" ", beforeWords.Concat(new[] { "[URL]" }).Concat(afterWords));
    }

    // Matches full URLs with http/https
    [GeneratedRegex(@"https?://[^\s<>""'\)\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex FullUrlRegex();

    // Matches domain patterns without scheme
    [GeneratedRegex(@"(?<![:/\w@])(www\.)?[a-zA-Z0-9][-a-zA-Z0-9]*\.[a-zA-Z]{2,}(?:\.[a-zA-Z]{2,})?(?:/[^\s<>""'\)\]]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex DomainPatternRegex();
}
