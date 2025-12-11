using System.Text.RegularExpressions;

namespace ChangeDetection.Core;

/// <summary>
/// Utility for normalizing tags to ensure consistency.
/// Tags are lowercased, trimmed, and deduplicated.
/// </summary>
public static partial class TagNormalizer
{
    /// <summary>
    /// Normalizes a single tag: lowercase, trim, collapse whitespace.
    /// </summary>
    /// <param name="tag">The tag to normalize.</param>
    /// <returns>The normalized tag, or null if the tag is empty/whitespace.</returns>
    public static string? Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        
        // Trim, lowercase, and collapse multiple spaces to single space
        var normalized = WhitespaceRegex().Replace(tag.Trim().ToLowerInvariant(), " ");
        
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
    
    /// <summary>
    /// Normalizes a list of tags: normalizes each, removes nulls/empties, deduplicates.
    /// </summary>
    /// <param name="tags">The tags to normalize.</param>
    /// <returns>A list of unique, normalized tags.</returns>
    public static List<string> NormalizeList(IEnumerable<string>? tags)
    {
        if (tags == null)
            return [];
        
        return tags
            .Select(Normalize)
            .Where(t => t != null)
            .Cast<string>()
            .Distinct()
            .OrderBy(t => t)
            .ToList();
    }
    
    /// <summary>
    /// Checks if a tag is valid (non-empty after normalization).
    /// </summary>
    public static bool IsValid(string? tag) => Normalize(tag) != null;
    
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
