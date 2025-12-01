using ChangeDetection.Shared.Dtos;
using Shouldly;

namespace ChangeDetection.Tests.Dtos;

public class WatchDtoTests
{
    [Fact]
    public void WatchListItemDto_HasDefaultValues()
    {
        // Act
        var dto = new WatchListItemDto { Url = "https://example.com" };

        // Assert
        dto.Id.ShouldBe("");
        dto.Status.ShouldBe("Idle");
        dto.IsEnabled.ShouldBeTrue();
        dto.ChangeCount.ShouldBe(0);
        dto.HasRecentChanges.ShouldBeFalse();
    }

    [Fact]
    public void WatchDetailDto_HasDefaultValues()
    {
        // Act
        var dto = new WatchDetailDto { Url = "https://example.com" };

        // Assert
        dto.Id.ShouldBe("");
        dto.Status.ShouldBe("Idle");
        dto.IsEnabled.ShouldBeTrue();
        dto.IgnorePatterns.ShouldBeEmpty();
    }

    [Fact]
    public void WatchCreateDto_HasDefaultValues()
    {
        // Act
        var dto = new WatchCreateDto { Url = "https://example.com" };

        // Assert
        dto.CheckInterval.ShouldBe(TimeSpan.FromHours(1));
        dto.IsEnabled.ShouldBeTrue();
        dto.IgnorePatterns.ShouldBeEmpty();
        dto.FetchSettings.ShouldNotBeNull();
        dto.NotificationSettings.ShouldNotBeNull();
    }

    [Fact]
    public void FetchSettingsDto_HasDefaultValues()
    {
        // Act
        var dto = new FetchSettingsDto();

        // Assert
        dto.UseJavaScript.ShouldBeFalse();
        dto.TimeoutSeconds.ShouldBe(30);
        dto.CustomHeaders.ShouldBeEmpty();
        dto.CaptureScreenshot.ShouldBeFalse();
    }

    [Fact]
    public void NotificationSettingsDto_HasDefaultValues()
    {
        // Act
        var dto = new NotificationSettingsDto();

        // Assert
        dto.EmailEnabled.ShouldBeFalse();
        dto.EmailRecipients.ShouldBeEmpty();
        dto.WebhookEnabled.ShouldBeFalse();
        dto.MinimumImportanceToNotify.ShouldBe("Medium");
    }
}

public class ChangeDtoTests
{
    [Fact]
    public void ChangeListItemDto_HasDefaultValues()
    {
        // Act
        var dto = new ChangeListItemDto();

        // Assert
        dto.Id.ShouldBe("");
        dto.WatchId.ShouldBe("");
        dto.Summary.ShouldBe("");
        dto.Importance.ShouldBe("Low");
        dto.IsViewed.ShouldBeFalse();
        dto.IsNotified.ShouldBeFalse();
    }

    [Fact]
    public void ChangeDetailDto_HasDefaultValues()
    {
        // Act
        var dto = new ChangeDetailDto();

        // Assert
        dto.Id.ShouldBe("");
        dto.WatchId.ShouldBe("");
        dto.Summary.ShouldBe("");
        dto.Importance.ShouldBe("Low");
        dto.IsViewed.ShouldBeFalse();
    }

    [Fact]
    public void SnapshotInfoDto_HasDefaultValues()
    {
        // Act
        var dto = new SnapshotInfoDto();

        // Assert
        dto.Id.ShouldBe("");
        dto.Content.ShouldBe("");
    }
}

public class LlmDtoTests
{
    [Fact]
    public void ProcessInputRequest_CanSetInput()
    {
        // Arrange & Act
        var request = new ProcessInputRequest
        {
            Input = "Watch example.com"
        };

        // Assert
        request.Input.ShouldBe("Watch example.com");
    }

    [Fact]
    public void ProcessInputResponse_HasDefaultValues()
    {
        // Act
        var response = new ProcessInputResponse();

        // Assert
        response.IsSuccess.ShouldBeFalse();
        response.Intent.ShouldBe("Unknown");
        response.NeedsClarification.ShouldBeFalse();
        response.ClarificationQuestions.ShouldBeEmpty();
        response.Suggestions.ShouldBeEmpty();
    }

    [Fact]
    public void ParsedWatchRequestDto_HasDefaultValues()
    {
        // Act
        var dto = new ParsedWatchRequestDto();

        // Assert - all properties are nullable with null defaults
        dto.Url.ShouldBeNull();
        dto.UseJavaScript.ShouldBeNull();
        dto.Tags.ShouldBeNull();
    }

    [Fact]
    public void SuggestionChipDto_CanSetProperties()
    {
        // Arrange & Act
        var chip = new SuggestionChipDto
        {
            Label = "Add selector",
            Value = ".content",
            Type = "SetValue"
        };

        // Assert
        chip.Label.ShouldBe("Add selector");
        chip.Value.ShouldBe(".content");
        chip.Type.ShouldBe("SetValue");
    }
}

public class LlmProviderDtoTests
{
    [Fact]
    public void LlmProviderDto_HasDefaultValues()
    {
        // Act
        var dto = new LlmProviderDto();

        // Assert
        dto.Id.ShouldBe("");
        dto.ProviderType.ShouldBe("OpenAI");
        dto.IsEnabled.ShouldBeFalse();
        dto.IsHealthy.ShouldBeFalse();
    }

    [Fact]
    public void LlmProviderCreateDto_HasDefaultValues()
    {
        // Act
        var dto = new LlmProviderCreateDto();

        // Assert
        dto.ProviderType.ShouldBe("");
        dto.Priority.ShouldBe(1);
        dto.MaxTokens.ShouldBe(4096);
        dto.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public void LlmUsageStatsDto_HasDefaultValues()
    {
        // Act
        var dto = new LlmUsageStatsDto();

        // Assert
        dto.TotalRequests.ShouldBe(0);
        dto.SuccessCount.ShouldBe(0);
        dto.FailureCount.ShouldBe(0);
        dto.TotalCost.ShouldBe(0);
        dto.ByProvider.ShouldBeEmpty();
    }

    [Fact]
    public void ProviderUsageDto_HasDefaultValues()
    {
        // Act
        var dto = new ProviderUsageDto();

        // Assert
        dto.RequestCount.ShouldBe(0);
        dto.InputTokens.ShouldBe(0);
        dto.OutputTokens.ShouldBe(0);
        dto.Cost.ShouldBe(0);
    }
}
