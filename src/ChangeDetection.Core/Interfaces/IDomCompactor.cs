namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Configuration options for DOM compaction.
/// </summary>
public record DomCompactorOptions
{
    /// <summary>
    /// Maximum number of characters for text content within elements.
    /// Text longer than this will be truncated with ellipsis.
    /// </summary>
    public int MaxTextLength { get; init; } = 50;

    /// <summary>
    /// Maximum number of classes to preserve per element.
    /// Utility/framework classes are filtered out first.
    /// </summary>
    public int MaxClassesPerElement { get; init; } = 2;

    /// <summary>
    /// Whether to remove empty wrapper divs (divs with single child and no meaningful attributes).
    /// </summary>
    public bool CollapseEmptyWrappers { get; init; } = true;

    /// <summary>
    /// Whether to filter out common utility/framework classes (Tailwind, Bootstrap).
    /// </summary>
    public bool FilterUtilityClasses { get; init; } = true;

    /// <summary>
    /// Whether to add data-node-id attributes for unique element targeting.
    /// </summary>
    public bool AddNodeIds { get; init; } = false;

    /// <summary>
    /// Maximum depth of the DOM tree to include. Elements deeper than this are summarized.
    /// 0 = no limit.
    /// </summary>
    public int MaxDepth { get; init; } = 0;

    /// <summary>
    /// Tags to completely remove (including their content).
    /// </summary>
    public HashSet<string> RemoveTags { get; init; } =
    [
        "script", "style", "noscript", "svg", "path", "iframe", "meta", "link",
        "head", "template", "slot", "picture", "source"
    ];

    /// <summary>
    /// Attributes to always preserve.
    /// </summary>
    public HashSet<string> PreserveAttributes { get; init; } =
    [
        "id", "class", "href", "src", "alt", "title", "type", "name", "value",
        "placeholder", "aria-label", "role", "disabled", "checked", "selected",
        "datetime", "content", "for", "target", "rel"
    ];

    /// <summary>
    /// Attribute prefixes to preserve (e.g., "data-" keeps all data attributes).
    /// </summary>
    public HashSet<string> PreserveAttributePrefixes { get; init; } = ["data-"];

    /// <summary>
    /// Default options optimized for selector generation.
    /// </summary>
    public static DomCompactorOptions Default => new();

    /// <summary>
    /// Aggressive compaction for very large DOMs.
    /// </summary>
    public static DomCompactorOptions Aggressive => new()
    {
        MaxTextLength = 30,
        MaxClassesPerElement = 1,
        CollapseEmptyWrappers = true,
        MaxDepth = 8
    };
}

/// <summary>
/// Result of DOM compaction.
/// </summary>
public record DomCompactionResult
{
    /// <summary>
    /// The compacted HTML string.
    /// </summary>
    public required string Html { get; init; }

    /// <summary>
    /// Original size in characters.
    /// </summary>
    public int OriginalSize { get; init; }

    /// <summary>
    /// Compacted size in characters.
    /// </summary>
    public int CompactedSize { get; init; }

    /// <summary>
    /// Compression ratio (compacted / original).
    /// </summary>
    public float CompressionRatio => OriginalSize > 0 ? (float)CompactedSize / OriginalSize : 1;

    /// <summary>
    /// Number of elements removed.
    /// </summary>
    public int ElementsRemoved { get; init; }

    /// <summary>
    /// Number of wrapper elements collapsed.
    /// </summary>
    public int WrappersCollapsed { get; init; }
}

/// <summary>
/// Compacts HTML DOM while preserving selector-relevant structure.
/// Unlike full linearization, this keeps tag names, IDs, meaningful classes,
/// and data attributes needed for CSS/XPath selector generation.
/// </summary>
public interface IDomCompactor
{
    /// <summary>
    /// Compacts the HTML DOM to reduce size while preserving selector structure.
    /// </summary>
    /// <param name="html">The original HTML string.</param>
    /// <param name="options">Compaction options. Uses defaults if null.</param>
    /// <returns>Compaction result with the compacted HTML and statistics.</returns>
    DomCompactionResult Compact(string html, DomCompactorOptions? options = null);

    /// <summary>
    /// Compacts HTML to fit within a target token budget.
    /// Automatically adjusts compaction aggressiveness.
    /// </summary>
    /// <param name="html">The original HTML string.</param>
    /// <param name="targetTokens">Target token count (estimated as chars/4).</param>
    /// <param name="maxIterations">Maximum iterations to reach target.</param>
    /// <returns>Compaction result, may exceed target if minimum viable compaction is larger.</returns>
    DomCompactionResult CompactToTokenBudget(string html, int targetTokens, int maxIterations = 5);
}
