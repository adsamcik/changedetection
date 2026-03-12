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
            var options = new JsonDocumentOptions { MaxDepth = 10 };
            using var doc = JsonDocument.Parse(analysisProfileJson, options);
            profile = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return rules;
        }

        int priority = 100; // Higher priority = evaluated first

        // Dealbreaker companies/themes → SuppressNotification
        // Match against multiple fields: company, title, description, requirements
        if (profile.TryGetProperty("dealbreakers", out var dealbreakers) && dealbreakers.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dealbreakers.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var dealbreaker = item.GetString();
                if (string.IsNullOrWhiteSpace(dealbreaker)) continue;

                rules.Add(new FilterRule
                {
                    Name = $"Dealbreaker: {dealbreaker}",
                    Description = $"Auto-generated from profile dealbreaker: {dealbreaker}",
                    Priority = priority--,
                    StopProcessing = true,
                    Logic = FilterLogic.Or, // Match in ANY of these fields
                    Conditions =
                    [
                        new FilterCondition
                        {
                            FieldName = "company",
                            Operator = FilterOperator.Contains,
                            Value = dealbreaker
                        },
                        new FilterCondition
                        {
                            FieldName = "title",
                            Operator = FilterOperator.Contains,
                            Value = dealbreaker
                        },
                        new FilterCondition
                        {
                            FieldName = "requirements",
                            Operator = FilterOperator.Contains,
                            Value = dealbreaker
                        },
                        new FilterCondition
                        {
                            FieldName = "description",
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

        // Education disqualifiers — guard against malformed education values
        if (profile.TryGetProperty("education", out var education) &&
            education.ValueKind == JsonValueKind.Object &&
            education.TryGetProperty("level", out var eduLevel) &&
            eduLevel.ValueKind == JsonValueKind.String)
        {
            var candidateLevel = eduLevel.GetString();

            // PhD disqualification for MSc and BSc candidates
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

            // MSc disqualification for BSc candidates
            if (candidateLevel is "BSc")
            {
                rules.Add(new FilterRule
                {
                    Name = "Disqualify: MSc required",
                    Description = "Auto-generated: candidate has BSc, MSc-required roles are disqualified",
                    Priority = priority--,
                    StopProcessing = true,
                    Logic = FilterLogic.Or,
                    Conditions =
                    [
                        new FilterCondition
                        {
                            FieldName = "education_required",
                            Operator = FilterOperator.Equals,
                            Value = "MSc"
                        },
                        new FilterCondition
                        {
                            FieldName = "requirements",
                            Operator = FilterOperator.Regex,
                            Value = @"(?i)\brequired?\b.*\bM\.?Sc\.?\b|\bM\.?Sc\.?\b.*\brequired?\b"
                        }
                    ],
                    Actions =
                    [
                        new FilterAction { Type = FilterActionType.SuppressNotification },
                        new FilterAction
                        {
                            Type = FilterActionType.AddTag,
                            Parameters = new Dictionary<string, string> { ["tag"] = "DISQUALIFIED_MSC" }
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
                if (item.ValueKind != JsonValueKind.String) continue;
                var skill = item.GetString();
                if (!string.IsNullOrWhiteSpace(skill))
                    noneSkills.Add(skill);
            }

            if (noneSkills.Count > 0)
            {
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

        // Location filtering — only suppress when location IS present and doesn't match
        if (profile.TryGetProperty("target_locations", out var locations) && locations.ValueKind == JsonValueKind.Array)
        {
            var locationValues = new List<string>();
            foreach (var item in locations.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var loc = item.GetString();
                if (!string.IsNullOrWhiteSpace(loc))
                    locationValues.Add(loc);
            }

            if (locationValues.Count > 0)
            {
                // First condition: location field must be present and non-empty
                // Use Contains with empty string negated to check field exists
                var conditions = new List<FilterCondition>();
                conditions.AddRange(locationValues.Select(loc => new FilterCondition
                {
                    FieldName = "location",
                    Operator = FilterOperator.Contains,
                    Value = loc,
                    Negate = true // NONE of the target locations match → suppress
                }));

                // Add a guard condition: location must contain at least one character
                // (so missing/empty location fields don't trigger suppression)
                conditions.Insert(0, new FilterCondition
                {
                    FieldName = "location",
                    Operator = FilterOperator.Regex,
                    Value = ".+" // location must be non-empty for this rule to fire
                });

                rules.Add(new FilterRule
                {
                    Name = "Location filter",
                    Description = $"Auto-generated: only locations matching [{string.Join(", ", locationValues)}]. Missing location = pass.",
                    Priority = priority--,
                    Logic = FilterLogic.And,
                    Conditions = conditions,
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
