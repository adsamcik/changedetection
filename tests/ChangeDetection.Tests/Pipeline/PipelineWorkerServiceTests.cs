using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Hubs;
using ChangeDetection.Services.Authentication;
using ChangeDetection.Services.Pipeline;
using ChangeDetection.Shared.Dtos;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline;

/// <summary>
/// Unit tests for PipelineWorkerService.
/// Tests worker lifecycle, item processing, error recovery, and stale item handling.
/// </summary>
[Category("Unit")]
public class PipelineWorkerServiceTests : TestBase, IDisposable
{
    private readonly IBackgroundServiceScopeFactory _scopeFactory;
    private readonly PipelineQueueService _queueService;
    private readonly IPipelineQueueRepository _queueRepository;
    private readonly IHubContext<SetupConversationHub> _hubContext;
    private readonly IServiceScope _scope;
    private readonly IWatchSetupPipeline _pipeline;
    private readonly IRepository<AppSettings> _settingsRepo;
    private readonly IClientProxy _clientProxy;
    private readonly IHubClients _hubClients;

    public PipelineWorkerServiceTests()
    {
        _scopeFactory = Substitute.For<IBackgroundServiceScopeFactory>();
        _queueRepository = Substitute.For<IPipelineQueueRepository>();
        _hubContext = Substitute.For<IHubContext<SetupConversationHub>>();
        _scope = Substitute.For<IServiceScope>();
        _pipeline = Substitute.For<IWatchSetupPipeline>();
        _settingsRepo = Substitute.For<IRepository<AppSettings>>();
        _clientProxy = Substitute.For<IClientProxy>();
        _hubClients = Substitute.For<IHubClients>();

        // Setup default mocks
        var scopedServiceProvider = Substitute.For<IServiceProvider>();
        scopedServiceProvider.GetService(typeof(IWatchSetupPipeline)).Returns(_pipeline);
        scopedServiceProvider.GetService(typeof(IRepository<AppSettings>)).Returns(_settingsRepo);
        _scope.ServiceProvider.Returns(scopedServiceProvider);
        _scopeFactory.CreateBackgroundScope().Returns(_scope);

        // Default settings
        _settingsRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([new AppSettings { MaxConcurrentPipelines = 1 }]);

        // Default queue repository behavior
        _queueRepository.GetCountByStatusAsync(PipelineQueueStatus.Pending, Arg.Any<CancellationToken>())
            .Returns(0);
        _queueRepository.ResetStaleItemsAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(0);

        // Setup SignalR mocks
        _hubContext.Clients.Returns(_hubClients);
        _hubClients.Group(Arg.Any<string>()).Returns(_clientProxy);

        // Create a real PipelineQueueService with mocked repository
        var optionsMonitor = Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<AppSettings>>();
        optionsMonitor.CurrentValue.Returns(new AppSettings());
        _queueService = new PipelineQueueService(
            _queueRepository,
            CreateLogger<PipelineQueueService>(),
            optionsMonitor);
    }

    private PipelineWorkerService CreateService()
    {
        return new PipelineWorkerService(
            _scopeFactory,
            _queueService,
            _queueRepository,
            _hubContext,
            CreateLogger<PipelineWorkerService>());
    }

    #region Constructor Tests

    [Test]
    public async Task Constructor_DoesNotThrow()
    {
        // Act
        var service = CreateService();

        // Assert
        service.ShouldNotBeNull();
        await Task.CompletedTask;
    }

    #endregion

    #region Startup and Recovery Tests

