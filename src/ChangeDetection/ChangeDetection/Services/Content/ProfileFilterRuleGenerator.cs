using System.Text.Json;
using System.Text.RegularExpressions;
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
                        },
                        // Also check title — catches "PhD fellowship/position/student" listings
                        // from portals that only expose title on listing pages
                        new FilterCondition
                        {
                            FieldName = "title",
                            Operator = FilterOperator.Regex,
                            Value = @"(?i)\bPh\.?D\.?\b\s*(fellowship|position|student|stipend|scholarship|project|program)"
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
                    // For multi-word skills like "organoid culture", use regex to match
                    // all key words appearing in the text (handles "organoid cell culture",
                    // "organoid-based culture", etc.)
                    var words = skill.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Where(w => w.Length > 3) // Skip short words like "and", "for", "in"
                        .ToList();

                    // Build a regex that matches all significant words in any order
                    var regexPattern = words.Count > 1
                        ? $"(?i)(?=.*\\b{string.Join(")(?=.*\\b", words.Select(System.Text.RegularExpressions.Regex.Escape))})"
                        : $"(?i)\\b{System.Text.RegularExpressions.Regex.Escape(skill)}\\b";

                    rules.Add(new FilterRule
                    {
                        Name = $"Skill gap: {skill}",
                        Description = $"Auto-generated: candidate lacks {skill}. Downgrade if required in skills or title.",
                        Priority = priority--,
                        Logic = FilterLogic.Or, // Match in ANY of these fields
                        Conditions =
                        [
                            new FilterCondition
                            {
                                FieldName = "skills_required",
                                Operator = FilterOperator.Contains,
                                Value = skill
                            },
                            // Regex-based title check handles multi-word skills with intervening words
                            // e.g., "organoid culture" matches "Organoid cell culture specialist"
                            new FilterCondition
                            {
                                FieldName = "title",
                                Operator = FilterOperator.Regex,
                                Value = regexPattern
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

        // Location filtering — only suppress when location field clearly contains
        // a geographic location that doesn't match targets.
        // Guard: if location looks like a department/institution name (contains
        // "Department", "Institut", "Faculty", etc.), don't suppress — it's not a city.
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
                var conditions = new List<FilterCondition>();

                // Guard 1: location field must be non-empty
                conditions.Add(new FilterCondition
                {
                    FieldName = "location",
                    Operator = FilterOperator.Regex,
                    Value = ".+"
                });

                // Guard 2: location must NOT look like a department/institution name.
                // If it does, this is a portal that puts org units in the location column,
                // and we should NOT use it for geographic filtering (would cause false negatives).
                conditions.Add(new FilterCondition
                {
                    FieldName = "location",
                    Operator = FilterOperator.Regex,
                    Value = @"(?i)\b(Departments?|Institu\w+|Faculty|Centers?|Centres?|Laborator\w+|Labs?|Sections?|Divisions?|Schools?|Museums?|Clinics?|Groups?|Units?)\b",
                    Negate = true // must NOT match — if it does, location is an org name, skip filter
                });

                // Then: none of the target locations match → suppress
                // Use word-boundary regex to prevent substring false matches
                // (e.g., "Lund" matching "Kalundborg" via plain Contains)
                conditions.AddRange(locationValues.Select(loc => new FilterCondition
                {
                    FieldName = "location",
                    Operator = FilterOperator.Regex,
                    Value = $@"(?i)\b{Regex.Escape(loc)}\b",
                    Negate = true
                }));

                rules.Add(new FilterRule
                {
                    Name = "Location filter",
                    Description = $"Auto-generated: only geographic locations matching [{string.Join(", ", locationValues)}]. Department/institution names and missing locations = pass.",
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

        // Salary floor filtering — flag when salary is stated and below floor
        if (profile.TryGetProperty("salary_floor", out var salaryFloor) && salaryFloor.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in salaryFloor.EnumerateObject())
            {
                // Skip non-numeric entries like "note"
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                var floorStr = prop.Value.GetString();
                if (string.IsNullOrWhiteSpace(floorStr)) continue;

                // Extract numeric value from strings like "50,000 CZK/month" or "~30,000 DKK/month"
                var numericStr = new string(floorStr.Where(c => char.IsDigit(c)).ToArray());
                if (!double.TryParse(numericStr, out var floorValue) || floorValue <= 0) continue;

                var locationHint = prop.Name; // e.g., "prague", "copenhagen"

                rules.Add(new FilterRule
                {
                    Name = $"Salary floor: {locationHint}",
                    Description = $"Auto-generated: salary below {floorStr} for {locationHint} roles. Tags but does not suppress — review recommended.",
                    Priority = priority--,
                    Logic = FilterLogic.And,
                    Conditions =
                    [
                        // Salary field must be present
                        new FilterCondition
                        {
                            FieldName = "salary",
                            Operator = FilterOperator.Regex,
                            Value = @"\d" // Must contain at least one digit
                        },
                        // Salary below floor
                        new FilterCondition
                        {
                            FieldName = "salary",
                            Operator = FilterOperator.LessThan,
                            Value = floorValue.ToString("F0")
                        }
                    ],
                    Actions =
                    [
                        new FilterAction
                        {
                            Type = FilterActionType.AddTag,
                            Parameters = new Dictionary<string, string> { ["tag"] = "LOW_SALARY" }
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

        // Language filtering — flag when required language is not in candidate's languages
        if (profile.TryGetProperty("languages", out var languages) && languages.ValueKind == JsonValueKind.Array)
        {
            var candidateLanguages = new List<string>();
            foreach (var item in languages.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var lang = item.GetString();
                if (!string.IsNullOrWhiteSpace(lang))
                    candidateLanguages.Add(lang);
            }

            if (candidateLanguages.Count > 0)
            {
                // Build negated conditions: suppress when language_required is present
                // AND none of the candidate's languages match
                var langConditions = new List<FilterCondition>
                {
                    // Guard: language_required field must exist and be non-empty
                    new()
                    {
                        FieldName = "language_required",
                        Operator = FilterOperator.Regex,
                        Value = ".+"
                    }
                };

                // Extract just the language names (strip proficiency notes like "(native)", "(C1)")
                langConditions.AddRange(candidateLanguages.Select(lang =>
                {
                    var langName = lang.Split('(')[0].Trim();
                    return new FilterCondition
                    {
                        FieldName = "language_required",
                        Operator = FilterOperator.Contains,
                        Value = langName,
                        Negate = true // NONE of the candidate languages match → flag
                    };
                }));

                rules.Add(new FilterRule
                {
                    Name = "Language requirement filter",
                    Description = $"Auto-generated: candidate speaks [{string.Join(", ", candidateLanguages)}]. Flag if required language doesn't match.",
                    Priority = priority--,
                    Logic = FilterLogic.And,
                    Conditions = langConditions,
                    Actions =
                    [
                        new FilterAction
                        {
                            Type = FilterActionType.AddTag,
                            Parameters = new Dictionary<string, string> { ["tag"] = "LANGUAGE_GAP" }
                        },
                        new FilterAction
                        {
                            Type = FilterActionType.SetImportance,
                            Parameters = new Dictionary<string, string> { ["level"] = "Medium" }
                        }
                    ]
                });
            }
        }

        // Experience level filtering — flag when experience requirement significantly exceeds candidate's
        if (profile.TryGetProperty("experience_years", out var expYears) && expYears.ValueKind == JsonValueKind.String)
        {
            var expStr = expYears.GetString();
            if (!string.IsNullOrWhiteSpace(expStr))
            {
                // Parse the upper bound of experience range (e.g., "1-3" → 3)
                var parts = expStr.Split('-', '–');
                var maxYears = parts.Length > 1
                    ? (int.TryParse(parts[^1].Trim(), out var upper) ? upper : 0)
                    : (int.TryParse(parts[0].Trim(), out var single) ? single : 0);

                if (maxYears > 0)
                {
                    // Flag roles requiring significantly more experience (> 2x candidate max)
                    var threshold = Math.Max(maxYears + 3, maxYears * 2);
                    rules.Add(new FilterRule
                    {
                        Name = "Experience level filter",
                        Description = $"Auto-generated: candidate has {expStr} years experience. Flag roles requiring >{threshold} years.",
                        Priority = priority--,
                        Logic = FilterLogic.And,
                        Conditions =
                        [
                            new FilterCondition
                            {
                                FieldName = "experience_years_required",
                                Operator = FilterOperator.Regex,
                                Value = @"\d" // Must contain a digit
                            },
                            new FilterCondition
                            {
                                FieldName = "experience_years_required",
                                Operator = FilterOperator.GreaterThan,
                                Value = threshold.ToString()
                            }
                        ],
                        Actions =
                        [
                            new FilterAction
                            {
                                Type = FilterActionType.AddTag,
                                Parameters = new Dictionary<string, string> { ["tag"] = "EXPERIENCE_GAP" }
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

        return rules;
    }
}
