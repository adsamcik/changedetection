using ChangeDetection.Core.Entities;
using ChangeDetection.Services.LLM.Factories;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Llm.Factories;

[Category("Unit")]
public class LlmKernelFactoryTests
{
    #region OllamaKernelFactory Tests

    [Test]
    public async Task OllamaKernelFactory_ProviderType_ReturnsOllama()
    {
        // Arrange
        var factory = new OllamaKernelFactory();

        // Assert
        factory.ProviderType.ShouldBe(LlmProviderType.Ollama);
    }

    [Test]
    public async Task OllamaKernelFactory_CreateKernel_ReturnsKernelWithChatService()
    {
        // Arrange
        var factory = new OllamaKernelFactory();
        var config = new LlmProviderConfig
        {
            Name = "Test Ollama",
            Model = "llama3",
            Endpoint = "http://localhost:11434"
        };
        using var httpClient = new HttpClient();

        // Act
        var kernel = factory.CreateKernel(config, httpClient);

        // Assert
        kernel.ShouldNotBeNull();
        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        chatService.ShouldNotBeNull();
    }

    [Test]
    public async Task OllamaKernelFactory_CreateKernel_UsesDefaultEndpoint()
    {
        // Arrange
        var factory = new OllamaKernelFactory();
        var config = new LlmProviderConfig
        {
            Name = "Test Ollama",
            Model = "llama3",
            Endpoint = null // No endpoint specified
        };
        using var httpClient = new HttpClient();

        // Act - should not throw, uses default localhost:11434
        var kernel = factory.CreateKernel(config, httpClient);

        // Assert
        kernel.ShouldNotBeNull();
        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        chatService.ShouldNotBeNull();
    }

    #endregion

    #region OpenAIKernelFactory Tests

    [Test]
    public async Task OpenAIKernelFactory_ProviderType_ReturnsOpenAI()
    {
        // Arrange
        var factory = new OpenAIKernelFactory();

        // Assert
        factory.ProviderType.ShouldBe(LlmProviderType.OpenAI);
    }

    [Test]
    public async Task OpenAIKernelFactory_CreateKernel_ReturnsKernelWithChatService()
    {
        // Arrange
        var factory = new OpenAIKernelFactory();
        var config = new LlmProviderConfig
        {
            Name = "Test OpenAI",
            Model = "gpt-4",
            ApiKey = "test-api-key"
        };
        using var httpClient = new HttpClient();

        // Act
        var kernel = factory.CreateKernel(config, httpClient);

        // Assert
        kernel.ShouldNotBeNull();
        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        chatService.ShouldNotBeNull();
    }

