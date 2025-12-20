using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services;
using ChangeDetection.Services.Persistence;
using LiteDB;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace ChangeDetection.Tests.Services;

public class ServerWatchServiceTests
{
    private readonly LiteDbContext _dbContext;
    private readonly ILiteDatabase _mockDatabase;
    private readonly IRepository<WatchedSite> _watchRepo;
    private readonly IRepository<ChangeSnapshot> _snapshotRepo;
    private readonly IRepository<ChangeEvent> _eventRepo;
    private readonly IContentFetcher _contentFetcher;
    private readonly IContentExtractor _contentExtractor;
    private readonly IDiffService _diffService;
    private readonly IObjectExtractionService _objectExtractionService;
    private readonly IObjectDiffService _objectDiffService;
    private readonly IErrorResolutionService _errorResolutionService;
    private readonly IChangeAnalyzer _changeAnalyzer;
    private readonly IContentEnricher _contentEnricher;
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
        _contentFetcher = Substitute.For<IContentFetcher>();
        _contentExtractor = Substitute.For<IContentExtractor>();
        _diffService = Substitute.For<IDiffService>();
        _objectExtractionService = Substitute.For<IObjectExtractionService>();
        _objectDiffService = Substitute.For<IObjectDiffService>();
        _errorResolutionService = Substitute.For<IErrorResolutionService>();
        _changeAnalyzer = Substitute.For<IChangeAnalyzer>();
        _contentEnricher = Substitute.For<IContentEnricher>();
        _priceTrackingService = Substitute.For<IPriceTrackingService>();
        _logger = Substitute.For<ILogger<ServerWatchService>>();

        _sut = new ServerWatchService(
            _dbContext,
            _watchRepo,
            _snapshotRepo,
            _eventRepo,
            _contentFetcher,
            _contentExtractor,
            _diffService,
            _objectExtractionService,
            _objectDiffService,
            _errorResolutionService,
            _changeAnalyzer,
            _contentEnricher,
            _priceTrackingService,
            _logger);
    }

    [Fact]
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
    }

    [Fact]
    public async Task GetByIdAsync_NonExistingWatch_ReturnsNull()
    {
        // Arrange
        var watchId = Guid.NewGuid();
        _watchRepo.GetByIdAsync(watchId, Arg.Any<CancellationToken>()).Returns((WatchedSite?)null);

        // Act
        var result = await _sut.GetByIdAsync(watchId);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
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
    }

    [Fact]
    public async Task CreateWatchAsync_ValidRequest_CreatesWatch()
    {
        // Arrange
        var request = new CreateWatchRequest
        {
            Url = "https://example.com",
            Name = "Test Watch",
            CheckInterval = TimeSpan.FromHours(1)
        };

        // Act
        var result = await _sut.CreateWatchAsync(request);

        // Assert
        result.ShouldNotBeNull();
        result.Url.ShouldBe("https://example.com");
        result.Name.ShouldBe("Test Watch");
        await _watchRepo.Received(1).InsertAsync(Arg.Any<WatchedSite>(), Arg.Any<CancellationToken>());
    }

    [Fact]
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

        // Act
        var result = await _sut.CreateWatchAsync(request);

        // Assert
        result.FetchSettings.UseJavaScript.ShouldBeTrue();
    }

    [Fact]
    public async Task UpdateWatchAsync_ExistingWatch_UpdatesValues()
    {
        // Arrange
        var watch = new WatchedSite { Url = "https://example.com" };

        // Act
        await _sut.UpdateWatchAsync(watch);

        // Assert
        await _watchRepo.Received(1).UpdateAsync(watch, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteWatchAsync_ExistingWatch_DeletesWatch()
    {
        // Arrange
        var watchId = Guid.NewGuid();

        // Act
        await _sut.DeleteWatchAsync(watchId);

        // Assert
        await _watchRepo.Received(1).DeleteAsync(watchId, Arg.Any<CancellationToken>());
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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

        // Act
        var result = await _sut.CheckForChangesAsync(watchId);

        // Assert
        await _snapshotRepo.Received(1).InsertAsync(Arg.Any<ChangeSnapshot>(), Arg.Any<CancellationToken>());
        result.ShouldBeNull(); // First check, no previous snapshot to compare
    }

    [Fact]
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

        // Act
        var result = await _sut.CheckForChangesAsync(watchId);

        // Assert
        await _eventRepo.Received(1).InsertAsync(Arg.Any<ChangeEvent>(), Arg.Any<CancellationToken>());
        result.ShouldNotBeNull();
    }

    [Fact]
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

        // Act
        var result = await _sut.CheckForChangesAsync(watchId);

        // Assert
        result.ShouldBeNull();
        await _eventRepo.DidNotReceive().InsertAsync(Arg.Any<ChangeEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
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
