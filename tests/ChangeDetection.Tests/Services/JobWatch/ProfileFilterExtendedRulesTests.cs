using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Content;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.JobWatch;

/// <summary>
/// Tests for the extended ProfileFilterRuleGenerator — salary, language, and experience rules.
/// </summary>
[Category("Unit")]
public class ProfileFilterExtendedRulesTests : TestBase
{
    private static readonly string FullCandidateProfile = """
        {
            "education": { "level": "MSc", "field": "molecular and cell biology" },
            "experience_years": "1-3",
            "techniques_strong": ["PCR", "cell culture", "ELISA"],
            "techniques_none": ["organoid culture", "mass spectrometry"],
            "target_locations": ["Prague", "Copenhagen"],
            "languages": ["Czech (native)", "English (C1)", "German (basic)"],
            "salary_floor": {
                "prague": "50,000 CZK/month",
                "copenhagen": "30,000 DKK/month"
            },
            "dealbreakers": ["SOTIO"]
        }
        """;

    private readonly ProfileFilterRuleGenerator _ruleGen = new();

    [Test]
    public async Task SalaryRules_AreGenerated_ForEachLocation()
    {
        var rules = _ruleGen.GenerateRules(FullCandidateProfile);

        var salaryRules = rules.Where(r => r.Name.StartsWith("Salary floor")).ToList();
        salaryRules.Count.ShouldBeGreaterThanOrEqualTo(2, "Should generate salary rules for prague and copenhagen");

        var pragueRule = salaryRules.FirstOrDefault(r => r.Name.Contains("prague"));
        pragueRule.ShouldNotBeNull("Should have a Prague salary rule");
        pragueRule.Actions.ShouldContain(a =>
            a.Type == FilterActionType.AddTag &&
            a.Parameters.GetValueOrDefault("tag") == "LOW_SALARY");

        // Salary rules should NOT suppress notifications — just tag and downgrade
        pragueRule.Actions.ShouldNotContain(a => a.Type == FilterActionType.SuppressNotification);
        await Task.CompletedTask;
    }

    [Test]
    public async Task LanguageRule_IsGenerated_WithCandidateLanguages()
    {
        var rules = _ruleGen.GenerateRules(FullCandidateProfile);

        var langRule = rules.FirstOrDefault(r => r.Name.Contains("Language"));
        langRule.ShouldNotBeNull("Should generate a language requirement filter");

        // Should have guard condition + negated conditions for each language
        langRule.Conditions.Count.ShouldBeGreaterThan(1);

        // Should tag as LANGUAGE_GAP, not suppress
        langRule.Actions.ShouldContain(a =>
            a.Type == FilterActionType.AddTag &&
            a.Parameters.GetValueOrDefault("tag") == "LANGUAGE_GAP");
        langRule.Actions.ShouldNotContain(a => a.Type == FilterActionType.SuppressNotification);
        await Task.CompletedTask;
    }

    [Test]
    public async Task LanguageRule_StripsProfileNotation()
    {
        var rules = _ruleGen.GenerateRules(FullCandidateProfile);
        var langRule = rules.First(r => r.Name.Contains("Language"));

        // "Czech (native)" should become just "Czech" in the condition
        var czechCondition = langRule.Conditions.FirstOrDefault(c =>
            c.Value?.Contains("Czech") == true);
        czechCondition.ShouldNotBeNull();
        czechCondition!.Value.ShouldNotContain("(native)");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExperienceRule_IsGenerated_WithThreshold()
    {
        var rules = _ruleGen.GenerateRules(FullCandidateProfile);

        var expRule = rules.FirstOrDefault(r => r.Name.Contains("Experience"));
        expRule.ShouldNotBeNull("Should generate an experience level filter");

        // For 1-3 years, threshold should be max(3+3, 3*2) = max(6, 6) = 6
        var thresholdCondition = expRule.Conditions.FirstOrDefault(c =>
            c.Operator == FilterOperator.GreaterThan);
        thresholdCondition.ShouldNotBeNull();
        thresholdCondition!.Value.ShouldBe("6");

        expRule.Actions.ShouldContain(a =>
            a.Type == FilterActionType.AddTag &&
            a.Parameters.GetValueOrDefault("tag") == "EXPERIENCE_GAP");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExistingRules_StillWork_WithNewRules()
    {
        // Verify backward compatibility — existing rules should still be generated
        var rules = _ruleGen.GenerateRules(FullCandidateProfile);

        rules.ShouldContain(r => r.Name.Contains("PhD"), "PhD disqualification should still exist");
        rules.ShouldContain(r => r.Name.Contains("SOTIO"), "Dealbreaker should still exist");
        rules.ShouldContain(r => r.Name.Contains("organoid"), "Skill gap should still exist");
        rules.ShouldContain(r => r.Name.Contains("Location"), "Location filter should still exist");
        await Task.CompletedTask;
    }

    [Test]
    public async Task MissingSalaryFloor_GeneratesNoSalaryRules()
    {
        var minimalProfile = """
            {
                "education": { "level": "MSc" },
                "techniques_strong": ["PCR"]
            }
            """;

        var rules = _ruleGen.GenerateRules(minimalProfile);
        rules.ShouldNotContain(r => r.Name.StartsWith("Salary floor"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task LanguagesAsObject_DoesNotCrash()
    {
        // The original profile has languages as object, not array — should handle gracefully
        var objectProfile = """
            {
                "education": { "level": "MSc" },
                "languages": { "Czech": "native", "English": "C1" }
            }
            """;

        var rules = _ruleGen.GenerateRules(objectProfile);
        // Languages-as-object won't match array parsing — no language rule generated, no crash
        rules.ShouldNotContain(r => r.Name.Contains("Language"));
        await Task.CompletedTask;
    }
}
