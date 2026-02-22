using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services;

public class ServerWatchGroupService(
    IRepository<WatchGroup> groupRepo,
    IRepository<WatchedSite> watchRepo,
    IPriceHistoryRepository priceHistoryRepo,
    ILogger<ServerWatchGroupService> logger) : IWatchGroupService
{
    public async Task<IEnumerable<WatchGroup>> GetAllAsync(CancellationToken ct = default)
    {
        var groups = await groupRepo.GetAllAsync(ct);
        return groups.OrderBy(g => g.Name);
    }

    public async Task<WatchGroup?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await groupRepo.GetByIdAsync(id, ct);

    public async Task<WatchGroup> CreateGroupAsync(WatchGroupCreateRequest request, CancellationToken ct = default)
    {
        var group = new WatchGroup
        {
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            Icon = request.Icon,
            UserIntent = request.UserIntent,
            Tags = request.Tags
        };
        await groupRepo.InsertAsync(group, ct);
        logger.LogInformation("Created watch group {Id}: {Name}", group.Id, group.Name);
        return group;
    }

    public async Task UpdateGroupAsync(WatchGroup group, CancellationToken ct = default)
    {
        group.UpdatedAt = DateTime.UtcNow;
        await groupRepo.UpdateAsync(group, ct);
    }

    public async Task DeleteGroupAsync(Guid id, bool deleteWatches = false, CancellationToken ct = default)
    {
        var group = await groupRepo.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException($"Watch group {id} not found");
        var members = await GetGroupMembersAsync(id, ct);

        if (deleteWatches)
        {
            foreach (var watch in members)
                await watchRepo.DeleteAsync(watch.Id, ct);
        }
        else
        {
            foreach (var watch in members)
            {
                watch.GroupId = null;
                await watchRepo.UpdateAsync(watch, ct);
            }
        }

        await groupRepo.DeleteAsync(id, ct);
        logger.LogInformation("Deleted watch group {Id}: {Name}, {Action} {Count} watches",
            id, group.Name, deleteWatches ? "deleted" : "unlinked", members.Count);
    }

    public async Task AddWatchToGroupAsync(Guid groupId, Guid watchId, CancellationToken ct = default)
    {
        _ = await groupRepo.GetByIdAsync(groupId, ct)
            ?? throw new InvalidOperationException($"Watch group {groupId} not found");
        var watch = await watchRepo.GetByIdAsync(watchId, ct)
            ?? throw new InvalidOperationException($"Watch {watchId} not found");

        watch.GroupId = groupId;
        await watchRepo.UpdateAsync(watch, ct);
        logger.LogDebug("Added watch {WatchId} to group {GroupId}", watchId, groupId);
    }

    public async Task RemoveWatchFromGroupAsync(Guid groupId, Guid watchId, CancellationToken ct = default)
    {
        _ = await groupRepo.GetByIdAsync(groupId, ct)
            ?? throw new InvalidOperationException($"Watch group {groupId} not found");
        var watch = await watchRepo.GetByIdAsync(watchId, ct)
            ?? throw new InvalidOperationException($"Watch {watchId} not found");

        if (watch.GroupId != groupId)
            throw new InvalidOperationException($"Watch {watchId} is not a member of group {groupId}");

        watch.GroupId = null;
        await watchRepo.UpdateAsync(watch, ct);
        logger.LogDebug("Removed watch {WatchId} from group {GroupId}", watchId, groupId);
    }

    public async Task<List<WatchedSite>> GetGroupMembersAsync(Guid groupId, CancellationToken ct = default)
    {
        var watches = await watchRepo.FindAsync(w => w.GroupId == groupId, ct);
        return watches.ToList();
    }

    public async Task<AggregateSnapshot> ComputeAggregateAsync(Guid groupId, CancellationToken ct = default)
    {
        var group = await groupRepo.GetByIdAsync(groupId, ct)
            ?? throw new InvalidOperationException($"Watch group {groupId} not found");
        var members = await GetGroupMembersAsync(groupId, ct);

        var snapshot = new AggregateSnapshot
        {
            GroupId = groupId,
            Members = members.Select(m => new AggregateSnapshotMember
            {
                WatchId = m.Id, Name = m.Name ?? m.Url, Url = m.Url,
                Status = m.Status, LastChecked = m.LastChecked,
                HasErrors = m.Status == WatchStatus.Error
            }).ToList()
        };

        foreach (var fieldConfig in group.AggregateFields)
            snapshot.Fields.Add(await ComputeFieldValueAsync(fieldConfig, members, ct));

        return snapshot;
    }

    public async Task<AggregateAlertResult> EvaluateAggregateAlertsAsync(Guid groupId, CancellationToken ct = default)
    {
        var group = await groupRepo.GetByIdAsync(groupId, ct)
            ?? throw new InvalidOperationException($"Watch group {groupId} not found");
        var snapshot = await ComputeAggregateAsync(groupId, ct);
        var result = new AggregateAlertResult { GroupId = groupId };

        foreach (var alert in group.AggregateAlerts.Where(a => a.IsEnabled))
        {
            if (alert.CooldownPeriod.HasValue && alert.LastTriggeredAt.HasValue
                && DateTime.UtcNow - alert.LastTriggeredAt.Value < alert.CooldownPeriod.Value)
                continue;

            var field = snapshot.Fields.FirstOrDefault(f => f.FieldName == alert.FieldName);
            if (field?.AggregatedValue is null) continue;

            var triggered = alert.ConditionType switch
            {
                AlertConditionType.DropsBelow => field.AggregatedValue < alert.Value,
                AlertConditionType.RisesAbove => field.AggregatedValue > alert.Value,
                AlertConditionType.EntersRange => alert.SecondaryValue.HasValue
                    && field.AggregatedValue >= alert.Value
                    && field.AggregatedValue <= alert.SecondaryValue.Value,
                AlertConditionType.ExitsRange => alert.SecondaryValue.HasValue
                    && (field.AggregatedValue < alert.Value || field.AggregatedValue > alert.SecondaryValue.Value),
                AlertConditionType.TargetReached => Math.Abs(field.AggregatedValue.Value - alert.Value) < 0.01,
                _ => false
            };

            if (!triggered) continue;

            alert.LastTriggeredAt = DateTime.UtcNow;
            alert.TriggerCount++;
            if (alert.OneTime) alert.IsEnabled = false;

            result.TriggeredAlerts.Add(new TriggeredAggregateAlert
            {
                AlertId = alert.Id, FieldName = alert.FieldName,
                AggregatedValue = field.AggregatedValue,
                ThresholdValue = alert.Value,
                Message = alert.NotificationTemplate
                    ?? $"{alert.FieldName} ({alert.Function}) {alert.ConditionType}: {field.AggregatedValue:F2} vs threshold {alert.Value:F2}",
                Importance = alert.ImportanceOverride ?? ChangeImportance.High
            });
        }

        if (result.TriggeredAlerts.Count > 0)
            await groupRepo.UpdateAsync(group, ct);

        return result;
    }

    private async Task<AggregateFieldValue> ComputeFieldValueAsync(
        AggregateFieldConfig config, List<WatchedSite> members, CancellationToken ct)
    {
        var perSiteValues = new List<PerSiteValue>();
        var numericValues = new List<(Guid WatchId, string Name, double Value, DateTime Timestamp)>();

        foreach (var member in members)
        {
            var latest = await priceHistoryRepo.GetLatestAsync(member.Id, config.FieldName, ct: ct);
            perSiteValues.Add(new PerSiteValue
            {
                WatchId = member.Id,
                WatchName = member.Name ?? member.Url,
                Value = latest != null ? (double)latest.Value : null,
                FormattedValue = latest != null ? FormatValue(latest.Value, config) : null,
                LastUpdated = latest?.Timestamp,
                Status = member.Status
            });
            if (latest != null)
                numericValues.Add((member.Id, member.Name ?? member.Url, (double)latest.Value, latest.Timestamp));
        }

        var (aggregated, bestName) = Aggregate(config.Function, numericValues);

        return new AggregateFieldValue
        {
            FieldName = config.FieldName,
            Function = config.Function,
            AggregatedValue = aggregated,
            FormattedValue = aggregated.HasValue ? FormatValue((decimal)aggregated.Value, config) : null,
            BestSourceName = bestName,
            PerSiteValues = perSiteValues
        };
    }

    private static (double? Value, string? BestName) Aggregate(
        AggregateFunction fn,
        List<(Guid WatchId, string Name, double Value, DateTime Timestamp)> vals)
    {
        if (vals.Count == 0) return (null, null);
        return fn switch
        {
            AggregateFunction.Min => (vals.Min(v => v.Value), vals.MinBy(v => v.Value).Name),
            AggregateFunction.Max => (vals.Max(v => v.Value), vals.MaxBy(v => v.Value).Name),
            AggregateFunction.Average => (vals.Average(v => v.Value), null),
            AggregateFunction.Sum => (vals.Sum(v => v.Value), null),
            AggregateFunction.Count => (vals.Count, null),
            AggregateFunction.Median => (GetMedian(vals.Select(v => v.Value).ToList()), null),
            AggregateFunction.Latest => (vals.MaxBy(v => v.Timestamp).Value, vals.MaxBy(v => v.Timestamp).Name),
            AggregateFunction.Range => (vals.Max(v => v.Value) - vals.Min(v => v.Value), null),
            _ => (null, null)
        };
    }

    private static double GetMedian(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }

    private static string FormatValue(decimal value, AggregateFieldConfig config)
    {
        var unit = config.CurrencyCode ?? config.Unit ?? "";
        if (string.IsNullOrEmpty(unit)) return value.ToString("N2");
        return $"{value:N2} {unit}".Trim();
    }
}
