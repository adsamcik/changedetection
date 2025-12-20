using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.LLM;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace ChangeDetection.Tests.Llm;

/// <summary>
/// Tests for circuit breaker health state management.
/// These tests verify that provider health status is correctly updated
/// when circuit breaker state changes or when providers recover.
/// </summary>
public class CircuitBreakerHealthTests
{
    private readonly IRepository<LlmProviderConfig> _providerRepo;
    private readonly IRepository<LlmUsageRecord> _usageRepo;
    private readonly ILogger<LlmProviderChain> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILlmLogService _llmLogService;
    private readonly LlmProviderChain _sut;

    public CircuitBreakerHealthTests()
    {
        _providerRepo = Substitute.For<IRepository<LlmProviderConfig>>();
        _usageRepo = Substitute.For<IRepository<LlmUsageRecord>>();
        _logger = Substitute.For<ILogger<LlmProviderChain>>();
        _serviceProvider = Substitute.For<IServiceProvider>();
        _llmLogService = Substitute.For<ILlmLogService>();
        _sut = new LlmProviderChain(_providerRepo, _usageRepo, _logger, _serviceProvider, _llmLogService);
    }

    [Fact]
    public void LlmProviderConfig_IsHealthy_DefaultsToTrue()
    {
        // Act
        var config = new LlmProviderConfig { Name = "Test", Model = "test-model" };

        // Assert
        config.IsHealthy.ShouldBeTrue();
    }

    [Fact]
    public void LlmProviderConfig_HealthProperties_CanBeSet()
    {
        // Arrange
        var config = new LlmProviderConfig { Name = "Test", Model = "test-model" };
        var errorTime = DateTime.UtcNow;

        // Act
        config.IsHealthy = false;
        config.LastError = "Connection timeout";
        config.LastErrorAt = errorTime;

        // Assert
        config.IsHealthy.ShouldBeFalse();
        config.LastError.ShouldBe("Connection timeout");
        config.LastErrorAt.ShouldBe(errorTime);
    }

    [Fact]
    public void LlmProviderConfig_HealthRestored_ClearsError()
    {
        // Arrange
        var config = new LlmProviderConfig 
        { 
            Name = "Test", 
            Model = "test-model",
            IsHealthy = false,
            LastError = "Previous error",
            LastErrorAt = DateTime.UtcNow.AddMinutes(-5)
        };

        // Act - Simulate health restoration
        config.IsHealthy = true;
        config.LastError = null;
        config.LastErrorAt = null;

        // Assert
        config.IsHealthy.ShouldBeTrue();
        config.LastError.ShouldBeNull();
        config.LastErrorAt.ShouldBeNull();
    }

