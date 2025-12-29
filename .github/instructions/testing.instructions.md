---
applyTo: "tests/**/*.cs"
---
# Testing Instructions

## Framework & Assertions

Use **TUnit** with **Shouldly** assertions. Tests can optionally inherit from `TestBase` for integrated logging.

## TestBase Usage

```csharp
public class MyServiceTests : TestBase
{
    [Test]
    public async Task MyTest()
    {
        var logger = CreateLogger<MyService>();
        var sut = new MyService(logger);
        
        Log("Starting test scenario...");
        sut.DoWork();
        
        var logs = LogCollector.GetSnapshot();
        logs.ShouldContain(r => r.Message.Contains("expected log"));
        await Task.CompletedTask;
    }
}
```

## Test Categories

| Category | Description |
|----------|-------------|
| `Unit` | Fast, isolated unit tests |
| `Integration` | Tests with real service interactions |
| `EndToEnd` | Full pipeline tests with caching |
| `LlmCached` | Uses SQLite-cached LLM responses (deterministic, fast) |
| `RequiresOllama` | Needs live Ollama server (for cache population only) |
| `RequiresInternet` | Needs external network (for cache population only) |

Apply categories with: `[Category("Unit")]`

### Cache-Based Test Execution

Tests run in two modes based on environment:

| Mode | When | LLM Behavior | Content Fetch Behavior |
|------|------|--------------|------------------------|
| **CacheFirst** | `-IncludeOllama`/`-IncludeInternet` flags | Cache hit → cached, miss → call Ollama | Cache hit → cached, miss → fetch URL |
| **CacheOnly** | Default (no flags) | Cache hit → cached, miss → **fail** | Cache hit → cached, miss → **fail** |

**All tests run from cache by default.** No Ollama or internet required for normal test runs.

### LlmCached vs RequiresOllama

**`LlmCached`** tests use the SQLite caching layer:
- Run fast with cached responses (milliseconds)
- Deterministic results from cached LLM output
- In CI (`SKIP_OLLAMA_TESTS=true`): CacheOnly mode, throws on cache miss
- In development: CacheFirst mode, calls Ollama on miss and caches result

**`RequiresOllama`** tests need a live LLM server:
- Only `CaptureOllamaTrafficTests` should use this category
- Used to capture NEW responses for the cache
- Run with `-IncludeOllama` flag to populate cache

## Running Tests

> **⚠️ MANDATORY: Always use `./test.ps1` - NEVER call `dotnet run` directly!**
>
> The `test.ps1` script ensures all test output is captured to `test-output.log`.
> This is critical for debugging failed tests and reviewing test behavior.

```powershell
# All tests (uses cached LLM and content responses)
./test.ps1

# Populate caches when Ollama and internet are available
./test.ps1 -IncludeOllama -IncludeInternet

# Filter tests using TUnit treenode filter syntax
# Format: Assembly/Namespace/Class/Test[Properties]
./test.ps1 -Filter "*/*/FlowStateEntryTests/*"              # All tests in class
./test.ps1 -Filter "/*/*/*/*[Category=Unit]"                 # All Unit tests  
./test.ps1 -Filter "*/*/*/WatchServiceTests/CreateWatch*"    # Tests starting with CreateWatch

# Skip build (after recent build)
./test.ps1 -NoBuild

# Show more/less console output (default: 50 lines)
./test.ps1 -TailLines 100
./test.ps1 -TailLines 0  # Show all

# Control parallelism
./test.ps1 -MaxParallel 4
```

### Script Parameters

| Parameter | Description | Default |
|-----------|-------------|---------|
| `-Filter` | TUnit treenode filter expression | (all tests) |
| `-NoBuild` | Skip building before tests | `$false` |
| `-Project` | Test project path | `tests/ChangeDetection.Tests` |
| `-TailLines` | Lines to show in console | `50` |
| `-MaxParallel` | Max parallel tests | `8` |
| `-IncludeOllama` | Use CacheFirst mode for LLM (populate cache) | `$false` |
| `-IncludeInternet` | Use CacheFirst mode for content (populate cache) | `$false` |

## Test Output Files

| File | Content |
|------|---------|
| `test-output.log` | Full console output with stack traces (ALWAYS generated) |
| `TestResults/*.trx` | Structured XML results |

## ❌ Forbidden Patterns

**NEVER** run tests like this - logs will be lost:
```powershell
# ❌ WRONG - No logging!
dotnet run --project tests/ChangeDetection.Tests

# ❌ WRONG - Partial output only!  
dotnet run ... | Select-Object -Last 20
```

## Debugging Test Failures

When tests fail:
1. **Check `test-output.log`** - Contains full stack traces and diagnostic output
2. **Review `TestResults/*.trx`** - Structured results for CI parsing
3. **Use `Log()` method** - Add diagnostic logging in TestBase-derived tests
4. **Check `LogCollector.GetSnapshot()`** - Verify expected log messages

## Test Writing Best Practices

