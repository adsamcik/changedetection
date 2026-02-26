using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.Search;

/// <summary>
/// Compares search results across multiple providers to detect SERP manipulation.
/// Flags results that appear in only one provider or have wildly different rankings.
/// </summary>
public class AdversarialSearchAnalyzer(ILogger<AdversarialSearchAnalyzer> logger)
{
    /// <summary>
    /// Analyzes multi-provider results for ranking anomalies and manipulation signals.
    /// </summary>
    public AdversarialAnalysisResult Analyze(MultiProviderResultSet multiResults)
    {
        if (multiResults.ProviderResults.Count < 2)
        {
            return new AdversarialAnalysisResult
            {
                Query = multiResults.Query,
                ProviderCount = multiResults.ProviderResults.Count,
                Anomalies = [],
                OverallTrustScore = 1.0f,
                Assessment = "Cannot perform adversarial analysis with fewer than 2 providers."
            };
        }

        var anomalies = new List<SearchAnomaly>();

        // Build per-URL position map across providers
        var urlPositions = BuildPositionMap(multiResults);

        foreach (var (url, positions) in urlPositions)
        {
            var providerCount = positions.Count;
            var totalProviders = multiResults.ProviderResults.Count;

            // Anomaly 1: Result appears in only one provider (exclusive result)
            if (providerCount == 1 && totalProviders >= 2)
            {
                var entry = positions.First();
                if (entry.Value <= 3) // Only flag if it's a top-3 result in that provider
                {
                    anomalies.Add(new SearchAnomaly
                    {
                        Url = url,
                        Type = AnomalyType.ExclusiveResult,
                        Severity = entry.Value == 1 ? AnomalySeverity.High : AnomalySeverity.Medium,
                        Description = $"Top-{entry.Value} result in {entry.Key} but absent from {totalProviders - 1} other provider(s)",
                        AffectedProviders = [entry.Key]
                    });
                }
            }

            // Anomaly 2: Wildly different rankings across providers
            if (providerCount >= 2)
            {
                var positionValues = positions.Values.ToList();
                var maxDiff = positionValues.Max() - positionValues.Min();
                if (maxDiff >= 10)
                {
                    anomalies.Add(new SearchAnomaly
                    {
                        Url = url,
                        Type = AnomalyType.RankingDiscrepancy,
                        Severity = maxDiff >= 20 ? AnomalySeverity.High : AnomalySeverity.Medium,
                        Description = $"Position varies by {maxDiff} across providers (range: {positionValues.Min()}-{positionValues.Max()})",
                        AffectedProviders = positions.Keys.ToList()
                    });
                }
            }
        }

        // Calculate trust score
        var totalResults = urlPositions.Count;
        var anomalyWeight = anomalies.Sum(a => a.Severity == AnomalySeverity.High ? 2.0f : 1.0f);
        var trustScore = totalResults > 0
            ? Math.Clamp(1.0f - (anomalyWeight / totalResults * 0.3f), 0f, 1f)
            : 1.0f;

        logger.LogDebug("Adversarial analysis for '{Query}': {AnomalyCount} anomalies, trust={Trust:F2}",
            multiResults.Query, anomalies.Count, trustScore);

        return new AdversarialAnalysisResult
        {
            Query = multiResults.Query,
            ProviderCount = multiResults.ProviderResults.Count,
            Anomalies = anomalies,
            OverallTrustScore = trustScore,
            Assessment = BuildAssessment(anomalies, trustScore)
        };
    }

    internal static Dictionary<string, Dictionary<string, int>> BuildPositionMap(MultiProviderResultSet multiResults)
    {
        var map = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var providerResult in multiResults.ProviderResults)
        {
            if (!providerResult.IsSuccess) continue;

            foreach (var result in providerResult.Results)
            {
                if (!map.TryGetValue(result.Url, out var positions))
                {
                    positions = new Dictionary<string, int>();
                    map[result.Url] = positions;
                }
                positions[providerResult.ProviderId] = result.Position;
            }
        }

        return map;
    }

    private static string BuildAssessment(List<SearchAnomaly> anomalies, float trustScore)
    {
        if (anomalies.Count == 0)
            return "No manipulation signals detected. Results are consistent across providers.";

        var highCount = anomalies.Count(a => a.Severity == AnomalySeverity.High);
        if (highCount > 0)
            return $"⚠️ {highCount} high-severity anomaly(ies) detected. Review flagged results for possible SERP manipulation.";

        return $"{anomalies.Count} minor ranking discrepancy(ies) detected. Results are generally consistent.";
    }
}

/// <summary>Result of adversarial cross-provider analysis.</summary>
public record AdversarialAnalysisResult
{
    public required string Query { get; init; }
    public int ProviderCount { get; init; }
    public required IReadOnlyList<SearchAnomaly> Anomalies { get; init; }
    public float OverallTrustScore { get; init; }
    public required string Assessment { get; init; }
    public bool HasAnomalies => Anomalies.Count > 0;
}

/// <summary>A detected search result anomaly.</summary>
public record SearchAnomaly
{
    public required string Url { get; init; }
    public required AnomalyType Type { get; init; }
    public required AnomalySeverity Severity { get; init; }
    public required string Description { get; init; }
    public List<string> AffectedProviders { get; init; } = [];
}

public enum AnomalyType
{
    /// <summary>Result appears in only one provider (possible manipulation).</summary>
    ExclusiveResult,

    /// <summary>Same URL ranked very differently across providers.</summary>
    RankingDiscrepancy
}

public enum AnomalySeverity
{
    Low,
    Medium,
    High
}
