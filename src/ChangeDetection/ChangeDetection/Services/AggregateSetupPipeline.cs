using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services;

/// <summary>
/// Orchestrates the creation of an aggregate watch group by calling the existing
/// IWatchSetupPipeline once per URL, then aligning schemas across sites and
/// suggesting aggregation functions. Pure overlay — never modifies existing pipelines.
/// </summary>
public class AggregateSetupPipeline(
    IWatchSetupPipeline watchSetupPipeline,
    IWatchGroupService groupService,
    IWatchService watchService,
    ILogger<AggregateSetupPipeline> logger) : IAggregateSetupPipeline
{
    public async IAsyncEnumerable<AggregateSetupProgress> SetupGroupStreamingAsync(
        AggregateSetupRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var totalUrls = request.Urls.Count;
        logger.LogInformation("Starting aggregate setup for {Count} URLs: {Intent}", totalUrls, request.UserIntent);

        yield return new AggregateSetupProgress
        {
            Stage = AggregateSetupStage.Started,
            Message = $"Setting up group for {totalUrls} sites...",
            TotalCount = totalUrls
        };

        // Create the group first
        var group = await groupService.CreateGroupAsync(new WatchGroupCreateRequest
        {
            Name = request.GroupName ?? request.UserIntent,
            Description = $"Aggregate watch: {request.UserIntent}",
            UserIntent = request.UserIntent
        }, ct);

        var watchIds = new List<Guid>();
        var failedUrls = new List<string>();
        var schemas = new List<(Guid WatchId, string Url, ExtractionSchema? Schema)>();

        // Set up each watch sequentially via existing pipeline
        for (var i = 0; i < totalUrls; i++)
        {
            var url = request.Urls[i];

            yield return new AggregateSetupProgress
            {
                Stage = AggregateSetupStage.SettingUpWatch,
                Message = $"Setting up watch {i + 1}/{totalUrls}...",
                Url = url,
                CompletedCount = i,
                TotalCount = totalUrls
            };

            // Process the URL and collect progress result
            AggregateSetupProgress progressResult = null;

            try
            {
                var input = $"{request.UserIntent} {url}";
                var result = await watchSetupPipeline.ProcessAsync(input, request.PipelineOptions, ct);

                if (result.IsSuccess && result.FinalConfiguration != null)
                {
                    // Create the watch from pipeline result
                    var watchId = await CreateWatchFromPipelineResultAsync(result, group.Id, ct);
                    watchIds.Add(watchId);
                    schemas.Add((watchId, url, result.FinalConfiguration.Schema));

                    progressResult = new AggregateSetupProgress
                    {
                        Stage = AggregateSetupStage.WatchSetupComplete,
                        Message = $"Watch {i + 1}/{totalUrls} ready: {url}",
                        Url = url,
                        CompletedCount = i + 1,
                        TotalCount = totalUrls,
                        Confidence = result.FinalConfiguration.Confidence
                    };
                }
                else
                {
                    failedUrls.Add(url);
                    logger.LogWarning("Pipeline failed for URL {Url}: {Error}", url, result.ErrorMessage);

                    progressResult = new AggregateSetupProgress
                    {
                        Stage = AggregateSetupStage.WatchSetupFailed,
                        Message = $"Failed to set up {url}: {result.ErrorMessage}",
                        Url = url,
                        CompletedCount = i + 1,
                        TotalCount = totalUrls
                    };
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedUrls.Add(url);
                logger.LogError(ex, "Exception during pipeline for URL {Url}", url);

                progressResult = new AggregateSetupProgress
                {
                    Stage = AggregateSetupStage.WatchSetupFailed,
                    Message = $"Error setting up {url}: {ex.Message}",
                    Url = url,
                    CompletedCount = i + 1,
                    TotalCount = totalUrls
                };
            }

            // Yield the result outside the try-catch
            if (progressResult != null)
            {
                yield return progressResult;
            }
        }

        if (watchIds.Count == 0)
        {
            yield return new AggregateSetupProgress
            {
                Stage = AggregateSetupStage.Failed,
                Message = "No watches could be set up. Group created but empty."
            };
            yield break;
        }

        // Align schemas across sites
        yield return new AggregateSetupProgress
        {
            Stage = AggregateSetupStage.AligningSchemas,
            Message = "Analyzing schemas across sites...",
            CompletedCount = watchIds.Count,
            TotalCount = totalUrls
        };

        var suggestions = AlignSchemasAndSuggestAggregation(schemas, request.FieldHint);

        // Apply suggestions to the group
        if (suggestions.Count > 0)
        {
            var updatedGroup = await groupService.GetByIdAsync(group.Id, ct);
            if (updatedGroup != null)
            {
                updatedGroup.AggregateFields = suggestions.Select(s => new AggregateFieldConfig
                {
                    FieldName = s.FieldName,
                    Function = Enum.TryParse<AggregateFunction>(s.SuggestedFunction, true, out var fn)
                        ? fn : AggregateFunction.Min,
                    DisplayLabel = s.FieldName,
                    IsPrimary = s == suggestions[0]
                }).ToList();

                await groupService.UpdateGroupAsync(updatedGroup, ct);
            }
        }

        yield return new AggregateSetupProgress
        {
            Stage = AggregateSetupStage.SuggestingAggregation,
            Message = $"Configured {suggestions.Count} aggregate field(s)",
            CompletedCount = watchIds.Count,
            TotalCount = totalUrls,
            Confidence = suggestions.Count > 0 ? suggestions.Average(s => s.Confidence) : 0
        };

        yield return new AggregateSetupProgress
        {
            Stage = AggregateSetupStage.Complete,
            Message = $"Group ready with {watchIds.Count} watches ({failedUrls.Count} failed)",
            CompletedCount = watchIds.Count,
            TotalCount = totalUrls
        };

        logger.LogInformation(
            "Aggregate setup complete: group {GroupId}, {WatchCount} watches, {FailedCount} failed",
            group.Id, watchIds.Count, failedUrls.Count);
    }

    public async Task<AggregateSetupResult> SetupGroupAsync(
        AggregateSetupRequest request,
        CancellationToken ct = default)
    {
        AggregateSetupResult? result = null;
        var watchIds = new List<Guid>();
        var failedUrls = new List<Guid>();
        var suggestions = new List<AggregateFieldSuggestion>();

        await foreach (var progress in SetupGroupStreamingAsync(request, ct))
        {
            if (progress.Stage == AggregateSetupStage.Complete || progress.Stage == AggregateSetupStage.Failed)
            {
                result = new AggregateSetupResult
                {
                    IsSuccess = progress.Stage == AggregateSetupStage.Complete,
                    ErrorMessage = progress.Stage == AggregateSetupStage.Failed ? progress.Message : null,
                    WatchIds = watchIds,
                    FailedUrls = failedUrls,
                    SuggestedFields = suggestions
                };
            }
        }

        return result ?? new AggregateSetupResult { IsSuccess = false, ErrorMessage = "Pipeline did not complete" };
    }

    private async Task<Guid> CreateWatchFromPipelineResultAsync(
        PipelineResult result, Guid groupId, CancellationToken ct)
    {
        var config = result.FinalConfiguration!;
        var watch = await watchService.CreateWatchAsync(new CreateWatchRequest
        {
            Url = config.Url,
            Name = config.Name,
            CssSelector = config.CssSelector,
            UseJavaScript = config.UseJavaScript,
            CheckInterval = config.CheckInterval ?? TimeSpan.FromHours(1),
            Tags = config.Tags
        }, ct);

        // Link watch to group
        await groupService.AddWatchToGroupAsync(groupId, watch.Id, ct);
        return watch.Id;
    }

    /// <summary>
    /// Aligns schemas across sites by finding common field names and suggesting
    /// aggregation functions. This is a deterministic heuristic for when LLM is unavailable;
    /// the LLM-based schema matching agent can override this.
    /// </summary>
    private static List<AggregateFieldSuggestion> AlignSchemasAndSuggestAggregation(
        List<(Guid WatchId, string Url, ExtractionSchema? Schema)> schemas,
        string? fieldHint)
    {
        var suggestions = new List<AggregateFieldSuggestion>();
        var allFields = schemas
            .Where(s => s.Schema?.Fields != null)
            .SelectMany(s => s.Schema!.Fields.Select(f => f.Name))
            .GroupBy(name => name.ToLowerInvariant())
            .OrderByDescending(g => g.Count())
            .ToList();

        // Prioritize field hint if provided
        if (!string.IsNullOrEmpty(fieldHint))
        {
            var hintLower = fieldHint.ToLowerInvariant();
            var matchedField = allFields.FirstOrDefault(g => g.Key.Contains(hintLower));
            if (matchedField != null)
            {
                suggestions.Add(new AggregateFieldSuggestion
                {
                    FieldName = matchedField.First(),
                    SuggestedFunction = SuggestFunction(matchedField.Key),
                    Reasoning = $"Matched field hint '{fieldHint}'",
                    Confidence = 0.9f
                });
            }
        }

        // Add common fields that appear in multiple schemas
        foreach (var fieldGroup in allFields.Where(g => g.Count() >= 2))
        {
            if (suggestions.Any(s => s.FieldName.Equals(fieldGroup.First(), StringComparison.OrdinalIgnoreCase)))
                continue;

            suggestions.Add(new AggregateFieldSuggestion
            {
                FieldName = fieldGroup.First(),
                SuggestedFunction = SuggestFunction(fieldGroup.Key),
                Reasoning = $"Found in {fieldGroup.Count()}/{schemas.Count} sites",
                Confidence = (float)fieldGroup.Count() / schemas.Count
            });
        }

        return suggestions;
    }

    private static string SuggestFunction(string fieldName)
    {
        var lower = fieldName.ToLowerInvariant();
        if (lower.Contains("price") || lower.Contains("cost"))
            return "Min";
        if (lower.Contains("stock") || lower.Contains("quantity") || lower.Contains("count"))
            return "Sum";
        if (lower.Contains("rating") || lower.Contains("score"))
            return "Average";
        return "Min";
    }
}
