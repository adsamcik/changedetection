using ChangeDetection.Shared.Dtos;
using Shouldly;

namespace ChangeDetection.Tests.Pipeline;

/// <summary>
/// Unit tests for FlowStateEntry and FlowStateEntryDto.
/// Tests the data structures used for session state history.
/// </summary>
public class FlowStateEntryTests
{
    #region FlowStateEntry Tests

    [Fact]
    public void FlowStateEntry_HasRequiredProperties()
    {
        var entry = new FlowStateEntry
        {
            Stage = "UrlExtraction",
            Status = FlowStateStatus.InProgress,
            Summary = "Extracting URL from input..."
        };

        entry.Stage.ShouldBe("UrlExtraction");
        entry.Status.ShouldBe(FlowStateStatus.InProgress);
        entry.Summary.ShouldBe("Extracting URL from input...");
    }

    [Fact]
    public void FlowStateEntry_SupportsOptionalProperties()
    {
        var entry = new FlowStateEntry
        {
            Stage = "Complete",
            Status = FlowStateStatus.Completed,
            Summary = "Watch created successfully",
            Details = "URL: https://example.com/events",
            IsCurrentState = true,
            WatchId = Guid.NewGuid()
        };

        entry.Details.ShouldNotBeNullOrEmpty();
        entry.IsCurrentState.ShouldBeTrue();
        entry.WatchId.ShouldNotBeNull();
    }

    [Fact]
    public void FlowStateEntry_SupportsOptions()
    {
        var options = new List<FlowOption>
        {
            new("Event cards", ".event-card", true, "Preview content"),
            new("Full page", "fullpage", false, null)
        };

        var entry = new FlowStateEntry
        {
            Stage = "SelectorSelection",
            Status = FlowStateStatus.Question,
            Summary = "Select content to monitor",
            InputType = "select",
            Options = options
        };

        entry.Options.ShouldNotBeNull();
        entry.Options.Count.ShouldBe(2);
        entry.Options[0].IsRecommended.ShouldBeTrue();
        entry.Options[1].Preview.ShouldBeNull();
    }

    [Fact]
    public void FlowStateEntry_DefaultTimestamp_IsUtcNow()
    {
        var before = DateTimeOffset.UtcNow;
        
        var entry = new FlowStateEntry
        {
            Stage = "Test",
            Status = FlowStateStatus.InProgress,
            Summary = "Test"
        };
        
        var after = DateTimeOffset.UtcNow;

        entry.Timestamp.ShouldBeGreaterThanOrEqualTo(before.AddMilliseconds(-10));
        entry.Timestamp.ShouldBeLessThanOrEqualTo(after.AddMilliseconds(10));
    }

    #endregion

    #region FlowStateEntryDto Tests

    [Fact]
    public void FlowStateEntryDto_HasAllProperties()
    {
        var dto = new FlowStateEntryDto
        {
            Stage = "UrlExtraction",
            Status = FlowStateStatusDto.Completed,
            Summary = "URL extracted successfully",
            Timestamp = DateTimeOffset.UtcNow,
            IsCurrentState = false,
            Details = "Found: https://example.com",
            InputType = null,
            Options = null,
            WatchId = null
        };

        dto.Stage.ShouldBe("UrlExtraction");
        dto.Status.ShouldBe(FlowStateStatusDto.Completed);
        dto.Summary.ShouldBe("URL extracted successfully");
    }

    [Fact]
    public void FlowStateEntryDto_WatchId_CanBeSet()
    {
        var watchId = Guid.NewGuid();
        
        var dto = new FlowStateEntryDto
        {
            Stage = "Complete",
            Status = FlowStateStatusDto.Completed,
            Summary = "Watch created",
            WatchId = watchId
        };

        dto.WatchId.ShouldBe(watchId);
    }

    [Fact]
    public void FlowStateEntryDto_Options_CanBeSet()
    {
        var dto = new FlowStateEntryDto
        {
            Stage = "SelectorSelection",
            Status = FlowStateStatusDto.Question,
            Summary = "Select option",
            Options =
            [
                new FlowOptionDto { Label = "Option 1", Value = "opt1", IsRecommended = true },
                new FlowOptionDto { Label = "Option 2", Value = "opt2", IsRecommended = false }
            ]
        };

        dto.Options.ShouldNotBeNull();
        dto.Options.Count.ShouldBe(2);
        dto.Options[0].Label.ShouldBe("Option 1");
    }

