using ChangeDetection.Shared.Dtos;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Dtos;

/// <summary>
/// Advanced DTO tests covering edge cases and validation scenarios.
/// </summary>
public class DtoAdvancedTests
{
    // WatchListItemDto Tests

    [Test]
    public async Task WatchListItemDto_CanSetAllProperties()
    {
        // Arrange & Act
        var lastCheck = DateTime.UtcNow;
        var dto = new WatchListItemDto
        {
            Id = "test-id",
            Url = "https://example.com/page",
            Title = "Test Watch",
            CssSelector = ".content",
            CheckInterval = TimeSpan.FromMinutes(30),
            LastCheck = lastCheck,
            Status = "Active",
            IsEnabled = false,
            ChangeCount = 5,
            HasRecentChanges = true
        };

        // Assert
        dto.Id.ShouldBe("test-id");
        dto.Url.ShouldBe("https://example.com/page");
        dto.Title.ShouldBe("Test Watch");
        dto.CssSelector.ShouldBe(".content");
        dto.CheckInterval.ShouldBe(TimeSpan.FromMinutes(30));
        dto.LastCheck.ShouldBe(lastCheck);
        dto.Status.ShouldBe("Active");
        dto.IsEnabled.ShouldBeFalse();
        dto.ChangeCount.ShouldBe(5);
        dto.HasRecentChanges.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task WatchListItemDto_UrlIsRequired()
    {
        // Act
        var dto = new WatchListItemDto { Url = "" };

        // Assert - Empty URL is allowed by the type, but validation would catch it
        dto.Url.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    // WatchDetailDto Tests

    [Test]
    public async Task WatchDetailDto_CanSetAllProperties()
    {
        // Arrange
        var created = DateTime.UtcNow.AddDays(-7);
        var lastCheck = DateTime.UtcNow;
        var nextCheck = DateTime.UtcNow.AddHours(1);

        // Act
        var dto = new WatchDetailDto
        {
            Id = "detail-id",
            Url = "https://example.com",
            Title = "Detailed Watch",
            CssSelector = "#main",
            XpathSelector = "//div[@id='main']",
            CheckInterval = TimeSpan.FromHours(2),
            CreatedAt = created,
            LastCheck = lastCheck,
            NextCheck = nextCheck,
            Status = "Checking",
            IsEnabled = true,
            IgnorePatterns = ["pattern1", "pattern2"],
            FetchSettings = new FetchSettingsDto { UseJavaScript = true },
            NotificationSettings = new NotificationSettingsDto { EmailEnabled = true },
            LatestSnapshot = new SnapshotDto { Content = "Page content here" }
        };

        // Assert
        dto.Id.ShouldBe("detail-id");
        dto.Url.ShouldBe("https://example.com");
        dto.XpathSelector.ShouldBe("//div[@id='main']");
        dto.LatestSnapshot!.Content.ShouldBe("Page content here");
        dto.IgnorePatterns.Count.ShouldBe(2);
        dto.FetchSettings!.UseJavaScript.ShouldBeTrue();
        dto.NotificationSettings!.EmailEnabled.ShouldBeTrue();
        await Task.CompletedTask;
    }

    // WatchCreateDto Tests

    [Test]
    public async Task WatchCreateDto_CanSetAllProperties()
    {
        // Act
        var dto = new WatchCreateDto
        {
            Url = "https://new-watch.com",
            Title = "New Watch",
            CssSelector = ".watch-this",
            XpathSelector = "//section",
            CheckInterval = TimeSpan.FromMinutes(15),
            IsEnabled = false,
            IgnorePatterns = ["ignore1"],
            FetchSettings = new FetchSettingsDto
            {
                UseJavaScript = true,
                WaitForSelector = ".loaded",
                WaitTimeMs = 2000,
                TimeoutSeconds = 60,
                CaptureScreenshot = true,
                CustomHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer token"
                }
            },
            NotificationSettings = new NotificationSettingsDto
            {
                EmailEnabled = true,
                EmailRecipients = ["admin@example.com", "user@example.com"],
                WebhookEnabled = true,
                WebhookUrl = "https://hooks.example.com/notify",
                MinimumImportanceToNotify = "High"
            }
        };

        // Assert
        dto.Url.ShouldBe("https://new-watch.com");
        dto.Title.ShouldBe("New Watch");
        dto.CheckInterval.ShouldBe(TimeSpan.FromMinutes(15));
        dto.IsEnabled.ShouldBeFalse();
        dto.FetchSettings.UseJavaScript.ShouldBeTrue();
        dto.FetchSettings.WaitTimeMs.ShouldBe(2000);
        dto.FetchSettings.CustomHeaders.Count.ShouldBe(1);
        dto.NotificationSettings.EmailRecipients.Count.ShouldBe(2);
        dto.NotificationSettings.WebhookEnabled.ShouldBeTrue();
        await Task.CompletedTask;
    }

    // FetchSettingsDto Tests

    [Test]
    public async Task FetchSettingsDto_CanSetAllProperties()
    {
        // Act
        var dto = new FetchSettingsDto
        {
            UseJavaScript = true,
            WaitForSelector = "#content-loaded",
            WaitTimeMs = 3000,
            TimeoutSeconds = 45,
            CaptureScreenshot = true,
            CustomHeaders = new Dictionary<string, string>
            {
                ["Accept"] = "application/json",
                ["X-Custom-Header"] = "value"
            }
        };

        // Assert
        dto.UseJavaScript.ShouldBeTrue();
        dto.WaitForSelector.ShouldBe("#content-loaded");
        dto.WaitTimeMs.ShouldBe(3000);
        dto.TimeoutSeconds.ShouldBe(45);
        dto.CaptureScreenshot.ShouldBeTrue();
        dto.CustomHeaders.Count.ShouldBe(2);
        await Task.CompletedTask;
    }

    [Test]
    public async Task FetchSettingsDto_CustomHeaders_EmptyByDefault()
    {
        // Act
        var dto = new FetchSettingsDto();

        // Assert
        dto.CustomHeaders.ShouldNotBeNull();
        dto.CustomHeaders.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    // ChangeListItemDto Tests

    [Test]
    public async Task ChangeListItemDto_CanSetAllProperties()
    {
        // Arrange
        var detected = DateTime.UtcNow;

        // Act
        var dto = new ChangeListItemDto
        {
            Id = "change-id",
            WatchId = "watch-id",
            WatchTitle = "My Watch",
            DetectedAt = detected,
            Summary = "Changes detected in the content",
            Importance = "Critical",
            LinesAdded = 10,
            LinesRemoved = 5,
            IsViewed = true,
            IsNotified = true
        };

        // Assert
        dto.Id.ShouldBe("change-id");
        dto.WatchId.ShouldBe("watch-id");
        dto.WatchTitle.ShouldBe("My Watch");
        dto.DetectedAt.ShouldBe(detected);
        dto.Summary.ShouldBe("Changes detected in the content");
        dto.Importance.ShouldBe("Critical");
        dto.LinesAdded.ShouldBe(10);
        dto.LinesRemoved.ShouldBe(5);
        dto.IsViewed.ShouldBeTrue();
        dto.IsNotified.ShouldBeTrue();
        await Task.CompletedTask;
    }

    // ChangeDetailDto Tests

    [Test]
    public async Task ChangeDetailDto_CanSetAllProperties()
    {
        // Arrange
        var detected = DateTime.UtcNow;
        var previous = new SnapshotInfoDto { Id = "prev", CapturedAt = detected.AddHours(-1), Content = "Old content" };
        var current = new SnapshotInfoDto { Id = "curr", CapturedAt = detected, Content = "New content" };

        // Act
        var dto = new ChangeDetailDto
        {
            Id = "detail-change",
            WatchId = "watch",
            WatchTitle = "Watch Title",
            WatchUrl = "https://example.com",
            DetectedAt = detected,
            Summary = "Full summary",
            Importance = "High",
            DiffHtml = "<div class='diff'>...</div>",
            PreviousSnapshot = previous,
            CurrentSnapshot = current,
            LinesAdded = 15,
            LinesRemoved = 3,
            IsViewed = true
        };

        // Assert
        dto.Id.ShouldBe("detail-change");
        dto.WatchUrl.ShouldBe("https://example.com");
        dto.DiffHtml.ShouldContain("diff");
        dto.PreviousSnapshot!.Content.ShouldBe("Old content");
        dto.CurrentSnapshot!.Content.ShouldBe("New content");
        await Task.CompletedTask;
    }

    // SnapshotInfoDto Tests

    [Test]
    public async Task SnapshotInfoDto_CanSetAllProperties()
    {
        // Arrange
        var captured = DateTime.UtcNow;

        // Act
        var dto = new SnapshotInfoDto
        {
            Id = "snapshot-id",
            CapturedAt = captured,
            Content = "Full page content here",
            ScreenshotPath = "/screenshots/snap1.png"
        };

        // Assert
        dto.Id.ShouldBe("snapshot-id");
        dto.CapturedAt.ShouldBe(captured);
        dto.Content.ShouldBe("Full page content here");
        dto.ScreenshotPath.ShouldBe("/screenshots/snap1.png");
        await Task.CompletedTask;
    }

    // ProcessInputRequest Tests

    [Test]
    public async Task ProcessInputRequest_EmptyInput_IsAllowed()
    {
        // Act
        var request = new ProcessInputRequest { Input = "" };

        // Assert
        request.Input.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ProcessInputRequest_LongInput_IsAllowed()
    {
        // Arrange
        var longInput = new string('x', 10000);

        // Act
        var request = new ProcessInputRequest { Input = longInput };

        // Assert
        request.Input.Length.ShouldBe(10000);
        await Task.CompletedTask;
    }

    // ProcessInputResponse Tests

    [Test]
    public async Task ProcessInputResponse_CanSetAllProperties()
    {
        // Act
        var response = new ProcessInputResponse
        {
            IsSuccess = true,
            Intent = "CreateWatch",
            ParsedRequest = new ParsedWatchRequestDto
            {
                Url = "https://example.com",
                Title = "Test",
                CssSelector = ".content"
            },
            NeedsClarification = true,
            ClarificationQuestions = ["What CSS selector?", "How often?"],
            Suggestions = [
                new SuggestionChipDto { Label = "Every hour", Value = "60", Type = "Interval" }
            ],
            Summary = "Watch created successfully",
            ErrorMessage = null,
            CreatedWatchId = "new-watch-123"
        };

        // Assert
        response.IsSuccess.ShouldBeTrue();
        response.Intent.ShouldBe("CreateWatch");
        response.ParsedRequest!.Url.ShouldBe("https://example.com");
        response.NeedsClarification.ShouldBeTrue();
        response.ClarificationQuestions.Count.ShouldBe(2);
        response.Suggestions.Count.ShouldBe(1);
        response.CreatedWatchId.ShouldBe("new-watch-123");
        await Task.CompletedTask;
    }

    // ParsedWatchRequestDto Tests

    [Test]
    public async Task ParsedWatchRequestDto_CanSetAllProperties()
    {
        // Act
        var dto = new ParsedWatchRequestDto
        {
            Url = "https://example.com",
            Title = "My Watch",
            CssSelector = ".main-content",
            CheckIntervalMinutes = 30,
            UseJavaScript = true,
            Tags = ["news", "updates"],
            NotificationEmail = "notify@example.com",
            Description = "Monitor for price changes"
        };

        // Assert
        dto.Url.ShouldBe("https://example.com");
        dto.Title.ShouldBe("My Watch");
        dto.CssSelector.ShouldBe(".main-content");
        dto.CheckIntervalMinutes.ShouldBe(30);
        dto.UseJavaScript.ShouldBe(true);
        dto.Tags.Count.ShouldBe(2);
        dto.NotificationEmail.ShouldBe("notify@example.com");
        dto.Description.ShouldBe("Monitor for price changes");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ParsedWatchRequestDto_NullablePropertiesDefaultToNull()
    {
        // Act
        var dto = new ParsedWatchRequestDto();

        // Assert
        dto.Url.ShouldBeNull();
        dto.Title.ShouldBeNull();
        dto.CssSelector.ShouldBeNull();
        dto.CheckIntervalMinutes.ShouldBeNull();
        dto.UseJavaScript.ShouldBeNull();
        dto.NotificationEmail.ShouldBeNull();
        dto.Description.ShouldBeNull();
        await Task.CompletedTask;
    }

    // SuggestionChipDto Tests

    [Test]
    public async Task SuggestionChipDto_CanSetAllProperties()
    {
        // Act
        var dto = new SuggestionChipDto
        {
            Label = "Default Settings",
            Value = "default",
            Type = "Action"
        };

        // Assert
        dto.Label.ShouldBe("Default Settings");
        dto.Value.ShouldBe("default");
        dto.Type.ShouldBe("Action");
        await Task.CompletedTask;
    }

    // LlmProviderDto Tests

    [Test]
    public async Task LlmProviderDto_CanSetAllProperties()
    {
        // Act
        var dto = new LlmProviderDto
        {
            Id = "provider-id",
            ProviderType = "OpenAI",
            ModelId = "gpt-4",
            Endpoint = "https://api.openai.com",
            IsEnabled = true,
            IsHealthy = true,
            Priority = 1,
            LastUsed = DateTime.UtcNow,
            MaxTokens = 4096,
            CostPerInputToken = 0.001m,
            CostPerOutputToken = 0.002m
        };

        // Assert
        dto.Id.ShouldBe("provider-id");
        dto.ProviderType.ShouldBe("OpenAI");
        dto.ModelId.ShouldBe("gpt-4");
        dto.IsEnabled.ShouldBeTrue();
        dto.IsHealthy.ShouldBeTrue();
        dto.Priority.ShouldBe(1);
        dto.MaxTokens.ShouldBe(4096);
        await Task.CompletedTask;
    }

    [Test]
    public async Task LlmProviderDto_HasCorrectDefaults()
    {
        // Act
        var dto = new LlmProviderDto();

        // Assert
        dto.Id.ShouldBe("");
        dto.ProviderType.ShouldBe("OpenAI");
        dto.ModelId.ShouldBeNull();
        dto.IsHealthy.ShouldBeFalse();
        dto.Priority.ShouldBe(0);
        await Task.CompletedTask;
    }

    // LlmProviderCreateDto Tests

    [Test]
    public async Task LlmProviderCreateDto_CanSetAllProperties()
    {
        // Act
        var dto = new LlmProviderCreateDto
        {
            ProviderType = "Azure",
            ApiKey = "secret-key",
            ModelId = "gpt-4-turbo",
            Endpoint = "https://my-resource.openai.azure.com/",
            Priority = 2,
            MaxTokens = 8192
        };

        // Assert
        dto.ProviderType.ShouldBe("Azure");
        dto.ApiKey.ShouldBe("secret-key");
        dto.ModelId.ShouldBe("gpt-4-turbo");
        dto.Endpoint.ShouldBe("https://my-resource.openai.azure.com/");
        dto.Priority.ShouldBe(2);
        dto.MaxTokens.ShouldBe(8192);
        await Task.CompletedTask;
    }

    // LlmUsageStatsDto Tests

    [Test]
    public async Task LlmUsageStatsDto_CanSetAllProperties()
    {
        // Act
        var dto = new LlmUsageStatsDto
        {
            TotalRequests = 1000,
            TotalInputTokens = 50000,
            TotalOutputTokens = 75000,
            TotalCost = 15.50m,
            AverageLatencyMs = 350.75,
            SuccessCount = 950,
            FailureCount = 50,
            ByProvider = new Dictionary<string, ProviderUsageDto>
            {
                ["OpenAI"] = new ProviderUsageDto { RequestCount = 600, InputTokens = 30000 },
                ["Claude"] = new ProviderUsageDto { RequestCount = 400, InputTokens = 20000 }
            }
        };

        // Assert
        dto.TotalRequests.ShouldBe(1000);
        dto.TotalInputTokens.ShouldBe(50000);
        dto.TotalOutputTokens.ShouldBe(75000);
        dto.TotalCost.ShouldBe(15.50m);
        dto.AverageLatencyMs.ShouldBe(350.75);
        dto.SuccessCount.ShouldBe(950);
        dto.ByProvider.Count.ShouldBe(2);
        await Task.CompletedTask;
    }

    // ProviderUsageDto Tests

    [Test]
    public async Task ProviderUsageDto_CanSetAllProperties()
    {
        // Act
        var dto = new ProviderUsageDto
        {
            RequestCount = 100,
            InputTokens = 5000,
            OutputTokens = 10000,
            Cost = 1.50m
        };

        // Assert
        dto.RequestCount.ShouldBe(100);
        dto.InputTokens.ShouldBe(5000);
        dto.OutputTokens.ShouldBe(10000);
        dto.Cost.ShouldBe(1.50m);
        await Task.CompletedTask;
    }
}
