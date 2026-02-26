using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Shared.Dtos;
using Microsoft.AspNetCore.OutputCaching;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChangeDetection.Endpoints;

/// <summary>
/// API endpoints for change history.
/// </summary>
public static class ChangeEndpoints
{
    public static RouteGroupBuilder MapChangeEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", GetAllChanges)
            .WithName("GetAllChanges")
            .Produces<List<ChangeListItemDto>>()
            .CacheOutput(policy => policy.Expire(TimeSpan.FromSeconds(5)).Tag("changes").SetVaryByQuery("watchId", "limit").SetVaryByHeader("Remote-User"));

        group.MapGet("/{id}", GetChangeById)
            .WithName("GetChangeById")
            .Produces<ChangeDetailDto>()
            .Produces(404)
            .CacheOutput(policy => policy.Expire(TimeSpan.FromSeconds(30)).Tag("changes").SetVaryByRouteValue("id").SetVaryByHeader("Remote-User"));

        group.MapPost("/{id}/viewed", MarkAsViewed)
            .WithName("MarkChangeAsViewed")
            .Produces(204)
            .Produces(404);

        group.MapPost("/{id}/feedback", SubmitFeedback)
            .WithName("SubmitChangeFeedback")
            .Produces(204)
            .Produces(404);

        group.MapGet("/quality/{watchId}", GetQualityMetrics)
            .WithName("GetQualityMetrics")
            .Produces<QualityMetricsDto>()
            .Produces(404);

        group.MapGet("/quality/{watchId}/recommendation", GetThresholdRecommendation)
            .WithName("GetThresholdRecommendation")
            .Produces<TrustRecommendation>()
            .Produces(204)
            .Produces(404);

        group.MapGet("/unviewed/count", GetUnviewedCount)
            .WithName("GetUnviewedCount")
            .Produces<int>()
            .CacheOutput(policy => policy.Expire(TimeSpan.FromSeconds(5)).Tag("changes").SetVaryByHeader("Remote-User"));

        group.MapGet("/{watchId}/field-history/{objectIdentity}/{fieldName}", GetFieldHistory)
            .WithName("GetFieldHistory")
            .Produces<FieldHistoryDto>()
            .Produces(404);

