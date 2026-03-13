using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Content;
using ChangeDetection.Services.JobWatch;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.JobWatch;

/// <summary>
/// Integration tests for Job Watch profile-based filtering scenarios.
/// Tests the complete flow: profile → filter rules → change evaluation → alert decisions.
/// </summary>
[Category("Unit")]
public class JobWatchFilterIntegrationTests : TestBase
{
    private static readonly string CandidateProfile = """
        {
            "education": { "level": "MSc", "field": "molecular and cell biology" },
            "experience_years": "1-3",
            "techniques_strong": ["PCR", "qPCR", "cell culture", "ELISA", "flow cytometry", "western blot"],
            "techniques_basic": ["CRISPR", "protein expression"],
            "techniques_none": ["organoid culture", "mass spectrometry", "NGS library prep", "animal models"],
            "target_locations": ["Prague", "Copenhagen", "Lyngby", "Bagsværd", "Måløv", "Hørsholm", "Gentofte", "Kvistgaard", "Malmö", "Lund"],
            "languages": { "Czech": "native", "English": "C1", "German": "basic" },
            "salary_floor": { "prague_czk": 50000, "copenhagen_dkk": 30000 },
            "dealbreakers": ["SOTIO", "animal-heavy work"],
            "preferences": ["variety", "autonomy", "intellectual challenge"]
        }
        """;

    private readonly ProfileFilterRuleGenerator _ruleGen = new();

    [Test]
    public async Task PhDRequired_Listing_IsDisqualified()
    {
        // A job listing that explicitly requires a PhD
        var rules = _ruleGen.GenerateRules(CandidateProfile);
        var phdRule = rules.First(r => r.Name.Contains("PhD"));

        // Simulate a listing with education_required = "PhD"
        var fields = new Dictionary<string, string?> { ["education_required"] = "PhD" };

        var conditionMet = EvaluateConditions(phdRule, fields);
        conditionMet.ShouldBeTrue("PhD required listing should trigger disqualification rule");

        phdRule.Actions.ShouldContain(a => a.Type == FilterActionType.SuppressNotification);
        phdRule.Actions.ShouldContain(a =>
            a.Type == FilterActionType.AddTag && a.Parameters["tag"] == "DISQUALIFIED_PHD");

        await Task.CompletedTask;
    }

    [Test]
    public async Task MScRequired_Listing_PassesEducationFilter()
    {
        var rules = _ruleGen.GenerateRules(CandidateProfile);
        var phdRule = rules.First(r => r.Name.Contains("PhD"));

        // MSc requirement should NOT trigger the PhD disqualification
        var fields = new Dictionary<string, string?> { ["education_required"] = "MSc" };
        var conditionMet = EvaluateConditions(phdRule, fields);
        conditionMet.ShouldBeFalse("MSc listing should NOT trigger PhD disqualification");

        await Task.CompletedTask;
    }

    [Test]
    public async Task OrganoidCulture_Required_IsSkillGap()
    {
        var rules = _ruleGen.GenerateRules(CandidateProfile);
        var organoidRule = rules.First(r => r.Name.Contains("organoid"));

        var fields = new Dictionary<string, string?> { ["skills_required"] = "organoid culture, cell culture" };
        var conditionMet = EvaluateConditions(organoidRule, fields);
        conditionMet.ShouldBeTrue("Listing requiring organoid culture should trigger skill gap");

        organoidRule.Actions.ShouldContain(a =>
            a.Type == FilterActionType.AddTag && a.Parameters["tag"] == "SKILL_GAP");

        await Task.CompletedTask;
    }

    [Test]
    public async Task PCR_Required_NoSkillGap()
    {
        var rules = _ruleGen.GenerateRules(CandidateProfile);

        // PCR is in techniques_strong, so there should be no skill gap rule for it
        var pcrRule = rules.FirstOrDefault(r => r.Name.Contains("PCR") && r.Name.Contains("Skill gap"));
        pcrRule.ShouldBeNull("PCR is in strong techniques, no skill gap rule should exist");

        await Task.CompletedTask;
    }

    [Test]
    public async Task SOTIO_Company_IsDealbreaker()
    {
        var rules = _ruleGen.GenerateRules(CandidateProfile);
        var sotioRule = rules.First(r => r.Name.Contains("SOTIO"));

        var fields = new Dictionary<string, string?> { ["company"] = "SOTIO a.s." };
        var conditionMet = EvaluateConditions(sotioRule, fields);
        conditionMet.ShouldBeTrue("SOTIO company should trigger dealbreaker");

        sotioRule.StopProcessing.ShouldBeTrue("Dealbreaker should stop further rule processing");

        await Task.CompletedTask;
    }

