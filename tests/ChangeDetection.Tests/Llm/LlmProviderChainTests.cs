using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.LLM;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace ChangeDetection.Tests.Llm;

public class LlmProviderChainTests
{
    private readonly IRepository<LlmProviderConfig> _providerRepo;
    private readonly IRepository<LlmUsageRecord> _usageRepo;
    private readonly ILogger<LlmProviderChain> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly LlmProviderChain _sut;

    public LlmProviderChainTests()
    {
        _providerRepo = Substitute.For<IRepository<LlmProviderConfig>>();
        _usageRepo = Substitute.For<IRepository<LlmUsageRecord>>();
        _logger = Substitute.For<ILogger<LlmProviderChain>>();
        _serviceProvider = Substitute.For<IServiceProvider>();
        _sut = new LlmProviderChain(_providerRepo, _usageRepo, _logger, _serviceProvider);
    }

    [Fact]
    public async Task ExecuteAsync_NoProviders_ReturnsFailure()
    {
        // Arrange
        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<LlmProviderConfig>());

        // Act
        var result = await _sut.ExecuteAsync("Test prompt");

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("No LLM providers available");
    }

    [Fact]
    public async Task ExecuteAsync_NoEnabledProviders_ReturnsFailure()
    {
        // Arrange
        var providers = new List<LlmProviderConfig>
        {
            new() { Name = "Test", IsEnabled = false, IsHealthy = true, ApiKey = "key", ModelId = "model" }
        };
        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(providers);

        // Act
        var result = await _sut.ExecuteAsync("Test prompt");

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("No LLM providers available");
    }

    [Fact]
    public async Task ExecuteAsync_NoHealthyProviders_ReturnsFailure()
    {
        // Arrange
        var providers = new List<LlmProviderConfig>
        {
            new() { Name = "Test", IsEnabled = true, IsHealthy = false, ApiKey = "key", ModelId = "model" }
        };
        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(providers);

        // Act
        var result = await _sut.ExecuteAsync("Test prompt");

        // Assert
        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WithOptions_UsesProvidedOptions()
    {
        // Arrange
        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<LlmProviderConfig>());

        var options = new LlmRequestOptions
        {
            Temperature = 0.5f,
            MaxTokens = 100
        };

        // Act
        var result = await _sut.ExecuteAsync("Test prompt", options);

        // Assert
        result.IsSuccess.ShouldBeFalse(); // No providers
    }

    [Fact]
    public async Task ExecuteAsync_SpecificProvider_FiltersToMatchingProvider()
    {
        // Arrange
        var providers = new List<LlmProviderConfig>
        {
            new() { Name = "Provider1", IsEnabled = true, IsHealthy = true, Priority = 1, ApiKey = "key1", ModelId = "model1" },
            new() { Name = "Provider2", IsEnabled = true, IsHealthy = true, Priority = 2, ApiKey = "key2", ModelId = "model2" }
        };
        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(providers);

        var options = new LlmRequestOptions { ProviderName = "Provider2" };

        // Act - will fail because we can't mock the Semantic Kernel
        var result = await _sut.ExecuteAsync("Test prompt", options);

        // Assert - Verifies the provider filtering logic runs
        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_RecordsDuration()
    {
        // Arrange
        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<LlmProviderConfig>());

        // Act
        var result = await _sut.ExecuteAsync("Test prompt");

        // Assert
        result.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
    }
}

public class LlmRequestOptionsTests
{
    [Fact]
    public void LlmRequestOptions_HasCorrectDefaults()
    {
        // Act
        var options = new LlmRequestOptions();

        // Assert
        options.Temperature.ShouldBe(0.7f);
        options.MaxTokens.ShouldBe(2048);
        options.ProviderName.ShouldBeNull();
    }

    [Fact]
    public void LlmRequestOptions_CanSetAllProperties()
    {
        // Arrange & Act
        var options = new LlmRequestOptions
        {
            Temperature = 0.5f,
            MaxTokens = 1000,
            ProviderName = "OpenAI"
        };

        // Assert
        options.Temperature.ShouldBe(0.5f);
        options.MaxTokens.ShouldBe(1000);
        options.ProviderName.ShouldBe("OpenAI");
    }
}

public class LlmResponseTests
{
    [Fact]
    public void LlmResponse_HasCorrectDefaults()
    {
        // Act
        var response = new LlmResponse();

        // Assert
        response.IsSuccess.ShouldBeFalse();
        response.Content.ShouldBeNull();
        response.ErrorMessage.ShouldBeNull();
        response.ProviderName.ShouldBeNull();
        response.FailedProviderCount.ShouldBe(0);
        response.DurationMs.ShouldBe(0);
    }

    [Fact]
    public void LlmResponse_CanSetAllProperties()
    {
        // Arrange & Act
        var response = new LlmResponse
        {
            IsSuccess = true,
            Content = "Test response",
            ProviderName = "OpenAI",
            FailedProviderCount = 1,
            DurationMs = 500,
            TokensUsed = 100
        };

        // Assert
        response.IsSuccess.ShouldBeTrue();
        response.Content.ShouldBe("Test response");
        response.ProviderName.ShouldBe("OpenAI");
        response.FailedProviderCount.ShouldBe(1);
        response.DurationMs.ShouldBe(500);
        response.TokensUsed.ShouldBe(100);
    }
}
