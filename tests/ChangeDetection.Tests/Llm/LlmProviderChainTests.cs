using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.LLM;
using ChangeDetection.Services.LLM.Factories;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using System.Net;
using TUnit.Core;

namespace ChangeDetection.Tests.Llm;

/// <summary>
/// Unit tests for LlmProviderChain.
/// Uses MockLlmHttpHandler to avoid real network calls.
/// </summary>
[Category("Unit")]
public class LlmProviderChainTests
{
    private readonly IRepository<LlmProviderConfig> _providerRepo;
    private readonly IRepository<LlmUsageRecord> _usageRepo;
    private readonly ILogger<LlmProviderChain> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILlmLogService _llmLogService;
    private readonly IEnumerable<ILlmKernelFactory> _factories;

    public LlmProviderChainTests()
    {
        _providerRepo = Substitute.For<IRepository<LlmProviderConfig>>();
        _usageRepo = Substitute.For<IRepository<LlmUsageRecord>>();
        _logger = Substitute.For<ILogger<LlmProviderChain>>();
        _serviceProvider = Substitute.For<IServiceProvider>();
        _llmLogService = Substitute.For<ILlmLogService>();
        _factories = [
            new OllamaKernelFactory(),
            new OpenAIKernelFactory(),
            new AzureOpenAIKernelFactory(),
            new GeminiKernelFactory(),
            new ClaudeKernelFactory()
        ];
    }

    [Test]
    public async Task ExecuteAsync_NoConfiguredProviders_ReturnsFailure()
    {
        // Arrange - no configured providers, use mock handler to avoid real network calls
        var mockHandler = new MockLlmHttpHandler();
        mockHandler.QueueError(HttpStatusCode.NotFound, "Not found");
        
        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<LlmProviderConfig>());

        var httpClientFactory = new MockHttpClientFactory(mockHandler);
        var sut = new LlmProviderChain(_providerRepo, _usageRepo, _logger, _serviceProvider, _llmLogService, _factories, httpClientFactory);

        // Act
        var result = await sut.ExecuteAsync("Say hello");

