using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Background;
using ChangeDetection.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace ChangeDetection.Tests.Background;

public class ChangeCheckBackgroundServiceTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IServiceScope _scope;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWatchService _watchService;
    private readonly INotificationService _notificationService;
    private readonly IHubContext<ChangeDetectionHub> _hubContext;
    private readonly ILogger<ChangeCheckBackgroundService> _logger;

    public ChangeCheckBackgroundServiceTests()
    {
        _watchService = Substitute.For<IWatchService>();
        _notificationService = Substitute.For<INotificationService>();
        _hubContext = Substitute.For<IHubContext<ChangeDetectionHub>>();
        _logger = Substitute.For<ILogger<ChangeCheckBackgroundService>>();
        
        _scope = Substitute.For<IServiceScope>();
        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        _serviceProvider = Substitute.For<IServiceProvider>();
        
        var scopedServiceProvider = Substitute.For<IServiceProvider>();
        scopedServiceProvider.GetService(typeof(IWatchService)).Returns(_watchService);
        scopedServiceProvider.GetService(typeof(INotificationService)).Returns(_notificationService);
        scopedServiceProvider.GetService(typeof(IHubContext<ChangeDetectionHub>)).Returns(_hubContext);
        
        _scope.ServiceProvider.Returns(scopedServiceProvider);
        _scopeFactory.CreateScope().Returns(_scope);
        _serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(_scopeFactory);
    }

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        // Act & Assert
        var service = new ChangeCheckBackgroundService(_serviceProvider, _logger);
        service.ShouldNotBeNull();
    }
}

public class ChangeDetectionHubTests
{
    [Fact]
    public void Hub_CanBeInstantiated()
    {
        // Act
        var hub = new ChangeDetectionHub();

        // Assert
        hub.ShouldNotBeNull();
    }
}
