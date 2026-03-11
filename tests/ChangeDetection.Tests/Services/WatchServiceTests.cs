using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services;
using ChangeDetection.Services.Persistence;
using LiteDB;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services;

public class ServerWatchServiceTests
{
    private readonly LiteDbContext _dbContext;
    private readonly ILiteDatabase _mockDatabase;
    private readonly IRepository<WatchedSite> _watchRepo;
    private readonly IRepository<ChangeSnapshot> _snapshotRepo;
    private readonly IRepository<ChangeEvent> _eventRepo;
    private readonly IRepository<FieldValueHistory> _fieldValueHistoryRepo;
    private readonly IRepository<NotificationOutboxEntry> _notificationOutboxRepo;
    private readonly IRepository<BlockExecutionSnapshotEntity> _blockSnapshotRepo;
    private readonly IPriceHistoryRepository _priceHistoryRepo;
    private readonly IContentFetcher _contentFetcher;
    private readonly IContentExtractor _contentExtractor;
    private readonly IDiffService _diffService;
    private readonly IObjectExtractionService _objectExtractionService;
    private readonly IObjectDiffService _objectDiffService;
    private readonly IErrorResolutionService _errorResolutionService;
    private readonly IChangeAnalyzer _changeAnalyzer;
    private readonly IContentEnricher _contentEnricher;
    private readonly IDeduplicationService _deduplicationService;
    private readonly IPriceTrackingService _priceTrackingService;
    private readonly ILogger<ServerWatchService> _logger;
    private readonly ServerWatchService _sut;

    public ServerWatchServiceTests()
    {
        _mockDatabase = Substitute.For<ILiteDatabase>();
        _dbContext = Substitute.ForPartsOf<LiteDbContext>("Filename=:memory:");
        _dbContext.Database.Returns(_mockDatabase);
        _watchRepo = Substitute.For<IRepository<WatchedSite>>();
        _snapshotRepo = Substitute.For<IRepository<ChangeSnapshot>>();
        _eventRepo = Substitute.For<IRepository<ChangeEvent>>();
        _fieldValueHistoryRepo = Substitute.For<IRepository<FieldValueHistory>>();
        _notificationOutboxRepo = Substitute.For<IRepository<NotificationOutboxEntry>>();
        _blockSnapshotRepo = Substitute.For<IRepository<BlockExecutionSnapshotEntity>>();
        _priceHistoryRepo = Substitute.For<IPriceHistoryRepository>();
        _contentFetcher = Substitute.For<IContentFetcher>();
        _contentExtractor = Substitute.For<IContentExtractor>();
        _diffService = Substitute.For<IDiffService>();
        _objectExtractionService = Substitute.For<IObjectExtractionService>();
        _objectDiffService = Substitute.For<IObjectDiffService>();
        _errorResolutionService = Substitute.For<IErrorResolutionService>();
        _changeAnalyzer = Substitute.For<IChangeAnalyzer>();
        _contentEnricher = Substitute.For<IContentEnricher>();
        _deduplicationService = Substitute.For<IDeduplicationService>();
        _priceTrackingService = Substitute.For<IPriceTrackingService>();
        _logger = Substitute.For<ILogger<ServerWatchService>>();

        _sut = new ServerWatchService(
            _dbContext,
            _watchRepo,
            _snapshotRepo,
            _eventRepo,
            _fieldValueHistoryRepo,
            _notificationOutboxRepo,
            _blockSnapshotRepo,
            _priceHistoryRepo,
            _contentFetcher,
            _contentExtractor,
            _diffService,
            _objectExtractionService,
            _objectDiffService,
            _errorResolutionService,
            _changeAnalyzer,
            _contentEnricher,
            _deduplicationService,
            _priceTrackingService,
            Substitute.For<IRepository<WatchGroup>>(),
            _logger);
    }