    #endregion

    #region FlowStateStatus Mapping Tests

    [Theory]
    [InlineData(FlowStateStatusDto.Pending, FlowStateStatus.Pending)]
    [InlineData(FlowStateStatusDto.InProgress, FlowStateStatus.InProgress)]
    [InlineData(FlowStateStatusDto.Thinking, FlowStateStatus.Thinking)]
    [InlineData(FlowStateStatusDto.Completed, FlowStateStatus.Completed)]
    [InlineData(FlowStateStatusDto.Failed, FlowStateStatus.Failed)]
    [InlineData(FlowStateStatusDto.Question, FlowStateStatus.Question)]
    [InlineData(FlowStateStatusDto.Recovery, FlowStateStatus.Recovery)]
    public void FlowStateStatus_MapsCorrectlyBetweenDtoAndRecord(FlowStateStatusDto dtoStatus, FlowStateStatus expectedStatus)
    {
        var mapped = dtoStatus switch
        {
            FlowStateStatusDto.Pending => FlowStateStatus.Pending,
            FlowStateStatusDto.InProgress => FlowStateStatus.InProgress,
            FlowStateStatusDto.Thinking => FlowStateStatus.Thinking,
            FlowStateStatusDto.Completed => FlowStateStatus.Completed,
            FlowStateStatusDto.Failed => FlowStateStatus.Failed,
            FlowStateStatusDto.Question => FlowStateStatus.Question,
            FlowStateStatusDto.Recovery => FlowStateStatus.Recovery,
            _ => FlowStateStatus.Pending
        };

        mapped.ShouldBe(expectedStatus);
    }

    #endregion

    #region FlowOption Tests

    [Fact]
    public void FlowOption_RecordCreation()
    {
        var option = new FlowOption("Label", "value", true, "Preview text");

        option.Label.ShouldBe("Label");
        option.Value.ShouldBe("value");
        option.IsRecommended.ShouldBeTrue();
        option.Preview.ShouldBe("Preview text");
    }

    [Fact]
    public void FlowOption_DefaultValues()
    {
        var option = new FlowOption("Label", "value");

        option.IsRecommended.ShouldBeFalse();
        option.Preview.ShouldBeNull();
    }

    #endregion

    #region Stage Progression Tests

    [Fact]
    public void FlowStateEntry_CommonStageProgression()
    {
        var entries = new List<FlowStateEntry>
        {
            new() { Stage = "UrlExtraction", Status = FlowStateStatus.InProgress, Summary = "Starting..." },
            new() { Stage = "UrlExtraction", Status = FlowStateStatus.Completed, Summary = "URL extracted" },
            new() { Stage = "ContentFetching", Status = FlowStateStatus.InProgress, Summary = "Fetching..." },
            new() { Stage = "ContentFetching", Status = FlowStateStatus.Completed, Summary = "Content fetched" },
            new() { Stage = "ContentAnalysis", Status = FlowStateStatus.InProgress, Summary = "Analyzing..." },
            new() { Stage = "ContentAnalysis", Status = FlowStateStatus.Completed, Summary = "Analysis complete" },
            new() { Stage = "SelectorGeneration", Status = FlowStateStatus.InProgress, Summary = "Generating..." },
            new() { Stage = "SelectorGeneration", Status = FlowStateStatus.Completed, Summary = "Selectors generated" },
            new() { Stage = "Complete", Status = FlowStateStatus.Completed, Summary = "Watch created", WatchId = Guid.NewGuid() }
        };

        // Verify progression
        entries.Count.ShouldBe(9);
        entries.First().Stage.ShouldBe("UrlExtraction");
        entries.Last().Stage.ShouldBe("Complete");
        entries.Last().WatchId.ShouldNotBeNull();
    }

