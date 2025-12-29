using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Startup;

[Category("Unit")]
public class WatchStatusRecoveryServiceTests
{
    private readonly IRepository<WatchedSite> _watchRepo;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WatchStatusRecoveryService> _logger;

    public WatchStatusRecoveryServiceTests()
    {
        _watchRepo = Substitute.For<IRepository<WatchedSite>>();
        _logger = Substitute.For<ILogger<WatchStatusRecoveryService>>();
        
        var scope = Substitute.For<IServiceScope>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scopedServiceProvider = Substitute.For<IServiceProvider>();
        
        scopedServiceProvider.GetService(typeof(IRepository<WatchedSite>)).Returns(_watchRepo);
        scope.ServiceProvider.Returns(scopedServiceProvider);
        scopeFactory.CreateScope().Returns(scope);
        
        _serviceProvider = Substitute.For<IServiceProvider>();
        _serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(scopeFactory);
    }

    [Test]
    public async Task StartAsync_NoStuckWatches_DoesNothing()
    {
        // Arrange
        _watchRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<WatchedSite, bool>>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        
        var service = new WatchStatusRecoveryService(_serviceProvider, _logger);
        
        // Act
        await service.StartAsync(CancellationToken.None);
        
        // Assert
        await _watchRepo.DidNotReceive().UpdateAsync(Arg.Any<WatchedSite>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartAsync_WithStuckWatches_ResetsToActive()
    {
        // Arrange
        var stuckWatch = new WatchedSite
        {
            Id = Guid.NewGuid(),
            Url = "https://example.com",
            Name = "Test Watch",
            Status = WatchStatus.Checking
        };
        
        _watchRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<WatchedSite, bool>>>(), Arg.Any<CancellationToken>())
            .Returns([stuckWatch]);
        
        var service = new WatchStatusRecoveryService(_serviceProvider, _logger);
        
        // Act
        await service.StartAsync(CancellationToken.None);
        
        // Assert
        await _watchRepo.Received(1).UpdateAsync(Arg.Is<WatchedSite>(w => 
            w.Id == stuckWatch.Id && 
            w.Status == WatchStatus.Active), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartAsync_WithMultipleStuckWatches_ResetsAllToActive()
    {
        // Arrange
        var stuckWatches = new List<WatchedSite>
        {
            new() { Id = Guid.NewGuid(), Url = "https://example1.com", Status = WatchStatus.Checking },
            new() { Id = Guid.NewGuid(), Url = "https://example2.com", Status = WatchStatus.Checking },
            new() { Id = Guid.NewGuid(), Url = "https://example3.com", Status = WatchStatus.Checking }
        };
        
        _watchRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<WatchedSite, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(stuckWatches);
        
        var service = new WatchStatusRecoveryService(_serviceProvider, _logger);
        
        // Act
        await service.StartAsync(CancellationToken.None);
        
        // Assert
        await _watchRepo.Received(3).UpdateAsync(Arg.Any<WatchedSite>(), Arg.Any<CancellationToken>());
        stuckWatches.ShouldAllBe(w => w.Status == WatchStatus.Active);
    }

    [Test]
    public async Task StartAsync_WhenRepositoryThrows_DoesNotRethrow()
    {
        // Arrange - Recovery should not prevent app startup
        _watchRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<WatchedSite, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IEnumerable<WatchedSite>>(new InvalidOperationException("Database error")));
        
        var service = new WatchStatusRecoveryService(_serviceProvider, _logger);
        
        // Act & Assert - Should not throw
        await Should.NotThrowAsync(() => service.StartAsync(CancellationToken.None));
    }

    [Test]
    public async Task StopAsync_DoesNothing()
    {
        // Arrange
        var service = new WatchStatusRecoveryService(_serviceProvider, _logger);
        
        // Act & Assert - StopAsync is a no-op
        await service.StopAsync(CancellationToken.None);
    }
}

[Category("Unit")]
public class GracefulShutdownServiceTests
{
    private readonly IRepository<WatchedSite> _watchRepo;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<GracefulShutdownService> _logger;

    public GracefulShutdownServiceTests()
    {
        _watchRepo = Substitute.For<IRepository<WatchedSite>>();
        _logger = Substitute.For<ILogger<GracefulShutdownService>>();
        
        var scope = Substitute.For<IServiceScope>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scopedServiceProvider = Substitute.For<IServiceProvider>();
        
        scopedServiceProvider.GetService(typeof(IRepository<WatchedSite>)).Returns(_watchRepo);
        scope.ServiceProvider.Returns(scopedServiceProvider);
        scopeFactory.CreateScope().Returns(scope);
        
        _serviceProvider = Substitute.For<IServiceProvider>();
        _serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(scopeFactory);
    }

    [Test]
    public async Task StartAsync_LogsInitialization()
    {
        // Arrange
        var service = new GracefulShutdownService(_serviceProvider, _logger);
        
        // Act
        await service.StartAsync(CancellationToken.None);
        
        // Assert - Just verify it doesn't throw
        service.ShouldNotBeNull();
    }

    [Test]
    public async Task StopAsync_NoCheckingWatches_DoesNotUpdate()
    {
        // Arrange
        _watchRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<WatchedSite, bool>>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        
        var service = new GracefulShutdownService(_serviceProvider, _logger);
        
        // Act
        await service.StopAsync(CancellationToken.None);
        
        // Assert
        await _watchRepo.DidNotReceive().UpdateAsync(Arg.Any<WatchedSite>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StopAsync_WithCheckingWatches_ResetsAndAddsShutdownNote()
    {
        // Arrange
        var checkingWatch = new WatchedSite
        {
            Id = Guid.NewGuid(),
            Url = "https://example.com",
            Name = "Test Watch",
            Status = WatchStatus.Checking
        };
        
        _watchRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<WatchedSite, bool>>>(), Arg.Any<CancellationToken>())
            .Returns([checkingWatch]);
        
        var service = new GracefulShutdownService(_serviceProvider, _logger);
        
        // Act
        await service.StopAsync(CancellationToken.None);
        
        // Assert
        await _watchRepo.Received(1).UpdateAsync(Arg.Is<WatchedSite>(w => 
            w.Id == checkingWatch.Id && 
            w.Status == WatchStatus.Active &&
            w.LastError == "Check interrupted by application shutdown"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StopAsync_WhenRepositoryThrows_DoesNotRethrow()
    {
        // Arrange - Shutdown should complete gracefully even if DB fails
        _watchRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<WatchedSite, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IEnumerable<WatchedSite>>(new InvalidOperationException("Database error")));
        
        var service = new GracefulShutdownService(_serviceProvider, _logger);
        
        // Act & Assert - Should not throw
        await Should.NotThrowAsync(() => service.StopAsync(CancellationToken.None));
    }

    [Test]
    public async Task StopAsync_WhenCancelled_HandlesGracefully()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();
        
        _watchRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<WatchedSite, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IEnumerable<WatchedSite>>(new OperationCanceledException()));
        
        var service = new GracefulShutdownService(_serviceProvider, _logger);
        
        // Act & Assert - Should handle cancellation gracefully
        await Should.NotThrowAsync(() => service.StopAsync(cts.Token));
    }
}
