using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.JobWatch;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.JobWatch;

/// <summary>
/// Tests for ItemTrackingService — lifecycle state machine and cross-portal dedup.
/// </summary>
[Category("Unit")]
public class ItemTrackingServiceTests : TestBase
{
    private readonly IRepository<TrackedItem> _repo = Substitute.For<IRepository<TrackedItem>>();
    private readonly IAlertPolicyService _alertPolicy;
    private readonly ItemTrackingService _sut;

    public ItemTrackingServiceTests()
    {
        _alertPolicy = new AlertPolicyService(NullLogger<AlertPolicyService>.Instance);
        _sut = new ItemTrackingService(_repo, _alertPolicy, NullLogger<ItemTrackingService>.Instance);
    }

    [Test]
    public async Task ProcessDiff_NewItems_CreatesTrackedItems()
    {
        SetupEmptyRepo();

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

        result.NewItems.Count.ShouldBe(2);
        result.NewItems[0].DisplayName.ShouldBe("Lab Scientist");
        result.NewItems[1].DisplayName.ShouldBe("Research Assistant");
        result.NewItems.ShouldAllBe(l => l.State == TrackedItemState.New);

        await _repo.Received(2).InsertAsync(Arg.Any<TrackedItem>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessDiff_DuplicateItem_DeduplicatesWithinGroup()
    {
        var groupId = Guid.NewGuid();
        var watch1Id = Guid.NewGuid();
        var watch2Id = Guid.NewGuid();

        var existingListing = new TrackedItem
        {
            OwnerId = Guid.Empty,
            WatchGroupId = groupId,
            SourceWatchId = watch1Id,
            IdentityKey = "lab scientist|biocorp",
            DisplayName = "Lab Scientist",
            DisplaySecondary = "BioCorp"
        };

        SetupRepoWith(groupId, existingListing);

        var diff = new ObjectDiffResult
        {
            AddedItems = [CreateExtractedObject("Lab Scientist", "BioCorp", "Copenhagen")]
        };

        var result = await _sut.ProcessDiffAsync(
            groupId, watch2Id, Guid.Empty,
            diff, null, null, CancellationToken.None);

        result.NewItems.Count.ShouldBe(0);
        result.DuplicateItems.Count.ShouldBe(1);
        existingListing.AdditionalSourceWatchIds.ShouldContain(watch2Id);
    }

    [Test]
    public async Task ProcessDiff_RemovedItem_IncrementsAbsenceCounter()
    {
        var groupId = Guid.NewGuid();
        var existing = new TrackedItem
        {
            OwnerId = Guid.Empty,
            WatchGroupId = groupId,
            SourceWatchId = Guid.NewGuid(),
            IdentityKey = "lab scientist|biocorp",
            DisplayName = "Lab Scientist",
            DisplaySecondary = "BioCorp",
            State = TrackedItemState.Alerted,
            ConsecutiveAbsences = 0
        };

        SetupRepoWith(groupId, existing);

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
        var existing = new TrackedItem
        {
            OwnerId = Guid.Empty,
            WatchGroupId = groupId,
            SourceWatchId = Guid.NewGuid(),
            IdentityKey = "lab scientist|biocorp",
            DisplayName = "Lab Scientist",
            DisplaySecondary = "BioCorp",
            State = TrackedItemState.Alerted,
            ConsecutiveAbsences = 1 // Already absent once
        };

        SetupRepoWith(groupId, existing);

        var diff = new ObjectDiffResult
        {
            RemovedItems = [CreateExtractedObject("Lab Scientist", "BioCorp", "Copenhagen")]
        };

        var result = await _sut.ProcessDiffAsync(
            groupId, Guid.NewGuid(), Guid.Empty,
            diff, null, null, CancellationToken.None);

        result.ConfirmedExpired.Count.ShouldBe(1);
        existing.State.ShouldBe(TrackedItemState.Expired);
    }

    [Test]
    public async Task TransitionState_ValidTransitions_Succeed()
    {
        var listing = new TrackedItem
        {
            Id = Guid.NewGuid(),
            IdentityKey = "test|co",
            State = TrackedItemState.New
        };
        _repo.GetByIdAsync(listing.Id, Arg.Any<CancellationToken>()).Returns(listing);

        var result = await _sut.TransitionStateAsync(listing.Id, TrackedItemState.Alerted, null, CancellationToken.None);
        result.ShouldBeTrue();
        listing.State.ShouldBe(TrackedItemState.Alerted);
        listing.AlertedAt.ShouldNotBeNull();
    }

    [Test]
    public async Task TransitionState_InvalidTransition_ReturnsFalse()
    {
        var listing = new TrackedItem
        {
            Id = Guid.NewGuid(),
            IdentityKey = "test|co",
            State = TrackedItemState.Expired
        };
        _repo.GetByIdAsync(listing.Id, Arg.Any<CancellationToken>()).Returns(listing);

        // Expired → Applied is not valid
        var result = await _sut.TransitionStateAsync(listing.Id, TrackedItemState.ActedOn, null, CancellationToken.None);
        result.ShouldBeFalse();
        listing.State.ShouldBe(TrackedItemState.Expired);
    }

    [Test]
    public async Task ExpirePassedDeadlines_ExpiresOverdueListings()
    {
        var groupId = Guid.NewGuid();
        var expiredListing = new TrackedItem
        {
            OwnerId = Guid.Empty,
            WatchGroupId = groupId,
            IdentityKey = "expired|co",
            State = TrackedItemState.Alerted,
            Deadline = DateTime.UtcNow.AddDays(-2) // Past deadline
        };
        var activeListing = new TrackedItem
        {
            OwnerId = Guid.Empty,
            WatchGroupId = groupId,
            IdentityKey = "active|co",
            State = TrackedItemState.Alerted,
            Deadline = DateTime.UtcNow.AddDays(5) // Future deadline
        };
        var noDeadlineListing = new TrackedItem
        {
            OwnerId = Guid.Empty,
            WatchGroupId = groupId,
            IdentityKey = "nodeadline|co",
            State = TrackedItemState.New,
            Deadline = null
        };

        // FindAsync for active listings (not expired/dismissed)
        _repo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<TrackedItem, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<TrackedItem> { expiredListing, activeListing, noDeadlineListing });

        var count = await _sut.ExpirePassedDeadlinesAsync(groupId, CancellationToken.None);

        count.ShouldBe(1);
        expiredListing.State.ShouldBe(TrackedItemState.Expired);
        expiredListing.ExpiryReason.ShouldBe(ExpiryReason.DeadlinePassed);
        activeListing.State.ShouldBe(TrackedItemState.Alerted);
        noDeadlineListing.State.ShouldBe(TrackedItemState.New);
    }

    private void SetupEmptyRepo()
    {
        _repo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<TrackedItem, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<TrackedItem>());
    }

    private void SetupRepoWith(Guid groupId, params TrackedItem[] listings)
    {
        _repo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<TrackedItem, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(listings.ToList());
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
