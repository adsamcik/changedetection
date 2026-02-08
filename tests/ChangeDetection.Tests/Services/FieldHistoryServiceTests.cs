using System.Linq.Expressions;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services;
using NSubstitute;
using Shouldly;

namespace ChangeDetection.Tests.Services;

[Category("Unit")]
public class FieldHistoryServiceTests : TestBase
{
    private readonly IRepository<FieldValueHistory> _repository;
    private readonly IAlertThresholdEvaluator _alertEvaluator;
    private readonly FieldHistoryService _sut;

    private readonly Guid _watchId = Guid.NewGuid();
    private const string ObjectIdentity = "_single";
    private const string FieldName = "price";

    public FieldHistoryServiceTests()
    {
        _repository = Substitute.For<IRepository<FieldValueHistory>>();
        _alertEvaluator = Substitute.For<IAlertThresholdEvaluator>();
        var logger = CreateLogger<FieldHistoryService>();
        _sut = new FieldHistoryService(_repository, _alertEvaluator, logger);
    }

    // ========== RecordValuesAsync ==========

    [Test]
    public async Task RecordValuesAsync_WithValidData_StoresSuccessfully()
    {
        _repository.FindAsync(Arg.Any<Expression<Func<FieldValueHistory, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<FieldValueHistory>());

        var snapshotId = Guid.NewGuid();
        var values = new List<FieldValueRecord>
        {
            new()
            {
                ObjectIdentity = ObjectIdentity,
                FieldName = "price",
                RawValue = "$29.99",
                NumericValue = 29.99,
                FieldType = FieldType.Currency,
                CurrencyCode = "USD"
            },
            new()
            {
                ObjectIdentity = ObjectIdentity,
                FieldName = "quantity",
                RawValue = "5",
                NumericValue = 5,
                FieldType = FieldType.Number
            }
        };

        await _sut.RecordValuesAsync(_watchId, snapshotId, values);

        await _repository.Received(2).InsertAsync(Arg.Any<FieldValueHistory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RecordValueAsync_FirstValue_SetsRunningMinMax()
    {
        _repository.FindAsync(Arg.Any<Expression<Func<FieldValueHistory, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<FieldValueHistory>());

        FieldValueHistory? captured = null;
        await _repository.InsertAsync(Arg.Do<FieldValueHistory>(h => captured = h), Arg.Any<CancellationToken>());

        await _sut.RecordValueAsync(_watchId, ObjectIdentity, FieldName, "$100", 100.0, FieldType.Currency, Guid.NewGuid());

        captured.ShouldNotBeNull();
        captured.RunningMin.ShouldBe(100.0);
        captured.RunningMax.ShouldBe(100.0);
        captured.Direction.ShouldBe(ChangeDirection.Unknown);
        captured.ChangeFromPrevious.ShouldBeNull();
    }

    [Test]
    public async Task RecordValueAsync_WithPreviousValue_CalculatesChange()
    {
        var previous = CreateHistory(numericValue: 100.0, runningMin: 90.0, runningMax: 110.0);
        _repository.FindAsync(Arg.Any<Expression<Func<FieldValueHistory, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { previous });

        FieldValueHistory? captured = null;
        await _repository.InsertAsync(Arg.Do<FieldValueHistory>(h => captured = h), Arg.Any<CancellationToken>());

        await _sut.RecordValueAsync(_watchId, ObjectIdentity, FieldName, "$120", 120.0, FieldType.Currency, Guid.NewGuid());

        captured.ShouldNotBeNull();
        captured.ChangeFromPrevious.ShouldBe(20.0);
        captured.PercentChangeFromPrevious.ShouldBe(20.0);
        captured.Direction.ShouldBe(ChangeDirection.Increased);
        captured.RunningMin.ShouldBe(90.0);
        captured.RunningMax.ShouldBe(120.0);
    }

    [Test]
    public async Task RecordValueAsync_Decrease_SetsDirectionDecreased()
    {
        var previous = CreateHistory(numericValue: 100.0, runningMin: 80.0, runningMax: 100.0);
        _repository.FindAsync(Arg.Any<Expression<Func<FieldValueHistory, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { previous });

        FieldValueHistory? captured = null;
        await _repository.InsertAsync(Arg.Do<FieldValueHistory>(h => captured = h), Arg.Any<CancellationToken>());

        await _sut.RecordValueAsync(_watchId, ObjectIdentity, FieldName, "$75", 75.0, FieldType.Currency, Guid.NewGuid());

        captured.ShouldNotBeNull();
        captured.Direction.ShouldBe(ChangeDirection.Decreased);
        captured.ChangeFromPrevious.ShouldBe(-25.0);
        captured.RunningMin.ShouldBe(75.0);
        captured.RunningMax.ShouldBe(100.0);
    }

    [Test]
    public async Task RecordValueAsync_SameValue_SetsDirectionUnchanged()
    {
        var previous = CreateHistory(numericValue: 50.0, runningMin: 50.0, runningMax: 50.0);
        _repository.FindAsync(Arg.Any<Expression<Func<FieldValueHistory, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { previous });

        FieldValueHistory? captured = null;
        await _repository.InsertAsync(Arg.Do<FieldValueHistory>(h => captured = h), Arg.Any<CancellationToken>());

        await _sut.RecordValueAsync(_watchId, ObjectIdentity, FieldName, "$50", 50.0, FieldType.Currency, Guid.NewGuid());

        captured.ShouldNotBeNull();
        captured.Direction.ShouldBe(ChangeDirection.Unchanged);
        captured.ChangeFromPrevious.ShouldBe(0.0);
        captured.PercentChangeFromPrevious.ShouldBe(0.0);
    }

    // ========== GetHistoryAsync ==========

    [Test]
    public async Task GetHistoryAsync_ReturnsChronologicalEntries()
    {
        var now = DateTime.UtcNow;
        var entries = new[]
        {
            CreateHistory(capturedAt: now.AddHours(-2), numericValue: 10),
            CreateHistory(capturedAt: now.AddHours(-1), numericValue: 20),
            CreateHistory(capturedAt: now, numericValue: 30)
        };
        _repository.FindAsync(Arg.Any<Expression<Func<FieldValueHistory, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(entries);

        var result = await _sut.GetHistoryAsync(_watchId, ObjectIdentity, FieldName);

        result.Count.ShouldBe(3);
        // Service returns descending order (most recent first)
        result[0].NumericValue.ShouldBe(30);
        result[1].NumericValue.ShouldBe(20);
        result[2].NumericValue.ShouldBe(10);
    }

    [Test]
    public async Task GetHistoryAsync_WithDateFilters_FiltersCorrectly()
    {
        var now = DateTime.UtcNow;
        var entries = new[]
        {
            CreateHistory(capturedAt: now.AddDays(-5), numericValue: 10),
            CreateHistory(capturedAt: now.AddDays(-2), numericValue: 20),
            CreateHistory(capturedAt: now, numericValue: 30)
        };
        _repository.FindAsync(Arg.Any<Expression<Func<FieldValueHistory, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(entries);

        var result = await _sut.GetHistoryAsync(_watchId, ObjectIdentity, FieldName,
            from: now.AddDays(-3), to: now.AddDays(-1));

        result.Count.ShouldBe(1);
        result[0].NumericValue.ShouldBe(20);
    }

    [Test]
    public async Task GetHistoryAsync_WithLimit_ReturnsLimitedResults()
    {
        var now = DateTime.UtcNow;
        var entries = new[]
        {
            CreateHistory(capturedAt: now.AddHours(-3), numericValue: 10),
            CreateHistory(capturedAt: now.AddHours(-2), numericValue: 20),
            CreateHistory(capturedAt: now.AddHours(-1), numericValue: 30),
            CreateHistory(capturedAt: now, numericValue: 40)
        };
        _repository.FindAsync(Arg.Any<Expression<Func<FieldValueHistory, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(entries);

        var result = await _sut.GetHistoryAsync(_watchId, ObjectIdentity, FieldName, limit: 2);

        result.Count.ShouldBe(2);
        // Most recent first due to descending order
        result[0].NumericValue.ShouldBe(40);
        result[1].NumericValue.ShouldBe(30);
    }

    // ========== GetStatisticsAsync ==========

    [Test]
    public async Task GetStatisticsAsync_ReturnsCorrectStats()
    {
        var now = DateTime.UtcNow;
        var entries = new[]
        {
            CreateHistory(capturedAt: now.AddHours(-4), numericValue: 10),
            CreateHistory(capturedAt: now.AddHours(-3), numericValue: 20),
            CreateHistory(capturedAt: now.AddHours(-2), numericValue: 30),
            CreateHistory(capturedAt: now.AddHours(-1), numericValue: 40),
            CreateHistory(capturedAt: now, numericValue: 50)
        };
        _repository.FindAsync(Arg.Any<Expression<Func<FieldValueHistory, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(entries);

        var stats = await _sut.GetStatisticsAsync(_watchId, ObjectIdentity, FieldName);

        stats.ShouldNotBeNull();
        stats.Min.ShouldBe(10);
        stats.Max.ShouldBe(50);
        stats.Average.ShouldBe(30);
        stats.Count.ShouldBe(5);
        stats.FirstValue.ShouldBe(10);
        stats.LastValue.ShouldBe(50);
    }

    [Test]
    public async Task GetStatisticsAsync_CalculatesStandardDeviation()
    {
        var now = DateTime.UtcNow;
        // Values: 10, 20, 30 -> mean=20, variance=((100+0+100)/3)=66.67, stddev=~8.165
        var entries = new[]
        {
            CreateHistory(capturedAt: now.AddHours(-2), numericValue: 10),
            CreateHistory(capturedAt: now.AddHours(-1), numericValue: 20),
            CreateHistory(capturedAt: now, numericValue: 30)
        };
        _repository.FindAsync(Arg.Any<Expression<Func<FieldValueHistory, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(entries);

        var stats = await _sut.GetStatisticsAsync(_watchId, ObjectIdentity, FieldName);

        stats.ShouldNotBeNull();
        stats.Variance.ShouldNotBeNull();
        stats.Variance.Value.ShouldBe(200.0 / 3.0, tolerance: 0.01);
        stats.StandardDeviation.ShouldNotBeNull();
        stats.StandardDeviation.Value.ShouldBe(Math.Sqrt(200.0 / 3.0), tolerance: 0.01);
    }

    [Test]
    public async Task GetStatisticsAsync_WithUpwardTrend_ReturnsTrendUp()
    {
        var now = DateTime.UtcNow;
        // Recent values (descending): 50, 40, 30, 20, 10
        // recentAvg (first 3 desc) = (50+40+30)/3 = 40
        // olderAvg (skip 2 take 3 desc) = (30+20+10)/3 = 20
        // 40 > 20*1.01 → Up
        var entries = new[]
        {
            CreateHistory(capturedAt: now.AddHours(-4), numericValue: 10),
            CreateHistory(capturedAt: now.AddHours(-3), numericValue: 20),
            CreateHistory(capturedAt: now.AddHours(-2), numericValue: 30),
            CreateHistory(capturedAt: now.AddHours(-1), numericValue: 40),
            CreateHistory(capturedAt: now, numericValue: 50)
        };
        _repository.FindAsync(Arg.Any<Expression<Func<FieldValueHistory, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(entries);

        var stats = await _sut.GetStatisticsAsync(_watchId, ObjectIdentity, FieldName);

        stats.ShouldNotBeNull();
        stats.Trend.ShouldBe(TrendDirection.Up);
    }

    [Test]
    public async Task GetStatisticsAsync_WithDownwardTrend_ReturnsTrendDown()
    {
        var now = DateTime.UtcNow;
        // Recent values (descending): 10, 20, 30, 40, 50
        // recentAvg (first 3 desc) = (10+20+30)/3 = 20
        // olderAvg (skip 2 take 3 desc) = (30+40+50)/3 = 40
        // 20 < 40*0.99 → Down
        var entries = new[]
        {
            CreateHistory(capturedAt: now.AddHours(-4), numericValue: 50),
            CreateHistory(capturedAt: now.AddHours(-3), numericValue: 40),
            CreateHistory(capturedAt: now.AddHours(-2), numericValue: 30),
            CreateHistory(capturedAt: now.AddHours(-1), numericValue: 20),
            CreateHistory(capturedAt: now, numericValue: 10)
        };
        _repository.FindAsync(Arg.Any<Expression<Func<FieldValueHistory, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(entries);

        var stats = await _sut.GetStatisticsAsync(_watchId, ObjectIdentity, FieldName);

        stats.ShouldNotBeNull();
        stats.Trend.ShouldBe(TrendDirection.Down);
    }

    // ========== Empty history ==========

    [Test]
    public async Task GetStatisticsAsync_EmptyHistory_ReturnsNull()
    {
        _repository.FindAsync(Arg.Any<Expression<Func<FieldValueHistory, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<FieldValueHistory>());

        var stats = await _sut.GetStatisticsAsync(_watchId, ObjectIdentity, FieldName);

        stats.ShouldBeNull();
    }

    [Test]
    public async Task GetStatisticsAsync_OnlyNonNumericValues_ReturnsNull()
    {
        var entries = new[]
        {
            CreateHistory(numericValue: null, rawValue: "In Stock"),
            CreateHistory(numericValue: null, rawValue: "Out of Stock")
        };
        _repository.FindAsync(Arg.Any<Expression<Func<FieldValueHistory, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(entries);

        var stats = await _sut.GetStatisticsAsync(_watchId, ObjectIdentity, FieldName);

        stats.ShouldBeNull();
    }

    [Test]
    public async Task GetHistoryAsync_EmptyHistory_ReturnsEmptyList()
    {
        _repository.FindAsync(Arg.Any<Expression<Func<FieldValueHistory, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<FieldValueHistory>());

        var result = await _sut.GetHistoryAsync(_watchId, ObjectIdentity, FieldName);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task GetLatestValueAsync_EmptyHistory_ReturnsNull()
    {
        _repository.FindAsync(Arg.Any<Expression<Func<FieldValueHistory, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<FieldValueHistory>());

        var result = await _sut.GetLatestValueAsync(_watchId, ObjectIdentity, FieldName);

        result.ShouldBeNull();
    }

    // ========== EvaluateAlertsAsync ==========

    [Test]
    public async Task EvaluateAlertsAsync_ThresholdCrossed_TriggersAlert()
    {
        var threshold = new FieldAlertThreshold
        {
            Id = Guid.NewGuid(),
            Name = "Price drop alert",
            ConditionType = AlertConditionType.DropsBelow,
            Value = 50.0,
            IsEnabled = true,
            ImportanceOverride = ChangeImportance.High
        };
        var field = new SchemaField
        {
            Name = FieldName,
            Selector = ".price",
            AlertThresholds = [threshold]
        };

        _alertEvaluator.Evaluate(field, 60.0, 45.0, Arg.Any<double?>())
            .Returns(new AlertEvaluationResult
            {
                TriggeredThresholds =
                [
                    new TriggeredThreshold
                    {
                        Threshold = threshold,
                        Field = field,
                        Message = "Price dropped below 50.0",
                        OldValue = 60.0,
                        NewValue = 45.0
                    }
                ]
            });

        var alerts = await _sut.EvaluateAlertsAsync(field, 60.0, 45.0);

        alerts.Count.ShouldBe(1);
        alerts[0].ThresholdId.ShouldBe(threshold.Id);
        alerts[0].AlertName.ShouldBe("Price drop alert");
        alerts[0].ConditionType.ShouldBe(AlertConditionType.DropsBelow);
        alerts[0].ThresholdValue.ShouldBe(50.0);
        alerts[0].ActualValue.ShouldBe(45.0);
        alerts[0].Importance.ShouldBe(ChangeImportance.High);

        _alertEvaluator.Received(1).RecordTrigger(threshold);
    }

    [Test]
    public async Task EvaluateAlertsAsync_NoThresholds_ReturnsEmptyList()
    {
        var field = new SchemaField
        {
            Name = FieldName,
            Selector = ".price",
            AlertThresholds = []
        };

        var alerts = await _sut.EvaluateAlertsAsync(field, 100.0, 105.0);

        alerts.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAlertsAsync_NullNewValue_ReturnsEmptyList()
    {
        var field = new SchemaField
        {
            Name = FieldName,
            Selector = ".price",
            AlertThresholds =
            [
                new FieldAlertThreshold
                {
                    ConditionType = AlertConditionType.DropsBelow,
                    Value = 50.0,
                    IsEnabled = true
                }
            ]
        };

        var alerts = await _sut.EvaluateAlertsAsync(field, 100.0, null);

        alerts.ShouldBeEmpty();
    }

    // ========== Duplicate timestamps ==========

    [Test]
    public async Task RecordValueAsync_DuplicateTimestamp_StoresBothEntries()
    {
        _repository.FindAsync(Arg.Any<Expression<Func<FieldValueHistory, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<FieldValueHistory>());

        var snapshotId = Guid.NewGuid();
        await _sut.RecordValueAsync(_watchId, ObjectIdentity, FieldName, "$100", 100.0, FieldType.Currency, snapshotId);
        await _sut.RecordValueAsync(_watchId, ObjectIdentity, FieldName, "$101", 101.0, FieldType.Currency, snapshotId);

        await _repository.Received(2).InsertAsync(Arg.Any<FieldValueHistory>(), Arg.Any<CancellationToken>());
    }

    // ========== CalculateChange ==========

    [Test]
    public void CalculateChange_NullValues_ReturnsUnknownDirection()
    {
        var result = _sut.CalculateChange(null, 100.0);

        result.Direction.ShouldBe(ChangeDirection.Unknown);
        result.AbsoluteChange.ShouldBeNull();
    }

    [Test]
    public void CalculateChange_Increase_ReturnsCorrectMetrics()
    {
        var result = _sut.CalculateChange(100.0, 120.0);

        result.Direction.ShouldBe(ChangeDirection.Increased);
        result.AbsoluteChange.ShouldBe(20.0);
        result.PercentageChange.ShouldBe(20.0);
    }

    [Test]
    public void CalculateChange_Decrease_ReturnsCorrectMetrics()
    {
        var result = _sut.CalculateChange(100.0, 80.0);

        result.Direction.ShouldBe(ChangeDirection.Decreased);
        result.AbsoluteChange.ShouldBe(-20.0);
        result.PercentageChange.ShouldBe(-20.0);
    }

    [Test]
    public void CalculateChange_WithStatistics_DetectsOutlier()
    {
        var stats = new FieldStatistics
        {
            ObjectIdentity = ObjectIdentity,
            FieldName = FieldName,
            Min = 90,
            Max = 110,
            Average = 100,
            StandardDeviation = 5,
            Trend = TrendDirection.Stable
        };

        var result = _sut.CalculateChange(100.0, 115.0, stats);

        result.IsNewMaximum.ShouldBeTrue();
        result.IsOutlier.ShouldBeTrue();
        result.ZScore.ShouldNotBeNull();
        result.ZScore!.Value.ShouldBe(3.0, tolerance: 0.01);
    }

    // ========== Helpers ==========

    private FieldValueHistory CreateHistory(
        double? numericValue = null,
        string? rawValue = null,
        DateTime? capturedAt = null,
        double? runningMin = null,
        double? runningMax = null)
    {
        return new FieldValueHistory
        {
            WatchedSiteId = _watchId,
            ObjectIdentity = ObjectIdentity,
            FieldName = FieldName,
            NumericValue = numericValue,
            RawValue = rawValue,
            CapturedAt = capturedAt ?? DateTime.UtcNow,
            SnapshotId = Guid.NewGuid(),
            FieldType = FieldType.Currency,
            RunningMin = runningMin,
            RunningMax = runningMax
        };
    }
}