    [Fact]
    public void FlowStateEntry_QuestionInterruptionFlow()
    {
        var entries = new List<FlowStateEntry>
        {
            new() { Stage = "UrlExtraction", Status = FlowStateStatus.InProgress, Summary = "Starting..." },
            new() { Stage = "UrlExtraction", Status = FlowStateStatus.Question, Summary = "Multiple URLs found", 
                Options = [new("Site 1", "https://site1.com"), new("Site 2", "https://site2.com")] }
        };

        var lastEntry = entries.Last();
        lastEntry.Status.ShouldBe(FlowStateStatus.Question);
        lastEntry.Options.ShouldNotBeNull();
        lastEntry.Options.Count.ShouldBe(2);
    }

    [Fact]
    public void FlowStateEntry_ErrorFlow()
    {
        var entries = new List<FlowStateEntry>
        {
            new() { Stage = "UrlExtraction", Status = FlowStateStatus.InProgress, Summary = "Starting..." },
            new() { Stage = "ContentFetching", Status = FlowStateStatus.InProgress, Summary = "Fetching..." },
            new() { Stage = "ContentFetching", Status = FlowStateStatus.Failed, Summary = "Failed to fetch", 
                Details = "Connection timeout after 30 seconds" }
        };

        var lastEntry = entries.Last();
        lastEntry.Status.ShouldBe(FlowStateStatus.Failed);
        lastEntry.Details.ShouldNotBeNull();
        lastEntry.Details!.ShouldContain("timeout");
    }

    #endregion
}

/// <summary>
/// Tests for session state history list management logic.
/// </summary>
public class SessionStateHistoryManagementTests
{
    [Fact]
    public void HistoryList_MarksOnlyLastEntryAsCurrent()
    {
        var history = new List<FlowStateEntryDto>();

        // Simulate recording entries
        RecordEntry(history, new FlowStateEntryDto
        {
            Stage = "Stage1",
            Status = FlowStateStatusDto.InProgress,
            Summary = "First",
            IsCurrentState = true
        });

        history.Count(e => e.IsCurrentState).ShouldBe(1);
        history.Last().IsCurrentState.ShouldBeTrue();

        RecordEntry(history, new FlowStateEntryDto
        {
            Stage = "Stage2",
            Status = FlowStateStatusDto.InProgress,
            Summary = "Second",
            IsCurrentState = true
        });

        history.Count(e => e.IsCurrentState).ShouldBe(1);
        history.Last().IsCurrentState.ShouldBeTrue();
        history.First().IsCurrentState.ShouldBeFalse();
    }

    [Fact]
    public void HistoryList_PreservesAllEntries()
    {
        var history = new List<FlowStateEntryDto>();

        for (int i = 0; i < 10; i++)
        {
            RecordEntry(history, new FlowStateEntryDto
            {
                Stage = $"Stage{i}",
                Status = FlowStateStatusDto.InProgress,
                Summary = $"Entry {i}",
                IsCurrentState = true
            });
        }

        history.Count.ShouldBe(10);
        
        for (int i = 0; i < 10; i++)
        {
            history[i].Stage.ShouldBe($"Stage{i}");
        }
    }

    [Fact]
    public void HistoryList_CopyIsIndependent()
    {
        var history = new List<FlowStateEntryDto>
        {
            new() { Stage = "Stage1", Status = FlowStateStatusDto.Completed, Summary = "First" },
            new() { Stage = "Stage2", Status = FlowStateStatusDto.Completed, Summary = "Second" }
        };

        // Create a copy like GetSessionHistory does
        var copy = history.ToList();

        // Modify original
        history.Add(new FlowStateEntryDto { Stage = "Stage3", Status = FlowStateStatusDto.InProgress, Summary = "Third" });

        // Copy should be unaffected
        copy.Count.ShouldBe(2);
        history.Count.ShouldBe(3);
    }

    /// <summary>
    /// Simulates the RecordStateEntry logic
    /// </summary>
    private static void RecordEntry(List<FlowStateEntryDto> history, FlowStateEntryDto entry)
    {
        // Mark all previous entries as not current
        foreach (var existing in history)
        {
            existing.IsCurrentState = false;
        }
        
        history.Add(entry);
    }
}

