using ChangeDetection.Core.Entities;
using ChangeDetection.Services;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services;

[Category("Unit")]
public class TrustAutopilotTests
{
    private readonly TrustAutopilot _sut = new();

    private static List<ChangeEvent> CreateFeedbackEvents(
        int helpful, int falsePositive, int irrelevant,
        float helpfulRelevance = 0.8f, float fpRelevance = 0.3f)
    {
        var events = new List<ChangeEvent>();
        for (var i = 0; i < helpful; i++)
            events.Add(new ChangeEvent { Feedback = UserFeedback.Helpful, RelevanceScore = helpfulRelevance });
        for (var i = 0; i < falsePositive; i++)
            events.Add(new ChangeEvent { Feedback = UserFeedback.FalsePositive, RelevanceScore = fpRelevance });
        for (var i = 0; i < irrelevant; i++)
            events.Add(new ChangeEvent { Feedback = UserFeedback.Irrelevant, RelevanceScore = fpRelevance - 0.1f });
        return events;
    }

    [Test]
    public async Task ComputeRecommendation_InsufficientData_ReturnsNull()
    {
        var events = CreateFeedbackEvents(3, 2, 0);
        var result = _sut.ComputeRecommendation(events, 0.5f);
        result.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ComputeRecommendation_ClearSeparation_FindsOptimalThreshold()
    {
        // Helpful events at 0.8, FP events at 0.3 — threshold should land between them
        var events = CreateFeedbackEvents(8, 5, 0, helpfulRelevance: 0.8f, fpRelevance: 0.3f);
        var result = _sut.ComputeRecommendation(events, 0.5f);
        result.ShouldNotBeNull();
        result!.RecommendedThreshold.ShouldBeGreaterThan(0.3f);
        result.RecommendedThreshold.ShouldBeLessThanOrEqualTo(0.8f);
        result.SampleSize.ShouldBe(13);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ComputeRecommendation_AlreadyOptimal_ReturnsNull()
    {
        var events = CreateFeedbackEvents(8, 5, 0, helpfulRelevance: 0.8f, fpRelevance: 0.3f);
        // Set current threshold near optimal
        var first = _sut.ComputeRecommendation(events, 0.5f);
        first.ShouldNotBeNull();
        // Now ask again with the recommended threshold — should return null (no change needed)
        var second = _sut.ComputeRecommendation(events, first!.RecommendedThreshold);
        second.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ComputeMetrics_AllHelpful_PerfectPrecision()
    {
        var events = CreateFeedbackEvents(10, 0, 0);
        var (precision, recall) = TrustAutopilot.ComputeMetrics(events, 0.5f);
        precision.ShouldBe(1.0);
        recall.ShouldBe(1.0);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ComputeMetrics_MixedFeedback_CorrectCalculation()
    {
        var events = new List<ChangeEvent>
        {
            new() { Feedback = UserFeedback.Helpful, RelevanceScore = 0.9f },
            new() { Feedback = UserFeedback.Helpful, RelevanceScore = 0.7f },
            new() { Feedback = UserFeedback.FalsePositive, RelevanceScore = 0.8f },
            new() { Feedback = UserFeedback.Helpful, RelevanceScore = 0.3f } // Below threshold
        };

        var (precision, recall) = TrustAutopilot.ComputeMetrics(events, 0.5f);
        // Above 0.5: 2 helpful + 1 FP → precision = 2/3
        precision.ShouldBe(2.0 / 3.0, 0.01);
        // Helpful total: 3, above threshold: 2 → recall = 2/3
        recall.ShouldBe(2.0 / 3.0, 0.01);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ComputeRecommendation_IgnoresNoFeedback()
    {
        var events = CreateFeedbackEvents(8, 5, 0);
        events.Add(new ChangeEvent { Feedback = UserFeedback.None, RelevanceScore = 0.5f });
        events.Add(new ChangeEvent { Feedback = UserFeedback.None, RelevanceScore = null });

        var result = _sut.ComputeRecommendation(events, 0.5f);
        result.ShouldNotBeNull();
        result!.SampleSize.ShouldBe(13); // Only counted feedback events
        await Task.CompletedTask;
    }
}
