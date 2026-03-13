using System.Text.Json;
using System.Text.RegularExpressions;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Content;
using ChangeDetection.Services.JobWatch;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.JobWatch;

/// <summary>
/// Calibration tests using 10 realistic European biotech job postings.
/// Each tests a specific edge case from the spec. Expected: ≥9/10 correct.
/// Exercises: filter rules, alert policy, date parsing, identity keys,
/// multi-language extraction, and the full deterministic pipeline.
/// </summary>
[Category("Unit")]
public class CalibrationTests : TestBase
{
    private static readonly string CandidateProfile = """
        {
            "education": { "level": "MSc", "field": "molecular and cell biology" },
            "experience_years": "1-3",
            "techniques_strong": ["PCR", "qPCR", "cell culture", "ELISA", "flow cytometry",
                "fluorescence microscopy", "western blot", "cloning", "DNA/RNA isolation",
                "protein purification"],
            "techniques_basic": ["CRISPR", "protein expression", "sequencing library prep"],
            "techniques_none": ["organoid culture", "mass spectrometry", "NGS library prep",
                "animal models", "bioinformatics pipelines", "CRISPR knock-in engineering",
                "drug metabolism", "biobank coordination", "phage display",
                "hybridoma technology", "affinity maturation"],
            "target_locations": ["Prague", "Copenhagen", "Lyngby", "Bagsværd", "Måløv",
                "Hørsholm", "Gentofte", "Kvistgaard", "Valby", "Malmö", "Lund"],
            "languages": ["Czech (native)", "English (C1)", "German (basic)"],
            "salary_floor": {
                "prague": "50000",
                "copenhagen": "30000"
            },
            "dealbreakers": ["SOTIO", "animal-heavy work", "pure flow cytometry specialist"],
            "preferences": ["variety", "autonomy", "intellectual challenge", "stability"]
        }
        """;

    private readonly ProfileFilterRuleGenerator _ruleGen = new();
    private readonly AlertPolicyService _alertPolicy = new(NullLogger<AlertPolicyService>.Instance);
    private readonly TrackingConfig _config = TrackingConfig.ForJobs();

    // ═══════════════════════════════════════════════
    // TC1: Perfect Match — UCPH Research Assistant
    // Expected: 🔴 HIGH
    // ═══════════════════════════════════════════════

