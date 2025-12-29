using ChangeDetection.Core.Interfaces;
using ChangeDetection.Tests.Llm.Cache;

namespace ChangeDetection.Tests.Scraping.Cache;

/// <summary>
/// Content fetcher that caches fetch results in a SQLite database.
/// 
/// This enables deterministic testing by:
/// 1. First run with CacheFirst mode captures real HTTP responses
/// 2. Subsequent runs return cached responses instantly
/// 3. CI runs use CacheOnly to ensure no real HTTP calls
/// 
/// The cache is keyed by URL, so identical URLs return identical responses.
/// 
/// Usage:
/// <code>
/// // Development: auto-capture new responses
/// var fetcher = new CachingContentFetcher(realFetcher, CacheMode.CacheFirst);
/// 
/// // CI: fail if response not cached  
/// var fetcher = new CachingContentFetcher(realFetcher, CacheMode.CacheOnly);
/// </code>
/// </summary>
public class CachingContentFetcher : IContentFetcher
{
    private readonly IContentFetcher _inner;
    private readonly ContentCache _cache;
    private readonly CacheMode _mode;
    private readonly TextWriter? _output;
    
    private int _cacheHits;
    private int _cacheMisses;
    private int _cacheStores;

    /// <summary>Gets the number of cache hits during this fetcher's lifetime.</summary>
    public int CacheHits => _cacheHits;
    
    /// <summary>Gets the number of cache misses during this fetcher's lifetime.</summary>
    public int CacheMisses => _cacheMisses;
    
    /// <summary>Gets the number of new entries stored during this fetcher's lifetime.</summary>
    public int CacheStores => _cacheStores;

    /// <summary>
    /// Creates a caching fetcher with the default shared cache.
    /// Uses a singleton cache instance for thread-safe parallel test execution.
    /// </summary>
    /// <param name="inner">The real content fetcher to wrap</param>
    /// <param name="mode">The caching mode to use</param>
    /// <param name="output">Optional output for logging cache activity</param>
    public CachingContentFetcher(
        IContentFetcher inner,
        CacheMode mode = CacheMode.CacheFirst,
        TextWriter? output = null)
    {
        _inner = inner;
        _cache = ContentCache.GetSharedInstance();
        _mode = mode;
        _output = output;
    }

    /// <summary>
    /// Creates a caching fetcher with a specific cache instance.
    /// </summary>
    /// <param name="inner">The real content fetcher to wrap</param>
    /// <param name="cache">The cache to use</param>
    /// <param name="mode">The caching mode to use</param>
    /// <param name="output">Optional output for logging cache activity</param>
    public CachingContentFetcher(
        IContentFetcher inner,
        ContentCache cache,
        CacheMode mode = CacheMode.CacheFirst,
        TextWriter? output = null)
    {
        _inner = inner;
        _cache = cache;
        _mode = mode;
        _output = output;
    }

    /// <summary>
    /// Gets the default cache mode based on environment variables.
    /// Returns CacheOnly if SKIP_INTERNET_TESTS is set, CacheFirst otherwise.
    /// </summary>
    public static CacheMode GetDefaultCacheMode()
    {
        var skipInternet = Environment.GetEnvironmentVariable("SKIP_INTERNET_TESTS");
        return skipInternet == "true" ? CacheMode.CacheOnly : CacheMode.CacheFirst;
    }

    public async Task<FetchResult> FetchAsync(string url, FetchOptions options, CancellationToken ct = default)
    {
        // In Bypass mode, always fetch from source
        if (_mode == CacheMode.Bypass)
        {
            return await _inner.FetchAsync(url, options, ct);
        }

        // Check cache first (except in RefreshCache mode)
        if (_mode != CacheMode.RefreshCache)
        {
            var cached = _cache.TryGet(url);
            if (cached != null)
            {
                Interlocked.Increment(ref _cacheHits);
                _output?.WriteLine($"[ContentCache] HIT: {url}");
                
                return new FetchResult
                {
                    IsSuccess = cached.IsSuccess,
                    Html = cached.Html,
                    HttpStatusCode = cached.HttpStatusCode,
                    ErrorMessage = cached.ErrorMessage,
                    DurationMs = cached.DurationMs,
                    ResponseHeaders = cached.ResponseHeaders
                };
            }
        }

        // Cache miss
        Interlocked.Increment(ref _cacheMisses);
        _output?.WriteLine($"[ContentCache] MISS: {url}");

        // In CacheOnly mode, fail on cache miss
        if (_mode == CacheMode.CacheOnly)
        {
            throw new CacheMissException(
                $"Content cache miss for URL: {url}. " +
                "Run with -IncludeInternet to populate the cache.");
        }

        // Fetch from source
        var result = await _inner.FetchAsync(url, options, ct);

        // Store in cache
        var entry = new CachedContentEntry
        {
            Url = url,
            Html = result.Html,
            HttpStatusCode = result.HttpStatusCode,
            ErrorMessage = result.ErrorMessage,
            ResponseHeaders = result.ResponseHeaders,
            DurationMs = result.DurationMs,
            IsSuccess = result.IsSuccess
        };
        
        _cache.Store(url, entry);
        Interlocked.Increment(ref _cacheStores);
        _output?.WriteLine($"[ContentCache] STORED: {url}");

        return result;
    }
}

/// <summary>
/// Exception thrown when cache is in CacheOnly mode and a URL is not found in cache.
/// </summary>
public class CacheMissException : InvalidOperationException
{
    public CacheMissException(string message) : base(message) { }
}