### Naming Convention
```csharp
[Test]
public async Task MethodName_Scenario_ExpectedBehavior()
{
    // Arrange, Act, Assert
    await Task.CompletedTask;
}

[Test]
[Arguments("input1", "expected1")]
[Arguments("input2", "expected2")]
public async Task MethodName_WithVariousInputs_ReturnsExpected(string input, string expected)
{
    await Task.CompletedTask;
}
```

### Async Tests
```csharp
[Test]
public async Task AsyncMethod_Scenario_ExpectedBehavior()
{
    // Use proper async/await
    var result = await sut.DoWorkAsync();
    result.ShouldNotBeNull();
}
```

### Mocking with NSubstitute
```csharp
var mockRepo = Substitute.For<IRepository<WatchedSite>>();
mockRepo.GetByIdAsync(Arg.Any<Guid>()).Returns(expectedSite);
```

## LLM Response Mocking

For deterministic testing without a live LLM server, use the mocking infrastructure in `tests/ChangeDetection.Tests/Llm/`.

### Overview

| Component | Purpose |
|-----------|---------|
| `MockLlmHttpHandler` | Returns canned responses for LLM HTTP requests |
| `LoggingHttpHandler` | Captures real Ollama traffic for fixture creation |
| `LlmFixtureManager` | Persists/loads response fixtures from JSON files |
| `CaptureOllamaTrafficTests` | Tests that capture real responses |

### Using MockLlmHttpHandler

```csharp
[Test]
public async Task TestWithMockedLlm()
{
    // Create handler with a default response
    using var handler = new MockLlmHttpHandler()
        .WithDefaultResponse("Hello! How can I help?");
    
    // Or queue specific responses
    handler.QueueResponse("First response");
    handler.QueueResponse("Second response");
    
    // Build kernel with the mock handler
    var httpClient = new HttpClient(handler);
    var kernel = Kernel.CreateBuilder()
        .AddOpenAIChatCompletion("model", "key", new Uri("http://fake/v1"), httpClient)
        .Build();
    
    // Test your LLM-dependent code
    var chat = kernel.GetRequiredService<IChatCompletionService>();
    var result = await chat.GetChatMessageContentAsync("Hello");
    result.Content.ShouldBe("Hello! How can I help?");
    
    // Verify captured requests
    handler.CapturedRequests.Count.ShouldBe(1);
}
```

### SQLite-Based LLM Response Caching

For tests that require real LLM behavior but need to be deterministic and fast,
use the SQLite-based caching infrastructure in `tests/ChangeDetection.Tests/Llm/Cache/`.

| Component | Purpose |
|-----------|---------|
| `LlmResponseCache` | SQLite database for request/response pairs |
| `CachingLlmHttpHandler` | HTTP handler that checks cache first |
| `CachedLlmKernelFactory` | Factory for creating cached Semantic Kernel |
| `CacheMode` | Controls caching behavior (CacheFirst, CacheOnly, etc.) |

#### Cache Modes

| Mode | Behavior | Use Case |
|------|----------|----------|
| `CacheFirst` | Check cache, call LLM on miss, store result | Development |
| `CacheOnly` | Only use cache, throw on miss | CI/CD |
| `RefreshCache` | Always call LLM, update cache | Refresh stale entries |
| `Bypass` | Skip cache completely | Debugging |

#### Using Cached Kernels

```csharp
using ChangeDetection.Tests.Llm.Cache;

[Test]
public async Task TestWithCachedLlm()
{
    // Auto-detect mode from SKIP_OLLAMA_TESTS environment variable
    var (kernel, handler) = CachedLlmKernelFactory.CreateKernel();
    
    try
    {
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddUserMessage("Hello!");
        
        // First run: calls real LLM, caches response
        // Subsequent runs: returns cached response instantly
        var response = await chat.GetChatMessageContentAsync(history);
        response.Content.ShouldNotBeNullOrEmpty();
        
        // Check cache statistics
        TestContext.Current?.OutputWriter?.WriteLine(handler.GetStatisticsSummary());
    }
    finally
    {
        handler.Dispose();
    }
}
```

#### Workflow for Cached Tests

1. **Development (with Ollama)**: Run tests normally
   ```powershell
   ./test.ps1 -Filter "*/*/MyCachedTests/*" -IncludeOllama
   ```
   - Real LLM calls made, responses cached in SQLite
   
2. **Subsequent runs (without Ollama)**: Tests use cache
   ```powershell
   ./test.ps1 -Filter "*/*/MyCachedTests/*"
   ```
   - Cached responses returned instantly
   - No Ollama required

3. **CI/CD**: Tests fail if cache missing
   - `SKIP_OLLAMA_TESTS=true` triggers `CacheOnly` mode
   - Missing cache entries cause `CacheMissException`

#### Cache Location

Cache database: `tests/ChangeDetection.Tests/Llm/Cache/llm-responses.db`

> **Note**: Commit the cache database to source control to share cached
> responses across the team and CI. Use `.gitattributes` to mark as binary.

