using ChangeDetection.Shared.Dtos;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Dtos;

public class WatchDtoTests
{
    [Test]
    public async Task WatchListItemDto_HasDefaultValues()
    {
        // Act
        var dto = new WatchListItemDto { Url = "https://example.com" };

        // Assert
        dto.Id.ShouldBe("");
        dto.Status.ShouldBe("Idle");
        dto.IsEnabled.ShouldBeTrue();
        dto.ChangeCount.ShouldBe(0);
        dto.HasRecentChanges.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task WatchDetailDto_HasDefaultValues()
    {
        // Act
        var dto = new WatchDetailDto { Url = "https://example.com" };

        // Assert
        dto.Id.ShouldBe("");
        dto.Status.ShouldBe("Idle");
        dto.IsEnabled.ShouldBeTrue();
        dto.IgnorePatterns.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task WatchCreateDto_HasDefaultValues()
    {
        // Act
        var dto = new WatchCreateDto { Url = "https://example.com" };

        // Assert
        dto.CheckInterval.ShouldBe(TimeSpan.FromHours(1));
        dto.IsEnabled.ShouldBeTrue();
        dto.IgnorePatterns.ShouldBeEmpty();
        dto.FetchSettings.ShouldNotBeNull();
        dto.NotificationSettings.ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task FetchSettingsDto_HasDefaultValues()
    {
        // Act
        var dto = new FetchSettingsDto();

        // Assert
        dto.UseJavaScript.ShouldBeFalse();
        dto.TimeoutSeconds.ShouldBe(30);
        dto.CustomHeaders.ShouldBeEmpty();
        dto.CaptureScreenshot.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task NotificationSettingsDto_HasDefaultValues()
    {
        // Act
        var dto = new NotificationSettingsDto();

        // Assert
        dto.EmailEnabled.ShouldBeFalse();
        dto.EmailRecipients.ShouldBeEmpty();
        dto.WebhookEnabled.ShouldBeFalse();
        dto.MinimumImportanceToNotify.ShouldBe("Medium");
        await Task.CompletedTask;
    }
}

public class ChangeDtoTests
{
    [Test]
    public async Task ChangeListItemDto_HasDefaultValues()
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
        await Task.CompletedTask;
    }

    [Test]
    public async Task ChangeDetailDto_HasDefaultValues()
    {
        // Act
        var dto = new ChangeDetailDto();

        // Assert
        dto.Id.ShouldBe("");
        dto.WatchId.ShouldBe("");
        dto.Summary.ShouldBe("");
        dto.Importance.ShouldBe("Low");
        dto.IsViewed.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task SnapshotInfoDto_HasDefaultValues()
    {
        // Act
        var dto = new SnapshotInfoDto();

        // Assert
        dto.Id.ShouldBe("");
        dto.Content.ShouldBe("");
        await Task.CompletedTask;
    }
}

public class LlmDtoTests
{
    [Test]
    public async Task ProcessInputRequest_CanSetInput()
    {
        // Arrange & Act
        var request = new ProcessInputRequest
        {
            Input = "Watch example.com"
        };

        // Assert
        request.Input.ShouldBe("Watch example.com");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ProcessInputResponse_HasDefaultValues()
    {
        // Act
        var response = new ProcessInputResponse();

        // Assert
        response.IsSuccess.ShouldBeFalse();
        response.Intent.ShouldBe("Unknown");
        response.NeedsClarification.ShouldBeFalse();
        response.ClarificationQuestions.ShouldBeEmpty();
        response.Suggestions.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ParsedWatchRequestDto_HasDefaultValues()
    {
        // Act
        var dto = new ParsedWatchRequestDto();

        // Assert - all properties are nullable with null defaults
        dto.Url.ShouldBeNull();
        dto.UseJavaScript.ShouldBeNull();
        dto.Tags.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task SuggestionChipDto_CanSetProperties()
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
        await Task.CompletedTask;
    }
}

public class LlmProviderDtoTests
{
    [Test]
    public async Task LlmProviderDto_HasDefaultValues()
    {
        // Act
        var dto = new LlmProviderDto();

        // Assert
        dto.Id.ShouldBe("");
        dto.ProviderType.ShouldBe("OpenAI");
        dto.IsEnabled.ShouldBeFalse();
        dto.IsHealthy.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task LlmProviderCreateDto_HasDefaultValues()
    {
        // Act
        var dto = new LlmProviderCreateDto();

        // Assert
        dto.ProviderType.ShouldBe("");
        dto.Priority.ShouldBe(1);
        dto.MaxTokens.ShouldBe(4096);
        dto.IsEnabled.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task LlmUsageStatsDto_HasDefaultValues()
    {
        // Act
        var dto = new LlmUsageStatsDto();

        // Assert
        dto.TotalRequests.ShouldBe(0);
        dto.SuccessCount.ShouldBe(0);
        dto.FailureCount.ShouldBe(0);
        dto.TotalCost.ShouldBe(0);
        dto.ByProvider.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ProviderUsageDto_HasDefaultValues()
    {
        // Act
        var dto = new ProviderUsageDto();

        // Assert
        dto.RequestCount.ShouldBe(0);
        dto.InputTokens.ShouldBe(0);
        dto.OutputTokens.ShouldBe(0);
        dto.Cost.ShouldBe(0);
        await Task.CompletedTask;
    }
}
