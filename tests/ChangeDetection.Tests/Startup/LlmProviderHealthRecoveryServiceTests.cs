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
public class LlmProviderHealthRecoveryServiceTests
{
    private readonly IRepository<LlmProviderConfig> _providerRepo;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LlmProviderHealthRecoveryService> _logger;

    public LlmProviderHealthRecoveryServiceTests()
    {
        _providerRepo = Substitute.For<IRepository<LlmProviderConfig>>();
        _logger = Substitute.For<ILogger<LlmProviderHealthRecoveryService>>();

        var scope = Substitute.For<IServiceScope>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scopedServiceProvider = Substitute.For<IServiceProvider>();

        scopedServiceProvider.GetService(typeof(IRepository<LlmProviderConfig>)).Returns(_providerRepo);
        scope.ServiceProvider.Returns(scopedServiceProvider);
        scopeFactory.CreateScope().Returns(scope);

        _serviceProvider = Substitute.For<IServiceProvider>();
        _serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(scopeFactory);
    }

    [Test]
    public async Task Constructor_DoesNotThrow()
    {
        var service = new LlmProviderHealthRecoveryService(_serviceProvider, _logger);
        service.ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task StartAsync_NoUnhealthyProviders_DoesNotUpdate()
    {
        // Arrange
        _providerRepo.FindAsync(
                Arg.Any<System.Linq.Expressions.Expression<Func<LlmProviderConfig, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns([]);

        var service = new LlmProviderHealthRecoveryService(_serviceProvider, _logger);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        await _providerRepo.DidNotReceive()
            .UpdateAsync(Arg.Any<LlmProviderConfig>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartAsync_WithUnhealthyProviders_ResetsToHealthy()
    {
        // Arrange
        var unhealthyProvider = new LlmProviderConfig
        {
            Id = Guid.NewGuid(),
            Name = "Test Provider",
            Model = "test-model",
            IsHealthy = false,
            LastError = "Previous error"
        };

        _providerRepo.FindAsync(
                Arg.Any<System.Linq.Expressions.Expression<Func<LlmProviderConfig, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns([unhealthyProvider]);

        var service = new LlmProviderHealthRecoveryService(_serviceProvider, _logger);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        await _providerRepo.Received(1).UpdateAsync(
            Arg.Is<LlmProviderConfig>(p =>
                p.Id == unhealthyProvider.Id &&
                p.IsHealthy &&
                p.LastError == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartAsync_WithMultipleUnhealthyProviders_ResetsAll()
    {
        // Arrange
        var providers = new List<LlmProviderConfig>
        {
            new() { Name = "Provider1", Model = "m1", IsHealthy = false, LastError = "err1" },
            new() { Name = "Provider2", Model = "m2", IsHealthy = false, LastError = "err2" }
        };

        _providerRepo.FindAsync(
                Arg.Any<System.Linq.Expressions.Expression<Func<LlmProviderConfig, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(providers);

        var service = new LlmProviderHealthRecoveryService(_serviceProvider, _logger);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        await _providerRepo.Received(2)
            .UpdateAsync(Arg.Any<LlmProviderConfig>(), Arg.Any<CancellationToken>());
        providers.ShouldAllBe(p => p.IsHealthy);
        providers.ShouldAllBe(p => p.LastError == null);
    }

    [Test]
    public async Task StartAsync_WhenRepositoryThrows_DoesNotRethrow()
    {
        // Arrange
        _providerRepo.FindAsync(
                Arg.Any<System.Linq.Expressions.Expression<Func<LlmProviderConfig, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IEnumerable<LlmProviderConfig>>(
                new InvalidOperationException("Database error")));

        var service = new LlmProviderHealthRecoveryService(_serviceProvider, _logger);

        // Act & Assert
        await Should.NotThrowAsync(() => service.StartAsync(CancellationToken.None));
    }

    [Test]
    public async Task StopAsync_CompletesWithoutError()
    {
        var service = new LlmProviderHealthRecoveryService(_serviceProvider, _logger);
        await service.StopAsync(CancellationToken.None);
    }
}
