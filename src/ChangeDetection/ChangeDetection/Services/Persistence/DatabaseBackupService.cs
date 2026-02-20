using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Persistence;

namespace ChangeDetection.Services.Persistence;

/// <summary>
/// Creates and manages LiteDB database backups via file-level copy after checkpoint.
/// </summary>
public class DatabaseBackupService(
    LiteDbContext dbContext,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ILogger<DatabaseBackupService> logger) : IDatabaseBackupService
{
    private readonly string _dbPath = configuration.GetValue<string>("LiteDb:Path") ?? "changedetection.db";
    private readonly string _backupDir = Path.Combine(environment.ContentRootPath, "backups");
    private readonly Lock _backupLock = new();

    public Task<string> CreateBackupAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_backupDir);

        var fileName = $"backup-{DateTime.UtcNow:yyyy-MM-dd-HHmmss}.db";
        var destinationPath = Path.Combine(_backupDir, fileName);

        lock (_backupLock)
        {
            dbContext.Database.Checkpoint();
            File.Copy(_dbPath, destinationPath, overwrite: true);
        }

        var fileInfo = new FileInfo(destinationPath);
        logger.LogInformation("Database backup created: {FileName} ({SizeBytes} bytes)", fileName, fileInfo.Length);

        return Task.FromResult(destinationPath);
    }

    public Task<IReadOnlyList<BackupInfo>> GetBackupsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_backupDir))
            return Task.FromResult<IReadOnlyList<BackupInfo>>([]);

        var backups = new DirectoryInfo(_backupDir)
            .GetFiles("backup-*.db")
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => new BackupInfo(f.Name, f.Length, f.CreationTimeUtc))
            .ToList();

        return Task.FromResult<IReadOnlyList<BackupInfo>>(backups);
    }

    public Task<int> CleanupOldBackupsAsync(int retainCount = 7, CancellationToken ct = default)
    {
        if (!Directory.Exists(_backupDir))
            return Task.FromResult(0);

        var files = new DirectoryInfo(_backupDir)
            .GetFiles("backup-*.db")
            .OrderByDescending(f => f.CreationTimeUtc)
            .ToList();

        var toDelete = files.Skip(retainCount).ToList();
        var deleted = 0;

        foreach (var file in toDelete)
        {
            try
            {
                file.Delete();
                deleted++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete old backup: {FileName}", file.Name);
            }
        }

        if (deleted > 0)
            logger.LogInformation("Backup cleanup: deleted {DeletedCount} old backups, retained {RetainCount}", deleted, retainCount);

        return Task.FromResult(deleted);
    }

    public Task<string?> GetBackupPathAsync(string backupName, CancellationToken ct = default)
    {
        if (!Directory.Exists(_backupDir))
            return Task.FromResult<string?>(null);

        // Prevent path traversal
        var safeName = Path.GetFileName(backupName);
        var fullPath = Path.Combine(_backupDir, safeName);

        return Task.FromResult<string?>(File.Exists(fullPath) ? fullPath : null);
    }
}
