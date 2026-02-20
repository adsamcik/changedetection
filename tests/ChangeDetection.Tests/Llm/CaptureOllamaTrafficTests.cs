using ChangeDetection.Tests.Infrastructure;
using ChangeDetection.Tests.Llm.Cache;
using Microsoft.SemanticKernel.ChatCompletion;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Llm;

/// <summary>
/// Tests that exercise LLM functionality with cached responses.
/// 
/// Uses CachedLlmKernelFactory which:
/// - On first run (cache miss): Calls real Ollama, caches the response
/// - On subsequent runs (cache hit): Returns cached response instantly
/// - In CI (SKIP_OLLAMA_TESTS=true): Fails if response not in cache
/// 
/// Run these tests:
///   ./test.ps1 -Filter "/*/*/*/*CaptureOllamaTrafficTests*"
///   ./test.ps1 -Filter "/*/*/*/*CaptureSimpleGreeting*"  (single test)
/// 
/// Note: TUnit requires the /*/*/*/*ClassName* pattern for filtering tests by name.
/// </summary>
[Category("Integration")]
[Category("RequiresOllama")]
public class CaptureOllamaTrafficTests : TestBase
{
    [Before(Test)]
    public void SkipIfOllamaUnavailable()
    {
        CacheSkipHelper.SkipIfOllamaUnavailable();
    }

    /// <summary>
    /// Creates a cached kernel with optional logging output.
    /// </summary>
    private (Microsoft.SemanticKernel.Kernel Kernel, CachingLlmHttpHandler Handler) CreateCachedKernel()
    {
        var output = TestContext.Current?.OutputWriter ?? Console.Out;
        return CachedLlmKernelFactory.CreateKernel(output: output);
    }

    [Test]
    public async Task CaptureSimpleGreeting()
    {
        // Arrange
        var (kernel, handler) = CreateCachedKernel();
        using var _ = handler;
        var chatService = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddUserMessage("Hello! Please respond with exactly 'Hi there!' and nothing else.");

        // Act
        var result = await chatService.GetChatMessageContentAsync(history);

        // Assert
        result.Content.ShouldNotBeNullOrEmpty();
        
        // Log cache stats
        LogCacheStats("Simple Greeting", handler);
    }

    [Test]
    public async Task CapturePriceExtraction()
    {
        // Arrange
        var (kernel, handler) = CreateCachedKernel();
        using var _ = handler;
        var chatService = kernel.GetRequiredService<IChatCompletionService>();

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
        var result = await chatService.GetChatMessageContentAsync(history);

        // Assert
        result.Content.ShouldNotBeNullOrEmpty();
        result.Content.ShouldContain("29.99");
        
        // Log cache stats
        LogCacheStats("Price Extraction", handler);
    }

    [Test]
    public async Task CaptureSelectorGeneration()
    {
        // Arrange
        var (kernel, handler) = CreateCachedKernel();
        using var _ = handler;
        var chatService = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage("You are a CSS selector generation assistant. Output only valid JSON.");
        history.AddUserMessage("""
            Given this HTML structure, generate a CSS selector to find the main product title:
            <html>
              <body>
                <div class="container">
                  <h1 class="product-title" id="main-title">Awesome Product</h1>
                  <p class="description">Great product description here</p>
                </div>
              </body>
            </html>
            
            Return JSON: {"selector": string, "confidence": number between 0 and 1}
            """);

        // Act
        var result = await chatService.GetChatMessageContentAsync(history);

        // Assert
        result.Content.ShouldNotBeNullOrEmpty();
        // Should contain some form of selector
        (result.Content.Contains("product-title") || result.Content.Contains("main-title") || result.Content.Contains("h1"))
            .ShouldBeTrue("Response should contain a selector targeting the title");
        
        // Log cache stats
        LogCacheStats("Selector Generation", handler);
    }

    [Test]
    public async Task CaptureContentClassification()
    {
        // Arrange
        var (kernel, handler) = CreateCachedKernel();
        using var _ = handler;
        var chatService = kernel.GetRequiredService<IChatCompletionService>();

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
        var result = await chatService.GetChatMessageContentAsync(history);

        // Assert
        result.Content.ShouldNotBeNullOrEmpty();
        result.Content.ToLowerInvariant().ShouldContain("product");
        
        // Log cache stats
        LogCacheStats("Content Classification", handler);
    }

    [Test]
    public async Task CaptureChangeAnalysis()
    {
        // Arrange
        var (kernel, handler) = CreateCachedKernel();
        using var _ = handler;
        var chatService = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage("You analyze changes between content versions and summarize what changed.");
        history.AddUserMessage("""
            Compare these two versions:
            
            BEFORE:
            Product: Widget Pro
            Price: $99.99
            Status: In Stock
            
            AFTER:
            Product: Widget Pro
            Price: $79.99
            Status: Low Stock
            
            Summarize what changed in one sentence.
            """);

        // Act
        var result = await chatService.GetChatMessageContentAsync(history);

        // Assert
        result.Content.ShouldNotBeNullOrEmpty();
        // Should mention price change
        (result.Content.Contains("price") || result.Content.Contains("Price") ||
         result.Content.Contains("99") || result.Content.Contains("79")).ShouldBeTrue();
        
        // Log cache stats
        LogCacheStats("Change Analysis", handler);
    }

