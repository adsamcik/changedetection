using ChangeDetection.Tests.Llm.Cache;

namespace ChangeDetection.Tests.Infrastructure;

/// <summary>
/// Utility methods for skipping tests when LLM or content caches are unavailable.
/// Uses TUnit's Skip.Test() API to convert cache-miss failures into skipped tests.
/// </summary>
public static class CacheSkipHelper
{
    /// <summary>
    /// Skips the test if SKIP_OLLAMA_TESTS=true (RequiresOllama tests).
    /// Call from [Before(Test)] hooks.
    /// </summary>
    public static void SkipIfOllamaUnavailable()
    {
        if (Environment.GetEnvironmentVariable("SKIP_OLLAMA_TESTS") == "true")
            Skip.Test("Ollama not available (SKIP_OLLAMA_TESTS=true). Run with -IncludeOllama to populate cache.");
    }

    /// <summary>
    /// Skips the test if SKIP_INTERNET_TESTS=true (RequiresInternet tests).
    /// Call from [Before(Test)] hooks.
    /// </summary>
    public static void SkipIfInternetUnavailable()
    {
        if (Environment.GetEnvironmentVariable("SKIP_INTERNET_TESTS") == "true")
            Skip.Test("Internet not available (SKIP_INTERNET_TESTS=true). Run with -IncludeInternet to populate cache.");
    }

    /// <summary>
    /// Converts a CacheMissException to a test skip.
    /// Call from catch blocks: catch (CacheMissException ex) { CacheSkipHelper.SkipOnCacheMiss(ex); }
    /// </summary>
    public static void SkipOnCacheMiss(CacheMissException ex)
    {
        Skip.Test($"LLM cache miss: {ex.Message}. Run with -IncludeOllama to populate cache.");
    }

    /// <summary>
    /// Checks if we're in CacheOnly mode for LLM requests.
    /// </summary>
    public static bool IsLlmCacheOnly =>
        CachedLlmKernelFactory.GetDefaultCacheMode() == CacheMode.CacheOnly;

    /// <summary>
    /// Checks if we're in CacheOnly mode for content fetching.
    /// </summary>
    public static bool IsContentCacheOnly =>
        Environment.GetEnvironmentVariable("SKIP_INTERNET_TESTS") == "true";
}
