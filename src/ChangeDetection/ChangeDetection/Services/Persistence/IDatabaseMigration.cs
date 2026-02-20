using LiteDB;

namespace ChangeDetection.Services.Persistence;

/// <summary>
/// Represents a versioned database migration that transforms the LiteDB schema.
/// </summary>
public interface IDatabaseMigration
{
    /// <summary>
    /// The version number for this migration. Must be unique and sequential.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// A human-readable description of what this migration does.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Executes the migration against the database.
    /// </summary>
    Task MigrateAsync(ILiteDatabase database, CancellationToken ct);
}
