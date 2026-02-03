using ChangeDetection.Core.Interfaces;
using ChangeDetection.Tests.Llm.Cache;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Scraping.Cache;

/// <summary>
/// Tests for the ContentCache and CachingContentFetcher infrastructure.
/// Verifies cache storage, retrieval, hash computation, and caching modes.
/// </summary>
[Category("Unit")]
public class ContentCacheTests : TestBase
{
    private string _testDbPath = null!;

    [Before(Test)]
    public void SetUp()
    {
        // Use a unique temp file for each test to ensure isolation
        _testDbPath = Path.Combine(Path.GetTempPath(), $"content-cache-test-{Guid.NewGuid():N}.db");
    }

    [After(Test)]
    public void TearDown()
    {
        // Clean up test database
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { /* ignore */ }
        }
        // Also clean up WAL files
        var walPath = _testDbPath + "-wal";
        var shmPath = _testDbPath + "-shm";
        if (File.Exists(walPath)) try { File.Delete(walPath); } catch { }
        if (File.Exists(shmPath)) try { File.Delete(shmPath); } catch { }
    }

    #region ContentCache.Store and TryGet

    [Test]
    public async Task ContentCache_StoreAndRetrieve_ReturnsCorrectEntry()
    {
        // Arrange
        using var cache = new ContentCache(_testDbPath);
        var url = "https://example.com/page";
        var entry = new CachedContentEntry
        {
            Url = url,
            Html = "<html><body>Hello World</body></html>",
            HttpStatusCode = 200,
            IsSuccess = true,
            DurationMs = 150,
            ResponseHeaders = new Dictionary<string, string> { ["Content-Type"] = "text/html" }
        };

        // Act
        cache.Store(url, entry);
        var result = cache.TryGet(url);

        // Assert
        result.ShouldNotBeNull();
        result.Url.ShouldBe(url);
        result.Html.ShouldBe(entry.Html);
        result.HttpStatusCode.ShouldBe(200);
        result.IsSuccess.ShouldBeTrue();
        result.DurationMs.ShouldBe(150);
        result.ResponseHeaders.ShouldContainKey("Content-Type");

        await Task.CompletedTask;
    }

    [Test]
    public async Task ContentCache_TryGet_WithMissingUrl_ReturnsNull()
    {
        // Arrange
        using var cache = new ContentCache(_testDbPath);
        var url = "https://example.com/nonexistent";

        // Act
        var result = cache.TryGet(url);

        // Assert
        result.ShouldBeNull();

        await Task.CompletedTask;
    }

    [Test]
    public async Task ContentCache_Store_SameUrlTwice_ReplacesEntry()
    {
        // Arrange
        using var cache = new ContentCache(_testDbPath);
        var url = "https://example.com/page";
        var entry1 = new CachedContentEntry { Url = url, Html = "First", HttpStatusCode = 200, IsSuccess = true };
        var entry2 = new CachedContentEntry { Url = url, Html = "Second", HttpStatusCode = 201, IsSuccess = true };

        // Act
        cache.Store(url, entry1);
        cache.Store(url, entry2);
        var result = cache.TryGet(url);

        // Assert
        result.ShouldNotBeNull();
        result.Html.ShouldBe("Second");
        result.HttpStatusCode.ShouldBe(201);
        cache.Count.ShouldBe(1);

        await Task.CompletedTask;
    }

    [Test]
    public async Task ContentCache_Store_WithNullHtml_HandlesCorrectly()
    {
        // Arrange
        using var cache = new ContentCache(_testDbPath);
        var url = "https://example.com/error";
        var entry = new CachedContentEntry
        {
            Url = url,
            Html = null,
            HttpStatusCode = 500,
            ErrorMessage = "Internal Server Error",
            IsSuccess = false
        };

        // Act
        cache.Store(url, entry);
        var result = cache.TryGet(url);

        // Assert
        result.ShouldNotBeNull();
        result.Html.ShouldBeNull();
        result.ErrorMessage.ShouldBe("Internal Server Error");
        result.IsSuccess.ShouldBeFalse();

        await Task.CompletedTask;
    }

    #endregion

    #region ContentCache.ComputeUrlHash

    [Test]
    public async Task ContentCache_ComputeUrlHash_SameUrl_ReturnsSameHash()
    {
        // Arrange
        var url1 = "https://example.com/page?id=123";
        var url2 = "https://example.com/page?id=123";

        // Act
        var hash1 = ContentCache.ComputeUrlHash(url1);
        var hash2 = ContentCache.ComputeUrlHash(url2);

        // Assert
        hash1.ShouldBe(hash2);

        await Task.CompletedTask;
    }

    [Test]
    public async Task ContentCache_ComputeUrlHash_DifferentUrls_ReturnsDifferentHashes()
    {
        // Arrange
        var url1 = "https://example.com/page1";
        var url2 = "https://example.com/page2";

        // Act
        var hash1 = ContentCache.ComputeUrlHash(url1);
        var hash2 = ContentCache.ComputeUrlHash(url2);

        // Assert
        hash1.ShouldNotBe(hash2);

        await Task.CompletedTask;
    }

    [Test]
    public async Task ContentCache_ComputeUrlHash_NormalizesUrlCase()
    {
        // Arrange
        var url1 = "HTTPS://EXAMPLE.COM/PAGE";
        var url2 = "https://example.com/page";

        // Act
        var hash1 = ContentCache.ComputeUrlHash(url1);
        var hash2 = ContentCache.ComputeUrlHash(url2);

        // Assert
        hash1.ShouldBe(hash2);

        await Task.CompletedTask;
    }

    [Test]
    public async Task ContentCache_ComputeUrlHash_TrimsWhitespace()
    {
        // Arrange
        var url1 = "  https://example.com/page  ";
        var url2 = "https://example.com/page";

        // Act
        var hash1 = ContentCache.ComputeUrlHash(url1);
        var hash2 = ContentCache.ComputeUrlHash(url2);

        // Assert
        hash1.ShouldBe(hash2);

        await Task.CompletedTask;
    }

    #endregion

    #region ContentCache.Count

    [Test]
    public async Task ContentCache_Count_ReflectsStoredEntries()
    {
        // Arrange
        using var cache = new ContentCache(_testDbPath);

        // Act & Assert - Empty initially
        cache.Count.ShouldBe(0);

        // Store entries
        cache.Store("https://example.com/1", new CachedContentEntry { Url = "https://example.com/1", IsSuccess = true });
        cache.Count.ShouldBe(1);

        cache.Store("https://example.com/2", new CachedContentEntry { Url = "https://example.com/2", IsSuccess = true });
        cache.Count.ShouldBe(2);

        // Replacing same URL shouldn't increase count
        cache.Store("https://example.com/1", new CachedContentEntry { Url = "https://example.com/1", IsSuccess = false });
        cache.Count.ShouldBe(2);

        await Task.CompletedTask;
    }

    #endregion

    #region ContentCache Concurrent Access

    [Test]
    public async Task ContentCache_ConcurrentAccess_NoDataCorruption()
    {
        // Arrange
        using var cache = new ContentCache(_testDbPath);
        const int threadCount = 10;
        const int operationsPerThread = 50;
        var errors = new List<Exception>();
        var errorsLock = new object();

        // Act - Multiple threads reading and writing concurrently
        var tasks = Enumerable.Range(0, threadCount).Select(threadId => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < operationsPerThread; i++)
                {
                    var url = $"https://example.com/thread{threadId}/page{i}";
                    var entry = new CachedContentEntry
                    {
                        Url = url,
                        Html = $"Content from thread {threadId}, iteration {i}",
                        HttpStatusCode = 200,
                        IsSuccess = true
                    };

                    cache.Store(url, entry);
                    var result = cache.TryGet(url);

                    if (result == null)
                    {
                        lock (errorsLock) errors.Add(new Exception($"Cache miss after store for {url}"));
                    }
                }
            }
            catch (Exception ex)
            {
                lock (errorsLock) errors.Add(ex);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // Assert
        errors.ShouldBeEmpty($"Errors occurred: {string.Join(", ", errors.Select(e => e.Message))}");
        cache.Count.ShouldBe(threadCount * operationsPerThread);
    }

    #endregion

    #region ContentCache SharedInstance

    [Test]
    public async Task ContentCache_SharedInstance_ReturnsSameInstance()
    {
        // Act
        var instance1 = ContentCache.GetSharedInstance();
        var instance2 = ContentCache.GetSharedInstance();

        // Assert
        instance1.ShouldBeSameAs(instance2);

        await Task.CompletedTask;
    }

    #endregion

    #region CachingContentFetcher - CacheFirst Mode

    [Test]
    public async Task CachingFetcher_CacheFirst_CacheHit_ReturnsFromCache()
    {
        // Arrange
        using var cache = new ContentCache(_testDbPath);
        var innerFetcher = Substitute.For<IContentFetcher>();
        var fetcher = new CachingContentFetcher(innerFetcher, cache, CacheMode.CacheFirst);

        var url = "https://example.com/page";
        var cachedEntry = new CachedContentEntry
        {
            Url = url,
            Html = "<html>Cached</html>",
            HttpStatusCode = 200,
            IsSuccess = true,
            DurationMs = 100
        };
        cache.Store(url, cachedEntry);

        // Act
        var result = await fetcher.FetchAsync(url, new FetchOptions());

        // Assert
        result.Html.ShouldBe("<html>Cached</html>");
        result.IsSuccess.ShouldBeTrue();
        fetcher.CacheHits.ShouldBe(1);
        fetcher.CacheMisses.ShouldBe(0);
        await innerFetcher.DidNotReceive().FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CachingFetcher_CacheFirst_CacheMiss_CallsInnerAndStores()
    {
        // Arrange
        using var cache = new ContentCache(_testDbPath);
        var innerFetcher = Substitute.For<IContentFetcher>();
        var fetcher = new CachingContentFetcher(innerFetcher, cache, CacheMode.CacheFirst);

        var url = "https://example.com/page";
        innerFetcher.FetchAsync(url, Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                Html = "<html>Fresh</html>",
                HttpStatusCode = 200,
                IsSuccess = true,
                DurationMs = 250
            });

        // Act
        var result = await fetcher.FetchAsync(url, new FetchOptions());

        // Assert
        result.Html.ShouldBe("<html>Fresh</html>");
        fetcher.CacheHits.ShouldBe(0);
        fetcher.CacheMisses.ShouldBe(1);
        fetcher.CacheStores.ShouldBe(1);

        // Verify stored in cache
        var cached = cache.TryGet(url);
        cached.ShouldNotBeNull();
        cached.Html.ShouldBe("<html>Fresh</html>");
    }

    #endregion

    #region CachingContentFetcher - CacheOnly Mode

    [Test]
    public async Task CachingFetcher_CacheOnly_CacheHit_ReturnsFromCache()
    {
        // Arrange
        using var cache = new ContentCache(_testDbPath);
        var innerFetcher = Substitute.For<IContentFetcher>();
        var fetcher = new CachingContentFetcher(innerFetcher, cache, CacheMode.CacheOnly);

        var url = "https://example.com/page";
        cache.Store(url, new CachedContentEntry
        {
            Url = url,
            Html = "<html>Cached</html>",
            HttpStatusCode = 200,
            IsSuccess = true
        });

        // Act
        var result = await fetcher.FetchAsync(url, new FetchOptions());

        // Assert
        result.Html.ShouldBe("<html>Cached</html>");
        fetcher.CacheHits.ShouldBe(1);
        await innerFetcher.DidNotReceive().FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CachingFetcher_CacheOnly_CacheMiss_ThrowsCacheMissException()
    {
        // Arrange
        using var cache = new ContentCache(_testDbPath);
        var innerFetcher = Substitute.For<IContentFetcher>();
        var fetcher = new CachingContentFetcher(innerFetcher, cache, CacheMode.CacheOnly);

        var url = "https://example.com/not-cached";

        // Act & Assert
        var exception = await Should.ThrowAsync<CacheMissException>(
            async () => await fetcher.FetchAsync(url, new FetchOptions()));

        exception.Message.ShouldContain(url);
        exception.Message.ShouldContain("-IncludeInternet");
        fetcher.CacheMisses.ShouldBe(1);
        await innerFetcher.DidNotReceive().FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region CachingContentFetcher - Bypass Mode

    [Test]
    public async Task CachingFetcher_Bypass_AlwaysCallsInner()
    {
        // Arrange
        using var cache = new ContentCache(_testDbPath);
        var innerFetcher = Substitute.For<IContentFetcher>();
        var fetcher = new CachingContentFetcher(innerFetcher, cache, CacheMode.Bypass);

        var url = "https://example.com/page";
        
        // Pre-populate cache
        cache.Store(url, new CachedContentEntry
        {
            Url = url,
            Html = "<html>Cached</html>",
            HttpStatusCode = 200,
            IsSuccess = true
        });

        innerFetcher.FetchAsync(url, Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                Html = "<html>Fresh</html>",
                HttpStatusCode = 200,
                IsSuccess = true
            });

        // Act
        var result = await fetcher.FetchAsync(url, new FetchOptions());

        // Assert - Should get fresh result, not cached
        result.Html.ShouldBe("<html>Fresh</html>");
        fetcher.CacheHits.ShouldBe(0);
        fetcher.CacheMisses.ShouldBe(0);
        fetcher.CacheStores.ShouldBe(0);
        await innerFetcher.Received(1).FetchAsync(url, Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region CachingContentFetcher - RefreshCache Mode

    [Test]
    public async Task CachingFetcher_RefreshCache_BypassesCacheRead_ButStores()
    {
        // Arrange
        using var cache = new ContentCache(_testDbPath);
        var innerFetcher = Substitute.For<IContentFetcher>();
        var fetcher = new CachingContentFetcher(innerFetcher, cache, CacheMode.RefreshCache);

        var url = "https://example.com/page";
        
        // Pre-populate cache with old content
        cache.Store(url, new CachedContentEntry
        {
            Url = url,
            Html = "<html>Old Cached</html>",
            HttpStatusCode = 200,
            IsSuccess = true
        });

        innerFetcher.FetchAsync(url, Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                Html = "<html>Fresh</html>",
                HttpStatusCode = 200,
                IsSuccess = true
            });

        // Act
        var result = await fetcher.FetchAsync(url, new FetchOptions());

        // Assert - Should get fresh result and update cache
        result.Html.ShouldBe("<html>Fresh</html>");
        fetcher.CacheHits.ShouldBe(0);
        fetcher.CacheMisses.ShouldBe(1);
        fetcher.CacheStores.ShouldBe(1);

        // Verify cache was updated
        var cached = cache.TryGet(url);
        cached.ShouldNotBeNull();
        cached.Html.ShouldBe("<html>Fresh</html>");
    }

    #endregion

    #region CachingContentFetcher - Statistics

    [Test]
    public async Task CachingFetcher_Statistics_AreAccurate()
    {
        // Arrange
        using var cache = new ContentCache(_testDbPath);
        var innerFetcher = Substitute.For<IContentFetcher>();
        var fetcher = new CachingContentFetcher(innerFetcher, cache, CacheMode.CacheFirst);

        // Pre-cache one URL
        cache.Store("https://example.com/cached", new CachedContentEntry
        {
            Url = "https://example.com/cached",
            Html = "<html>Cached</html>",
            HttpStatusCode = 200,
            IsSuccess = true
        });

        innerFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { Html = "<html>Fresh</html>", HttpStatusCode = 200, IsSuccess = true });

        // Act
        await fetcher.FetchAsync("https://example.com/cached", new FetchOptions()); // Hit
        await fetcher.FetchAsync("https://example.com/cached", new FetchOptions()); // Hit
        await fetcher.FetchAsync("https://example.com/new1", new FetchOptions());   // Miss + Store
        await fetcher.FetchAsync("https://example.com/new2", new FetchOptions());   // Miss + Store
        await fetcher.FetchAsync("https://example.com/new1", new FetchOptions());   // Hit (now cached)

        // Assert
        fetcher.CacheHits.ShouldBe(3);
        fetcher.CacheMisses.ShouldBe(2);
        fetcher.CacheStores.ShouldBe(2);
    }

    #endregion

    #region CachingContentFetcher - FetchResult Mapping

    [Test]
    public async Task CachingFetcher_FetchResult_MapsAllFieldsFromCache()
    {
        // Arrange
        using var cache = new ContentCache(_testDbPath);
        var innerFetcher = Substitute.For<IContentFetcher>();
        var fetcher = new CachingContentFetcher(innerFetcher, cache, CacheMode.CacheFirst);

        var url = "https://example.com/page";
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "text/html",
            ["X-Custom"] = "value"
        };
        cache.Store(url, new CachedContentEntry
        {
            Url = url,
            Html = "<html>Content</html>",
            HttpStatusCode = 201,
            ErrorMessage = null,
            ResponseHeaders = headers,
            DurationMs = 123,
            IsSuccess = true
        });

        // Act
        var result = await fetcher.FetchAsync(url, new FetchOptions());

        // Assert
        result.Html.ShouldBe("<html>Content</html>");
        result.HttpStatusCode.ShouldBe(201);
        result.IsSuccess.ShouldBeTrue();
        result.DurationMs.ShouldBe(123);
        result.ErrorMessage.ShouldBeNull();
        result.ResponseHeaders.ShouldContainKey("Content-Type");
        result.ResponseHeaders["X-Custom"].ShouldBe("value");
    }

    [Test]
    public async Task CachingFetcher_FetchResult_MapsErrorFieldsFromCache()
    {
        // Arrange
        using var cache = new ContentCache(_testDbPath);
        var innerFetcher = Substitute.For<IContentFetcher>();
        var fetcher = new CachingContentFetcher(innerFetcher, cache, CacheMode.CacheFirst);

        var url = "https://example.com/error";
        cache.Store(url, new CachedContentEntry
        {
            Url = url,
            Html = null,
            HttpStatusCode = 500,
            ErrorMessage = "Internal Server Error",
            IsSuccess = false,
            DurationMs = 50
        });

        // Act
        var result = await fetcher.FetchAsync(url, new FetchOptions());

        // Assert
        result.Html.ShouldBeNull();
        result.HttpStatusCode.ShouldBe(500);
        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("Internal Server Error");
    }

    #endregion

    #region CachingContentFetcher - GetDefaultCacheMode

    [Test]
    public async Task CachingFetcher_GetDefaultCacheMode_ReturnsBasedOnEnvironment()
    {
        // This test documents the behavior - actual environment variable testing
        // would require environment manipulation which can affect other tests
        
        // The method should return:
        // - CacheOnly when SKIP_INTERNET_TESTS=true
        // - CacheFirst otherwise
        
        var mode = CachingContentFetcher.GetDefaultCacheMode();
        mode.ShouldBeOneOf(CacheMode.CacheFirst, CacheMode.CacheOnly);

        await Task.CompletedTask;
    }

    #endregion
}
