using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Persistence;
using LiteDB;
using Shouldly;

namespace ChangeDetection.Tests.Persistence;

public class LiteDbRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly LiteDbContext _context;
    private readonly LiteDbRepository<WatchedSite> _repository;

    public LiteDbRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        _context = new LiteDbContext(_dbPath);
        _repository = new LiteDbRepository<WatchedSite>(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task InsertAsync_NewEntity_AssignsId()
    {
        // Arrange
        var entity = new WatchedSite { Url = "https://example.com" };

        // Act
        await _repository.InsertAsync(entity, CancellationToken.None);

        // Assert
        entity.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
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

    [Fact]
    public async Task GetByIdAsync_NonExistingEntity_ReturnsNull()
    {
        // Act
        var result = await _repository.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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
/// </summary>
public class LiteDbContextTests : IDisposable
{
    private readonly string _dbPath;
    private LiteDbContext? _context;

    public LiteDbContextTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
    }

    public void Dispose()
    {
        _context?.Dispose();
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    [Fact]
    public void Database_IsAccessible()
    {
        // Act
        _context = new LiteDbContext(_dbPath);
        var db = _context.Database;

        // Assert
        db.ShouldNotBeNull();
    }

    [Fact]
    public void Database_CanGetCollection()
    {
        // Act
        _context = new LiteDbContext(_dbPath);
        var collection = _context.Database.GetCollection<WatchedSite>("watches");

        // Assert
        collection.ShouldNotBeNull();
    }

    [Fact]
    public void Database_CanStoreAndRetrieveChangeSnapshot()
    {
        // Arrange
        _context = new LiteDbContext(_dbPath);
        var collection = _context.Database.GetCollection<ChangeSnapshot>("snapshots");
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
    }

    [Fact]
    public void Database_CanStoreAndRetrieveChangeEvent()
    {
        // Arrange
        _context = new LiteDbContext(_dbPath);
        var collection = _context.Database.GetCollection<ChangeEvent>("events");
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
    }

    [Fact]
    public void Database_CanQueryByWatchedSiteId()
    {
        // Arrange
        _context = new LiteDbContext(_dbPath);
        var collection = _context.Database.GetCollection<ChangeSnapshot>("snapshots");
        var watchId = Guid.NewGuid();
        
        collection.Insert(new ChangeSnapshot { WatchedSiteId = watchId, ContentHash = "hash1", Content = "content1" });
        collection.Insert(new ChangeSnapshot { WatchedSiteId = watchId, ContentHash = "hash2", Content = "content2" });
        collection.Insert(new ChangeSnapshot { WatchedSiteId = Guid.NewGuid(), ContentHash = "hash3", Content = "content3" });

        // Act
        var results = collection.Find(x => x.WatchedSiteId == watchId).ToList();

        // Assert
        results.Count.ShouldBe(2);
    }
}