    [Test]
    public async Task CaptureIntentExtraction()
    {
        // Arrange
        var (kernel, handler) = CreateCachedKernel();
        using var _ = handler;
        var chatService = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage("You extract user intent from webpage descriptions. Be concise.");
        history.AddUserMessage("""
            The user wants to monitor this page:
            URL: https://example.com/events
            Content: A list of upcoming community events with dates, titles, and locations.
            
            What does the user want to track? Respond in one sentence.
            """);

        // Act
        var result = await chatService.GetChatMessageContentAsync(history);

        // Assert
        result.Content.ShouldNotBeNullOrEmpty();
        (result.Content.Contains("event") || result.Content.Contains("Event")).ShouldBeTrue();
        
        // Log cache stats
        LogCacheStats("Intent Extraction", handler);
    }

    [Test]
    public async Task CaptureSectionIdentification()
    {
        // Arrange
        var (kernel, handler) = CreateCachedKernel();
        using var _ = handler;
        var chatService = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage("You identify HTML sections. Output valid JSON only, no markdown.");
        history.AddUserMessage("""
            Identify the key sections in this HTML:
            <div class="events">
              <div class="event">
                <h3>Summer Festival</h3>
                <span class="date">July 15, 2024</span>
                <span class="location">Central Park</span>
              </div>
              <div class="event">
                <h3>Tech Conference</h3>
                <span class="date">August 20, 2024</span>
                <span class="location">Convention Center</span>
              </div>
            </div>
            
            Return JSON array: [{"name": string, "selector": string, "isTarget": boolean, "description": string}]
            """);

        // Act
        var result = await chatService.GetChatMessageContentAsync(history);

        // Assert
        result.Content.ShouldNotBeNullOrEmpty();
        (result.Content.Contains("events") || result.Content.Contains("event")).ShouldBeTrue();
        
        // Log cache stats
        LogCacheStats("Section Identification", handler);
    }

    [Test]
    public async Task CaptureEventListClassification()
    {
        // Arrange
        var (kernel, handler) = CreateCachedKernel();
        using var _ = handler;
        var chatService = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage("You classify webpage content types.");
        history.AddUserMessage("""
            Classify this webpage content:
            
            ---
            Upcoming Events
            
            Summer Festival - July 15, 2024 - Central Park
            Tech Conference - August 20, 2024 - Convention Center
            Art Exhibition - September 5, 2024 - City Gallery
            ---
            
            Options: product, article, blog, forum, search-results, event-list, other
            Respond with just the page type.
            """);

        // Act
        var result = await chatService.GetChatMessageContentAsync(history);

        // Assert
        result.Content.ShouldNotBeNullOrEmpty();
        (result.Content.Contains("event") || result.Content.Contains("Event") || result.Content.Contains("list")).ShouldBeTrue();
        
        // Log cache stats
        LogCacheStats("Event List Classification", handler);
    }

    [Test]
    public async Task CaptureMultiSelectorGeneration()
    {
        // Arrange
        var (kernel, handler) = CreateCachedKernel();
        using var _ = handler;
        var chatService = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage("You generate CSS selectors. Output valid JSON only, no markdown.");
        history.AddUserMessage("""
            Generate CSS selectors for this product listing HTML:
            <div class="product-list">
              <div class="product">
                <span class="name">Widget Pro</span>
                <span class="price">$99.99</span>
                <span class="stock">In Stock</span>
              </div>
            </div>
            
            Return JSON array: [{"selector": string, "type": "CssSelector", "description": string, "reasoning": string}]
            Include selectors for: product name, price, and stock status.
            """);

        // Act
        var result = await chatService.GetChatMessageContentAsync(history);

        // Assert
        result.Content.ShouldNotBeNullOrEmpty();
        (result.Content.Contains("selector") || result.Content.Contains("product")).ShouldBeTrue();
        
        // Log cache stats
        LogCacheStats("Multi-Selector Generation", handler);
    }

    private void LogCacheStats(string testName, CachingLlmHttpHandler handler)
    {
        Console.WriteLine();
        Console.WriteLine($"[{testName}] Cache Stats: Hits={handler.CacheHits}, Misses={handler.CacheMisses}, Stores={handler.CacheStores}");
        if (handler.CacheHits > 0)
        {
            Console.WriteLine($"  ✓ Response served from cache");
        }
        if (handler.CacheMisses > 0)
        {
            Console.WriteLine($"  → Called real Ollama, response cached for future runs");
        }
    }
}