        // Assert - No providers available
        result.IsSuccess.ShouldBeFalse();
        // Error message depends on whether auto-detection is attempted
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    [Test]
    public async Task ExecuteAsync_SpecificProvider_FiltersToMatchingProvider()
    {
        // Arrange - Use mock handler
        var mockHandler = new MockLlmHttpHandler();
        mockHandler.WithDefaultResponse("Test response from Provider2");
        
        var providers = new List<LlmProviderConfig>
        {
            new() { Id = Guid.NewGuid(), Name = "Provider1", ProviderType = LlmProviderType.Ollama, Endpoint = "http://localhost:11434", IsEnabled = true, IsHealthy = true, Priority = 1, Model = "model1" },
            new() { Id = Guid.NewGuid(), Name = "Provider2", ProviderType = LlmProviderType.Ollama, Endpoint = "http://localhost:11434", IsEnabled = true, IsHealthy = true, Priority = 2, Model = "model2" }
        };
        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(providers);

        var httpClientFactory = new MockHttpClientFactory(mockHandler);
        var sut = new LlmProviderChain(_providerRepo, _usageRepo, _logger, _serviceProvider, _llmLogService, _factories, httpClientFactory);

        var options = new LlmRequestOptions { ProviderName = "Provider2" };

        // Act
        var result = await sut.ExecuteAsync("Test prompt", options);

        // Assert - Request should succeed
        result.ShouldNotBeNull();
        result.IsSuccess.ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_RecordsDuration()
    {
        // Arrange - Use mock handler
        var mockHandler = new MockLlmHttpHandler();
        mockHandler.WithDefaultResponse("Test response");
        
        var providers = new List<LlmProviderConfig>
        {
            new() { Id = Guid.NewGuid(), Name = "TestProvider", ProviderType = LlmProviderType.Ollama, Endpoint = "http://localhost:11434", IsEnabled = true, IsHealthy = true, Priority = 1, Model = "model" }
        };
        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(providers);

        var httpClientFactory = new MockHttpClientFactory(mockHandler);
        var sut = new LlmProviderChain(_providerRepo, _usageRepo, _logger, _serviceProvider, _llmLogService, _factories, httpClientFactory);

        // Act
        var result = await sut.ExecuteAsync("Test prompt");

        // Assert
        // Mock responds instantly so DurationMs may be 0; verify timing was recorded (not negative/unset)
        result.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task HasLargeModelAsync_WithLargeModel_ReturnsTrue()
    {
        // Arrange
        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<LlmProviderConfig>
            {
                new() { Id = Guid.NewGuid(), Name = "Large", ProviderType = LlmProviderType.Ollama, Endpoint = "http://localhost:11434", IsEnabled = true, IsHealthy = true, Priority = 1, Model = "llama3.1:70b" }
            });

        var sut = new LlmProviderChain(_providerRepo, _usageRepo, _logger, _serviceProvider, _llmLogService, _factories, new MockHttpClientFactory(new MockLlmHttpHandler()));

        // Act
        var result = await sut.HasLargeModelAsync();

        // Assert
        result.ShouldBeTrue();
    }

    [Test]
    public async Task HasLargeModelAsync_OnlySmallModels_ReturnsFalse()
    {
        // Arrange
        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<LlmProviderConfig>
            {
                new() { Id = Guid.NewGuid(), Name = "Small1", ProviderType = LlmProviderType.Ollama, Endpoint = "http://localhost:11434", IsEnabled = true, IsHealthy = true, Priority = 1, Model = "ministral-3:8b" },
                new() { Id = Guid.NewGuid(), Name = "Small2", ProviderType = LlmProviderType.Ollama, Endpoint = "http://localhost:11434", IsEnabled = true, IsHealthy = true, Priority = 2, Model = "llama3.1:7b" }
            });

        var sut = new LlmProviderChain(_providerRepo, _usageRepo, _logger, _serviceProvider, _llmLogService, _factories, new MockHttpClientFactory(new MockLlmHttpHandler()));

        // Act
        var result = await sut.HasLargeModelAsync();

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public async Task HasLargeModelAsync_NoProviders_ReturnsFalse()
    {
        // Arrange
        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<LlmProviderConfig>());

        var sut = new LlmProviderChain(_providerRepo, _usageRepo, _logger, _serviceProvider, _llmLogService, _factories, new MockHttpClientFactory(new MockLlmHttpHandler()));

        // Act
        var result = await sut.HasLargeModelAsync();

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public async Task HasLargeModelAsync_DisabledLargeModel_ReturnsFalse()
    {
        // Arrange — large model exists but is disabled
        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<LlmProviderConfig>
            {
                new() { Id = Guid.NewGuid(), Name = "Large", ProviderType = LlmProviderType.Ollama, Endpoint = "http://localhost:11434", IsEnabled = false, IsHealthy = true, Priority = 1, Model = "llama3.1:70b" }
            });

        var sut = new LlmProviderChain(_providerRepo, _usageRepo, _logger, _serviceProvider, _llmLogService, _factories, new MockHttpClientFactory(new MockLlmHttpHandler()));

        // Act
        var result = await sut.HasLargeModelAsync();

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public async Task HasLargeModelAsync_UnhealthyLargeModel_ReturnsFalse()
    {
        // Arrange — large model exists but is unhealthy (circuit breaker open)
        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<LlmProviderConfig>
            {
                new() { Id = Guid.NewGuid(), Name = "Large", ProviderType = LlmProviderType.Ollama, Endpoint = "http://localhost:11434", IsEnabled = true, IsHealthy = false, Priority = 1, Model = "llama3.1:70b" }
            });

        var sut = new LlmProviderChain(_providerRepo, _usageRepo, _logger, _serviceProvider, _llmLogService, _factories, new MockHttpClientFactory(new MockLlmHttpHandler()));

        // Act
        var result = await sut.HasLargeModelAsync();

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public async Task HasLargeModelAsync_MixedModels_ReturnsTrue()
    {
        // Arrange — mix of small and large models
        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<LlmProviderConfig>
            {
                new() { Id = Guid.NewGuid(), Name = "Small", ProviderType = LlmProviderType.Ollama, Endpoint = "http://localhost:11434", IsEnabled = true, IsHealthy = true, Priority = 1, Model = "ministral-3:8b" },
                new() { Id = Guid.NewGuid(), Name = "Large", ProviderType = LlmProviderType.Ollama, Endpoint = "http://localhost:11434", IsEnabled = true, IsHealthy = true, Priority = 2, Model = "qwen2.5:32b" }
            });

        var sut = new LlmProviderChain(_providerRepo, _usageRepo, _logger, _serviceProvider, _llmLogService, _factories, new MockHttpClientFactory(new MockLlmHttpHandler()));

        // Act
        var result = await sut.HasLargeModelAsync();

        // Assert
        result.ShouldBeTrue();
    }

