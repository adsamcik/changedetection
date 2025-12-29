using ChangeDetection.Tests.Llm.Cache;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Llm;

/// <summary>
/// Tests for the LLM response caching infrastructure.
/// </summary>
[Category("Unit")]
public class LlmResponseCacheTests : TestBase
{
    private string _testDbPath = null!;
    
    [Before(Test)]
    public void SetUp()
    {
        // Use a unique temp file for each test
        _testDbPath = Path.Combine(Path.GetTempPath(), $"llm-cache-test-{Guid.NewGuid():N}.db");
    }

    [After(Test)]
    public void TearDown()
    {
        // Clean up test database
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { /* ignore */ }
        }
    }

    [Test]
    public async Task Store_AndRetrieve_ReturnsCorrectResponse()
    {
        // Arrange
        using var cache = new LlmResponseCache(_testDbPath);
        var requestBody = """{"messages":[{"role":"user","content":"Hello!"}],"model":"test-model"}""";
        var responseBody = """{"choices":[{"message":{"content":"Hi there!"}}]}""";

        // Act
        cache.Store(requestBody, responseBody, model: "test-model");
        var result = cache.TryGet(requestBody);

        // Assert
        result.ShouldNotBeNull();
        result.ResponseBody.ShouldBe(responseBody);
        result.ResponseContent.ShouldBe("Hi there!");
        result.Model.ShouldBe("test-model");
        
        await Task.CompletedTask;
    }

    [Test]
    public async Task TryGet_WithMissingRequest_ReturnsNull()
    {
        // Arrange
        using var cache = new LlmResponseCache(_testDbPath);
        var requestBody = """{"messages":[{"role":"user","content":"Unknown prompt"}],"model":"test"}""";

        // Act
        var result = cache.TryGet(requestBody);

        // Assert
        result.ShouldBeNull();
        
        await Task.CompletedTask;
    }

    [Test]
    public async Task ComputeRequestHash_SameRequest_ReturnsSameHash()
    {
        // Arrange
        var request1 = """{"messages":[{"role":"user","content":"Hello!"}],"model":"test-model","temperature":0.7}""";
        var request2 = """{"messages":[{"role":"user","content":"Hello!"}],"model":"test-model","temperature":0.7}""";

        // Act
        var hash1 = LlmResponseCache.ComputeRequestHash(request1);
        var hash2 = LlmResponseCache.ComputeRequestHash(request2);

        // Assert
        hash1.ShouldBe(hash2);
        hash1.Length.ShouldBe(64); // SHA256 hex
        
        await Task.CompletedTask;
    }

    [Test]
    public async Task ComputeRequestHash_DifferentPrompt_ReturnsDifferentHash()
    {
        // Arrange
        var request1 = """{"messages":[{"role":"user","content":"Hello!"}],"model":"test-model"}""";
        var request2 = """{"messages":[{"role":"user","content":"Goodbye!"}],"model":"test-model"}""";

        // Act
        var hash1 = LlmResponseCache.ComputeRequestHash(request1);
        var hash2 = LlmResponseCache.ComputeRequestHash(request2);

        // Assert
        hash1.ShouldNotBe(hash2);
        
        await Task.CompletedTask;
    }

    [Test]
    public async Task ComputeRequestHash_DifferentModel_ReturnsDifferentHash()
    {
        // Arrange
        var request1 = """{"messages":[{"role":"user","content":"Hello!"}],"model":"model-a"}""";
        var request2 = """{"messages":[{"role":"user","content":"Hello!"}],"model":"model-b"}""";

        // Act
        var hash1 = LlmResponseCache.ComputeRequestHash(request1);
        var hash2 = LlmResponseCache.ComputeRequestHash(request2);

        // Assert
        hash1.ShouldNotBe(hash2);
        
        await Task.CompletedTask;
    }

    [Test]
    public async Task Store_UpdatesExistingEntry_WhenSameRequest()
    {
        // Arrange
        using var cache = new LlmResponseCache(_testDbPath);
        var requestBody = """{"messages":[{"role":"user","content":"Hello!"}],"model":"test"}""";
        var response1 = """{"choices":[{"message":{"content":"Response 1"}}]}""";
        var response2 = """{"choices":[{"message":{"content":"Response 2"}}]}""";

        // Act
        cache.Store(requestBody, response1);
        cache.Store(requestBody, response2);
        var result = cache.TryGet(requestBody);

        // Assert
        result.ShouldNotBeNull();
        result.ResponseContent.ShouldBe("Response 2");
        
        var stats = cache.GetStatistics();
        stats.TotalEntries.ShouldBe(1);
        
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetStatistics_ReturnsCorrectCounts()
    {
        // Arrange
        using var cache = new LlmResponseCache(_testDbPath);
        
        // Store 3 different requests
        for (var i = 0; i < 3; i++)
        {
            var request = "{\"messages\":[{\"role\":\"user\",\"content\":\"Prompt " + i + "\"}],\"model\":\"test\"}";
            var response = "{\"choices\":[{\"message\":{\"content\":\"Response " + i + "\"}}]}";
            cache.Store(request, response);
        }

        // Hit one of them twice
        var hitRequest = """{"messages":[{"role":"user","content":"Prompt 1"}],"model":"test"}""";
        cache.TryGet(hitRequest);
        cache.TryGet(hitRequest);

        // Act
        var stats = cache.GetStatistics();

        // Assert
        stats.TotalEntries.ShouldBe(3);
        stats.TotalHits.ShouldBe(2);
        stats.EntriesWithHits.ShouldBe(1);
        
        await Task.CompletedTask;
    }

    [Test]
    public async Task Clear_RemovesAllEntries()
    {
        // Arrange
        using var cache = new LlmResponseCache(_testDbPath);
        var request = """{"messages":[{"role":"user","content":"Hello!"}],"model":"test"}""";
        var response = """{"choices":[{"message":{"content":"Hi!"}}]}""";
        cache.Store(request, response);

        // Act
        cache.Clear();
        var stats = cache.GetStatistics();

        // Assert
        stats.TotalEntries.ShouldBe(0);
        
        await Task.CompletedTask;
    }

    [Test]
    public async Task ListEntries_ReturnsAllCachedEntries()
    {
        // Arrange
        using var cache = new LlmResponseCache(_testDbPath);
        
        for (var i = 0; i < 3; i++)
        {
            var request = $$$"""{"messages":[{"role":"user","content":"Prompt {{{i}}}"}],"model":"test-model"}""";
            var response = $$$"""{"choices":[{"message":{"content":"Response {{{i}}}"}}]}""";
            cache.Store(request, response, model: "test-model");
        }

        // Act
        var entries = cache.ListEntries().ToList();

        // Assert
        entries.Count.ShouldBe(3);
        entries.ShouldAllBe(e => e.Model == "test-model");
        entries.ShouldAllBe(e => e.RequestHash.Length == 64);
        
        await Task.CompletedTask;
    }

    [Test]
    public async Task PruneOlderThan_RemovesOldEntries()
    {
        // Arrange
        using var cache = new LlmResponseCache(_testDbPath);
        var request = """{"messages":[{"role":"user","content":"Old prompt"}],"model":"test"}""";
        var response = """{"choices":[{"message":{"content":"Old response"}}]}""";
        cache.Store(request, response);

        // Act - prune entries older than 0 time (everything)
        var removed = cache.PruneOlderThan(TimeSpan.Zero);

        // Assert
        removed.ShouldBe(1);
        cache.GetStatistics().TotalEntries.ShouldBe(0);
        
        await Task.CompletedTask;
    }
}
