namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Generates deterministic FilterRules from a structured analysis profile (JSON).
/// Used to auto-create rules for watches in a group that has an AnalysisProfileJson.
/// </summary>
public interface IProfileFilterRuleGenerator
{
    /// <summary>
    /// Generate filter rules from a structured analysis profile.
    /// Rules handle deterministic checks (education, salary, location, dealbreakers).
    /// Nuanced checks (skill equivalence, ambiguous requirements) are left to LLM scoring.
    /// </summary>
    /// <param name="analysisProfileJson">The structured profile JSON from WatchGroup.</param>
    /// <returns>List of filter rules to apply to watches in the group.</returns>
    List<Entities.FilterRule> GenerateRules(string analysisProfileJson);
}