### Content Fetching Cache (External URLs)

For tests that fetch external URLs (e.g., E2E pipeline tests), use the content
caching infrastructure in `tests/ChangeDetection.Tests/Scraping/Cache/`.

| Component | Purpose |
|-----------|---------|
| `ContentCache` | SQLite database for URL→FetchResult pairs |
| `CachingContentFetcher` | Decorator for `IContentFetcher` |
| `CachingWebApplicationFactory` | Base factory that enables both LLM and content caching |

#### How It Works

`CachingWebApplicationFactory` automatically wraps `IContentFetcher` with `CachingContentFetcher`:

```csharp
// Your test factory just extends CachingWebApplicationFactory
public class MyTestFactory : CachingWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder); // Important: enables caching
        // Your additional configuration...
    }
}
```

#### Cache Modes

Controlled by `SKIP_INTERNET_TESTS` environment variable:
- **CacheFirst** (default with `-IncludeInternet`): Fetch real URL on cache miss, store result
- **CacheOnly** (default without flag): Return cached content, fail on miss

#### Content Cache Location

Cache database: `tests/ChangeDetection.Tests/Scraping/Cache/content-cache.db`

#### Populating the Cache

Run tests with both flags when Ollama and internet are available:

```powershell
# Populate all caches (run when you have Ollama + internet)
./test.ps1 -IncludeOllama -IncludeInternet

# Then normal runs use cache only
./test.ps1
```

### Combined Caching Architecture

The test infrastructure provides two caching layers:

```
┌─────────────────────────────────────────────────────────────────┐
│                    CachingWebApplicationFactory                  │
├─────────────────────────────────────────────────────────────────┤
│  ┌─────────────────────────────┐  ┌───────────────────────────┐ │
│  │   CachingLlmHttpHandler     │  │  CachingContentFetcher    │ │
│  │   ─────────────────────     │  │  ─────────────────────    │ │
│  │   Intercepts LLM API calls  │  │  Wraps IContentFetcher    │ │
│  │   Cache: llm-responses.db   │  │  Cache: content-cache.db  │ │
│  │   Mode: SKIP_OLLAMA_TESTS   │  │  Mode: SKIP_INTERNET_TESTS│ │
│  └─────────────────────────────┘  └───────────────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
```

#### Environment Variables

| Variable | Effect |
|----------|--------|
| `SKIP_OLLAMA_TESTS=true` | LLM cache: CacheOnly (fail on miss) |
| `SKIP_INTERNET_TESTS=true` | Content cache: CacheOnly (fail on miss) |

Both are set automatically by `test.ps1` unless `-IncludeOllama`/`-IncludeInternet` flags are used.

### Capturing Real Responses (Legacy)

Run capture tests with Ollama to generate fixture files:

```powershell
# Run all capture tests (requires Ollama on localhost:11434)
./test.ps1 -Filter "/*/*/*/*CaptureOllamaTrafficTests*"

# Run a specific capture test
./test.ps1 -Filter "/*/*/*/*CaptureSimpleGreeting*"
```

Captured responses are automatically saved to `tests/ChangeDetection.Tests/Llm/Fixtures/Responses/`.

### Using Fixture Files (Legacy)

```csharp
using ChangeDetection.Tests.Llm.Fixtures;

[Test]
public async Task TestWithFixture()
{
    // Load a saved fixture
    var response = LlmFixtureManager.GetFixtureResponse("price-extraction");
    
    using var handler = new MockLlmHttpHandler()
        .WithDefaultResponse(response);
    
    // ... test code using the mock
}

[Test]
public void ListAvailableFixtures()
{
    var fixtures = LlmFixtureManager.GetAvailableFixtures();
    foreach (var name in fixtures)
        Console.WriteLine($"Available: {name}");
}
```

### Creating New Fixtures

1. Add a new capture test in `CaptureOllamaTrafficTests.cs`
2. Run with Ollama: `./test.ps1 -Filter "/*/*/*/*YourCaptureTest*"`
3. Fixture is saved automatically via `LlmFixtureManager.SaveFixture()`
4. Use fixture in unit tests with `LlmFixtureManager.GetFixtureResponse()`

### Fixture File Structure

```json
{
  "name": "price-extraction",
  "description": "Price extraction from product HTML",
  "capturedAt": "2024-01-15T10:30:00Z",
  "request": {
    "method": "POST",
    "uri": "http://localhost:11434/v1/chat/completions",
    "body": "{ ... }"
  },
  "response": {
    "statusCode": 200,
    "body": "{ ... raw response ... }",
    "content": "The extracted price is $29.99"
  },
  "durationMs": 1234
}
```

### Key Benefits

- **Deterministic**: Same response every time, no LLM variance
- **Fast**: Tests run in milliseconds instead of seconds
- **Offline**: No Ollama server required for CI/CD
- **Real data**: Fixtures captured from actual LLM responses
- **Cross-process safe**: SQLite with WAL mode handles concurrent access

