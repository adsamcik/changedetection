using LiteDB;

namespace ChangeDetection.Services.Persistence;

/// <summary>
/// Runs pending database migrations on startup in version order.
/// Stores applied migration history in the _schema_version collection.
/// </summary>
public class DatabaseMigrationRunner(
    LiteDbContext dbContext,
    IEnumerable<IDatabaseMigration> migrations,
    ILogger<DatabaseMigrationRunner> logger) : IHostedService
{
    private const string SchemaVersionCollection = "_schema_version";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var database = dbContext.Database;
        var versionCollection = database.GetCollection<SchemaVersion>(SchemaVersionCollection);

        var currentVersion = versionCollection
            .FindAll()
            .OrderByDescending(v => v.Version)
            .FirstOrDefault()?.Version ?? 0;

        logger.LogInformation("Current database schema version: {Version}", currentVersion);

        var pending = migrations
            .Where(m => m.Version > currentVersion)
            .OrderBy(m => m.Version)
            .ToList();

        if (pending.Count == 0)
        {
            logger.LogInformation("Database schema is up to date");
            return;
        }

        logger.LogInformation("Applying {Count} pending migration(s)", pending.Count);

        foreach (var migration in pending)
        {
            logger.LogInformation(
                "Applying migration V{Version}: {Description}",
                migration.Version.ToString().PadLeft(3, '0'),
                migration.Description);

            try
            {
                await migration.MigrateAsync(database, cancellationToken);

                versionCollection.Insert(new SchemaVersion
                {
                    Id = ObjectId.NewObjectId(),
                    Version = migration.Version,
                    Description = migration.Description,
                    AppliedAt = DateTime.UtcNow
                });

                logger.LogInformation("Migration V{Version} applied successfully", migration.Version.ToString().PadLeft(3, '0'));
            }
            catch (Exception ex)
            {
                logger.LogCritical(
                    ex,
                    "Migration V{Version} failed: {Description}. Startup aborted.",
                    migration.Version.ToString().PadLeft(3, '0'),
                    migration.Description);

                throw;
            }
        }

        logger.LogInformation("All migrations applied. Database schema version: {Version}", pending[^1].Version);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Tracks applied schema migrations in the database.
/// </summary>
public class SchemaVersion
{
    public required ObjectId Id { get; set; }
    public required int Version { get; set; }
    public required string Description { get; set; }
    public required DateTime AppliedAt { get; set; }
}
