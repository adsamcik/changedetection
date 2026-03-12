using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

public interface IWatchGroupService
{
    Task<IEnumerable<WatchGroup>> GetAllAsync(CancellationToken ct = default);
    Task<WatchGroup?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<WatchGroup> CreateGroupAsync(WatchGroupCreateRequest request, CancellationToken ct = default);
    Task UpdateGroupAsync(WatchGroup group, CancellationToken ct = default);
    Task DeleteGroupAsync(Guid id, bool deleteWatches = false, CancellationToken ct = default);
    Task AddWatchToGroupAsync(Guid groupId, Guid watchId, CancellationToken ct = default);
    Task RemoveWatchFromGroupAsync(Guid groupId, Guid watchId, CancellationToken ct = default);
    Task<List<WatchedSite>> GetGroupMembersAsync(Guid groupId, CancellationToken ct = default);
    Task<AggregateSnapshot> ComputeAggregateAsync(Guid groupId, CancellationToken ct = default);
    Task<AggregateAlertResult> EvaluateAggregateAlertsAsync(Guid groupId, CancellationToken ct = default);
}

public class WatchGroupCreateRequest
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? UserIntent { get; set; }
    public string? AnalysisProfileJson { get; set; }
    public string? TemplateId { get; set; }
    public int? TemplateVersion { get; set; }
    public List<string> Tags { get; set; } = [];
}
