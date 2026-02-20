using LiteDB;

namespace ChangeDetection.Services.Persistence.Migrations;

/// <summary>
/// Baseline migration that ensures core collection indexes exist.
/// This captures the initial schema state so future migrations have a known starting point.
/// </summary>
public class V001_InitialSchema : IDatabaseMigration
{
    public int Version => 1;
    public string Description => "Ensure baseline indexes on core collections";

    public Task MigrateAsync(ILiteDatabase database, CancellationToken ct)
    {
        // Watches
        var watches = database.GetCollection("watches");
        watches.EnsureIndex("Url");
        watches.EnsureIndex("IsEnabled");
        watches.EnsureIndex("Status");
        watches.EnsureIndex("LastChecked");
        watches.EnsureIndex("OwnerId");

        // Snapshots
        var snapshots = database.GetCollection("snapshots");
        snapshots.EnsureIndex("WatchedSiteId");
        snapshots.EnsureIndex("CapturedAt");
        snapshots.EnsureIndex("OwnerId");

        // Events
        var events = database.GetCollection("events");
        events.EnsureIndex("WatchedSiteId");
        events.EnsureIndex("DetectedAt");
        events.EnsureIndex("IsViewed");
        events.EnsureIndex("OwnerId");

        return Task.CompletedTask;
    }
}