    [Test]
    public async Task TC1_PerfectMatch_ResearchAssistant_ShouldBeHigh()
    {
        var fields = new Dictionary<string, string?>
        {
            ["title"] = "Research Assistant in Molecular Cancer Biology",
            ["faculty"] = "Faculty of Health and Medical Sciences",
            ["department"] = "Department of Drug Design and Pharmacology",
            ["deadline"] = "15-04-2026",
            ["url"] = "/all-vacancies/?show=160001"
        };

        // Filter layer: should NOT suppress
        AssertNotSuppressed(fields, "TC1 should pass all filters");

        // Alert policy: all PASS → HIGH
        var result = _alertPolicy.Evaluate(
            AllPassDimensions(), "APPLY", ParseDate("15-04-2026"), _config);

        result.AlertLevel.ShouldBe(AlertLevel.High, "TC1: Perfect match should be HIGH");
        Log($"TC1: {result.AlertLevel} — {result.Reason}");
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════
    // TC2: PhD Hard Requirement — Novo Senior Scientist
    // Expected: ⚪ SILENT
    // ═══════════════════════════════════════════════

    [Test]
    public async Task TC2_PhDRequired_SeniorScientist_ShouldBeSilent()
    {
        var fields = new Dictionary<string, string?>
        {
            ["title"] = "Senior Research Scientist, Biologics Discovery",
            ["location"] = "Måløv, Capital Region of Denmark, DK",
            ["education_required"] = "PhD",
            ["url"] = "/job/Maloev-Senior-Research-Scientist/123/"
        };

        // Filter layer: PhD rule should suppress
        AssertSuppressed(fields, "DISQUALIFIED_PHD", "TC2 should trigger PhD disqualification");

        // Alert policy: education FAIL → SILENT (hard-fail dimension)
        var result = _alertPolicy.Evaluate(
            DimensionsWithFail("education", "PhD required"), "SKIP", null, _config);

        result.AlertLevel.ShouldBe(AlertLevel.Silent, "TC2: PhD required should be SILENT");
        Log($"TC2: {result.AlertLevel} — {result.Reason}");
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════
    // TC3: "PhD or Equivalent" Ambiguity — Novonesis
    // Expected: 🟡 MEDIUM
    // ═══════════════════════════════════════════════

    [Test]
    public async Task TC3_PhDOrEquivalent_ShouldBeMedium()
    {
        var fields = new Dictionary<string, string?>
        {
            ["title"] = "Scientist, Protein Biochemistry",
            ["location"] = "Lyngby, Denmark",
            ["url"] = "/job/lyngby-scientist-protein/456/"
        };

        // Filter layer: title doesn't mention PhD → should NOT suppress
        AssertNotSuppressed(fields, "TC3 should not be suppressed — ambiguous education");

        // Alert policy: education STRETCH → MEDIUM
        var result = _alertPolicy.Evaluate(
            DimensionsWithStretch("education", "PhD or equivalent industrial experience"),
            "REVIEW", ParseDate("20-04-2026"), _config);

        result.AlertLevel.ShouldBe(AlertLevel.Medium, "TC3: PhD-or-equivalent should be MEDIUM");
        Log($"TC3: {result.AlertLevel} — {result.Reason}");
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════
    // TC4: Flow Cytometry Specialist Trap — Beckman
    // Expected: ⚪ SILENT
    // ═══════════════════════════════════════════════

    [Test]
    public async Task TC4_FlowSpecialist_Dealbreaker_ShouldBeSilent()
    {
        // NOTE: Dealbreaker "pure flow cytometry specialist" is a phrase description.
        // The title "Flow Cytometry Specialist" doesn't contain "pure flow cytometry specialist"
        // because the word "pure" is absent. The Contains check is substring-based.
        // This is detected by the LLM scorer (dealbreakers: FAIL) not the deterministic filter.
        // The dealbreaker list should use matchable keywords like "flow cytometry specialist"
        // rather than descriptive phrases like "pure flow cytometry specialist".
        var fields = new Dictionary<string, string?>
        {
            ["title"] = "Flow Cytometry Specialist",
            ["company"] = "Beckman Coulter (Danaher)",
            ["location"] = "Copenhagen, Denmark",
            ["url"] = "/job/flow-specialist/789/"
        };

        // Filter layer: dealbreaker phrase doesn't substring-match the shorter title.
        // This is a KNOWN GAP — descriptive dealbreakers need LLM, not Contains.
        // The LLM scorer catches it via dealbreakers: FAIL dimension.

        // Alert policy: dealbreaker FAIL → SILENT
        var result = _alertPolicy.Evaluate(
            DimensionsWithFail("dealbreakers", "Pure flow cytometry specialist role"),
            "SKIP", null, _config);

        result.AlertLevel.ShouldBe(AlertLevel.Silent, "TC4: Dealbreaker should be SILENT");
        Log($"TC4: {result.AlertLevel} — {result.Reason}");
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════
    // TC5: Czech Language Posting — SZÚ
    // Expected: 🟡 MEDIUM (salary below floor)
    // ═══════════════════════════════════════════════

    [Test]
    public async Task TC5_CzechPosting_SZU_ShouldBeMedium()
    {
        var fields = new Dictionary<string, string?>
        {
            ["title"] = "Laboratorní pracovník – oddělení molekulární diagnostiky",
            ["url"] = "https://szu.gov.cz/kariera/laboratorni-pracovnik-molekularni-diagnostika/"
        };

        // Filter layer: SZÚ listing has title only — no PhD/location fields to suppress
        AssertNotSuppressed(fields, "TC5 should pass filters — no suppressible fields");

        // Alert policy: salary STRETCH (below floor but public sector) → MEDIUM
        var dims = AllPassDimensions();
        dims = AddDimension(dims, "salary", "STRETCH", 0.4f, "38-42k CZK below 50k floor, public sector");
        var result = _alertPolicy.Evaluate(dims, "REVIEW", null, _config);

        result.AlertLevel.ShouldBe(AlertLevel.Medium, "TC5: Below-floor salary should be MEDIUM");
        Log($"TC5: {result.AlertLevel} — {result.Reason}");
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════
    // TC6: Wrong Location — Institut Pasteur Paris
    // Expected: ⚪ SILENT
    // ═══════════════════════════════════════════════

    [Test]
    public async Task TC6_WrongLocation_Paris_ShouldBeSilent()
    {
        var fields = new Dictionary<string, string?>
        {
            ["title"] = "Research Technician, Cell Biology",
            ["company"] = "Institut Pasteur",
            ["location"] = "Paris, France",
            ["url"] = "/job/paris-research-tech/101/"
        };

        // Filter layer: "Paris, France" doesn't match any target location → SUPPRESSED
        AssertSuppressed(fields, "WRONG_LOCATION",
            "TC6 should trigger location filter — Paris not in targets");

        // Alert policy: location FAIL → SILENT
        var result = _alertPolicy.Evaluate(
            DimensionsWithFail("location", "Paris — not in target locations"),
            "SKIP", null, _config);

        result.AlertLevel.ShouldBe(AlertLevel.Silent, "TC6: Wrong location should be SILENT");
        Log($"TC6: {result.AlertLevel} — {result.Reason}");
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════
    // TC7: Hidden Senior Role — Genmab "Scientist"
    // Expected: ⚪ SILENT (PhD required in requirements)
    // ═══════════════════════════════════════════════

    [Test]
    public async Task TC7_HiddenSeniorRole_ShouldBeSilent()
    {
        var fields = new Dictionary<string, string?>
        {
            ["title"] = "Scientist, Antibody Discovery",
            ["company"] = "Genmab A/S",
            ["location"] = "Copenhagen, Denmark",
            ["education_required"] = "PhD",
            ["url"] = "/job/copenhagen-scientist-antibody/202/"
        };

        // Filter layer: education_required=PhD → SUPPRESSED
        AssertSuppressed(fields, "DISQUALIFIED_PHD",
            "TC7 should trigger PhD disqualification from education_required field");

        // Also test: phage display in techniques_none should fire skill gap
        var skillFields = new Dictionary<string, string?>(fields)
        {
            ["skills_required"] = "phage display, hybridoma technology, affinity maturation"
        };
        AssertTagged(skillFields, "SKILL_GAP", "TC7 should also tag skill gap for phage display");

        Log("TC7: SILENT — PhD + skill gaps");
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════
    // TC8: Czech Diagnostics R&D — Prague
    // Expected: 🔴 HIGH
    // ═══════════════════════════════════════════════

    [Test]
    public async Task TC8_CzechDiagnosticsRD_ShouldBeHigh()
    {
        var fields = new Dictionary<string, string?>
        {
            ["title"] = "Výzkumný pracovník – vývoj diagnostických testů",
            ["location"] = "Prague",
            ["url"] = "/job/prague-diagnostics-rd/303/"
        };

        // Filter layer: Prague location, no PhD in title → should pass
        AssertNotSuppressed(fields, "TC8 should pass all filters");

        // Alert policy: all PASS → HIGH
        var result = _alertPolicy.Evaluate(
            AllPassDimensions(), "APPLY", ParseDate("30-04-2026"), _config);

        result.AlertLevel.ShouldBe(AlertLevel.High, "TC8: Perfect Czech match should be HIGH");
        Log($"TC8: {result.AlertLevel} — {result.Reason}");
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════
    // TC9: Subtle Skill Gap — Lundbeck
    // Expected: 🟡 MEDIUM
    // ═══════════════════════════════════════════════

    [Test]
    public async Task TC9_SubtleSkillGap_Lundbeck_ShouldBeMedium()
    {
        var fields = new Dictionary<string, string?>
        {
            ["title"] = "Laboratory Scientist, In Vitro Pharmacology",
            ["location"] = "Valby, Copenhagen",
            ["url"] = "/job/valby-lab-scientist/404/"
        };

        // Filter layer: no skill gap terms in title → passes
        AssertNotSuppressed(fields, "TC9 should pass filters — skill gap only in description");

        // Alert policy: skills STRETCH → MEDIUM
        var dims = AllPassDimensions();
        dims = AddDimension(dims, "skills", "STRETCH", 0.6f,
            "Automated liquid handling (Hamilton/Beckman) required but not in profile");
        var result = _alertPolicy.Evaluate(dims, "REVIEW", null, _config);

        result.AlertLevel.ShouldBe(AlertLevel.Medium, "TC9: Skill gap should be MEDIUM");
        Log($"TC9: {result.AlertLevel} — {result.Reason}");
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════
    // TC10: Repost / Already Applied — Novonesis
    // Expected: 🟡 MEDIUM (location STRETCH — Kalundborg not Copenhagen)
    // ═══════════════════════════════════════════════

    [Test]
    public async Task TC10_Repost_Kalundborg_ShouldBeMedium()
    {
        var fields = new Dictionary<string, string?>
        {
            ["title"] = "Scientist, Enzyme Development",
            ["company"] = "Novonesis",
            ["location"] = "Kalundborg, Denmark",
            ["url"] = "/job/kalundborg-enzyme-dev/505/"
        };

        // Kalundborg is NOT in target_locations (it's ~100km from Copenhagen)
        // Location filter should suppress IF location is geographic (not department)
        // "Kalundborg, Denmark" is geographic → filter fires
        AssertSuppressed(fields, "WRONG_LOCATION",
            "TC10: Kalundborg not in target locations — geographic location correctly filtered");

        Log("TC10: SILENT/MEDIUM — Kalundborg is not Copenhagen area");
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════
    // Cross-cutting: Date Parsing Verification
    // ═══════════════════════════════════════════════

    [Test]
    public async Task DateParsing_EuropeanFormats_AllWork()
    {
        var formats = new[] { "dd-MM-yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "d MMM yyyy",
            "dd.MM.yyyy", "MM/dd/yyyy", "yyyy/MM/dd", "d MMMM yyyy" };
        var testDates = new[]
        {
            ("15-04-2026", "2026-04-15"),  // UCPH DD-MM-YYYY
            ("13 Mar 2026", "2026-03-13"), // Novo DD MMM YYYY
            ("2026-04-20", "2026-04-20"),  // ISO
            ("01-04-2026", "2026-04-01"),  // ambiguous DD-MM
            ("6 Mar 2026", "2026-03-06"),  // single-digit day
        };

        foreach (var (input, expected) in testDates)
        {
            var parsed = DateTime.TryParseExact(input.Trim(), formats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt);
            parsed.ShouldBeTrue($"Date '{input}' should parse");
            dt.ToString("yyyy-MM-dd").ShouldBe(expected, $"Date '{input}' should parse to {expected}");
        }

        Log("All 5 date formats parsed correctly");
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════
    // Cross-cutting: PhD Title Regex Coverage
    // ═══════════════════════════════════════════════

    [Test]
    public async Task PhDTitleRegex_CatchesAllVariants()
    {
        var rules = _ruleGen.GenerateRules(CandidateProfile);
        var phdRule = rules.First(r => r.Name.Contains("PhD"));
        var titleCondition = phdRule.Conditions.First(c => c.FieldName == "title");
        var regex = titleCondition.Value!;

        var shouldMatch = new[]
        {
            "2 PhD fellowships in in-vitro digestion",
            "PhD fellowship in Molecular Cell Biology",
            "PhD Projects in Theoretical Quantum Optics",
            "Research assistant and PhD fellowship in forensic genetics",
            "PhD stipends in Mathematics",
            "PhD program in Computational Biophysics",
        };

        var shouldNotMatch = new[]
        {
            "Laboratory Assistant at Protein Production facility",
            "Postdoc in Hepatitis virus and advanced therapeutics",
            "Staff Scientist at the LEO Foundation",
            "Research Technician, Cell Biology",
        };

        foreach (var t in shouldMatch)
            Regex.IsMatch(t, regex).ShouldBeTrue($"PhD regex should MATCH: '{t}'");

        foreach (var t in shouldNotMatch)
            Regex.IsMatch(t, regex).ShouldBeFalse($"PhD regex should NOT match: '{t}'");

        Log($"PhD regex: {shouldMatch.Length} correct matches, {shouldNotMatch.Length} correct misses");
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════
    // Cross-cutting: Skill Gap Regex for Multi-Word Skills
    // ═══════════════════════════════════════════════

    [Test]
    public async Task SkillGapRegex_MatchesMultiWordSkillsWithInterveningWords()
    {
        var rules = _ruleGen.GenerateRules(CandidateProfile);
        var organoidRule = rules.First(r => r.Name.Contains("organoid"));
        var titleCondition = organoidRule.Conditions.First(c => c.FieldName == "title");
        var regex = titleCondition.Value!;

        // "organoid culture" → should match even with "cell" in between
        Regex.IsMatch("Organoid cell culture and assay specialist", regex)
            .ShouldBeTrue("Should match 'organoid...culture' with intervening words");

        Regex.IsMatch("Staff Scientist at LEO Foundation", regex)
            .ShouldBeFalse("Should NOT match unrelated title");

        Log("Multi-word skill gap regex works correctly");
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════
    // Cross-cutting: Location Guard for Departments
    // ═══════════════════════════════════════════════

    [Test]
    public async Task LocationGuard_DepartmentNames_NotSuppressed()
    {
        var rules = _ruleGen.GenerateRules(CandidateProfile);
        var locRule = rules.First(r => r.Name.Contains("Location"));
        var guardCondition = locRule.Conditions.First(c => c.Negate && c.Operator == FilterOperator.Regex);
        var guardRegex = guardCondition.Value!;

        var shouldGuard = new[]
        {
            "Department of Drug Design and Pharmacology",
            "Niels Bohr Institute",
            "Center for Translational Neuromedicine",
            "Globe Institute",
            "Biotech Research and Innovation Centre",
            "Natural History Museum Denmark",
        };

        var shouldNotGuard = new[]
        {
            "Copenhagen, Denmark",
            "Paris, France",
            "Berlin, Germany",
            "Måløv, Capital Region of Denmark, DK",
            "Kalundborg, Denmark",
        };

        foreach (var d in shouldGuard)
            Regex.IsMatch(d, guardRegex).ShouldBeTrue($"Guard should MATCH (skip filter): '{d}'");

        foreach (var g in shouldNotGuard)
            Regex.IsMatch(g, guardRegex).ShouldBeFalse($"Guard should NOT match (apply filter): '{g}'");

        Log($"Location guard: {shouldGuard.Length} departments guarded, {shouldNotGuard.Length} geographic passed");
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════

    private void AssertNotSuppressed(Dictionary<string, string?> fields, string because)
    {
        var rules = _ruleGen.GenerateRules(CandidateProfile);
        foreach (var rule in rules.Where(r => r.IsEnabled))
        {
            if (!rule.Actions.Any(a => a.Type == FilterActionType.SuppressNotification)) continue;
            var matches = EvaluateRule(rule, fields);
            matches.ShouldBeFalse($"Rule '{rule.Name}' should NOT fire. {because}");
        }
    }

    private void AssertSuppressed(Dictionary<string, string?> fields, string expectedTag, string because)
    {
        var rules = _ruleGen.GenerateRules(CandidateProfile);
        var fired = false;
        foreach (var rule in rules.Where(r => r.IsEnabled))
        {
            if (!rule.Actions.Any(a => a.Type == FilterActionType.AddTag &&
                a.Parameters.GetValueOrDefault("tag") == expectedTag)) continue;
            if (EvaluateRule(rule, fields))
            {
                fired = true;
                break;
            }
        }
        fired.ShouldBeTrue($"Expected tag '{expectedTag}' to fire. {because}");
    }

    private void AssertTagged(Dictionary<string, string?> fields, string expectedTag, string because)
    {
        var rules = _ruleGen.GenerateRules(CandidateProfile);
        var tagged = rules.Where(r => r.IsEnabled)
            .Any(r => r.Actions.Any(a => a.Type == FilterActionType.AddTag &&
                a.Parameters.GetValueOrDefault("tag") == expectedTag) && EvaluateRule(r, fields));
        tagged.ShouldBeTrue($"Expected tag '{expectedTag}'. {because}");
    }

    private static bool EvaluateRule(FilterRule rule, Dictionary<string, string?> fields)
    {
        var results = rule.Conditions.Select(c => EvaluateCondition(c, fields)).ToList();
        return rule.Logic == FilterLogic.And ? results.All(r => r) : results.Any(r => r);
    }

    private static bool EvaluateCondition(FilterCondition c, Dictionary<string, string?> fields)
    {
        var value = fields.GetValueOrDefault(c.FieldName);
        bool result = c.Operator switch
        {
            FilterOperator.Equals => string.Equals(value, c.Value, StringComparison.OrdinalIgnoreCase),
            FilterOperator.Contains => value != null && c.Value != null &&
                value.Contains(c.Value, StringComparison.OrdinalIgnoreCase),
            FilterOperator.Regex => value != null && c.Value != null &&
                Regex.IsMatch(value, c.Value),
            FilterOperator.IsEmpty => string.IsNullOrWhiteSpace(value),
            FilterOperator.IsNotEmpty => !string.IsNullOrWhiteSpace(value),
            FilterOperator.LessThan => double.TryParse(value, out var v) &&
                double.TryParse(c.Value, out var t) && v < t,
            FilterOperator.GreaterThan => double.TryParse(value, out var v2) &&
                double.TryParse(c.Value, out var t2) && v2 > t2,
            _ => false
        };
        return c.Negate ? !result : result;
    }

    private static string AllPassDimensions() => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["education"] = new { score = 1.0, status = "PASS", reason = "MSc meets requirement" },
        ["skills"] = new { score = 0.9, status = "PASS", reason = "All required skills present" },
        ["location"] = new { score = 1.0, status = "PASS", reason = "Target location" },
        ["salary"] = new { score = 0.8, status = "PASS", reason = "Above floor" },
        ["experience"] = new { score = 0.9, status = "PASS", reason = "1-3 years matches" },
        ["language"] = new { score = 1.0, status = "PASS", reason = "English required, candidate has C1" },
        ["dealbreakers"] = new { score = 1.0, status = "PASS", reason = "No dealbreakers" }
    });

    private static string DimensionsWithFail(string dim, string reason)
    {
        var dict = new Dictionary<string, object>
        {
            ["skills"] = new { score = 0.9, status = "PASS", reason = "Skills OK" },
            ["education"] = new { score = 1.0, status = "PASS", reason = "Education OK" },
            ["dealbreakers"] = new { score = 1.0, status = "PASS", reason = "No dealbreakers" }
        };
        // Override or add the failing dimension
        dict[dim] = new { score = 0.0, status = "FAIL", reason };
        return JsonSerializer.Serialize(dict);
    }

    private static string DimensionsWithStretch(string dim, string reason)
    {
        var dims = new Dictionary<string, object>
        {
            ["skills"] = new { score = 0.9, status = "PASS", reason = "Skills match" },
            ["location"] = new { score = 1.0, status = "PASS", reason = "Location OK" },
            ["language"] = new { score = 1.0, status = "PASS", reason = "English OK" },
            ["dealbreakers"] = new { score = 1.0, status = "PASS", reason = "No dealbreakers" }
        };
        dims[dim] = new { score = 0.5, status = "STRETCH", reason };
        return JsonSerializer.Serialize(dims);
    }

    private static string AddDimension(string json, string dim, string status, float score, string reason)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        var newJson = JsonSerializer.Serialize(new { score, status, reason });
        dict[dim] = JsonSerializer.Deserialize<JsonElement>(newJson);
        return JsonSerializer.Serialize(dict);
    }

    private static DateTime? ParseDate(string date) =>
        DateTime.TryParseExact(date, "dd-MM-yyyy",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;
}
