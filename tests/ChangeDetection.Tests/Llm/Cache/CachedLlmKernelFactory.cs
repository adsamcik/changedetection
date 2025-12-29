using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChangeDetection.Tests.Llm.Cache;

/// <summary>
/// Factory for creating cached LLM kernels for testing.
/// 
/// Provides a simple API to create Semantic Kernel instances that:
/// - Use cached responses when available
/// - Capture new responses for future runs
/// - Fail fast in CI when cache is missing
/// 
/// Usage in tests:
/// <code>
/// [Test]
/// public async Task MyLlmTest()
/// {
///     // Development: auto-capture new responses
///     var kernel = CachedLlmKernelFactory.CreateKernel(CacheMode.CacheFirst);
///     
///     // Or for CI: fail if not cached
///     var kernel = CachedLlmKernelFactory.CreateKernel(CacheMode.CacheOnly);
///     
///     var chat = kernel.GetRequiredService&lt;IChatCompletionService&gt;();
///     var result = await chat.GetChatMessageContentAsync("Hello!");
/// }
/// </code>
/// </summary>
public static class CachedLlmKernelFactory
{
    /// <summary>
    /// Default Ollama endpoint for local testing.
    /// </summary>
    public const string DefaultOllamaEndpoint = "http://localhost:11434";
    
    /// <summary>
    /// Default model for testing.
    /// </summary>
    public const string DefaultModel = "ministral-3:3b";

    /// <summary>
    /// Determines the cache mode based on environment variables.
    /// - If SKIP_OLLAMA_TESTS=true, use CacheOnly (CI mode)
    /// - Otherwise, use CacheFirst (development mode)
    /// </summary>
    public static CacheMode GetDefaultCacheMode()
    {
        return Environment.GetEnvironmentVariable("SKIP_OLLAMA_TESTS") == "true"
            ? CacheMode.CacheOnly
            : CacheMode.CacheFirst;
    }

    /// <summary>
    /// Creates a Semantic Kernel with cached LLM responses.
    /// </summary>
    /// <param name="mode">The caching mode (default: auto-detect from environment)</param>
    /// <param name="model">The model to use (default: ministral-3:3b)</param>
    /// <param name="endpoint">The LLM endpoint (default: localhost:11434)</param>
    /// <param name="output">Optional output for logging cache activity</param>
    /// <returns>A tuple of (Kernel, CachingLlmHttpHandler) - caller owns the handler</returns>
    public static (Kernel Kernel, CachingLlmHttpHandler Handler) CreateKernel(
        CacheMode? mode = null,
        string? model = null,
        string? endpoint = null,
        TextWriter? output = null)
    {
        var effectiveMode = mode ?? GetDefaultCacheMode();
        var effectiveModel = model ?? DefaultModel;
        var effectiveEndpoint = endpoint ?? DefaultOllamaEndpoint;

        var handler = new CachingLlmHttpHandler(effectiveMode, output);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(effectiveEndpoint),
            Timeout = TimeSpan.FromMinutes(5)
        };

        var kernel = Kernel.CreateBuilder()
            .AddOpenAIChatCompletion(
                modelId: effectiveModel,
                apiKey: "ollama",
                endpoint: new Uri($"{effectiveEndpoint}/v1"),
                httpClient: httpClient)
            .Build();

        return (kernel, handler);
    }

    /// <summary>
    /// Creates a cached kernel specifically for CI environments.
    /// Throws CacheMissException if a response is not in cache.
    /// </summary>
    public static (Kernel Kernel, CachingLlmHttpHandler Handler) CreateCiKernel(
        string? model = null,
        TextWriter? output = null)
    {
        return CreateKernel(
            mode: CacheMode.CacheOnly,
            model: model,
            output: output);
    }

    /// <summary>
    /// Creates a cached kernel for development.
    /// Automatically captures new responses to the cache.
    /// </summary>
    public static (Kernel Kernel, CachingLlmHttpHandler Handler) CreateDevKernel(
        string? model = null,
        TextWriter? output = null)
    {
        return CreateKernel(
            mode: CacheMode.CacheFirst,
            model: model,
            output: output);
    }

    /// <summary>
    /// Refreshes the cache for specific prompts by forcing real LLM calls.
    /// </summary>
    public static (Kernel Kernel, CachingLlmHttpHandler Handler) CreateRefreshKernel(
        string? model = null,
        TextWriter? output = null)
    {
        return CreateKernel(
            mode: CacheMode.RefreshCache,
            model: model,
            output: output);
    }

    /// <summary>
    /// Checks if Ollama is available for capturing new responses.
    /// </summary>
    public static async Task<bool> IsOllamaAvailableAsync(string? endpoint = null)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await client.GetAsync($"{endpoint ?? DefaultOllamaEndpoint}/api/tags");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Extension methods for working with cached LLM tests.
/// </summary>
public static class CachedLlmTestExtensions
{
    /// <summary>
    /// Logs cache statistics to the test output.
    /// Call this in test cleanup to see cache usage.
    /// </summary>
    public static void LogCacheStatistics(this CachingLlmHttpHandler handler, TextWriter output)
    {
        output.WriteLine(handler.GetStatisticsSummary());
    }

    /// <summary>
    /// Asserts that all LLM calls were served from cache.
    /// Useful in CI to verify no real LLM calls were made.
    /// </summary>
    public static void AssertAllFromCache(this CachingLlmHttpHandler handler)
    {
        if (handler.CacheMisses > 0)
        {
            throw new InvalidOperationException(
                $"Expected all LLM calls to be cached, but had {handler.CacheMisses} cache misses. " +
                $"Run tests locally with Ollama to populate the cache.");
        }
    }
}
