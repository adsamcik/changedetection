using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services;

/// <summary>
/// Unit tests for AlertThresholdEvaluator.
/// Tests all AlertConditionType scenarios and cooldown/OneTime logic.
/// </summary>
public class AlertThresholdEvaluatorTests
{
    private readonly ILogger<AlertThresholdEvaluator> _logger;
    private readonly AlertThresholdEvaluator _evaluator;

    public AlertThresholdEvaluatorTests()
    {
        _logger = Substitute.For<ILogger<AlertThresholdEvaluator>>();
        _evaluator = new AlertThresholdEvaluator(_logger);
    }

    #region DropsBelow Tests

    [Test]
    public async Task Evaluate_DropsBelow_Triggers_WhenValueDropsBelowThreshold()
    {
        // Arrange
        var field = CreateField("Price", new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.DropsBelow,
            Value = 100
        });

        // Act - price drops from 150 to 80
        var result = _evaluator.Evaluate(field, 150, 80, null);

        // Assert
        result.HasTriggeredAlerts.ShouldBeTrue();
        result.TriggeredThresholds.ShouldHaveSingleItem();
        result.TriggeredThresholds[0].Message.ShouldContain("dropped below");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Evaluate_DropsBelow_DoesNotTrigger_WhenValueStaysAbove()
    {
        // Arrange
        var field = CreateField("Price", new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.DropsBelow,
            Value = 100
        });

        // Act - price stays above 100
        var result = _evaluator.Evaluate(field, 150, 120, null);

        // Assert
        result.HasTriggeredAlerts.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Evaluate_DropsBelow_DoesNotTrigger_WhenAlreadyBelow()
    {
        // Arrange
        var field = CreateField("Price", new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.DropsBelow,
            Value = 100
        });

        // Act - price was already below threshold
        var result = _evaluator.Evaluate(field, 80, 70, null);

