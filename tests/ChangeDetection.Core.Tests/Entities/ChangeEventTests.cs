using ChangeDetection.Core.Entities;
using Shouldly;

namespace ChangeDetection.Core.Tests.Entities;

[Category("Unit")]
public class ChangeEventTests
{
    [Test]
    public async Task DefaultValues_ShouldBeCorrect()
    {
        var evt = new ChangeEvent();

        evt.Id.ShouldNotBe(Guid.Empty);
        evt.ChangeType.ShouldBe(ChangeType.Unknown);
        evt.Importance.ShouldBe(ChangeImportance.Low);
        evt.IsNotified.ShouldBeFalse();
        evt.IsViewed.ShouldBeFalse();
        evt.LinesAdded.ShouldBe(0);
        evt.LinesRemoved.ShouldBe(0);
        evt.SuppressNotification.ShouldBeFalse();
        evt.HasAmbiguousIdentities.ShouldBeFalse();
        evt.HasAnomalies.ShouldBeFalse();
        evt.HasLlmAnalysis.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task OwnerId_ShouldDefaultToGuidEmpty()
    {
        var evt = new ChangeEvent();

        evt.OwnerId.ShouldBe(Guid.Empty);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Collections_ShouldDefaultToEmpty()
    {
        var evt = new ChangeEvent();

        evt.Tags.ShouldBeEmpty();
        evt.AppliedActions.ShouldBeEmpty();
        evt.TriggeredAlerts.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task NullableProperties_ShouldDefaultToNull()
    {
        var evt = new ChangeEvent();

        evt.DiffSummary.ShouldBeNull();
        evt.DiffHtml.ShouldBeNull();
        evt.NotifiedAt.ShouldBeNull();
        evt.ObjectsDiff.ShouldBeNull();
        evt.FilterEvaluationResult.ShouldBeNull();
        evt.SemanticSummary.ShouldBeNull();
        evt.BriefSummary.ShouldBeNull();
        evt.RelevanceScore.ShouldBeNull();
        evt.RelevanceReason.ShouldBeNull();
        evt.AnomalyScore.ShouldBeNull();
        evt.AnalysisConfidence.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task DetectedAt_ShouldBeRecentUtc()
    {
        var before = DateTime.UtcNow;
        var evt = new ChangeEvent();
        var after = DateTime.UtcNow;

        evt.DetectedAt.ShouldBeGreaterThanOrEqualTo(before);
        evt.DetectedAt.ShouldBeLessThanOrEqualTo(after);
        await Task.CompletedTask;
    }
}
