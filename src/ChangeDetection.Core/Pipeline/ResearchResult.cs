namespace ChangeDetection.Core.Pipeline;

public sealed record ResearchCandidate(
    string Url,
    string Reasoning,
    string? Title = null,
    string? Source = null);

public sealed record ResearchResult
{
    public List<ResearchCandidate> Candidates { get; init; } = [];
    public List<string> GeneratedPrompts { get; init; } = [];
    public string Summary { get; init; } = string.Empty;
}
