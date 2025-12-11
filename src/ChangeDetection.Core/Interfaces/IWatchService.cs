using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for managing watched sites.
/// </summary>
public interface IWatchService
{
    Task<WatchedSite> CreateWatchAsync(CreateWatchRequest request, CancellationToken ct = default);
    Task<WatchedSite?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IEnumerable<WatchedSite>> GetAllAsync(CancellationToken ct = default);
    Task<IEnumerable<WatchedSite>> GetWatchesDueForCheckAsync(CancellationToken ct = default);
    Task<IEnumerable<WatchedSite>> GetByTagAsync(string tag, CancellationToken ct = default);
    Task UpdateWatchAsync(WatchedSite watch, CancellationToken ct = default);
    Task DeleteWatchAsync(Guid id, CancellationToken ct = default);
    Task<ChangeEvent?> CheckForChangesAsync(Guid watchId, CancellationToken ct = default);
    Task EnableWatchAsync(Guid id, CancellationToken ct = default);
    Task DisableWatchAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Request to create a new watch.
/// </summary>
public class CreateWatchRequest
{
    public required string Url { get; set; }
    public string? Name { get; set; }
    public string? CssSelector { get; set; }
    public string? XPathSelector { get; set; }
    public TimeSpan? CheckInterval { get; set; }
    public bool UseJavaScript { get; set; }
    public List<string>? Tags { get; set; }
    public List<string>? IgnorePatterns { get; set; }
    public Dictionary<string, string>? TagColors { get; set; }
    public Guid? CategoryId { get; set; }
    public NotificationSettings? Notifications { get; set; }
    public FetchSettings? FetchSettings { get; set; }
    public string? LlmProviderOverride { get; set; }
    public string? Description { get; set; }
}