    [Test]
    public async Task OpenAIKernelFactory_CreateKernel_ThrowsWhenApiKeyMissing()
    {
        // Arrange
        var factory = new OpenAIKernelFactory();
        var config = new LlmProviderConfig
        {
            Name = "Test OpenAI",
            Model = "gpt-4",
            ApiKey = null // Missing
        };
        using var httpClient = new HttpClient();

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => factory.CreateKernel(config, httpClient))
            .Message.ShouldContain("API key");
    }

    #endregion

    #region AzureOpenAIKernelFactory Tests

    [Test]
    public async Task AzureOpenAIKernelFactory_ProviderType_ReturnsAzureOpenAI()
    {
        // Arrange
        var factory = new AzureOpenAIKernelFactory();

        // Assert
        factory.ProviderType.ShouldBe(LlmProviderType.AzureOpenAI);
    }

    [Test]
    public async Task AzureOpenAIKernelFactory_CreateKernel_ReturnsKernelWithChatService()
    {
        // Arrange
        var factory = new AzureOpenAIKernelFactory();
        var config = new LlmProviderConfig
        {
            Name = "Test Azure OpenAI",
            Model = "gpt-4-deployment",
            ApiKey = "test-api-key",
            Endpoint = "https://my-resource.openai.azure.com"
        };
        using var httpClient = new HttpClient();

        // Act
        var kernel = factory.CreateKernel(config, httpClient);

        // Assert
        kernel.ShouldNotBeNull();
        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        chatService.ShouldNotBeNull();
    }

    [Test]
    public async Task AzureOpenAIKernelFactory_CreateKernel_ThrowsWhenApiKeyMissing()
    {
        // Arrange
        var factory = new AzureOpenAIKernelFactory();
        var config = new LlmProviderConfig
        {
            Name = "Test Azure OpenAI",
            Model = "gpt-4-deployment",
            ApiKey = null, // Missing
            Endpoint = "https://my-resource.openai.azure.com"
        };
        using var httpClient = new HttpClient();

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => factory.CreateKernel(config, httpClient))
            .Message.ShouldContain("API key");
    }

    [Test]
    public async Task AzureOpenAIKernelFactory_CreateKernel_ThrowsWhenEndpointMissing()
    {
        // Arrange
        var factory = new AzureOpenAIKernelFactory();
        var config = new LlmProviderConfig
        {
            Name = "Test Azure OpenAI",
            Model = "gpt-4-deployment",
            ApiKey = "test-api-key",
            Endpoint = null // Missing
        };
        using var httpClient = new HttpClient();

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => factory.CreateKernel(config, httpClient))
            .Message.ShouldContain("endpoint");
    }

    #endregion

    #region GeminiKernelFactory Tests

    [Test]
    public async Task GeminiKernelFactory_ProviderType_ReturnsGemini()
    {
        // Arrange
        var factory = new GeminiKernelFactory();

        // Assert
        factory.ProviderType.ShouldBe(LlmProviderType.Gemini);
    }

    [Test]
    public async Task GeminiKernelFactory_CreateKernel_ReturnsKernelWithChatService()
    {
        // Arrange
        var factory = new GeminiKernelFactory();
        var config = new LlmProviderConfig
        {
            Name = "Test Gemini",
            Model = "gemini-pro",
            ApiKey = "test-api-key"
        };
        using var httpClient = new HttpClient();

        // Act
        var kernel = factory.CreateKernel(config, httpClient);

        // Assert
        kernel.ShouldNotBeNull();
        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        chatService.ShouldNotBeNull();
    }

    [Test]
    public async Task GeminiKernelFactory_CreateKernel_ThrowsWhenApiKeyMissing()
    {
        // Arrange
        var factory = new GeminiKernelFactory();
        var config = new LlmProviderConfig
        {
            Name = "Test Gemini",
            Model = "gemini-pro",
            ApiKey = null // Missing
        };
        using var httpClient = new HttpClient();

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => factory.CreateKernel(config, httpClient))
            .Message.ShouldContain("API key");
    }

    #endregion

    #region ClaudeKernelFactory Tests

    [Test]
    public async Task ClaudeKernelFactory_ProviderType_ReturnsClaude()
    {
        // Arrange
        var factory = new ClaudeKernelFactory();

        // Assert
        factory.ProviderType.ShouldBe(LlmProviderType.Claude);
    }

    [Test]
    public async Task ClaudeKernelFactory_CreateKernel_ReturnsKernelWithChatService()
    {
        // Arrange
        var factory = new ClaudeKernelFactory();
        var config = new LlmProviderConfig
        {
            Name = "Test Claude",
            Model = "claude-3-opus-20240229",
            ApiKey = "test-api-key"
        };
        using var httpClient = new HttpClient();

        // Act
        var kernel = factory.CreateKernel(config, httpClient);

        // Assert
        kernel.ShouldNotBeNull();
        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        chatService.ShouldNotBeNull();
    }

    [Test]
    public async Task ClaudeKernelFactory_CreateKernel_ThrowsWhenApiKeyMissing()
    {
        // Arrange
        var factory = new ClaudeKernelFactory();
        var config = new LlmProviderConfig
        {
            Name = "Test Claude",
            Model = "claude-3-opus-20240229",
            ApiKey = null // Missing
        };
        using var httpClient = new HttpClient();

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => factory.CreateKernel(config, httpClient))
            .Message.ShouldContain("API key");
    }

    #endregion

    #region CopilotKernelFactory Tests

    [Test]
    public async Task CopilotKernelFactory_ProviderType_ReturnsCopilot()
    {
        // Arrange
        using var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => { });
        var factory = new CopilotKernelFactory(
            loggerFactory.CreateLogger<CopilotKernelFactory>(),
            loggerFactory);

        // Assert
        factory.ProviderType.ShouldBe(LlmProviderType.Copilot);
    }

    #endregion
}
