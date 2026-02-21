using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Pipeline;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services;

/// <summary>
/// Unit tests for PipelineQueueService.
/// Tests queue operations, rate limiting, worker operations, and dead letter queue.
/// </summary>
[Category("Unit")]
public class PipelineQueueServiceTests : TestBase, IDisposable
{
    private readonly IPipelineQueueRepository _repository;
    private readonly IOptionsMonitor<AppSettings> _settingsMonitor;
    private readonly AppSettings _settings;
    private readonly PipelineQueueService _sut;

    public PipelineQueueServiceTests()
    {
        _repository = Substitute.For<IPipelineQueueRepository>();
        _settings = new AppSettings
        {
            MaxPendingItemsPerUser = 10,
            MaxConcurrentItemsPerUser = 2
        };
        _settingsMonitor = Substitute.For<IOptionsMonitor<AppSettings>>();
        _settingsMonitor.CurrentValue.Returns(_settings);

        _sut = new PipelineQueueService(
            _repository,
            CreateLogger<PipelineQueueService>(),
            _settingsMonitor);
    }

    public void Dispose()
    {
        _sut.Dispose();
        GC.SuppressFinalize(this);
    }

    #region EnqueueProcessAsync Tests

    [Test]
    public async Task EnqueueProcessAsync_WithValidInput_CreatesQueueItemAndSignals()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var userInput = "Watch https://example.com";
        
        _repository.EnqueueAsync(Arg.Any<PipelineQueueItem>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<PipelineQueueItem>());

        // Act
        var itemId = await _sut.EnqueueProcessAsync(sessionId, ownerId, userInput);

        // Assert
        itemId.ShouldNotBe(Guid.Empty);
        await _repository.Received(1).EnqueueAsync(
            Arg.Is<PipelineQueueItem>(item =>
                item.SessionId == sessionId &&
                item.OwnerId == ownerId &&
                item.UserInput == userInput &&
                item.OperationType == PipelineOperationType.Process),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnqueueProcessAsync_PropagatesCancellationToken()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        _repository.EnqueueAsync(Arg.Any<PipelineQueueItem>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<PipelineQueueItem>());

        // Act
        await _sut.EnqueueProcessAsync(Guid.NewGuid(), Guid.NewGuid(), "test", ct: token);

        // Assert
        await _repository.Received(1).EnqueueAsync(Arg.Any<PipelineQueueItem>(), token);
    }

