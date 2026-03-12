using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Content;

/// <summary>
/// Generates deterministic FilterRules from a structured analysis profile.
/// Handles binary checks that don't need LLM judgment.
/// </summary>
public class ProfileFilterRuleGenerator : IProfileFilterRuleGenerator
{
    public List<FilterRule> GenerateRules(string analysisProfileJson)
    {
        var rules = new List<FilterRule>();

        JsonElement profile;
        try
        {
            profile = JsonSerializer.Deserialize<JsonElement>(analysisProfileJson);
        }
        catch (JsonException)
        {
            return rules;
        }

        int priority = 100; // Higher priority = evaluated first

        // Dealbreaker companies → SuppressNotification
        if (profile.TryGetProperty("dealbreakers", out var dealbreakers) && dealbreakers.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dealbreakers.EnumerateArray())
            {
                var dealbreaker = item.GetString();
                if (string.IsNullOrWhiteSpace(dealbreaker)) continue;

                rules.Add(new FilterRule
                {
                    Name = $"Dealbreaker: {dealbreaker}",
                    Description = $"Auto-generated from profile dealbreaker: {dealbreaker}",
                    Priority = priority--,
                    StopProcessing = true,
                    Conditions =
                    [
                        new FilterCondition
                        {
                            FieldName = "company",
                            Operator = FilterOperator.Contains,
                            Value = dealbreaker
                        }
                    ],
                    Actions =
                    [
                        new FilterAction { Type = FilterActionType.SuppressNotification },
                        new FilterAction
                        {
                            Type = FilterActionType.AddTag,
                            Parameters = new Dictionary<string, string> { ["tag"] = "DEALBREAKER" }
                        },
                        new FilterAction
                        {
                            Type = FilterActionType.SetImportance,
                            Parameters = new Dictionary<string, string> { ["level"] = "Low" }
                        }
                    ]
                });
            }
        }

        // Education disqualifiers → PhD required = suppress
        if (profile.TryGetProperty("education", out var education) &&
            education.TryGetProperty("level", out var eduLevel))
        {
            var candidateLevel = eduLevel.GetString();
            if (candidateLevel is "MSc" or "BSc")
            {
                rules.Add(new FilterRule
                {
                    Name = "Disqualify: PhD required",
                    Description = $"Auto-generated: candidate has {candidateLevel}, PhD-required roles are disqualified",
                    Priority = priority--,
                    StopProcessing = true,
                    Logic = FilterLogic.Or,
                    Conditions =
                    [
                        new FilterCondition
                        {
                            FieldName = "education_required",
                            Operator = FilterOperator.Equals,
                            Value = "PhD"
                        },
                        new FilterCondition
                        {
                            FieldName = "requirements",
                            Operator = FilterOperator.Regex,
                            Value = @"(?i)\brequired?\b.*\bPh\.?D\.?\b|\bPh\.?D\.?\b.*\brequired?\b"
                        }
                    ],
                    Actions =
                    [
                        new FilterAction { Type = FilterActionType.SuppressNotification },
                        new FilterAction
                        {
                            Type = FilterActionType.AddTag,
                            Parameters = new Dictionary<string, string> { ["tag"] = "DISQUALIFIED_PHD" }
                        },
                        new FilterAction
                        {
                            Type = FilterActionType.SetImportance,
                            Parameters = new Dictionary<string, string> { ["level"] = "Low" }
                        }
                    ]
                });
            }
        }

        // Techniques in "none" list → suppress if required
        if (profile.TryGetProperty("techniques_none", out var techNone) && techNone.ValueKind == JsonValueKind.Array)
        {
            var noneSkills = new List<string>();
            foreach (var item in techNone.EnumerateArray())
            {
                var skill = item.GetString();
                if (!string.IsNullOrWhiteSpace(skill))
                    noneSkills.Add(skill);
            }

            if (noneSkills.Count > 0)
            {
                // Create one rule per absent technique
                foreach (var skill in noneSkills)
                {
                    rules.Add(new FilterRule
                    {
                        Name = $"Skill gap: {skill}",
                        Description = $"Auto-generated: candidate lacks {skill}. Downgrade if required.",
                        Priority = priority--,
                        Conditions =
                        [
                            new FilterCondition
                            {
                                FieldName = "skills_required",
                                Operator = FilterOperator.Contains,
                                Value = skill
                            }
                        ],
                        Actions =
                        [
                            new FilterAction
                            {
                                Type = FilterActionType.AddTag,
                                Parameters = new Dictionary<string, string> { ["tag"] = "SKILL_GAP" }
                            },
                            new FilterAction
                            {
                                Type = FilterActionType.SetImportance,
                                Parameters = new Dictionary<string, string> { ["level"] = "Low" }
                            }
                        ]
                    });
                }
            }
        }

        // Location filtering
        if (profile.TryGetProperty("target_locations", out var locations) && locations.ValueKind == JsonValueKind.Array)
        {
            var locationValues = new List<string>();
            foreach (var item in locations.EnumerateArray())
            {
                var loc = item.GetString();
                if (!string.IsNullOrWhiteSpace(loc))
                    locationValues.Add(loc);
            }

            if (locationValues.Count > 0)
            {
                // Create a rule that checks location is NOT in any target location
                // Using negated Contains conditions with OR logic
                rules.Add(new FilterRule
                {
                    Name = "Location filter",
                    Description = $"Auto-generated: only locations matching [{string.Join(", ", locationValues)}]",
                    Priority = priority--,
                    Logic = FilterLogic.And,
                    Conditions = locationValues.Select(loc => new FilterCondition
                    {
                        FieldName = "location",
                        Operator = FilterOperator.Contains,
                        Value = loc,
                        Negate = true // NONE of the target locations match → suppress
                    }).ToList(),
                    Actions =
                    [
                        new FilterAction { Type = FilterActionType.SuppressNotification },
                        new FilterAction
                        {
                            Type = FilterActionType.AddTag,
                            Parameters = new Dictionary<string, string> { ["tag"] = "WRONG_LOCATION" }
                        }
                    ]
                });
            }
        }

        return rules;
    }
}
