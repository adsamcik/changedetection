using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
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
        group.MapGet("/{id}/aggregate", GetAggregate).WithName("GetGroupAggregate").Produces<AggregateSnapshotDto>().Produces(404);
        group.MapGet("/{id}/alerts/evaluate", EvaluateAlerts).WithName("EvaluateGroupAlerts").Produces<AggregateAlertResultDto>().Produces(404);
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
}
