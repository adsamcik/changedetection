namespace ChangeDetection.Tests.Llm.Cache;

/// <summary>
/// Simple IHttpClientFactory implementation for tests that returns HttpClients
/// configured with the caching LLM handler.
/// 
/// Usage:
/// <code>
/// var factory = new CachingHttpClientFactory(CacheMode.CacheFirst);
/// var llmChain = new LlmProviderChain(providerRepo, usageRepo, logger, serviceProvider, llmLogService, factory);
/// </code>
/// </summary>
public class CachingHttpClientFactory : IHttpClientFactory, IDisposable
{
    private readonly CachingLlmHttpHandler _handler;
    private bool _disposed;

    /// <summary>
    /// Creates a factory with the default cache location.
    /// </summary>
    /// <param name="mode">The caching mode to use</param>
    /// <param name="output">Optional output for logging cache activity</param>
    public CachingHttpClientFactory(CacheMode mode = CacheMode.CacheFirst, TextWriter? output = null)
    {
        _handler = new CachingLlmHttpHandler(mode, output);
    }

    /// <summary>
    /// Creates a factory with an existing cache.
    /// </summary>
    /// <param name="cache">The cache to use</param>
    /// <param name="mode">The caching mode</param>
    /// <param name="output">Optional output for logging</param>
    public CachingHttpClientFactory(LlmResponseCache cache, CacheMode mode = CacheMode.CacheFirst, TextWriter? output = null)
    {
        _handler = new CachingLlmHttpHandler(cache, mode, output);
    }

    /// <summary>
    /// Gets the underlying caching handler for inspection.
    /// </summary>
    public CachingLlmHttpHandler Handler => _handler;

    /// <inheritdoc />
    public HttpClient CreateClient(string name)
    {
        // Return a new HttpClient that delegates to our caching handler
        // The handler is shared across clients but that's fine for testing
        return new HttpClient(_handler, disposeHandler: false);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _handler.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
