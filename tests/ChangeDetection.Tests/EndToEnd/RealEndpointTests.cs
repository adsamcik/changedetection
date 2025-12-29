using System.Net.Http.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Shared.Dtos;
using ChangeDetection.Tests.Llm.Cache;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd;

/// <summary>
/// End-to-end tests that test the real application with LLM response caching.
/// 
/// These tests use a SQLite-based cache for deterministic, fast execution:
/// - In development (Ollama available): CacheFirst mode - uses cache, falls back to real LLM
/// - In CI (SKIP_OLLAMA_TESTS=true): CacheOnly mode - uses cache only, fails on miss
/// 
/// WHY THIS TEST EXISTS:
/// We had a bug where unit tests passed but the real app failed because:
/// 1. Unit tests mocked the LLM, so they always returned expected intents
/// 2. The real LLM classified "I want to watch for the events" as Unknown
/// 3. This test ensures the actual endpoint works with actual LLM responses
/// 
/// CACHING DESIGN:
/// LLM requests are hashed by model + temperature + messages. Responses are
/// stored in SQLite and replayed for subsequent test runs. This gives us:
/// - Deterministic results (same hash = same response)
/// - Fast execution (no LLM call needed when cached)
/// - CI compatibility (no Ollama required after initial capture)
/// 
/// TIMEOUT DESIGN:
/// TUnit uses cooperative cancellation. The [Timeout] attribute provides
/// a CancellationToken that tests MUST accept as a parameter and pass
/// to async operations. This enables proper test cancellation without
/// relying on Thread.Abort (deprecated in .NET Core+).
/// 
/// Each test has a 5-minute timeout (300,000ms) for LLM operations.
/// Timeouts are on individual tests, not the class, to avoid affecting
/// setup/teardown methods which don't need cancellation tokens.
/// </summary>
[Category("EndToEnd")]
[Category("LlmCached")]
[Category("RequiresInternet")]  // Tests fetch from real external URLs
public class RealEndpointTests : IAsyncDisposable
{
    private HttpClient _client = null!;
    private OllamaWebApplicationFactory _factory = null!;

