using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services;

[Category("Unit")]
public class WatchGroupServiceTests : TestBase
{
    private readonly IRepository<WatchGroup> _groupRepo;
    private readonly IRepository<WatchedSite> _watchRepo;
    private readonly IRepository<ChangeSnapshot> _snapshotRepo;
    private readonly IPriceHistoryRepository _priceHistoryRepo;
    private readonly ServerWatchGroupService _sut;

    public WatchGroupServiceTests()
    {
        _groupRepo = Substitute.For<IRepository<WatchGroup>>();
        _watchRepo = Substitute.For<IRepository<WatchedSite>>();
        _snapshotRepo = Substitute.For<IRepository<ChangeSnapshot>>();
        _priceHistoryRepo = Substitute.For<IPriceHistoryRepository>();
        var logger = CreateLogger<ServerWatchGroupService>();
        _sut = new ServerWatchGroupService(_groupRepo, _watchRepo, _snapshotRepo, _priceHistoryRepo, logger);
    }

    [Test]
    public async Task CreateGroupAsync_WithValidData_ReturnsGroup()
    {
        var request = new WatchGroupCreateRequest { Name = "PS5 Tracking" };
        var result = await _sut.CreateGroupAsync(request);
        result.ShouldNotBeNull();
        result.Name.ShouldBe("PS5 Tracking");
        await _groupRepo.Received(1).InsertAsync(Arg.Any<WatchGroup>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteGroupAsync_WithDeleteChildren_DeletesWatches()
    {
        var groupId = Guid.NewGuid();
        var group = new WatchGroup { Id = groupId, Name = "G" };
        var watch = new WatchedSite { Url = "https://a.com", GroupId = groupId };
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);
        _watchRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<WatchedSite, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { watch });
        await _sut.DeleteGroupAsync(groupId, deleteWatches: true);
        await _watchRepo.Received(1).DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _groupRepo.Received(1).DeleteAsync(groupId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteGroupAsync_WithoutDeleteChildren_UnlinksWatches()
    {
        var groupId = Guid.NewGuid();
        var group = new WatchGroup { Id = groupId, Name = "G" };
        var watch = new WatchedSite { Url = "https://a.com", GroupId = groupId };
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);
        _watchRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<WatchedSite, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { watch });
        await _sut.DeleteGroupAsync(groupId, deleteWatches: false);
        watch.GroupId.ShouldBeNull();
        await _watchRepo.Received(1).UpdateAsync(watch, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AddWatchToGroupAsync_SetsGroupId()
    {
        var groupId = Guid.NewGuid();
        var watch = new WatchedSite { Url = "https://a.com" };
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(new WatchGroup { Id = groupId, Name = "G" });
        _watchRepo.GetByIdAsync(watch.Id, Arg.Any<CancellationToken>()).Returns(watch);
        await _sut.AddWatchToGroupAsync(groupId, watch.Id);
        watch.GroupId.ShouldBe(groupId);
    }

    [Test]
    public async Task RemoveWatchFromGroupAsync_WrongGroup_Throws()
    {
        var groupId = Guid.NewGuid();
        var watch = new WatchedSite { Url = "https://a.com", GroupId = Guid.NewGuid() };
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(new WatchGroup { Id = groupId, Name = "G" });
        _watchRepo.GetByIdAsync(watch.Id, Arg.Any<CancellationToken>()).Returns(watch);
        await Should.ThrowAsync<InvalidOperationException>(() => _sut.RemoveWatchFromGroupAsync(groupId, watch.Id));
    }

    [Test]
    public async Task ComputeAggregateAsync_Min_ReturnsLowest()
    {
        var (groupId, members) = SetupGroupWithMembers(2);
        SetupPriceHistory(members[0].Id, "Price", 399.99m);
        SetupPriceHistory(members[1].Id, "Price", 449.99m);
        var group = CreateGroupWithField(groupId, "Price", AggregateFunction.Min);
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);

        var result = await _sut.ComputeAggregateAsync(groupId);
        result.Fields[0].AggregatedValue.ShouldNotBeNull();
        result.Fields[0].AggregatedValue.Value.ShouldBe(399.99, tolerance: 0.01);
        result.Fields[0].BestSourceName.ShouldBe("Site 0");
    }

    [Test]
    public async Task ComputeAggregateAsync_Average_ReturnsMean()
    {
        var (groupId, members) = SetupGroupWithMembers(2);
        SetupPriceHistory(members[0].Id, "Price", 100m);
        SetupPriceHistory(members[1].Id, "Price", 200m);
        var group = CreateGroupWithField(groupId, "Price", AggregateFunction.Average);
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);

        var result = await _sut.ComputeAggregateAsync(groupId);
        result.Fields[0].AggregatedValue.ShouldNotBeNull();
        result.Fields[0].AggregatedValue.Value.ShouldBe(150.0, tolerance: 0.01);
    }

