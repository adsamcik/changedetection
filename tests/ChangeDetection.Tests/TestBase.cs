using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit.Abstractions;

namespace ChangeDetection.Tests;

/// <summary>
/// Base class for tests that provides integrated logging infrastructure.
/// Captures all ILogger output and forwards it to xUnit's ITestOutputHelper.
/// 
/// Usage:
/// 1. Inherit from TestBase and call base(output) in constructor
/// 2. Use CreateLogger&lt;T&gt;() for service loggers instead of mocking
/// 3. Access LogCollector.GetSnapshot() to assert on logged messages
/// </summary>
public abstract class TestBase
{
    /// <summary>
    /// xUnit's test output helper for writing to test output.
    /// </summary>
    protected ITestOutputHelper Output { get; }

    /// <summary>
    /// Collects all log entries written via loggers created by this base class.
    /// Use GetSnapshot() to retrieve captured logs for assertions.
    /// </summary>
    protected FakeLogCollector LogCollector { get; }

    protected TestBase(ITestOutputHelper output)
    {
        Output = output;
        LogCollector = FakeLogCollector.Create(new FakeLogCollectorOptions
        {
            OutputSink = output.WriteLine
        });
    }

    /// <summary>
    /// Creates a fake logger for the specified type that captures all log entries
    /// and writes them to the test output.
    /// </summary>
    protected FakeLogger<T> CreateLogger<T>() => new(LogCollector);

    /// <summary>
    /// Creates a fake logger with the specified category name that captures all log entries
    /// and writes them to the test output.
    /// </summary>
    protected FakeLogger CreateLogger(string category) => new(LogCollector, category);

    /// <summary>
    /// Writes a message directly to the test output.
    /// </summary>
    protected void Log(string message) => Output.WriteLine(message);

    /// <summary>
    /// Writes a formatted message directly to the test output.
    /// </summary>
    protected void Log(string format, params object[] args) => Output.WriteLine(format, args);
}
