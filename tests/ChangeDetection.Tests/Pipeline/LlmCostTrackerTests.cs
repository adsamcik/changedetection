using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.BlockExecution;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using System.Linq.Expressions;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline;

[Category("Unit")]
public class LlmCostTrackerTests
{
    private IRepository<LlmUsageRecord> _usageRepo = null!;
    private LlmCostTracker _tracker = null!;

    [Before(Test)]
    public async Task SetUp()
    {
        _usageRepo = Substitute.For<IRepository<LlmUsageRecord>>();
        var logger = Substitute.For<ILogger<LlmCostTracker>>();
        _tracker = new LlmCostTracker(_usageRepo, logger);
        await Task.CompletedTask;
    }

    [Test]
    public async Task RecordUsageAsync_StoresRecord()
    {
        // Arrange
        var watchId = Guid.NewGuid();

        // Act
        await _tracker.RecordUsageAsync(watchId, "block-1", "gpt-4", 100, 50, 0.05m);

        // Assert
        await _usageRepo.Received(1).InsertAsync(
            Arg.Is<LlmUsageRecord>(r =>
                r.WatchedSiteId == watchId &&
                r.ProviderName == "gpt-4" &&
                r.Model == "gpt-4" &&
                r.InputTokens == 100 &&
                r.OutputTokens == 50 &&
                r.Cost == 0.05m &&
                r.IsSuccess),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetCurrentMonthCostAsync_SumsCurrentMonthOnly()
    {
        // Arrange
        var watchId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var currentMonthRecords = new List<LlmUsageRecord>
        {
            new() { ProviderName = "b1", Model = "m1", WatchedSiteId = watchId, Cost = 0.10m, Timestamp = now.AddDays(-1) },
            new() { ProviderName = "b2", Model = "m1", WatchedSiteId = watchId, Cost = 0.25m, Timestamp = now.AddDays(-2) },
            new() { ProviderName = "b3", Model = "m1", WatchedSiteId = watchId, Cost = 0.15m, Timestamp = now }
        };

        _usageRepo.FindAsync(Arg.Any<Expression<Func<LlmUsageRecord, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(currentMonthRecords);

        // Act
        var cost = await _tracker.GetCurrentMonthCostAsync(watchId);

        // Assert
        cost.ShouldBe(0.50m);
    }

    [Test]
    public async Task IsBudgetExceededAsync_ReturnsTrueWhenOverBudget()
    {
        // Arrange
        var watchId = Guid.NewGuid();
        var records = new List<LlmUsageRecord>
        {
            new() { ProviderName = "b1", Model = "m1", WatchedSiteId = watchId, Cost = 5.00m, Timestamp = DateTime.UtcNow },
            new() { ProviderName = "b2", Model = "m1", WatchedSiteId = watchId, Cost = 6.00m, Timestamp = DateTime.UtcNow }
        };

        _usageRepo.FindAsync(Arg.Any<Expression<Func<LlmUsageRecord, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(records);

        // Act
        var exceeded = await _tracker.IsBudgetExceededAsync(watchId, 10.00m);

        // Assert
        exceeded.ShouldBeTrue();
    }

    [Test]
    public async Task IsBudgetExceededAsync_ReturnsFalseWhenUnderBudget()
    {
        // Arrange
        var watchId = Guid.NewGuid();
        var records = new List<LlmUsageRecord>
        {
            new() { ProviderName = "b1", Model = "m1", WatchedSiteId = watchId, Cost = 1.00m, Timestamp = DateTime.UtcNow },
            new() { ProviderName = "b2", Model = "m1", WatchedSiteId = watchId, Cost = 2.00m, Timestamp = DateTime.UtcNow }
        };

        _usageRepo.FindAsync(Arg.Any<Expression<Func<LlmUsageRecord, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(records);

        // Act
        var exceeded = await _tracker.IsBudgetExceededAsync(watchId, 10.00m);

        // Assert
        exceeded.ShouldBeFalse();
    }

    [Test]
    public async Task GetUsageSummaryAsync_ReturnsCorrectSummary()
    {
        // Arrange
        var watchId = Guid.NewGuid();
        var records = new List<LlmUsageRecord>
        {
            new() { ProviderName = "b1", Model = "m1", WatchedSiteId = watchId, Cost = 0.10m, InputTokens = 100, OutputTokens = 50, Timestamp = DateTime.UtcNow },
            new() { ProviderName = "b2", Model = "m1", WatchedSiteId = watchId, Cost = 0.20m, InputTokens = 200, OutputTokens = 80, Timestamp = DateTime.UtcNow }
        };

        _usageRepo.FindAsync(Arg.Any<Expression<Func<LlmUsageRecord, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(records);
        _usageRepo.CountAsync(Arg.Any<Expression<Func<LlmUsageRecord, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(5);

        // Act
        var summary = await _tracker.GetUsageSummaryAsync(watchId);

        // Assert
        summary.WatchId.ShouldBe(watchId);
        summary.CurrentMonthCost.ShouldBe(0.30m);
        summary.CurrentMonthTokens.ShouldBe(430);
        summary.TotalRecords.ShouldBe(5);
        summary.MonthlyBudget.ShouldBeNull();
    }
}
