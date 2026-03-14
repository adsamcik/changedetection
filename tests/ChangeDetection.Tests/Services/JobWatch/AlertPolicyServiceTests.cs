using ChangeDetection.Core.Entities;
using ChangeDetection.Services.JobWatch;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.JobWatch;

/// <summary>
/// Tests for AlertPolicyService — maps LLM dimension scores to alert levels.
/// </summary>
[Category("Unit")]
public class AlertPolicyServiceTests : TestBase
{
    private readonly AlertPolicyService _sut = new(NullLogger<AlertPolicyService>.Instance);

    [Test]
    public async Task AllDimensionsPass_ReturnsHighAlert()
    {
        var dimensionsJson = """
            {
                "education": { "score": 1.0, "status": "PASS", "reason": "MSc meets requirement" },
                "skills": { "score": 0.9, "status": "PASS", "reason": "All required skills present" },
                "location": { "score": 1.0, "status": "PASS", "reason": "Copenhagen area" },
                "salary": { "score": 0.8, "status": "PASS", "reason": "Above floor" },
                "dealbreakers": { "score": 1.0, "status": "PASS", "reason": "No dealbreakers" }
            }
            """;

        var result = _sut.Evaluate(dimensionsJson, "APPLY");

        result.AlertLevel.ShouldBe(AlertLevel.High);
        result.Reason.ShouldContain("All checks pass");
        await Task.CompletedTask;
    }

    [Test]
    public async Task EducationFail_ReturnsMediumAlert_NotSilent()
    {
        // Education is NOT a hard-fail dimension (career advisor feedback #1).
        // "PhD or equivalent" must produce MEDIUM, not SILENT.
        var dimensionsJson = """
            {
                "education": { "score": 0.0, "status": "FAIL", "reason": "PhD required" },
                "skills": { "score": 0.9, "status": "PASS", "reason": "Skills match" },
                "location": { "score": 1.0, "status": "PASS", "reason": "Copenhagen" }
            }
            """;

        var result = _sut.Evaluate(dimensionsJson, "SKIP");

        // Education FAIL is treated as STRETCH (non-hard-fail dimension) → MEDIUM
        result.AlertLevel.ShouldBe(AlertLevel.Medium);
        await Task.CompletedTask;
    }

    [Test]
    public async Task DealbreakeFail_ReturnsSilentAlert()
    {
        var dimensionsJson = """
            {
                "education": { "score": 1.0, "status": "PASS", "reason": "MSc OK" },
                "skills": { "score": 0.9, "status": "PASS", "reason": "Skills match" },
                "dealbreakers": { "score": 0.0, "status": "FAIL", "reason": "SOTIO is excluded" }
            }
            """;

        var result = _sut.Evaluate(dimensionsJson, "SKIP");

        result.AlertLevel.ShouldBe(AlertLevel.Silent);
        result.Reason.ShouldContain("dealbreakers");
        await Task.CompletedTask;
    }

    [Test]
    public async Task StretchDimension_ReturnsMediumAlert()
    {
        var dimensionsJson = """
            {
                "education": { "score": 0.5, "status": "STRETCH", "reason": "PhD or equivalent experience" },
                "skills": { "score": 0.9, "status": "PASS", "reason": "All required skills present" },
                "location": { "score": 1.0, "status": "PASS", "reason": "Copenhagen area" }
            }
            """;

        var result = _sut.Evaluate(dimensionsJson, "REVIEW");

        result.AlertLevel.ShouldBe(AlertLevel.Medium);
        result.Reason.ShouldContain("education");
        await Task.CompletedTask;
    }

    [Test]
    public async Task NonHardFailDimension_TreatedAsStretch()
    {
        // salary and experience FAILs are not hard-fail dimensions
        var dimensionsJson = """
            {
                "education": { "score": 1.0, "status": "PASS", "reason": "MSc OK" },
                "skills": { "score": 0.9, "status": "PASS", "reason": "Skills match" },
                "salary": { "score": 0.2, "status": "FAIL", "reason": "Below salary floor" },
                "location": { "score": 1.0, "status": "PASS", "reason": "Copenhagen" }
            }
            """;

        var result = _sut.Evaluate(dimensionsJson, "REVIEW");

        // salary FAIL should NOT make it SILENT — it's treated as STRETCH
        result.AlertLevel.ShouldBe(AlertLevel.Medium);
        result.Reason.ShouldContain("salary");
        await Task.CompletedTask;
    }

    [Test]
    public async Task DeadlineWithin3Days_EscalatesToHigh()
    {
        var dimensionsJson = """
            {
                "education": { "score": 0.5, "status": "STRETCH", "reason": "Ambiguous requirement" },
                "skills": { "score": 0.9, "status": "PASS", "reason": "Skills match" }
            }
            """;

        var deadline = DateTime.UtcNow.AddDays(2);
        var result = _sut.Evaluate(dimensionsJson, "REVIEW", deadline);

        result.AlertLevel.ShouldBe(AlertLevel.High);
        result.UrgencyApplied.ShouldBeTrue();
        result.PreUrgencyLevel.ShouldBe(AlertLevel.Medium);
        result.Reason.ShouldContain("URGENT");
        await Task.CompletedTask;
    }

    [Test]
    public async Task DeadlineWithin7Days_MediumEscalatesToHigh()
    {
        var dimensionsJson = """
            {
                "education": { "score": 0.5, "status": "STRETCH", "reason": "Ambiguous" },
                "skills": { "score": 0.9, "status": "PASS", "reason": "OK" }
            }
            """;

        var deadline = DateTime.UtcNow.AddDays(5);
        var result = _sut.Evaluate(dimensionsJson, "REVIEW", deadline);

        result.AlertLevel.ShouldBe(AlertLevel.High);
        result.UrgencyApplied.ShouldBeTrue();
        result.DaysUntilDeadline.ShouldBe(5);
        await Task.CompletedTask;
    }

    [Test]
    public async Task DeadlinePassed_DoesNotEscalateSilent()
    {
        var dimensionsJson = """
            {
                "dealbreakers": { "score": 0.0, "status": "FAIL", "reason": "Dealbreaker triggered" },
                "skills": { "score": 0.9, "status": "PASS", "reason": "OK" }
            }
            """;

        var deadline = DateTime.UtcNow.AddDays(-1);
        var result = _sut.Evaluate(dimensionsJson, "SKIP", deadline);

        // SILENT should remain SILENT even with deadline urgency
        result.AlertLevel.ShouldBe(AlertLevel.Silent);
        result.UrgencyApplied.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task NullDimensions_DefaultsBasedOnRecommendation()
    {
        var result = _sut.Evaluate(null, "SKIP");
        result.AlertLevel.ShouldBe(AlertLevel.Silent);

        var result2 = _sut.Evaluate(null, "APPLY");
        result2.AlertLevel.ShouldBe(AlertLevel.Medium);
        await Task.CompletedTask;
    }

    [Test]
    public async Task EvaluateRemoval_ReturnsInfoLevel()
    {
        var listing = new TrackedItem
        {
            IdentityKey = "test|company",
            DisplayName = "Lab Scientist",
            DisplaySecondary = "BioCorp"
        };

        var result = _sut.EvaluateRemoval(listing);
        result.AlertLevel.ShouldBe(AlertLevel.Info);
        result.Reason.ShouldContain("Lab Scientist");
        result.Reason.ShouldContain("BioCorp");
        await Task.CompletedTask;
    }
}