        return group;
    }

    private static async Task<IResult> GetAllChanges(
        IRepository<ChangeEvent> eventRepo,
        IRepository<WatchedSite> watchRepo,
        string? watchId,
        int? limit,
        CancellationToken ct)
    {
        IEnumerable<ChangeEvent> events;
        
        if (!string.IsNullOrEmpty(watchId) && Guid.TryParse(watchId, out var guidWatchId))
        {
            events = await eventRepo.FindAsync(e => e.WatchedSiteId == guidWatchId, ct);
        }
        else
        {
            events = await eventRepo.GetAllAsync(ct);
        }
        
        IEnumerable<ChangeEvent> orderedEvents = events.OrderByDescending(e => e.DetectedAt);
        
        if (limit.HasValue && limit.Value > 0)
        {
            orderedEvents = orderedEvents.Take(limit.Value);
        }

        var watches = (await watchRepo.GetAllAsync(ct)).ToDictionary(w => w.Id);
        
        var dtos = orderedEvents.Select(e =>
        {
            watches.TryGetValue(e.WatchedSiteId, out var watch);
            var dto = new ChangeListItemDto
            {
                Id = e.Id.ToString(),
                WatchId = e.WatchedSiteId.ToString(),
                WatchTitle = watch?.Name,
                DetectedAt = e.DetectedAt,
                Summary = e.DiffSummary ?? "Changes detected",
                Importance = e.Importance.ToString(),
                LinesAdded = e.LinesAdded,
                LinesRemoved = e.LinesRemoved,
                IsViewed = e.IsViewed,
                IsNotified = e.IsNotified,
                HasObjectDiff = e.ObjectsDiff != null
            };
            
            if (e.ObjectsDiff != null)
            {
                dto.ObjectsAdded = e.ObjectsDiff.AddedItems.Count;
                dto.ObjectsRemoved = e.ObjectsDiff.RemovedItems.Count;
                dto.ObjectsModified = e.ObjectsDiff.ModifiedItems.Count;
            }
            
            return dto;
        }).ToList();

        return Results.Ok(dtos);
    }

    private static async Task<IResult> GetChangeById(
        string id,
        IRepository<ChangeEvent> eventRepo,
        IRepository<ChangeSnapshot> snapshotRepo,
        IRepository<WatchedSite> watchRepo,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid ID");

        var change = await eventRepo.GetByIdAsync(guidId, ct);
        if (change == null)
            return Results.NotFound();

        var watch = await watchRepo.GetByIdAsync(change.WatchedSiteId, ct);
        var previousSnapshot = await snapshotRepo.GetByIdAsync(change.PreviousSnapshotId, ct);
        var currentSnapshot = await snapshotRepo.GetByIdAsync(change.CurrentSnapshotId, ct);

        // Generate plain text diff from HTML or use empty string
        var diffText = change.DiffHtml?.Replace("<ins>", "+").Replace("</ins>", "")
                                       .Replace("<del>", "-").Replace("</del>", "")
                                       .Replace("<br>", "\n").Replace("<br/>", "\n")
                      ?? "";

        var dto = new ChangeDetailDto
        {
            Id = change.Id.ToString(),
            WatchId = change.WatchedSiteId.ToString(),
            WatchTitle = watch?.Name,
            WatchUrl = watch?.Url,
            DetectedAt = change.DetectedAt,
            Summary = change.DiffSummary ?? "Changes detected",
            DiffText = diffText,
            DiffHtml = change.DiffHtml,
            Importance = change.Importance.ToString(),
            LinesAdded = change.LinesAdded,
            LinesRemoved = change.LinesRemoved,
            IsViewed = change.IsViewed,
            HasObjectDiff = change.ObjectsDiff != null,
            PreviousSnapshot = previousSnapshot != null ? new SnapshotInfoDto
            {
                Id = previousSnapshot.Id.ToString(),
                CapturedAt = previousSnapshot.CapturedAt,
                Content = previousSnapshot.Content,
                ScreenshotPath = previousSnapshot.ScreenshotPath,
                ElementScreenshotPath = previousSnapshot.ElementScreenshotPath,
                ElementBoundingBox = ParseElementBoundingBox(previousSnapshot.ElementBoundingBoxJson)
            } : null,
            CurrentSnapshot = currentSnapshot != null ? new SnapshotInfoDto
            {
                Id = currentSnapshot.Id.ToString(),
                CapturedAt = currentSnapshot.CapturedAt,
                Content = currentSnapshot.Content,
                ScreenshotPath = currentSnapshot.ScreenshotPath,
                ElementScreenshotPath = currentSnapshot.ElementScreenshotPath,
                ElementBoundingBox = ParseElementBoundingBox(currentSnapshot.ElementBoundingBoxJson)
            } : null
        };

        // Add object diff data if available
        if (change.ObjectsDiff != null && watch?.Schema != null)
        {
            dto.Schema = MapToSchemaDto(watch.Schema);
            dto.ObjectDiff = MapToObjectDiffDto(change.ObjectsDiff, watch.Schema);
        }

        return Results.Ok(dto);
    }

    private static ElementBoundingBoxDto? ParseElementBoundingBox(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new ElementBoundingBoxDto
            {
                X = root.GetProperty("x").GetDouble(),
                Y = root.GetProperty("y").GetDouble(),
                Width = root.GetProperty("width").GetDouble(),
                Height = root.GetProperty("height").GetDouble()
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IResult> GetFieldHistory(
        string watchId,
        string objectIdentity,
        string fieldName,
        IRepository<ChangeSnapshot> snapshotRepo,
        IRepository<WatchedSite> watchRepo,
        int? limit,
        CancellationToken ct)
    {
        if (!Guid.TryParse(watchId, out var guidWatchId))
            return Results.BadRequest("Invalid watch ID");

        var watch = await watchRepo.GetByIdAsync(guidWatchId, ct);
        if (watch?.Schema == null)
            return Results.NotFound("Watch or schema not found");

        var field = watch.Schema.Fields.FirstOrDefault(f => f.Name == fieldName);
        if (field == null)
            return Results.NotFound("Field not found");

        // Get snapshots for this watch
        var snapshots = (await snapshotRepo.FindAsync(s => s.WatchedSiteId == guidWatchId, ct))
            .Where(s => !string.IsNullOrEmpty(s.ExtractedObjectsJson))
            .OrderByDescending(s => s.CapturedAt)
            .Take(limit ?? 100)
            .ToList();

        var dataPoints = new List<FieldHistoryPointDto>();
        var decodedIdentity = Uri.UnescapeDataString(objectIdentity);

        foreach (var snapshot in snapshots.OrderBy(s => s.CapturedAt))
        {
            try
            {
                var objects = JsonSerializer.Deserialize<List<ExtractedObject>>(
                    snapshot.ExtractedObjectsJson!,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                var targetObject = objects?.FirstOrDefault(o => o.IdentityKey == decodedIdentity);
                if (targetObject?.Fields.TryGetValue(fieldName, out var value) == true)
                {
                    var numericValue = ParseNumericValue(value, field.Type);
                    dataPoints.Add(new FieldHistoryPointDto
                    {
                        CapturedAt = snapshot.CapturedAt,
                        Value = value,
                        NumericValue = numericValue,
                        FormattedValue = FormatFieldValue(value, field)
                    });
                }
            }
            catch
            {
                // Skip snapshots with invalid JSON
            }
        }

        var numericPoints = dataPoints.Where(p => p.NumericValue.HasValue).ToList();
        var result = new FieldHistoryDto
        {
            ObjectIdentity = decodedIdentity,
            FieldName = fieldName,
            FieldType = field.Type.ToString(),
            CurrencyCode = field.CurrencyCode,
            Unit = field.Unit,
            DataPoints = dataPoints
        };

        if (numericPoints.Count > 0)
        {
            result.MinValue = numericPoints.Min(p => p.NumericValue!.Value);
            result.MaxValue = numericPoints.Max(p => p.NumericValue!.Value);
            result.AverageValue = numericPoints.Average(p => p.NumericValue!.Value);

            if (numericPoints.Count >= 2)
            {
                var first = numericPoints.First().NumericValue!.Value;
                var last = numericPoints.Last().NumericValue!.Value;
                result.Trend = last > first ? "up" : last < first ? "down" : "stable";
            }
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> MarkAsViewed(
        string id,
        IRepository<ChangeEvent> eventRepo,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guidId))
            return Results.BadRequest("Invalid ID");

        var change = await eventRepo.GetByIdAsync(guidId, ct);
        if (change == null)
            return Results.NotFound();

        change.IsViewed = true;
        await eventRepo.UpdateAsync(change, ct);
        
        return Results.NoContent();
    }

    private static async Task<IResult> GetUnviewedCount(
        IRepository<ChangeEvent> eventRepo,
        CancellationToken ct)
    {
        var count = await eventRepo.CountAsync(e => !e.IsViewed, ct);
        return Results.Ok(count);
    }

    // ========== Mapping Helpers ==========

    private static ExtractionSchemaDto MapToSchemaDto(ExtractionSchema schema)
    {
        return new ExtractionSchemaDto
        {
            ItemSelector = schema.ItemSelector,
            Fields = schema.Fields.Select(f => new SchemaFieldDto
            {
                Name = f.Name,
                Type = f.Type.ToString(),
                Selector = f.Selector,
                IsRequired = f.IsRequired,
                IsIdentityField = f.IsIdentityField,
                SampleValue = f.SampleValue,
                Confidence = f.Confidence,
                CurrencyCode = f.CurrencyCode,
                DecimalPlaces = f.DecimalPlaces,
                FormatString = f.FormatString,
                TrackHistory = f.TrackHistory,
                Unit = f.Unit,
                AllowedValues = f.AllowedValues
            }).ToList(),
            IdentityFieldNames = schema.IdentityFieldNames,
            Version = schema.Version,
            DiffSettings = new ObjectDiffSettingsDto
            {
                Granularity = schema.DiffSettings.Granularity.ToString(),
                EnableImportanceScoring = schema.DiffSettings.EnableImportanceScoring,
                DefaultImportance = schema.DiffSettings.DefaultImportance.ToString()
            }
        };
    }

    private static ObjectDiffDetailDto MapToObjectDiffDto(ObjectDiffResult diff, ExtractionSchema schema)
    {
        var fieldLookup = schema.Fields.ToDictionary(f => f.Name);

        return new ObjectDiffDetailDto
        {
            AddedObjects = diff.AddedItems.Select(o => MapToExtractedObjectDto(o, schema)).ToList(),
            RemovedObjects = diff.RemovedItems.Select(o => MapToExtractedObjectDto(o, schema)).ToList(),
            ModifiedObjects = diff.ModifiedItems.Select(m => MapToModificationDto(m, fieldLookup)).ToList(),
            HasAmbiguousIdentities = diff.HasAmbiguousIdentities,
            AmbiguityDetails = diff.AmbiguityDetails,
            TotalCurrentObjects = diff.AddedItems.Count + diff.ModifiedItems.Count,
            TotalPreviousObjects = diff.RemovedItems.Count + diff.ModifiedItems.Count
        };
    }

    private static ExtractedObjectDetailDto MapToExtractedObjectDto(ExtractedObject obj, ExtractionSchema schema)
    {
        var fieldLookup = schema.Fields.ToDictionary(f => f.Name);
        var identityParts = schema.IdentityFieldNames
            .Select(name => obj.Fields.GetValueOrDefault(name))
            .Where(v => v != null);

        return new ExtractedObjectDetailDto
        {
            IdentityKey = obj.IdentityKey,
            DisplayLabel = string.Join(" - ", identityParts),
            Index = obj.Index,
            Fields = obj.Fields.Select(kvp =>
            {
                var field = fieldLookup.GetValueOrDefault(kvp.Key);
                return new FieldValueDto
                {
                    Name = kvp.Key,
                    Value = kvp.Value,
                    Type = field?.Type.ToString() ?? "String",
                    FormattedValue = field != null ? FormatFieldValue(kvp.Value, field) : kvp.Value,
                    NumericValue = field != null ? ParseNumericValue(kvp.Value, field.Type) : null
                };
            }).ToList()
        };
    }

    private static ObjectModificationDto MapToModificationDto(
        ObjectModification mod,
        Dictionary<string, SchemaField> fieldLookup)
    {
        return new ObjectModificationDto
        {
            IdentityKey = mod.IdentityKey,
            DisplayLabel = mod.IdentityKey,
            FieldChanges = mod.FieldChanges.Select(fc =>
            {
                var field = fieldLookup.GetValueOrDefault(fc.FieldName);
                var fieldType = field?.Type ?? FieldType.String;

                var oldNumeric = ParseNumericValue(fc.OldValue, fieldType);
                var newNumeric = ParseNumericValue(fc.NewValue, fieldType);

                double? numericChange = null;
                double? percentageChange = null;
                bool? isIncrease = null;

                if (oldNumeric.HasValue && newNumeric.HasValue)
                {
                    numericChange = newNumeric.Value - oldNumeric.Value;
                    isIncrease = numericChange > 0;

                    if (oldNumeric.Value != 0)
                    {
                        percentageChange = (numericChange.Value / oldNumeric.Value) * 100;
                    }
                }

                return new FieldChangeDetailDto
                {
                    FieldName = fc.FieldName,
                    FieldType = fieldType.ToString(),
                    OldValue = fc.OldValue,
                    NewValue = fc.NewValue,
                    OldFormattedValue = field != null ? FormatFieldValue(fc.OldValue, field) : fc.OldValue,
                    NewFormattedValue = field != null ? FormatFieldValue(fc.NewValue, field) : fc.NewValue,
                    OldNumericValue = oldNumeric,
                    NewNumericValue = newNumeric,
                    NumericChange = numericChange,
                    PercentageChange = percentageChange,
                    IsIncrease = isIncrease,
                    CurrencyCode = field?.CurrencyCode,
                    Unit = field?.Unit,
                    Importance = fc.LlmImportance?.ToString(),
                    ImportanceReason = fc.ImportanceReason
                };
            }).ToList(),
            Importance = mod.FieldChanges.FirstOrDefault()?.LlmImportance?.ToString()
        };
    }

    private static double? ParseNumericValue(string? value, FieldType fieldType)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        // Handle currency/percentage types by stripping symbols
        var cleanValue = fieldType switch
        {
            FieldType.Currency => StripCurrencySymbols(value),
            FieldType.Percentage => value.TrimEnd('%', ' '),
            _ => value
        };

        if (double.TryParse(cleanValue, out var result))
            return result;

        return null;
    }

    private static string StripCurrencySymbols(string value)
    {
        // Remove common currency symbols and formatting
        return Regex.Replace(value, @"[\$\€\£\¥\₹\,\s]", "").Trim();
    }

    private static string FormatFieldValue(string? value, SchemaField field)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? "";

        var numericValue = ParseNumericValue(value, field.Type);

        return field.Type switch
        {
            FieldType.Currency when numericValue.HasValue =>
                $"{field.CurrencyCode ?? "$"}{numericValue.Value:N2}",
            FieldType.Percentage when numericValue.HasValue =>
                $"{numericValue.Value:N1}%",
            FieldType.Number when numericValue.HasValue && !string.IsNullOrEmpty(field.Unit) =>
                $"{numericValue.Value:N} {field.Unit}",
            _ => value
        };
    }

    private static async Task<IResult> SubmitFeedback(
        Guid id,
        FeedbackRequest request,
        IRepository<ChangeEvent> eventRepo,
        IOutputCacheStore cacheStore,
        CancellationToken ct)
    {
        var change = await eventRepo.GetByIdAsync(id, ct);
        if (change is null)
            return Results.NotFound();

        change.Feedback = request.Feedback;
        change.FeedbackAt = DateTime.UtcNow;
        change.FeedbackNote = request.Note;
        await eventRepo.UpdateAsync(change, ct);
        await cacheStore.EvictByTagAsync("changes", ct);

        return Results.NoContent();
    }

    private static async Task<IResult> GetQualityMetrics(
        Guid watchId,
        IRepository<ChangeEvent> eventRepo,
        IRepository<WatchedSite> watchRepo,
        CancellationToken ct)
    {
        var watch = await watchRepo.GetByIdAsync(watchId, ct);
        if (watch is null)
            return Results.NotFound();

        var events = (await eventRepo.FindAsync(e => e.WatchedSiteId == watchId, ct)).ToList();
        var withFeedback = events.Where(e => e.Feedback != UserFeedback.None).ToList();

        var helpful = withFeedback.Count(e => e.Feedback == UserFeedback.Helpful);
        var falsePositive = withFeedback.Count(e => e.Feedback == UserFeedback.FalsePositive);
        var irrelevant = withFeedback.Count(e => e.Feedback == UserFeedback.Irrelevant);
        var missed = withFeedback.Count(e => e.Feedback == UserFeedback.Missed);

        var truePositives = helpful;
        var falsePositives = falsePositive + irrelevant;
        var precision = truePositives + falsePositives > 0
            ? (double)truePositives / (truePositives + falsePositives)
            : (double?)null;

        var recall = truePositives + missed > 0
            ? (double)truePositives / (truePositives + missed)
            : (double?)null;

        return Results.Ok(new QualityMetricsDto
        {
            WatchId = watchId,
            TotalEvents = events.Count,
            EventsWithFeedback = withFeedback.Count,
            Helpful = helpful,
            FalsePositive = falsePositive,
            Irrelevant = irrelevant,
            Missed = missed,
            Precision = precision,
            Recall = recall,
            AverageRelevance = events.Where(e => e.RelevanceScore.HasValue)
                .Select(e => (double)e.RelevanceScore!.Value).DefaultIfEmpty().Average(),
            AverageConfidence = events.Where(e => e.AnalysisConfidence.HasValue)
                .Select(e => (double)e.AnalysisConfidence!.Value).DefaultIfEmpty().Average()
        });
    }

    private static async Task<IResult> GetThresholdRecommendation(
        Guid watchId,
        IRepository<ChangeEvent> eventRepo,
        IRepository<WatchedSite> watchRepo,
        ITrustAutopilot trustAutopilot,
        CancellationToken ct)
    {
        var watch = await watchRepo.GetByIdAsync(watchId, ct);
        if (watch is null)
            return Results.NotFound();

        var events = (await eventRepo.FindAsync(e => e.WatchedSiteId == watchId, ct)).ToList();
        var currentThreshold = watch.AnalysisSettings.MinRelevanceForNotification ?? 0.5f;

        var recommendation = trustAutopilot.ComputeRecommendation(events, currentThreshold);
        return recommendation is not null
            ? Results.Ok(recommendation)
            : Results.NoContent();
    }
}

public record FeedbackRequest(UserFeedback Feedback, string? Note = null);

public class QualityMetricsDto
{
    public Guid WatchId { get; set; }
    public int TotalEvents { get; set; }
    public int EventsWithFeedback { get; set; }
    public int Helpful { get; set; }
    public int FalsePositive { get; set; }
    public int Irrelevant { get; set; }
    public int Missed { get; set; }
    public double? Precision { get; set; }
    public double? Recall { get; set; }
    public double AverageRelevance { get; set; }
    public double AverageConfidence { get; set; }
}
