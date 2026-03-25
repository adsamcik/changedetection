using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Persistence;
using LiteDB;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Persistence;

public class LiteDbRepositoryTests
{
    private string _dbPath = null!;
    private LiteDbContext _context = null!;
    private LiteDbRepository<WatchedSite> _repository = null!;

    [Before(Test)]
    public async Task SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        _context = new LiteDbContext(_dbPath);
        _repository = new LiteDbRepository<WatchedSite>(new ThreadSafeLiteDbContext(_context));
        await Task.CompletedTask;
    }

    [After(Test)]
    public async Task TearDown()
    {
        _context.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
        await Task.CompletedTask;
    }

    [Test]
    public async Task InsertAsync_NewEntity_AssignsId()
    {
        // Arrange
        var entity = new WatchedSite { Url = "https://example.com" };

        // Act
        await _repository.InsertAsync(entity, CancellationToken.None);

        // Assert
        entity.Id.ShouldNotBe(Guid.Empty);
    }

    [Test]
    public async Task GetByIdAsync_ExistingEntity_ReturnsEntity()
    {
        // Arrange
        var entity = new WatchedSite { Url = "https://example.com", Name = "Test" };
        await _repository.InsertAsync(entity, CancellationToken.None);

        // Act
        var result = await _repository.GetByIdAsync(entity.Id, CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        result.Url.ShouldBe("https://example.com");
        result.Name.ShouldBe("Test");
    }

    [Test]
    public async Task GetByIdAsync_NonExistingEntity_ReturnsNull()
    {
        // Act
        var result = await _repository.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.ShouldBeNull();
    }

    [Test]
    public async Task GetAllAsync_MultipleEntities_ReturnsAll()
    {
        // Arrange
        await _repository.InsertAsync(new WatchedSite { Url = "https://site1.com" }, CancellationToken.None);
        await _repository.InsertAsync(new WatchedSite { Url = "https://site2.com" }, CancellationToken.None);
        await _repository.InsertAsync(new WatchedSite { Url = "https://site3.com" }, CancellationToken.None);

        // Act
        var result = await _repository.GetAllAsync(CancellationToken.None);

        // Assert
        result.Count().ShouldBe(3);
    }

    [Test]
    public async Task UpdateAsync_ExistingEntity_UpdatesValues()
    {
        // Arrange
        var entity = new WatchedSite { Url = "https://example.com", Name = "Original" };
        await _repository.InsertAsync(entity, CancellationToken.None);

        // Act
        entity.Name = "Updated";
        await _repository.UpdateAsync(entity, CancellationToken.None);

        // Assert
        var result = await _repository.GetByIdAsync(entity.Id, CancellationToken.None);
        result!.Name.ShouldBe("Updated");
    }

    [Test]
    public async Task DeleteAsync_ExistingEntity_RemovesEntity()
    {
        // Arrange
        var entity = new WatchedSite { Url = "https://example.com" };
        await _repository.InsertAsync(entity, CancellationToken.None);

        // Act
        await _repository.DeleteAsync(entity.Id, CancellationToken.None);

        // Assert
        var result = await _repository.GetByIdAsync(entity.Id, CancellationToken.None);
        result.ShouldBeNull();
    }

    [Test]
    public async Task FindAsync_WithPredicate_ReturnsMatchingEntities()
    {
        // Arrange
        await _repository.InsertAsync(new WatchedSite { Url = "https://enabled1.com", IsEnabled = true }, CancellationToken.None);
        await _repository.InsertAsync(new WatchedSite { Url = "https://disabled.com", IsEnabled = false }, CancellationToken.None);
        await _repository.InsertAsync(new WatchedSite { Url = "https://enabled2.com", IsEnabled = true }, CancellationToken.None);

        // Act
        var result = await _repository.FindAsync(x => x.IsEnabled, CancellationToken.None);

        // Assert
        result.Count().ShouldBe(2);
        result.All(x => x.IsEnabled).ShouldBeTrue();
    }

    [Test]
    public async Task CountAsync_ReturnsCorrectCount()
    {
        // Arrange
        await _repository.InsertAsync(new WatchedSite { Url = "https://site1.com" }, CancellationToken.None);
        await _repository.InsertAsync(new WatchedSite { Url = "https://site2.com" }, CancellationToken.None);

        // Act
        var count = await _repository.CountAsync(x => true, CancellationToken.None);

        // Assert
        count.ShouldBe(2);
    }

    [Test]
    public async Task CountAsync_WithPredicate_ReturnsFilteredCount()
    {
        // Arrange
        await _repository.InsertAsync(new WatchedSite { Url = "https://active1.com", Status = WatchStatus.Active }, CancellationToken.None);
        await _repository.InsertAsync(new WatchedSite { Url = "https://paused.com", Status = WatchStatus.Paused }, CancellationToken.None);
        await _repository.InsertAsync(new WatchedSite { Url = "https://active2.com", Status = WatchStatus.Active }, CancellationToken.None);

        // Act
        var count = await _repository.CountAsync(x => x.Status == WatchStatus.Active, CancellationToken.None);

        // Assert
        count.ShouldBe(2);
    }

    [Test]
    public async Task ExistsAsync_ExistingEntity_ReturnsTrue()
    {
        // Arrange
        var entity = new WatchedSite { Url = "https://example.com" };
        await _repository.InsertAsync(entity, CancellationToken.None);

        // Act
        var exists = await _repository.ExistsAsync(x => x.Id == entity.Id, CancellationToken.None);

        // Assert
        exists.ShouldBeTrue();
    }

    [Test]
    public async Task ExistsAsync_NonExistingEntity_ReturnsFalse()
    {
        // Act
        var exists = await _repository.ExistsAsync(x => x.Url == "https://nonexistent.com", CancellationToken.None);

        // Assert
        exists.ShouldBeFalse();
    }
}

