using ChangeDetection.Core.Entities;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Entities;

[Category("Unit")]
public class WatchedSiteTests
{
    [Test]
    public async Task NewWatchedSite_HasDefaultValues()
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
        await Task.CompletedTask;
    }

    [Test]
    public async Task WatchedSite_UrlRequired()
    {
        // Act & Assert
        var site = new WatchedSite { Url = "https://example.com" };
        site.Url.ShouldNotBeNullOrEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task WatchedSite_CanSetAllProperties()
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
        await Task.CompletedTask;
    }

    [Test]
    public async Task WatchedSite_NotificationSettings_DefaultValues()
    {
        // Act
        var site = new WatchedSite { Url = "https://example.com" };

        // Assert
        site.Notifications.EmailEnabled.ShouldBeFalse();
        site.Notifications.WebhookEnabled.ShouldBeFalse();
        site.Notifications.DiscordEnabled.ShouldBeFalse();
        site.Notifications.UseLlmSummary.ShouldBeFalse();
        site.Notifications.MinimumImportance.ShouldBe(ChangeImportance.Low);
        await Task.CompletedTask;
    }

    [Test]
    public async Task WatchedSite_FetchSettings_DefaultValues()
    {
        // Act
        var site = new WatchedSite { Url = "https://example.com" };

        // Assert
        site.FetchSettings.UseJavaScript.ShouldBeFalse();
        site.FetchSettings.TimeoutSeconds.ShouldBe(30);
        site.FetchSettings.Headers.ShouldBeEmpty();
        site.FetchSettings.CaptureScreenshot.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task WatchedSite_Tags_CanBeModified()
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
        await Task.CompletedTask;
    }
}

public class ChangeEventTests
{
    [Test]
    public async Task NewChangeEvent_HasDefaultValues()
    {
        // Act
        var change = new ChangeEvent();

        // Assert
        change.Id.ShouldNotBe(Guid.Empty);
        change.DetectedAt.ShouldBeInRange(DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddSeconds(1));
        change.IsViewed.ShouldBeFalse();
        change.IsNotified.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ChangeEvent_CanSetProperties()
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
        await Task.CompletedTask;
    }
}

public class ChangeSnapshotTests
{
    [Test]
    public async Task NewChangeSnapshot_HasRequiredProperties()
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
        await Task.CompletedTask;
    }

    [Test]
    public async Task ChangeSnapshot_CanSetContent()
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
        await Task.CompletedTask;
    }
}

public class LlmProviderConfigTests
{
    [Test]
    public async Task NewLlmProviderConfig_HasDefaultValues()
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
        await Task.CompletedTask;
    }

    [Test]
    [Arguments(LlmProviderType.OpenAI)]
    [Arguments(LlmProviderType.Ollama)]
    [Arguments(LlmProviderType.Claude)]
    [Arguments(LlmProviderType.Gemini)]
    [Arguments(LlmProviderType.AzureOpenAI)]
    public async Task LlmProviderConfig_SupportsAllProviderTypes(LlmProviderType providerType)
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
        await Task.CompletedTask;
    }

    [Test]
    public async Task LlmProviderConfig_CostTracking()
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
        await Task.CompletedTask;
    }
}

public class AppSettingsTests
{
    [Test]
    public async Task NewAppSettings_HasDefaultValues()
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
        await Task.CompletedTask;
    }

    [Test]
    public async Task NewAppSettings_HasNewFieldDefaults()
    {
        // Act
        var settings = new AppSettings();

        // Assert - new fields added for extended settings
        settings.DefaultUserAgent.ShouldBeNull();
        settings.DefaultFetchTimeoutSeconds.ShouldBe(30);
        settings.EnableLlmDebugLogging.ShouldBeFalse();
        settings.MaxRetryAttempts.ShouldBe(3);
        settings.RetryDelaySeconds.ShouldBe(60);
        await Task.CompletedTask;
    }

    [Test]
    public async Task AppSettings_CanSetNewFields()
    {
        // Arrange & Act
        var settings = new AppSettings
        {
            DefaultUserAgent = "CustomBot/1.0",
            DefaultFetchTimeoutSeconds = 45,
            EnableLlmDebugLogging = true,
            MaxRetryAttempts = 5,
            RetryDelaySeconds = 120
        };

        // Assert
        settings.DefaultUserAgent.ShouldBe("CustomBot/1.0");
        settings.DefaultFetchTimeoutSeconds.ShouldBe(45);
        settings.EnableLlmDebugLogging.ShouldBeTrue();
        settings.MaxRetryAttempts.ShouldBe(5);
        settings.RetryDelaySeconds.ShouldBe(120);
        await Task.CompletedTask;
    }

    [Test]
    public async Task AppSettings_CanSetEmailSettings()
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
        await Task.CompletedTask;
    }
}

public class EmailSettingsTests
{
    [Test]
    public async Task EmailSettings_HasDefaultValues()
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
        await Task.CompletedTask;
    }
}

public class LlmUsageRecordTests
{
    [Test]
    public async Task NewLlmUsageRecord_HasDefaultId()
    {
        // Act
        var record = new LlmUsageRecord
        {
            ProviderName = "OpenAI",
            Model = "gpt-4"
        };

        // Assert
        record.Id.ShouldNotBe(Guid.Empty);
        await Task.CompletedTask;
    }

    [Test]
    public async Task LlmUsageRecord_CanSetAllProperties()
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
        await Task.CompletedTask;
    }

    [Test]
    [Arguments(LlmUsageType.IntentClassification)]
    [Arguments(LlmUsageType.ChangeSummary)]
    [Arguments(LlmUsageType.NotificationGeneration)]
    [Arguments(LlmUsageType.EntityExtraction)]
    [Arguments(LlmUsageType.Validation)]
    [Arguments(LlmUsageType.Other)]
    public async Task LlmUsageRecord_SupportsAllUsageTypes(LlmUsageType usageType)
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
        await Task.CompletedTask;
    }
}

public class FetchSettingsTests
{
    [Test]
    public async Task NewFetchSettings_HasDefaultValues()
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
        await Task.CompletedTask;
    }

    [Test]
    public async Task FetchSettings_CanSetAllProperties()
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
        await Task.CompletedTask;
    }
}

