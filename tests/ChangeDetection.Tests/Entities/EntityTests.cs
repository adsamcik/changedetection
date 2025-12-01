using ChangeDetection.Core.Entities;
using Shouldly;

namespace ChangeDetection.Tests.Entities;

public class WatchedSiteTests
{
    [Fact]
    public void NewWatchedSite_HasDefaultValues()
    {
        // Act
        var site = new WatchedSite { Url = "https://example.com" };

        // Assert
        site.Id.ShouldNotBe(Guid.Empty);
        site.Status.ShouldBe(WatchStatus.Active);
        site.IsEnabled.ShouldBeTrue();
        site.CheckInterval.ShouldBe(TimeSpan.FromMinutes(30));
        site.Tags.ShouldBeEmpty();
        site.Notifications.ShouldNotBeNull();
        site.FetchSettings.ShouldNotBeNull();
        site.CreatedAt.ShouldBeInRange(DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void WatchedSite_UrlRequired()
    {
        // Act & Assert
        var site = new WatchedSite { Url = "https://example.com" };
        site.Url.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void WatchedSite_CanSetAllProperties()
    {
        // Arrange
        var id = Guid.NewGuid();
        var url = "https://test.com";
        var name = "Test Watch";
        var cssSelector = ".content";
        var xpathSelector = "//div[@id='main']";
        var checkInterval = TimeSpan.FromHours(1);

        // Act
        var site = new WatchedSite
        {
            Id = id,
            Url = url,
            Name = name,
            CssSelector = cssSelector,
            XPathSelector = xpathSelector,
            CheckInterval = checkInterval,
            IsEnabled = false,
            Status = WatchStatus.Paused
        };

        // Assert
        site.Id.ShouldBe(id);
        site.Url.ShouldBe(url);
        site.Name.ShouldBe(name);
        site.CssSelector.ShouldBe(cssSelector);
        site.XPathSelector.ShouldBe(xpathSelector);
        site.CheckInterval.ShouldBe(checkInterval);
        site.IsEnabled.ShouldBeFalse();
        site.Status.ShouldBe(WatchStatus.Paused);
    }

    [Fact]
    public void WatchedSite_NotificationSettings_DefaultValues()
    {
        // Act
        var site = new WatchedSite { Url = "https://example.com" };

        // Assert
        site.Notifications.EmailEnabled.ShouldBeFalse();
        site.Notifications.WebhookEnabled.ShouldBeFalse();
        site.Notifications.DiscordEnabled.ShouldBeFalse();
        site.Notifications.UseLlmSummary.ShouldBeFalse();
        site.Notifications.MinimumImportance.ShouldBe(ChangeImportance.Low);
    }

    [Fact]
    public void WatchedSite_FetchSettings_DefaultValues()
    {
        // Act
        var site = new WatchedSite { Url = "https://example.com" };

        // Assert
        site.FetchSettings.UseJavaScript.ShouldBeFalse();
        site.FetchSettings.TimeoutSeconds.ShouldBe(30);
        site.FetchSettings.Headers.ShouldBeEmpty();
        site.FetchSettings.CaptureScreenshot.ShouldBeFalse();
    }

    [Fact]
    public void WatchedSite_Tags_CanBeModified()
    {
        // Arrange
        var site = new WatchedSite { Url = "https://example.com" };

        // Act
        site.Tags.Add("news");
        site.Tags.Add("tech");

        // Assert
        site.Tags.Count.ShouldBe(2);
        site.Tags.ShouldContain("news");
        site.Tags.ShouldContain("tech");
    }
}

public class ChangeEventTests
{
    [Fact]
    public void NewChangeEvent_HasDefaultValues()
    {
        // Act
        var change = new ChangeEvent();

        // Assert
        change.Id.ShouldNotBe(Guid.Empty);
        change.DetectedAt.ShouldBeInRange(DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddSeconds(1));
        change.IsViewed.ShouldBeFalse();
        change.IsNotified.ShouldBeFalse();
    }

    [Fact]
    public void ChangeEvent_CanSetProperties()
    {
        // Arrange
        var watchId = Guid.NewGuid();
        var prevSnapshotId = Guid.NewGuid();
        var currSnapshotId = Guid.NewGuid();

        // Act
        var change = new ChangeEvent
        {
            WatchedSiteId = watchId,
            PreviousSnapshotId = prevSnapshotId,
            CurrentSnapshotId = currSnapshotId,
            ChangeType = ChangeType.Modified,
            Importance = ChangeImportance.High,
            LinesAdded = 10,
            LinesRemoved = 5,
            DiffSummary = "Content updated",
            DiffHtml = "<div>diff</div>"
        };

        // Assert
        change.WatchedSiteId.ShouldBe(watchId);
        change.PreviousSnapshotId.ShouldBe(prevSnapshotId);
        change.CurrentSnapshotId.ShouldBe(currSnapshotId);
        change.ChangeType.ShouldBe(ChangeType.Modified);
        change.Importance.ShouldBe(ChangeImportance.High);
        change.LinesAdded.ShouldBe(10);
        change.LinesRemoved.ShouldBe(5);
    }
}

public class ChangeSnapshotTests
{
    [Fact]
    public void NewChangeSnapshot_HasRequiredProperties()
    {
        // Act
        var snapshot = new ChangeSnapshot
        {
            Content = "test",
            ContentHash = "hash"
        };

        // Assert
        snapshot.Id.ShouldNotBe(Guid.Empty);
        snapshot.CapturedAt.ShouldBeInRange(DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void ChangeSnapshot_CanSetContent()
    {
        // Arrange
        var content = "Page content here";
        var hash = "abc123";

        // Act
        var snapshot = new ChangeSnapshot
        {
            Content = content,
            ContentHash = hash
        };

        // Assert
        snapshot.Content.ShouldBe(content);
        snapshot.ContentHash.ShouldBe(hash);
    }
}

public class LlmProviderConfigTests
{
    [Fact]
    public void NewLlmProviderConfig_HasDefaultValues()
    {
        // Act
        var config = new LlmProviderConfig
        {
            Name = "Test Provider",
            Model = "gpt-4"
        };

        // Assert
        config.Id.ShouldNotBe(Guid.Empty);
        config.IsEnabled.ShouldBeTrue();
        config.IsHealthy.ShouldBeTrue();
        config.MaxRetries.ShouldBe(3);
        config.TimeoutSeconds.ShouldBe(60);
        config.TotalTokensUsed.ShouldBe(0);
        config.TotalCost.ShouldBe(0);
    }

    [Theory]
    [InlineData(LlmProviderType.OpenAI)]
    [InlineData(LlmProviderType.Ollama)]
    [InlineData(LlmProviderType.Claude)]
    [InlineData(LlmProviderType.Gemini)]
    [InlineData(LlmProviderType.AzureOpenAI)]
    public void LlmProviderConfig_SupportsAllProviderTypes(LlmProviderType providerType)
    {
        // Act
        var config = new LlmProviderConfig
        {
            Name = "Test",
            Model = "test-model",
            ProviderType = providerType
        };

        // Assert
        config.ProviderType.ShouldBe(providerType);
    }

    [Fact]
    public void LlmProviderConfig_CostTracking()
    {
        // Arrange
        var config = new LlmProviderConfig
        {
            Name = "Test",
            Model = "gpt-4",
            CostPer1KInputTokens = 0.03m,
            CostPer1KOutputTokens = 0.06m
        };

        // Assert
        config.CostPer1KInputTokens.ShouldBe(0.03m);
        config.CostPer1KOutputTokens.ShouldBe(0.06m);
    }
}

public class AppSettingsTests
{
    [Fact]
    public void NewAppSettings_HasDefaultValues()
    {
        // Act
        var settings = new AppSettings();

        // Assert
        settings.Id.ShouldNotBe(Guid.Empty);
        settings.DefaultCheckInterval.ShouldBe(TimeSpan.FromMinutes(30));
        settings.MaxConcurrentChecks.ShouldBe(5);
        settings.SnapshotRetentionDays.ShouldBe(30);
        settings.ChangeEventRetentionDays.ShouldBe(90);
        settings.UseLlmForSummaries.ShouldBeTrue();
        settings.MaxPlaywrightInstances.ShouldBe(3);
        settings.Email.ShouldBeNull();
    }

    [Fact]
    public void AppSettings_CanSetEmailSettings()
    {
        // Arrange & Act
        var settings = new AppSettings
        {
            Email = new EmailSettings
            {
                SmtpHost = "smtp.example.com",
                SmtpPort = 465,
                UseSsl = true,
                Username = "user@example.com",
                Password = "secret",
                FromAddress = "noreply@example.com",
                FromName = "Test App"
            }
        };

        // Assert
        settings.Email.ShouldNotBeNull();
        settings.Email.SmtpHost.ShouldBe("smtp.example.com");
        settings.Email.SmtpPort.ShouldBe(465);
        settings.Email.UseSsl.ShouldBeTrue();
        settings.Email.Username.ShouldBe("user@example.com");
        settings.Email.FromAddress.ShouldBe("noreply@example.com");
        settings.Email.FromName.ShouldBe("Test App");
    }
}

public class EmailSettingsTests
{
    [Fact]
    public void EmailSettings_HasDefaultValues()
    {
        // Act
        var settings = new EmailSettings();

        // Assert
        settings.SmtpPort.ShouldBe(587);
        settings.UseSsl.ShouldBeTrue();
        settings.FromName.ShouldBe("Change Detection");
        settings.SmtpHost.ShouldBeNull();
        settings.Username.ShouldBeNull();
        settings.Password.ShouldBeNull();
        settings.FromAddress.ShouldBeNull();
    }
}

public class LlmUsageRecordTests
{
    [Fact]
    public void NewLlmUsageRecord_HasDefaultId()
    {
        // Act
        var record = new LlmUsageRecord
        {
            ProviderName = "OpenAI",
            Model = "gpt-4"
        };

        // Assert
        record.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void LlmUsageRecord_CanSetAllProperties()
    {
        // Arrange
        var providerId = Guid.NewGuid();
        var watchId = Guid.NewGuid();

        // Act
        var record = new LlmUsageRecord
        {
            ProviderId = providerId,
            ProviderName = "OpenAI",
            Model = "gpt-4",
            UsageType = LlmUsageType.ChangeSummary,
            WatchedSiteId = watchId,
            InputTokens = 500,
            OutputTokens = 150,
            Cost = 0.02m,
            DurationMs = 1500,
            IsSuccess = true
        };

        // Assert
        record.ProviderId.ShouldBe(providerId);
        record.ProviderName.ShouldBe("OpenAI");
        record.Model.ShouldBe("gpt-4");
        record.UsageType.ShouldBe(LlmUsageType.ChangeSummary);
        record.WatchedSiteId.ShouldBe(watchId);
        record.InputTokens.ShouldBe(500);
        record.OutputTokens.ShouldBe(150);
        record.Cost.ShouldBe(0.02m);
        record.DurationMs.ShouldBe(1500);
        record.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(LlmUsageType.IntentClassification)]
    [InlineData(LlmUsageType.ChangeSummary)]
    [InlineData(LlmUsageType.NotificationGeneration)]
    [InlineData(LlmUsageType.EntityExtraction)]
    [InlineData(LlmUsageType.Validation)]
    [InlineData(LlmUsageType.Other)]
    public void LlmUsageRecord_SupportsAllUsageTypes(LlmUsageType usageType)
    {
        // Act
        var record = new LlmUsageRecord
        {
            ProviderName = "Test",
            Model = "test-model",
            UsageType = usageType
        };

        // Assert
        record.UsageType.ShouldBe(usageType);
    }
}

public class FetchSettingsTests
{
    [Fact]
    public void NewFetchSettings_HasDefaultValues()
    {
        // Act
        var settings = new FetchSettings();

        // Assert
        settings.UseJavaScript.ShouldBeFalse();
        settings.TimeoutSeconds.ShouldBe(30);
        settings.UserAgent.ShouldBeNull();
        settings.ProxyUrl.ShouldBeNull();
        settings.WaitForSelector.ShouldBeNull();
        settings.WaitAfterLoadMs.ShouldBe(0);
        settings.CaptureScreenshot.ShouldBeFalse();
        settings.ViewportWidth.ShouldBe(1920);
        settings.ViewportHeight.ShouldBe(1080);
        settings.Headers.ShouldBeEmpty();
    }

    [Fact]
    public void FetchSettings_CanSetAllProperties()
    {
        // Arrange & Act
        var settings = new FetchSettings
        {
            UseJavaScript = true,
            TimeoutSeconds = 60,
            UserAgent = "Custom Agent",
            ProxyUrl = "http://proxy:8080",
            WaitForSelector = "#content",
            WaitAfterLoadMs = 2000,
            CaptureScreenshot = true,
            ViewportWidth = 1280,
            ViewportHeight = 720
        };
        settings.Headers["X-Custom"] = "value";

        // Assert
        settings.UseJavaScript.ShouldBeTrue();
        settings.TimeoutSeconds.ShouldBe(60);
        settings.UserAgent.ShouldBe("Custom Agent");
        settings.ProxyUrl.ShouldBe("http://proxy:8080");
        settings.WaitForSelector.ShouldBe("#content");
        settings.WaitAfterLoadMs.ShouldBe(2000);
        settings.CaptureScreenshot.ShouldBeTrue();
        settings.ViewportWidth.ShouldBe(1280);
        settings.ViewportHeight.ShouldBe(720);
        settings.Headers["X-Custom"].ShouldBe("value");
    }
}

