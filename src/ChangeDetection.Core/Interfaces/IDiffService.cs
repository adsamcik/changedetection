namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for generating diffs between content versions.
/// </summary>
public interface IDiffService
{
    /// <summary>
    /// Compares two versions of content and produces a diff result.
    /// </summary>
    DiffResult Compare(string oldContent, string newContent);
    
    /// <summary>
    /// Generates an HTML representation of the diff.
    /// </summary>
    string GenerateDiffHtml(DiffResult diff);
    
    /// <summary>
    /// Generates a text summary of the changes.
    /// </summary>
    string GenerateSummary(DiffResult diff);
}

/// <summary>
/// Result of a diff comparison.
/// </summary>
public class DiffResult
{
    public bool HasChanges { get; set; }
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public int LinesUnchanged { get; set; }
    public List<DiffLine> Lines { get; set; } = [];
}

/// <summary>
/// A single line in a diff.
/// </summary>
public class DiffLine
{
    public DiffLineType Type { get; set; }
    public string? Text { get; set; }
    public int? OldLineNumber { get; set; }
    public int? NewLineNumber { get; set; }
}

public enum DiffLineType
{
    Unchanged,
    Inserted,
    Deleted,
    Modified,
    Imaginary
}
