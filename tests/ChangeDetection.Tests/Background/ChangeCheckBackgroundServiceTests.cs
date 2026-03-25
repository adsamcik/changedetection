using System.Reflection;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Hubs;
using ChangeDetection.Services;
using ChangeDetection.Services.Authentication;
using ChangeDetection.Services.Background;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Background;

[Category("Unit")]
public class ChangeCheckBackgroundServiceTests
{
    private readonly IBackgroundServiceScopeFactory _scopeFactory;
    private readonly IWatchService _watchService;
    private readonly IRepository<AppSettings> _settingsRepo;
    private readonly IRepository<ChangeEvent> _eventRepo;
    private readonly IRepository<WatchedSite> _watchRepo;
    private readonly IHubContext<ChangeDetectionHub> _hubContext;
    private readonly IClientProxy _clientProxy;
    private readonly ILogger<ChangeCheckBackgroundService> _logger;
    private readonly IWatchExecutionLock _executionLock;
    private readonly IPipelineExecutor _pipelineExecutor;
    private readonly IBlockStateStore _stateStore;
    private readonly IPipelineRunSummaryStore _runSummaryStore;
    private readonly ChangeCheckBackgroundService _sut;

    public ChangeCheckBackgroundServiceTests()
    {
        _watchService = Substitute.For<IWatchService>();
        var notificationService = Substitute.For<INotificationService>();
        _settingsRepo = Substitute.For<IRepository<AppSettings>>();
        _eventRepo = Substitute.For<IRepository<ChangeEvent>>();
        _watchRepo = Substitute.For<IRepository<WatchedSite>>();
        _logger = Substitute.For<ILogger<ChangeCheckBackgroundService>>();
        _executionLock = new WatchExecutionLock();
        _pipelineExecutor = Substitute.For<IPipelineExecutor>();
        _stateStore = Substitute.For<IBlockStateStore>();
        _runSummaryStore = Substitute.For<IPipelineRunSummaryStore>();

        // Hub context mock chain
        _clientProxy = Substitute.For<IClientProxy>();
        _hubContext = Substitute.For<IHubContext<ChangeDetectionHub>>();
        _hubContext.Clients.Group(Arg.Any<string>()).Returns(_clientProxy);

        // Outer scope (from background service scope factory)
        var scope = Substitute.For<IServiceScope>();
        _scopeFactory = Substitute.For<IBackgroundServiceScopeFactory>();

        var outerProvider = Substitute.For<IServiceProvider>();
        outerProvider.GetService(typeof(IWatchService)).Returns(_watchService);
        outerProvider.GetService(typeof(IRepository<AppSettings>)).Returns(_settingsRepo);
        outerProvider.GetService(typeof(IHubContext<ChangeDetectionHub>)).Returns(_hubContext);

        // Inner scope factory for per-watch scopes (used by serviceProvider.CreateScope())
        var innerScopeFactory = Substitute.For<IServiceScopeFactory>();
        var innerScope = Substitute.For<IServiceScope>();
        var innerProvider = Substitute.For<IServiceProvider>();
        innerProvider.GetService(typeof(IWatchService)).Returns(_watchService);
        innerProvider.GetService(typeof(INotificationService)).Returns(notificationService);
        innerProvider.GetService(typeof(IRepository<ChangeEvent>)).Returns(_eventRepo);
        innerProvider.GetService(typeof(IRepository<WatchedSite>)).Returns(_watchRepo);
        innerProvider.GetService(typeof(IPipelineExecutor)).Returns(_pipelineExecutor);
        innerProvider.GetService(typeof(IBlockStateStore)).Returns(_stateStore);
        innerProvider.GetService(typeof(IPipelineRunSummaryStore)).Returns(_runSummaryStore);
        innerScope.ServiceProvider.Returns(innerProvider);
        innerScopeFactory.CreateScope().Returns(innerScope);
        outerProvider.GetService(typeof(IServiceScopeFactory)).Returns(innerScopeFactory);

        scope.ServiceProvider.Returns(outerProvider);
        _scopeFactory.CreateBackgroundScope().Returns(scope);

        // Default settings
        _settingsRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([new AppSettings { MaxConcurrentChecks = 5 }]);

        _pipelineExecutor.ExecuteAsync(
                Arg.Any<PipelineDefinition>(),
                Arg.Any<Guid>(),
                Arg.Any<IBlockStateStore>(),
                Arg.Any<object?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>())
            .Returns(new PipelineExecutionResult
            {
                Success = false,
                Error = "Synthetic failure",
                BlockResults = new Dictionary<string, BlockResult>(),
                ExecutionDurationMs = 10,
                WasBaseline = false,
                IsDegraded = false,
                SkippedBlockIds = []
            });

        _sut = new ChangeCheckBackgroundService(_scopeFactory, _logger, _executionLock);
    }

