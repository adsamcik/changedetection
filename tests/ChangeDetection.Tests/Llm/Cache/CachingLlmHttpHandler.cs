using System.Net;
using System.Text;

namespace ChangeDetection.Tests.Llm.Cache;

/// <summary>
/// Operating mode for the caching HTTP handler.
/// </summary>
public enum CacheMode
{
    /// <summary>
    /// Check cache first, call real LLM if not cached, store result.
    /// This is the default mode for development and test capture.
    /// </summary>
    CacheFirst,
    
    /// <summary>
    /// Only use cached responses. Throw if not in cache.
    /// Use this in CI to ensure no real LLM calls are made.
    /// </summary>
    CacheOnly,
    
    /// <summary>
    /// Always call real LLM, update cache with fresh responses.
    /// Use this to refresh outdated cache entries.
    /// </summary>
    RefreshCache,
    
    /// <summary>
    /// Bypass cache completely, always call real LLM.
    /// Use for debugging or when cache is suspected to be stale.
    /// </summary>
    Bypass
}

/// <summary>
/// HTTP handler that caches LLM responses in a SQLite database.
/// 
/// This enables deterministic testing by:
/// 1. First run with CacheFirst mode captures real LLM responses
/// 2. Subsequent runs return cached responses instantly
/// 3. CI runs use CacheOnly to ensure no real LLM calls
/// 
/// The cache is keyed by a hash of the request body (including model and temperature),
/// so identical prompts return identical responses across runs.
/// 
/// Usage:
/// <code>
/// // Development: auto-capture new responses
/// using var handler = new CachingLlmHttpHandler(CacheMode.CacheFirst);
/// 
/// // CI: fail if response not cached
/// using var handler = new CachingLlmHttpHandler(CacheMode.CacheOnly);
/// 
/// var httpClient = new HttpClient(handler);
/// var kernel = Kernel.CreateBuilder()
///     .AddOpenAIChatCompletion("model", "key", endpoint, httpClient)
///     .Build();
/// </code>
/// </summary>
public class CachingLlmHttpHandler : DelegatingHandler
{
    private readonly LlmResponseCache _cache;
    private readonly CacheMode _mode;
    private readonly TextWriter? _output;
    private readonly bool _ownsCache;
    
    private int _cacheHits;
    private int _cacheMisses;
    private int _cacheStores;

    /// <summary>Gets the number of cache hits during this handler's lifetime.</summary>
    public int CacheHits => _cacheHits;
    
    /// <summary>Gets the number of cache misses during this handler's lifetime.</summary>
    public int CacheMisses => _cacheMisses;
    
    /// <summary>Gets the number of new entries stored during this handler's lifetime.</summary>
    public int CacheStores => _cacheStores;

    /// <summary>
    /// Creates a caching handler with the default shared cache.
    /// Uses a singleton cache instance for thread-safe parallel test execution.
    /// </summary>
    /// <param name="mode">The caching mode to use</param>
    /// <param name="output">Optional output for logging cache activity</param>
    /// <param name="innerHandler">Optional inner handler (defaults to HttpClientHandler)</param>
    public CachingLlmHttpHandler(
        CacheMode mode = CacheMode.CacheFirst,
        TextWriter? output = null,
        HttpMessageHandler? innerHandler = null)
    {
        _cache = LlmResponseCache.GetSharedInstance();
        _mode = mode;
        _output = output;
        _ownsCache = false; // Shared instance - don't dispose
        InnerHandler = innerHandler ?? new HttpClientHandler();
    }

    /// <summary>
    /// Creates a caching handler with a specific cache path.
    /// Note: For parallel test execution, prefer the default constructor which uses a shared cache.
    /// </summary>
    /// <param name="cachePath">Path to the SQLite cache database</param>
    /// <param name="mode">The caching mode to use</param>
    /// <param name="output">Optional output for logging cache activity</param>
    /// <param name="innerHandler">Optional inner handler (defaults to HttpClientHandler)</param>
    public CachingLlmHttpHandler(
        string cachePath,
        CacheMode mode = CacheMode.CacheFirst,
        TextWriter? output = null,
        HttpMessageHandler? innerHandler = null)
    {
        _cache = new LlmResponseCache(cachePath);
        _mode = mode;
        _output = output;
        _ownsCache = true;
        InnerHandler = innerHandler ?? new HttpClientHandler();
    }

