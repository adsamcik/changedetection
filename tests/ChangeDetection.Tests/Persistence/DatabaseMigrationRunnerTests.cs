using ChangeDetection.Services.Persistence;
using LiteDB;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Persistence;

[Category("Unit")]
public class DatabaseMigrationRunnerTests : TestBase
{
    private static (string dbPath, LiteDbContext context, Action cleanup) CreateTempDb()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_mig_{Guid.NewGuid()}.db");
        var context = new LiteDbContext(dbPath);
        return (dbPath, context, () =>
        {
            try { context.Dispose(); } catch { }
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        });
    }

    [Test]
    public async Task StartAsync_WithPendingMigrations_AppliesInOrder()
    {
        var (_, dbContext, cleanup) = CreateTempDb();
        try
        {
            // Arrange
            var appliedOrder = new List<int>();
            var migrations = new IDatabaseMigration[]
            {
                new FakeMigration(2, "Second", (_, _) => { appliedOrder.Add(2); return Task.CompletedTask; }),
                new FakeMigration(1, "First", (_, _) => { appliedOrder.Add(1); return Task.CompletedTask; }),
                new FakeMigration(3, "Third", (_, _) => { appliedOrder.Add(3); return Task.CompletedTask; }),
            };
            var logger = CreateLogger<DatabaseMigrationRunner>();
            var runner = new DatabaseMigrationRunner(dbContext, migrations, logger);

            // Act
            await runner.StartAsync(CancellationToken.None);

            // Assert
            appliedOrder.ShouldBe([1, 2, 3]);

            var versions = dbContext.Database
                .GetCollection<SchemaVersion>("_schema_version")
                .FindAll()
                .OrderBy(v => v.Version)
                .ToList();
            versions.Count.ShouldBe(3);
            versions[0].Version.ShouldBe(1);
            versions[1].Version.ShouldBe(2);
            versions[2].Version.ShouldBe(3);
        }
        finally
        {
            cleanup();
        }
    }

    [Test]
    public async Task StartAsync_WithNoMigrations_DoesNothing()
    {
        var (_, dbContext, cleanup) = CreateTempDb();
        try
        {
            // Arrange
            var logger = CreateLogger<DatabaseMigrationRunner>();
            var runner = new DatabaseMigrationRunner(dbContext, [], logger);

            // Act
            await runner.StartAsync(CancellationToken.None);

            // Assert
            var versions = dbContext.Database
                .GetCollection<SchemaVersion>("_schema_version")
                .FindAll()
                .ToList();
            versions.ShouldBeEmpty();

            var logs = LogCollector.GetSnapshot();
            logs.ShouldContain(r => r.Message.Contains("up to date"));
        }
        finally
        {
            cleanup();
        }
    }

    [Test]
    public async Task StartAsync_SkipsAlreadyAppliedMigrations()
    {
        var (_, dbContext, cleanup) = CreateTempDb();
        try
        {
            // Arrange - pre-apply migration V1
            var versionCollection = dbContext.Database.GetCollection<SchemaVersion>("_schema_version");
            versionCollection.Insert(new SchemaVersion
            {
                Id = ObjectId.NewObjectId(),
                Version = 1,
                Description = "Already applied",
                AppliedAt = DateTime.UtcNow
            });

            var appliedVersions = new List<int>();
            var migrations = new IDatabaseMigration[]
            {
                new FakeMigration(1, "First", (_, _) => { appliedVersions.Add(1); return Task.CompletedTask; }),
                new FakeMigration(2, "Second", (_, _) => { appliedVersions.Add(2); return Task.CompletedTask; }),
            };
            var logger = CreateLogger<DatabaseMigrationRunner>();
            var runner = new DatabaseMigrationRunner(dbContext, migrations, logger);

            // Act
            await runner.StartAsync(CancellationToken.None);

            // Assert - only V2 should have been applied
            appliedVersions.ShouldBe([2]);

            var versions = versionCollection.FindAll().OrderBy(v => v.Version).ToList();
            versions.Count.ShouldBe(2);
            versions[0].Version.ShouldBe(1);
            versions[1].Version.ShouldBe(2);
        }
        finally
        {
            cleanup();
        }
    }

    [Test]
    public async Task StartAsync_OnMigrationFailure_ThrowsAndStopsStartup()
    {
        var (_, dbContext, cleanup) = CreateTempDb();
        try
        {
            // Arrange
            var appliedVersions = new List<int>();
            var migrations = new IDatabaseMigration[]
            {
                new FakeMigration(1, "OK", (_, _) => { appliedVersions.Add(1); return Task.CompletedTask; }),
                new FakeMigration(2, "Fails", (_, _) => throw new InvalidOperationException("Migration error")),
                new FakeMigration(3, "Never reached", (_, _) => { appliedVersions.Add(3); return Task.CompletedTask; }),
            };
            var logger = CreateLogger<DatabaseMigrationRunner>();
            var runner = new DatabaseMigrationRunner(dbContext, migrations, logger);

            // Act & Assert
            var ex = await Should.ThrowAsync<InvalidOperationException>(
                () => runner.StartAsync(CancellationToken.None));
            ex.Message.ShouldBe("Migration error");

            // V1 applied, V2 failed, V3 never reached
            appliedVersions.ShouldBe([1]);

            var versions = dbContext.Database
                .GetCollection<SchemaVersion>("_schema_version")
                .FindAll()
                .ToList();
            versions.Count.ShouldBe(1);
            versions[0].Version.ShouldBe(1);
        }
        finally
        {
            cleanup();
        }
    }

    private class FakeMigration(int version, string description, Func<ILiteDatabase, CancellationToken, Task>? action = null)
        : IDatabaseMigration
    {
        public int Version => version;
        public string Description => description;

        public Task MigrateAsync(ILiteDatabase database, CancellationToken ct)
            => action?.Invoke(database, ct) ?? Task.CompletedTask;
    }
}
