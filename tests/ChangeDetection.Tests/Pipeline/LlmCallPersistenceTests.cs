using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Endpoints;
using ChangeDetection.Services.LLM;
using ChangeDetection.Services.Persistence;
using ChangeDetection.Services.Pipeline;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline;

/// <summary>
/// Tests for LLM call persistence: PipelineExecutionContext, LlmLogService pipeline run filtering,
/// and PipelineEventService prompt/response text storage.
/// </summary>
[Category("Unit")]
public class LlmCallPersistenceTests : TestBase
{
    #region PipelineExecutionContext Tests

    [Test]
    public async Task PipelineExecutionContext_SetsAndGetsRunId()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var originalValue = PipelineExecutionContext.CurrentPipelineRunId;

        try
        {
            // Act
            PipelineExecutionContext.CurrentPipelineRunId = runId;

            // Assert
            PipelineExecutionContext.CurrentPipelineRunId.ShouldBe(runId);
        }
        finally
        {
            PipelineExecutionContext.CurrentPipelineRunId = originalValue;
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task PipelineExecutionContext_NullByDefault()
    {
        // AsyncLocal values are null in a fresh async context
        Guid? value = null;
        var task = Task.Run(() => value = PipelineExecutionContext.CurrentPipelineRunId);
        await task;

        value.ShouldBeNull();
    }

    [Test]
    public async Task PipelineExecutionContext_ClearsOnReset()
    {
        // Arrange
        var originalValue = PipelineExecutionContext.CurrentPipelineRunId;

        try
        {
            PipelineExecutionContext.CurrentPipelineRunId = Guid.NewGuid();
            PipelineExecutionContext.CurrentPipelineRunId.ShouldNotBeNull();

            // Act
            PipelineExecutionContext.CurrentPipelineRunId = null;

            // Assert
            PipelineExecutionContext.CurrentPipelineRunId.ShouldBeNull();
        }
        finally
        {
            PipelineExecutionContext.CurrentPipelineRunId = originalValue;
        }

        await Task.CompletedTask;
    }

    #endregion

    #region LlmLogService Pipeline Run Filtering Tests

    [Test]
    public async Task LlmLogService_GetLogsForPipelineRun_ReturnsMatchingEntries()
    {
        // Arrange
        var service = new LlmLogService();
        var runId1 = Guid.NewGuid();
        var runId2 = Guid.NewGuid();

        service.Log(new LlmLogEntry
        {
            Level = LlmLogLevel.Info,
            ProviderName = "Ollama",
            Category = LlmLogCategory.Request,
            Message = "Request for run 1",
            PipelineRunId = runId1,
        });
        service.Log(new LlmLogEntry
        {
            Level = LlmLogLevel.Info,
            ProviderName = "Ollama",
            Category = LlmLogCategory.Response,
            Message = "Response for run 2",
            PipelineRunId = runId2,
        });
        service.Log(new LlmLogEntry
        {
            Level = LlmLogLevel.Info,
            ProviderName = "Ollama",
            Category = LlmLogCategory.Response,
            Message = "Another entry for run 1",
            PipelineRunId = runId1,
        });

        // Act
        var logsForRun1 = service.GetLogsForPipelineRun(runId1);
        var logsForRun2 = service.GetLogsForPipelineRun(runId2);

        // Assert
        logsForRun1.Count.ShouldBe(2);
        logsForRun1.ShouldAllBe(e => e.PipelineRunId == runId1);

        logsForRun2.Count.ShouldBe(1);
        logsForRun2[0].Message.ShouldBe("Response for run 2");

        await Task.CompletedTask;
    }

    [Test]
    public async Task LlmLogService_GetLogsForPipelineRun_ReturnsEmptyWhenNoMatch()
    {
        // Arrange
        var service = new LlmLogService();
        var existingRunId = Guid.NewGuid();
        var nonExistentRunId = Guid.NewGuid();

        service.Log(new LlmLogEntry
        {
            Level = LlmLogLevel.Info,
            ProviderName = "Ollama",
            Category = LlmLogCategory.Request,
            Message = "Some entry",
            PipelineRunId = existingRunId,
        });

        // Act
        var logs = service.GetLogsForPipelineRun(nonExistentRunId);

        // Assert
        logs.ShouldBeEmpty();

        await Task.CompletedTask;
    }

    [Test]
    public async Task LlmLogService_LogExtension_IncludesPipelineRunId()
    {
        // Arrange
        var service = new LlmLogService();
        var runId = Guid.NewGuid();
        var originalValue = PipelineExecutionContext.CurrentPipelineRunId;

        try
        {
            PipelineExecutionContext.CurrentPipelineRunId = runId;

            // Act — use extension methods that read PipelineExecutionContext
            var requestId = service.LogRequest("TestProvider", "test-model", "What is 2+2?");
            service.LogResponse("TestProvider", "test-model", "4", durationMs: 100,
                inputTokens: 10, outputTokens: 5, requestId: requestId);

            // Assert
            var logs = service.GetLogsForPipelineRun(runId);
            logs.Count.ShouldBe(2);
            logs.ShouldAllBe(e => e.PipelineRunId == runId);

            var requestLog = logs.First(e => e.Category == LlmLogCategory.Request);
            requestLog.RequestId.ShouldBe(requestId);
            requestLog.FullPrompt.ShouldBe("What is 2+2?");

            var responseLog = logs.First(e => e.Category == LlmLogCategory.Response);
            responseLog.FullResponse.ShouldBe("4");
            responseLog.InputTokens.ShouldBe(10);
            responseLog.OutputTokens.ShouldBe(5);
        }
        finally
        {
            PipelineExecutionContext.CurrentPipelineRunId = originalValue;
        }

        await Task.CompletedTask;
    }

    #endregion

    #region PipelineEventService Prompt/Response Persistence Tests

    private string _dbPath = null!;
    private LiteDbContext _context = null!;
    private PipelineEventService _service = null!;

    [Before(Test)]
    public async Task SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"llm_persist_test_{Guid.NewGuid()}.db");
        _context = new LiteDbContext(_dbPath);
        _service = new PipelineEventService(_context, CreateLogger<PipelineEventService>());
        await Task.CompletedTask;
    }

    [After(Test)]
    public async Task TearDown()
    {
        _context.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
        await Task.CompletedTask;
    }

    [Test]
    public async Task RecordLlmCallAsync_StoresPromptAndResponseText()
    {
        // Arrange
        var run = await _service.StartRunAsync(Guid.NewGuid(), "test input", Guid.NewGuid());
        var promptText = "Analyze the following HTML and extract the price...";
        var responseText = """{"price": "$29.99", "confidence": 0.95}""";

        // Act
        var ev = await _service.RecordLlmCallAsync(
            run.Id,
            PipelineStageNames.ContentAnalysis,
            "Ollama",
            "llama3.2",
            inputTokens: 1200,
            outputTokens: 350,
            durationMs: 3000,
            promptText: promptText,
            responseText: responseText);

        // Assert
        ev.ShouldNotBeNull();
        ev.PromptText.ShouldBe(promptText);
        ev.ResponseText.ShouldBe(responseText);
        ev.EventType.ShouldBe(PipelineEventTypes.LlmCall);
        ev.LlmProvider.ShouldBe("Ollama");
        ev.LlmModel.ShouldBe("llama3.2");

        // Verify persisted data round-trips through LiteDB
        var events = await _service.GetEventsForRunAsync(run.Id);
        var persisted = events.First(e => e.EventType == PipelineEventTypes.LlmCall);
        persisted.PromptText.ShouldBe(promptText);
        persisted.ResponseText.ShouldBe(responseText);
    }

    [Test]
    public async Task PipelineEventDto_IncludesPromptAndResponseText()
    {
        // Arrange — the PipelineEventDto (in PipelineDebugEndpoints) maps from PipelineEvent
        // Verify the entity has the fields and they round-trip correctly
        var ev = new PipelineEvent
        {
            Stage = PipelineStageNames.ContentAnalysis,
            EventType = PipelineEventTypes.LlmCall,
            PromptText = "Extract the main heading from this page",
            ResponseText = "The main heading is: Welcome to Example.com",
            LlmProvider = "OpenAI",
            LlmModel = "gpt-4",
            InputTokens = 500,
            OutputTokens = 100,
        };

        // Act — verify the DTO record has matching fields
        var dto = new PipelineEventDto
        {
            Id = ev.Id,
            Stage = ev.Stage,
            EventType = ev.EventType,
            SequenceNumber = ev.SequenceNumber,
            Summary = ev.Summary,
            Details = ev.Details,
            DataJson = ev.DataJson,
            IsSuccess = ev.IsSuccess,
            ErrorMessage = ev.ErrorMessage,
            StackTrace = ev.StackTrace,
            Timestamp = ev.Timestamp,
            DurationMs = ev.DurationMs,
            LlmProvider = ev.LlmProvider,
            LlmModel = ev.LlmModel,
            InputTokens = ev.InputTokens,
            OutputTokens = ev.OutputTokens,
            Confidence = ev.Confidence,
            PromptText = ev.PromptText,
            ResponseText = ev.ResponseText
        };

        // Assert
        dto.PromptText.ShouldBe("Extract the main heading from this page");
        dto.ResponseText.ShouldBe("The main heading is: Welcome to Example.com");
        dto.LlmProvider.ShouldBe("OpenAI");
        dto.LlmModel.ShouldBe("gpt-4");
        dto.InputTokens.ShouldBe(500);
        dto.OutputTokens.ShouldBe(100);

        await Task.CompletedTask;
    }

    #endregion
}
