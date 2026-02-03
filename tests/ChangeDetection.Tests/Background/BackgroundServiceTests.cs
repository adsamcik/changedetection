using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Authentication;
using ChangeDetection.Services.Background;
using ChangeDetection.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Background;

[Category("Unit")]
public class ChangeCheckBackgroundServiceTests
{
    private readonly IBackgroundServiceScopeFactory _scopeFactory;
    private readonly IServiceScope _scope;
    private readonly IWatchService _watchService;
    private readonly INotificationService _notificationService;
    private readonly IHubContext<ChangeDetectionHub> _hubContext;
    private readonly IRepository<AppSettings> _settingsRepo;
    private readonly ILogger<ChangeCheckBackgroundService> _logger;

    public ChangeCheckBackgroundServiceTests()
    {
        _watchService = Substitute.For<IWatchService>();
        _notificationService = Substitute.For<INotificationService>();
        _hubContext = Substitute.For<IHubContext<ChangeDetectionHub>>();
        _settingsRepo = Substitute.For<IRepository<AppSettings>>();
        _logger = Substitute.For<ILogger<ChangeCheckBackgroundService>>();
        
        _scope = Substitute.For<IServiceScope>();
        _scopeFactory = Substitute.For<IBackgroundServiceScopeFactory>();
        
        var scopedServiceProvider = Substitute.For<IServiceProvider>();
        scopedServiceProvider.GetService(typeof(IWatchService)).Returns(_watchService);
        scopedServiceProvider.GetService(typeof(INotificationService)).Returns(_notificationService);
        scopedServiceProvider.GetService(typeof(IHubContext<ChangeDetectionHub>)).Returns(_hubContext);
        scopedServiceProvider.GetService(typeof(IRepository<AppSettings>)).Returns(_settingsRepo);
        
        // Default settings
        _settingsRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([new AppSettings { MaxConcurrentChecks = 5 }]);
        
        _scope.ServiceProvider.Returns(scopedServiceProvider);
        _scopeFactory.CreateBackgroundScope().Returns(_scope);
    }

    [Test]
    public async Task Constructor_DoesNotThrow()
    {
        // Act & Assert
        var service = new ChangeCheckBackgroundService(_scopeFactory, _logger);
        service.ShouldNotBeNull();
        await Task.CompletedTask;
    }
}

public class ChangeDetectionHubTests
{
    [Test]
    public async Task Hub_CanBeInstantiated()
    {
        // Arrange
        var logger = Substitute.For<ILogger<ChangeDetectionHub>>();
        var userContext = Substitute.For<IUserContext>();
        
        // Act
        var hub = new ChangeDetectionHub(logger, userContext);

        // Assert
        hub.ShouldNotBeNull();
        await Task.CompletedTask;
    }
}
