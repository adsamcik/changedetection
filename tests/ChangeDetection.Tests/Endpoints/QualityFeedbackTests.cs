using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Endpoints;
using Microsoft.AspNetCore.OutputCaching;
using NSubstitute;
using Shouldly;
using TUnit.Core;
using System.Linq.Expressions;

namespace ChangeDetection.Tests.Endpoints;

[Category("Unit")]
public class QualityFeedbackTests
{
    private readonly IRepository<ChangeEvent> _eventRepo;
    private readonly IRepository<WatchedSite> _watchRepo;
    private readonly IOutputCacheStore _cacheStore;
    private readonly Guid _watchId = Guid.NewGuid();

    public QualityFeedbackTests()
    {
        _eventRepo = Substitute.For<IRepository<ChangeEvent>>();
        _watchRepo = Substitute.For<IRepository<WatchedSite>>();
        _cacheStore = Substitute.For<IOutputCacheStore>();

        _watchRepo.GetByIdAsync(_watchId, Arg.Any<CancellationToken>())
            .Returns(new WatchedSite { Id = _watchId, Url = "https://example.com" });
    }

    [Test]
    public async Task SubmitFeedback_ValidEvent_UpdatesFeedback()
    {
        var eventId = Guid.NewGuid();
        var changeEvent = new ChangeEvent
        {
            Id = eventId,
            WatchedSiteId = _watchId,
            Feedback = UserFeedback.None
        };
        _eventRepo.GetByIdAsync(eventId, Arg.Any<CancellationToken>()).Returns(changeEvent);

        var method = typeof(ChangeEndpoints).GetMethod("SubmitFeedback",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var request = new FeedbackRequest(UserFeedback.Helpful, "Great catch!");
        var result = await (Task<Microsoft.AspNetCore.Http.IResult>)method!.Invoke(null, [
            eventId, request, _eventRepo, _cacheStore, CancellationToken.None
        ])!;

        await _eventRepo.Received(1).UpdateAsync(
            Arg.Is<ChangeEvent>(e => e.Feedback == UserFeedback.Helpful && e.FeedbackNote == "Great catch!"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SubmitFeedback_MissingEvent_ReturnsNotFound()
    {
        var eventId = Guid.NewGuid();
        _eventRepo.GetByIdAsync(eventId, Arg.Any<CancellationToken>()).Returns((ChangeEvent?)null);

        var method = typeof(ChangeEndpoints).GetMethod("SubmitFeedback",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var request = new FeedbackRequest(UserFeedback.FalsePositive);
        var result = await (Task<Microsoft.AspNetCore.Http.IResult>)method!.Invoke(null, [
            eventId, request, _eventRepo, _cacheStore, CancellationToken.None
        ])!;

        result.ShouldBeOfType<Microsoft.AspNetCore.Http.HttpResults.NotFound>();
    }

    [Test]
    public async Task GetQualityMetrics_ComputesPrecisionRecall()
    {
        var events = new List<ChangeEvent>
        {
            new() { WatchedSiteId = _watchId, Feedback = UserFeedback.Helpful, RelevanceScore = 0.9f },
            new() { WatchedSiteId = _watchId, Feedback = UserFeedback.Helpful, RelevanceScore = 0.8f },
            new() { WatchedSiteId = _watchId, Feedback = UserFeedback.FalsePositive, RelevanceScore = 0.3f },
            new() { WatchedSiteId = _watchId, Feedback = UserFeedback.Missed },
            new() { WatchedSiteId = _watchId, Feedback = UserFeedback.None, RelevanceScore = 0.7f }
        };

        _eventRepo.FindAsync(Arg.Any<Expression<Func<ChangeEvent, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(events);

        var method = typeof(ChangeEndpoints).GetMethod("GetQualityMetrics",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var result = await (Task<Microsoft.AspNetCore.Http.IResult>)method!.Invoke(null, [
            _watchId, _eventRepo, _watchRepo, CancellationToken.None
        ])!;

        var ok = result.ShouldBeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<QualityMetricsDto>>();
        var metrics = ok.Value!;

        metrics.TotalEvents.ShouldBe(5);
        metrics.EventsWithFeedback.ShouldBe(4);
        metrics.Helpful.ShouldBe(2);
        metrics.FalsePositive.ShouldBe(1);
        metrics.Missed.ShouldBe(1);

        // Precision = TP / (TP + FP) = 2 / (2 + 1) = 0.667
        metrics.Precision.ShouldNotBeNull();
        metrics.Precision!.Value.ShouldBe(2.0 / 3.0, 0.01);

        // Recall = TP / (TP + FN) = 2 / (2 + 1) = 0.667
        metrics.Recall.ShouldNotBeNull();
        metrics.Recall!.Value.ShouldBe(2.0 / 3.0, 0.01);
    }

    [Test]
    public async Task GetQualityMetrics_NoFeedback_ReturnsNullPrecision()
    {
        var events = new List<ChangeEvent>
        {
            new() { WatchedSiteId = _watchId, Feedback = UserFeedback.None }
        };

        _eventRepo.FindAsync(Arg.Any<Expression<Func<ChangeEvent, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(events);

        var method = typeof(ChangeEndpoints).GetMethod("GetQualityMetrics",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var result = await (Task<Microsoft.AspNetCore.Http.IResult>)method!.Invoke(null, [
            _watchId, _eventRepo, _watchRepo, CancellationToken.None
        ])!;

        var ok = result.ShouldBeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<QualityMetricsDto>>();
        ok.Value!.Precision.ShouldBeNull();
        ok.Value!.Recall.ShouldBeNull();
        ok.Value!.EventsWithFeedback.ShouldBe(0);
    }

    [Test]
    public async Task GetQualityMetrics_WatchNotFound_ReturnsNotFound()
    {
        var missingId = Guid.NewGuid();
        _watchRepo.GetByIdAsync(missingId, Arg.Any<CancellationToken>()).Returns((WatchedSite?)null);

        var method = typeof(ChangeEndpoints).GetMethod("GetQualityMetrics",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var result = await (Task<Microsoft.AspNetCore.Http.IResult>)method!.Invoke(null, [
            missingId, _eventRepo, _watchRepo, CancellationToken.None
        ])!;

        result.ShouldBeOfType<Microsoft.AspNetCore.Http.HttpResults.NotFound>();
    }
}