/// <summary>
/// Tests for determining UI state from history entries.
/// </summary>
public class HistoryStateRestorationTests
{
    [Fact]
    public void RestoreState_QuestionEntry_SetsAwaitingInput()
    {
        var history = new List<FlowStateEntryDto>
        {
            new() { Stage = "UrlExtraction", Status = FlowStateStatusDto.Completed, Summary = "Done", IsCurrentState = false },
            new() { Stage = "UrlSelection", Status = FlowStateStatusDto.Question, Summary = "Select URL", 
                IsCurrentState = true, InputType = "select",
                Options = [new() { Label = "Site 1", Value = "https://site1.com" }] }
        };

        var lastEntry = history.Last();
        
        // Simulate client-side state restoration
        var awaitingInput = lastEntry.Status == FlowStateStatusDto.Question;
        var currentQuestion = lastEntry.Summary;
        var currentInputType = lastEntry.InputType ?? "text";
        var currentOptions = lastEntry.Options;

        awaitingInput.ShouldBeTrue();
        currentQuestion.ShouldBe("Select URL");
        currentInputType.ShouldBe("select");
        currentOptions.ShouldNotBeNull();
        currentOptions.Count.ShouldBe(1);
    }

    [Fact]
    public void RestoreState_CompletedEntry_SetsComplete()
    {
        var watchId = Guid.NewGuid();
        var history = new List<FlowStateEntryDto>
        {
            new() { Stage = "UrlExtraction", Status = FlowStateStatusDto.Completed, Summary = "Done", IsCurrentState = false },
            new() { Stage = "Complete", Status = FlowStateStatusDto.Completed, Summary = "Watch created", 
                IsCurrentState = true, WatchId = watchId }
        };

        var lastEntry = history.Last();
        
        // Simulate client-side state restoration
        var isComplete = lastEntry.Status == FlowStateStatusDto.Completed && lastEntry.Stage == "Complete";
        var successMessage = lastEntry.Summary;
        var restoredWatchId = lastEntry.WatchId;

        isComplete.ShouldBeTrue();
        successMessage.ShouldBe("Watch created");
        restoredWatchId.ShouldBe(watchId);
    }

    [Fact]
    public void RestoreState_FailedEntry_SetsError()
    {
        var history = new List<FlowStateEntryDto>
        {
            new() { Stage = "UrlExtraction", Status = FlowStateStatusDto.Completed, Summary = "Done", IsCurrentState = false },
            new() { Stage = "ContentFetching", Status = FlowStateStatusDto.Failed, Summary = "Connection failed", 
                IsCurrentState = true, Details = "Timeout after 30s" }
        };

        var lastEntry = history.Last();
        
        // Simulate client-side state restoration
        var hasError = lastEntry.Status == FlowStateStatusDto.Failed;
        var errorMessage = lastEntry.Summary;

        hasError.ShouldBeTrue();
        errorMessage.ShouldBe("Connection failed");
    }

    [Fact]
    public void RestoreState_InProgressEntry_ShowsProcessing()
    {
        var history = new List<FlowStateEntryDto>
        {
            new() { Stage = "UrlExtraction", Status = FlowStateStatusDto.Completed, Summary = "Done", IsCurrentState = false },
            new() { Stage = "ContentAnalysis", Status = FlowStateStatusDto.InProgress, Summary = "Analyzing...", 
                IsCurrentState = true }
        };

        var lastEntry = history.Last();
        
        // Simulate client-side state restoration
        var isProcessing = lastEntry.Status == FlowStateStatusDto.InProgress;
        var isQuestion = lastEntry.Status == FlowStateStatusDto.Question;
        var isComplete = lastEntry.Stage == "Complete";
        var isError = lastEntry.Status == FlowStateStatusDto.Failed;

        isProcessing.ShouldBeTrue();
        isQuestion.ShouldBeFalse();
        isComplete.ShouldBeFalse();
        isError.ShouldBeFalse();
    }

    [Fact]
    public void RestoreState_EmptyHistory_ShowsEmpty()
    {
        var history = new List<FlowStateEntryDto>();

        var hasHistory = history.Count > 0;
        
        hasHistory.ShouldBeFalse();
    }
}