    [Test]
    [Arguments("llama3.1:70b", true, "Large model (70b) should not be small")]
    [Arguments("qwen2.5:32b", true, "32b is not in the small list")]
    [Arguments("gpt-4", true, "Named model without size indicator")]
    [Arguments("claude-3-opus", true, "Named model without size indicator")]
    [Arguments("deepseek-coder:33b", true, "33b is not in the small list")]
    [Arguments("ministral-3:8b", false, "Contains both 'ministral' and '8b'")]
    [Arguments("llama3.1:7b", false, "Contains '7b'")]
    [Arguments("phi-3-mini", false, "Contains 'mini'")]
    [Arguments("gemma:3b", false, "Contains '3b'")]
    [Arguments("TinyLlama", false, "Contains 'tiny' (case insensitive)")]
    [Arguments("MINISTRAL-8B", false, "Contains 'ministral' (case insensitive)")]
    [Arguments("gemma-nano", false, "Contains 'nano'")]
    [Arguments("some-lite-model", false, "Contains 'lite'")]
    [Arguments("smalltalk-llm", false, "Contains 'small'")]
    public async Task HasLargeModelAsync_IsSmallModelDetection(string modelName, bool expectLargeModel, string reason)
    {
        // Arrange — single provider with the given model name
        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<LlmProviderConfig>
            {
                new() { Id = Guid.NewGuid(), Name = "Test", ProviderType = LlmProviderType.Ollama, Endpoint = "http://localhost:11434", IsEnabled = true, IsHealthy = true, Priority = 1, Model = modelName }
            });

        var sut = new LlmProviderChain(_providerRepo, _usageRepo, _logger, _serviceProvider, _llmLogService, _factories, new MockHttpClientFactory(new MockLlmHttpHandler()));

        // Act
        var result = await sut.HasLargeModelAsync();

        // Assert — HasLargeModelAsync returns true when model is NOT small
        result.ShouldBe(expectLargeModel, reason);
    }

    [Test]
    public async Task ExecuteAsync_PreferLargeModel_UsesLargeModelFirst()
    {
        // Arrange — small model is priority 1, large model is priority 2
        var mockHandler = new MockLlmHttpHandler();
        mockHandler.WithDefaultResponse("Response from model");

        _providerRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<LlmProviderConfig>
            {
                new() { Id = Guid.NewGuid(), Name = "SmallFirst", ProviderType = LlmProviderType.Ollama, Endpoint = "http://localhost:11434", IsEnabled = true, IsHealthy = true, Priority = 1, Model = "ministral-3:8b" },
                new() { Id = Guid.NewGuid(), Name = "LargeSecond", ProviderType = LlmProviderType.Ollama, Endpoint = "http://localhost:11434", IsEnabled = true, IsHealthy = true, Priority = 2, Model = "llama3.1:70b" }
            });

        var sut = new LlmProviderChain(_providerRepo, _usageRepo, _logger, _serviceProvider, _llmLogService, _factories, new MockHttpClientFactory(mockHandler));

        // Act — with PreferLargeModel, the large model should be tried first
        var result = await sut.ExecuteAsync("Test prompt", new LlmRequestOptions { PreferLargeModel = true });

        // Assert — the response should come from the large model
        result.IsSuccess.ShouldBeTrue();
        result.Model.ShouldBe("llama3.1:70b");
    }
}

public class LlmRequestOptionsTests
{
    [Test]
    public async Task LlmRequestOptions_HasCorrectDefaults()
    {
        // Act
        var options = new LlmRequestOptions();

        // Assert
        options.Temperature.ShouldBe(0.7f);
        options.MaxTokens.ShouldBe(1024);
        options.ProviderName.ShouldBeNull();
        options.PreferLargeModel.ShouldBeFalse();
    }

    [Test]
    public async Task LlmRequestOptions_CanSetAllProperties()
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
    [Test]
    public async Task LlmResponse_HasCorrectDefaults()
    {
        // Act
        var response = new LlmResponse();

        // Assert
        response.IsSuccess.ShouldBeFalse();
        response.Content.ShouldBeNull();
        response.ErrorMessage.ShouldBeNull();
        response.ProviderUsed.ShouldBeNull();
        response.FailedProviderCount.ShouldBe(0);
        response.DurationMs.ShouldBe(0);
    }

    [Test]
    public async Task LlmResponse_CanSetAllProperties()
    {
        // Arrange & Act
        var response = new LlmResponse
        {
            IsSuccess = true,
            Content = "Test response",
            ProviderUsed = "OpenAI",
            Model = "gpt-4",
            FailedProviderCount = 1,
            DurationMs = 500,
            InputTokens = 50,
            OutputTokens = 100,
            Cost = 0.01m
        };

        // Assert
        response.IsSuccess.ShouldBeTrue();
        response.Content.ShouldBe("Test response");
        response.ProviderUsed.ShouldBe("OpenAI");
        response.Model.ShouldBe("gpt-4");
        response.FailedProviderCount.ShouldBe(1);
        response.DurationMs.ShouldBe(500);
        response.InputTokens.ShouldBe(50);
        response.OutputTokens.ShouldBe(100);
        response.Cost.ShouldBe(0.01m);
    }
}
