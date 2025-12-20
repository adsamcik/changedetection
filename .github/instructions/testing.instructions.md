---
applyTo: "tests/**/*.cs"
---
# Testing Instructions

## Framework & Assertions

Use **xUnit** with **Shouldly** assertions. All test classes inherit from `TestBase` for integrated logging.

## TestBase Usage

```csharp
public class MyServiceTests : TestBase
{
    public MyServiceTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void MyTest()
    {
        var logger = CreateLogger<MyService>();
        var sut = new MyService(logger);
        
        Log("Starting test scenario...");
        sut.DoWork();
        
        var logs = LogCollector.GetSnapshot();
        logs.ShouldContain(r => r.Message.Contains("expected log"));
    }
}
```

## Test Categories (Traits)

| Trait | Description |
|-------|-------------|
| `Category=Unit` | Fast, isolated unit tests |
| `Category=Integration` | Tests with real service interactions |
| `Category=EndToEnd` | Full pipeline tests |
| `Category=RequiresOllama` | Needs Ollama running locally |
| `Category=RequiresInternet` | Needs external network access |

## Running Tests

> **⚠️ MANDATORY: Always use `./test.ps1` - NEVER call `dotnet test` directly!**
>
> The `test.ps1` script ensures all test output is captured to `test-output.log`.
> This is critical for debugging failed tests and reviewing test behavior.

```powershell
# All tests
./test.ps1

# Specific test by name
./test.ps1 -Filter "FullyQualifiedName~TestClassName"

# By category
./test.ps1 -Filter "Category=EndToEnd"

# Skip build (after recent build)
./test.ps1 -Filter "FullyQualifiedName~MyTest" -NoBuild

# Show more/less console output (default: 50 lines)
./test.ps1 -TailLines 100
./test.ps1 -TailLines 0  # Show all
```

### Script Parameters

| Parameter | Description | Default |
|-----------|-------------|---------|
| `-Filter` | xUnit filter expression | (all tests) |
| `-NoBuild` | Skip building before tests | `$false` |
| `-Project` | Test project path | `tests/ChangeDetection.Tests` |
| `-TailLines` | Lines to show in console | `50` |

## Test Output Files

| File | Content |
|------|---------|
| `test-output.log` | Full console output with stack traces (ALWAYS generated) |
| `TestResults/results.trx` | Structured XML results |

## ❌ Forbidden Patterns

**NEVER** run tests like this - logs will be lost:
```powershell
# ❌ WRONG - No logging!
dotnet test --filter "..."

# ❌ WRONG - Partial output only!  
dotnet test ... | Select-Object -Last 20
```

## Debugging Test Failures

When tests fail:
1. **Check `test-output.log`** - Contains full stack traces and diagnostic output
2. **Review `TestResults/results.trx`** - Structured results for CI parsing
3. **Use `Log()` method** - Add diagnostic logging in TestBase-derived tests
4. **Check `LogCollector.GetSnapshot()`** - Verify expected log messages

## Test Writing Best Practices

### Naming Convention
```csharp
[Fact]
public void MethodName_Scenario_ExpectedBehavior()
{
    // Arrange, Act, Assert
}

[Theory]
[InlineData("input1", "expected1")]
[InlineData("input2", "expected2")]
public void MethodName_WithVariousInputs_ReturnsExpected(string input, string expected)
{
}
```

### Async Tests
```csharp
[Fact]
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
