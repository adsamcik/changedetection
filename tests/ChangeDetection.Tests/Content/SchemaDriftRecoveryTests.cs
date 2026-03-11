using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services;
using ChangeDetection.Services.Persistence;
using LiteDB;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Content;

/// <summary>
/// Tests for schema drift auto-recovery in ServerWatchService.CheckForChangesAsync.
/// Verifies the TryAutoResolveSchemaDriftAsync private method behavior through the public API.
/// </summary>
[Category("Unit")]
public class SchemaDriftRecoveryTests : TestBase
{
    private readonly LiteDbContext _dbContext;
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
    private readonly ServerWatchService _sut;

    private const string TestHtml = "<html><body><div class='items'><div class='item'>Product A</div></div></body></html>";

    public SchemaDriftRecoveryTests()
    {
        var mockDatabase = Substitute.For<ILiteDatabase>();
        _dbContext = Substitute.ForPartsOf<LiteDbContext>("Filename=:memory:");
        _dbContext.Database.Returns(mockDatabase);
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
            CreateLogger<ServerWatchService>());
    }

    private static WatchedSite CreateSchemaWatch(
        bool autoResolutionEnabled = true,
        int autoResolutionAttempts = 0,
        int maxAutoResolutionAttempts = 3)
    {
        var watch = new WatchedSite
        {
            Url = "https://example.com/products",
            Name = "Schema Drift Test Watch",
            SchemaEnabled = true,
            Schema = new ExtractionSchema
            {
                ItemSelector = ".item",
                Fields = [new SchemaField { Name = "title", Selector = ".title" }],
                Version = 1
            },
            AutoErrorResolutionEnabled = autoResolutionEnabled,
            AutoResolutionAttempts = autoResolutionAttempts,
            MaxAutoResolutionAttempts = maxAutoResolutionAttempts
        };
        return watch;
    }

    private void SetupBaseMocks(WatchedSite watch)
    {
        _watchRepo.GetByIdAsync(watch.Id, Arg.Any<CancellationToken>()).Returns(watch);

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = true, Html = TestHtml, HttpStatusCode = 200, DurationMs = 100 });

        _contentExtractor.ExtractText(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("Product A");
        _contentExtractor.ComputeHash(Arg.Any<string>())
            .Returns("hash123");

        _deduplicationService.CheckForDuplicateAsync(Arg.Any<DeduplicationRequest>(), Arg.Any<CancellationToken>())
            .Returns(DeduplicationResult.NotDuplicate());

        // No previous snapshot for schema drift lookup
        _snapshotRepo.FirstOrDefaultOrderedDescAsync(
                Arg.Any<System.Linq.Expressions.Expression<Func<ChangeSnapshot, bool>>>(),
                Arg.Any<System.Linq.Expressions.Expression<Func<ChangeSnapshot, object>>>(),
                Arg.Any<CancellationToken>())
            .ReturnsNull();
    }

    [Test]
    public async Task CheckForChanges_SchemaDriftDetected_CallsErrorResolutionWithSchemaDriftType()
    {
        // Arrange
        var watch = CreateSchemaWatch();
        SetupBaseMocks(watch);

        _objectExtractionService.ExtractAsync(Arg.Any<string>(), Arg.Any<ExtractionSchema>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectExtractionResult { Success = false, DriftDetected = true, Error = "Item selector '.item' matched 0 elements" });

        ErrorResolutionContext? capturedContext = null;
        _errorResolutionService.TryResolveAsync(Arg.Any<ErrorResolutionContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedContext = callInfo.ArgAt<ErrorResolutionContext>(0);
                return Task.FromResult(new ErrorResolutionResult
                {
                    IsResolved = false,
                    Diagnosis = "Could not find matching selector",
                    AutoFixApplied = false
                });
            });

        // Act
        await _sut.CheckForChangesAsync(watch.Id);

        // Assert
        await _errorResolutionService.Received(1).TryResolveAsync(
            Arg.Any<ErrorResolutionContext>(), Arg.Any<CancellationToken>());
        capturedContext.ShouldNotBeNull();
        capturedContext!.ErrorType.ShouldBe(ErrorType.SchemaDrift);
        capturedContext.Watch.Id.ShouldBe(watch.Id);
    }

    [Test]
    public async Task CheckForChanges_SchemaDriftResolved_UpdatesItemSelectorAndIncrementsVersion()
    {
        // Arrange
        var watch = CreateSchemaWatch();
        SetupBaseMocks(watch);

        _objectExtractionService.ExtractAsync(Arg.Any<string>(), Arg.Any<ExtractionSchema>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectExtractionResult { Success = false, DriftDetected = true, Error = "Drift detected" });

        _errorResolutionService.TryResolveAsync(Arg.Any<ErrorResolutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new ErrorResolutionResult
            {
                IsResolved = true,
                AutoFixApplied = true,
                Diagnosis = "Item selector changed from .item to .product",
                NewItemSelector = ".product",
                Confidence = 0.92f,
                RequiresUserApproval = false
            });

        // Act
        await _sut.CheckForChangesAsync(watch.Id);

        // Assert
        watch.Schema!.ItemSelector.ShouldBe(".product");
        watch.Schema.Version.ShouldBe(2);
        watch.SelectorHistory.Count.ShouldBe(1);
        watch.SelectorHistory[0].PreviousCssSelector.ShouldBe(".item");
        watch.SelectorHistory[0].ChangeReason.ShouldBe("Schema drift auto-recovery");
    }

    [Test]
    public async Task CheckForChanges_SchemaDriftResolutionFailed_SchemaUnchanged()
    {
        // Arrange
        var watch = CreateSchemaWatch();
        SetupBaseMocks(watch);

        _objectExtractionService.ExtractAsync(Arg.Any<string>(), Arg.Any<ExtractionSchema>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectExtractionResult { Success = false, DriftDetected = true, Error = "Drift detected" });

        _errorResolutionService.TryResolveAsync(Arg.Any<ErrorResolutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new ErrorResolutionResult
            {
                IsResolved = false,
                AutoFixApplied = false,
                Diagnosis = "Cannot determine new selector"
            });

        // Act
        await _sut.CheckForChangesAsync(watch.Id);

        // Assert
        watch.Schema!.ItemSelector.ShouldBe(".item");
        watch.Schema.Version.ShouldBe(1);
        watch.SelectorHistory.ShouldBeEmpty();
    }

    [Test]
    public async Task CheckForChanges_SchemaDriftResolvedButRequiresApproval_SetsLastErrorDuringResolution()
    {
        // Arrange
        var watch = CreateSchemaWatch();
        SetupBaseMocks(watch);

        _objectExtractionService.ExtractAsync(Arg.Any<string>(), Arg.Any<ExtractionSchema>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectExtractionResult { Success = false, DriftDetected = true, Error = "Drift detected" });

        _errorResolutionService.TryResolveAsync(Arg.Any<ErrorResolutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new ErrorResolutionResult
            {
                IsResolved = true,
                AutoFixApplied = false,
                RequiresUserApproval = true,
                Diagnosis = "Major structure change detected",
                Confidence = 0.75f,
                NewItemSelector = ".new-product"
            });

        // Capture the LastError value during intermediate UpdateAsync calls
        string? capturedLastError = null;
        _watchRepo.UpdateAsync(Arg.Any<WatchedSite>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(callInfo =>
            {
                var updatedWatch = callInfo.ArgAt<WatchedSite>(0);
                if (updatedWatch.LastError != null && updatedWatch.LastError.Contains("Schema drift"))
                    capturedLastError = updatedWatch.LastError;
            });

        // Act
        await _sut.CheckForChangesAsync(watch.Id);

        // Assert
        watch.Schema!.ItemSelector.ShouldBe(".item"); // Unchanged
        watch.Schema.Version.ShouldBe(1); // Unchanged
        capturedLastError.ShouldNotBeNull();
        capturedLastError.ShouldContain("Schema drift detected");
        capturedLastError.ShouldContain("Review and approve");
    }

    [Test]
    public async Task CheckForChanges_AutoResolutionDisabled_DoesNotCallErrorResolution()
    {
        // Arrange
        var watch = CreateSchemaWatch(autoResolutionEnabled: false);
        SetupBaseMocks(watch);

        _objectExtractionService.ExtractAsync(Arg.Any<string>(), Arg.Any<ExtractionSchema>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectExtractionResult { Success = false, DriftDetected = true, Error = "Drift detected" });

        // Act
        await _sut.CheckForChangesAsync(watch.Id);

        // Assert
        await _errorResolutionService.DidNotReceive().TryResolveAsync(
            Arg.Any<ErrorResolutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CheckForChanges_MaxResolutionAttemptsExceeded_DoesNotCallErrorResolution()
    {
        // Arrange: maxAutoResolutionAttempts=0 prevents any auto-resolution attempt
        // (AutoResolutionAttempts gets reset to 0 on successful text extraction,
        // so we test the guard via MaxAutoResolutionAttempts=0 boundary)
        var watch = CreateSchemaWatch(autoResolutionAttempts: 0, maxAutoResolutionAttempts: 0);
        SetupBaseMocks(watch);

        _objectExtractionService.ExtractAsync(Arg.Any<string>(), Arg.Any<ExtractionSchema>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectExtractionResult { Success = false, DriftDetected = true, Error = "Drift detected" });

        // Act
        await _sut.CheckForChangesAsync(watch.Id);

        // Assert
        await _errorResolutionService.DidNotReceive().TryResolveAsync(
            Arg.Any<ErrorResolutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CheckForChanges_ResolutionSucceeds_RetriesExtraction()
    {
        // Arrange
        var watch = CreateSchemaWatch();
        SetupBaseMocks(watch);

        var callCount = 0;
        _objectExtractionService.ExtractAsync(Arg.Any<string>(), Arg.Any<ExtractionSchema>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 1)
                    return new ObjectExtractionResult { Success = false, DriftDetected = true, Error = "Drift detected" };

                return new ObjectExtractionResult
                {
                    Success = true,
                    Objects = [new ExtractedObject { Fields = new Dictionary<string, string?> { ["title"] = "Product A" } }]
                };
            });

        _errorResolutionService.TryResolveAsync(Arg.Any<ErrorResolutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new ErrorResolutionResult
            {
                IsResolved = true,
                AutoFixApplied = true,
                Diagnosis = "Fixed selector",
                NewItemSelector = ".product",
                Confidence = 0.95f
            });

        // Act
        await _sut.CheckForChangesAsync(watch.Id);

        // Assert
        callCount.ShouldBe(2);
        await _objectExtractionService.Received(2).ExtractAsync(
            Arg.Any<string>(), Arg.Any<ExtractionSchema>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CheckForChanges_ResolutionSucceedsButRetryFails_SnapshotStillHasDrift()
    {
        // Arrange
        var watch = CreateSchemaWatch();
        SetupBaseMocks(watch);

        _objectExtractionService.ExtractAsync(Arg.Any<string>(), Arg.Any<ExtractionSchema>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectExtractionResult { Success = false, DriftDetected = true, Error = "Drift detected" });

        _errorResolutionService.TryResolveAsync(Arg.Any<ErrorResolutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new ErrorResolutionResult
            {
                IsResolved = true,
                AutoFixApplied = true,
                Diagnosis = "Attempted fix",
                NewItemSelector = ".new-item",
                Confidence = 0.90f
            });

        ChangeSnapshot? savedSnapshot = null;
        _snapshotRepo.InsertAsync(Arg.Any<ChangeSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                savedSnapshot = callInfo.ArgAt<ChangeSnapshot>(0);
                return Task.CompletedTask;
            });

        // Act
        await _sut.CheckForChangesAsync(watch.Id);

        // Assert
        savedSnapshot.ShouldNotBeNull();
        savedSnapshot!.SchemaDriftDetected.ShouldBeTrue();
    }
}
