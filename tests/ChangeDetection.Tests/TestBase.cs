using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using TUnit.Core;

namespace ChangeDetection.Tests;

/// <summary>
/// Base class for tests that provides integrated logging infrastructure.
/// Captures all ILogger output and forwards it to TUnit's TestContext output.
/// 
/// Usage:
/// 1. Inherit from TestBase
/// 2. Use CreateLogger&lt;T&gt;() for service loggers instead of mocking
/// 3. Access LogCollector.GetSnapshot() to assert on logged messages
/// </summary>
public abstract class TestBase
{
    /// <summary>
    /// Collects all log entries written via loggers created by this base class.
    /// Use GetSnapshot() to retrieve captured logs for assertions.
    /// </summary>
    protected FakeLogCollector LogCollector { get; }

    protected TestBase()
    {
        LogCollector = FakeLogCollector.Create(new FakeLogCollectorOptions
        {
            OutputSink = message => TestContext.Current?.OutputWriter?.WriteLine(message)
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
    protected void Log(string message) => TestContext.Current?.OutputWriter?.WriteLine(message);

    /// <summary>
    /// Writes a formatted message directly to the test output.
    /// </summary>
    protected void Log(string format, params object[] args) => TestContext.Current?.OutputWriter?.WriteLine(format, args);

    /// <summary>
    /// Creates a mock IDomCompactor that passes through HTML unchanged.
    /// Useful for tests that don't need actual DOM compaction.
    /// </summary>
    protected static IDomCompactor CreatePassThroughDomCompactor()
    {
        var compactor = Substitute.For<IDomCompactor>();
        
        compactor.Compact(Arg.Any<string>(), Arg.Any<DomCompactorOptions?>())
            .Returns(callInfo =>
            {
                var html = callInfo.Arg<string>();
                return new DomCompactionResult
                {
                    Html = html,
                    OriginalSize = html.Length,
                    CompactedSize = html.Length,
                    ElementsRemoved = 0,
                    WrappersCollapsed = 0
                };
            });

        compactor.CompactToTokenBudget(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(callInfo =>
            {
                var html = callInfo.Arg<string>();
                return new DomCompactionResult
                {
                    Html = html,
                    OriginalSize = html.Length,
                    CompactedSize = html.Length,
                    ElementsRemoved = 0,
                    WrappersCollapsed = 0
                };
            });

        return compactor;
    }
}