    /// <summary>
    /// Creates a caching handler with an existing cache instance.
    /// </summary>
    /// <param name="cache">The cache to use (caller retains ownership)</param>
    /// <param name="mode">The caching mode to use</param>
    /// <param name="output">Optional output for logging cache activity</param>
    /// <param name="innerHandler">Optional inner handler (defaults to HttpClientHandler)</param>
    public CachingLlmHttpHandler(
        LlmResponseCache cache,
        CacheMode mode = CacheMode.CacheFirst,
        TextWriter? output = null,
        HttpMessageHandler? innerHandler = null)
    {
        _cache = cache;
        _mode = mode;
        _output = output;
        _ownsCache = false;
        InnerHandler = innerHandler ?? new HttpClientHandler();
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Only cache chat completion requests
        if (!IsCacheableRequest(request))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var requestBody = request.Content != null
            ? await request.Content.ReadAsStringAsync(cancellationToken)
            : "";

        var hash = LlmResponseCache.ComputeRequestHash(requestBody);
        var model = ExtractModel(requestBody);

        // Handle different cache modes
        switch (_mode)
        {
            case CacheMode.Bypass:
                Log($"[Cache BYPASS] {model} hash={hash[..12]}...");
                return await base.SendAsync(request, cancellationToken);

            case CacheMode.CacheOnly:
                var cachedOnly = _cache.TryGet(requestBody);
                if (cachedOnly == null)
                {
                    Interlocked.Increment(ref _cacheMisses);
                    throw new CacheMissException(
                        $"LLM response not in cache (mode=CacheOnly). Hash: {hash}, Model: {model}");
                }
                Interlocked.Increment(ref _cacheHits);
                Log($"[Cache HIT] {model} hash={hash[..12]}... ({cachedOnly.DurationMs}ms original)");
                return CreateResponse(cachedOnly.ResponseBody);

            case CacheMode.RefreshCache:
                Log($"[Cache REFRESH] {model} hash={hash[..12]}...");
                return await FetchAndCacheAsync(request, requestBody, model, hash, cancellationToken);

            case CacheMode.CacheFirst:
            default:
                var cached = _cache.TryGet(requestBody);
                if (cached != null)
                {
                    Interlocked.Increment(ref _cacheHits);
                    Log($"[Cache HIT] {model} hash={hash[..12]}... ({cached.DurationMs}ms original)");
                    return CreateResponse(cached.ResponseBody);
                }
                Interlocked.Increment(ref _cacheMisses);
                Log($"[Cache MISS] {model} hash={hash[..12]}... calling real LLM");
                return await FetchAndCacheAsync(request, requestBody, model, hash, cancellationToken);
        }
    }

    private async Task<HttpResponseMessage> FetchAndCacheAsync(
        HttpRequestMessage request,
        string requestBody,
        string model,
        string hash,
        CancellationToken cancellationToken)
    {
        // Need to recreate request content since it was already read
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var response = await base.SendAsync(request, cancellationToken);
        stopwatch.Stop();

        if (response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            
            _cache.Store(
                requestBody,
                responseBody,
                model: model,
                endpoint: request.RequestUri?.Host,
                durationMs: stopwatch.ElapsedMilliseconds);
            
            Interlocked.Increment(ref _cacheStores);
            Log($"[Cache STORE] {model} hash={hash[..12]}... ({stopwatch.ElapsedMilliseconds}ms)");

            // Recreate response content since we consumed it
            response.Content = new StringContent(responseBody, Encoding.UTF8, "application/json");
        }

        return response;
    }

    private static bool IsCacheableRequest(HttpRequestMessage request)
    {
        // Only cache POST requests to chat completion endpoints
        if (request.Method != HttpMethod.Post)
            return false;

        var path = request.RequestUri?.AbsolutePath ?? "";
        return path.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/v1/completions", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractModel(string requestBody)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(requestBody);
            if (doc.RootElement.TryGetProperty("model", out var model))
            {
                return model.GetString() ?? "unknown";
            }
        }
        catch
        {
            // Ignore parsing errors
        }
        return "unknown";
    }

    private static HttpResponseMessage CreateResponse(string responseBody)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        };
    }

    private void Log(string message)
    {
        _output?.WriteLine(message);
    }

    private static string GetDefaultCachePath()
    {
        // Store cache in the test project's Cache directory
        var testDir = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Llm", "Cache");
        return Path.Combine(testDir, "llm-responses.db");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsCache)
        {
            _cache.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Gets statistics summary for logging.
    /// </summary>
    public string GetStatisticsSummary()
    {
        var stats = _cache.GetStatistics();
        return $"Cache: {stats.TotalEntries} entries, Session: {_cacheHits} hits / {_cacheMisses} misses / {_cacheStores} stores";
    }
}

/// <summary>
/// Exception thrown when CacheOnly mode is used and the response is not in cache.
/// </summary>
public class CacheMissException : InvalidOperationException
{
    public CacheMissException(string message) : base(message) { }
}