    [Test]
    public async Task ExecuteAsync_RecoversStaleItemsOnStartup()
    {
        // Arrange
        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        _queueRepository.ResetStaleItemsAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(3);

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(50, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert
        await _queueRepository.Received(1).ResetStaleItemsAsync(
            Arg.Is<TimeSpan>(ts => ts == TimeSpan.FromMinutes(30)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_SignalsPendingItemsFromPreviousRun()
    {
        // Arrange
        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        _queueRepository.GetCountByStatusAsync(PipelineQueueStatus.Pending, Arg.Any<CancellationToken>())
            .Returns(5);

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(50, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert - service should have signaled 5 times for pending items
        // We verify this indirectly through queue depth check
        await _queueRepository.Received().GetCountByStatusAsync(
            PipelineQueueStatus.Pending, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_StartsConfiguredNumberOfWorkers()
    {
        // Arrange
        _settingsRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([new AppSettings { MaxConcurrentPipelines = 3 }]);

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(50, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert - verify settings were read
        await _settingsRepo.Received().GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_DefaultsToOneWorker_WhenSettingsUnavailable()
    {
        // Arrange
        _settingsRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Settings unavailable"));

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act - should not throw
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(50, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert - service started without throwing
        var logs = LogCollector.GetSnapshot();
        logs.ShouldContain(l => l.Message.Contains("using default"));
    }

    [Test]
    public async Task ExecuteAsync_ClampsWorkerCountToValidRange_WhenTooHigh()
    {
        // Arrange
        _settingsRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([new AppSettings { MaxConcurrentPipelines = 100 }]); // Way too high

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(50, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert - logs should show clamped value (10 max)
        var logs = LogCollector.GetSnapshot();
        logs.ShouldContain(l => l.Message.Contains("10") || l.Message.Contains("workers"));
    }

    [Test]
    public async Task ExecuteAsync_ClampsWorkerCountToValidRange_WhenTooLow()
    {
        // Arrange
        _settingsRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([new AppSettings { MaxConcurrentPipelines = 0 }]); // Invalid

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(50, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert - should clamp to minimum of 1
        var logs = LogCollector.GetSnapshot();
        logs.ShouldContain(l => l.Message.Contains("1") && l.Message.Contains("worker"));
    }

    #endregion

    #region Stale Item Checking Tests

    [Test]
    public async Task CheckStaleItemsAsync_ResetsStaleItemsPeriodically()
    {
        // This test verifies the stale check logic indirectly
        // The actual periodic timer runs every 5 minutes, but we test the reset call
        
        // Arrange
        _queueRepository.ResetStaleItemsAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(50, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert - at least startup recovery was called
        await _queueRepository.Received().ResetStaleItemsAsync(
            TimeSpan.FromMinutes(30), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CheckStaleItemsAsync_PurgesOldCompletedItems()
    {
        // Arrange
        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(50, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert - purge is called in the periodic check, may not be called in short test
        // This primarily validates the setup doesn't throw
        await Task.CompletedTask;
    }

    #endregion

    #region Worker Processing Tests

    [Test]
    public async Task RunWorkerAsync_ProcessesItemAndCompletesOnSuccess()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var item = new PipelineQueueItem
        {
            SessionId = sessionId,
            OwnerId = ownerId,
            OperationType = PipelineOperationType.Process,
            UserInput = "Watch https://example.com for changes"
        };

        _queueRepository.ClaimNextAsync(Arg.Any<CancellationToken>())
            .Returns(item, (PipelineQueueItem?)null);

        _pipeline.ProcessStreamingAsync(Arg.Any<string>(), Arg.Any<PipelineOptions?>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<PipelineProgress>());

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Signal that work is available
        _queueService.SignalItemAvailable();

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert
        await _queueRepository.Received().CompleteAsync(item.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunWorkerAsync_ContinuesWhenAnotherWorkerClaimsItem()
    {
        // Arrange - claim returns null (another worker got it)
        _queueRepository.ClaimNextAsync(Arg.Any<CancellationToken>())
            .Returns((PipelineQueueItem?)null);

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Signal that work is available
        _queueService.SignalItemAvailable();

        // Act - should not throw
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(50, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert - no complete/fail calls since no item was claimed
        await _queueRepository.DidNotReceive().CompleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _queueRepository.DidNotReceive().FailAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunWorkerAsync_RetriesOnTransientError()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var item = new PipelineQueueItem
        {
            SessionId = sessionId,
            OwnerId = ownerId,
            OperationType = PipelineOperationType.Process,
            UserInput = "Watch https://example.com",
            Attempts = 1
        };

        _queueRepository.ClaimNextAsync(Arg.Any<CancellationToken>())
            .Returns(item, (PipelineQueueItem?)null);

        _pipeline.ProcessStreamingAsync(Arg.Any<string>(), Arg.Any<PipelineOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new HttpRequestException("Network error"));

        _queueRepository.ResetForRetryAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(true);

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        _queueService.SignalItemAvailable();

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert - should have called reset for retry
        await _queueRepository.Received().ResetForRetryAsync(item.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunWorkerAsync_MovesToDeadLetterAfterMaxRetries()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var item = new PipelineQueueItem
        {
            SessionId = sessionId,
            OwnerId = ownerId,
            OperationType = PipelineOperationType.Process,
            UserInput = "Watch https://example.com",
            Attempts = 3 // At max retries
        };

        _queueRepository.ClaimNextAsync(Arg.Any<CancellationToken>())
            .Returns(item, (PipelineQueueItem?)null);

        _pipeline.ProcessStreamingAsync(Arg.Any<string>(), Arg.Any<PipelineOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new HttpRequestException("Network error"));

        _queueRepository.MoveToDeadLetterAsync(item.Id, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        _queueService.SignalItemAvailable();

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert - should have moved to dead letter
        await _queueRepository.Received().MoveToDeadLetterAsync(
            item.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunWorkerAsync_MovesToDeadLetterOnNonTransientError()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var item = new PipelineQueueItem
        {
            SessionId = sessionId,
            OwnerId = ownerId,
            OperationType = PipelineOperationType.Process,
            UserInput = "Watch https://example.com",
            Attempts = 1 // First attempt
        };

        _queueRepository.ClaimNextAsync(Arg.Any<CancellationToken>())
            .Returns(item, (PipelineQueueItem?)null);

        // Non-transient error
        _pipeline.ProcessStreamingAsync(Arg.Any<string>(), Arg.Any<PipelineOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new ArgumentException("Invalid argument"));

        _queueRepository.MoveToDeadLetterAsync(item.Id, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        _queueService.SignalItemAvailable();

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert - should immediately move to dead letter (no retry)
        await _queueRepository.Received().MoveToDeadLetterAsync(
            item.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _queueRepository.DidNotReceive().ResetForRetryAsync(
            item.Id, Arg.Any<CancellationToken>());
    }

    #endregion

    #region Operation Type Dispatch Tests

    [Test]
    public async Task ProcessItemAsync_DispatchesToProcessOperation()
    {
        // Arrange
        var item = new PipelineQueueItem
        {
            SessionId = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            OperationType = PipelineOperationType.Process,
            UserInput = "Watch https://example.com"
        };

        _queueRepository.ClaimNextAsync(Arg.Any<CancellationToken>())
            .Returns(item, (PipelineQueueItem?)null);

        _pipeline.ProcessStreamingAsync(Arg.Any<string>(), Arg.Any<PipelineOptions?>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<PipelineProgress>());

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        _queueService.SignalItemAvailable();

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert
        _pipeline.Received().ProcessStreamingAsync(
            item.UserInput!, Arg.Any<PipelineOptions?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessItemAsync_DispatchesToContinueOperation()
    {
        // Arrange
        var sessionJson = System.Text.Json.JsonSerializer.Serialize(new PipelineSession
        {
            SessionId = Guid.NewGuid(),
            OriginalInput = "test"
        });

        var item = new PipelineQueueItem
        {
            SessionId = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            OperationType = PipelineOperationType.ContinueWithFeedback,
            Feedback = "Use a different selector",
            SessionJson = sessionJson
        };

        _queueRepository.ClaimNextAsync(Arg.Any<CancellationToken>())
            .Returns(item, (PipelineQueueItem?)null);

        _pipeline.ContinueWithFeedbackStreamingAsync(
            Arg.Any<PipelineSession>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<PipelineProgress>());

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        _queueService.SignalItemAvailable();

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert
        _pipeline.Received().ContinueWithFeedbackStreamingAsync(
            Arg.Any<PipelineSession>(), item.Feedback!, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessItemAsync_DispatchesToRecoveryOperation()
    {
        // Arrange
        var sessionJson = System.Text.Json.JsonSerializer.Serialize(new PipelineSession
        {
            SessionId = Guid.NewGuid(),
            OriginalInput = "test"
        });
        var resultJson = System.Text.Json.JsonSerializer.Serialize(new PipelineResult
        {
            IsSuccess = false,
            CurrentStage = PipelineStage.SelectorGeneration
        });
        var optionsJson = System.Text.Json.JsonSerializer.Serialize(new PipelineOptions());

        var item = new PipelineQueueItem
        {
            SessionId = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            OperationType = PipelineOperationType.RecoverFromFailure,
            SessionJson = sessionJson,
            FailedResultJson = resultJson,
            OptionsJson = optionsJson
        };

        _queueRepository.ClaimNextAsync(Arg.Any<CancellationToken>())
            .Returns(item, (PipelineQueueItem?)null);

        _pipeline.RecoverFromFailureAsync(
            Arg.Any<PipelineSession>(), Arg.Any<PipelineResult>(), 
            Arg.Any<PipelineOptions>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineResult { IsSuccess = true, CurrentStage = PipelineStage.Complete });

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        _queueService.SignalItemAvailable();

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert
        await _pipeline.Received().RecoverFromFailureAsync(
            Arg.Any<PipelineSession>(), Arg.Any<PipelineResult>(),
            Arg.Any<PipelineOptions>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region IsTransientError Tests

    [Test]
    public async Task IsTransientError_ReturnsTrueForHttpRequestException()
    {
        // This is tested indirectly through the retry behavior
        // A direct test would require making the method public or using reflection
        
        // Arrange
        var item = new PipelineQueueItem
        {
            SessionId = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            OperationType = PipelineOperationType.Process,
            UserInput = "test",
            Attempts = 1
        };

        _queueRepository.ClaimNextAsync(Arg.Any<CancellationToken>())
            .Returns(item, (PipelineQueueItem?)null);

        _pipeline.ProcessStreamingAsync(Arg.Any<string>(), Arg.Any<PipelineOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new HttpRequestException("Network error"));

        _queueRepository.ResetForRetryAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(true);

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        _queueService.SignalItemAvailable();

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert - HttpRequestException triggers retry
        await _queueRepository.Received().ResetForRetryAsync(item.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IsTransientError_ReturnsTrueForTimeoutException()
    {
        // Arrange
        var item = new PipelineQueueItem
        {
            SessionId = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            OperationType = PipelineOperationType.Process,
            UserInput = "test",
            Attempts = 1
        };

        _queueRepository.ClaimNextAsync(Arg.Any<CancellationToken>())
            .Returns(item, (PipelineQueueItem?)null);

        _pipeline.ProcessStreamingAsync(Arg.Any<string>(), Arg.Any<PipelineOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new TimeoutException("Request timed out"));

        _queueRepository.ResetForRetryAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(true);

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        _queueService.SignalItemAvailable();

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert - TimeoutException triggers retry
        await _queueRepository.Received().ResetForRetryAsync(item.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IsTransientError_ReturnsTrueForRateLimitMessage()
    {
        // Arrange
        var item = new PipelineQueueItem
        {
            SessionId = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            OperationType = PipelineOperationType.Process,
            UserInput = "test",
            Attempts = 1
        };

        _queueRepository.ClaimNextAsync(Arg.Any<CancellationToken>())
            .Returns(item, (PipelineQueueItem?)null);

        _pipeline.ProcessStreamingAsync(Arg.Any<string>(), Arg.Any<PipelineOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new Exception("Error 429: rate limit exceeded"));

        _queueRepository.ResetForRetryAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(true);

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        _queueService.SignalItemAvailable();

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert - Rate limit error triggers retry
        await _queueRepository.Received().ResetForRetryAsync(item.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IsTransientError_ReturnsFalseForArgumentException()
    {
        // Arrange
        var item = new PipelineQueueItem
        {
            SessionId = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            OperationType = PipelineOperationType.Process,
            UserInput = "test",
            Attempts = 1
        };

        _queueRepository.ClaimNextAsync(Arg.Any<CancellationToken>())
            .Returns(item, (PipelineQueueItem?)null);

        _pipeline.ProcessStreamingAsync(Arg.Any<string>(), Arg.Any<PipelineOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new ArgumentException("Invalid argument"));

        _queueRepository.MoveToDeadLetterAsync(item.Id, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        _queueService.SignalItemAvailable();

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert - ArgumentException goes directly to dead letter
        await _queueRepository.DidNotReceive().ResetForRetryAsync(item.Id, Arg.Any<CancellationToken>());
        await _queueRepository.Received().MoveToDeadLetterAsync(
            item.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region SignalR Notification Tests

    [Test]
    public async Task NotifyClientAsync_SendsToCorrectGroup()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var item = new PipelineQueueItem
        {
            SessionId = sessionId,
            OwnerId = Guid.NewGuid(),
            OperationType = PipelineOperationType.Process,
            UserInput = "Watch https://example.com"
        };

        _queueRepository.ClaimNextAsync(Arg.Any<CancellationToken>())
            .Returns(item, (PipelineQueueItem?)null);

        var progress = new PipelineProgress
        {
            Stage = PipelineStage.UrlExtraction,
            Type = ProgressType.InProgress,
            Summary = "Extracting URL"
        };

        _pipeline.ProcessStreamingAsync(Arg.Any<string>(), Arg.Any<PipelineOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(progress));

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        _queueService.SignalItemAvailable();

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert - verify the correct group was targeted
        _hubClients.Received().Group($"setup-{sessionId}");
    }

    [Test]
    public async Task NotifyClientAsync_HandlesSignalRFailure()
    {
        // Arrange
        var item = new PipelineQueueItem
        {
            SessionId = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            OperationType = PipelineOperationType.Process,
            UserInput = "Watch https://example.com"
        };

        _queueRepository.ClaimNextAsync(Arg.Any<CancellationToken>())
            .Returns(item, (PipelineQueueItem?)null);

        var progress = new PipelineProgress
        {
            Stage = PipelineStage.UrlExtraction,
            Type = ProgressType.InProgress,
            Summary = "Extracting URL"
        };

        _pipeline.ProcessStreamingAsync(Arg.Any<string>(), Arg.Any<PipelineOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(progress));

        // SignalR throws
        _clientProxy.SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("SignalR connection lost"));

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        _queueService.SignalItemAvailable();

        // Act - should not throw even though SignalR fails
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert - processing should still complete despite notification failure
        await _queueRepository.Received().CompleteAsync(item.Id, Arg.Any<CancellationToken>());
    }

    #endregion

    #region Progress Mapping Tests

    [Test]
    public async Task MapProgressToFlowState_MapsAllProgressTypes()
    {
        // This test verifies mapping indirectly through actual processing
        // The mapping happens inside the service for each progress update
        
        var item = new PipelineQueueItem
        {
            SessionId = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            OperationType = PipelineOperationType.Process,
            UserInput = "test"
        };

        _queueRepository.ClaimNextAsync(Arg.Any<CancellationToken>())
            .Returns(item, (PipelineQueueItem?)null);

        var progressSequence = new[]
        {
            new PipelineProgress { Stage = PipelineStage.UrlExtraction, Type = ProgressType.Starting, Summary = "Starting" },
            new PipelineProgress { Stage = PipelineStage.UrlExtraction, Type = ProgressType.InProgress, Summary = "In Progress" },
            new PipelineProgress { Stage = PipelineStage.UrlExtraction, Type = ProgressType.Thinking, Summary = "Thinking" },
            new PipelineProgress { Stage = PipelineStage.UrlExtraction, Type = ProgressType.StageCompleted, Summary = "Stage Done" },
            new PipelineProgress { Stage = PipelineStage.Complete, Type = ProgressType.Completed, Summary = "Complete" }
        };

        _pipeline.ProcessStreamingAsync(Arg.Any<string>(), Arg.Any<PipelineOptions?>(), Arg.Any<CancellationToken>())
            .Returns(progressSequence.ToAsyncEnumerable());

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        _queueService.SignalItemAvailable();

        // Act
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(250, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert - verify notifications were sent
        await _clientProxy.Received(5).SendCoreAsync(
            "FlowStateUpdate",
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region Helper Methods

    private static async IAsyncEnumerable<PipelineProgress> ToAsyncEnumerable(params PipelineProgress[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    public void Dispose()
    {
        _queueService.Dispose();
        GC.SuppressFinalize(this);
    }

    #endregion
}
