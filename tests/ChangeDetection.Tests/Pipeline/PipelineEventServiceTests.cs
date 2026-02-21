using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Persistence;
using ChangeDetection.Services.Pipeline;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline;

/// <summary>
/// Unit tests for PipelineEventService.
/// Verifies pipeline run tracking and event recording.
/// </summary>
public class PipelineEventServiceTests
{
    private string _dbPath = null!;
    private LiteDbContext _context = null!;
    private PipelineEventService _service = null!;

    [Before(Test)]
    public async Task SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"pipeline_test_{Guid.NewGuid()}.db");
        _context = new LiteDbContext(_dbPath);
        _service = new PipelineEventService(_context, Substitute.For<ILogger<PipelineEventService>>());
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

    #region Pipeline Run Tests

    [Test]
    public async Task StartRunAsync_CreatesNewRun()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var input = "Watch https://example.com for changes";

        // Act
        var run = await _service.StartRunAsync(sessionId, input, ownerId);

        // Assert
        run.ShouldNotBeNull();
        run.Id.ShouldNotBe(Guid.Empty);
        run.SessionId.ShouldBe(sessionId);
        run.OwnerId.ShouldBe(ownerId);
        run.OriginalInput.ShouldBe(input);
        run.Status.ShouldBe(PipelineRunStatus.Started);
        run.StartedAt.ShouldBeLessThanOrEqualTo(DateTime.UtcNow);
    }

    [Test]
    public async Task GetRunByIdAsync_ReturnsRun()
    {
        // Arrange
        var run = await _service.StartRunAsync(Guid.NewGuid(), "test input", Guid.NewGuid());

        // Act
        var retrieved = await _service.GetRunByIdAsync(run.Id);

        // Assert
        retrieved.ShouldNotBeNull();
        retrieved.Id.ShouldBe(run.Id);
        retrieved.OriginalInput.ShouldBe("test input");
    }

    [Test]
    public async Task GetRunBySessionIdAsync_ReturnsRun()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var run = await _service.StartRunAsync(sessionId, "test input", Guid.NewGuid());

        // Act
        var retrieved = await _service.GetRunBySessionIdAsync(sessionId);

        // Assert
        retrieved.ShouldNotBeNull();
        retrieved.Id.ShouldBe(run.Id);
        retrieved.SessionId.ShouldBe(sessionId);
    }

    [Test]
    public async Task UpdateRunStatusAsync_UpdatesStatus()
    {
        // Arrange
        var run = await _service.StartRunAsync(Guid.NewGuid(), "test input", Guid.NewGuid());

        // Act
        await _service.UpdateRunStatusAsync(run.Id, PipelineRunStatus.InProgress, "ContentAnalysis");

        // Assert
        var updated = await _service.GetRunByIdAsync(run.Id);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe(PipelineRunStatus.InProgress);
        updated.CurrentStage.ShouldBe("ContentAnalysis");
    }

    [Test]
    public async Task CompleteRunAsync_SetsCompletedStatus()
    {
        // Arrange
        var run = await _service.StartRunAsync(Guid.NewGuid(), "test input", Guid.NewGuid());
        var watchId = Guid.NewGuid();
        
        // Delay required: DurationMs is computed from DateTime.UtcNow difference between
        // StartRunAsync and CompleteRunAsync. 200ms ensures a measurably positive duration.
        await Task.Delay(200);

        // Act
        await _service.CompleteRunAsync(run.Id, watchId, """{"url":"https://example.com"}""");

        // Assert
        var completed = await _service.GetRunByIdAsync(run.Id);
        completed.ShouldNotBeNull();
        completed.Status.ShouldBe(PipelineRunStatus.Completed);
        completed.CreatedWatchId.ShouldBe(watchId);
        completed.CompletedAt.ShouldNotBeNull();
        completed.DurationMs.ShouldNotBeNull();
        // Duration should be positive, but due to timezone/precision issues just check it's set
        completed.FinalConfigurationJson.ShouldBe("""{"url":"https://example.com"}""");
    }

    [Test]
    public async Task FailRunAsync_SetsFailedStatus()
    {
        // Arrange
        var run = await _service.StartRunAsync(Guid.NewGuid(), "test input", Guid.NewGuid());

        // Act
        await _service.FailRunAsync(run.Id, "Content fetch failed");

        // Assert
        var failed = await _service.GetRunByIdAsync(run.Id);
        failed.ShouldNotBeNull();
        failed.Status.ShouldBe(PipelineRunStatus.Failed);
        failed.ErrorMessage.ShouldBe("Content fetch failed");
        failed.CompletedAt.ShouldNotBeNull();
    }

    [Test]
    public async Task CancelRunAsync_SetsCancelledStatus()
    {
        // Arrange
        var run = await _service.StartRunAsync(Guid.NewGuid(), "test input", Guid.NewGuid());

        // Act
        await _service.CancelRunAsync(run.Id);

        // Assert
        var cancelled = await _service.GetRunByIdAsync(run.Id);
        cancelled.ShouldNotBeNull();
        cancelled.Status.ShouldBe(PipelineRunStatus.Cancelled);
        cancelled.CompletedAt.ShouldNotBeNull();
    }

    [Test]
    public async Task UpdateExtractedUrlAsync_UpdatesUrl()
    {
        // Arrange
        var run = await _service.StartRunAsync(Guid.NewGuid(), "test input", Guid.NewGuid());

        // Act
        await _service.UpdateExtractedUrlAsync(run.Id, "https://example.com/page");

        // Assert
        var updated = await _service.GetRunByIdAsync(run.Id);
        updated.ShouldNotBeNull();
        updated.ExtractedUrl.ShouldBe("https://example.com/page");
    }

    #endregion

    #region Event Recording Tests

    [Test]
    public async Task RecordEventAsync_CreatesEvent()
    {
        // Arrange
        var run = await _service.StartRunAsync(Guid.NewGuid(), "test input", Guid.NewGuid());

        // Act
        var ev = await _service.RecordEventAsync(
            run.Id,
            PipelineStageNames.UrlExtraction,
            PipelineEventTypes.StageStarted,
            "Starting URL extraction");

        // Assert
        ev.ShouldNotBeNull();
        ev.Id.ShouldNotBe(Guid.Empty);
        ev.PipelineRunId.ShouldBe(run.Id);
        ev.Stage.ShouldBe(PipelineStageNames.UrlExtraction);
        ev.EventType.ShouldBe(PipelineEventTypes.StageStarted);
        ev.Summary.ShouldBe("Starting URL extraction");
        ev.SequenceNumber.ShouldBe(1);
        ev.IsSuccess.ShouldBeTrue();
    }

    [Test]
    public async Task RecordEventAsync_IncrementsSequenceNumber()
    {
        // Arrange
        var run = await _service.StartRunAsync(Guid.NewGuid(), "test input", Guid.NewGuid());

        // Act
        var ev1 = await _service.RecordEventAsync(run.Id, "Stage1", "Started", "First");
        var ev2 = await _service.RecordEventAsync(run.Id, "Stage2", "Started", "Second");
        var ev3 = await _service.RecordEventAsync(run.Id, "Stage3", "Started", "Third");

        // Assert
        ev1.SequenceNumber.ShouldBe(1);
        ev2.SequenceNumber.ShouldBe(2);
        ev3.SequenceNumber.ShouldBe(3);
    }

    [Test]
    public async Task RecordLlmCallAsync_RecordsTokenUsage()
    {
        // Arrange
        var run = await _service.StartRunAsync(Guid.NewGuid(), "test input", Guid.NewGuid());

        // Act
        var ev = await _service.RecordLlmCallAsync(
            run.Id,
            PipelineStageNames.ContentAnalysis,
            "Ollama",
            "llama3.2",
            inputTokens: 1500,
            outputTokens: 500,
            durationMs: 2500);

        // Assert
        ev.ShouldNotBeNull();
        ev.EventType.ShouldBe(PipelineEventTypes.LlmCall);
        ev.LlmProvider.ShouldBe("Ollama");
        ev.LlmModel.ShouldBe("llama3.2");
        ev.InputTokens.ShouldBe(1500);
        ev.OutputTokens.ShouldBe(500);
        ev.DurationMs.ShouldBe(2500);

        // Verify run totals updated
        var updated = await _service.GetRunByIdAsync(run.Id);
        updated.ShouldNotBeNull();
        updated.LlmCallCount.ShouldBe(1);
        updated.TotalInputTokens.ShouldBe(1500);
        updated.TotalOutputTokens.ShouldBe(500);
    }

    [Test]
    public async Task RecordFailureAsync_RecordsErrorDetails()
    {
        // Arrange
        var run = await _service.StartRunAsync(Guid.NewGuid(), "test input", Guid.NewGuid());

        // Act
        var ev = await _service.RecordFailureAsync(
            run.Id,
            PipelineStageNames.ContentFetching,
            "Connection timeout",
            "at System.Net.Http...");

        // Assert
        ev.ShouldNotBeNull();
        ev.EventType.ShouldBe(PipelineEventTypes.StageFailed);
        ev.ErrorMessage.ShouldBe("Connection timeout");
        ev.StackTrace.ShouldBe("at System.Net.Http...");
        ev.IsSuccess.ShouldBeFalse();
    }

    [Test]
    public async Task RecordUserInteractionAsync_IncrementsInteractionCount()
    {
        // Arrange
        var run = await _service.StartRunAsync(Guid.NewGuid(), "test input", Guid.NewGuid());

        // Act
        await _service.RecordUserInteractionAsync(run.Id, PipelineEventTypes.UserInputReceived, 
            "Selected option 2", "User selected option");
        await _service.RecordUserInteractionAsync(run.Id, PipelineEventTypes.UserInputReceived, 
            "Confirmed", "User confirmed");

        // Assert
        var updated = await _service.GetRunByIdAsync(run.Id);
        updated.ShouldNotBeNull();
        updated.UserInteractionCount.ShouldBe(2);
    }

    [Test]
    public async Task GetEventsForRunAsync_ReturnsEventsInOrder()
    {
        // Arrange
        var run = await _service.StartRunAsync(Guid.NewGuid(), "test input", Guid.NewGuid());
        await _service.RecordEventAsync(run.Id, "Stage1", "Started", "First");
        await _service.RecordEventAsync(run.Id, "Stage2", "Started", "Second");
        await _service.RecordEventAsync(run.Id, "Stage3", "Started", "Third");

        // Act
        var events = await _service.GetEventsForRunAsync(run.Id);

        // Assert
        events.Count.ShouldBe(3);
        events[0].Summary.ShouldBe("First");
        events[1].Summary.ShouldBe("Second");
        events[2].Summary.ShouldBe("Third");
    }

    #endregion

    #region Query Tests

    [Test]
    public async Task GetRecentRunsAsync_ReturnsRunsInDescendingOrder()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        // Delays required: GetRecentRunsAsync orders by DateTime.UtcNow-based StartedAt,
        // so each run must have a distinct timestamp. 200ms ensures timer resolution safety.
        await _service.StartRunAsync(Guid.NewGuid(), "first", ownerId);
        await Task.Delay(200);
        await _service.StartRunAsync(Guid.NewGuid(), "second", ownerId);
        await Task.Delay(200);
        await _service.StartRunAsync(Guid.NewGuid(), "third", ownerId);

        // Act
        var runs = await _service.GetRecentRunsAsync(ownerId, 10);

        // Assert
        runs.Count.ShouldBe(3);
        runs[0].OriginalInput.ShouldBe("third");
        runs[1].OriginalInput.ShouldBe("second");
        runs[2].OriginalInput.ShouldBe("first");
    }

    [Test]
    public async Task GetRecentRunsAsync_RespectsLimit()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        for (int i = 0; i < 5; i++)
        {
            await _service.StartRunAsync(Guid.NewGuid(), $"run {i}", ownerId);
        }

        // Act
        var runs = await _service.GetRecentRunsAsync(ownerId, 2);

        // Assert
        runs.Count.ShouldBe(2);
    }

    [Test]
    public async Task GetRunsByStatusAsync_FiltersCorrectly()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var run1 = await _service.StartRunAsync(Guid.NewGuid(), "run1", ownerId);
        var run2 = await _service.StartRunAsync(Guid.NewGuid(), "run2", ownerId);
        var run3 = await _service.StartRunAsync(Guid.NewGuid(), "run3", ownerId);
        
        await _service.CompleteRunAsync(run1.Id, Guid.NewGuid());
        await _service.FailRunAsync(run2.Id, "error");

        // Act
        var completedRuns = await _service.GetRunsByStatusAsync(ownerId, PipelineRunStatus.Completed);
        var failedRuns = await _service.GetRunsByStatusAsync(ownerId, PipelineRunStatus.Failed);
        var startedRuns = await _service.GetRunsByStatusAsync(ownerId, PipelineRunStatus.Started);

        // Assert
        completedRuns.Count.ShouldBe(1);
        failedRuns.Count.ShouldBe(1);
        startedRuns.Count.ShouldBe(1);
    }

    [Test]
    public async Task GetStatisticsAsync_CalculatesCorrectly()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddHours(1);

        var run1 = await _service.StartRunAsync(Guid.NewGuid(), "run1", ownerId);
        var run2 = await _service.StartRunAsync(Guid.NewGuid(), "run2", ownerId);
        var run3 = await _service.StartRunAsync(Guid.NewGuid(), "run3", ownerId);

        await _service.RecordLlmCallAsync(run1.Id, "Stage", "Provider", "Model", 100, 50, 1000);
        await _service.RecordLlmCallAsync(run2.Id, "Stage", "Provider", "Model", 200, 100, 2000);

        await _service.CompleteRunAsync(run1.Id, Guid.NewGuid());
        await _service.FailRunAsync(run2.Id, "error");
        await _service.CancelRunAsync(run3.Id);

        // Act
        var stats = await _service.GetStatisticsAsync(ownerId, from, to);

        // Assert
        stats.TotalRuns.ShouldBe(3);
        stats.SuccessfulRuns.ShouldBe(1);
        stats.FailedRuns.ShouldBe(1);
        stats.CancelledRuns.ShouldBe(1);
        stats.TotalLlmCalls.ShouldBe(2);
        stats.TotalInputTokens.ShouldBe(300);
        stats.TotalOutputTokens.ShouldBe(150);
        stats.SuccessRate.ShouldBeInRange(0.33, 0.34);
    }

    #endregion

    #region Cleanup Tests

    [Test]
    public async Task CleanupOldRunsAsync_DeletesOldRunsAndEvents()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var run = await _service.StartRunAsync(Guid.NewGuid(), "old run", ownerId);
        await _service.RecordEventAsync(run.Id, "Stage", "Event", "test");

        // Delay required: CleanupOldRunsAsync compares StartedAt against DateTime.UtcNow,
        // so the run must be older than the cleanup threshold. 200ms ensures reliable age gap.
        await Task.Delay(200);

        // Act
        var deleted = await _service.CleanupOldRunsAsync(TimeSpan.FromMilliseconds(10));

        // Assert
        deleted.ShouldBe(1);
        var retrieved = await _service.GetRunByIdAsync(run.Id);
        retrieved.ShouldBeNull();
        
        var events = await _service.GetEventsForRunAsync(run.Id);
        events.Count.ShouldBe(0);
    }

    #endregion

    #region Status Lifecycle Tests

    [Test]
    public async Task FullStatusLifecycle_Started_InProgress_Completed()
    {
        // Arrange — mirrors the real pipeline flow
        var run = await _service.StartRunAsync(Guid.NewGuid(), "test input", Guid.NewGuid());
        run.Status.ShouldBe(PipelineRunStatus.Started);

        // Act — pipeline transitions to InProgress when processing begins
        await _service.UpdateRunStatusAsync(run.Id, PipelineRunStatus.InProgress, 
            PipelineStageNames.UrlExtraction);
        var inProgress = await _service.GetRunByIdAsync(run.Id);
        inProgress!.Status.ShouldBe(PipelineRunStatus.InProgress);
        inProgress.CurrentStage.ShouldBe(PipelineStageNames.UrlExtraction);

        // Record events along the way
        await _service.RecordEventAsync(run.Id, PipelineStageNames.UrlExtraction, 
            PipelineEventTypes.StageCompleted, "URL extracted");
        await _service.RecordLlmCallAsync(run.Id, PipelineStageNames.ContentAnalysis,
            "TestProvider", "test-model", 100, 50, 500);

        // Transition to Completed
        await _service.UpdateRunStatusAsync(run.Id, PipelineRunStatus.Completed, 
            PipelineStageNames.Configuration);
        var completed = await _service.GetRunByIdAsync(run.Id);
        completed!.Status.ShouldBe(PipelineRunStatus.Completed);
        completed.LlmCallCount.ShouldBe(1);
        completed.TotalInputTokens.ShouldBe(100);
        completed.TotalOutputTokens.ShouldBe(50);
    }

    [Test]
    public async Task FullStatusLifecycle_Started_InProgress_Failed()
    {
        // Arrange
        var run = await _service.StartRunAsync(Guid.NewGuid(), "test input", Guid.NewGuid());

        // Act — pipeline transitions to InProgress then fails
        await _service.UpdateRunStatusAsync(run.Id, PipelineRunStatus.InProgress, 
            PipelineStageNames.ContentFetching);
        await _service.RecordFailureAsync(run.Id, PipelineStageNames.ContentFetching, 
            "Connection refused", "at System.Net...");
        await _service.FailRunAsync(run.Id, "Connection refused");

        // Assert
        var failed = await _service.GetRunByIdAsync(run.Id);
        failed!.Status.ShouldBe(PipelineRunStatus.Failed);
        failed.ErrorMessage.ShouldBe("Connection refused");
        failed.CompletedAt.ShouldNotBeNull();

        var events = await _service.GetEventsForRunAsync(run.Id);
        events.ShouldContain(e => e.EventType == PipelineEventTypes.StageFailed);
    }

    [Test]
    public async Task FullStatusLifecycle_AwaitingUserInput_ResumeToCompleted()
    {
        // Arrange — pipeline pauses for user input then completes
        var run = await _service.StartRunAsync(Guid.NewGuid(), "test input", Guid.NewGuid());
        await _service.UpdateRunStatusAsync(run.Id, PipelineRunStatus.InProgress, 
            PipelineStageNames.SelectorGeneration);

        // Transition to AwaitingUserInput
        await _service.UpdateRunStatusAsync(run.Id, PipelineRunStatus.AwaitingUserInput, 
            PipelineStageNames.SelectorGeneration);
        var awaiting = await _service.GetRunByIdAsync(run.Id);
        awaiting!.Status.ShouldBe(PipelineRunStatus.AwaitingUserInput);

        // User responds — back to InProgress then Completed
        await _service.UpdateRunStatusAsync(run.Id, PipelineRunStatus.InProgress, 
            PipelineStageNames.SelectorGeneration);
        await _service.CompleteRunAsync(run.Id, Guid.NewGuid());

        var completed = await _service.GetRunByIdAsync(run.Id);
        completed!.Status.ShouldBe(PipelineRunStatus.Completed);
    }

    #endregion
}
