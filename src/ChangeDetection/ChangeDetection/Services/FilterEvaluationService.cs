using System.Text.RegularExpressions;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services;

/// <summary>
/// Service for evaluating filter rules against extracted objects and changes.
/// Returns actions to apply based on matched conditions.
/// </summary>
public class FilterEvaluationService(
    ILogger<FilterEvaluationService> logger) : IFilterEvaluationService
{
    /// <inheritdoc />
    public Task<FilterEvaluationResult> EvaluateAsync(
        ObjectDiffResult diffResult,
        IReadOnlyList<FilterRule> rules,
        CancellationToken ct = default)
    {
        var result = new FilterEvaluationResult();

        if (rules.Count == 0)
        {
            return Task.FromResult(result);
        }

        // Sort rules by priority (descending)
        var sortedRules = rules
            .Where(r => r.IsEnabled)
            .OrderByDescending(r => r.Priority)
            .ToList();

        logger.LogDebug("Evaluating {RuleCount} filter rules against diff with {AddedCount} added, {RemovedCount} removed, {ModifiedCount} modified",
            sortedRules.Count, diffResult.AddedItems.Count, diffResult.RemovedItems.Count, diffResult.ModifiedItems.Count);

        // Skip evaluation for ambiguous items
        if (diffResult.HasAmbiguousIdentities)
        {
            logger.LogWarning("Skipping filter evaluation due to ambiguous identities");
            return Task.FromResult(result);
        }

        // Evaluate each object against all rules
        foreach (var addedItem in diffResult.AddedItems)
        {
            var actions = EvaluateObjectAgainstRules(addedItem, ChangeType.Added, sortedRules, result);
            if (actions.Count > 0)
            {
                result.FilteredObjects.Add(new FilteredObjectResult
                {
                    Object = addedItem,
                    MatchedRuleIds = actions.Select(a => a.RuleId).Distinct().ToList(),
                    AppliedActions = actions
                });
            }
        }

        foreach (var removedItem in diffResult.RemovedItems)
        {
            var actions = EvaluateObjectAgainstRules(removedItem, ChangeType.Removed, sortedRules, result);
            if (actions.Count > 0)
            {
                result.FilteredObjects.Add(new FilteredObjectResult
                {
                    Object = removedItem,
                    MatchedRuleIds = actions.Select(a => a.RuleId).Distinct().ToList(),
                    AppliedActions = actions
                });
            }
        }

        foreach (var modifiedItem in diffResult.ModifiedItems)
        {
            var actions = EvaluateObjectAgainstRules(modifiedItem.CurrentObject, ChangeType.Modified, sortedRules, result);
            if (actions.Count > 0)
            {
                result.FilteredObjects.Add(new FilteredObjectResult
                {
                    Object = modifiedItem.CurrentObject,
                    MatchedRuleIds = actions.Select(a => a.RuleId).Distinct().ToList(),
                    AppliedActions = actions
                });
            }
        }

        // Aggregate results
        result.Actions = result.FilteredObjects
            .SelectMany(f => f.AppliedActions)
            .ToList();

        result.SuppressNotification = result.Actions
            .Any(a => a.Action.Type == FilterActionType.SuppressNotification);

        result.TagsToAdd = result.Actions
            .Where(a => a.Action.Type == FilterActionType.AddTag)
            .Select(a => a.Action.Parameters.GetValueOrDefault("tag"))
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct()
            .ToList()!;

        result.RouteToChannels = result.Actions
            .Where(a => a.Action.Type == FilterActionType.RouteToChannel)
            .Select(a => a.Action.Parameters.GetValueOrDefault("channel"))
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct()
            .ToList()!;

        // Get highest importance override
        var importanceActions = result.Actions
            .Where(a => a.Action.Type == FilterActionType.SetImportance)
            .Select(a => ParseImportance(a.Action.Parameters.GetValueOrDefault("level")))
            .Where(i => i.HasValue)
            .ToList();

        if (importanceActions.Count > 0)
        {
            result.ImportanceOverride = importanceActions.Max();
        }

        result.ObjectsRequiringReview = result.Actions
            .Where(a => a.Action.Type == FilterActionType.RequireReview)
            .Select(a => result.FilteredObjects.FirstOrDefault(f => 
                f.AppliedActions.Contains(a))?.Object)
            .Where(o => o != null)
            .ToList()!;

        result.HighlightedObjects = result.Actions
            .Where(a => a.Action.Type == FilterActionType.Highlight)
            .Select(a => result.FilteredObjects.FirstOrDefault(f => 
                f.AppliedActions.Contains(a))?.Object)
            .Where(o => o != null)
            .ToList()!;

        logger.LogInformation(
            "Filter evaluation complete: {MatchedCount} objects matched, suppress={Suppress}, tags={TagCount}, channels={ChannelCount}",
            result.FilteredObjects.Count,
            result.SuppressNotification,
            result.TagsToAdd.Count,
            result.RouteToChannels.Count);

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<List<AppliedFilterAction>> EvaluateObjectAsync(
        ExtractedObject obj,
        ChangeType changeType,
        IReadOnlyList<FilterRule> rules,
        CancellationToken ct = default)
    {
        var result = new FilterEvaluationResult();
        var sortedRules = rules
            .Where(r => r.IsEnabled)
            .OrderByDescending(r => r.Priority)
            .ToList();

        var actions = EvaluateObjectAgainstRules(obj, changeType, sortedRules, result);
        return Task.FromResult(actions);
    }

    private List<AppliedFilterAction> EvaluateObjectAgainstRules(
        ExtractedObject obj,
        ChangeType changeType,
        List<FilterRule> sortedRules,
        FilterEvaluationResult aggregateResult)
    {
        var appliedActions = new List<AppliedFilterAction>();

        foreach (var rule in sortedRules)
        {
            if (EvaluateConditions(obj, changeType, rule.Conditions, rule.Logic))
            {
                logger.LogDebug("Rule '{RuleName}' matched object with identity '{Identity}'",
                    rule.Name, obj.IdentityKey);

                foreach (var action in rule.Actions)
                {
                    appliedActions.Add(new AppliedFilterAction
                    {
                        RuleId = rule.Id,
                        RuleName = rule.Name,
                        Action = action
                    });
                }

                if (rule.StopProcessing)
                {
                    break;
                }
            }
        }

        return appliedActions;
    }

    private bool EvaluateConditions(
        ExtractedObject obj,
        ChangeType changeType,
        List<FilterCondition> conditions,
        FilterLogic logic)
    {
        if (conditions.Count == 0)
        {
            return false;
        }

        var results = conditions.Select(c => EvaluateCondition(obj, changeType, c)).ToList();

        var result = logic == FilterLogic.And
            ? results.All(r => r)
            : results.Any(r => r);

        return result;
    }

    private bool EvaluateCondition(
        ExtractedObject obj,
        ChangeType changeType,
        FilterCondition condition)
    {
        string? fieldValue;

        // Handle special field names
        if (condition.FieldName == "$changeType")
        {
            fieldValue = changeType.ToString();
        }
        else
        {
            fieldValue = obj.Fields.GetValueOrDefault(condition.FieldName);
        }

        var result = EvaluateOperator(fieldValue, condition.Operator, condition.Value);

        return condition.Negate ? !result : result;
    }

    private static bool EvaluateOperator(string? fieldValue, FilterOperator op, string? conditionValue)
    {
        switch (op)
        {
            case FilterOperator.IsEmpty:
                return string.IsNullOrWhiteSpace(fieldValue);

            case FilterOperator.IsNotEmpty:
                return !string.IsNullOrWhiteSpace(fieldValue);

            case FilterOperator.Equals:
                return string.Equals(fieldValue, conditionValue, StringComparison.OrdinalIgnoreCase);

            case FilterOperator.Contains:
                return fieldValue?.Contains(conditionValue ?? "", StringComparison.OrdinalIgnoreCase) ?? false;

            case FilterOperator.StartsWith:
                return fieldValue?.StartsWith(conditionValue ?? "", StringComparison.OrdinalIgnoreCase) ?? false;

            case FilterOperator.EndsWith:
                return fieldValue?.EndsWith(conditionValue ?? "", StringComparison.OrdinalIgnoreCase) ?? false;

            case FilterOperator.Regex:
                if (string.IsNullOrEmpty(conditionValue) || string.IsNullOrEmpty(fieldValue))
                    return false;
                try
                {
                    return Regex.IsMatch(fieldValue, conditionValue, RegexOptions.IgnoreCase);
                }
                catch
                {
                    return false;
                }

            case FilterOperator.GreaterThan:
                return CompareValues(fieldValue, conditionValue) > 0;

            case FilterOperator.LessThan:
                return CompareValues(fieldValue, conditionValue) < 0;

            default:
                return false;
        }
    }

    private static int CompareValues(string? left, string? right)
    {
        // Try numeric comparison first
        if (double.TryParse(left, out var leftNum) && double.TryParse(right, out var rightNum))
        {
            return leftNum.CompareTo(rightNum);
        }

        // Try date comparison
        if (DateTime.TryParse(left, out var leftDate) && DateTime.TryParse(right, out var rightDate))
        {
            return leftDate.CompareTo(rightDate);
        }

        // Fall back to string comparison
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static ChangeImportance? ParseImportance(string? level)
    {
        return level?.ToLowerInvariant() switch
        {
            "low" => ChangeImportance.Low,
            "medium" => ChangeImportance.Medium,
            "high" => ChangeImportance.High,
            "critical" => ChangeImportance.Critical,
            _ => null
        };
    }
}
