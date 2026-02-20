using ChangeDetection.Services.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Persistence;

[Category("Unit")]
public class DatabaseBackupServiceTests : TestBase
{
    private (string tempDir, string dbPath, LiteDbContext context, DatabaseBackupService service, Action cleanup) SetupBackupTest()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test_backup_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        var dbPath = Path.Combine(tempDir, "test.db");
        var context = new LiteDbContext(dbPath);

        // Ensure the DB file has some content
        context.Database.GetCollection("seed").Insert(new LiteDB.BsonDocument { ["key"] = "value" });

        var config = Substitute.For<IConfiguration>();
        var section = Substitute.For<IConfigurationSection>();
        section.Value.Returns(dbPath);
        config.GetSection("LiteDb:Path").Returns(section);

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(tempDir);

        var logger = CreateLogger<DatabaseBackupService>();
        var service = new DatabaseBackupService(context, config, env, logger);

        return (tempDir, dbPath, context, service, () =>
        {
            try { context.Dispose(); } catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        });
    }

    [Test]
    public async Task CreateBackupAsync_CreatesBackupFile()
    {
        var (tempDir, _, _, service, cleanup) = SetupBackupTest();
        try
        {
            // Act
            var backupPath = await service.CreateBackupAsync();

            // Assert
            File.Exists(backupPath).ShouldBeTrue();
            new FileInfo(backupPath).Length.ShouldBeGreaterThan(0);
            backupPath.ShouldStartWith(Path.Combine(tempDir, "backups"));
        }
        finally
        {
            cleanup();
        }
    }

    [Test]
    public async Task CleanupOldBackupsAsync_KeepsNewestRetainCount()
    {
        var (tempDir, _, _, service, cleanup) = SetupBackupTest();
        try
        {
            // Arrange - create 5 backup files with staggered timestamps
            var backupDir = Path.Combine(tempDir, "backups");
            Directory.CreateDirectory(backupDir);

            for (var i = 0; i < 5; i++)
            {
                var filePath = Path.Combine(backupDir, $"backup-2024-01-0{i + 1}-120000.db");
                await File.WriteAllTextAsync(filePath, $"backup {i}");
                // Set creation time so ordering is deterministic
                File.SetCreationTimeUtc(filePath, new DateTime(2024, 1, i + 1, 12, 0, 0, DateTimeKind.Utc));
            }

            // Act - retain only 2 newest
            var deleted = await service.CleanupOldBackupsAsync(retainCount: 2);

            // Assert
            deleted.ShouldBe(3);
            var remaining = Directory.GetFiles(backupDir, "backup-*.db");
            remaining.Length.ShouldBe(2);
        }
        finally
        {
            cleanup();
        }
    }

    [Test]
    public async Task GetBackupPathAsync_PreventsPathTraversal()
    {
        var (tempDir, _, _, service, cleanup) = SetupBackupTest();
        try
        {
            // Arrange - create a backup so the backup dir exists
            await service.CreateBackupAsync();

            // Act - try path traversal
            var result = await service.GetBackupPathAsync("../../../etc/passwd");

            // Assert - should not find anything outside the backup directory
            result.ShouldBeNull();
        }
        finally
        {
            cleanup();
        }
    }

    [Test]
    public async Task GetBackupsAsync_ReturnsOrderedByDate()
    {
        var (tempDir, _, _, service, cleanup) = SetupBackupTest();
        try
        {
            // Arrange - create backup files with known timestamps
            var backupDir = Path.Combine(tempDir, "backups");
            Directory.CreateDirectory(backupDir);

            var files = new[]
            {
                ("backup-2024-01-01-120000.db", new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc)),
                ("backup-2024-01-03-120000.db", new DateTime(2024, 1, 3, 12, 0, 0, DateTimeKind.Utc)),
                ("backup-2024-01-02-120000.db", new DateTime(2024, 1, 2, 12, 0, 0, DateTimeKind.Utc)),
            };

            foreach (var (name, date) in files)
            {
                var filePath = Path.Combine(backupDir, name);
                await File.WriteAllTextAsync(filePath, "data");
                File.SetCreationTimeUtc(filePath, date);
            }

            // Act
            var backups = await service.GetBackupsAsync();

            // Assert - newest first
            backups.Count.ShouldBe(3);
            backups[0].FileName.ShouldContain("01-03");
            backups[1].FileName.ShouldContain("01-02");
            backups[2].FileName.ShouldContain("01-01");
        }
        finally
        {
            cleanup();
        }
    }
}
