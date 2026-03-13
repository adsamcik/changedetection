using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.JobWatch;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.JobWatch;

/// <summary>
/// Tests for ListingTrackingService — lifecycle state machine and cross-portal dedup.
/// </summary>
[Category("Unit")]
public class ListingTrackingServiceTests : TestBase
{
    private readonly IRepository<TrackedListing> _repo = Substitute.For<IRepository<TrackedListing>>();
    private readonly IAlertPolicyService _alertPolicy;
    private readonly ListingTrackingService _sut;

    public ListingTrackingServiceTests()
    {
        _alertPolicy = new AlertPolicyService(NullLogger<AlertPolicyService>.Instance);
        _sut = new ListingTrackingService(_repo, _alertPolicy, NullLogger<ListingTrackingService>.Instance);
    }

    [Test]
    public async Task ProcessDiff_NewItems_CreatesTrackedListings()
    {
        _repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns([]);

        var diff = new ObjectDiffResult
        {
            AddedItems =
            [
                CreateExtractedObject("Lab Scientist", "BioCorp", "Copenhagen"),
                CreateExtractedObject("Research Assistant", "NovoCo", "Prague")
            ]
        };

        var dimensionsJson = """{ "education": { "score": 1.0, "status": "PASS", "reason": "OK" } }""";
        var result = await _sut.ProcessDiffAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.Empty,
            diff, dimensionsJson, "APPLY", CancellationToken.None);

        result.NewListings.Count.ShouldBe(2);
        result.NewListings[0].Title.ShouldBe("Lab Scientist");
        result.NewListings[1].Title.ShouldBe("Research Assistant");
        result.NewListings.ShouldAllBe(l => l.State == ListingState.New);

        await _repo.Received(2).InsertAsync(Arg.Any<TrackedListing>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessDiff_DuplicateItem_DeduplicatesWithinGroup()
    {
        var groupId = Guid.NewGuid();
        var watch1Id = Guid.NewGuid();
        var watch2Id = Guid.NewGuid();

        var existingListing = new TrackedListing
        {
            OwnerId = Guid.Empty,
            WatchGroupId = groupId,
            SourceWatchId = watch1Id,
            IdentityKey = "lab scientist|biocorp",
            Title = "Lab Scientist",
            Company = "BioCorp"
        };

        _repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns([existingListing]);

        var diff = new ObjectDiffResult
        {
            AddedItems = [CreateExtractedObject("Lab Scientist", "BioCorp", "Copenhagen")]
        };

        var result = await _sut.ProcessDiffAsync(
            groupId, watch2Id, Guid.Empty,
            diff, null, null, CancellationToken.None);

        result.NewListings.Count.ShouldBe(0);
        result.DuplicateListings.Count.ShouldBe(1);
        existingListing.AdditionalSourceWatchIds.ShouldContain(watch2Id);
    }

    [Test]
    public async Task ProcessDiff_RemovedItem_IncrementsAbsenceCounter()
    {
        var groupId = Guid.NewGuid();
        var existing = new TrackedListing
        {
            OwnerId = Guid.Empty,
            WatchGroupId = groupId,
            SourceWatchId = Guid.NewGuid(),
            IdentityKey = "lab scientist|biocorp",
            Title = "Lab Scientist",
            Company = "BioCorp",
            State = ListingState.Alerted,
            ConsecutiveAbsences = 0
        };

        _repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns([existing]);

        var diff = new ObjectDiffResult
        {
            RemovedItems = [CreateExtractedObject("Lab Scientist", "BioCorp", "Copenhagen")]
        };

        var result = await _sut.ProcessDiffAsync(
            groupId, Guid.NewGuid(), Guid.Empty,
            diff, null, null, CancellationToken.None);

        // First absence — not yet confirmed
        result.PotentiallyExpired.Count.ShouldBe(1);
        result.ConfirmedExpired.Count.ShouldBe(0);
        existing.ConsecutiveAbsences.ShouldBe(1);
    }

    [Test]
    public async Task ProcessDiff_SecondAbsence_ConfirmsExpiry()
    {
        var groupId = Guid.NewGuid();
        var existing = new TrackedListing
        {
            OwnerId = Guid.Empty,
            WatchGroupId = groupId,
            SourceWatchId = Guid.NewGuid(),
            IdentityKey = "lab scientist|biocorp",
            Title = "Lab Scientist",
            Company = "BioCorp",
            State = ListingState.Alerted,
            ConsecutiveAbsences = 1 // Already absent once
        };

        _repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns([existing]);

        var diff = new ObjectDiffResult
        {
            RemovedItems = [CreateExtractedObject("Lab Scientist", "BioCorp", "Copenhagen")]
        };

        var result = await _sut.ProcessDiffAsync(
            groupId, Guid.NewGuid(), Guid.Empty,
            diff, null, null, CancellationToken.None);

        result.ConfirmedExpired.Count.ShouldBe(1);
        existing.State.ShouldBe(ListingState.Expired);
    }

    [Test]
    public async Task TransitionState_ValidTransitions_Succeed()
    {
        var listing = new TrackedListing
        {
            Id = Guid.NewGuid(),
            IdentityKey = "test|co",
            State = ListingState.New
        };
        _repo.GetByIdAsync(listing.Id, Arg.Any<CancellationToken>()).Returns(listing);

        var result = await _sut.TransitionStateAsync(listing.Id, ListingState.Alerted, null, CancellationToken.None);
        result.ShouldBeTrue();
        listing.State.ShouldBe(ListingState.Alerted);
        listing.AlertedAt.ShouldNotBeNull();
    }

    [Test]
    public async Task TransitionState_InvalidTransition_ReturnsFalse()
    {
        var listing = new TrackedListing
        {
            Id = Guid.NewGuid(),
            IdentityKey = "test|co",
            State = ListingState.Expired
        };
        _repo.GetByIdAsync(listing.Id, Arg.Any<CancellationToken>()).Returns(listing);

        // Expired → Applied is not valid
        var result = await _sut.TransitionStateAsync(listing.Id, ListingState.Applied, null, CancellationToken.None);
        result.ShouldBeFalse();
        listing.State.ShouldBe(ListingState.Expired);
    }

    [Test]
    public async Task ExpirePassedDeadlines_ExpiresOverdueListings()
    {
        var groupId = Guid.NewGuid();
        var listings = new List<TrackedListing>
        {
            new()
            {
                OwnerId = Guid.Empty,
                WatchGroupId = groupId,
                IdentityKey = "expired|co",
                State = ListingState.Alerted,
                Deadline = DateTime.UtcNow.AddDays(-2) // Past deadline
            },
            new()
            {
                OwnerId = Guid.Empty,
                WatchGroupId = groupId,
                IdentityKey = "active|co",
                State = ListingState.Alerted,
                Deadline = DateTime.UtcNow.AddDays(5) // Future deadline
            },
            new()
            {
                OwnerId = Guid.Empty,
                WatchGroupId = groupId,
                IdentityKey = "nodeadline|co",
                State = ListingState.New,
                Deadline = null
            }
        };

        _repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(listings);

        var count = await _sut.ExpirePassedDeadlinesAsync(groupId, CancellationToken.None);

        count.ShouldBe(1);
        listings[0].State.ShouldBe(ListingState.Expired);
        listings[1].State.ShouldBe(ListingState.Alerted);
        listings[2].State.ShouldBe(ListingState.New);
    }

    private static ExtractedObject CreateExtractedObject(string title, string company, string location)
    {
        return new ExtractedObject
        {
            Fields = new Dictionary<string, string?>
            {
                ["title"] = title,
                ["company"] = company,
                ["location"] = location
            }
        };
    }
}
