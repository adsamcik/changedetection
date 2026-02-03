using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.LLM;
using ChangeDetection.Services.LLM.Factories;
using ChangeDetection.Tests.Llm.Fixtures;
using ChangeDetection.Tests.Llm.TestHelpers;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Llm;

/// <summary>
/// Tests demonstrating how to use MockLlmHttpHandler for deterministic LLM testing.
/// 
/// These tests focus on the mock handler infrastructure itself - queueing responses,
/// capturing requests, handling errors, etc. For tests using real captured LLM responses,
/// see FixtureBasedLlmTests.
/// </summary>
[Category("Unit")]
public class MockLlmIntegrationTests : TestBase
{
    #region Sample Test Responses
    
    // Simple inline responses for infrastructure testing
    private const string SimpleTextResponse = "Hello! I'm a mock LLM response.";

    #endregion

    [Test]
    public async Task MockHandler_ReturnsChatCompletion_WhenQueuedResponseAvailable()
    {
        // Arrange
        var mockHandler = new MockLlmHttpHandler()
            .QueueResponse(SimpleTextResponse);

        var (chain, _) = CreateLlmChain(mockHandler);

        // Act
        var result = await chain.ExecuteAsync("Say hello");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Content.ShouldBe(SimpleTextResponse);
    }

    [Test]
    public async Task MockHandler_CapturesRequests_ForAssertions()
    {
        // Arrange
        var mockHandler = new MockLlmHttpHandler()
            .WithDefaultResponse("OK");

        var (chain, _) = CreateLlmChain(mockHandler);

        // Act
        await chain.ExecuteAsync("What is the weather today?");

        // Assert
        mockHandler.CapturedRequests.Count.ShouldBe(1);
        var request = mockHandler.CapturedRequests[0];
        request.RequestUri?.AbsolutePath.ShouldBe("/v1/chat/completions");
        request.GetUserMessage().ShouldNotBeNull();
        request.GetUserMessage()!.ShouldContain("What is the weather today?");
    }

    [Test]
    public async Task MockHandler_ReturnsQueuedResponses_InOrder()
    {
        // Arrange
        var mockHandler = new MockLlmHttpHandler()
            .QueueResponse("First response")
            .QueueResponse("Second response")
            .QueueResponse("Third response");

        var (chain, _) = CreateLlmChain(mockHandler);

        // Act & Assert
        var result1 = await chain.ExecuteAsync("Query 1");
        result1.Content.ShouldBe("First response");

        var result2 = await chain.ExecuteAsync("Query 2");
        result2.Content.ShouldBe("Second response");

        var result3 = await chain.ExecuteAsync("Query 3");
        result3.Content.ShouldBe("Third response");
    }

    [Test]
    public async Task MockHandler_HandlesError_WhenQueuedErrorResponse()
    {
        // Arrange
        var mockHandler = new MockLlmHttpHandler()
            .QueueError(System.Net.HttpStatusCode.TooManyRequests, "Rate limit exceeded");

        var (chain, _) = CreateLlmChain(mockHandler);

        // Act
        var result = await chain.ExecuteAsync("This should fail");

        // Assert
        result.IsSuccess.ShouldBeFalse();
        // The error message will be from Semantic Kernel, not our mock directly
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    [Test]
    public async Task MockHandler_ReportsTokenUsage_FromResponse()
    {
        // Arrange
        var mockHandler = new MockLlmHttpHandler()
            .QueueResponse("Test response", inputTokens: 150, outputTokens: 75);

        var (chain, _) = CreateLlmChain(mockHandler);

        // Act
        var result = await chain.ExecuteAsync("Test prompt");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        // Note: Semantic Kernel parses the usage from the response, but may have its own token counting.
        // We verify that token usage is reported (the infrastructure works), 
        // not that exact values match our mock (SK may count differently)
        result.InputTokens.ShouldBeGreaterThan(0);
        result.OutputTokens.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task MockHandler_UsesDefaultResponse_WhenNoQueuedResponses()
    {
        // Arrange
        var mockHandler = new MockLlmHttpHandler()
            .WithDefaultResponse("Default fallback response");

        var (chain, _) = CreateLlmChain(mockHandler);

        // Act - Multiple calls should all get the default response
        var result1 = await chain.ExecuteAsync("Query 1");
        var result2 = await chain.ExecuteAsync("Query 2");

        // Assert
        result1.Content.ShouldBe("Default fallback response");
        result2.Content.ShouldBe("Default fallback response");
    }

    [Test]
    public async Task MockHandler_Returns404_ForUnknownEndpoint()
    {
        // Arrange
        var mockHandler = new MockLlmHttpHandler();
        using var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("http://localhost:11434")
        };

        // Act
        var response = await httpClient.GetAsync("/unknown/endpoint");

        // Assert
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("Unknown endpoint");
    }

    [Test]
    public async Task MockHandler_ThrowsException_WhenNoResponseConfigured()
    {
        // Arrange
        var mockHandler = new MockLlmHttpHandler(); // No responses configured
        using var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("http://localhost:11434")
        };

        // Act & Assert
        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await httpClient.PostAsync("/v1/chat/completions", new StringContent("{}"))
        );

        exception.Message.ShouldContain("No mock response configured");
    }

    [Test]
    public async Task MockHandler_CapturesRequestMethod()
    {
        // Arrange
        var mockHandler = new MockLlmHttpHandler()
            .WithDefaultResponse("OK");

        var (chain, _) = CreateLlmChain(mockHandler);

        // Act
        await chain.ExecuteAsync("Test prompt");

        // Assert
        mockHandler.CapturedRequests.Count.ShouldBeGreaterThan(0);
        var request = mockHandler.CapturedRequests[0];
        request.Method.ShouldBe(System.Net.Http.HttpMethod.Post);
    }

    #region Test Helpers

    private (LlmProviderChain Chain, MockLlmHttpHandler Handler) CreateLlmChain(MockLlmHttpHandler mockHandler)
    {
        var providerRepo = new InMemoryRepository<LlmProviderConfig>();
        var usageRepo = new InMemoryRepository<LlmUsageRecord>();
        var logger = CreateLogger<LlmProviderChain>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var llmLogService = Substitute.For<ILlmLogService>();
        var httpClientFactory = new MockHttpClientFactory(mockHandler);

        IEnumerable<ILlmKernelFactory> factories = [
            new OllamaKernelFactory(),
            new OpenAIKernelFactory(),
            new AzureOpenAIKernelFactory(),
            new GeminiKernelFactory(),
            new ClaudeKernelFactory()
        ];

        // Configure a mock Ollama provider pointing to our mock handler
        providerRepo.InsertAsync(new LlmProviderConfig
        {
            Id = Guid.NewGuid(),
            Name = "MockOllama",
            ProviderType = LlmProviderType.Ollama,
            Model = "mock-model",
            Endpoint = "http://localhost:11434", // Doesn't matter - we intercept at handler level
            Priority = 1,
            IsEnabled = true,
            IsHealthy = true,
            TimeoutSeconds = 30
        }).Wait();

        var chain = new LlmProviderChain(
            providerRepo,
            usageRepo,
            logger,
            serviceProvider,
            llmLogService,
            factories,
            httpClientFactory);

        return (chain, mockHandler);
    }

    #endregion
}