    [Test]
    public async Task GetByIdAsync_ExistingWatch_ReturnsWatch()
    {
        // Arrange
        var watchId = Guid.NewGuid();
        var watch = new WatchedSite { Id = watchId, Url = "https://example.com" };
        _watchRepo.GetByIdAsync(watchId, Arg.Any<CancellationToken>()).Returns(watch);

        // Act
        var result = await _sut.GetByIdAsync(watchId);

        // Assert
        result.ShouldNotBeNull();
        result.Id.ShouldBe(watchId);
        await _watchRepo.Received(1).GetByIdAsync(
            Arg.Is<Guid>(id => id == watchId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetByIdAsync_PropagatesCancellationToken()
    {
        // Arrange
        var watchId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        _watchRepo.GetByIdAsync(watchId, token).Returns((WatchedSite?)null);

        // Act
        await _sut.GetByIdAsync(watchId, token);

        // Assert
        await _watchRepo.Received(1).GetByIdAsync(watchId, token);
    }

    [Test]
    public async Task GetByIdAsync_NonExistingWatch_ReturnsNull()
    {
        // Arrange
        var watchId = Guid.NewGuid();
        _watchRepo.GetByIdAsync(watchId, Arg.Any<CancellationToken>()).Returns((WatchedSite?)null);

        // Act
        var result = await _sut.GetByIdAsync(watchId);

        // Assert
        result.ShouldBeNull();
        await _watchRepo.Received(1).GetByIdAsync(
            Arg.Is<Guid>(id => id == watchId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetAllAsync_ReturnsAllWatches()
    {
        // Arrange
        var watches = new List<WatchedSite>
        {
            new() { Url = "https://site1.com" },
            new() { Url = "https://site2.com" }
        };
        _watchRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(watches);

        // Act
        var result = await _sut.GetAllAsync();

        // Assert
        result.Count().ShouldBe(2);
        await _watchRepo.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetAllAsync_PropagatesCancellationToken()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        _watchRepo.GetAllAsync(token).Returns(Enumerable.Empty<WatchedSite>());

        // Act
        await _sut.GetAllAsync(token);

        // Assert
        await _watchRepo.Received(1).GetAllAsync(token);
    }

    [Test]
    public async Task CreateWatchAsync_ValidRequest_CreatesWatch()
    {
        // Arrange
        var request = new CreateWatchRequest
        {
            Url = "https://example.com",
            Name = "Test Watch",
            CheckInterval = TimeSpan.FromHours(1)
        };

        // Capture the WatchedSite passed to InsertAsync
        WatchedSite? capturedWatch = null;
        await _watchRepo.InsertAsync(
            Arg.Do<WatchedSite>(w => capturedWatch = w), Arg.Any<CancellationToken>());

        // Stub CheckForChangesAsync dependencies (called after insert)
        _watchRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((WatchedSite?)null); // Return null so initial check short-circuits

        // Act
        var result = await _sut.CreateWatchAsync(request);

        // Assert
        result.ShouldNotBeNull();
        result.Url.ShouldBe("https://example.com");
        result.Name.ShouldBe("Test Watch");
        await _watchRepo.Received(1).InsertAsync(Arg.Any<WatchedSite>(), Arg.Any<CancellationToken>());

        // Verify computed properties on the captured entity
        capturedWatch.ShouldNotBeNull();
        capturedWatch!.Id.ShouldNotBe(Guid.Empty);
        capturedWatch.CheckInterval.ShouldBe(TimeSpan.FromHours(1));
        capturedWatch.ScheduleSettings.ShouldNotBeNull();
        capturedWatch.ScheduleSettings.Mode.ShouldBe(CheckScheduleMode.Fixed);
        capturedWatch.ScheduleSettings.BaseInterval.ShouldBe(TimeSpan.FromHours(1));
        capturedWatch.Tags.ShouldBeEmpty();
        capturedWatch.IgnorePatterns.ShouldBeEmpty();
        capturedWatch.FetchSettings.ShouldNotBeNull();
    }

    [Test]
    public async Task CreateWatchAsync_WithFetchSettings_AppliesSettings()
    {
        // Arrange
        var request = new CreateWatchRequest
        {
            Url = "https://example.com",
            UseJavaScript = true,
            FetchSettings = new FetchSettings
            {
                UseJavaScript = true,
                TimeoutSeconds = 60
            }
        };

        // Capture the WatchedSite passed to InsertAsync
        WatchedSite? capturedWatch = null;
        await _watchRepo.InsertAsync(
            Arg.Do<WatchedSite>(w => capturedWatch = w), Arg.Any<CancellationToken>());

        // Stub CheckForChangesAsync dependencies
        _watchRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((WatchedSite?)null);

        // Act
        var result = await _sut.CreateWatchAsync(request);

        // Assert
        result.FetchSettings.UseJavaScript.ShouldBeTrue();

        // Verify the persisted entity received the explicit FetchSettings
        capturedWatch.ShouldNotBeNull();
        capturedWatch!.FetchSettings.UseJavaScript.ShouldBeTrue();
        capturedWatch.FetchSettings.TimeoutSeconds.ShouldBe(60);
    }

    [Test]
    public async Task UpdateWatchAsync_ExistingWatch_UpdatesValues()
    {
        // Arrange
        var watch = new WatchedSite { Url = "https://example.com" };
        var beforeUpdate = watch.UpdatedAt;

        // Act
        await _sut.UpdateWatchAsync(watch);

        // Assert — verifies the exact same watch object was passed through
        await _watchRepo.Received(1).UpdateAsync(
            Arg.Is<WatchedSite>(w => w == watch), Arg.Any<CancellationToken>());
        // Verify UpdatedAt was set by the service
        watch.UpdatedAt.ShouldBeGreaterThan(beforeUpdate);
    }

    [Test]
    public async Task DeleteWatchAsync_ExistingWatch_DeletesWatch()
    {
        // Arrange
        var watchId = Guid.NewGuid();
        _snapshotRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<ChangeSnapshot, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<ChangeSnapshot>());
        _mockDatabase.BeginTrans().Returns(true);
        _mockDatabase.Commit().Returns(true);

        // Act
        await _sut.DeleteWatchAsync(watchId);

        // Assert — verify the exact watchId was forwarded to delete
        await _watchRepo.Received(1).DeleteAsync(
            Arg.Is<Guid>(id => id == watchId), Arg.Any<CancellationToken>());
        // Verify cascading deletes also happened
        await _snapshotRepo.Received(1).DeleteManyAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<ChangeSnapshot, bool>>>(), Arg.Any<CancellationToken>());
        await _eventRepo.Received(1).DeleteManyAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<ChangeEvent, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnableWatchAsync_DisabledWatch_EnablesWatch()
    {
        // Arrange
        var watchId = Guid.NewGuid();
        var watch = new WatchedSite { Id = watchId, Url = "https://example.com", IsEnabled = false };
        _watchRepo.GetByIdAsync(watchId, Arg.Any<CancellationToken>()).Returns(watch);

        // Act
        await _sut.EnableWatchAsync(watchId);

        // Assert
        watch.IsEnabled.ShouldBeTrue();
        await _watchRepo.Received(1).UpdateAsync(watch, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DisableWatchAsync_EnabledWatch_DisablesWatch()
    {
        // Arrange
        var watchId = Guid.NewGuid();
        var watch = new WatchedSite { Id = watchId, Url = "https://example.com", IsEnabled = true };
        _watchRepo.GetByIdAsync(watchId, Arg.Any<CancellationToken>()).Returns(watch);

        // Act
        await _sut.DisableWatchAsync(watchId);

        // Assert
        watch.IsEnabled.ShouldBeFalse();
        await _watchRepo.Received(1).UpdateAsync(watch, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CheckForChangesAsync_FirstCheck_CreatesSnapshot()
    {
        // Arrange
        var watchId = Guid.NewGuid();
        var watch = new WatchedSite { Id = watchId, Url = "https://example.com" };
        _watchRepo.GetByIdAsync(watchId, Arg.Any<CancellationToken>()).Returns(watch);
        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = true, Html = "<p>content</p>" });
        _contentExtractor.ExtractText(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns("extracted");
        _contentExtractor.ComputeHash(Arg.Any<string>()).Returns("hash123");
        _snapshotRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<ChangeSnapshot, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<ChangeSnapshot>());
        
        // Setup deduplication to return not duplicate
        _deduplicationService.CheckForDuplicateAsync(Arg.Any<DeduplicationRequest>(), Arg.Any<CancellationToken>())
            .Returns(DeduplicationResult.NotDuplicate());

        // Act
        var result = await _sut.CheckForChangesAsync(watchId);

        // Assert
        await _snapshotRepo.Received(1).InsertAsync(Arg.Any<ChangeSnapshot>(), Arg.Any<CancellationToken>());
        result.ShouldBeNull(); // First check, no previous snapshot to compare
    }

    [Test]
    public async Task CheckForChangesAsync_ContentChanged_CreatesChangeEvent()
    {
        // Arrange
        var watchId = Guid.NewGuid();
        var watch = new WatchedSite { Id = watchId, Url = "https://example.com", LastContentHash = "oldhash" };
        var previousSnapshot = new ChangeSnapshot 
        { 
            Id = Guid.NewGuid(), 
            WatchedSiteId = watchId, 
            Content = "old content",
            ContentHash = "oldhash",
            CapturedAt = DateTime.UtcNow.AddHours(-1)
        };

        _watchRepo.GetByIdAsync(watchId, Arg.Any<CancellationToken>()).Returns(watch);
        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = true, Html = "new content" });
        _contentExtractor.ExtractText(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns("new extracted");
        _contentExtractor.ComputeHash("new extracted").Returns("newhash");
        
        // Setup deduplication to return not duplicate
        _deduplicationService.CheckForDuplicateAsync(Arg.Any<DeduplicationRequest>(), Arg.Any<CancellationToken>())
            .Returns(DeduplicationResult.NotDuplicate());
        
        _snapshotRepo.FirstOrDefaultOrderedDescAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<ChangeSnapshot, bool>>>(),
            Arg.Any<System.Linq.Expressions.Expression<Func<ChangeSnapshot, DateTime>>>(),
            Arg.Any<CancellationToken>())
            .Returns(previousSnapshot);
        _diffService.Compare(Arg.Any<string>(), Arg.Any<string>()).Returns(new DiffResult 
        { 
            HasChanges = true, 
            LinesAdded = 5, 
            LinesRemoved = 2 
        });
        _diffService.GenerateSummary(Arg.Any<DiffResult>()).Returns("5 lines added, 2 removed");
        _diffService.GenerateDiffHtml(Arg.Any<DiffResult>()).Returns("<diff>html</diff>");

        // Capture the ChangeEvent passed to InsertAsync
        ChangeEvent? capturedEvent = null;
        await _eventRepo.InsertAsync(
            Arg.Do<ChangeEvent>(e => capturedEvent = e), Arg.Any<CancellationToken>());

        // Act
        var result = await _sut.CheckForChangesAsync(watchId);

        // Assert
        await _eventRepo.Received(1).InsertAsync(Arg.Any<ChangeEvent>(), Arg.Any<CancellationToken>());
        result.ShouldNotBeNull();

        // Verify captured ChangeEvent properties
        capturedEvent.ShouldNotBeNull();
        capturedEvent!.WatchedSiteId.ShouldBe(watchId);
        capturedEvent.PreviousSnapshotId.ShouldBe(previousSnapshot.Id);
        capturedEvent.LinesAdded.ShouldBe(5);
        capturedEvent.LinesRemoved.ShouldBe(2);
        capturedEvent.DiffSummary.ShouldBe("5 lines added, 2 removed");
        capturedEvent.DiffHtml.ShouldBe("<diff>html</diff>");
        capturedEvent.ChangeType.ShouldNotBe(default);
    }

    [Test]
    public async Task CheckForChangesAsync_NoChanges_ReturnsNull()
    {
        // Arrange
        var watchId = Guid.NewGuid();
        var watch = new WatchedSite { Id = watchId, Url = "https://example.com", LastContentHash = "samehash" };
        var previousSnapshot = new ChangeSnapshot 
        { 
            Id = Guid.NewGuid(), 
            WatchedSiteId = watchId, 
            Content = "same content",
            ContentHash = "samehash",
            CapturedAt = DateTime.UtcNow.AddHours(-1)
        };

        _watchRepo.GetByIdAsync(watchId, Arg.Any<CancellationToken>()).Returns(watch);
        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = true, Html = "same content" });
        _contentExtractor.ExtractText(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns("same content");
        _contentExtractor.ComputeHash("same content").Returns("samehash");
        _snapshotRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<ChangeSnapshot, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { previousSnapshot });
        
        // Setup deduplication to detect exact hash match (content unchanged)
        _deduplicationService.CheckForDuplicateAsync(Arg.Any<DeduplicationRequest>(), Arg.Any<CancellationToken>())
            .Returns(DeduplicationResult.ExactHashMatch());

        // Act
        var result = await _sut.CheckForChangesAsync(watchId);

        // Assert
        result.ShouldBeNull();
        await _eventRepo.DidNotReceive().InsertAsync(Arg.Any<ChangeEvent>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetWatchesDueForCheckAsync_ReturnsOnlyDueWatches()
    {
        // Arrange - implementation uses GetAllAsync and filters in-memory
        var dueWatch = new WatchedSite 
        { 
            Url = "https://due.com", 
            IsEnabled = true, 
            Status = WatchStatus.Active,
            LastChecked = DateTime.UtcNow.AddHours(-2),
            CheckInterval = TimeSpan.FromHours(1)
        };
        var notDueWatch = new WatchedSite 
        { 
            Url = "https://notdue.com", 
            IsEnabled = true, 
            Status = WatchStatus.Active,
            LastChecked = DateTime.UtcNow.AddMinutes(-30),
            CheckInterval = TimeSpan.FromHours(1)
        };
        var disabledWatch = new WatchedSite 
        { 
            Url = "https://disabled.com", 
            IsEnabled = false, 
            Status = WatchStatus.Active,
            LastChecked = DateTime.UtcNow.AddHours(-2),
            CheckInterval = TimeSpan.FromHours(1)
        };

        _watchRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { dueWatch, notDueWatch, disabledWatch });

        // Act
        var result = await _sut.GetWatchesDueForCheckAsync();

        // Assert
        var resultList = result.ToList();
        resultList.Count.ShouldBe(1);
        resultList[0].Url.ShouldBe("https://due.com");
    }
}
