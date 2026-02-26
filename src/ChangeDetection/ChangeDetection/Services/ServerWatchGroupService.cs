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
            snapshot.Fields.Add(await ComputeFieldValueAsync(fieldConfig, members, snapshot, ct));

        // Compute absence summary
        var allPerSiteValues = snapshot.Fields.SelectMany(f => f.PerSiteValues).ToList();
        var perWatchStates = allPerSiteValues
            .GroupBy(p => p.WatchId)
            .ToDictionary(g => g.Key, g => g.First().AvailabilityState);

        snapshot.AbsenceSummary = new AbsenceSummary
        {
            AvailableCount = perWatchStates.Count(kv => kv.Value == SiteAvailabilityState.Available),
            MissingPendingCount = perWatchStates.Count(kv => kv.Value == SiteAvailabilityState.MissingPending),
            ConfirmedAbsentCount = perWatchStates.Count(kv => kv.Value == SiteAvailabilityState.ConfirmedAbsent),
            AbsentWatchIds = perWatchStates
                .Where(kv => kv.Value == SiteAvailabilityState.ConfirmedAbsent)
                .Select(kv => kv.Key).ToList()
        };

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
            if (field is null) continue;

            var triggered = alert.ConditionType switch
            {
                AlertConditionType.DropsBelow => field.AggregatedValue.HasValue && field.AggregatedValue < alert.Value,
                AlertConditionType.RisesAbove => field.AggregatedValue.HasValue && field.AggregatedValue > alert.Value,
                AlertConditionType.EntersRange => field.AggregatedValue.HasValue && alert.SecondaryValue.HasValue
                    && field.AggregatedValue >= alert.Value
                    && field.AggregatedValue <= alert.SecondaryValue.Value,
                AlertConditionType.ExitsRange => field.AggregatedValue.HasValue && alert.SecondaryValue.HasValue
                    && (field.AggregatedValue < alert.Value || field.AggregatedValue > alert.SecondaryValue.Value),
                AlertConditionType.TargetReached => field.AggregatedValue.HasValue
                    && Math.Abs(field.AggregatedValue.Value - alert.Value) < 0.01,
                AlertConditionType.RankChanged => EvaluateRankChanged(field, group, alert),
                AlertConditionType.OutlierDetected => EvaluateOutlierDetected(field, group),
                AlertConditionType.SiteAbsent => snapshot.AbsenceSummary?.ConfirmedAbsentCount > 0,
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
                Message = BuildAlertMessage(alert, field, snapshot),
                Importance = alert.ImportanceOverride ?? ChangeImportance.High
            });
        }

        if (result.TriggeredAlerts.Count > 0)
            await groupRepo.UpdateAsync(group, ct);
        else
        {
            // Persist rank tracking state even when no alerts fire
            var hasRankUpdates = group.AggregateFields.Any(f => f.CurrentLeaderHoldCount > 0);
            if (hasRankUpdates)
                await groupRepo.UpdateAsync(group, ct);
        }

        return result;
    }

    private async Task<AggregateFieldValue> ComputeFieldValueAsync(
        AggregateFieldConfig config, List<WatchedSite> members,
        AggregateSnapshot snapshot, CancellationToken ct)
    {
        var perSiteValues = new List<PerSiteValue>();
        var numericValues = new List<(Guid WatchId, string Name, double Value, DateTime Timestamp)>();

        foreach (var member in members)
        {
            var latest = await priceHistoryRepo.GetLatestAsync(member.Id, config.FieldName, ct: ct);
            var psv = new PerSiteValue
            {
                WatchId = member.Id,
                WatchName = member.Name ?? member.Url,
                Value = latest != null ? (double)latest.Value : null,
                FormattedValue = latest != null ? FormatValue(latest.Value, config) : null,
                LastUpdated = latest?.Timestamp,
                Status = member.Status
            };

            // Absence detection: track consecutive failures
            if (member.Status == WatchStatus.Error || latest is null)
            {
                // Use Status as a proxy — error status means site is failing
                var isFailing = member.Status == WatchStatus.Error;
                psv.ConsecutiveFailures = isFailing ? MissingPendingThreshold : (latest is null ? 1 : 0);
                psv.AvailabilityState = isFailing && latest is null
                    ? SiteAvailabilityState.ConfirmedAbsent
                    : isFailing
                        ? SiteAvailabilityState.MissingPending
                        : SiteAvailabilityState.Available;
            }

            // Sanity guard: quarantine suspicious readings
            if (latest != null)
            {
                var value = (double)latest.Value;
                var quarantineReason = CheckSanity(value, member.Id, config.FieldName, numericValues);
                if (quarantineReason != null)
                {
                    psv.IsQuarantined = true;
                    psv.QuarantineReason = quarantineReason;
                    snapshot.DataQualityWarnings.Add(new DataQualityWarning
                    {
                        WatchId = member.Id,
                        WatchName = member.Name ?? member.Url,
                        FieldName = config.FieldName,
                        ReportedValue = value,
                        Reason = quarantineReason
                    });
                    logger.LogWarning("Sanity guard quarantined {Watch} field {Field}: {Value} ({Reason})",
                        member.Name ?? member.Url, config.FieldName, value, quarantineReason);
                }
                else
                {
                    numericValues.Add((member.Id, member.Name ?? member.Url, value, latest.Timestamp));
                }
            }

            perSiteValues.Add(psv);
        }

        var (aggregated, bestName) = Aggregate(config.Function, numericValues);

        // Compute outlier deviations from median
        if (numericValues.Count >= 3)
        {
            var median = GetMedian(numericValues.Select(v => v.Value).ToList());
            foreach (var psv in perSiteValues.Where(p => p.Value.HasValue && !p.IsQuarantined))
            {
                psv.DeviationFromMedianPercent = median != 0
                    ? Math.Abs(psv.Value!.Value - median) / Math.Abs(median) * 100.0
                    : 0;
            }
        }

        // Update rank tracking on config
        if (bestName != null && bestName != config.PreviousBestSource)
        {
            if (config.PreviousBestSource is null)
            {
                // First time — just record, don't count as change
                config.PreviousBestSource = bestName;
                config.CurrentLeaderHoldCount = 1;
            }
            else
            {
                // New leader detected — increment hold count for consecutive checks
                config.CurrentLeaderHoldCount++;
            }
        }
        else if (bestName != null)
        {
            // Same leader as previous — reset hold count (no pending change)
            config.CurrentLeaderHoldCount = 0;
        }

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

    // --- Rank-switch detection ---

    private const int MissingPendingThreshold = 3;
    private const int ConfirmedAbsentThreshold = 5;

    private static bool EvaluateRankChanged(
        AggregateFieldValue field, WatchGroup group, AggregateAlert alert)
    {
        var config = group.AggregateFields.FirstOrDefault(f => f.FieldName == alert.FieldName);
        if (config is null || field.BestSourceName is null || config.PreviousBestSource is null)
            return false;

        // Only fire if the leader has changed AND held for enough checks (stability filter)
        if (field.BestSourceName != config.PreviousBestSource
            && config.CurrentLeaderHoldCount >= config.RankStabilityRequired)
        {
            // Update previous source now that alert will fire
            config.PreviousBestSource = field.BestSourceName;
            config.CurrentLeaderHoldCount = 0;
            return true;
        }

        return false;
    }

    private static bool EvaluateOutlierDetected(AggregateFieldValue field, WatchGroup group)
    {
        var config = group.AggregateFields.FirstOrDefault(f => f.FieldName == field.FieldName);
        if (config is null) return false;

        return field.PerSiteValues.Any(p =>
            !p.IsQuarantined
            && p.DeviationFromMedianPercent.HasValue
            && p.DeviationFromMedianPercent.Value > config.OutlierThresholdPercent);
    }

    // --- Sanity guard ---

    private static string? CheckSanity(
        double value, Guid watchId, string fieldName,
        List<(Guid WatchId, string Name, double Value, DateTime Timestamp)> existingValues)
    {
        if (value <= 0)
            return $"Value is {value:F2} (zero or negative)";

        // Check against other members' values already collected
        if (existingValues.Count >= 2)
        {
            var median = GetMedian(existingValues.Select(v => v.Value).ToList());
            if (median > 0)
            {
                var deviationPercent = Math.Abs(value - median) / median * 100.0;
                if (deviationPercent > 200.0)
                    return $"Value {value:F2} deviates {deviationPercent:F0}% from group median {median:F2}";
            }
        }

        return null;
    }

    // --- Alert message building ---

    private static string BuildAlertMessage(AggregateAlert alert, AggregateFieldValue field, AggregateSnapshot snapshot)
    {
        if (alert.NotificationTemplate != null)
            return alert.NotificationTemplate;

        return alert.ConditionType switch
        {
            AlertConditionType.RankChanged =>
                $"Best source for {alert.FieldName} changed to {field.BestSourceName}",
            AlertConditionType.OutlierDetected =>
                BuildOutlierMessage(field),
            AlertConditionType.SiteAbsent =>
                $"{snapshot.AbsenceSummary?.ConfirmedAbsentCount} site(s) confirmed absent",
            _ =>
                $"{alert.FieldName} ({alert.Function}) {alert.ConditionType}: {field.AggregatedValue:F2} vs threshold {alert.Value:F2}"
        };
    }

    private static string BuildOutlierMessage(AggregateFieldValue field)
    {
        var outliers = field.PerSiteValues
            .Where(p => !p.IsQuarantined && p.DeviationFromMedianPercent > 20)
            .Select(p => $"{p.WatchName} ({p.Value:F2}, {p.DeviationFromMedianPercent:F0}% deviation)")
            .ToList();
        return outliers.Count > 0
            ? $"Outlier detected on {field.FieldName}: {string.Join(", ", outliers)}"
            : $"Outlier detected on {field.FieldName}";
    }
}
