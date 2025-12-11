using System.Security.Cryptography;
using System.Text;

namespace ChangeDetection.Core;

/// <summary>
/// Generates deterministic colors for tags based on their name.
/// Users can override these colors, but this provides sensible defaults.
/// </summary>
public static class TagColorGenerator
{
    /// <summary>
    /// Predefined color palette for tags.
    /// These are carefully selected to be visually distinct and accessible.
    /// </summary>
    private static readonly string[] ColorPalette =
    [
        "#3B82F6", // Blue
        "#10B981", // Emerald
        "#F59E0B", // Amber
        "#EF4444", // Red
        "#8B5CF6", // Violet
        "#EC4899", // Pink
        "#06B6D4", // Cyan
        "#84CC16", // Lime
        "#F97316", // Orange
        "#6366F1", // Indigo
        "#14B8A6", // Teal
        "#A855F7", // Purple
        "#22C55E", // Green
        "#EAB308", // Yellow
        "#0EA5E9", // Sky
        "#D946EF", // Fuchsia
    ];
    
    /// <summary>
    /// Generates a deterministic color for a tag based on its name.
    /// The same tag name will always produce the same color.
    /// </summary>
    /// <param name="tagName">The normalized tag name.</param>
    /// <returns>A hex color code (e.g., "#3B82F6").</returns>
    public static string GetColor(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
            return ColorPalette[0];
        
        // Use a simple hash to pick a color from the palette
        var hash = GetStableHash(tagName.ToLowerInvariant());
        var index = Math.Abs(hash) % ColorPalette.Length;
        
        return ColorPalette[index];
    }
    
    /// <summary>
    /// Gets the color for a tag, considering user overrides.
    /// </summary>
    /// <param name="tagName">The normalized tag name.</param>
    /// <param name="tagColors">Dictionary of user-overridden tag colors.</param>
    /// <returns>The user-specified color if available, otherwise the auto-generated color.</returns>
    public static string GetColor(string tagName, IDictionary<string, string>? tagColors)
    {
        if (tagColors != null && tagColors.TryGetValue(tagName, out var userColor))
            return userColor;
        
        return GetColor(tagName);
    }
    
    /// <summary>
    /// Generates a stable hash for a string that won't change between runs.
    /// </summary>
    private static int GetStableHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = MD5.HashData(bytes);
        return BitConverter.ToInt32(hashBytes, 0);
    }
    
    /// <summary>
    /// Gets all available palette colors.
    /// </summary>
    public static IReadOnlyList<string> GetPalette() => ColorPalette;
}
