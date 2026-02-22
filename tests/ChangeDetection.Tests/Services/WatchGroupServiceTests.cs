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
    private readonly IPriceHistoryRepository _priceHistoryRepo;
    private readonly ServerWatchGroupService _sut;

    public WatchGroupServiceTests()
    {
        _groupRepo = Substitute.For<IRepository<WatchGroup>>();
        _watchRepo = Substitute.For<IRepository<WatchedSite>>();
        _priceHistoryRepo = Substitute.For<IPriceHistoryRepository>();
        var logger = CreateLogger<ServerWatchGroupService>();
        _sut = new ServerWatchGroupService(_groupRepo, _watchRepo, _priceHistoryRepo, logger);
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
