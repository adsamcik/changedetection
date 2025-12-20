using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services;

/// <summary>
/// Evaluates alert thresholds against value changes.
/// Handles cooldown periods, one-time alerts, and trigger counting.
/// </summary>
public class AlertThresholdEvaluator(ILogger<AlertThresholdEvaluator> logger) : IAlertThresholdEvaluator
{
    /// <inheritdoc/>
    public AlertEvaluationResult Evaluate(
        SchemaField field,
        double? oldValue,
        double newValue,
        double? baselineValue = null)
    {
        var result = new AlertEvaluationResult();
        var triggeredThresholds = new List<TriggeredThreshold>();
        ChangeImportance? highestImportance = null;

        foreach (var threshold in field.AlertThresholds)
        {
            if (!CanTrigger(threshold))
                continue;

            var (triggered, message) = EvaluateThreshold(threshold, oldValue, newValue, baselineValue, field);

            if (triggered)
            {
                logger.LogDebug(
                    "Threshold triggered for field {FieldName}: {Condition} (value: {NewValue})",
                    field.Name,
                    threshold.ConditionType,
                    newValue);

                var triggeredThreshold = new TriggeredThreshold
                {
                    Threshold = threshold,
                    Field = field,
                    Message = message,
                    OldValue = oldValue,
                    NewValue = newValue,
                    CalculatedChange = CalculateChange(oldValue, newValue, threshold.ConditionType)
                };

                triggeredThresholds.Add(triggeredThreshold);

                // Track highest importance
                var importance = threshold.ImportanceOverride ?? ChangeImportance.Medium;
                if (!highestImportance.HasValue || importance > highestImportance.Value)
                {
                    highestImportance = importance;
                }
            }
        }

        return new AlertEvaluationResult
        {
            TriggeredThresholds = triggeredThresholds,
            HighestImportance = highestImportance,
            CombinedMessage = triggeredThresholds.Count > 0
                ? string.Join("; ", triggeredThresholds.Select(t => t.Message))
                : null
        };
    }

    /// <inheritdoc/>
    public AlertEvaluationResult EvaluateStockChange(
        SchemaField field,
        StockStatus? oldStatus,
        StockStatus newStatus)
    {
        // Stock alerts are typically simpler - just notify on any change
        // or specifically on "back in stock" / "out of stock" transitions
        var triggeredThresholds = new List<TriggeredThreshold>();

        if (oldStatus != newStatus)
        {
            // Check for "back in stock" alert
            if (oldStatus is StockStatus.OutOfStock or StockStatus.Discontinued &&
                newStatus == StockStatus.InStock)
            {
                triggeredThresholds.Add(new TriggeredThreshold
                {
                    Threshold = new FieldAlertThreshold
                    {
                        Name = "Back in Stock",
                        ConditionType = AlertConditionType.TargetReached,
                        ImportanceOverride = ChangeImportance.High
                    },
                    Field = field,
                    Message = $"'{field.Name}' is now back in stock!",
                    OldValue = (double)oldStatus.GetValueOrDefault(),
                    NewValue = (double)newStatus
                });
            }
            // Check for "went out of stock" alert
            else if (oldStatus == StockStatus.InStock &&
                     newStatus is StockStatus.OutOfStock or StockStatus.Discontinued)
            {
                triggeredThresholds.Add(new TriggeredThreshold
                {
                    Threshold = new FieldAlertThreshold
                    {
                        Name = "Out of Stock",
                        ConditionType = AlertConditionType.DropsBelow,
                        ImportanceOverride = ChangeImportance.Medium
                    },
                    Field = field,
                    Message = $"'{field.Name}' is now out of stock",
                    OldValue = (double)oldStatus.GetValueOrDefault(),
                    NewValue = (double)newStatus
                });
            }
            else
            {
                // Generic stock status change
                triggeredThresholds.Add(new TriggeredThreshold
                {
                    Threshold = new FieldAlertThreshold
                    {
                        Name = "Stock Status Change",
                        ConditionType = AlertConditionType.ChangesBy,
                        ImportanceOverride = ChangeImportance.Low
                    },
                    Field = field,
                    Message = $"'{field.Name}' stock status changed from {oldStatus} to {newStatus}",
                    OldValue = oldStatus.HasValue ? (double)oldStatus.Value : null,
                    NewValue = (double)newStatus
                });
            }
        }

        return new AlertEvaluationResult
        {
            TriggeredThresholds = triggeredThresholds,
            HighestImportance = triggeredThresholds.Count > 0
                ? triggeredThresholds.Max(t => t.Threshold.ImportanceOverride ?? ChangeImportance.Low)
                : null,
            CombinedMessage = triggeredThresholds.Count > 0
                ? string.Join("; ", triggeredThresholds.Select(t => t.Message))
                : null
        };
    }

    /// <inheritdoc/>
    public void RecordTrigger(FieldAlertThreshold threshold)
    {
        threshold.LastTriggeredAt = DateTime.UtcNow;
        threshold.TriggerCount++;

        if (threshold.OneTime)
        {
            threshold.IsEnabled = false;
            logger.LogDebug("One-time threshold '{Name}' has been disabled after triggering", threshold.Name);
        }
    }

