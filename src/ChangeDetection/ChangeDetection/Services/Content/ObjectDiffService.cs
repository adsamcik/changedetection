using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Content;

/// <summary>
/// Service for computing diffs between sets of extracted objects.
/// Supports configurable granularity and LLM-based importance scoring.
/// </summary>
public class ObjectDiffService(
    ILlmProviderChain llmChain,
    ILogger<ObjectDiffService> logger) : IObjectDiffService
{
    /// <inheritdoc />
    public Task<ObjectDiffResult> ComputeDiffAsync(
        IReadOnlyList<ExtractedObject> previousObjects,
        IReadOnlyList<ExtractedObject> currentObjects,
        ExtractionSchema schema,
        CancellationToken ct = default)
    {
        logger.LogDebug("Computing diff: {PrevCount} previous, {CurrCount} current objects",
            previousObjects.Count, currentObjects.Count);

        var result = new ObjectDiffResult();
        var granularity = schema.DiffSettings.Granularity;

        // Build lookup maps by identity key
        var previousByKey = previousObjects
            .Where(o => !string.IsNullOrEmpty(o.IdentityKey))
            .GroupBy(o => o.IdentityKey!)
            .ToDictionary(g => g.Key, g => g.ToList());

        var currentByKey = currentObjects
            .Where(o => !string.IsNullOrEmpty(o.IdentityKey))
            .GroupBy(o => o.IdentityKey!)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Check for ambiguous identities
        var ambiguousPrev = previousByKey.Where(kvp => kvp.Value.Count > 1).ToList();
        var ambiguousCurr = currentByKey.Where(kvp => kvp.Value.Count > 1).ToList();

        if (ambiguousPrev.Count > 0 || ambiguousCurr.Count > 0)
        {
            result.HasAmbiguousIdentities = true;
            foreach (var amb in ambiguousPrev)
            {
                result.AmbiguityDetails.Add(
                    $"Previous snapshot has {amb.Value.Count} objects with identity '{amb.Key}'");
            }
            foreach (var amb in ambiguousCurr)
            {
                result.AmbiguityDetails.Add(
                    $"Current snapshot has {amb.Value.Count} objects with identity '{amb.Key}'");
            }
        }

        // Find added objects (in current but not in previous)
        if (granularity != DiffGranularity.FieldLevel)
        {
            foreach (var kvp in currentByKey)
            {
                if (!previousByKey.ContainsKey(kvp.Key))
                {
                    result.AddedItems.AddRange(kvp.Value);
                }
            }
        }

        // Find removed objects (in previous but not in current)
        if (granularity != DiffGranularity.FieldLevel)
        {
            foreach (var kvp in previousByKey)
            {
                if (!currentByKey.ContainsKey(kvp.Key))
                {
                    result.RemovedItems.AddRange(kvp.Value);
                }
            }
        }

        // Find modified objects (in both, but with different fields)
        if (granularity != DiffGranularity.ItemsOnly)
        {
            foreach (var kvp in currentByKey)
            {
                if (previousByKey.TryGetValue(kvp.Key, out var prevList))
                {
                    // Use first object from each (ambiguous case is already flagged)
                    var prevObj = prevList[0];
                    var currObj = kvp.Value[0];

                    var modification = ComputeFieldChanges(prevObj, currObj, schema);
                    if (modification.FieldChanges.Count > 0)
                    {
                        result.ModifiedItems.Add(modification);
                    }
                }
            }
        }

        logger.LogInformation(
            "Diff computed: {Added} added, {Removed} removed, {Modified} modified, {Ambiguous} ambiguous",
            result.AddedItems.Count,
            result.RemovedItems.Count,
            result.ModifiedItems.Count,
            result.HasAmbiguousIdentities ? "yes" : "no");

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public async Task<ObjectDiffResult> ScoreImportanceAsync(
        ObjectDiffResult diffResult,
        ExtractionSchema schema,
        string? userIntent = null,
        CancellationToken ct = default)
    {
        if (!schema.DiffSettings.EnableImportanceScoring)
        {
            // Apply default importance to all changes
            var defaultImportance = schema.DiffSettings.DefaultImportance;

            foreach (var mod in diffResult.ModifiedItems)
            {
                foreach (var change in mod.FieldChanges)
                {
                    change.LlmImportance = defaultImportance;
                }
            }

            return diffResult;
        }

        // Use LLM to score importance
        if (!diffResult.HasChanges)
        {
            return diffResult;
        }

        try
        {
            var scoredResult = await ScoreWithLlmAsync(diffResult, schema, userIntent, ct);
            return scoredResult;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LLM importance scoring failed, using defaults");
            
            // Fall back to default importance
            foreach (var mod in diffResult.ModifiedItems)
            {
                foreach (var change in mod.FieldChanges)
                {
                    change.LlmImportance = schema.DiffSettings.DefaultImportance;
                }
            }

            return diffResult;
        }
    }

    private ObjectModification ComputeFieldChanges(
        ExtractedObject prevObj,
        ExtractedObject currObj,
        ExtractionSchema schema)
    {
        var modification = new ObjectModification
        {
            IdentityKey = currObj.IdentityKey ?? "",
            PreviousObject = prevObj,
            CurrentObject = currObj
        };

        // Get all field names from schema
        var fieldNames = schema.Fields.Select(f => f.Name).ToHashSet();

        // Also include any fields that exist in the objects but not schema
        fieldNames.UnionWith(prevObj.Fields.Keys);
        fieldNames.UnionWith(currObj.Fields.Keys);

        foreach (var fieldName in fieldNames)
        {
            var prevValue = prevObj.Fields.GetValueOrDefault(fieldName);
            var currValue = currObj.Fields.GetValueOrDefault(fieldName);

            // Normalize for comparison
            prevValue = NormalizeValue(prevValue);
            currValue = NormalizeValue(currValue);

            if (!string.Equals(prevValue, currValue, StringComparison.Ordinal))
            {
                modification.FieldChanges.Add(new FieldChange
                {
                    FieldName = fieldName,
                    OldValue = prevValue,
                    NewValue = currValue
                });
            }
        }

        return modification;
    }

    private static string? NormalizeValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        // Normalize whitespace
        return string.Join(" ", value.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task<ObjectDiffResult> ScoreWithLlmAsync(
        ObjectDiffResult diffResult,
        ExtractionSchema schema,
        string? userIntent,
        CancellationToken ct)
    {
        var systemPrompt = """
            You are a change importance analyzer. Score the importance of detected changes to objects.
            
            Importance levels:
            - Low: Minor cosmetic changes, typos, formatting
            - Medium: Notable updates that users might want to know about
            - High: Significant changes that definitely warrant attention
            - Critical: Urgent changes requiring immediate attention
            
            Consider:
            - Identity fields changing is usually more important
            - Date/time changes may indicate scheduling updates
            - Price changes are often important for product listings
            - New items may be more important than modifications
            
            Respond in JSON format:
            {
                "scoredChanges": [
                    {
                        "identityKey": "key of the changed item",
                        "fieldChanges": [
                            {
                                "fieldName": "name of changed field",
                                "importance": "Low|Medium|High|Critical",
                                "reason": "brief explanation"
                            }
                        ]
                    }
                ],
                "addedItemsImportance": "Low|Medium|High|Critical",
                "removedItemsImportance": "Low|Medium|High|Critical",
                "overallSummary": "brief summary of the changes"
            }
            """;

        var changesDescription = BuildChangesDescription(diffResult);

        var userPrompt = $"""
            Content type: {schema.Fields.FirstOrDefault()?.Name ?? "items"}
            {(string.IsNullOrEmpty(userIntent) ? "" : $"User's monitoring intent: {userIntent}")}
            
            Changes detected:
            {changesDescription}
            """;

        var response = await llmChain.ExecuteAsync(
            $"{systemPrompt}\n\nUser: {userPrompt}",
            new LlmRequestOptions
            {
                Temperature = 0.2f,
                MaxTokens = 1024,
                ExpectJson = true,
                UsageType = LlmUsageType.ImportanceScoring
            },
            ct);

        if (!response.IsSuccess)
        {
            logger.LogWarning("LLM importance scoring failed: {Error}", response.ErrorMessage);
            return diffResult;
        }

        try
        {
            var scoringResult = JsonSerializer.Deserialize<ImportanceScoringResult>(
                ExtractJson(response.Content ?? ""),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (scoringResult?.ScoredChanges != null)
            {
                var scoredByKey = scoringResult.ScoredChanges
                    .Where(s => s.IdentityKey != null)
                    .ToDictionary(s => s.IdentityKey!, s => s);

                foreach (var mod in diffResult.ModifiedItems)
                {
                    if (scoredByKey.TryGetValue(mod.IdentityKey, out var scored))
                    {
                        var fieldScores = scored.FieldChanges?
                            .Where(f => f.FieldName != null)
                            .ToDictionary(f => f.FieldName!, f => f)
                            ?? new Dictionary<string, FieldImportanceResult>();

                        foreach (var change in mod.FieldChanges)
                        {
                            if (fieldScores.TryGetValue(change.FieldName, out var fieldScore))
                            {
                                change.LlmImportance = ParseImportance(fieldScore.Importance);
                                change.ImportanceReason = fieldScore.Reason;
                            }
                        }
                    }
                }
            }

            return diffResult;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse importance scoring response");
            return diffResult;
        }
    }

    private static string BuildChangesDescription(ObjectDiffResult diffResult)
    {
        var lines = new List<string>();

        if (diffResult.AddedItems.Count > 0)
        {
            lines.Add($"Added {diffResult.AddedItems.Count} new items:");
            foreach (var item in diffResult.AddedItems.Take(5))
            {
                lines.Add($"  - {item.IdentityKey ?? $"Item {item.Index}"}");
            }
        }

        if (diffResult.RemovedItems.Count > 0)
        {
            lines.Add($"Removed {diffResult.RemovedItems.Count} items:");
            foreach (var item in diffResult.RemovedItems.Take(5))
            {
                lines.Add($"  - {item.IdentityKey ?? $"Item {item.Index}"}");
            }
        }

        if (diffResult.ModifiedItems.Count > 0)
        {
            lines.Add($"Modified {diffResult.ModifiedItems.Count} items:");
            foreach (var mod in diffResult.ModifiedItems.Take(5))
            {
                lines.Add($"  - {mod.IdentityKey}:");
                foreach (var change in mod.FieldChanges.Take(3))
                {
                    lines.Add($"    • {change.FieldName}: '{change.OldValue}' → '{change.NewValue}'");
                }
            }
        }

        return string.Join("\n", lines);
    }

    private static string ExtractJson(string content)
    {
        content = content.Trim();
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return content[start..(end + 1)];
        }
        return content;
    }

    private static ChangeImportance ParseImportance(string? importance)
    {
        return importance?.ToLowerInvariant() switch
        {
            "low" => ChangeImportance.Low,
            "medium" => ChangeImportance.Medium,
            "high" => ChangeImportance.High,
            "critical" => ChangeImportance.Critical,
            _ => ChangeImportance.Medium
        };
    }
}

// Internal classes for JSON deserialization
internal class ImportanceScoringResult
{
    public List<ScoredChangeResult>? ScoredChanges { get; set; }
    public string? AddedItemsImportance { get; set; }
    public string? RemovedItemsImportance { get; set; }
    public string? OverallSummary { get; set; }
}

internal class ScoredChangeResult
{
    public string? IdentityKey { get; set; }
    public List<FieldImportanceResult>? FieldChanges { get; set; }
}

internal class FieldImportanceResult
{
    public string? FieldName { get; set; }
    public string? Importance { get; set; }
    public string? Reason { get; set; }
}
