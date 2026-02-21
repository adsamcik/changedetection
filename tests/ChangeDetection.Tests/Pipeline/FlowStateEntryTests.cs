using ChangeDetection.Shared.Dtos;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline;

/// <summary>
/// Contract tests for FlowStateEntry record and FlowStateEntryDto class.
/// Verifies data model shapes, defaults, record semantics, and enum parity
/// that production code (SetupConversationHub, PipelineWorkerService) relies on.
/// </summary>
[Category("Unit")]
public class FlowStateEntryTests
{
    #region FlowStateEntry Record Shape Tests

    [Test]
    public async Task FlowStateEntry_HasRequiredProperties()
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
        await Task.CompletedTask;
    }

    [Test]
    public async Task FlowStateEntry_SupportsOptionalProperties()
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
        await Task.CompletedTask;
    }

    [Test]
    public async Task FlowStateEntry_SupportsOptions()
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
        await Task.CompletedTask;
    }

    [Test]
    public async Task FlowStateEntry_DefaultTimestamp_IsUtcNow()
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
        await Task.CompletedTask;
    }

    #endregion

    #region FlowStateEntry Record Semantics Tests

    [Test]
    public async Task FlowStateEntry_ValueEquality_SameValues_AreEqual()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var entry1 = new FlowStateEntry { Stage = "Test", Status = FlowStateStatus.InProgress, Summary = "Testing", Timestamp = timestamp };
        var entry2 = new FlowStateEntry { Stage = "Test", Status = FlowStateStatus.InProgress, Summary = "Testing", Timestamp = timestamp };

        entry1.ShouldBe(entry2);
        await Task.CompletedTask;
    }

    [Test]
    public async Task FlowStateEntry_ValueEquality_DifferentStatus_AreNotEqual()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var entry1 = new FlowStateEntry { Stage = "Test", Status = FlowStateStatus.InProgress, Summary = "Test", Timestamp = timestamp };
        var entry2 = new FlowStateEntry { Stage = "Test", Status = FlowStateStatus.Completed, Summary = "Test", Timestamp = timestamp };

        entry1.ShouldNotBe(entry2);
        await Task.CompletedTask;
    }

    [Test]
    public async Task FlowStateEntry_WithExpression_CreatesModifiedCopy()
    {
        var original = new FlowStateEntry { Stage = "UrlExtraction", Status = FlowStateStatus.InProgress, Summary = "Starting..." };
        var completed = original with { Status = FlowStateStatus.Completed, Summary = "URL extracted" };

        original.Status.ShouldBe(FlowStateStatus.InProgress);
        completed.Status.ShouldBe(FlowStateStatus.Completed);
        completed.Stage.ShouldBe(original.Stage);
        await Task.CompletedTask;
    }

    #endregion

    #region FlowStateEntryDto Tests

    [Test]
    public async Task FlowStateEntryDto_HasAllProperties()
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
        await Task.CompletedTask;
    }

    [Test]
    public async Task FlowStateEntryDto_WatchId_CanBeSet()
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
        await Task.CompletedTask;
    }

    [Test]
    public async Task FlowStateEntryDto_Options_CanBeSet()
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
        await Task.CompletedTask;
    }

    [Test]
    public async Task FlowStateEntryDto_DefaultStatus_IsPending()
    {
        var dto = new FlowStateEntryDto { Stage = "Test", Summary = "Test" };

        dto.Status.ShouldBe(FlowStateStatusDto.Pending);
        await Task.CompletedTask;
    }

    [Test]
    public async Task FlowStateEntryDto_DefaultIsCurrentState_IsFalse()
    {
        var dto = new FlowStateEntryDto { Stage = "Test", Summary = "Test" };

        dto.IsCurrentState.ShouldBeFalse();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Production RecordStateEntry (SetupConversationHub) mutates IsCurrentState
    /// on existing entries to mark only the latest as current.
    /// </summary>
    [Test]
    public async Task FlowStateEntryDto_IsCurrentState_IsMutable()
    {
        var dto = new FlowStateEntryDto { Stage = "Test", Summary = "Test", IsCurrentState = true };

        dto.IsCurrentState.ShouldBeTrue();
        dto.IsCurrentState = false;
        dto.IsCurrentState.ShouldBeFalse();
        await Task.CompletedTask;
    }

    #endregion

    #region FlowStateStatus Enum Parity Tests

    /// <summary>
    /// FlowStateStatus (record enum) and FlowStateStatusDto (DTO enum) must stay in sync.
    /// Production MapProgressToFlowState maps to FlowStateStatusDto by name parity.
    /// </summary>
    [Test]
    public async Task FlowStateStatus_AndDto_HaveIdenticalMemberNames()
    {
        var recordNames = Enum.GetNames<FlowStateStatus>();
        var dtoNames = Enum.GetNames<FlowStateStatusDto>();

        dtoNames.ShouldBe(recordNames);
        await Task.CompletedTask;
    }

    [Test]
    public async Task FlowStateStatus_AndDto_HaveIdenticalIntValues()
    {
        foreach (var name in Enum.GetNames<FlowStateStatus>())
        {
            var recordValue = (int)Enum.Parse<FlowStateStatus>(name);
            var dtoValue = (int)Enum.Parse<FlowStateStatusDto>(name);
            dtoValue.ShouldBe(recordValue, $"Enum value mismatch for {name}");
        }
        await Task.CompletedTask;
    }

    #endregion

    #region FlowOption Tests

    [Test]
    public async Task FlowOption_RecordCreation()
    {
        var option = new FlowOption("Label", "value", true, "Preview text");

        option.Label.ShouldBe("Label");
        option.Value.ShouldBe("value");
        option.IsRecommended.ShouldBeTrue();
        option.Preview.ShouldBe("Preview text");
        await Task.CompletedTask;
    }

    [Test]
    public async Task FlowOption_DefaultValues()
    {
        var option = new FlowOption("Label", "value");

        option.IsRecommended.ShouldBeFalse();
        option.Preview.ShouldBeNull();
        await Task.CompletedTask;
    }

    #endregion
}
