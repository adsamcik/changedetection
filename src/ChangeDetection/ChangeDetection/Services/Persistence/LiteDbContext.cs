using LiteDB;

namespace ChangeDetection.Services.Persistence;

/// <summary>
/// LiteDB database context for the application.
/// </summary>
public class LiteDbContext : IDisposable
{
    private readonly ILiteDatabase _database;
    private bool _disposed;

    public LiteDbContext(string connectionString)
    {
        // Configure BsonMapper for entities with 'required' properties
        ConfigureBsonMapper();
        
        var connection = new ConnectionString(connectionString)
        {
            Connection = ConnectionType.Shared
        };
        _database = new LiteDatabase(connection);
        
        ConfigureCollections();
    }

    public ILiteDatabase Database => _database;

    private static void ConfigureBsonMapper()
    {
        // Configure ChangeSnapshot entity
        BsonMapper.Global.Entity<Core.Entities.ChangeSnapshot>()
            .Id(x => x.Id)
            .Field(x => x.WatchedSiteId, "WatchedSiteId")
            .Field(x => x.CapturedAt, "CapturedAt")
            .Field(x => x.ContentHash, "ContentHash")
            .Field(x => x.Content, "Content")
            .Field(x => x.ScreenshotPath, "ScreenshotPath")
            .Field(x => x.HttpStatusCode, "HttpStatusCode")
            .Field(x => x.FetchDurationMs, "FetchDurationMs")
            .Field(x => x.ContentSizeBytes, "ContentSizeBytes");

        // Configure ChangeEvent entity
        BsonMapper.Global.Entity<Core.Entities.ChangeEvent>()
            .Id(x => x.Id)
            .Field(x => x.WatchedSiteId, "WatchedSiteId")
            .Field(x => x.PreviousSnapshotId, "PreviousSnapshotId")
            .Field(x => x.CurrentSnapshotId, "CurrentSnapshotId")
            .Field(x => x.DetectedAt, "DetectedAt")
            .Field(x => x.DiffSummary, "DiffSummary")
            .Field(x => x.DiffHtml, "DiffHtml")
            .Field(x => x.ChangeType, "ChangeType")
            .Field(x => x.Importance, "Importance")
            .Field(x => x.IsNotified, "IsNotified")
            .Field(x => x.NotifiedAt, "NotifiedAt")
            .Field(x => x.LinesAdded, "LinesAdded")
            .Field(x => x.LinesRemoved, "LinesRemoved")
            .Field(x => x.IsViewed, "IsViewed");

        // Configure WatchedSite entity
        BsonMapper.Global.Entity<Core.Entities.WatchedSite>()
            .Id(x => x.Id)
            .Field(x => x.Url, "Url");

        // Configure LlmProviderConfig entity
        BsonMapper.Global.Entity<Core.Entities.LlmProviderConfig>()
            .Id(x => x.Id)
            .Field(x => x.Name, "Name")
            .Field(x => x.ApiKey, "ApiKey")
            .Field(x => x.Endpoint, "Endpoint")
            .Field(x => x.Model, "Model");

        // Configure LlmUsageRecord entity
        BsonMapper.Global.Entity<Core.Entities.LlmUsageRecord>()
            .Id(x => x.Id)
            .Field(x => x.ProviderId, "ProviderId")
            .Field(x => x.ProviderName, "ProviderName")
            .Field(x => x.Model, "Model");
    }

    private void ConfigureCollections()
    {
        // Configure indexes for WatchedSites
        var watches = _database.GetCollection<Core.Entities.WatchedSite>("watches");
        watches.EnsureIndex(x => x.Url);
        watches.EnsureIndex(x => x.IsEnabled);
        watches.EnsureIndex(x => x.Status);
        watches.EnsureIndex(x => x.LastChecked);
        watches.EnsureIndex(x => x.Tags);

        // Configure indexes for ChangeSnapshots
        var snapshots = _database.GetCollection<Core.Entities.ChangeSnapshot>("snapshots");
        snapshots.EnsureIndex(x => x.WatchedSiteId);
        snapshots.EnsureIndex(x => x.CapturedAt);

        // Configure indexes for ChangeEvents
        var events = _database.GetCollection<Core.Entities.ChangeEvent>("events");
        events.EnsureIndex(x => x.WatchedSiteId);
        events.EnsureIndex(x => x.DetectedAt);
        events.EnsureIndex(x => x.IsViewed);

        // Configure indexes for LlmProviderConfigs
        var providers = _database.GetCollection<Core.Entities.LlmProviderConfig>("llm_providers");
        providers.EnsureIndex(x => x.Name, unique: true);
        providers.EnsureIndex(x => x.Priority);
        providers.EnsureIndex(x => x.IsEnabled);

        // Configure indexes for LlmUsageRecords
        var usage = _database.GetCollection<Core.Entities.LlmUsageRecord>("llm_usage");
        usage.EnsureIndex(x => x.ProviderId);
        usage.EnsureIndex(x => x.Timestamp);
        usage.EnsureIndex(x => x.UsageType);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _database.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