    [Test]
    public async Task ComputeAggregateAsync_Sum_ReturnsTotal()
    {
        var (groupId, members) = SetupGroupWithMembers(2);
        SetupPriceHistory(members[0].Id, "Stock", 5m);
        SetupPriceHistory(members[1].Id, "Stock", 10m);
        var group = CreateGroupWithField(groupId, "Stock", AggregateFunction.Sum);
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);

        var result = await _sut.ComputeAggregateAsync(groupId);
        result.Fields[0].AggregatedValue.ShouldNotBeNull();
        result.Fields[0].AggregatedValue.Value.ShouldBe(15.0, tolerance: 0.01);
    }

    [Test]
    public async Task ComputeAggregateAsync_Median_ReturnsMedian()
    {
        var (groupId, members) = SetupGroupWithMembers(3);
        SetupPriceHistory(members[0].Id, "Price", 100m);
        SetupPriceHistory(members[1].Id, "Price", 500m);
        SetupPriceHistory(members[2].Id, "Price", 200m);
        var group = CreateGroupWithField(groupId, "Price", AggregateFunction.Median);
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);

        var result = await _sut.ComputeAggregateAsync(groupId);
        result.Fields[0].AggregatedValue.ShouldNotBeNull();
        result.Fields[0].AggregatedValue.Value.ShouldBe(200.0, tolerance: 0.01);
    }

    [Test]
    public async Task ComputeAggregateAsync_Range_ReturnsSpread()
    {
        var (groupId, members) = SetupGroupWithMembers(3);
        SetupPriceHistory(members[0].Id, "Price", 100m);
        SetupPriceHistory(members[1].Id, "Price", 400m);
        SetupPriceHistory(members[2].Id, "Price", 200m);
        var group = CreateGroupWithField(groupId, "Price", AggregateFunction.Range);
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);

        var result = await _sut.ComputeAggregateAsync(groupId);
        result.Fields[0].AggregatedValue.ShouldNotBeNull();
        result.Fields[0].AggregatedValue.Value.ShouldBe(300.0, tolerance: 0.01);
    }

    [Test]
    public async Task ComputeAggregateAsync_NoData_ReturnsNull()
    {
        var (groupId, _) = SetupGroupWithMembers(0);
        var group = CreateGroupWithField(groupId, "Price", AggregateFunction.Min);
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);

        var result = await _sut.ComputeAggregateAsync(groupId);
        result.Fields[0].AggregatedValue.ShouldBeNull();
    }

    [Test]
    public async Task EvaluateAlerts_DropsBelow_TriggersWhenMet()
    {
        var (groupId, members) = SetupGroupWithMembers(1);
        SetupPriceHistory(members[0].Id, "Price", 45m);
        var group = new WatchGroup
        {
            Id = groupId, Name = "Test",
            AggregateFields = [new AggregateFieldConfig { FieldName = "Price", Function = AggregateFunction.Min }],
            AggregateAlerts = [new AggregateAlert
            {
                FieldName = "Price", Function = AggregateFunction.Min,
                ConditionType = AlertConditionType.DropsBelow, Value = 50
            }]
        };
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);

        var result = await _sut.EvaluateAggregateAlertsAsync(groupId);
        result.TriggeredAlerts.Count.ShouldBe(1);
        result.TriggeredAlerts[0].AggregatedValue.ShouldNotBeNull();
        result.TriggeredAlerts[0].AggregatedValue.Value.ShouldBe(45.0, tolerance: 0.01);
    }

    [Test]
    public async Task EvaluateAlerts_DropsBelow_DoesNotTriggerWhenNotMet()
    {
        var (groupId, members) = SetupGroupWithMembers(1);
        SetupPriceHistory(members[0].Id, "Price", 55m);
        var group = new WatchGroup
        {
            Id = groupId, Name = "Test",
            AggregateFields = [new AggregateFieldConfig { FieldName = "Price", Function = AggregateFunction.Min }],
            AggregateAlerts = [new AggregateAlert
            {
                FieldName = "Price", Function = AggregateFunction.Min,
                ConditionType = AlertConditionType.DropsBelow, Value = 50
            }]
        };
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);

        var result = await _sut.EvaluateAggregateAlertsAsync(groupId);
        result.TriggeredAlerts.Count.ShouldBe(0);
    }

    [Test]
    public async Task EvaluateAlerts_OneTime_DisablesAfterTrigger()
    {
        var (groupId, members) = SetupGroupWithMembers(1);
        SetupPriceHistory(members[0].Id, "Price", 40m);
        var alert = new AggregateAlert
        {
            FieldName = "Price", Function = AggregateFunction.Min,
            ConditionType = AlertConditionType.DropsBelow, Value = 50, OneTime = true
        };
        var group = new WatchGroup
        {
            Id = groupId, Name = "Test",
            AggregateFields = [new AggregateFieldConfig { FieldName = "Price", Function = AggregateFunction.Min }],
            AggregateAlerts = [alert]
        };
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);

        await _sut.EvaluateAggregateAlertsAsync(groupId);
        alert.IsEnabled.ShouldBeFalse();
        alert.TriggerCount.ShouldBe(1);
    }

    [Test]
    public async Task EvaluateAlerts_Cooldown_SkipsDuringCooldown()
    {
        var (groupId, members) = SetupGroupWithMembers(1);
        SetupPriceHistory(members[0].Id, "Price", 40m);
        var group = new WatchGroup
        {
            Id = groupId, Name = "Test",
            AggregateFields = [new AggregateFieldConfig { FieldName = "Price", Function = AggregateFunction.Min }],
            AggregateAlerts = [new AggregateAlert
            {
                FieldName = "Price", Function = AggregateFunction.Min,
                ConditionType = AlertConditionType.DropsBelow, Value = 50,
                CooldownPeriod = TimeSpan.FromHours(1),
                LastTriggeredAt = DateTime.UtcNow.AddMinutes(-30)
            }]
        };
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);

        var result = await _sut.EvaluateAggregateAlertsAsync(groupId);
        result.TriggeredAlerts.Count.ShouldBe(0);
    }

    // --- Rank-Switch Tests ---

    [Test]
    public async Task RankChanged_WhenLeaderChanges_TriggersAfterStability()
    {
        var (groupId, members) = SetupGroupWithMembers(3);
        var fieldConfig = new AggregateFieldConfig
        {
            FieldName = "Price", Function = AggregateFunction.Min,
            PreviousBestSource = "Site 0", RankStabilityRequired = 2, CurrentLeaderHoldCount = 0
        };
        var group = new WatchGroup
        {
            Id = groupId, Name = "Test",
            AggregateFields = [fieldConfig],
            AggregateAlerts = [new AggregateAlert
            {
                FieldName = "Price", Function = AggregateFunction.Min,
                ConditionType = AlertConditionType.RankChanged, IsEnabled = true
            }]
        };
        // Site 1 is now cheapest (was Site 0)
        SetupPriceHistory(members[0].Id, "Price", 500m);
        SetupPriceHistory(members[1].Id, "Price", 350m);
        SetupPriceHistory(members[2].Id, "Price", 450m);
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);

        // First check: hold count = 1, stability requires 2 → should NOT trigger
        var result1 = await _sut.EvaluateAggregateAlertsAsync(groupId);
        result1.TriggeredAlerts.Count.ShouldBe(0);

        // Second check: hold count = 2 → should trigger
        var result2 = await _sut.EvaluateAggregateAlertsAsync(groupId);
        result2.TriggeredAlerts.Count.ShouldBe(1);
        result2.TriggeredAlerts[0].Message.ShouldContain("Site 1");
    }

    [Test]
    public async Task RankChanged_WhenSameLeader_DoesNotTrigger()
    {
        var (groupId, members) = SetupGroupWithMembers(2);
        var fieldConfig = new AggregateFieldConfig
        {
            FieldName = "Price", Function = AggregateFunction.Min,
            PreviousBestSource = "Site 0", RankStabilityRequired = 1, CurrentLeaderHoldCount = 5
        };
        var group = new WatchGroup
        {
            Id = groupId, Name = "Test",
            AggregateFields = [fieldConfig],
            AggregateAlerts = [new AggregateAlert
            {
                FieldName = "Price", Function = AggregateFunction.Min,
                ConditionType = AlertConditionType.RankChanged, IsEnabled = true
            }]
        };
        // Site 0 is still cheapest
        SetupPriceHistory(members[0].Id, "Price", 350m);
        SetupPriceHistory(members[1].Id, "Price", 450m);
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);

        var result = await _sut.EvaluateAggregateAlertsAsync(groupId);
        result.TriggeredAlerts.Count.ShouldBe(0);
    }

    // --- Outlier Detection Tests ---

    [Test]
    public async Task OutlierDetected_WhenSiteDiverges_Triggers()
    {
        var (groupId, members) = SetupGroupWithMembers(4);
        var group = new WatchGroup
        {
            Id = groupId, Name = "Test",
            AggregateFields = [new AggregateFieldConfig
            {
                FieldName = "Price", Function = AggregateFunction.Min,
                OutlierThresholdPercent = 15.0
            }],
            AggregateAlerts = [new AggregateAlert
            {
                FieldName = "Price", Function = AggregateFunction.Min,
                ConditionType = AlertConditionType.OutlierDetected, IsEnabled = true
            }]
        };
        // 3 sites at ~$460, one at $350 (>20% deviation from median)
        SetupPriceHistory(members[0].Id, "Price", 450m);
        SetupPriceHistory(members[1].Id, "Price", 460m);
        SetupPriceHistory(members[2].Id, "Price", 470m);
        SetupPriceHistory(members[3].Id, "Price", 350m);
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);

        var result = await _sut.EvaluateAggregateAlertsAsync(groupId);
        result.TriggeredAlerts.Count.ShouldBe(1);
        result.TriggeredAlerts[0].Message.ShouldContain("Outlier");
    }

    [Test]
    public async Task OutlierDetected_WhenAllClose_DoesNotTrigger()
    {
        var (groupId, members) = SetupGroupWithMembers(3);
        var group = new WatchGroup
        {
            Id = groupId, Name = "Test",
            AggregateFields = [new AggregateFieldConfig
            {
                FieldName = "Price", Function = AggregateFunction.Min,
                OutlierThresholdPercent = 20.0
            }],
            AggregateAlerts = [new AggregateAlert
            {
                FieldName = "Price", Function = AggregateFunction.Min,
                ConditionType = AlertConditionType.OutlierDetected, IsEnabled = true
            }]
        };
        SetupPriceHistory(members[0].Id, "Price", 450m);
        SetupPriceHistory(members[1].Id, "Price", 460m);
        SetupPriceHistory(members[2].Id, "Price", 455m);
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);

        var result = await _sut.EvaluateAggregateAlertsAsync(groupId);
        result.TriggeredAlerts.Count.ShouldBe(0);
    }

    // --- Absence Detection Tests ---

    [Test]
    public async Task AbsenceDetection_ErrorStatus_MarksMissingPending()
    {
        var (groupId, members) = SetupGroupWithMembers(2);
        members[1].Status = WatchStatus.Error;
        var group = CreateGroupWithField(groupId, "Price", AggregateFunction.Min);
        SetupPriceHistory(members[0].Id, "Price", 450m);
        // No price history for member 1 (error site)
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);

        var snapshot = await _sut.ComputeAggregateAsync(groupId);
        snapshot.AbsenceSummary.ShouldNotBeNull();
        snapshot.AbsenceSummary.ConfirmedAbsentCount.ShouldBe(1);
        snapshot.AbsenceSummary.AbsentWatchIds.ShouldContain(members[1].Id);
    }

    [Test]
    public async Task SiteAbsentAlert_WhenAbsent_Triggers()
    {
        var (groupId, members) = SetupGroupWithMembers(2);
        members[1].Status = WatchStatus.Error;
        var group = new WatchGroup
        {
            Id = groupId, Name = "Test",
            AggregateFields = [new AggregateFieldConfig { FieldName = "Price", Function = AggregateFunction.Min }],
            AggregateAlerts = [new AggregateAlert
            {
                FieldName = "Price", Function = AggregateFunction.Min,
                ConditionType = AlertConditionType.SiteAbsent, IsEnabled = true
            }]
        };
        SetupPriceHistory(members[0].Id, "Price", 450m);
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);

        var result = await _sut.EvaluateAggregateAlertsAsync(groupId);
        result.TriggeredAlerts.Count.ShouldBe(1);
        result.TriggeredAlerts[0].Message.ShouldContain("absent");
    }

    [Test]
    public async Task AbsentSites_ExcludedFromAggregate()
    {
        var (groupId, members) = SetupGroupWithMembers(3);
        members[2].Status = WatchStatus.Error;
        var group = CreateGroupWithField(groupId, "Price", AggregateFunction.Min);
        SetupPriceHistory(members[0].Id, "Price", 450m);
        SetupPriceHistory(members[1].Id, "Price", 400m);
        // member 2 has no data (error status) — should be excluded from aggregate
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);

        var snapshot = await _sut.ComputeAggregateAsync(groupId);
        snapshot.Fields[0].AggregatedValue.ShouldNotBeNull();
        snapshot.Fields[0].AggregatedValue.Value.ShouldBe(400.0, tolerance: 0.01);
        snapshot.Fields[0].PerSiteValues
            .First(p => p.WatchId == members[2].Id)
            .AvailabilityState.ShouldBe(SiteAvailabilityState.ConfirmedAbsent);
    }

    // --- Sanity Guard Tests ---

    [Test]
    public async Task SanityGuard_ZeroValue_Quarantines()
    {
        var (groupId, members) = SetupGroupWithMembers(2);
        var group = CreateGroupWithField(groupId, "Price", AggregateFunction.Min);
        SetupPriceHistory(members[0].Id, "Price", 450m);
        SetupPriceHistory(members[1].Id, "Price", 0m);
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);

        var snapshot = await _sut.ComputeAggregateAsync(groupId);
        snapshot.DataQualityWarnings.Count.ShouldBe(1);
        snapshot.DataQualityWarnings[0].Reason.ShouldContain("zero or negative");
        snapshot.Fields[0].PerSiteValues
            .First(p => p.WatchId == members[1].Id).IsQuarantined.ShouldBeTrue();
        // Aggregate should use only the non-quarantined value
        snapshot.Fields[0].AggregatedValue.ShouldNotBeNull();
        snapshot.Fields[0].AggregatedValue.Value.ShouldBe(450.0, tolerance: 0.01);
    }

    [Test]
    public async Task SanityGuard_NegativeValue_Quarantines()
    {
        var (groupId, members) = SetupGroupWithMembers(2);
        var group = CreateGroupWithField(groupId, "Price", AggregateFunction.Min);
        SetupPriceHistory(members[0].Id, "Price", 450m);
        SetupPriceHistory(members[1].Id, "Price", -10m);
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);

        var snapshot = await _sut.ComputeAggregateAsync(groupId);
        snapshot.DataQualityWarnings.Count.ShouldBe(1);
        snapshot.Fields[0].AggregatedValue.Value.ShouldBe(450.0, tolerance: 0.01);
    }

    [Test]
    public async Task SanityGuard_ExtremeDeviation_Quarantines()
    {
        var (groupId, members) = SetupGroupWithMembers(3);
        var group = CreateGroupWithField(groupId, "Price", AggregateFunction.Min);
        SetupPriceHistory(members[0].Id, "Price", 450m);
        SetupPriceHistory(members[1].Id, "Price", 460m);
        SetupPriceHistory(members[2].Id, "Price", 5000m); // >200% deviation
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);

        var snapshot = await _sut.ComputeAggregateAsync(groupId);
        snapshot.DataQualityWarnings.Count.ShouldBe(1);
        snapshot.DataQualityWarnings[0].WatchId.ShouldBe(members[2].Id);
        snapshot.Fields[0].PerSiteValues
            .First(p => p.WatchId == members[2].Id).IsQuarantined.ShouldBeTrue();
        // Aggregate uses only the 2 non-quarantined values
        snapshot.Fields[0].AggregatedValue.Value.ShouldBe(450.0, tolerance: 0.01);
    }

    [Test]
    public async Task SanityGuard_NormalValues_PassThrough()
    {
        var (groupId, members) = SetupGroupWithMembers(3);
        var group = CreateGroupWithField(groupId, "Price", AggregateFunction.Min);
        SetupPriceHistory(members[0].Id, "Price", 450m);
        SetupPriceHistory(members[1].Id, "Price", 460m);
        SetupPriceHistory(members[2].Id, "Price", 440m);
        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>()).Returns(group);

        var snapshot = await _sut.ComputeAggregateAsync(groupId);
        snapshot.DataQualityWarnings.Count.ShouldBe(0);
        snapshot.Fields[0].PerSiteValues.ShouldAllBe(p => !p.IsQuarantined);
    }

    [Test]
    public async Task GetGroupHealthAsync_ClassifiesMembersAndCountsItems()
    {
        var groupId = Guid.NewGuid();
        var healthy = new WatchedSite
        {
            Url = "https://healthy.example",
            Name = "Healthy",
            GroupId = groupId,
            Status = WatchStatus.Active,
            LastChecked = DateTime.UtcNow.AddMinutes(-5)
        };
        var degraded = new WatchedSite
        {
            Url = "https://degraded.example",
            Name = "Degraded",
            GroupId = groupId,
            Status = WatchStatus.Checking,
            ConsecutiveFailures = 2,
            LastError = "Temporary timeout"
        };
        var errored = new WatchedSite
        {
            Url = "https://errored.example",
            Name = "Errored",
            GroupId = groupId,
            Status = WatchStatus.Error,
            ConsecutiveFailures = 4,
            LastError = "Fetch failed"
        };

        _groupRepo.GetByIdAsync(groupId, Arg.Any<CancellationToken>())
            .Returns(new WatchGroup { Id = groupId, Name = "Health Group" });
        _watchRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<WatchedSite, bool>>>(), Arg.Any<CancellationToken>())
            .Returns([healthy, degraded, errored]);
        _snapshotRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<ChangeSnapshot, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var predicate = ci.Arg<System.Linq.Expressions.Expression<Func<ChangeSnapshot, bool>>>().Compile();
                var snapshots = new[]
                {
                    new ChangeSnapshot
                    {
                        WatchedSiteId = healthy.Id,
                        ContentHash = "healthy",
                        Content = "healthy",
                        ExtractedObjectsJson = """[{"identityKey":"1"},{"identityKey":"2"}]"""
                    },
                    new ChangeSnapshot
                    {
                        WatchedSiteId = degraded.Id,
                        ContentHash = "degraded",
                        Content = "degraded",
                        ExtractedObjectsJson = """[{"identityKey":"1"}]"""
                    }
                };

                return snapshots.Where(predicate).ToList();
            });

        var result = await _sut.GetGroupHealthAsync(groupId);

        result.TotalWatches.ShouldBe(3);
        result.Healthy.ShouldBe(1);
        result.Degraded.ShouldBe(1);
        result.Errored.ShouldBe(1);
        result.Watches.ShouldContain(w => w.Name == "Healthy" && w.ItemCount == 2);
        result.Watches.ShouldContain(w => w.Name == "Errored" && w.Status == WatchHealthStatus.Errored);
    }

    // --- Helpers ---

    private (Guid GroupId, List<WatchedSite> Members) SetupGroupWithMembers(int count)
    {
        var groupId = Guid.NewGuid();
        var members = Enumerable.Range(0, count)
            .Select(i => new WatchedSite { Url = $"https://site{i}.com", Name = $"Site {i}", GroupId = groupId })
            .ToList();
        _watchRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<WatchedSite, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(members);
        return (groupId, members);
    }

    private void SetupPriceHistory(Guid watchId, string fieldName, decimal value)
    {
        _priceHistoryRepo.GetLatestAsync(watchId, fieldName, null, Arg.Any<CancellationToken>())
            .Returns(new PriceHistoryEntry { WatchId = watchId, FieldName = fieldName, Value = value });
    }

    private static WatchGroup CreateGroupWithField(Guid id, string fieldName, AggregateFunction function) => new()
    {
        Id = id, Name = "Test",
        AggregateFields = [new AggregateFieldConfig { FieldName = fieldName, Function = function }]
    };
}