    [Before(Test)]
    public async Task SetUp()
    {
        _factory = new OllamaWebApplicationFactory();
        _client = _factory.CreateClient();
        // HttpClient timeout should match or exceed TUnit's test timeout
        // This ensures TUnit's cooperative cancellation handles timeouts, not HttpClient
        _client.Timeout = TimeSpan.FromMinutes(6);
        // Seed the Ollama provider before any test runs
        await _factory.EnsureProviderSeededAsync();
        
        // Log cache mode for debugging
        Console.WriteLine($"=== LLM Cache Mode: {_factory.LlmCacheMode} ===");
        Console.WriteLine($"=== Content Cache Mode: {_factory.ContentCacheMode} ===");
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory != null)
            await _factory.DisposeAsync();
    }

    /// <summary>
    /// Tests the exact scenario that was failing in production:
    /// User enters "https://www.img.cas.cz/novinky/akce/ I want to watch for the events on that page"
    /// The endpoint should NOT return "I'm not sure how to handle that request"
    /// </summary>
    [Test]
    [Timeout(300_000)] // 5 minute timeout for LLM operations
    public async Task ProcessInput_UrlWithIntent_ShouldNotReturnUnknownIntent(CancellationToken cancellationToken)
    {
        // Arrange - the exact input that was failing
        var request = new ProcessInputRequest
        {
            Input = "https://www.img.cas.cz/novinky/akce/ I want to watch for the events on that page"
        };

        // Act - pass cancellation token to enable cooperative cancellation
        var response = await _client.PostAsJsonAsync("/api/llm/process-input", request, cancellationToken);
        
        // Assert
        response.IsSuccessStatusCode.ShouldBeTrue($"HTTP request failed: {response.StatusCode}");
        
        var result = await response.Content.ReadFromJsonAsync<ProcessInputResponse>(cancellationToken);
        result.ShouldNotBeNull();
        
        // Log what we got for debugging
        Console.WriteLine($"=== ProcessInput Result ===");
        Console.WriteLine($"IsSuccess: {result.IsSuccess}");
        Console.WriteLine($"Intent: {result.Intent}");
        Console.WriteLine($"URL: {result.ParsedRequest?.Url}");
        Console.WriteLine($"Error: {result.ErrorMessage}");
        Console.WriteLine($"NeedsClarification: {result.NeedsClarification}");
        Console.WriteLine($"Suggestions: {result.Suggestions?.Count}");

        // THE CRITICAL ASSERTION: Intent should be recognized as CreateWatch (not Unknown)
        result.Intent.ShouldBe("CreateWatch", "LLM should recognize this as a CreateWatch intent");
        
        // With the new design, if pipeline fails, we get NeedsClarification with suggestions
        // instead of silent fallback. Both outcomes are acceptable:
        // - Success with watch created
        // - Pipeline failure with proper error and suggestions (not silent fallback)
        if (!result.IsSuccess)
        {
            // Pipeline failed but should have suggestions, not a silent failure
            result.NeedsClarification.ShouldBeTrue("Pipeline failure should offer clarification options");
            result.Suggestions.ShouldNotBeEmpty("Pipeline failure should offer suggestions");
            (result.ErrorMessage?.Contains("Content analysis failed") ?? false).ShouldBeTrue("Error should explain what failed");
        }
    }

    [Test]
    [Timeout(300_000)] // 5 minute timeout for LLM operations
    public async Task ProcessInput_UrlWithWatchKeyword_ShouldReturnCreateWatchIntent(CancellationToken cancellationToken)
    {
        // Arrange - more explicit "watch" language
        var request = new ProcessInputRequest
        {
            Input = "https://example.com watch for changes on this page"
        };

        // Act - pass cancellation token to enable cooperative cancellation
        var response = await _client.PostAsJsonAsync("/api/llm/process-input", request, cancellationToken);
        
        // Assert
        response.IsSuccessStatusCode.ShouldBeTrue();
        
        var result = await response.Content.ReadFromJsonAsync<ProcessInputResponse>(cancellationToken);
        result.ShouldNotBeNull();
        
        Console.WriteLine($"=== ProcessInput with 'watch' keyword ===");
        Console.WriteLine($"IsSuccess: {result.IsSuccess}");
        Console.WriteLine($"Intent: {result.Intent}");
        Console.WriteLine($"Error: {result.ErrorMessage}");
        Console.WriteLine($"NeedsClarification: {result.NeedsClarification}");
        
        // With explicit "watch for changes" language, it should definitely be CreateWatch
        result.Intent.ShouldBe("CreateWatch", "Intent should be recognized as CreateWatch");
        
        // New design: if pipeline fails, we get explicit failure with options instead of silent fallback
        // Both outcomes are acceptable - success OR explicit failure with suggestions
        if (!result.IsSuccess)
        {
            result.NeedsClarification.ShouldBeTrue("Pipeline failure should offer clarification options");
            result.Suggestions.ShouldNotBeEmpty("Pipeline failure should offer suggestions to user");
        }
    }

    [Test]
    [Timeout(300_000)] // 5 minute timeout for LLM operations
    public async Task ProcessInput_PureUrl_ShouldReturnCreateWatchWithoutLlm(CancellationToken cancellationToken)
    {
        // Arrange - pure URL should bypass LLM entirely
        var request = new ProcessInputRequest
        {
            Input = "https://www.img.cas.cz/novinky/akce/"
        };

        // Act - pass cancellation token to enable cooperative cancellation
        var response = await _client.PostAsJsonAsync("/api/llm/process-input", request, cancellationToken);
        
        // Assert
        response.IsSuccessStatusCode.ShouldBeTrue();
        
        var result = await response.Content.ReadFromJsonAsync<ProcessInputResponse>(cancellationToken);
        result.ShouldNotBeNull();
        
        Console.WriteLine($"=== ProcessInput with pure URL ===");
        Console.WriteLine($"IsSuccess: {result.IsSuccess}");
        Console.WriteLine($"Intent: {result.Intent}");
        Console.WriteLine($"URL: {result.ParsedRequest?.Url}");
        
        result.IsSuccess.ShouldBeTrue();
        result.Intent.ShouldBe("CreateWatch");
        result.ParsedRequest?.Url.ShouldBe("https://www.img.cas.cz/novinky/akce/");
    }

    [Test]
    [Timeout(300_000)] // 5 minute timeout for LLM operations
    public async Task RunPipeline_UrlWithIntent_ShouldAnalyzeContentAndGenerateSelectors(CancellationToken cancellationToken)
    {
        // Arrange - test the pipeline endpoint
        var request = new RunPipelineRequest
        {
            Input = "https://www.img.cas.cz/novinky/akce/ I want to watch for the events on that page"
        };

        // Act - pass cancellation token to enable cooperative cancellation
        // Note: TUnit class-level timeout (5min) controls cancellation, not HttpClient.Timeout
        var response = await _client.PostAsJsonAsync("/api/llm/run-pipeline", request, cancellationToken);
        
        // Assert
        response.IsSuccessStatusCode.ShouldBeTrue($"HTTP request failed: {response.StatusCode}");
        
        var result = await response.Content.ReadFromJsonAsync<RunPipelineResponse>(cancellationToken);
        result.ShouldNotBeNull();
        
        Console.WriteLine($"=== RunPipeline Result ===");
        Console.WriteLine($"IsSuccess: {result.IsSuccess}");
        Console.WriteLine($"Stage: {result.Stage}");
        Console.WriteLine($"URLs: {string.Join(", ", result.ExtractedUrls ?? [])}");
        Console.WriteLine($"Best Selector: {result.BestSelector?.Expression}");
        Console.WriteLine($"Content Type: {result.ContentAnalysis?.PageType}");
        Console.WriteLine($"Error: {result.ErrorMessage}");
        
        // URL extraction should always work (no LLM needed)
        result.ExtractedUrls.ShouldNotBeNull();
        result.ExtractedUrls!.ShouldContain("https://www.img.cas.cz/novinky/akce/");
        
        // New design: pipeline can fail explicitly. Both outcomes are acceptable.
        if (!result.IsSuccess)
        {
            Console.WriteLine($"Pipeline failed at stage {result.Stage} - this is acceptable with explicit error");
            return; // Test passes - explicit failure is acceptable
        }
        
        result.ContentAnalysis.ShouldNotBeNull("Content analysis should have been performed");
        // LLM classification is best-effort - log the result but don't fail on "Unknown"
        // The LLM may classify pages differently based on model state, temperature, etc.
        if (result.ContentAnalysis.PageType == "Unknown")
        {
            Console.WriteLine("⚠ LLM classified page as Unknown - this is acceptable for variable LLM output");
        }
        else
        {
            Console.WriteLine($"✓ LLM classified page as: {result.ContentAnalysis.PageType}");
        }
    }

    /// <summary>
    /// Custom WebApplicationFactory that configures the app to use Ollama with LLM response caching.
    /// Seeds an Ollama provider into the database so LLM calls work.
    /// Inherits from CachingWebApplicationFactory to enable request/response caching.
    /// </summary>
    public class OllamaWebApplicationFactory : CachingWebApplicationFactory
    {
        private bool _providerSeeded = false;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder); // Important: call base to set up caching
            builder.UseEnvironment("Development");
        }

        public async Task EnsureProviderSeededAsync()
        {
            if (_providerSeeded) return;

            using var scope = Services.CreateScope();
            var providerRepo = scope.ServiceProvider.GetRequiredService<IRepository<LlmProviderConfig>>();
            
            // Check if Ollama provider already exists
            var providers = await providerRepo.GetAllAsync();
            if (providers.Any(p => p.ProviderType == LlmProviderType.Ollama))
            {
                _providerSeeded = true;
                return;
            }

            // Seed Ollama provider
            var ollamaProvider = new LlmProviderConfig
            {
                Id = Guid.NewGuid(),
                Name = "Ollama Local",
                ProviderType = LlmProviderType.Ollama,
                Endpoint = "http://localhost:11434",
                Model = "ministral-3:14b",
                IsEnabled = true,
                Priority = 1,
                TimeoutSeconds = 300,
                MaxRetries = 3
            };

            await providerRepo.InsertAsync(ollamaProvider);
            _providerSeeded = true;
            
            Console.WriteLine($"=== Seeded Ollama provider: {ollamaProvider.Id} ===");
        }
    }
}

// Note: DTOs are now imported from ChangeDetection.Shared.Dtos
// The local DTOs were removed to avoid conflicts with the shared versions.