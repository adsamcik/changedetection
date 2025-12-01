using System.Text;
using ChangeDetection.Core.Interfaces;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace ChangeDetection.Services.Content;

/// <summary>
/// Service for generating diffs between content versions.
/// </summary>
public class DiffService : IDiffService
{
    public DiffResult Compare(string oldContent, string newContent)
    {
        var differ = new Differ();
        var builder = new InlineDiffBuilder(differ);
        var diff = builder.BuildDiffModel(oldContent, newContent);

        var result = new DiffResult
        {
            HasChanges = diff.HasDifferences,
            Lines = []
        };

        int oldLineNum = 0;
        int newLineNum = 0;

        foreach (var line in diff.Lines)
        {
            var diffLine = new DiffLine
            {
                Text = line.Text,
                Type = MapChangeType(line.Type)
            };

            switch (line.Type)
            {
                case ChangeType.Unchanged:
                    oldLineNum++;
                    newLineNum++;
                    diffLine.OldLineNumber = oldLineNum;
                    diffLine.NewLineNumber = newLineNum;
                    result.LinesUnchanged++;
                    break;
                    
                case ChangeType.Inserted:
                    newLineNum++;
                    diffLine.NewLineNumber = newLineNum;
                    result.LinesAdded++;
                    break;
                    
                case ChangeType.Deleted:
                    oldLineNum++;
                    diffLine.OldLineNumber = oldLineNum;
                    result.LinesRemoved++;
                    break;
                    
                case ChangeType.Modified:
                    oldLineNum++;
                    newLineNum++;
                    diffLine.OldLineNumber = oldLineNum;
                    diffLine.NewLineNumber = newLineNum;
                    result.LinesAdded++;
                    result.LinesRemoved++;
                    break;
                    
                case ChangeType.Imaginary:
                    diffLine.Type = DiffLineType.Imaginary;
                    break;
            }

            result.Lines.Add(diffLine);
        }

        return result;
    }

    public string GenerateDiffHtml(DiffResult diff)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<div class=\"diff-container\">");

        foreach (var line in diff.Lines)
        {
            var cssClass = line.Type switch
            {
                DiffLineType.Inserted => "diff-added",
                DiffLineType.Deleted => "diff-removed",
                DiffLineType.Modified => "diff-modified",
                _ => "diff-unchanged"
            };

            var prefix = line.Type switch
            {
                DiffLineType.Inserted => "+",
                DiffLineType.Deleted => "-",
                DiffLineType.Modified => "~",
                _ => " "
            };

            var lineNumbers = "";
            if (line.OldLineNumber.HasValue || line.NewLineNumber.HasValue)
            {
                var oldNum = line.OldLineNumber?.ToString() ?? "";
                var newNum = line.NewLineNumber?.ToString() ?? "";
                lineNumbers = $"<span class=\"line-numbers\">{oldNum,4} {newNum,4}</span>";
            }

            var escapedText = System.Net.WebUtility.HtmlEncode(line.Text ?? "");
            sb.AppendLine($"<div class=\"diff-line {cssClass}\">{lineNumbers}<span class=\"prefix\">{prefix}</span><span class=\"content\">{escapedText}</span></div>");
        }

        sb.AppendLine("</div>");
        return sb.ToString();
    }

    public string GenerateSummary(DiffResult diff)
    {
        if (!diff.HasChanges)
        {
            return "No changes detected.";
        }

        var parts = new List<string>();
        
        if (diff.LinesAdded > 0)
        {
            parts.Add($"{diff.LinesAdded} line{(diff.LinesAdded == 1 ? "" : "s")} added");
        }
        
        if (diff.LinesRemoved > 0)
        {
            parts.Add($"{diff.LinesRemoved} line{(diff.LinesRemoved == 1 ? "" : "s")} removed");
        }

        return string.Join(", ", parts) + ".";
    }

    private static DiffLineType MapChangeType(ChangeType type)
    {
        return type switch
        {
            ChangeType.Unchanged => DiffLineType.Unchanged,
            ChangeType.Inserted => DiffLineType.Inserted,
            ChangeType.Deleted => DiffLineType.Deleted,
            ChangeType.Modified => DiffLineType.Modified,
            ChangeType.Imaginary => DiffLineType.Imaginary,
            _ => DiffLineType.Unchanged
        };
    }
}
