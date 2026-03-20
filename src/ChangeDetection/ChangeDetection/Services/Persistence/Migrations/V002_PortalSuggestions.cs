using LiteDB;

namespace ChangeDetection.Services.Persistence.Migrations;

public class V002_PortalSuggestions : IDatabaseMigration
{
    public int Version => 2;
    public string Description => "Add portal suggestion indexes";

    public Task MigrateAsync(ILiteDatabase database, CancellationToken ct)
    {
        var suggestions = database.GetCollection("portal_suggestions");
        suggestions.EnsureIndex("OwnerId");
        suggestions.EnsureIndex("GroupId");
        suggestions.EnsureIndex("Domain");
        suggestions.EnsureIndex("Status");
        suggestions.EnsureIndex("SourceWatchId");
        suggestions.EnsureIndex("CreatedAt");

        return Task.CompletedTask;
    }
}
