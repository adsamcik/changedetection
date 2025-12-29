using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.LLM;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Llm;

public class LlmLogServiceTests
{
    private readonly LlmLogService _sut;

    public LlmLogServiceTests()
    {
        _sut = new LlmLogService(maxEntries: 10);
    }

    [Test]
    public async Task Log_AddsEntryToLogs()
    {
        // Arrange
        var entry = new LlmLogEntry
        {
            Level = LlmLogLevel.Info,
            ProviderName = "TestProvider",
            Category = LlmLogCategory.Request,
            Message = "Test message"
        };

        // Act
        _sut.Log(entry);

        // Assert
        var logs = _sut.GetRecentLogs();
        logs.ShouldContain(entry);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Log_RaisesOnLogAddedEvent()
    {
        // Arrange
        LlmLogEntry? receivedEntry = null;
        _sut.OnLogAdded += e => receivedEntry = e;
        
        var entry = new LlmLogEntry
        {
            Level = LlmLogLevel.Info,
            ProviderName = "TestProvider",
            Category = LlmLogCategory.Request,
            Message = "Test message"
        };

        // Act
        _sut.Log(entry);

        // Assert
        receivedEntry.ShouldBe(entry);
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetRecentLogs_ReturnsLogsOrderedByTimestampDescending()
    {
        // Arrange
        var entry1 = new LlmLogEntry
        {
            Level = LlmLogLevel.Info,
            ProviderName = "Provider",
            Category = LlmLogCategory.Request,
            Message = "First",
            Timestamp = DateTime.UtcNow.AddMinutes(-2)
        };
        var entry2 = new LlmLogEntry
        {
            Level = LlmLogLevel.Info,
            ProviderName = "Provider",
            Category = LlmLogCategory.Response,
            Message = "Second",
            Timestamp = DateTime.UtcNow.AddMinutes(-1)
        };
        var entry3 = new LlmLogEntry
        {
            Level = LlmLogLevel.Info,
            ProviderName = "Provider",
            Category = LlmLogCategory.Response,
            Message = "Third",
            Timestamp = DateTime.UtcNow
        };

        _sut.Log(entry1);
        _sut.Log(entry2);
        _sut.Log(entry3);

        // Act
        var logs = _sut.GetRecentLogs();

        // Assert
        logs[0].Message.ShouldBe("Third");
        logs[1].Message.ShouldBe("Second");
        logs[2].Message.ShouldBe("First");
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetRecentLogs_RespectsCountLimit()
    {
        // Arrange
        for (int i = 0; i < 5; i++)
        {
            _sut.Log(new LlmLogEntry
            {
                Level = LlmLogLevel.Info,
                ProviderName = "Provider",
                Category = LlmLogCategory.Request,
                Message = $"Entry {i}"
            });
        }

        // Act
        var logs = _sut.GetRecentLogs(count: 2);

        // Assert
        logs.Count.ShouldBe(2);
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetLogsForProvider_FiltersCorrectly()
    {
        // Arrange
        _sut.Log(new LlmLogEntry
        {
            Level = LlmLogLevel.Info,
            ProviderName = "ProviderA",
            Category = LlmLogCategory.Request,
            Message = "From A"
        });
        _sut.Log(new LlmLogEntry
        {
            Level = LlmLogLevel.Info,
            ProviderName = "ProviderB",
            Category = LlmLogCategory.Request,
            Message = "From B"
        });

        // Act
        var logsA = _sut.GetLogsForProvider("ProviderA");
        var logsB = _sut.GetLogsForProvider("ProviderB");

        // Assert
        logsA.Count.ShouldBe(1);
        logsA[0].Message.ShouldBe("From A");
        logsB.Count.ShouldBe(1);
        logsB[0].Message.ShouldBe("From B");
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetLogsForProvider_IsCaseInsensitive()
    {
        // Arrange
        _sut.Log(new LlmLogEntry
        {
            Level = LlmLogLevel.Info,
            ProviderName = "OpenAI",
            Category = LlmLogCategory.Request,
            Message = "Test"
        });

        // Act
        var logs = _sut.GetLogsForProvider("openai");

        // Assert
        logs.Count.ShouldBe(1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Clear_RemovesAllEntries()
    {
        // Arrange
        _sut.Log(new LlmLogEntry
        {
            Level = LlmLogLevel.Info,
            ProviderName = "Provider",
            Category = LlmLogCategory.Request,
            Message = "Test"
        });

        // Act
        _sut.Clear();

        // Assert
        _sut.GetRecentLogs().ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Log_PrunesOldEntriesWhenOverLimit()
    {
        // Arrange - service has maxEntries: 10
        for (int i = 0; i < 15; i++)
        {
            _sut.Log(new LlmLogEntry
            {
                Level = LlmLogLevel.Info,
                ProviderName = "Provider",
                Category = LlmLogCategory.Request,
                Message = $"Entry {i}"
            });
        }

        // Act
        var logs = _sut.GetRecentLogs(100);

        // Assert - should be pruned to around maxEntries
        logs.Count.ShouldBeLessThanOrEqualTo(12); // Allows for 1.2x buffer
        await Task.CompletedTask;
    }
}

public class LlmLogServiceExtensionsTests
{
    private readonly LlmLogService _service;

    public LlmLogServiceExtensionsTests()
    {
        _service = new LlmLogService();
    }

    [Test]
    public async Task LogRequest_StoresFullPrompt()
    {
        // Arrange
        var prompt = "This is the full prompt that should be stored completely";

        // Act
        _service.LogRequest("TestProvider", "gpt-4", prompt);

        // Assert
        var logs = _service.GetRecentLogs();
        logs.ShouldHaveSingleItem();
        logs[0].FullPrompt.ShouldBe(prompt);
        logs[0].PromptPreview.ShouldBe(prompt); // Short prompt, preview equals full
    }

    [Test]
    public async Task LogRequest_TruncatesPromptPreviewForLongPrompts()
    {
        // Arrange
        var longPrompt = new string('x', 1000); // 1000 chars

        // Act
        _service.LogRequest("TestProvider", "gpt-4", longPrompt);

        // Assert
        var logs = _service.GetRecentLogs();
        logs[0].FullPrompt.ShouldBe(longPrompt);
        logs[0].FullPrompt!.Length.ShouldBe(1000);
        logs[0].PromptPreview!.Length.ShouldBeLessThan(1000);
        logs[0].PromptPreview!.ShouldContain("... (");
    }

    [Test]
    public async Task LogRequest_SetsCorrectProperties()
    {
        // Arrange
        var metadata = new Dictionary<string, string> { ["key"] = "value" };

        // Act
        _service.LogRequest("OpenAI", "gpt-4o", "Test prompt", metadata);

        // Assert
        var log = _service.GetRecentLogs().Single();
        log.Level.ShouldBe(LlmLogLevel.Debug);
        log.ProviderName.ShouldBe("OpenAI");
        log.Model.ShouldBe("gpt-4o");
        log.Category.ShouldBe(LlmLogCategory.Request);
        log.Message.ShouldContain("OpenAI");
        log.Metadata.ShouldBe(metadata);
    }

    [Test]
    public async Task LogResponse_StoresFullResponse()
    {
        // Arrange
        var response = "This is the full response that should be stored completely";

        // Act
        _service.LogResponse("TestProvider", "gpt-4", response, 150, 100, 50);

        // Assert
        var logs = _service.GetRecentLogs();
        logs.ShouldHaveSingleItem();
        logs[0].FullResponse.ShouldBe(response);
        logs[0].ResponsePreview.ShouldBe(response); // Short response, preview equals full
    }

    [Test]
    public async Task LogResponse_TruncatesResponsePreviewForLongResponses()
    {
        // Arrange
        var longResponse = new string('y', 1000); // 1000 chars

        // Act
        _service.LogResponse("TestProvider", "gpt-4", longResponse, 150, 100, 50);

        // Assert
        var logs = _service.GetRecentLogs();
        logs[0].FullResponse.ShouldBe(longResponse);
        logs[0].FullResponse!.Length.ShouldBe(1000);
        logs[0].ResponsePreview!.Length.ShouldBeLessThan(1000);
        logs[0].ResponsePreview!.ShouldContain("... (");
    }

    [Test]
    public async Task LogResponse_SetsCorrectProperties()
    {
        // Act
        _service.LogResponse("Anthropic", "claude-3", "Response", 250, 150, 75);

        // Assert
        var log = _service.GetRecentLogs().Single();
        log.Level.ShouldBe(LlmLogLevel.Info);
        log.ProviderName.ShouldBe("Anthropic");
        log.Model.ShouldBe("claude-3");
        log.Category.ShouldBe(LlmLogCategory.Response);
        log.DurationMs.ShouldBe(250);
        log.InputTokens.ShouldBe(150);
        log.OutputTokens.ShouldBe(75);
        log.IsSuccess.ShouldBe(true);
    }

    [Test]
    public async Task LogError_StoresFullPrompt()
    {
        // Arrange
        var prompt = "This is the prompt that caused the error";
        var exception = new InvalidOperationException("Test error");

        // Act
        _service.LogError("TestProvider", "gpt-4", exception, prompt);

        // Assert
        var logs = _service.GetRecentLogs();
        logs.ShouldHaveSingleItem();
        logs[0].FullPrompt.ShouldBe(prompt);
    }

    [Test]
    public async Task LogError_TruncatesPromptPreviewForLongPrompts()
    {
        // Arrange
        var longPrompt = new string('z', 1000);
        var exception = new InvalidOperationException("Test error");

        // Act
        _service.LogError("TestProvider", "gpt-4", exception, longPrompt);

        // Assert
        var logs = _service.GetRecentLogs();
        logs[0].FullPrompt.ShouldBe(longPrompt);
        logs[0].PromptPreview!.Length.ShouldBeLessThan(1000);
    }

    [Test]
    public async Task LogError_HandlesNullPrompt()
    {
        // Arrange
        var exception = new InvalidOperationException("Test error");

        // Act
        _service.LogError("TestProvider", "gpt-4", exception, prompt: null);

        // Assert
        var logs = _service.GetRecentLogs();
        logs[0].FullPrompt.ShouldBeNull();
        logs[0].PromptPreview.ShouldBeNull();
    }

    [Test]
    public async Task LogError_SetsCorrectProperties()
    {
        // Arrange
        var exception = new ArgumentException("Invalid argument");

        // Act
        _service.LogError("Ollama", "llama2", exception, "Prompt");

        // Assert
        var log = _service.GetRecentLogs().Single();
        log.Level.ShouldBe(LlmLogLevel.Error);
        log.ProviderName.ShouldBe("Ollama");
        log.Model.ShouldBe("llama2");
        log.Category.ShouldBe(LlmLogCategory.Error);
        log.ErrorMessage.ShouldBe("Invalid argument");
        log.ExceptionType.ShouldBe("ArgumentException");
        log.IsSuccess.ShouldBe(false);
    }

    [Test]
    public async Task LogRetry_SetsCorrectProperties()
    {
        // Act
        _service.LogRetry("OpenAI", "gpt-4", 2, "Timeout");

        // Assert
        var log = _service.GetRecentLogs().Single();
        log.Level.ShouldBe(LlmLogLevel.Warning);
        log.Category.ShouldBe(LlmLogCategory.Retry);
        log.Message.ShouldContain("attempt 2");
        log.Message.ShouldContain("Timeout");
        log.Metadata!["attemptNumber"].ShouldBe("2");
    }

    [Test]
    public async Task LogCircuitBreaker_Open_SetsWarningLevel()
    {
        // Act
        _service.LogCircuitBreaker("TestProvider", isOpen: true, "Too many failures");

        // Assert
        var log = _service.GetRecentLogs().Single();
        log.Level.ShouldBe(LlmLogLevel.Warning);
        log.Category.ShouldBe(LlmLogCategory.CircuitBreaker);
        log.Message.ShouldContain("OPENED");
        log.Metadata!["circuitState"].ShouldBe("OPENED");
    }

    [Test]
    public async Task LogCircuitBreaker_Closed_SetsInfoLevel()
    {
        // Act
        _service.LogCircuitBreaker("TestProvider", isOpen: false);

        // Assert
        var log = _service.GetRecentLogs().Single();
        log.Level.ShouldBe(LlmLogLevel.Info);
        log.Message.ShouldContain("CLOSED");
        log.Metadata!["circuitState"].ShouldBe("CLOSED");
    }

    [Test]
    public async Task LogFallback_SetsCorrectProperties()
    {
        // Act
        _service.LogFallback("OpenAI", "Anthropic", "Rate limited");

        // Assert
        var log = _service.GetRecentLogs().Single();
        log.Level.ShouldBe(LlmLogLevel.Warning);
        log.Category.ShouldBe(LlmLogCategory.Fallback);
        log.Message.ShouldContain("OpenAI");
        log.Message.ShouldContain("Anthropic");
        log.Metadata!["fromProvider"].ShouldBe("OpenAI");
        log.Metadata!["toProvider"].ShouldBe("Anthropic");
    }

    [Test]
    public async Task LogConnectionError_SetsCorrectProperties()
    {
        // Arrange
        var exception = new HttpRequestException("Connection refused");

        // Act
        _service.LogConnectionError("Ollama", "llama2", "Failed to connect", exception);

        // Assert
        var log = _service.GetRecentLogs().Single();
        log.Level.ShouldBe(LlmLogLevel.Error);
        log.Category.ShouldBe(LlmLogCategory.Connection);
        log.Message.ShouldBe("Failed to connect");
        log.ErrorMessage.ShouldBe("Connection refused");
        log.ExceptionType.ShouldBe("HttpRequestException");
        log.IsSuccess.ShouldBe(false);
    }

    [Test]
    public async Task PreviewTruncation_ExactlyAt500Chars_NoTruncation()
    {
        // Arrange
        var prompt = new string('a', 500);

        // Act
        _service.LogRequest("TestProvider", "model", prompt);

        // Assert
        var log = _service.GetRecentLogs().Single();
        log.PromptPreview.ShouldBe(prompt);
        log.PromptPreview!.ShouldNotContain("...");
    }

    [Test]
    public async Task PreviewTruncation_At501Chars_Truncates()
    {
        // Arrange
        var prompt = new string('a', 501);

        // Act
        _service.LogRequest("TestProvider", "model", prompt);

        // Assert
        var log = _service.GetRecentLogs().Single();
        log.PromptPreview.ShouldNotBeNull();
        log.PromptPreview.ShouldContain("...");
        // Original content is truncated to 500 chars, then suffix added
        log.PromptPreview.ShouldStartWith(new string('a', 500));
    }

    [Test]
    public async Task PreviewTruncation_EmptyString_ReturnsEmpty()
    {
        // Act
        _service.LogRequest("TestProvider", "model", "");

        // Assert
        var log = _service.GetRecentLogs().Single();
        log.PromptPreview.ShouldBe(string.Empty);
        log.FullPrompt.ShouldBe(string.Empty);
    }
}
