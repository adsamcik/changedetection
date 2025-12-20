using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Evaluates alert thresholds against value changes.
/// Handles cooldown periods, one-time alerts, and trigger counting.
/// </summary>
public interface IAlertThresholdEvaluator
{
    /// <summary>
    /// Evaluates all thresholds for a field against a value change.
    /// </summary>
    /// <param name="field">The schema field with threshold configurations.</param>
    /// <param name="oldValue">The previous value (null if first observation).</param>
    /// <param name="newValue">The new/current value.</param>
    /// <param name="baselineValue">Optional baseline value for percentage calculations.</param>
    /// <returns>Result containing any triggered thresholds.</returns>
    AlertEvaluationResult Evaluate(
        SchemaField field,
        double? oldValue,
        double newValue,
        double? baselineValue = null);

    /// <summary>
    /// Evaluates thresholds for a stock status change.
    /// </summary>
    /// <param name="field">The schema field (for threshold configuration).</param>
    /// <param name="oldStatus">The previous stock status.</param>
    /// <param name="newStatus">The new stock status.</param>
    /// <returns>Result containing any triggered thresholds.</returns>
    AlertEvaluationResult EvaluateStockChange(
        SchemaField field,
        StockStatus? oldStatus,
        StockStatus newStatus);

    /// <summary>
    /// Marks a threshold as triggered, updating LastTriggeredAt and TriggerCount.
    /// If OneTime is true, disables the threshold.
    /// </summary>
    void RecordTrigger(FieldAlertThreshold threshold);

    /// <summary>
    /// Checks if a threshold is ready to fire (not in cooldown, still enabled).
    /// </summary>
    bool CanTrigger(FieldAlertThreshold threshold);

    /// <summary>
    /// Resets a threshold's trigger state (re-enables if OneTime, clears cooldown).
    /// </summary>
    void ResetThreshold(FieldAlertThreshold threshold);
}