    [Test]
    public async Task WrongLocation_Berlin_IsSuppressed()
    {
        var rules = _ruleGen.GenerateRules(CandidateProfile);
        var locationRule = rules.First(r => r.Name.Contains("Location"));

        // Berlin is NOT in target locations
        var fields = new Dictionary<string, string?> { ["location"] = "Berlin, Germany" };
        var conditionMet = EvaluateConditions(locationRule, fields);
        conditionMet.ShouldBeTrue("Berlin should NOT match any target location (negated Contains)");

        locationRule.Actions.ShouldContain(a => a.Type == FilterActionType.SuppressNotification);
        locationRule.Actions.ShouldContain(a =>
            a.Type == FilterActionType.AddTag && a.Parameters["tag"] == "WRONG_LOCATION");

        await Task.CompletedTask;
    }

    [Test]
    public async Task TargetLocation_Copenhagen_PassesFilter()
    {
        var rules = _ruleGen.GenerateRules(CandidateProfile);
        var locationRule = rules.First(r => r.Name.Contains("Location"));

        // Copenhagen IS in target locations — at least one negated Contains should fail
        var fields = new Dictionary<string, string?> { ["location"] = "Copenhagen, Denmark" };
        var conditionMet = EvaluateConditions(locationRule, fields);
        conditionMet.ShouldBeFalse("Copenhagen should match target location, rule should NOT fire");

        await Task.CompletedTask;
    }

    [Test]
    public async Task TargetLocation_Lyngby_PassesFilter()
    {
        var rules = _ruleGen.GenerateRules(CandidateProfile);
        var locationRule = rules.First(r => r.Name.Contains("Location"));

        var fields = new Dictionary<string, string?> { ["location"] = "Lyngby, Denmark" };
        var conditionMet = EvaluateConditions(locationRule, fields);
        conditionMet.ShouldBeFalse("Lyngby is in target locations, rule should NOT fire");

        await Task.CompletedTask;
    }

    [Test]
    public async Task TargetLocation_Maloev_PassesFilter()
    {
        var rules = _ruleGen.GenerateRules(CandidateProfile);
        var locationRule = rules.First(r => r.Name.Contains("Location"));

        // Novo Nordisk uses suburb names like "Måløv, Capital Region of Denmark, DK"
        var fields = new Dictionary<string, string?> { ["location"] = "Måløv, Capital Region of Denmark, DK" };
        var conditionMet = EvaluateConditions(locationRule, fields);
        conditionMet.ShouldBeFalse("Måløv is a Copenhagen suburb in target locations, rule should NOT fire");

        await Task.CompletedTask;
    }

    [Test]
    public async Task TargetLocation_Hoersholm_PassesFilter()
    {
        var rules = _ruleGen.GenerateRules(CandidateProfile);
        var locationRule = rules.First(r => r.Name.Contains("Location"));

        var fields = new Dictionary<string, string?> { ["location"] = "Hørsholm, Capital Region of Denmark, DK" };
        var conditionMet = EvaluateConditions(locationRule, fields);
        conditionMet.ShouldBeFalse("Hørsholm is a Copenhagen suburb in target locations, rule should NOT fire");

        await Task.CompletedTask;
    }

    [Test]
    public async Task ObjectDiffResult_NewListing_DetectedAsAdded()
    {
        // Simulate what happens when a new listing appears in ObjectDiffResult
        var addedItem = new ExtractedObject
        {
            Fields = new Dictionary<string, string?>
            {
                ["title"] = "Research Assistant in Molecular Biology",
                ["company"] = "Novo Nordisk",
                ["location"] = "Bagsværd, Denmark",
                ["url"] = "https://careers.novonordisk.com/job/12345"
            },
            IdentityKey = "Research Assistant in Molecular Biology|Novo Nordisk",
            Index = 0
        };

        var diffResult = new ObjectDiffResult
        {
            AddedItems = [addedItem],
            RemovedItems = [],
            ModifiedItems = []
        };

        diffResult.HasChanges.ShouldBeTrue();
        diffResult.AddedItems.Count.ShouldBe(1);
        diffResult.AddedItems[0].Fields["title"].ShouldBe("Research Assistant in Molecular Biology");
        diffResult.AddedItems[0].Fields["location"].ShouldContain("Bagsværd");

        await Task.CompletedTask;
    }

