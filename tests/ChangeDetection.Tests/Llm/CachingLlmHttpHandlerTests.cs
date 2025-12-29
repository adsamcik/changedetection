using System.Net;
using System.Text;
using ChangeDetection.Tests.Llm.Cache;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Llm;

/// <summary>
/// Tests for the CachingLlmHttpHandler.
/// </summary>
[Category("Unit")]
public class CachingLlmHttpHandlerTests : TestBase
{
    private string _testDbPath = null!;
    
    [Before(Test)]
    public void SetUp()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"llm-cache-handler-test-{Guid.NewGuid():N}.db");
    }

    [After(Test)]
    public void TearDown()
    {
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { /* ignore */ }
        }
    }

    [Test]
    public async Task CacheFirst_CacheHit_ReturnsFromCache()
    {
        // Arrange
        using var cache = new LlmResponseCache(_testDbPath);
        var requestBody = """{"messages":[{"role":"user","content":"Hello!"}],"model":"test-model"}""";
        var responseBody = """{"choices":[{"message":{"role":"assistant","content":"Cached response!"}}]}""";
        cache.Store(requestBody, responseBody);

        var output = new StringWriter();
        using var handler = new CachingLlmHttpHandler(
            cache, 
            CacheMode.CacheFirst, 
            output,
            new FakeHttpHandler("""{"choices":[{"message":{"content":"Should not be called"}}]}"""));

        using var client = new HttpClient(handler);

        // Act
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/v1/chat/completions")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("Cached response!");
        
        handler.CacheHits.ShouldBe(1);
        handler.CacheMisses.ShouldBe(0);
        
        output.ToString().ShouldContain("[Cache HIT]");
    }

    [Test]
    public async Task CacheFirst_CacheMiss_CallsRealLlmAndStores()
    {
        // Arrange
        var requestBody = """{"messages":[{"role":"user","content":"New prompt"}],"model":"test-model"}""";
        var realResponse = """{"choices":[{"message":{"role":"assistant","content":"Real LLM response!"}}]}""";
        
        var output = new StringWriter();
        using var handler = new CachingLlmHttpHandler(
            _testDbPath,
            CacheMode.CacheFirst,
            output,
            new FakeHttpHandler(realResponse));

        using var client = new HttpClient(handler);

        // Act
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/v1/chat/completions")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("Real LLM response!");
        
        handler.CacheHits.ShouldBe(0);
        handler.CacheMisses.ShouldBe(1);
        handler.CacheStores.ShouldBe(1);
        
        output.ToString().ShouldContain("[Cache MISS]");
        output.ToString().ShouldContain("[Cache STORE]");
    }

    [Test]
    public async Task CacheOnly_CacheMiss_ThrowsCacheMissException()
    {
        // Arrange
        var requestBody = """{"messages":[{"role":"user","content":"Unknown prompt"}],"model":"test-model"}""";
        
        using var handler = new CachingLlmHttpHandler(
            _testDbPath,
            CacheMode.CacheOnly,
            innerHandler: new FakeHttpHandler("should not be called"));

        using var client = new HttpClient(handler);

        // Act & Assert
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/v1/chat/completions")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };

        await Should.ThrowAsync<CacheMissException>(async () => 
            await client.SendAsync(request));
        
        handler.CacheMisses.ShouldBe(1);
    }

    [Test]
    public async Task CacheOnly_CacheHit_ReturnsFromCache()
    {
        // Arrange
        using var cache = new LlmResponseCache(_testDbPath);
        var requestBody = """{"messages":[{"role":"user","content":"Known prompt"}],"model":"test-model"}""";
        var cachedResponse = """{"choices":[{"message":{"content":"Cached!"}}]}""";
        cache.Store(requestBody, cachedResponse);

        using var handler = new CachingLlmHttpHandler(
            cache,
            CacheMode.CacheOnly,
            innerHandler: new FakeHttpHandler("should not be called"));

        using var client = new HttpClient(handler);

        // Act
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/v1/chat/completions")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("Cached!");
        handler.CacheHits.ShouldBe(1);
    }

    [Test]
    public async Task RefreshCache_AlwaysCallsRealLlm()
    {
        // Arrange
        using var cache = new LlmResponseCache(_testDbPath);
        var requestBody = """{"messages":[{"role":"user","content":"Refresh me"}],"model":"test-model"}""";
        var oldResponse = """{"choices":[{"message":{"content":"Old cached"}}]}""";
        var newResponse = """{"choices":[{"message":{"content":"Fresh response!"}}]}""";
        cache.Store(requestBody, oldResponse);

        using var handler = new CachingLlmHttpHandler(
            cache,
            CacheMode.RefreshCache,
            innerHandler: new FakeHttpHandler(newResponse));

        using var client = new HttpClient(handler);

        // Act
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/v1/chat/completions")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("Fresh response!");
        
        // Cache should be updated
        var cached = cache.TryGet(requestBody);
        cached.ShouldNotBeNull();
        cached.ResponseContent.ShouldBe("Fresh response!");
    }

    [Test]
    public async Task Bypass_NeverUsesCache()
    {
        // Arrange
        using var cache = new LlmResponseCache(_testDbPath);
        var requestBody = """{"messages":[{"role":"user","content":"Bypass test"}],"model":"test-model"}""";
        var cachedResponse = """{"choices":[{"message":{"content":"Should be ignored"}}]}""";
        var realResponse = """{"choices":[{"message":{"content":"Direct from LLM"}}]}""";
        cache.Store(requestBody, cachedResponse);

        using var handler = new CachingLlmHttpHandler(
            cache,
            CacheMode.Bypass,
            innerHandler: new FakeHttpHandler(realResponse));

        using var client = new HttpClient(handler);

        // Act
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/v1/chat/completions")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        var response = await client.SendAsync(request);

        // Assert
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("Direct from LLM");
        
        // Stats should remain at 0
        handler.CacheHits.ShouldBe(0);
        handler.CacheMisses.ShouldBe(0);
        handler.CacheStores.ShouldBe(0);
    }

    [Test]
    public async Task NonCacheableRequest_PassesThrough()
    {
        // Arrange - GET request to models endpoint
        using var handler = new CachingLlmHttpHandler(
            _testDbPath,
            CacheMode.CacheFirst,
            innerHandler: new FakeHttpHandler("""{"models":[]}"""));

        using var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("http://localhost/v1/models");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        handler.CacheHits.ShouldBe(0);
        handler.CacheMisses.ShouldBe(0);
    }

    [Test]
    public async Task GetStatisticsSummary_ReturnsFormattedString()
    {
        // Arrange
        using var cache = new LlmResponseCache(_testDbPath);
        var requestBody = """{"messages":[{"role":"user","content":"Stats test"}],"model":"test"}""";
        var response = """{"choices":[{"message":{"content":"Response"}}]}""";
        cache.Store(requestBody, response);

        using var handler = new CachingLlmHttpHandler(cache, CacheMode.CacheFirst);
        using var client = new HttpClient(handler);

        // Make a hit
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/v1/chat/completions")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        await client.SendAsync(request);

        // Act
        var summary = handler.GetStatisticsSummary();

        // Assert
        summary.ShouldContain("entries");
        summary.ShouldContain("hits");
        summary.ShouldContain("misses");
        summary.ShouldContain("stores");
    }

    /// <summary>
    /// Fake HTTP handler that returns a pre-configured response.
    /// </summary>
    private class FakeHttpHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, 
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
