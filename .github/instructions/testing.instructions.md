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

```powershell
# All tests with output capture
dotnet test --logger "trx;LogFileName=results.trx" --logger "console;verbosity=detailed" --results-directory ./TestResults 2>&1 | Tee-Object -FilePath test-output.log

# Specific test
dotnet test --filter "FullyQualifiedName~TestClassName" --logger "console;verbosity=detailed" 2>&1 | Tee-Object -FilePath test-output.log

# By category
dotnet test --filter "Category=EndToEnd" --logger "trx;LogFileName=results.trx" --results-directory ./TestResults
```

## Test Output Files

| File | Content |
|------|---------|
| `test-output.log` | Full console output with stack traces |
| `TestResults/results.trx` | Structured XML results |