/// <summary>
/// Integration tests for LiteDbContext.
/// Uses local variables instead of instance fields to avoid TUnit0018 warnings
/// and ensure thread-safety in parallel test execution.
/// </summary>
public class LiteDbContextTests
{
    /// <summary>
    /// Creates a unique temp database path and returns a cleanup action.
    /// </summary>
    private static (string dbPath, Action cleanup) CreateTempDb()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        return (dbPath, () =>
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        });
    }

    [Test]
    public async Task Database_IsAccessible()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            // Act
            using var context = new LiteDbContext(dbPath);
            var db = context.Database;

            // Assert
            db.ShouldNotBeNull();
            await Task.CompletedTask;
        }
        finally
        {
            cleanup();
        }
    }

    [Test]
    public async Task Database_CanGetCollection()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            // Act
            using var context = new LiteDbContext(dbPath);
            var collection = context.Database.GetCollection<WatchedSite>("watches");

            // Assert
            collection.ShouldNotBeNull();
            await Task.CompletedTask;
        }
        finally
        {
            cleanup();
        }
    }

    [Test]
    public async Task Database_CanStoreAndRetrieveChangeSnapshot()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            // Arrange
            using var context = new LiteDbContext(dbPath);
            var collection = context.Database.GetCollection<ChangeSnapshot>("snapshots");
            var snapshot = new ChangeSnapshot
            {
                WatchedSiteId = Guid.NewGuid(),
                ContentHash = "abc123",
                Content = "Test content"
            };

            // Act
            collection.Insert(snapshot);
            var retrieved = collection.FindById(snapshot.Id);

            // Assert
            retrieved.ShouldNotBeNull();
            retrieved.ContentHash.ShouldBe("abc123");
            retrieved.Content.ShouldBe("Test content");
            await Task.CompletedTask;
        }
        finally
        {
            cleanup();
        }
    }

    [Test]
    public async Task Database_CanStoreAndRetrieveChangeEvent()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            // Arrange
            using var context = new LiteDbContext(dbPath);
            var collection = context.Database.GetCollection<ChangeEvent>("events");
            var changeEvent = new ChangeEvent
            {
                WatchedSiteId = Guid.NewGuid(),
                PreviousSnapshotId = Guid.NewGuid(),
                CurrentSnapshotId = Guid.NewGuid(),
                ChangeType = ChangeType.Modified,
                Importance = ChangeImportance.High
            };

            // Act
            collection.Insert(changeEvent);
            var retrieved = collection.FindById(changeEvent.Id);

            // Assert
            retrieved.ShouldNotBeNull();
            retrieved.ChangeType.ShouldBe(ChangeType.Modified);
            retrieved.Importance.ShouldBe(ChangeImportance.High);
            await Task.CompletedTask;
        }
        finally
        {
            cleanup();
        }
    }

    [Test]
    public async Task Database_CanQueryByWatchedSiteId()
    {
        var (dbPath, cleanup) = CreateTempDb();
        try
        {
            // Arrange
            using var context = new LiteDbContext(dbPath);
            var collection = context.Database.GetCollection<ChangeSnapshot>("snapshots");
            var watchId = Guid.NewGuid();
            
            collection.Insert(new ChangeSnapshot { WatchedSiteId = watchId, ContentHash = "hash1", Content = "content1" });
            collection.Insert(new ChangeSnapshot { WatchedSiteId = watchId, ContentHash = "hash2", Content = "content2" });
            collection.Insert(new ChangeSnapshot { WatchedSiteId = Guid.NewGuid(), ContentHash = "hash3", Content = "content3" });

            // Act
            var results = collection.Find(x => x.WatchedSiteId == watchId).ToList();

            // Assert
            results.Count.ShouldBe(2);
            await Task.CompletedTask;
        }
        finally
        {
            cleanup();
        }
    }
}
