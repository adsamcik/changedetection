using ChangeDetection.Tests.Infrastructure;
using ChangeDetection.Tests.Llm.Cache;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Llm;

/// <summary>
/// Example tests demonstrating the LLM response caching infrastructure.
/// 
/// These tests show how to use the caching layer to:
/// 1. Capture real LLM responses on first run (with Ollama)
/// 2. Replay cached responses on subsequent runs (without Ollama)
/// 3. Fail fast in CI when cache is missing
/// 
/// The caching layer handles everything automatically:
/// - Development: CacheFirst mode (uses cache if available, calls LLM if not)
/// - CI (SKIP_OLLAMA_TESTS=true): CacheOnly mode (throws if cache miss)
/// 
/// No explicit skip logic needed - the caching infrastructure manages behavior.
/// Tests use the LlmCached category to indicate they use cached LLM responses.
/// </summary>
[Category("LlmCached")]
public class CachedLlmExampleTests : TestBase
{
    private Kernel _kernel = null!;
    private CachingLlmHttpHandler _handler = null!;

    [Before(Test)]
    public async Task SetUp()
    {
        // Create kernel with caching - mode auto-detected from environment
        // - Development: CacheFirst (captures new, returns cached)
        // - CI (SKIP_OLLAMA_TESTS=true): CacheOnly (throws if not cached)
        var output = TestContext.Current?.OutputWriter;
        (_kernel, _handler) = CachedLlmKernelFactory.CreateKernel(output: output);
        await Task.CompletedTask;
    }

    [After(Test)]
    public void TearDown()
    {
        if (_handler != null)
        {
            TestContext.Current?.OutputWriter?.WriteLine(_handler.GetStatisticsSummary());
            _handler.Dispose();
        }
    }

    [Test]
    public async Task SimpleChat_WithCaching_ReturnsResponse()
    {
        // Arrange
        var chat = _kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddUserMessage("Hello! Please respond with exactly 'Hi there!' and nothing else.");

        // Act - This will use cached response if available, or call real LLM and cache it
        var response = await chat.GetChatMessageContentAsync(history);

        // Assert
        response.Content.ShouldNotBeNullOrEmpty();
        Log($"Response: {response.Content}");
        
        // After test, we can verify cache usage
        if (_handler.CacheHits > 0)
        {
            Log("✓ Response served from cache (fast!)");
        }
        else if (_handler.CacheStores > 0)
        {
            Log("✓ Response captured and cached for future runs");
        }
    }

    [Test]
    public async Task PriceExtraction_WithCaching_ReturnsJson()
    {
        // Arrange
        var chat = _kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage("You are a JSON extraction assistant. Output only valid JSON, no markdown.");
        history.AddUserMessage("""
            Extract the price from this HTML:
            <div class="product">
              <span class="price">$29.99</span>
              <span class="old-price">$49.99</span>
            </div>
            
            Return JSON: {"currentPrice": number, "originalPrice": number, "currency": string}
            """);

        // Act
        try
        {
            var response = await chat.GetChatMessageContentAsync(history);

            // Assert
            response.Content.ShouldNotBeNullOrEmpty();
            response.Content.ShouldContain("29.99");
            Log($"Price extraction: {response.Content}");
        }
        catch (CacheMissException ex)
        {
            CacheSkipHelper.SkipOnCacheMiss(ex);
        }
    }

    [Test]
    public async Task ContentClassification_WithCaching_IdentifiesPageType()
    {
        // Arrange
        var chat = _kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage("You are a content classification assistant.");
        history.AddUserMessage("""
            Classify this webpage content:
            
            ---
            Apple MacBook Pro 16-inch
            Price: $2,499.00
            In Stock: Yes
            Features: M3 Pro chip, 18GB RAM, 512GB SSD
            Add to Cart button available
            ---
            
            What type of page is this? Options: product, article, blog, forum, search-results, other
            Respond with just the page type.
            """);

        // Act
        try
        {
            var response = await chat.GetChatMessageContentAsync(history);

            // Assert
            response.Content.ShouldNotBeNullOrEmpty();
            response.Content!.ToLowerInvariant().ShouldContain("product");
            Log($"Classification: {response.Content}");
        }
        catch (CacheMissException ex)
        {
            CacheSkipHelper.SkipOnCacheMiss(ex);
        }
    }

    /// <summary>
    /// This test explicitly uses CacheOnly mode to demonstrate CI behavior.
    /// It will fail if the response is not already cached.
    /// </summary>
    [Test]
    public async Task CacheOnlyMode_ThrowsOnMiss()
    {
        // Arrange - Use explicit CacheOnly mode
        using var handler = new CachingLlmHttpHandler(CacheMode.CacheOnly);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        
        var kernel = Kernel.CreateBuilder()
            .AddOpenAIChatCompletion(
                modelId: "test-model",
                apiKey: "ollama",
                endpoint: new Uri("http://localhost:11434/v1"),
                httpClient: httpClient)
            .Build();

        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddUserMessage("This unique prompt should not be in the cache " + Guid.NewGuid());

        // Act & Assert - Should throw CacheMissException
        await Should.ThrowAsync<CacheMissException>(async () =>
            await chat.GetChatMessageContentAsync(history));
        
        Log("✓ CacheOnly correctly threw CacheMissException for uncached prompt");
    }
}
