using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Content;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Content;

/// <summary>
/// Unit tests for ProfileFilterRuleGenerator — generates deterministic FilterRules
/// from a structured analysis profile JSON.
/// </summary>
[Category("Unit")]
public class ProfileFilterRuleGeneratorTests : TestBase
{
    private readonly ProfileFilterRuleGenerator _sut = new();

    private const string FullProfile = """
        {
            "education": { "level": "MSc", "field": "molecular and cell biology" },
            "experience_years": "1-3",
            "techniques_strong": ["PCR", "qPCR", "cell culture", "ELISA", "flow cytometry"],
            "techniques_basic": ["CRISPR", "protein expression"],
            "techniques_none": ["organoid culture", "mass spectrometry", "NGS library prep"],
            "target_locations": ["Prague", "Copenhagen", "Lyngby", "Malmö"],
            "languages": { "Czech": "native", "English": "C1" },
            "salary_floor": { "prague_czk": 50000, "copenhagen_dkk": 30000 },
            "dealbreakers": ["SOTIO", "animal-heavy work"]
        }
        """;

    [Test]
    public async Task GenerateRules_WithFullProfile_GeneratesExpectedRuleCount()
    {
        var rules = _sut.GenerateRules(FullProfile);

        Log($"Generated {rules.Count} rules");
        foreach (var rule in rules)
            Log($"  - {rule.Name} (priority={rule.Priority}, conditions={rule.Conditions.Count}, actions={rule.Actions.Count})");

        // Should generate: 2 dealbreakers + 1 PhD rule + 3 techniques_none + 1 location = 7
        rules.Count.ShouldBeGreaterThanOrEqualTo(6);
        await Task.CompletedTask;
    }

    [Test]
    public async Task GenerateRules_DealbreakersCreateSuppressRules()
    {
        var rules = _sut.GenerateRules(FullProfile);

        var sotioRule = rules.FirstOrDefault(r => r.Name.Contains("SOTIO"));
        sotioRule.ShouldNotBeNull("Should have a rule for SOTIO dealbreaker");
        sotioRule.Conditions.ShouldHaveSingleItem();
        sotioRule.Conditions[0].FieldName.ShouldBe("company");
        sotioRule.Conditions[0].Operator.ShouldBe(FilterOperator.Contains);
        sotioRule.Conditions[0].Value.ShouldBe("SOTIO");
        sotioRule.Actions.ShouldContain(a => a.Type == FilterActionType.SuppressNotification);
        sotioRule.Actions.ShouldContain(a => a.Type == FilterActionType.AddTag
            && a.Parameters.GetValueOrDefault("tag") == "DEALBREAKER");
        sotioRule.StopProcessing.ShouldBeTrue();

        await Task.CompletedTask;
    }

    [Test]
    public async Task GenerateRules_MScProfile_GeneratesPhDDisqualifyRule()
    {
        var rules = _sut.GenerateRules(FullProfile);

        var phdRule = rules.FirstOrDefault(r => r.Name.Contains("PhD"));
        phdRule.ShouldNotBeNull("Should have a PhD disqualification rule for MSc candidate");
        phdRule.StopProcessing.ShouldBeTrue();
        phdRule.Logic.ShouldBe(FilterLogic.Or);
        phdRule.Conditions.Count.ShouldBeGreaterThanOrEqualTo(1);
        phdRule.Actions.ShouldContain(a => a.Type == FilterActionType.SuppressNotification);
        phdRule.Actions.ShouldContain(a => a.Type == FilterActionType.AddTag
            && a.Parameters.GetValueOrDefault("tag") == "DISQUALIFIED_PHD");

        await Task.CompletedTask;
    }

    [Test]
    public async Task GenerateRules_PhDProfile_NoPhDDisqualifyRule()
    {
        const string phdProfile = """
            {
                "education": { "level": "PhD", "field": "biochemistry" },
                "techniques_none": [],
                "target_locations": ["Boston"],
                "dealbreakers": []
            }
            """;

        var rules = _sut.GenerateRules(phdProfile);

        var phdRule = rules.FirstOrDefault(r => r.Name.Contains("PhD"));
        phdRule.ShouldBeNull("PhD holder should NOT have PhD disqualification rule");

        await Task.CompletedTask;
    }

    [Test]
    public async Task GenerateRules_TechniquesNone_CreateSkillGapRules()
    {
        var rules = _sut.GenerateRules(FullProfile);

        var skillGapRules = rules.Where(r => r.Name.Contains("Skill gap")).ToList();
        skillGapRules.Count.ShouldBe(3, "Should have 3 skill gap rules (organoid, mass spec, NGS)");

        var organoidRule = skillGapRules.First(r => r.Name.Contains("organoid"));
        organoidRule.Conditions.ShouldHaveSingleItem();
        organoidRule.Conditions[0].FieldName.ShouldBe("skills_required");
        organoidRule.Conditions[0].Operator.ShouldBe(FilterOperator.Contains);
        organoidRule.Actions.ShouldContain(a => a.Type == FilterActionType.AddTag
            && a.Parameters.GetValueOrDefault("tag") == "SKILL_GAP");
        organoidRule.Actions.ShouldContain(a => a.Type == FilterActionType.SetImportance);

        await Task.CompletedTask;
    }

    [Test]
    public async Task GenerateRules_TargetLocations_CreateLocationFilter()
    {
        var rules = _sut.GenerateRules(FullProfile);

        var locationRule = rules.FirstOrDefault(r => r.Name.Contains("Location"));
        locationRule.ShouldNotBeNull("Should have a location filter rule");
        locationRule.Logic.ShouldBe(FilterLogic.And);
        // All conditions should be negated — "if location does NOT contain ANY target"
        locationRule.Conditions.Count.ShouldBe(4, "Should have 4 negated location conditions");
        locationRule.Conditions.ShouldAllBe(c => c.Negate);
        locationRule.Actions.ShouldContain(a => a.Type == FilterActionType.SuppressNotification);
        locationRule.Actions.ShouldContain(a => a.Type == FilterActionType.AddTag
            && a.Parameters.GetValueOrDefault("tag") == "WRONG_LOCATION");

        await Task.CompletedTask;
    }

    [Test]
    public async Task GenerateRules_EmptyProfile_ReturnsEmptyList()
    {
        var rules = _sut.GenerateRules("{}");
        rules.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task GenerateRules_InvalidJson_ReturnsEmptyList()
    {
        var rules = _sut.GenerateRules("not valid json");
        rules.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task GenerateRules_PriorityDescending_HigherPriorityEvaluatedFirst()
    {
        var rules = _sut.GenerateRules(FullProfile);

        // Rules should have descending priority (higher numbers first)
        for (var i = 0; i < rules.Count - 1; i++)
        {
            rules[i].Priority.ShouldBeGreaterThanOrEqualTo(rules[i + 1].Priority,
                $"Rule '{rules[i].Name}' should have >= priority than '{rules[i + 1].Name}'");
        }

        await Task.CompletedTask;
    }
}