    [Test]
    public async Task ObjectDiffResult_RemovedListing_DetectedAsRemoved()
    {
        var removedItem = new ExtractedObject
        {
            Fields = new Dictionary<string, string?>
            {
                ["title"] = "Lab Technician",
                ["company"] = "Lundbeck",
                ["location"] = "Copenhagen"
            },
            IdentityKey = "Lab Technician|Lundbeck",
            Index = 0
        };

        var diffResult = new ObjectDiffResult
        {
            AddedItems = [],
            RemovedItems = [removedItem],
            ModifiedItems = []
        };

        diffResult.HasChanges.ShouldBeTrue();
        diffResult.RemovedItems.Count.ShouldBe(1);

        await Task.CompletedTask;
    }

    [Test]
    public async Task MatchDimensions_JsonRoundtrip_WorksCorrectly()
    {
        var dimensions = new Dictionary<string, object>
        {
            ["education"] = new { score = 0.9, status = "PASS", reason = "MSc meets requirement" },
            ["skills"] = new { score = 0.8, status = "PASS", reason = "PCR, cell culture, ELISA all required — all present" },
            ["location"] = new { score = 1.0, status = "PASS", reason = "Copenhagen is a target location" },
            ["salary"] = new { score = 1.0, status = "UNKNOWN", reason = "Salary not stated in listing" },
            ["dealbreakers"] = new { score = 1.0, status = "PASS", reason = "No dealbreakers triggered" }
        };

        var json = JsonSerializer.Serialize(dimensions);
        var changeEvent = new ChangeEvent
        {
            WatchedSiteId = Guid.NewGuid(),
            MatchDimensionsJson = json
        };

        changeEvent.MatchDimensionsJson.ShouldNotBeNull();
        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(changeEvent.MatchDimensionsJson);
        parsed.ShouldNotBeNull();
        parsed.ShouldContainKey("education");
        parsed.ShouldContainKey("skills");
        parsed["education"].GetProperty("status").GetString().ShouldBe("PASS");

        await Task.CompletedTask;
    }

    [Test]
    public async Task DeadlineUrgency_ThreeDaysAway_ShouldBeDetectable()
    {
        // The LLM prompt checks for urgency — we verify the deadline field is extractable
        var listing = new ExtractedObject
        {
            Fields = new Dictionary<string, string?>
            {
                ["title"] = "Research Scientist",
                ["company"] = "UCPH",
                ["deadline"] = DateTime.UtcNow.AddDays(2).ToString("yyyy-MM-dd")
            },
            IdentityKey = "Research Scientist|UCPH"
        };

        var deadline = listing.Fields["deadline"];
        deadline.ShouldNotBeNull();

        // Verify the deadline parses to a date within 3 days
        if (DateTime.TryParse(deadline, out var deadlineDate))
        {
            var daysUntil = (deadlineDate - DateTime.UtcNow).TotalDays;
            daysUntil.ShouldBeLessThan(3, "Deadline should be within 3 days for urgency escalation");
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Simple filter condition evaluator for testing purposes.
    /// Mirrors the production FilterEvaluationService logic for Contains/Equals/Regex.
    /// </summary>
    private static bool EvaluateConditions(FilterRule rule, Dictionary<string, string?> fields)
    {
        var results = rule.Conditions.Select(c => EvaluateCondition(c, fields)).ToList();
        var match = rule.Logic == FilterLogic.And
            ? results.All(r => r)
            : results.Any(r => r);
        return match;
    }

    private static bool EvaluateCondition(FilterCondition condition, Dictionary<string, string?> fields)
    {
        if (!fields.TryGetValue(condition.FieldName, out var fieldValue))
            fieldValue = null;

        var result = condition.Operator switch
        {
            FilterOperator.Contains => fieldValue?.Contains(condition.Value ?? "", StringComparison.OrdinalIgnoreCase) == true,
            FilterOperator.Equals => string.Equals(fieldValue, condition.Value, StringComparison.OrdinalIgnoreCase),
            FilterOperator.Regex when condition.Value is not null =>
                System.Text.RegularExpressions.Regex.IsMatch(fieldValue ?? "", condition.Value, System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            FilterOperator.IsEmpty => string.IsNullOrEmpty(fieldValue),
            FilterOperator.IsNotEmpty => !string.IsNullOrEmpty(fieldValue),
            _ => false
        };

        return condition.Negate ? !result : result;
    }
}
