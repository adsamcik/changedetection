using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Startup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Startup;

[Category("Unit")]
public class LlmProviderConfigSyncServiceTests
{
    private readonly IRepository<LlmProviderConfig> _providerRepo;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LlmProviderConfigSyncService> _logger;

    public LlmProviderConfigSyncServiceTests()
    {
        _providerRepo = Substitute.For<IRepository<LlmProviderConfig>>();
        _logger = Substitute.For<ILogger<LlmProviderConfigSyncService>>();

        var scope = Substitute.For<IServiceScope>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scopedServiceProvider = Substitute.For<IServiceProvider>();

        scopedServiceProvider.GetService(typeof(IRepository<LlmProviderConfig>)).Returns(_providerRepo);
        scope.ServiceProvider.Returns(scopedServiceProvider);
        scopeFactory.CreateScope().Returns(scope);

        _serviceProvider = Substitute.For<IServiceProvider>();
        _serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(scopeFactory);
    }

    private static IConfiguration BuildConfig(params LlmProviderOption[] providers)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < providers.Length; i++)
        {
            var p = providers[i];
            dict[$"LlmProviders:{i}:Name"] = p.Name;
            dict[$"LlmProviders:{i}:Model"] = p.Model;
            dict[$"LlmProviders:{i}:Type"] = p.Type.ToString();
            dict[$"LlmProviders:{i}:Enabled"] = p.Enabled.ToString();
            dict[$"LlmProviders:{i}:TimeoutSeconds"] = p.TimeoutSeconds.ToString();
            dict[$"LlmProviders:{i}:MaxRetries"] = p.MaxRetries.ToString();
            if (p.Endpoint != null) dict[$"LlmProviders:{i}:Endpoint"] = p.Endpoint;
            if (p.ApiKey != null) dict[$"LlmProviders:{i}:ApiKey"] = p.ApiKey;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();
    }

    [Test]
    public async Task Constructor_DoesNotThrow()
    {
        var config = BuildConfig();
        var service = new LlmProviderConfigSyncService(_serviceProvider, config, _logger);
        service.ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task StartAsync_NoProvidersConfigured_DoesNothing()
    {
        // Arrange
        var config = BuildConfig();
        var service = new LlmProviderConfigSyncService(_serviceProvider, config, _logger);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        await _providerRepo.DidNotReceive().GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartAsync_WithNewProvider_CreatesInDatabase()
    {
        // Arrange
        var option = new LlmProviderOption
        {
            Name = "Local Ollama",
            Model = "llama3.1",
            Type = LlmProviderType.Ollama,
            Endpoint = "http://localhost:11434"
        };
        var config = BuildConfig(option);

        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<LlmProviderConfig>());

        var service = new LlmProviderConfigSyncService(_serviceProvider, config, _logger);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        await _providerRepo.Received(1).InsertAsync(
            Arg.Is<LlmProviderConfig>(p =>
                p.Name == "Local Ollama" &&
                p.Model == "llama3.1" &&
                p.ProviderType == LlmProviderType.Ollama &&
                p.Endpoint == "http://localhost:11434" &&
                p.IsHealthy),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartAsync_WithExistingProvider_UpdatesInDatabase()
    {
        // Arrange
        var option = new LlmProviderOption
        {
            Name = "Local Ollama",
            Model = "llama3.2",
            Type = LlmProviderType.Ollama,
            Endpoint = "http://localhost:11434"
        };
        var config = BuildConfig(option);

        var existingProvider = new LlmProviderConfig
        {
            Id = Guid.NewGuid(),
            Name = "Local Ollama",
            Model = "llama3.1",
            ProviderType = LlmProviderType.Ollama,
            Endpoint = "http://localhost:11434"
        };

        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<LlmProviderConfig> { existingProvider });

        var service = new LlmProviderConfigSyncService(_serviceProvider, config, _logger);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        await _providerRepo.Received(1).UpdateAsync(
            Arg.Is<LlmProviderConfig>(p =>
                p.Id == existingProvider.Id &&
                p.Model == "llama3.2"),
            Arg.Any<CancellationToken>());
        await _providerRepo.DidNotReceive()
            .InsertAsync(Arg.Any<LlmProviderConfig>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartAsync_WithInvalidConfig_SkipsSync()
    {
        // Arrange - empty Name is invalid
        var option = new LlmProviderOption
        {
            Name = "",
            Model = "llama3.1",
            Type = LlmProviderType.Ollama
        };
        var config = BuildConfig(option);

        var service = new LlmProviderConfigSyncService(_serviceProvider, config, _logger);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert - should not reach the DB
        await _providerRepo.DidNotReceive().GetAllAsync(Arg.Any<CancellationToken>());
        await _providerRepo.DidNotReceive()
            .InsertAsync(Arg.Any<LlmProviderConfig>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartAsync_WithDuplicateNames_SkipsSync()
    {
        // Arrange
        var opt1 = new LlmProviderOption
        {
            Name = "Ollama",
            Model = "llama3.1",
            Type = LlmProviderType.Ollama
        };
        var opt2 = new LlmProviderOption
        {
            Name = "Ollama",
            Model = "llama3.2",
            Type = LlmProviderType.Ollama
        };
        var config = BuildConfig(opt1, opt2);

        var service = new LlmProviderConfigSyncService(_serviceProvider, config, _logger);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert - should not reach the DB due to validation errors
        await _providerRepo.DidNotReceive().GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartAsync_SetsCorrectPriority()
    {
        // Arrange
        var opt1 = new LlmProviderOption
        {
            Name = "Primary",
            Model = "gpt-4o",
            Type = LlmProviderType.Ollama
        };
        var opt2 = new LlmProviderOption
        {
            Name = "Fallback",
            Model = "llama3.1",
            Type = LlmProviderType.Ollama
        };
        var config = BuildConfig(opt1, opt2);

        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<LlmProviderConfig>());

        var service = new LlmProviderConfigSyncService(_serviceProvider, config, _logger);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert - priority matches array order
        await _providerRepo.Received(1).InsertAsync(
            Arg.Is<LlmProviderConfig>(p => p.Name == "Primary" && p.Priority == 0),
            Arg.Any<CancellationToken>());
        await _providerRepo.Received(1).InsertAsync(
            Arg.Is<LlmProviderConfig>(p => p.Name == "Fallback" && p.Priority == 1),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartAsync_WhenRepositoryThrows_DoesNotRethrow()
    {
        // Arrange
        var option = new LlmProviderOption
        {
            Name = "Ollama",
            Model = "llama3.1",
            Type = LlmProviderType.Ollama
        };
        var config = BuildConfig(option);

        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IEnumerable<LlmProviderConfig>>(
                new InvalidOperationException("Database error")));

        var service = new LlmProviderConfigSyncService(_serviceProvider, config, _logger);

        // Act & Assert
        await Should.NotThrowAsync(() => service.StartAsync(CancellationToken.None));
    }

    [Test]
    public async Task StartAsync_DoesNotOverwriteApiKeyWhenNotSet()
    {
        // Arrange - config has no ApiKey
        var option = new LlmProviderOption
        {
            Name = "Existing",
            Model = "llama3.1",
            Type = LlmProviderType.Ollama
        };
        var config = BuildConfig(option);

        var existingProvider = new LlmProviderConfig
        {
            Id = Guid.NewGuid(),
            Name = "Existing",
            Model = "old-model",
            ApiKey = "secret-key-123"
        };

        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<LlmProviderConfig> { existingProvider });

        var service = new LlmProviderConfigSyncService(_serviceProvider, config, _logger);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert - ApiKey should be preserved
        await _providerRepo.Received(1).UpdateAsync(
            Arg.Is<LlmProviderConfig>(p => p.ApiKey == "secret-key-123"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StopAsync_CompletesWithoutError()
    {
        var config = BuildConfig();
        var service = new LlmProviderConfigSyncService(_serviceProvider, config, _logger);
        await service.StopAsync(CancellationToken.None);
    }
}
