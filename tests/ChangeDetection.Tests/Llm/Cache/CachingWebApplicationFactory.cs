using ChangeDetection.Core.Interfaces;
using ChangeDetection.Tests.Llm.Cache;
using ChangeDetection.Tests.Scraping.Cache;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ChangeDetection.Tests.Llm.Cache;

/// <summary>
/// Base WebApplicationFactory that configures LLM response caching and content caching.
/// 
/// Use this as a base class for E2E tests that require LLM and/or external URLs to enable:
/// - CacheFirst mode when Ollama/Internet is available (captures new responses)
/// - CacheOnly mode in CI (replays from cache, fails on miss)
/// 
/// The caching is implemented via:
/// - HTTP message handler for LLM requests (/v1/chat/completions endpoints)
/// - CachingContentFetcher decorator for IContentFetcher (external URL fetching)
/// </summary>
/// <example>
/// public class MyTestFactory : CachingWebApplicationFactory
/// {
///     protected override void ConfigureWebHost(IWebHostBuilder builder)
///     {
///         base.ConfigureWebHost(builder);
///         // Add your additional configuration here
///         builder.UseEnvironment("Testing");
///     }
/// }
/// </example>
public class CachingWebApplicationFactory : WebApplicationFactory<Program>
{
    private static readonly string DefaultCachePath = Path.Combine(
        Path.GetDirectoryName(typeof(CachingWebApplicationFactory).Assembly.Location)!,
        "llm-responses.db");
    
    private CachingLlmHttpHandler? _handler;
    
    /// <summary>
    /// Gets the LLM cache mode being used. Determined automatically based on SKIP_OLLAMA_TESTS.
    /// </summary>
    public CacheMode LlmCacheMode => CachedLlmKernelFactory.GetDefaultCacheMode();
    
    /// <summary>
    /// Gets the content cache mode being used. Determined automatically based on SKIP_INTERNET_TESTS.
    /// </summary>
    public CacheMode ContentCacheMode
    {
        get
        {
            var skipInternet = Environment.GetEnvironmentVariable("SKIP_INTERNET_TESTS");
            return skipInternet == "true" ? CacheMode.CacheOnly : CacheMode.CacheFirst;
        }
    }
    
    /// <summary>
    /// Gets the path to the LLM cache database.
    /// </summary>
    public string CachePath { get; init; } = DefaultCachePath;
    
    /// <summary>
    /// Gets the caching handler for inspection (e.g., to check hit/miss statistics).
    /// </summary>
    public CachingLlmHttpHandler? CachingHandler => _handler;
    
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        
        var llmCache = new LlmResponseCache(CachePath);
        _handler = new CachingLlmHttpHandler(llmCache, LlmCacheMode);
        var contentCacheMode = ContentCacheMode;
        
        builder.ConfigureServices(services =>
        {
            // Remove all hosted services to prevent background work from hanging
            // the test process on shutdown. Tests exercise the app through HTTP
            // endpoints, not through background service timers.
            services.RemoveAll<IHostedService>();
            
            // Register the caching handler as the primary handler for LlmProvider HTTP clients
            services.AddHttpClient("LlmProvider")
                .ConfigurePrimaryHttpMessageHandler(() => _handler);
            
            // Wrap IContentFetcher with CachingContentFetcher for external URL caching
            // Use the decorator pattern to wrap the existing registration
            DecorateContentFetcher(services, contentCacheMode);
        });
    }
    
    /// <summary>
    /// Decorates IContentFetcher with CachingContentFetcher without requiring Scrutor.
    /// </summary>
    private static void DecorateContentFetcher(IServiceCollection services, CacheMode cacheMode)
    {
        // Find the existing IContentFetcher registration
        var existingDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IContentFetcher));
        if (existingDescriptor == null)
        {
            return; // No IContentFetcher registered, nothing to decorate
        }
        
        // Remove the existing registration
        services.Remove(existingDescriptor);
        
        // Add a new registration that wraps the original
        services.Add(new ServiceDescriptor(
            typeof(IContentFetcher),
            sp =>
            {
                // Create the original fetcher
                IContentFetcher inner;
                if (existingDescriptor.ImplementationInstance != null)
                {
                    inner = (IContentFetcher)existingDescriptor.ImplementationInstance;
                }
                else if (existingDescriptor.ImplementationFactory != null)
                {
                    inner = (IContentFetcher)existingDescriptor.ImplementationFactory(sp);
                }
                else if (existingDescriptor.ImplementationType != null)
                {
                    inner = (IContentFetcher)ActivatorUtilities.CreateInstance(sp, existingDescriptor.ImplementationType);
                }
                else
                {
                    throw new InvalidOperationException("Cannot resolve IContentFetcher from existing descriptor");
                }
                
                // Wrap it with caching
                return new CachingContentFetcher(inner, cacheMode, Console.Out);
            },
            existingDescriptor.Lifetime));
    }
    
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _handler?.Dispose();
        }
        base.Dispose(disposing);
    }
}