    /// <inheritdoc/>
    public bool CanTrigger(FieldAlertThreshold threshold)
    {
        if (!threshold.IsEnabled)
            return false;

        if (threshold.CooldownPeriod.HasValue && threshold.LastTriggeredAt.HasValue)
        {
            var cooldownEnds = threshold.LastTriggeredAt.Value + threshold.CooldownPeriod.Value;
            if (DateTime.UtcNow < cooldownEnds)
            {
                logger.LogDebug(
                    "Threshold '{Name}' is in cooldown until {CooldownEnds}",
                    threshold.Name,
                    cooldownEnds);
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public void ResetThreshold(FieldAlertThreshold threshold)
    {
        threshold.IsEnabled = true;
        threshold.LastTriggeredAt = null;
        threshold.TriggerCount = 0;
    }

    private (bool Triggered, string Message) EvaluateThreshold(
        FieldAlertThreshold threshold,
        double? oldValue,
        double newValue,
        double? baselineValue,
        SchemaField field)
    {
        var thresholdValue = threshold.Value;
        var secondaryValue = threshold.SecondaryValue;

        return threshold.ConditionType switch
        {
            AlertConditionType.DropsBelow =>
                (newValue < thresholdValue && (oldValue == null || oldValue >= thresholdValue),
                 $"'{field.Name}' dropped below {thresholdValue:N2} (now {newValue:N2})"),

            AlertConditionType.RisesAbove =>
                (newValue > thresholdValue && (oldValue == null || oldValue <= thresholdValue),
                 $"'{field.Name}' rose above {thresholdValue:N2} (now {newValue:N2})"),

            AlertConditionType.ChangesBy when oldValue.HasValue =>
                (Math.Abs(newValue - oldValue.Value) >= thresholdValue,
                 $"'{field.Name}' changed by {Math.Abs(newValue - oldValue.Value):N2} (threshold: {thresholdValue:N2})"),

            AlertConditionType.ChangesByPercent when oldValue.HasValue && oldValue.Value != 0 =>
                (Math.Abs((newValue - oldValue.Value) / oldValue.Value * 100) >= thresholdValue,
                 $"'{field.Name}' changed by {Math.Abs((newValue - oldValue.Value) / oldValue.Value * 100):N1}% (threshold: {thresholdValue:N1}%)"),

            AlertConditionType.DropsByPercent when oldValue.HasValue && oldValue.Value != 0 =>
                ((oldValue.Value - newValue) / oldValue.Value * 100 >= thresholdValue,
                 $"'{field.Name}' dropped by {(oldValue.Value - newValue) / oldValue.Value * 100:N1}% (threshold: {thresholdValue:N1}%)"),

            AlertConditionType.RisesByPercent when oldValue.HasValue && oldValue.Value != 0 =>
                ((newValue - oldValue.Value) / oldValue.Value * 100 >= thresholdValue,
                 $"'{field.Name}' rose by {(newValue - oldValue.Value) / oldValue.Value * 100:N1}% (threshold: {thresholdValue:N1}%)"),

            AlertConditionType.EntersRange when secondaryValue.HasValue =>
                (newValue >= thresholdValue && newValue <= secondaryValue.Value &&
                 (oldValue == null || oldValue < thresholdValue || oldValue > secondaryValue.Value),
                 $"'{field.Name}' entered range {thresholdValue:N2} - {secondaryValue:N2} (now {newValue:N2})"),

            AlertConditionType.ExitsRange when secondaryValue.HasValue =>
                ((newValue < thresholdValue || newValue > secondaryValue.Value) &&
                 oldValue.HasValue && oldValue >= thresholdValue && oldValue <= secondaryValue.Value,
                 $"'{field.Name}' exited range {thresholdValue:N2} - {secondaryValue:N2} (now {newValue:N2})"),

            AlertConditionType.NewMinimum when field.HistoricalMin.HasValue =>
                (newValue < field.HistoricalMin.Value,
                 $"'{field.Name}' reached new minimum: {newValue:N2} (previous min: {field.HistoricalMin:N2})"),

            AlertConditionType.NewMaximum when field.HistoricalMax.HasValue =>
                (newValue > field.HistoricalMax.Value,
                 $"'{field.Name}' reached new maximum: {newValue:N2} (previous max: {field.HistoricalMax:N2})"),

            AlertConditionType.ReturnsToBaseline when baselineValue.HasValue && baselineValue.Value != 0 =>
                (Math.Abs((newValue - baselineValue.Value) / baselineValue.Value * 100) <= thresholdValue &&
                 oldValue.HasValue && Math.Abs((oldValue.Value - baselineValue.Value) / baselineValue.Value * 100) > thresholdValue,
                 $"'{field.Name}' returned to within {thresholdValue:N1}% of baseline (now {newValue:N2}, baseline: {baselineValue:N2})"),

            AlertConditionType.TargetReached =>
                (newValue <= thresholdValue && (oldValue == null || oldValue > thresholdValue),
                 $"'{field.Name}' reached target of {thresholdValue:N2} (now {newValue:N2})"),

            _ => (false, string.Empty)
        };
    }

    private static double? CalculateChange(double? oldValue, double newValue, AlertConditionType conditionType)
    {
        if (!oldValue.HasValue)
            return null;

        return conditionType switch
        {
            AlertConditionType.ChangesByPercent or
            AlertConditionType.DropsByPercent or
            AlertConditionType.RisesByPercent when oldValue.Value != 0 =>
                (newValue - oldValue.Value) / oldValue.Value * 100,

            _ => newValue - oldValue.Value
        };
    }
}