    [Test]
    public async Task EnqueueProcessAsync_WithOptions_SerializesOptionsToJson()
    {
        // Arrange
        var options = new PipelineOptions { UseJavaScript = true, MaxIterations = 5 };
        _repository.EnqueueAsync(Arg.Any<PipelineQueueItem>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<PipelineQueueItem>());

        // Act
        await _sut.EnqueueProcessAsync(Guid.NewGuid(), Guid.NewGuid(), "test", options);

        // Assert
        await _repository.Received(1).EnqueueAsync(
            Arg.Is<PipelineQueueItem>(item => 
                item.OptionsJson != null && 
                item.OptionsJson.Contains("UseJavaScript", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnqueueProcessAsync_WithPriority_SetsItemPriority()
    {
        // Arrange
        _repository.EnqueueAsync(Arg.Any<PipelineQueueItem>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<PipelineQueueItem>());

        // Act
        await _sut.EnqueueProcessAsync(Guid.NewGuid(), Guid.NewGuid(), "test", priority: 5);

        // Assert
        await _repository.Received(1).EnqueueAsync(
            Arg.Is<PipelineQueueItem>(item => item.Priority == 5),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region EnqueueContinueAsync Tests

    [Test]
    public async Task EnqueueContinueAsync_WithSession_SerializesSessionToJson()
    {
        // Arrange
        var session = new PipelineSession { OriginalInput = "test input" };
        _repository.EnqueueAsync(Arg.Any<PipelineQueueItem>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<PipelineQueueItem>());

        // Act
        await _sut.EnqueueContinueAsync(Guid.NewGuid(), Guid.NewGuid(), session, "feedback");

        // Assert
        await _repository.Received(1).EnqueueAsync(
            Arg.Is<PipelineQueueItem>(item =>
                item.OperationType == PipelineOperationType.ContinueWithFeedback &&
                item.Feedback == "feedback" &&
                item.SessionJson != null),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region EnqueueRecoveryAsync Tests

    [Test]
    public async Task EnqueueRecoveryAsync_WithFailedResult_SerializesAllDataToJson()
    {
        // Arrange
        var session = new PipelineSession();
        var failedResult = new PipelineResult { ErrorMessage = "Failed" };
        var options = new PipelineOptions();
        _repository.EnqueueAsync(Arg.Any<PipelineQueueItem>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<PipelineQueueItem>());

        // Act
        await _sut.EnqueueRecoveryAsync(Guid.NewGuid(), Guid.NewGuid(), session, failedResult, options);

        // Assert
        await _repository.Received(1).EnqueueAsync(
            Arg.Is<PipelineQueueItem>(item =>
                item.OperationType == PipelineOperationType.RecoverFromFailure &&
                item.SessionJson != null &&
                item.FailedResultJson != null &&
                item.OptionsJson != null),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region TryEnqueueProcessAsync Tests

    [Test]
    public async Task TryEnqueueProcessAsync_WithinRateLimit_ReturnsSuccess()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var enqueuedItem = new PipelineQueueItem { SessionId = sessionId, OwnerId = ownerId, OperationType = PipelineOperationType.Process };
        
        _repository.TryEnqueueWithLimitAsync(
            Arg.Any<PipelineQueueItem>(), 
            Arg.Any<int>(), 
            Arg.Any<int>(), 
            Arg.Any<CancellationToken>())
            .Returns((true, enqueuedItem, null));

        // Act
        var result = await _sut.TryEnqueueProcessAsync(sessionId, ownerId, "test");

        // Assert
        result.Success.ShouldBeTrue();
        result.ItemId.ShouldNotBeNull();
        // Verify settings from AppSettings are forwarded to the repository
        await _repository.Received(1).TryEnqueueWithLimitAsync(
            Arg.Is<PipelineQueueItem>(item => item.SessionId == sessionId && item.OwnerId == ownerId),
            Arg.Is<int>(max => max == _settings.MaxPendingItemsPerUser),
            Arg.Is<int>(max => max == _settings.MaxConcurrentItemsPerUser),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TryEnqueueProcessAsync_ExceedsRateLimit_ReturnsRateLimited()
    {
        // Arrange
        var rateLimitResult = new RateLimitCheckResult(false, "Limit exceeded", 10, 10, 2, 2);
        _repository.TryEnqueueWithLimitAsync(
            Arg.Any<PipelineQueueItem>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns((false, null, rateLimitResult));

        // Act
        var result = await _sut.TryEnqueueProcessAsync(Guid.NewGuid(), Guid.NewGuid(), "test");

        // Assert
        result.Success.ShouldBeFalse();
        result.ItemId.ShouldBeNull();
        result.RateLimitResult.ShouldNotBeNull();
    }

    #endregion

    #region TryCancelAsync Tests

    [Test]
    public async Task TryCancelAsync_PendingItem_ReturnsTrueAndLogs()
    {
        // Arrange
        var itemId = Guid.NewGuid();
        _repository.TryCancelIfPendingAsync(itemId, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var result = await _sut.TryCancelAsync(itemId);

        // Assert
        result.ShouldBeTrue();
        await _repository.Received(1).TryCancelIfPendingAsync(
            Arg.Is<Guid>(id => id == itemId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TryCancelAsync_PropagatesCancellationToken()
    {
        // Arrange
        var itemId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        _repository.TryCancelIfPendingAsync(itemId, token).Returns(true);

        // Act
        await _sut.TryCancelAsync(itemId, token);

        // Assert
        await _repository.Received(1).TryCancelIfPendingAsync(itemId, token);
    }

    [Test]
    public async Task TryCancelAsync_AlreadyProcessing_ReturnsFalse()
    {
        // Arrange
        var itemId = Guid.NewGuid();
        _repository.TryCancelIfPendingAsync(itemId, Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var result = await _sut.TryCancelAsync(itemId);

        // Assert
        result.ShouldBeFalse();
        await _repository.Received(1).TryCancelIfPendingAsync(
            Arg.Is<Guid>(id => id == itemId), Arg.Any<CancellationToken>());
    }

    #endregion

    #region GetItem Tests

    [Test]
    public async Task GetItemAsync_ExistingItem_ReturnsItem()
    {
        // Arrange
        var itemId = Guid.NewGuid();
        var item = new PipelineQueueItem { SessionId = Guid.NewGuid(), OwnerId = Guid.NewGuid(), OperationType = PipelineOperationType.Process };
        _repository.GetByIdAsync(itemId, Arg.Any<CancellationToken>()).Returns(item);

        // Act
        var result = await _sut.GetItemAsync(itemId);

        // Assert
        result.ShouldNotBeNull();
        await _repository.Received(1).GetByIdAsync(
            Arg.Is<Guid>(id => id == itemId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetItemAsync_PropagatesCancellationToken()
    {
        // Arrange
        var itemId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        _repository.GetByIdAsync(itemId, token).Returns((PipelineQueueItem?)null);

        // Act
        await _sut.GetItemAsync(itemId, token);

        // Assert
        await _repository.Received(1).GetByIdAsync(itemId, token);
    }

    [Test]
    public async Task GetItemAsync_NonExistent_ReturnsNull()
    {
        // Arrange
        var itemId = Guid.NewGuid();
        _repository.GetByIdAsync(itemId, Arg.Any<CancellationToken>()).Returns((PipelineQueueItem?)null);

        // Act
        var result = await _sut.GetItemAsync(itemId);

        // Assert
        result.ShouldBeNull();
        await _repository.Received(1).GetByIdAsync(
            Arg.Is<Guid>(id => id == itemId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetItemBySessionAsync_ExistingSession_ReturnsItem()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var item = new PipelineQueueItem { SessionId = sessionId, OwnerId = Guid.NewGuid(), OperationType = PipelineOperationType.Process };
        _repository.GetBySessionIdAsync(sessionId, Arg.Any<CancellationToken>()).Returns(item);

        // Act
        var result = await _sut.GetItemBySessionAsync(sessionId);

        // Assert
        result.ShouldNotBeNull();
        result.SessionId.ShouldBe(sessionId);
        await _repository.Received(1).GetBySessionIdAsync(
            Arg.Is<Guid>(id => id == sessionId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetItemBySessionAsync_PropagatesCancellationToken()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        _repository.GetBySessionIdAsync(sessionId, token).Returns((PipelineQueueItem?)null);

        // Act
        await _sut.GetItemBySessionAsync(sessionId, token);

        // Assert
        await _repository.Received(1).GetBySessionIdAsync(sessionId, token);
    }

    [Test]
    public async Task GetQueueDepthAsync_ReturnsRepositoryCount()
    {
        // Arrange
        _repository.GetCountByStatusAsync(PipelineQueueStatus.Pending, Arg.Any<CancellationToken>()).Returns(5);

        // Act
        var result = await _sut.GetQueueDepthAsync();

        // Assert
        result.ShouldBe(5);
        await _repository.Received(1).GetCountByStatusAsync(
            Arg.Is<PipelineQueueStatus>(s => s == PipelineQueueStatus.Pending),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetQueueDepthAsync_PropagatesCancellationToken()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        _repository.GetCountByStatusAsync(PipelineQueueStatus.Pending, token).Returns(0);

        // Act
        await _sut.GetQueueDepthAsync(token);

        // Assert
        await _repository.Received(1).GetCountByStatusAsync(PipelineQueueStatus.Pending, token);
    }

    #endregion

    #region Worker Operation Tests

    [Test]
    public async Task ClaimNextAsync_ItemAvailable_IncrementsProcessingCount()
    {
        // Arrange
        var item = new PipelineQueueItem { SessionId = Guid.NewGuid(), OwnerId = Guid.NewGuid(), OperationType = PipelineOperationType.Process };
        _repository.ClaimNextAsync(Arg.Any<CancellationToken>()).Returns(item);
        var initialCount = _sut.ProcessingCount;

        // Act
        var result = await _sut.ClaimNextAsync();

        // Assert
        result.ShouldNotBeNull();
        _sut.ProcessingCount.ShouldBe(initialCount + 1);
    }

    [Test]
    public async Task ClaimNextAsync_NoItem_ReturnsNullWithoutIncrementing()
    {
        // Arrange
        _repository.ClaimNextAsync(Arg.Any<CancellationToken>()).Returns((PipelineQueueItem?)null);
        var initialCount = _sut.ProcessingCount;

        // Act
        var result = await _sut.ClaimNextAsync();

        // Assert
        result.ShouldBeNull();
        _sut.ProcessingCount.ShouldBe(initialCount);
    }

    [Test]
    public async Task CompleteAsync_DecrementsProcessingCount()
    {
        // Arrange - First claim an item to have a count > 0
        var item = new PipelineQueueItem { SessionId = Guid.NewGuid(), OwnerId = Guid.NewGuid(), OperationType = PipelineOperationType.Process };
        _repository.ClaimNextAsync(Arg.Any<CancellationToken>()).Returns(item);
        await _sut.ClaimNextAsync();
        var countAfterClaim = _sut.ProcessingCount;

        // Act
        await _sut.CompleteAsync(item.Id);

        // Assert
        _sut.ProcessingCount.ShouldBe(countAfterClaim - 1);
        await _repository.Received(1).CompleteAsync(item.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FailAsync_DecrementsProcessingCountAndPassesError()
    {
        // Arrange - First claim an item
        var item = new PipelineQueueItem { SessionId = Guid.NewGuid(), OwnerId = Guid.NewGuid(), OperationType = PipelineOperationType.Process };
        _repository.ClaimNextAsync(Arg.Any<CancellationToken>()).Returns(item);
        await _sut.ClaimNextAsync();
        var countAfterClaim = _sut.ProcessingCount;

        // Act
        await _sut.FailAsync(item.Id, "Test error");

        // Assert
        _sut.ProcessingCount.ShouldBe(countAfterClaim - 1);
        await _repository.Received(1).FailAsync(item.Id, "Test error", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReleaseSlot_DecrementsProcessingCountOnly()
    {
        // Arrange - First claim an item
        var item = new PipelineQueueItem { SessionId = Guid.NewGuid(), OwnerId = Guid.NewGuid(), OperationType = PipelineOperationType.Process };
        _repository.ClaimNextAsync(Arg.Any<CancellationToken>()).Returns(item);
        await _sut.ClaimNextAsync();
        var countAfterClaim = _sut.ProcessingCount;

        // Act
        _sut.ReleaseSlot();

        // Assert
        _sut.ProcessingCount.ShouldBe(countAfterClaim - 1);
        // Should NOT call any repository methods
        await _repository.DidNotReceive().CompleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().FailAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResetForRetryAsync_Success_DecrementsAndSignals()
    {
        // Arrange - First claim an item
        var item = new PipelineQueueItem { SessionId = Guid.NewGuid(), OwnerId = Guid.NewGuid(), OperationType = PipelineOperationType.Process };
        _repository.ClaimNextAsync(Arg.Any<CancellationToken>()).Returns(item);
        await _sut.ClaimNextAsync();
        var countAfterClaim = _sut.ProcessingCount;
        _repository.ResetForRetryAsync(item.Id, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var result = await _sut.ResetForRetryAsync(item.Id);

        // Assert
        result.ShouldBeTrue();
        _sut.ProcessingCount.ShouldBe(countAfterClaim - 1);
    }

    [Test]
    public async Task ResetForRetryAsync_Failure_ReturnsFalseWithoutDecrement()
    {
        // Arrange
        var itemId = Guid.NewGuid();
        _repository.ResetForRetryAsync(itemId, Arg.Any<CancellationToken>()).Returns(false);
        var initialCount = _sut.ProcessingCount;

        // Act
        var result = await _sut.ResetForRetryAsync(itemId);

        // Assert
        result.ShouldBeFalse();
        _sut.ProcessingCount.ShouldBe(initialCount);
    }

    [Test]
    public async Task GetAttemptCountAsync_ExistingItem_ReturnsAttempts()
    {
        // Arrange
        var itemId = Guid.NewGuid();
        var item = new PipelineQueueItem 
        { 
            SessionId = Guid.NewGuid(), 
            OwnerId = Guid.NewGuid(), 
            OperationType = PipelineOperationType.Process,
            Attempts = 3 
        };
        _repository.GetByIdAsync(itemId, Arg.Any<CancellationToken>()).Returns(item);

        // Act
        var result = await _sut.GetAttemptCountAsync(itemId);

        // Assert
        result.ShouldBe(3);
    }

    [Test]
    public async Task GetAttemptCountAsync_NonExistentItem_ReturnsZero()
    {
        // Arrange
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((PipelineQueueItem?)null);

        // Act
        var result = await _sut.GetAttemptCountAsync(Guid.NewGuid());

        // Assert
        result.ShouldBe(0);
    }

    #endregion

    #region Dead Letter Queue Tests

    [Test]
    public async Task MoveToDeadLetterAsync_Success_DecrementsCountAndLogs()
    {
        // Arrange - First claim an item
        var item = new PipelineQueueItem { SessionId = Guid.NewGuid(), OwnerId = Guid.NewGuid(), OperationType = PipelineOperationType.Process };
        _repository.ClaimNextAsync(Arg.Any<CancellationToken>()).Returns(item);
        await _sut.ClaimNextAsync();
        var countAfterClaim = _sut.ProcessingCount;
        _repository.MoveToDeadLetterAsync(item.Id, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        // Act
        await _sut.MoveToDeadLetterAsync(item.Id, "Exhausted retries");

        // Assert
        _sut.ProcessingCount.ShouldBe(countAfterClaim - 1);
        await _repository.Received(1).MoveToDeadLetterAsync(item.Id, "Exhausted retries", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RequeueDeadLetterItemAsync_Success_SignalsItemAvailable()
    {
        // Arrange
        var itemId = Guid.NewGuid();
        _repository.RequeueDeadLetterItemAsync(itemId, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var result = await _sut.RequeueDeadLetterItemAsync(itemId);

        // Assert
        result.ShouldBeTrue();
    }

    [Test]
    public async Task GetDeadLetterItemsAsync_DelegatesToRepository()
    {
        // Arrange
        var items = new List<PipelineQueueItem>
        {
            new() { SessionId = Guid.NewGuid(), OwnerId = Guid.NewGuid(), OperationType = PipelineOperationType.Process }
        };
        _repository.GetDeadLetterItemsAsync(Arg.Any<CancellationToken>()).Returns(items);

        // Act
        var result = await _sut.GetDeadLetterItemsAsync();

        // Assert
        result.Count.ShouldBe(1);
        await _repository.Received(1).GetDeadLetterItemsAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetDeadLetterItemsAsync_PropagatesCancellationToken()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        _repository.GetDeadLetterItemsAsync(token).Returns(new List<PipelineQueueItem>());

        // Act
        await _sut.GetDeadLetterItemsAsync(token);

        // Assert
        await _repository.Received(1).GetDeadLetterItemsAsync(token);
    }

    [Test]
    public async Task DeleteDeadLetterItemAsync_DelegatesToRepository()
    {
        // Arrange
        var itemId = Guid.NewGuid();
        _repository.DeleteDeadLetterItemAsync(itemId, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var result = await _sut.DeleteDeadLetterItemAsync(itemId);

        // Assert
        result.ShouldBeTrue();
        await _repository.Received(1).DeleteDeadLetterItemAsync(
            Arg.Is<Guid>(id => id == itemId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteDeadLetterItemAsync_PropagatesCancellationToken()
    {
        // Arrange
        var itemId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        _repository.DeleteDeadLetterItemAsync(itemId, token).Returns(true);

        // Act
        await _sut.DeleteDeadLetterItemAsync(itemId, token);

        // Assert
        await _repository.Received(1).DeleteDeadLetterItemAsync(itemId, token);
    }

    #endregion

    #region Queue Position Tests

    [Test]
    public async Task GetQueuePositionAsync_PendingItem_ReturnsPositionInfo()
    {
        // Arrange
        var itemId = Guid.NewGuid();
        _repository.GetQueuePositionAsync(itemId, Arg.Any<CancellationToken>()).Returns(3);
        _repository.GetCountByStatusAsync(PipelineQueueStatus.Pending, Arg.Any<CancellationToken>()).Returns(10);
        _repository.GetAverageProcessingTimeAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(TimeSpan.FromMinutes(2));

        // Act
        var result = await _sut.GetQueuePositionAsync(itemId);

        // Assert
        result.ShouldNotBeNull();
        result.Position.ShouldBe(3);
        result.TotalPending.ShouldBe(10);
        result.EstimatedWait.ShouldNotBeNull();
        result.EstimatedWait!.Value.TotalMinutes.ShouldBe(6); // 3 * 2 minutes
    }

    [Test]
    public async Task GetQueuePositionAsync_NotPending_ReturnsNull()
    {
        // Arrange
        _repository.GetQueuePositionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((int?)null);

        // Act
        var result = await _sut.GetQueuePositionAsync(Guid.NewGuid());

        // Assert
        result.ShouldBeNull();
    }

    [Test]
    public async Task GetQueuePositionAsync_NoAverageTime_ReturnsNullEstimatedWait()
    {
        // Arrange
        var itemId = Guid.NewGuid();
        _repository.GetQueuePositionAsync(itemId, Arg.Any<CancellationToken>()).Returns(2);
        _repository.GetCountByStatusAsync(PipelineQueueStatus.Pending, Arg.Any<CancellationToken>()).Returns(5);
        _repository.GetAverageProcessingTimeAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((TimeSpan?)null);

        // Act
        var result = await _sut.GetQueuePositionAsync(itemId);

        // Assert
        result.ShouldNotBeNull();
        result.Position.ShouldBe(2);
        result.EstimatedWait.ShouldBeNull();
    }

    #endregion

    #region Rate Limit Check Tests

    [Test]
    public async Task CheckRateLimitAsync_BelowLimits_ReturnsAllowed()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        _repository.GetPendingCountByOwnerAsync(ownerId, Arg.Any<CancellationToken>()).Returns(5);
        _repository.GetProcessingCountByOwnerAsync(ownerId, Arg.Any<CancellationToken>()).Returns(1);

        // Act
        var result = await _sut.CheckRateLimitAsync(ownerId);

        // Assert
        result.IsAllowed.ShouldBeTrue();
        result.Reason.ShouldBeNull();
        result.CurrentPending.ShouldBe(5);
        result.CurrentProcessing.ShouldBe(1);
        result.MaxPending.ShouldBe(_settings.MaxPendingItemsPerUser);
        result.MaxConcurrent.ShouldBe(_settings.MaxConcurrentItemsPerUser);
        // Verify the exact ownerId was forwarded
        await _repository.Received(1).GetPendingCountByOwnerAsync(
            Arg.Is<Guid>(id => id == ownerId), Arg.Any<CancellationToken>());
        await _repository.Received(1).GetProcessingCountByOwnerAsync(
            Arg.Is<Guid>(id => id == ownerId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CheckRateLimitAsync_AtPendingLimit_ReturnsNotAllowed()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        _repository.GetPendingCountByOwnerAsync(ownerId, Arg.Any<CancellationToken>()).Returns(10); // At limit
        _repository.GetProcessingCountByOwnerAsync(ownerId, Arg.Any<CancellationToken>()).Returns(0);

        // Act
        var result = await _sut.CheckRateLimitAsync(ownerId);

        // Assert
        result.IsAllowed.ShouldBeFalse();
        result.Reason.ShouldNotBeNull();
        result.Reason.ShouldContain("pending", Case.Insensitive);
    }

    [Test]
    public async Task CheckRateLimitAsync_AtConcurrentLimit_ReturnsNotAllowed()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        _repository.GetPendingCountByOwnerAsync(ownerId, Arg.Any<CancellationToken>()).Returns(0);
        _repository.GetProcessingCountByOwnerAsync(ownerId, Arg.Any<CancellationToken>()).Returns(2); // At limit

        // Act
        var result = await _sut.CheckRateLimitAsync(ownerId);

        // Assert
        result.IsAllowed.ShouldBeFalse();
        result.Reason.ShouldNotBeNull();
        result.Reason.ShouldContain("concurrent", Case.Insensitive);
    }

    #endregion

    #region Signal Tests

    [Test]
    public void SignalItemAvailable_DoesNotThrow()
    {
        // Act & Assert - Should not throw
        Should.NotThrow(() => _sut.SignalItemAvailable());
    }

    [Test]
    public async Task WaitForItemAsync_SignalReceived_ReturnsTrue()
    {
        // Arrange
        _sut.SignalItemAvailable();

        // Act
        var result = await _sut.WaitForItemAsync();

        // Assert
        result.ShouldBeTrue();
    }

    [Test]
    public async Task WaitForItemAsync_Cancelled_ReturnsFalse()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act
        var result = await _sut.WaitForItemAsync(cts.Token);

        // Assert
        result.ShouldBeFalse();
    }

    #endregion

    #region Deserialization Tests

    [Test]
    public void DeserializeOptions_ValidJson_ReturnsPipelineOptions()
    {
        // Arrange - JSON uses camelCase since the service uses JsonNamingPolicy.CamelCase
        var json = """{"useJavaScript":true,"maxIterations":5}""";

        // Act
        var result = PipelineQueueService.DeserializeOptions(json);

        // Assert
        result.ShouldNotBeNull();
        result.UseJavaScript.ShouldBeTrue();
        result.MaxIterations.ShouldBe(5);
    }

    [Test]
    public void DeserializeOptions_NullOrEmpty_ReturnsNull()
    {
        // Act & Assert
        PipelineQueueService.DeserializeOptions(null).ShouldBeNull();
        PipelineQueueService.DeserializeOptions("").ShouldBeNull();
    }

    [Test]
    public void DeserializeSession_ValidJson_ReturnsPipelineSession()
    {
        // Arrange - JSON uses camelCase since the service uses JsonNamingPolicy.CamelCase
        var json = """{"originalInput":"test input","currentIteration":2}""";

        // Act
        var result = PipelineQueueService.DeserializeSession(json);

        // Assert
        result.ShouldNotBeNull();
        result.OriginalInput.ShouldBe("test input");
        result.CurrentIteration.ShouldBe(2);
    }

    [Test]
    public void DeserializeResult_ValidJson_ReturnsPipelineResult()
    {
        // Arrange - JSON uses camelCase since the service uses JsonNamingPolicy.CamelCase
        var json = """{"isSuccess":false,"errorMessage":"Test error"}""";

        // Act
        var result = PipelineQueueService.DeserializeResult(json);

        // Assert
        result.ShouldNotBeNull();
        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("Test error");
    }

    #endregion
}