        // Assert
        result.HasTriggeredAlerts.ShouldBeFalse();
        await Task.CompletedTask;
    }

    #endregion

    #region RisesAbove Tests

    [Test]
    public async Task Evaluate_RisesAbove_Triggers_WhenValueRisesAboveThreshold()
    {
        // Arrange
        var field = CreateField("Price", new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.RisesAbove,
            Value = 100
        });

        // Act - price rises from 80 to 120
        var result = _evaluator.Evaluate(field, 80, 120, null);

        // Assert
        result.HasTriggeredAlerts.ShouldBeTrue();
        result.TriggeredThresholds[0].Message.ShouldContain("rose above");
        await Task.CompletedTask;
    }

    #endregion

    #region DropsByPercent Tests

    [Test]
    public async Task Evaluate_DropsByPercent_Triggers_WhenDropExceedsThreshold()
    {
        // Arrange
        var field = CreateField("Price", new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.DropsByPercent,
            Value = 20 // 20% drop threshold
        });

        // Act - 30% drop (100 -> 70)
        var result = _evaluator.Evaluate(field, 100, 70, null);

        // Assert
        result.HasTriggeredAlerts.ShouldBeTrue();
        result.TriggeredThresholds[0].CalculatedChange.ShouldBe(-30);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Evaluate_DropsByPercent_DoesNotTrigger_WhenDropBelowThreshold()
    {
        // Arrange
        var field = CreateField("Price", new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.DropsByPercent,
            Value = 20 // 20% drop threshold
        });

        // Act - 10% drop (100 -> 90)
        var result = _evaluator.Evaluate(field, 100, 90, null);

        // Assert
        result.HasTriggeredAlerts.ShouldBeFalse();
        await Task.CompletedTask;
    }

    #endregion

    #region RisesByPercent Tests

    [Test]
    public async Task Evaluate_RisesByPercent_Triggers_WhenRiseExceedsThreshold()
    {
        // Arrange
        var field = CreateField("Price", new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.RisesByPercent,
            Value = 15
        });

        // Act - 25% rise (100 -> 125)
        var result = _evaluator.Evaluate(field, 100, 125, null);

        // Assert
        result.HasTriggeredAlerts.ShouldBeTrue();
        await Task.CompletedTask;
    }

    #endregion

    #region ChangesBy Tests

    [Test]
    public async Task Evaluate_ChangesBy_Triggers_ForAbsoluteChange()
    {
        // Arrange
        var field = CreateField("Stock", new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.ChangesBy,
            Value = 50
        });

        // Act - absolute change of 75
        var result = _evaluator.Evaluate(field, 100, 175, null);

        // Assert
        result.HasTriggeredAlerts.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Evaluate_ChangesBy_Triggers_ForNegativeChange()
    {
        // Arrange
        var field = CreateField("Stock", new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.ChangesBy,
            Value = 50
        });

        // Act - absolute change of -60
        var result = _evaluator.Evaluate(field, 100, 40, null);

        // Assert
        result.HasTriggeredAlerts.ShouldBeTrue();
        await Task.CompletedTask;
    }

    #endregion

    #region ChangesByPercent Tests

    [Test]
    public async Task Evaluate_ChangesByPercent_Triggers_ForLargeChange()
    {
        // Arrange
        var field = CreateField("Price", new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.ChangesByPercent,
            Value = 10
        });

        // Act - 15% change
        var result = _evaluator.Evaluate(field, 100, 115, null);

        // Assert
        result.HasTriggeredAlerts.ShouldBeTrue();
        await Task.CompletedTask;
    }

    #endregion

    #region EntersRange/ExitsRange Tests

    [Test]
    public async Task Evaluate_EntersRange_Triggers_WhenValueEntersRange()
    {
        // Arrange
        var field = CreateField("Price", new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.EntersRange,
            Value = 80,
            SecondaryValue = 120
        });

        // Act - enters range from outside
        var result = _evaluator.Evaluate(field, 50, 100, null);

        // Assert
        result.HasTriggeredAlerts.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Evaluate_ExitsRange_Triggers_WhenValueExitsRange()
    {
        // Arrange
        var field = CreateField("Price", new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.ExitsRange,
            Value = 80,
            SecondaryValue = 120
        });

        // Act - exits range
        var result = _evaluator.Evaluate(field, 100, 150, null);

        // Assert
        result.HasTriggeredAlerts.ShouldBeTrue();
        await Task.CompletedTask;
    }

    #endregion

    #region NewMinimum/NewMaximum Tests

    [Test]
    public async Task Evaluate_NewMinimum_Triggers_WhenNewHistoricalMin()
    {
        // Arrange
        var field = CreateField("Price", new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.NewMinimum
        });
        field.HistoricalMin = 50;

        // Act - new historical minimum
        var result = _evaluator.Evaluate(field, 60, 45, null);

        // Assert
        result.HasTriggeredAlerts.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Evaluate_NewMaximum_Triggers_WhenNewHistoricalMax()
    {
        // Arrange
        var field = CreateField("Price", new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.NewMaximum
        });
        field.HistoricalMax = 150;

        // Act - new historical maximum
        var result = _evaluator.Evaluate(field, 140, 160, null);

        // Assert
        result.HasTriggeredAlerts.ShouldBeTrue();
        await Task.CompletedTask;
    }

    #endregion

    #region TargetReached Tests

    [Test]
    public async Task Evaluate_TargetReached_Triggers_WhenPriceReachesTarget()
    {
        // Arrange
        var field = CreateField("Price", new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.TargetReached,
            Value = 50 // target price
        });

        // Act - price reaches target
        var result = _evaluator.Evaluate(field, 80, 45, null);

        // Assert
        result.HasTriggeredAlerts.ShouldBeTrue();
        await Task.CompletedTask;
    }

    #endregion

    #region Cooldown Tests

    [Test]
    public async Task Evaluate_WithCooldown_DoesNotTrigger_WhenInCooldownPeriod()
    {
        // Arrange
        var threshold = new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.DropsBelow,
            Value = 100,
            CooldownPeriod = TimeSpan.FromHours(1),
            LastTriggeredAt = DateTime.UtcNow.AddMinutes(-30) // Triggered 30 mins ago
        };
        var field = CreateField("Price", threshold);

        // Act
        var result = _evaluator.Evaluate(field, 150, 80, null);

        // Assert
        result.HasTriggeredAlerts.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Evaluate_WithCooldown_Triggers_WhenCooldownExpired()
    {
        // Arrange
        var threshold = new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.DropsBelow,
            Value = 100,
            CooldownPeriod = TimeSpan.FromHours(1),
            LastTriggeredAt = DateTime.UtcNow.AddHours(-2) // Triggered 2 hours ago
        };
        var field = CreateField("Price", threshold);

        // Act
        var result = _evaluator.Evaluate(field, 150, 80, null);

        // Assert
        result.HasTriggeredAlerts.ShouldBeTrue();
        await Task.CompletedTask;
    }

    #endregion

    #region OneTime Tests

    [Test]
    public async Task RecordTrigger_OneTime_DisablesThreshold()
    {
        // Arrange
        var threshold = new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.TargetReached,
            Value = 50,
            OneTime = true,
            IsEnabled = true
        };

        // Act
        _evaluator.RecordTrigger(threshold);

        // Assert
        threshold.IsEnabled.ShouldBeFalse();
        threshold.TriggerCount.ShouldBe(1);
        threshold.LastTriggeredAt.ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task RecordTrigger_NotOneTime_KeepsEnabled()
    {
        // Arrange
        var threshold = new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.DropsBelow,
            Value = 100,
            OneTime = false,
            IsEnabled = true
        };

        // Act
        _evaluator.RecordTrigger(threshold);

        // Assert
        threshold.IsEnabled.ShouldBeTrue();
        threshold.TriggerCount.ShouldBe(1);
        await Task.CompletedTask;
    }

    #endregion

    #region Stock Status Tests

    [Test]
    public async Task EvaluateStockChange_BackInStock_Triggers()
    {
        // Arrange
        var field = CreateField("Stock", new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.ChangesBy // Generic change
        });

        // Act - back in stock
        var result = _evaluator.EvaluateStockChange(field, StockStatus.OutOfStock, StockStatus.InStock);

        // Assert
        result.HasTriggeredAlerts.ShouldBeTrue();
        result.TriggeredThresholds[0].Message.ShouldContain("back in stock");
        await Task.CompletedTask;
    }

    [Test]
    public async Task EvaluateStockChange_OutOfStock_Triggers()
    {
        // Arrange
        var field = CreateField("Stock", new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.ChangesBy
        });

        // Act - went out of stock
        var result = _evaluator.EvaluateStockChange(field, StockStatus.InStock, StockStatus.OutOfStock);

        // Assert
        result.HasTriggeredAlerts.ShouldBeTrue();
        result.TriggeredThresholds[0].Message.ShouldContain("out of stock");
        await Task.CompletedTask;
    }

    [Test]
    public async Task EvaluateStockChange_NoChange_DoesNotTrigger()
    {
        // Arrange
        var field = CreateField("Stock", new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.ChangesBy
        });

        // Act - no change
        var result = _evaluator.EvaluateStockChange(field, StockStatus.InStock, StockStatus.InStock);

        // Assert
        result.HasTriggeredAlerts.ShouldBeFalse();
        await Task.CompletedTask;
    }

    #endregion

    #region Disabled Threshold Tests

    [Test]
    public async Task Evaluate_DisabledThreshold_IsSkipped()
    {
        // Arrange
        var field = CreateField("Price", new FieldAlertThreshold
        {
            ConditionType = AlertConditionType.DropsBelow,
            Value = 100,
            IsEnabled = false // Disabled
        });

        // Act
        var result = _evaluator.Evaluate(field, 150, 80, null);

        // Assert
        result.HasTriggeredAlerts.ShouldBeFalse();
        await Task.CompletedTask;
    }

    #endregion

    #region Helper Methods

    private static SchemaField CreateField(string name, FieldAlertThreshold threshold)
    {
        return new SchemaField
        {
            Name = name,
            Selector = ".test",
            AlertThresholds = [threshold]
        };
    }

    #endregion
}
