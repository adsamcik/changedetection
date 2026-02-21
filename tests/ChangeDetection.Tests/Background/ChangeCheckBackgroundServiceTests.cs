using System.Collections.Concurrent;
using System.Reflection;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Hubs;
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
    private readonly IHubContext<ChangeDetectionHub> _hubContext;
    private readonly IClientProxy _clientProxy;
    private readonly ILogger<ChangeCheckBackgroundService> _logger;
    private readonly ChangeCheckBackgroundService _sut;

    public ChangeCheckBackgroundServiceTests()
    {
        _watchService = Substitute.For<IWatchService>();
        var notificationService = Substitute.For<INotificationService>();
        _settingsRepo = Substitute.For<IRepository<AppSettings>>();
        _eventRepo = Substitute.For<IRepository<ChangeEvent>>();
        _logger = Substitute.For<ILogger<ChangeCheckBackgroundService>>();

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
        innerScope.ServiceProvider.Returns(innerProvider);
        innerScopeFactory.CreateScope().Returns(innerScope);
        outerProvider.GetService(typeof(IServiceScopeFactory)).Returns(innerScopeFactory);

        scope.ServiceProvider.Returns(outerProvider);
        _scopeFactory.CreateBackgroundScope().Returns(scope);

        // Default settings
        _settingsRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([new AppSettings { MaxConcurrentChecks = 5 }]);

        _sut = new ChangeCheckBackgroundService(_scopeFactory, _logger);
    }

    private Task InvokeCheckPendingWatchesAsync(CancellationToken ct = default)
    {
        var method = typeof(ChangeCheckBackgroundService)
            .GetMethod("CheckPendingWatchesAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        return (Task)method!.Invoke(_sut, [ct])!;
    }

    private static ConcurrentDictionary<Guid, byte> GetRunningWatches()
    {
        var field = typeof(ChangeCheckBackgroundService)
            .GetField("_runningWatches", BindingFlags.NonPublic | BindingFlags.Static);
        return (ConcurrentDictionary<Guid, byte>)field!.GetValue(null)!;
    }

    [Test]
    public async Task ExecuteAsync_NoWatchesDue_DoesNotCheck()
    {
        _watchService.GetWatchesDueForCheckAsync(Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<WatchedSite>());

        await InvokeCheckPendingWatchesAsync();

        await _watchService.DidNotReceive()
            .CheckForChangesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_WatchDue_ChecksForChanges()
    {
        var watchId = Guid.NewGuid();
        var watch = new WatchedSite { Id = watchId, Url = "https://example.com" };

        _watchService.GetWatchesDueForCheckAsync(Arg.Any<CancellationToken>())
            .Returns([watch]);
        _watchService.CheckForChangesAsync(watchId, Arg.Any<CancellationToken>())
            .Returns((ChangeEvent?)null);
        _watchService.GetByIdAsync(watchId, Arg.Any<CancellationToken>())
            .Returns(watch);

        await InvokeCheckPendingWatchesAsync();

        await _watchService.Received(1)
            .CheckForChangesAsync(watchId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_DuplicateWatch_SkipsAlreadyRunning()
    {
        var watchId = Guid.NewGuid();
        var watch = new WatchedSite { Id = watchId, Url = "https://example.com" };
        var runningWatches = GetRunningWatches();

        // Pre-add the watch ID to simulate it already being checked
        runningWatches.TryAdd(watchId, 0);

        try
        {
            _watchService.GetWatchesDueForCheckAsync(Arg.Any<CancellationToken>())
                .Returns([watch]);

            await InvokeCheckPendingWatchesAsync();

            await _watchService.DidNotReceive()
                .CheckForChangesAsync(watchId, Arg.Any<CancellationToken>());
        }
        finally
        {
            runningWatches.TryRemove(watchId, out _);
        }
    }

    [Test]
    public async Task ExecuteAsync_CheckFailure_BroadcastsErrorStatus()
    {
        var watchId = Guid.NewGuid();
        var watch = new WatchedSite { Id = watchId, Url = "https://example.com" };

        _watchService.GetWatchesDueForCheckAsync(Arg.Any<CancellationToken>())
            .Returns([watch]);
        _watchService.CheckForChangesAsync(watchId, Arg.Any<CancellationToken>())
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
