using LiteDB;

namespace ChangeDetection.Services.Persistence;

/// <summary>
/// LiteDB database context for the application.
/// </summary>
public class LiteDbContext : IDisposable
{
    private readonly ILiteDatabase _database;
    private bool _disposed;
    private static bool _mapperConfigured;
    private static readonly object _mapperLock = new();

    public LiteDbContext(string connectionString)
    {
        // Ensure BsonMapper is configured only once
        EnsureBsonMapperConfigured();
        
        var connection = new ConnectionString(connectionString)
        {
            Connection = ConnectionType.Shared
        };
        _database = new LiteDatabase(connection);
        
        ConfigureCollections();
    }

    public virtual ILiteDatabase Database => _database;

    private static void EnsureBsonMapperConfigured()
    {
        if (_mapperConfigured) return;
        
        lock (_mapperLock)
        {
            if (_mapperConfigured) return;
            
            // LiteDB requires special handling for entities with 'required' keyword
            var mapper = BsonMapper.Global;
            
            // Enable automatic ID detection
            mapper.EmptyStringToNull = true;
            mapper.TrimWhitespace = true;

            // Register entity types with required properties to ensure proper serialization
            // WatchedSite
            mapper.Entity<Core.Entities.WatchedSite>()
                .Id(x => x.Id)
                .Field(x => x.Url, "Url");

            // ChangeSnapshot
            mapper.Entity<Core.Entities.ChangeSnapshot>()
                .Id(x => x.Id)
                .Field(x => x.WatchedSiteId, "WatchedSiteId")
                .Field(x => x.ContentHash, "ContentHash")
                .Field(x => x.Content, "Content");

            // ChangeEvent
            mapper.Entity<Core.Entities.ChangeEvent>()
                .Id(x => x.Id)
                .Field(x => x.WatchedSiteId, "WatchedSiteId")
                .Field(x => x.PreviousSnapshotId, "PreviousSnapshotId")
                .Field(x => x.CurrentSnapshotId, "CurrentSnapshotId");

            // LlmProviderConfig
            mapper.Entity<Core.Entities.LlmProviderConfig>()
                .Id(x => x.Id)
                .Field(x => x.Name, "Name")
                .Field(x => x.Model, "Model");

            // LlmUsageRecord
            mapper.Entity<Core.Entities.LlmUsageRecord>()
                .Id(x => x.Id)
                .Field(x => x.ProviderId, "ProviderId")
                .Field(x => x.ProviderName, "ProviderName")
                .Field(x => x.Model, "Model");

            // AppSettings
            mapper.Entity<Core.Entities.AppSettings>()
                .Id(x => x.Id);
            
            // User
            mapper.Entity<Core.Entities.User>()
                .Id(x => x.Id)
                .Field(x => x.Username, "Username");
            
            // Category
            mapper.Entity<Core.Entities.Category>()
                .Id(x => x.Id)
                .Field(x => x.Name, "Name");
            
            // View
            mapper.Entity<Core.Entities.View>()
                .Id(x => x.Id)
                .Field(x => x.Name, "Name");
            
            // PriceHistoryEntry
            mapper.Entity<Core.Entities.PriceHistoryEntry>()
                .Id(x => x.Id)
                .Field(x => x.WatchId, "WatchId")
                .Field(x => x.FieldName, "FieldName")
                .Field(x => x.Value, "Value");
            
            _mapperConfigured = true;
        }
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
        watches.EnsureIndex(x => x.OwnerId);

        // Configure indexes for ChangeSnapshots
        var snapshots = _database.GetCollection<Core.Entities.ChangeSnapshot>("snapshots");
        snapshots.EnsureIndex(x => x.WatchedSiteId);
        snapshots.EnsureIndex(x => x.CapturedAt);
        snapshots.EnsureIndex(x => x.OwnerId);

        // Configure indexes for ChangeEvents
        var events = _database.GetCollection<Core.Entities.ChangeEvent>("events");
        events.EnsureIndex(x => x.WatchedSiteId);
        events.EnsureIndex(x => x.DetectedAt);
        events.EnsureIndex(x => x.IsViewed);
        events.EnsureIndex(x => x.OwnerId);

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
        
        // Configure indexes for Categories
        var categories = _database.GetCollection<Core.Entities.Category>("categories");
        categories.EnsureIndex(x => x.Name);
        categories.EnsureIndex(x => x.OwnerId);
        
        // Configure indexes for Views
        var views = _database.GetCollection<Core.Entities.View>("views");
        views.EnsureIndex(x => x.Name);
        views.EnsureIndex(x => x.OwnerId);
        
        // Configure indexes for Users
        var users = _database.GetCollection<Core.Entities.User>("users");
        users.EnsureIndex(x => x.Username, unique: true);
        users.EnsureIndex(x => x.Email);
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
