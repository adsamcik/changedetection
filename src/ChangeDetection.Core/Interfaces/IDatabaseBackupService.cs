namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Information about a database backup file.
/// </summary>
public record BackupInfo(string FileName, long SizeBytes, DateTime CreatedAt);

/// <summary>
/// Service for creating and managing LiteDB database backups.
/// </summary>
public interface IDatabaseBackupService
{
    /// <summary>
    /// Creates a new database backup. Returns the file path of the created backup.
    /// </summary>
    Task<string> CreateBackupAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists all available backups, ordered by creation date descending.
    /// </summary>
    Task<IReadOnlyList<BackupInfo>> GetBackupsAsync(CancellationToken ct = default);

    /// <summary>
    /// Deletes old backups, keeping the newest <paramref name="retainCount"/>.
    /// Returns the number of deleted backups.
    /// </summary>
    Task<int> CleanupOldBackupsAsync(int retainCount = 7, CancellationToken ct = default);

    /// <summary>
    /// Returns the full file path for a backup by name, or null if not found.
    /// </summary>
    Task<string?> GetBackupPathAsync(string backupName, CancellationToken ct = default);
}