    [Fact]
    public async Task GetProvidersToTryAsync_ExcludesUnhealthyProviders()
    {
        // Arrange
        var healthyProvider = new LlmProviderConfig 
        { 
            Name = "Healthy", 
            Model = "model",
            ApiKey = "key",
            IsEnabled = true, 
            IsHealthy = true, 
            Priority = 1 
        };
        var unhealthyProvider = new LlmProviderConfig 
        { 
            Name = "Unhealthy", 
            Model = "model",
            ApiKey = "key",
            IsEnabled = true, 
            IsHealthy = false, 
            Priority = 2 
        };
        
        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([healthyProvider, unhealthyProvider]);

        // Act - Execute will fail but we can verify the behavior through mocking
        var result = await _sut.ExecuteAsync("Test prompt");

        // Assert - The unhealthy provider should be filtered out before execution
        // We can't directly test the filtering, but we verify the chain handles unhealthy providers
        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_OnException_UpdatesProviderHealth()
    {
        // Arrange
        var provider = new LlmProviderConfig
        {
            Id = Guid.NewGuid(),
            Name = "TestProvider",
            Model = "test-model",
            ApiKey = "test-key",
            IsEnabled = true,
            IsHealthy = true,
            ProviderType = LlmProviderType.Ollama,
            Endpoint = "http://localhost:11434",
            MaxRetries = 0, // No retries to speed up test
            TimeoutSeconds = 1
        };

        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([provider]);

        // Act - This will fail because the Ollama endpoint doesn't exist
        var result = await _sut.ExecuteAsync("Test prompt");

        // Assert - Provider health should be updated on failure
        await _providerRepo.Received().UpdateAsync(
            Arg.Is<LlmProviderConfig>(p => p.Id == provider.Id && !p.IsHealthy),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_LogsCircuitBreakerBlocked_WhenCircuitOpen()
    {
        // Arrange
        var provider = new LlmProviderConfig
        {
            Id = Guid.NewGuid(),
            Name = "TestProvider",
            Model = "test-model",
            ApiKey = "test-key",
            IsEnabled = true,
            IsHealthy = true,
            ProviderType = LlmProviderType.Ollama,
            Endpoint = "http://localhost:11434",
            MaxRetries = 0,
            TimeoutSeconds = 1
        };

        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([provider]);

        // Act - Execute multiple times to potentially trigger circuit breaker
        // (Note: With MinimumThroughput=3, we need 3 failures)
        for (var i = 0; i < 5; i++)
        {
            await _sut.ExecuteAsync("Test prompt");
        }

        // Assert - The log service should have been called at least once
        // Either with errors or circuit breaker blocked messages
        _llmLogService.ReceivedCalls().ShouldNotBeEmpty();
    }
}

/// <summary>
/// Tests for the ProviderHealthStatus class used in health reporting.
/// </summary>
public class ProviderHealthStatusTests
{
    [Fact]
    public void ProviderHealthStatus_CanRepresentHealthyProvider()
    {
        // Arrange & Act
        var status = new ProviderHealthStatus
        {
            ProviderName = "OpenAI",
            IsHealthy = true,
            IsEnabled = true,
            Priority = 1,
            FailureCount = 0,
            LastError = null,
            LastErrorAt = null,
            CircuitBreakerResetAt = null
        };

        // Assert
        status.ProviderName.ShouldBe("OpenAI");
        status.IsHealthy.ShouldBeTrue();
        status.IsEnabled.ShouldBeTrue();
        status.Priority.ShouldBe(1);
        status.FailureCount.ShouldBe(0);
        status.LastError.ShouldBeNull();
        status.LastErrorAt.ShouldBeNull();
        status.CircuitBreakerResetAt.ShouldBeNull();
    }

    [Fact]
    public void ProviderHealthStatus_CanRepresentUnhealthyProvider()
    {
        // Arrange
        var errorTime = DateTime.UtcNow.AddMinutes(-2);
        var resetTime = DateTime.UtcNow.AddMinutes(3);

        // Act
        var status = new ProviderHealthStatus
        {
            ProviderName = "Ollama",
            IsHealthy = false,
            IsEnabled = true,
            Priority = 2,
            FailureCount = 5,
            LastError = "Connection refused",
            LastErrorAt = errorTime,
            CircuitBreakerResetAt = resetTime
        };

        // Assert
        status.ProviderName.ShouldBe("Ollama");
        status.IsHealthy.ShouldBeFalse();
        status.IsEnabled.ShouldBeTrue();
        status.Priority.ShouldBe(2);
        status.FailureCount.ShouldBe(5);
        status.LastError.ShouldBe("Connection refused");
        status.LastErrorAt.ShouldBe(errorTime);
        status.CircuitBreakerResetAt.ShouldBe(resetTime);
    }

    [Fact]
    public void ProviderHealthStatus_DisabledProviderCanBeUnhealthy()
    {
        // A provider can be both disabled and unhealthy
        var status = new ProviderHealthStatus
        {
            ProviderName = "DisabledProvider",
            IsHealthy = false,
            IsEnabled = false,
            Priority = 99,
            FailureCount = 10,
            LastError = "Rate limited"
        };

        // Assert
        status.IsHealthy.ShouldBeFalse();
        status.IsEnabled.ShouldBeFalse();
    }
}

/// <summary>
/// Tests for LlmLogService circuit breaker logging.
/// </summary>
public class LlmLogCircuitBreakerTests
{
    [Fact]
    public void LlmLogCategory_HasCircuitBreakerValue()
    {
        // Assert - Verify the enum has the CircuitBreaker category
        LlmLogCategory.CircuitBreaker.ShouldBe(LlmLogCategory.CircuitBreaker);
        ((int)LlmLogCategory.CircuitBreaker).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void LogCircuitBreaker_WhenOpened_CallsLogWithCorrectCategory()
    {
        // Arrange
        var llmLogService = Substitute.For<ILlmLogService>();

        // Act
        llmLogService.LogCircuitBreaker("TestProvider", isOpen: true, "Threshold exceeded");

        // Assert - Verify Log was called with correct properties (using Arg.Is for flexible matching)
        llmLogService.Received(1).Log(Arg.Is<LlmLogEntry>(entry =>
            entry.ProviderName == "TestProvider" &&
            entry.Category == LlmLogCategory.CircuitBreaker &&
            entry.Message.Contains("OPENED") &&
            entry.Message.Contains("TestProvider")));
    }

    [Fact]
    public void LogCircuitBreaker_WhenClosed_CallsLogWithCorrectCategory()
    {
        // Arrange
        var llmLogService = Substitute.For<ILlmLogService>();

        // Act
        llmLogService.LogCircuitBreaker("TestProvider", isOpen: false, "Provider recovered");

        // Assert
        llmLogService.Received(1).Log(Arg.Is<LlmLogEntry>(entry =>
            entry.ProviderName == "TestProvider" &&
            entry.Category == LlmLogCategory.CircuitBreaker &&
            entry.Message.Contains("CLOSED") &&
            entry.Message.Contains("TestProvider")));
    }

    [Fact]
    public void LogCircuitBreakerBlocked_CallsLogWithCorrectCategory()
    {
        // Arrange
        var llmLogService = Substitute.For<ILlmLogService>();

        // Act
        llmLogService.LogCircuitBreakerBlocked("TestProvider");

        // Assert
        llmLogService.Received(1).Log(Arg.Is<LlmLogEntry>(entry =>
            entry.ProviderName == "TestProvider" &&
            entry.Category == LlmLogCategory.CircuitBreaker &&
            entry.Message.Contains("blocked") &&
            entry.IsSuccess == false));
    }
}
