using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChangeDetection.Services.GroupWatch;

/// <summary>
/// A single outreach signal detected on a careers page.
/// </summary>
public record OutreachSignal(string Type, string Evidence, float Confidence);

/// <summary>
/// Assessment of whether a company's careers page is receptive to speculative/cold applications.
/// </summary>
public record OutreachAssessment(bool IsOutreachFriendly, List<OutreachSignal> Signals, float OverallScore);

/// <summary>
/// Detects outreach-friendly signals on careers pages using regex pattern matching.
/// Rules-first approach: no LLM calls, runs in &lt;100ms.
/// </summary>
/// <remarks>
/// Outreach-friendly companies with "General Application" pages or talent communities
/// are valuable for career planning — they can be exported to the career docs system
/// (e.g. pozice.tex outreach table) for speculative application tracking.
/// </remarks>
public class OutreachSignalDetector
{
    /// <summary>
    /// Hard positive patterns that indicate a company is open to speculative applications.
    /// Each tuple: (signal type, regex pattern).
    /// </summary>
    private static readonly (string Type, Regex Pattern)[] HardPositives =
    [
        ("GeneralApplication", new Regex(
            @"(?i)(general\s+application|open\s+application|speculative\s+application|spontan\w*\s+bewerbung)",
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(50))),
        ("TalentCommunity", new Regex(
            @"(?i)(talent\s+(community|pool|network)|join\s+our\s+talent|connect\s+with\s+us)",
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(50))),
        ("SendCV", new Regex(
            @"(?i)(send\s+(us\s+)?your\s+(cv|resume|application)|submit\s+your\s+(cv|resume))",
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(50))),
        ("AlwaysHiring", new Regex(
            @"(?i)(always\s+looking|always\s+interested|we('re|\s+are)\s+always)",
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(50))),
        ("NamedRecruiter", new Regex(
            @"(?i)(contact\s+(our\s+)?recruiter|reach\s+out\s+to|email\s+(us|our\s+hr|our\s+team))",
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(50))),
    ];

    /// <summary>
    /// Hard negative patterns. ANY match = not outreach friendly, regardless of positives.
    /// </summary>
    private static readonly Regex[] HardNegatives =
    [
        new Regex(
            @"(?i)(apply\s+only\s+to\s+posted|only\s+accept\s+applications\s+for\s+(listed|posted)|do\s+not\s+send\s+unsolicited)",
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(50)),
    ];

    /// <summary>
    /// Confidence weights by signal type (higher = stronger signal).
    /// </summary>
    private static readonly Dictionary<string, float> SignalWeights = new()
    {
        ["GeneralApplication"] = 0.95f,
        ["TalentCommunity"] = 0.85f,
        ["SendCV"] = 0.90f,
        ["AlwaysHiring"] = 0.80f,
        ["NamedRecruiter"] = 0.70f,
    };

    /// <summary>
    /// Analyzes page content for outreach-friendly signals.
    /// </summary>
    /// <param name="pageContent">The HTML or text content of the careers page.</param>
    /// <param name="pageTitle">Optional page title for additional context.</param>
    /// <returns>An assessment with detected signals and overall score.</returns>
    public OutreachAssessment Analyze(string? pageContent, string? pageTitle = null)
    {
        if (string.IsNullOrWhiteSpace(pageContent))
            return new OutreachAssessment(false, [], 0f);

        var textToScan = pageTitle is not null
            ? $"{pageTitle}\n{pageContent}"
            : pageContent;

        // Check hard negatives first — any match = not outreach friendly
        foreach (var negative in HardNegatives)
        {
            try
            {
                if (negative.IsMatch(textToScan))
                    return new OutreachAssessment(false, [], 0f);
            }
            catch (RegexMatchTimeoutException)
            {
                // Treat timeout as no match — don't block on pathological input
            }
        }

        // Scan for hard positives
        var signals = new List<OutreachSignal>();
        foreach (var (type, pattern) in HardPositives)
        {
            try
            {
                var match = pattern.Match(textToScan);
                if (match.Success)
                {
                    var evidence = ExtractEvidence(textToScan, match);
                    var confidence = SignalWeights.GetValueOrDefault(type, 0.5f);
                    signals.Add(new OutreachSignal(type, evidence, confidence));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Skip this pattern on timeout
            }
        }

        if (signals.Count == 0)
            return new OutreachAssessment(false, [], 0f);

        // Overall score: weighted average of signal confidences, scaled to 0-10
        var weightedSum = signals.Sum(s => s.Confidence);
        var maxPossible = HardPositives.Length * 1.0f;
        var overallScore = Math.Min(10f, (weightedSum / maxPossible) * 10f);

        // Boost score for multiple signal types (diversity bonus)
        if (signals.Count >= 3)
            overallScore = Math.Min(10f, overallScore * 1.2f);
        else if (signals.Count >= 2)
            overallScore = Math.Min(10f, overallScore * 1.1f);

        // Round to 1 decimal
        overallScore = MathF.Round(overallScore, 1);

        return new OutreachAssessment(true, signals, overallScore);
    }

    /// <summary>
    /// Extracts a short evidence snippet around the regex match for display.
    /// </summary>
    private static string ExtractEvidence(string text, Match match)
    {
        const int contextChars = 40;
        var start = Math.Max(0, match.Index - contextChars);
        var end = Math.Min(text.Length, match.Index + match.Length + contextChars);

        var snippet = text[start..end].Trim();

        // Clean up whitespace and newlines
        snippet = Regex.Replace(snippet, @"\s+", " ");

        if (start > 0) snippet = "…" + snippet;
        if (end < text.Length) snippet += "…";

        return snippet;
    }

    /// <summary>
    /// Serializes an assessment to JSON for storage on the watch entity.
    /// </summary>
    public static string Serialize(OutreachAssessment assessment) =>
        JsonSerializer.Serialize(assessment, SerializerOptions);

    /// <summary>
    /// Deserializes an assessment from JSON stored on the watch entity.
    /// </summary>
    public static OutreachAssessment? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<OutreachAssessment>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}
