using ChangeDetection.Core.Entities;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Entities;

// WatchedSiteTests, ChangeEventTests, ChangeSnapshotTests moved to ChangeDetection.Core.Tests

[Category("Unit")]
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

}