    private static string CreatePipelineJson(string url) =>
        PipelineSerializer.Serialize(ChangeCheckBackgroundService.GenerateBasicPipeline(url, cssSelector: null));

    private Task InvokeCheckPendingWatchesAsync(CancellationToken ct = default)
    {
        var method = typeof(ChangeCheckBackgroundService)
            .GetMethod("CheckPendingWatchesAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        return (Task)method!.Invoke(_sut, [ct])!;
    }

    private Task InvokeRunOverdueWatchesAsync(CancellationToken ct = default)
    {
        var method = typeof(ChangeCheckBackgroundService)
            .GetMethod("RunOverdueWatchesAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        return (Task)method!.Invoke(_sut, [ct])!;
    }

    [Test]
    public async Task ExecuteAsync_NoWatchesDue_DoesNotCheck()
    {
        _watchService.GetWatchesDueForCheckAsync(Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<WatchedSite>());

        await InvokeCheckPendingWatchesAsync();

        await _pipelineExecutor.DidNotReceiveWithAnyArgs()
            .ExecuteAsync(default!, default, default!, default, default, default);
    }

    [Test]
    public async Task ExecuteAsync_WatchDue_ChecksForChanges()
    {
        var watchId = Guid.NewGuid();
        var watch = new WatchedSite
        {
            Id = watchId,
            Url = "https://example.com",
            PipelineDefinitionJson = CreatePipelineJson("https://example.com")
        };

        _watchService.GetWatchesDueForCheckAsync(Arg.Any<CancellationToken>())
            .Returns([watch]);

        await InvokeCheckPendingWatchesAsync();

        await _pipelineExecutor.Received(1)
            .ExecuteAsync(Arg.Any<PipelineDefinition>(), watchId, _stateStore, Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Is(false));
    }

    [Test]
    public async Task ExecuteAsync_DuplicateWatch_SkipsAlreadyRunning()
    {
        var watchId = Guid.NewGuid();
        var watch = new WatchedSite
        {
            Id = watchId,
            Url = "https://example.com",
            PipelineDefinitionJson = CreatePipelineJson("https://example.com")
        };

        _executionLock.TryAcquire(watchId).ShouldBeTrue();

        try
        {
            _watchService.GetWatchesDueForCheckAsync(Arg.Any<CancellationToken>())
                .Returns([watch]);

            await InvokeCheckPendingWatchesAsync();

            await _pipelineExecutor.DidNotReceiveWithAnyArgs()
                .ExecuteAsync(default!, default, default!, default, default, default);
        }
        finally
        {
            _executionLock.Release(watchId);
        }
    }

    [Test]
    public async Task RunOverdueWatchesAsync_OverdueWatch_RunsImmediately()
    {
        var watchId = Guid.NewGuid();
        var watch = new WatchedSite
        {
            Id = watchId,
            Url = "https://example.com",
            IsEnabled = true,
            LastChecked = DateTime.UtcNow.AddHours(-2),
            CheckInterval = TimeSpan.FromHours(1),
            PipelineDefinitionJson = CreatePipelineJson("https://example.com")
        };

        _watchService.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([watch]);

        await InvokeRunOverdueWatchesAsync();

        await _pipelineExecutor.Received(1)
            .ExecuteAsync(Arg.Any<PipelineDefinition>(), watchId, _stateStore, Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Is(false));
    }

    [Test]
    public async Task ExecuteAsync_CheckFailure_BroadcastsErrorStatus()
    {
        var watchId = Guid.NewGuid();
        var watch = new WatchedSite
        {
            Id = watchId,
            Url = "https://example.com",
            PipelineDefinitionJson = CreatePipelineJson("https://example.com")
        };

        _watchService.GetWatchesDueForCheckAsync(Arg.Any<CancellationToken>())
            .Returns([watch]);
        _pipelineExecutor.ExecuteAsync(
                Arg.Any<PipelineDefinition>(),
                watchId,
                _stateStore,
                Arg.Any<object?>(),
                Arg.Any<CancellationToken>(),
                Arg.Is(false))
            .ThrowsAsync(new InvalidOperationException("Test failure"));

        await InvokeCheckPendingWatchesAsync();

        // Should broadcast "Checking" then "Error" status
        await _clientProxy.Received(2).SendCoreAsync(
            "WatchStatusChanged",
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_CancellationRequested_StopsGracefully()
    {
        // Start and immediately stop — timer never ticks so no watches are processed
        await _sut.StartAsync(CancellationToken.None);
        await _sut.StopAsync(CancellationToken.None);

        await _watchService.DidNotReceive()
            .GetWatchesDueForCheckAsync(Arg.Any<CancellationToken>());
    }
}
