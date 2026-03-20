using System.Globalization;
using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.GroupWatch;
using ChangeDetection.Services.Pipeline;
using ChangeDetection.Shared.Dtos;

namespace ChangeDetection.Endpoints;

public static class WatchGroupEndpoints
{
    public static RouteGroupBuilder MapWatchGroupEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", GetAllGroups).WithName("GetAllGroups").Produces<List<WatchGroupListItemDto>>();
        group.MapGet("/{id}", GetGroupById).WithName("GetGroupById").Produces<WatchGroupDetailDto>().Produces(404);
        group.MapPost("/", CreateGroup).WithName("CreateGroup").Produces<WatchGroupDetailDto>(201);
        group.MapPut("/{id}", UpdateGroup).WithName("UpdateGroup").Produces<WatchGroupDetailDto>().Produces(404);
        group.MapDelete("/{id}", DeleteGroup).WithName("DeleteGroup").Produces(204).Produces(404);
        group.MapPost("/{id}/members/{watchId}", AddMember).WithName("AddGroupMember").Produces(204).Produces(404);
        group.MapDelete("/{id}/members/{watchId}", RemoveMember).WithName("RemoveGroupMember").Produces(204).Produces(404);
        group.MapGet("/{id}/health", GetHealth).WithName("GetGroupHealth").Produces<WatchGroupHealthDto>().Produces(404);
        group.MapGet("/{id}/aggregate", GetAggregate).WithName("GetGroupAggregate").Produces<AggregateSnapshotDto>().Produces(404);
        group.MapGet("/{id}/alerts/evaluate", EvaluateAlerts).WithName("EvaluateGroupAlerts").Produces<AggregateAlertResultDto>().Produces(404);
        group.MapGet("/{id}/results", GetGroupResults).WithName("GetGroupResults").Produces<GroupResultsDto>().Produces(404);
        group.MapGet("/{id}/suggestions", GetSuggestions).WithName("GetPortalSuggestions").Produces<List<PortalSuggestionDto>>().Produces(404);
        group.MapPost("/{id}/suggestions/{suggestionId}/accept", AcceptSuggestion).WithName("AcceptPortalSuggestion").Produces<PortalSuggestionAcceptResultDto>().Produces(404);
        group.MapPost("/{id}/suggestions/{suggestionId}/dismiss", DismissSuggestion).WithName("DismissPortalSuggestion").Produces(204).Produces(404);
        group.MapPut("/{id}/profile", UpdateProfile).WithName("UpdateGroupProfile").Produces<ProfileUpdateResult>().Produces(404).Produces(400);
        return group;
    }

    private static async Task<IResult> GetAllGroups(IWatchGroupService svc, CancellationToken ct)
    {
        var groups = await svc.GetAllAsync(ct);
        var dtos = new List<WatchGroupListItemDto>();
        foreach (var g in groups)
        {
            var members = await svc.GetGroupMembersAsync(g.Id, ct);
            var snap = await svc.ComputeAggregateAsync(g.Id, ct);
            dtos.Add(new WatchGroupListItemDto
            {
                Id = g.Id.ToString(), Name = g.Name, Description = g.Description, Icon = g.Icon,
                MemberCount = members.Count,
                PrimaryFields = snap.Fields
                    .Where(f => g.AggregateFields.Any(af => af.FieldName == f.FieldName && af.IsPrimary))
                    .Select(MapFieldValueDto).ToList(),
                ErrorCount = members.Count(m => m.Status == WatchStatus.Error),
                LastActivity = members.Count > 0 ? members.Max(m => m.LastChecked) : null,
                Tags = g.Tags
            });
        }
        return Results.Ok(dtos);
    }

    private static async Task<IResult> GetGroupById(Guid id, IWatchGroupService svc, CancellationToken ct)
    {
        var g = await svc.GetByIdAsync(id, ct);
        if (g is null) return Results.NotFound();
        var members = await svc.GetGroupMembersAsync(id, ct);
        var snap = await svc.ComputeAggregateAsync(id, ct);
        return Results.Ok(MapDetailDto(g, members, snap));
    }

    private static async Task<IResult> CreateGroup(WatchGroupCreateDto dto, IWatchGroupService svc, CancellationToken ct)
    {
        var g = await svc.CreateGroupAsync(new WatchGroupCreateRequest
        {
            Name = dto.Name, Description = dto.Description, Icon = dto.Icon, Tags = dto.Tags
        }, ct);
        return Results.Created($"/api/groups/{g.Id}", new WatchGroupDetailDto
        {
            Id = g.Id.ToString(), Name = g.Name, Description = g.Description, Icon = g.Icon,
            AggregateFields = [], AggregateAlerts = [], Members = [], Tags = g.Tags, CreatedAt = g.CreatedAt
        });
    }

    private static async Task<IResult> UpdateGroup(Guid id, WatchGroupUpdateDto dto, IWatchGroupService svc,
        IProfileFilterRuleGenerator filterRuleGen, IWatchService watchService, CancellationToken ct)
    {
        var g = await svc.GetByIdAsync(id, ct);
        if (g is null) return Results.NotFound();

        if (dto.Name is not null) g.Name = dto.Name;
        if (dto.Description is not null) g.Description = dto.Description;
        if (dto.Icon is not null) g.Icon = dto.Icon;
        if (dto.Tags is not null) g.Tags = dto.Tags;

        var profileChanged = false;
        if (dto.AnalysisProfileJson is not null)
        {
            if (dto.AnalysisProfileJson.Length > 65_536)
                return Results.BadRequest("AnalysisProfileJson exceeds maximum length of 65536 characters");
            try
            {
                using var doc = JsonDocument.Parse(dto.AnalysisProfileJson, new JsonDocumentOptions { MaxDepth = 10 });
                g.AnalysisProfileJson = dto.AnalysisProfileJson;
                profileChanged = true;
            }
            catch (JsonException)
            {
                return Results.BadRequest("AnalysisProfileJson is not valid JSON");
            }
        }

        if (dto.AggregateFields is not null)
            g.AggregateFields = dto.AggregateFields.Select(f => new AggregateFieldConfig
            {
                Id = Guid.TryParse(f.Id, out var fid) ? fid : Guid.NewGuid(),
                FieldName = f.FieldName,
                Function = Enum.TryParse<AggregateFunction>(f.Function, true, out var fn) ? fn : AggregateFunction.Min,
                DisplayLabel = f.DisplayLabel, IsPrimary = f.IsPrimary, Unit = f.Unit, CurrencyCode = f.CurrencyCode
            }).ToList();

        if (dto.AggregateAlerts is not null)
            g.AggregateAlerts = dto.AggregateAlerts.Select(a => new AggregateAlert
            {
                Id = Guid.TryParse(a.Id, out var aid) ? aid : Guid.NewGuid(),
                FieldName = a.FieldName,
                Function = Enum.TryParse<AggregateFunction>(a.Function, true, out var fn) ? fn : AggregateFunction.Min,
                ConditionType = Enum.TryParse<AlertConditionType>(a.ConditionType, true, out var cond) ? cond : AlertConditionType.DropsBelow,
                Value = a.Value, SecondaryValue = a.SecondaryValue, IsEnabled = a.IsEnabled, OneTime = a.OneTime,
                CooldownPeriod = TimeSpan.TryParse(a.CooldownPeriod, out var cd) ? cd : null,
                NotificationTemplate = a.NotificationTemplate,
                ImportanceOverride = Enum.TryParse<ChangeImportance>(a.ImportanceOverride, true, out var imp) ? imp : null
            }).ToList();

        await svc.UpdateGroupAsync(g, ct);

        // When profile changes, regenerate FilterRules for all member watches
        if (profileChanged && g.AnalysisProfileJson is not null)
        {
            var newRules = filterRuleGen.GenerateRules(g.AnalysisProfileJson);
            var memberWatches = await svc.GetGroupMembersAsync(id, ct);
            foreach (var member in memberWatches)
            {
                var watch = await watchService.GetByIdAsync(member.Id, ct);
                if (watch is null) continue;
                // Replace auto-generated rules (keep any user-defined rules)
                watch.FilterRules = watch.FilterRules
                    .Where(r => !r.Description?.StartsWith("Auto-generated") == true)
                    .Concat(newRules)
                    .ToList();
                await watchService.UpdateWatchAsync(watch, ct);
            }
        }

        var members = await svc.GetGroupMembersAsync(id, ct);
        var snap = await svc.ComputeAggregateAsync(id, ct);
        return Results.Ok(MapDetailDto(g, members, snap));
    }

    private static async Task<IResult> DeleteGroup(Guid id, IWatchGroupService svc, bool deleteWatches = false, CancellationToken ct = default)
    {
        var g = await svc.GetByIdAsync(id, ct);
        if (g is null) return Results.NotFound();
        await svc.DeleteGroupAsync(id, deleteWatches, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> AddMember(Guid id, Guid watchId, IWatchGroupService svc, CancellationToken ct)
    {
        try { await svc.AddWatchToGroupAsync(id, watchId, ct); return Results.NoContent(); }
        catch (InvalidOperationException ex) { return Results.NotFound(ex.Message); }
    }

    private static async Task<IResult> RemoveMember(Guid id, Guid watchId, IWatchGroupService svc, CancellationToken ct)
    {
        try { await svc.RemoveWatchFromGroupAsync(id, watchId, ct); return Results.NoContent(); }
        catch (InvalidOperationException ex) { return Results.NotFound(ex.Message); }
    }

    private static async Task<IResult> GetAggregate(Guid id, IWatchGroupService svc, CancellationToken ct)
    {
        var g = await svc.GetByIdAsync(id, ct);
        if (g is null) return Results.NotFound();
        return Results.Ok(MapSnapshotDto(await svc.ComputeAggregateAsync(id, ct)));
    }

    private static async Task<IResult> GetHealth(Guid id, IWatchGroupService svc, CancellationToken ct)
    {
        var g = await svc.GetByIdAsync(id, ct);
        if (g is null) return Results.NotFound();
        return Results.Ok(MapHealthDto(await svc.GetGroupHealthAsync(id, ct)));
    }

    private static async Task<IResult> EvaluateAlerts(Guid id, IWatchGroupService svc, CancellationToken ct)
    {
        var g = await svc.GetByIdAsync(id, ct);
        if (g is null) return Results.NotFound();
        var r = await svc.EvaluateAggregateAlertsAsync(id, ct);
        return Results.Ok(new AggregateAlertResultDto
        {
            GroupId = r.GroupId.ToString(), EvaluatedAt = r.EvaluatedAt,
            TriggeredAlerts = r.TriggeredAlerts.Select(t => new TriggeredAggregateAlertDto
            {
                AlertId = t.AlertId.ToString(), FieldName = t.FieldName,
                AggregatedValue = t.AggregatedValue, ThresholdValue = t.ThresholdValue,
                Message = t.Message, Importance = t.Importance.ToString()
            }).ToList()
        });
    }

    private static async Task<IResult> GetSuggestions(
        Guid id,
        IWatchGroupService groupSvc,
        IPortalSuggestionService suggestionSvc,
        CancellationToken ct)
    {
        var group = await groupSvc.GetByIdAsync(id, ct);
        if (group is null) return Results.NotFound();

        var suggestions = await suggestionSvc.GetPendingForGroupAsync(id, ct);
        return Results.Ok(suggestions.Select(MapSuggestionDto).ToList());
    }

    private static async Task<IResult> AcceptSuggestion(
        Guid id,
        Guid suggestionId,
        IWatchGroupService groupSvc,
        IPortalSuggestionService suggestionSvc,
        CancellationToken ct)
    {
        var group = await groupSvc.GetByIdAsync(id, ct);
        if (group is null) return Results.NotFound();

        var watch = await suggestionSvc.AcceptAsync(id, suggestionId, ct);
        if (watch is null) return Results.NotFound();

        return Results.Ok(new PortalSuggestionAcceptResultDto
        {
            SuggestionId = suggestionId.ToString(),
            WatchId = watch.Id.ToString(),
            Url = watch.Url
        });
    }

    private static async Task<IResult> DismissSuggestion(
        Guid id,
        Guid suggestionId,
        IWatchGroupService groupSvc,
        IPortalSuggestionService suggestionSvc,
        CancellationToken ct)
    {
        var group = await groupSvc.GetByIdAsync(id, ct);
        if (group is null) return Results.NotFound();

        var dismissed = await suggestionSvc.DismissAsync(id, suggestionId, ct);
        return dismissed ? Results.NoContent() : Results.NotFound();
    }

    // --- Mapping helpers ---

    private static WatchGroupDetailDto MapDetailDto(WatchGroup g, List<WatchedSite> members, AggregateSnapshot snap) => new()
    {
        Id = g.Id.ToString(), Name = g.Name, Description = g.Description, Icon = g.Icon,
        UserIntent = g.UserIntent, AnalysisProfileJson = g.AnalysisProfileJson,
        AggregateFields = g.AggregateFields.Select(f => new AggregateFieldConfigDto
        {
            Id = f.Id.ToString(), FieldName = f.FieldName, Function = f.Function.ToString(),
            DisplayLabel = f.DisplayLabel, IsPrimary = f.IsPrimary, Unit = f.Unit, CurrencyCode = f.CurrencyCode
        }).ToList(),
        AggregateAlerts = g.AggregateAlerts.Select(a => new AggregateAlertDto
        {
            Id = a.Id.ToString(), FieldName = a.FieldName, Function = a.Function.ToString(),
            ConditionType = a.ConditionType.ToString(), Value = a.Value, SecondaryValue = a.SecondaryValue,
            IsEnabled = a.IsEnabled, OneTime = a.OneTime, CooldownPeriod = a.CooldownPeriod?.ToString(),
            NotificationTemplate = a.NotificationTemplate, ImportanceOverride = a.ImportanceOverride?.ToString()
        }).ToList(),
        Members = members.Select(MapMemberDto).ToList(),
        LatestSnapshot = MapSnapshotDto(snap), Tags = g.Tags, CreatedAt = g.CreatedAt
    };

    private static AggregateFieldValueDto MapFieldValueDto(AggregateFieldValue f) => new()
    {
        FieldName = f.FieldName, Function = f.Function.ToString(), Value = f.AggregatedValue,
        FormattedValue = f.FormattedValue, BestSourceName = f.BestSourceName,
        PerSiteValues = f.PerSiteValues.Select(p => new PerSiteValueDto
        {
            WatchId = p.WatchId.ToString(), WatchName = p.WatchName,
            Value = p.Value, FormattedValue = p.FormattedValue,
            LastUpdated = p.LastUpdated, Status = p.Status.ToString()
        }).ToList()
    };

    private static PortalSuggestionDto MapSuggestionDto(PortalSuggestionEntity suggestion) => new()
    {
        Id = suggestion.Id.ToString(),
        Url = suggestion.Url,
        Domain = suggestion.Domain,
        DetectedPlatform = suggestion.DetectedPlatform,
        Reason = suggestion.Reason,
        SourceWatchId = suggestion.SourceWatchId.ToString(),
        CreatedAt = suggestion.CreatedAt
    };

    private static WatchGroupMemberDto MapMemberDto(WatchedSite w) => new()
    {
        WatchId = w.Id.ToString(), Name = w.Name ?? w.Url, Url = w.Url,
        Status = w.Status.ToString(), LastChecked = w.LastChecked, HasErrors = w.Status == WatchStatus.Error
    };

    private static AggregateSnapshotDto MapSnapshotDto(AggregateSnapshot s) => new()
    {
        GroupId = s.GroupId.ToString(), ComputedAt = s.ComputedAt,
        Fields = s.Fields.Select(MapFieldValueDto).ToList(),
        Members = s.Members.Select(m => new WatchGroupMemberDto
        {
            WatchId = m.WatchId.ToString(), Name = m.Name ?? "", Url = m.Url ?? "",
            Status = m.Status.ToString(), LastChecked = m.LastChecked, HasErrors = m.HasErrors
        }).ToList()
    };

    private static async Task<IResult> GetGroupResults(
        Guid id,
        IWatchGroupService groupSvc,
        IRepository<ChangeSnapshot> snapshotRepo,
        IRepository<ChangeEvent> eventRepo,
        IJobDeduplicationService deduplicationService,
        CancellationToken ct)
    {
        var group = await groupSvc.GetByIdAsync(id, ct);
        if (group is null) return Results.NotFound();

        var members = await groupSvc.GetGroupMembersAsync(id, ct);

        // Determine the most recent check time across the group
        var lastChecked = members
            .Where(m => m.LastChecked.HasValue)
            .Select(m => m.LastChecked!.Value)
            .DefaultIfEmpty()
            .Max();

        // Threshold: items first seen within 24h of the latest check are "new"
        var newThreshold = lastChecked != default
            ? lastChecked.AddHours(-24)
            : DateTime.UtcNow.AddHours(-24);

        var allItems = new List<GroupResultItemDto>();
        var healthyCount = members.Count(m => m.Status != WatchStatus.Error);

        // Well-known field names to extract into dedicated properties
        var titleFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "title", "name", "job_title", "jobtitle", "position", "role" };
        var urlFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "url", "link", "href", "apply_url", "apply_link" };
        var companyFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "company", "employer", "organization", "organisation" };
        var locationFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "location", "city", "region", "place" };
        var relevanceScoreFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "_relevanceScore", "relevanceScore" };

        foreach (var member in members)
        {
            // Get the latest snapshot with extracted objects
            var latestSnapshot = await snapshotRepo.FirstOrDefaultOrderedDescAsync(
                s => s.WatchedSiteId == member.Id,
                s => s.CapturedAt,
                ct);

            if (latestSnapshot?.ExtractedObjectsJson is null or "")
                continue;

            var objects = ParseExtractedObjects(latestSnapshot.ExtractedObjectsJson);
            if (objects is null or { Count: 0 }) continue;

            // Get relevance score from the most recent change event for this watch
            var latestEvent = await eventRepo.FirstOrDefaultOrderedDescAsync(
                e => e.WatchedSiteId == member.Id,
                e => e.DetectedAt,
                ct);

            var sourceName = member.Name ?? new Uri(member.Url, UriKind.RelativeOrAbsolute).Host;

            foreach (var obj in objects)
            {
                string? GetField(HashSet<string> candidates)
                {
                    foreach (var key in candidates)
                        if (obj.Fields.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val))
                            return val;
                    return null;
                }

                var title = GetField(titleFields) ?? obj.IdentityKey ?? $"Item #{obj.Index}";
                var url = GetField(urlFields);
                var company = GetField(companyFields) ?? DeriveCompanyFromWatchName(sourceName);
                var location = GetField(locationFields);

                // Collect remaining fields as extras
                var wellKnown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                wellKnown.UnionWith(titleFields);
                wellKnown.UnionWith(urlFields);
                wellKnown.UnionWith(companyFields);
                wellKnown.UnionWith(locationFields);
                wellKnown.UnionWith(relevanceScoreFields);

                var extras = new Dictionary<string, string>();
                foreach (var (key, val) in obj.Fields)
                {
                    if (!wellKnown.Contains(key) && !string.IsNullOrWhiteSpace(val))
                        extras[key] = val;
                }

                allItems.Add(new GroupResultItemDto
                {
                    Title = title,
                    Url = url,
                    Company = company,
                    Location = location,
                    Source = sourceName,
                    SourceWatchId = member.Id.ToString(),
                    Sources = string.IsNullOrWhiteSpace(url) ? new List<string>() : new List<string> { url },
                    SourceNames = new List<string> { sourceName },
                    SourceWatchIds = new List<string> { member.Id.ToString() },
                    RelevanceScore = TryGetPipelineRelevanceScore(obj) ?? latestEvent?.RelevanceScore,
                    FirstSeen = latestSnapshot.CapturedAt,
                    IsNew = latestSnapshot.CapturedAt >= newThreshold,
                    ExtraFields = extras
                });
            }
        }

        var deduped = deduplicationService.DeduplicateAcrossSources(allItems);

        return Results.Ok(new GroupResultsDto
        {
            GroupId = id.ToString(),
            GroupName = group.Name,
            GroupIcon = group.Icon,
            TotalWatches = members.Count,
            HealthyWatches = healthyCount,
            TotalItems = deduped.Count,
            NewItems = deduped.Count(i => i.IsNew),
            LastChecked = lastChecked != default ? lastChecked : null,
            Items = deduped
        });
    }

    /// <summary>
    /// Derives a company name from the watch name by stripping platform suffixes.
    /// E.g. "Again.bio (Teamtailor)" → "Again.bio", "Novo Nordisk careers" → "Novo Nordisk".
    /// Returns null if no meaningful company name can be extracted.
    /// </summary>
    private static string? DeriveCompanyFromWatchName(string watchName)
    {
        if (string.IsNullOrWhiteSpace(watchName))
            return null;

        var name = watchName.Trim();

        // Strip parenthesized platform suffix: "Company (Teamtailor)" → "Company"
        var parenIdx = name.LastIndexOf('(');
        if (parenIdx > 0)
            name = name[..parenIdx].Trim();

        // Strip common trailing keywords
        string[] suffixes = ["careers", "jobs", "career", "job openings", "vacancies", "hiring"];
        foreach (var suffix in suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length].TrimEnd(' ', '-', '–', '—', '|', ':');
            }
        }

        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// Parses extracted objects from snapshot JSON, handling both direct List&lt;ExtractedObject&gt;
    /// format and ListDiff wrapper format ({"items":[...],...}).
    /// </summary>
    private static List<ExtractedObject>? ParseExtractedObjects(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // ListDiff wrapper format: {"items":[...],"added":[...],"changed":false}
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                return ConvertJsonArrayToExtractedObjects(items);
            }

            // Direct array format
            if (root.ValueKind == JsonValueKind.Array)
            {
                // Try proper ExtractedObject format with Fields dict
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var typed = JsonSerializer.Deserialize<List<ExtractedObject>>(json, opts);
                if (typed is not null && typed.Any(o => o.Fields.Count > 0))
                    return typed;

                // Fallback: flat JSON objects
                return ConvertJsonArrayToExtractedObjects(root);
            }
        }
        catch
        {
            // Invalid JSON
        }

        return null;
    }

    private static List<ExtractedObject> ConvertJsonArrayToExtractedObjects(JsonElement array)
    {
        var objects = new List<ExtractedObject>();
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var eo = new ExtractedObject { Index = index++ };
            foreach (var prop in item.EnumerateObject())
            {
                eo.Fields[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.ToString();
            }
            objects.Add(eo);
        }
        return objects;
    }

    private static float? TryGetPipelineRelevanceScore(ExtractedObject obj)
    {
        foreach (var key in new[] { "_relevanceScore", "relevanceScore" })
        {
            if (!obj.Fields.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
                continue;

            if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
                return score;
        }

        return null;
    }

    private static WatchGroupHealthDto MapHealthDto(WatchGroupHealth health) => new()
    {
        GroupId = health.GroupId.ToString(),
        TotalWatches = health.TotalWatches,
        Healthy = health.Healthy,
        Degraded = health.Degraded,
        Errored = health.Errored,
        Watches = health.Watches.Select(w => new WatchHealthItemDto
        {
            Id = w.Id.ToString(),
            Name = w.Name,
            Url = w.Url,
            Status = w.Status.ToString(),
            LastChecked = w.LastChecked,
            ItemCount = w.ItemCount,
            PipelineBlocks = w.PipelineBlocks,
            ConsecutiveErrors = w.ConsecutiveErrors,
            LastError = w.LastError
        }).ToList()
    };

    private static async Task<IResult> UpdateProfile(
        Guid id,
        ProfileUpdateRequest request,
        IWatchGroupService groupSvc,
        IWatchService watchService,
        ILlmProviderChain llmChain,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("WatchGroupEndpoints");

        if (string.IsNullOrWhiteSpace(request.ProfileText))
            return Results.BadRequest("profileText is required");

        if (request.ProfileText.Length > 50_000)
            return Results.BadRequest("profileText exceeds maximum length of 50000 characters");

        var group = await groupSvc.GetByIdAsync(id, ct);
        if (group is null) return Results.NotFound();

        // Call LLM to extract structured keywords from the profile text
        var prompt = $$"""
            Given this candidate profile, extract relevance scoring keywords for job matching.

            Profile: "{{request.ProfileText}}"

            Return JSON only, no markdown fencing:
            {
              "positiveKeywords": [
                {"keyword": "example skill", "weight": 10},
                {"keyword": "another skill", "weight": 8}
              ],
              "negativeKeywords": [
                {"keyword": "director", "weight": -15},
                {"keyword": "senior manager", "weight": -10}
              ],
              "summary": "Brief one-line description of the candidate"
            }

            Rules:
            - positiveKeywords: skills, techniques, fields, qualifications, tools mentioned or implied. Weights 1-10 by importance.
            - negativeKeywords: seniority levels, roles, or requirements the candidate is unlikely to match. Weights -5 to -20.
            - Include both specific skills (e.g., "PCR", "flow cytometry") and broader terms (e.g., "molecular biology", "cell culture").
            - For a junior/mid candidate, add negative keywords for very senior roles (director, VP, principal investigator, 10+ years).
            - Return ONLY valid JSON, no explanation.
            """;

        var llmResponse = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.3f,
            MaxTokens = 2048,
            ExpectJson = true,
            UsageType = LlmUsageType.EntityExtraction
        }, ct);

        if (!llmResponse.IsSuccess || string.IsNullOrWhiteSpace(llmResponse.Content))
            return Results.Problem("Failed to extract keywords from profile text: " + (llmResponse.ErrorMessage ?? "empty response"));

        // Parse LLM response
        List<RelevanceKeyword> positiveKeywords;
        List<RelevanceKeyword> negativeKeywords;
        string? summary;
        try
        {
            var parsed = ParseProfileKeywords(llmResponse.Content);
            positiveKeywords = parsed.positive;
            negativeKeywords = parsed.negative;
            summary = parsed.summary;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse LLM keyword extraction response");
            return Results.Problem("Failed to parse keyword extraction response from LLM");
        }

        // Store the extracted profile on the group
        var profilePayload = new
        {
            profileText = request.ProfileText,
            positiveKeywords = positiveKeywords.Select(k => new { k.Keyword, k.Weight }),
            negativeKeywords = negativeKeywords.Select(k => new { k.Keyword, k.Weight }),
            summary,
            extractedAt = DateTime.UtcNow
        };
        group.AnalysisProfileJson = JsonSerializer.Serialize(profilePayload);
        group.UpdatedAt = DateTime.UtcNow;
        await groupSvc.UpdateGroupAsync(group, ct);

        // Patch all watches in the group with the new keywords
        var members = await groupSvc.GetGroupMembersAsync(id, ct);
        var watchesUpdated = 0;
        foreach (var member in members)
        {
            var watch = await watchService.GetByIdAsync(member.Id, ct);
            if (watch?.PipelineDefinitionJson is null) continue;

            var patched = PipelineKeywordPatcher.PatchPipelineJson(
                watch.PipelineDefinitionJson, positiveKeywords, negativeKeywords);

            if (patched is not null && patched != watch.PipelineDefinitionJson)
            {
                watch.PipelineDefinitionJson = patched;
                await watchService.UpdateWatchAsync(watch, ct);
                watchesUpdated++;
            }
        }

        logger.LogInformation(
            "Profile updated for group {GroupId}: {PositiveCount} positive, {NegativeCount} negative keywords, {WatchCount} watches patched",
            id, positiveKeywords.Count, negativeKeywords.Count, watchesUpdated);

        return Results.Ok(new ProfileUpdateResult
        {
            GroupId = id.ToString(),
            PositiveKeywords = positiveKeywords.Select(k => new KeywordDto { Keyword = k.Keyword, Weight = k.Weight }).ToList(),
            NegativeKeywords = negativeKeywords.Select(k => new KeywordDto { Keyword = k.Keyword, Weight = k.Weight }).ToList(),
            Summary = summary,
            WatchesUpdated = watchesUpdated
        });
    }

    private static (List<RelevanceKeyword> positive, List<RelevanceKeyword> negative, string? summary) ParseProfileKeywords(string llmContent)
    {
        // Strip markdown code fencing if present
        var content = llmContent.Trim();
        if (content.StartsWith("```"))
        {
            var firstNewline = content.IndexOf('\n');
            if (firstNewline >= 0)
                content = content[(firstNewline + 1)..];
            if (content.EndsWith("```"))
                content = content[..^3];
            content = content.Trim();
        }

        using var doc = JsonDocument.Parse(content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;

        var positive = new List<RelevanceKeyword>();
        if (root.TryGetProperty("positiveKeywords", out var posArray) && posArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in posArray.EnumerateArray())
            {
                var kw = item.TryGetProperty("keyword", out var k) ? k.GetString() : null;
                var weight = item.TryGetProperty("weight", out var w) && w.TryGetInt32(out var wv) ? wv : 5;
                if (!string.IsNullOrWhiteSpace(kw))
                    positive.Add(new RelevanceKeyword(kw, Math.Clamp(weight, 1, 10)));
            }
        }

        var negative = new List<RelevanceKeyword>();
        if (root.TryGetProperty("negativeKeywords", out var negArray) && negArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in negArray.EnumerateArray())
            {
                var kw = item.TryGetProperty("keyword", out var k) ? k.GetString() : null;
                var weight = item.TryGetProperty("weight", out var w) && w.TryGetInt32(out var wv) ? wv : -10;
                if (!string.IsNullOrWhiteSpace(kw))
                    negative.Add(new RelevanceKeyword(kw, Math.Clamp(weight, -20, -1)));
            }
        }

        string? summary = null;
        if (root.TryGetProperty("summary", out var sumEl) && sumEl.ValueKind == JsonValueKind.String)
            summary = sumEl.GetString();

        return (positive, negative, summary);
    }
}
