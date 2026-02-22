using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using Shouldly;
using TUnit.Core;
using System.Linq.Expressions;

namespace ChangeDetection.Tests.Endpoints;

[Category("Unit")]
public class FeedEndpointsTests
{
    private readonly IRepository<ChangeEvent> _eventRepo;
    private readonly IRepository<WatchedSite> _watchRepo;
    private readonly Guid _watchId = Guid.NewGuid();

    public FeedEndpointsTests()
    {
        _eventRepo = Substitute.For<IRepository<ChangeEvent>>();
        _watchRepo = Substitute.For<IRepository<WatchedSite>>();

        _watchRepo.GetByIdAsync(_watchId, Arg.Any<CancellationToken>())
            .Returns(new WatchedSite { Id = _watchId, Url = "https://example.com", Name = "Test Watch" });
    }

    private List<ChangeEvent> CreateTestEvents(int count)
    {
        return Enumerable.Range(0, count)
            .Select(i => new ChangeEvent
            {
                Id = Guid.NewGuid(),
                WatchedSiteId = _watchId,
                DetectedAt = DateTime.UtcNow.AddHours(-i),
                DiffSummary = $"Change {i}",
                BriefSummary = $"Brief {i}",
                ChangeType = ChangeType.Modified,
                Importance = ChangeImportance.Medium,
                LinesAdded = i,
                LinesRemoved = 0
            })
            .ToList();
    }

    [Test]
    public async Task GetChangeHistory_ReturnsPagedResults()
    {
        var events = CreateTestEvents(10);
        _eventRepo.FindAsync(
                Arg.Any<Expression<Func<ChangeEvent, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(events);

        var method = typeof(FeedEndpoints).GetMethod("GetChangeHistory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var result = await (Task<IResult>)method!.Invoke(null, [
            _watchId.ToString(), _eventRepo, _watchRepo, null, 5, CancellationToken.None
        ])!;

        var okResult = result.ShouldBeOfType<Ok<ChangeHistoryResponse>>();
        okResult.Value!.Items.Count.ShouldBe(5);
        okResult.Value.HasMore.ShouldBeTrue();
        okResult.Value.NextCursor.ShouldNotBeNull();
    }

    [Test]
    public async Task GetChangeHistory_InvalidWatchId_ReturnsBadRequest()
    {
        var method = typeof(FeedEndpoints).GetMethod("GetChangeHistory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var result = await (Task<IResult>)method!.Invoke(null, [
            "not-a-guid", _eventRepo, _watchRepo, null, null, CancellationToken.None
        ])!;

        result.ShouldBeOfType<BadRequest<string>>();
    }

    [Test]
    public async Task GetChangeHistory_WatchNotFound_ReturnsNotFound()
    {
        var missingId = Guid.NewGuid();
        _watchRepo.GetByIdAsync(missingId, Arg.Any<CancellationToken>()).Returns((WatchedSite?)null);

        var method = typeof(FeedEndpoints).GetMethod("GetChangeHistory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var result = await (Task<IResult>)method!.Invoke(null, [
            missingId.ToString(), _eventRepo, _watchRepo, null, null, CancellationToken.None
        ])!;

        result.ShouldBeOfType<NotFound<string>>();
    }

    [Test]
    public async Task ExportCsv_ReturnsResult()
    {
        var events = CreateTestEvents(3);
        _eventRepo.FindAsync(
                Arg.Any<Expression<Func<ChangeEvent, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(events);

        var method = typeof(FeedEndpoints).GetMethod("ExportCsv",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var result = await (Task<IResult>)method!.Invoke(null, [
            _watchId.ToString(), _eventRepo, _watchRepo, null, CancellationToken.None
        ])!;

        result.ShouldNotBeNull();
    }

    [Test]
    public async Task GetRssFeed_ReturnsResult()
    {
        var events = CreateTestEvents(3);
        _eventRepo.FindAsync(
                Arg.Any<Expression<Func<ChangeEvent, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(events);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("localhost");

        var method = typeof(FeedEndpoints).GetMethod("GetRssFeed",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var result = await (Task<IResult>)method!.Invoke(null, [
            _watchId.ToString(), _eventRepo, _watchRepo, httpContext, null, CancellationToken.None
        ])!;

        result.ShouldNotBeNull();
    }
}
