using ChangeDetection.Shared.Dtos;
using Shouldly;

namespace ChangeDetection.Tests.Dtos;

public class GeneralSettingsDtoTests
{
    [Fact]
    public void GeneralSettingsDto_HasDefaultValues()
    {
        // Act
        var dto = new GeneralSettingsDto();

        // Assert
        dto.DefaultCheckIntervalMinutes.ShouldBe(60);
        dto.MaxConcurrentChecks.ShouldBe(5);
        dto.SnapshotRetentionDays.ShouldBe(30);
        dto.ChangeEventRetentionDays.ShouldBe(90);
        dto.MaxPlaywrightInstances.ShouldBe(3);
        dto.UseLlmForSummaries.ShouldBeTrue();
        dto.DefaultUserAgent.ShouldBeNull();
        dto.DefaultFetchTimeoutSeconds.ShouldBe(30);
        dto.EnableLlmDebugLogging.ShouldBeFalse();
        dto.MaxRetryAttempts.ShouldBe(3);
        dto.RetryDelaySeconds.ShouldBe(60);
    }

    [Fact]
    public void GeneralSettingsDto_CanSetAllProperties()
    {
        // Act
        var dto = new GeneralSettingsDto
        {
            DefaultCheckIntervalMinutes = 120,
            MaxConcurrentChecks = 10,
            SnapshotRetentionDays = 60,
            ChangeEventRetentionDays = 180,
            MaxPlaywrightInstances = 5,
            UseLlmForSummaries = false,
            DefaultUserAgent = "Custom Bot/1.0",
            DefaultFetchTimeoutSeconds = 45,
            EnableLlmDebugLogging = true,
            MaxRetryAttempts = 5,
            RetryDelaySeconds = 120
        };

        // Assert
        dto.DefaultCheckIntervalMinutes.ShouldBe(120);
        dto.MaxConcurrentChecks.ShouldBe(10);
        dto.SnapshotRetentionDays.ShouldBe(60);
        dto.ChangeEventRetentionDays.ShouldBe(180);
        dto.MaxPlaywrightInstances.ShouldBe(5);
        dto.UseLlmForSummaries.ShouldBeFalse();
        dto.DefaultUserAgent.ShouldBe("Custom Bot/1.0");
        dto.DefaultFetchTimeoutSeconds.ShouldBe(45);
        dto.EnableLlmDebugLogging.ShouldBeTrue();
        dto.MaxRetryAttempts.ShouldBe(5);
        dto.RetryDelaySeconds.ShouldBe(120);
    }
}

public class GeneralSettingsUpdateDtoTests
{
    [Fact]
    public void GeneralSettingsUpdateDto_AllPropertiesNullByDefault()
    {
        // Act
        var dto = new GeneralSettingsUpdateDto();

        // Assert
        dto.DefaultCheckIntervalMinutes.ShouldBeNull();
        dto.MaxConcurrentChecks.ShouldBeNull();
        dto.SnapshotRetentionDays.ShouldBeNull();
        dto.ChangeEventRetentionDays.ShouldBeNull();
        dto.MaxPlaywrightInstances.ShouldBeNull();
        dto.UseLlmForSummaries.ShouldBeNull();
        dto.DefaultUserAgent.ShouldBeNull();
        dto.DefaultFetchTimeoutSeconds.ShouldBeNull();
        dto.EnableLlmDebugLogging.ShouldBeNull();
        dto.MaxRetryAttempts.ShouldBeNull();
        dto.RetryDelaySeconds.ShouldBeNull();
    }

    [Fact]
    public void GeneralSettingsUpdateDto_CanSetPartialUpdate()
    {
        // Act - Only set a few properties to simulate partial update
        var dto = new GeneralSettingsUpdateDto
        {
            MaxConcurrentChecks = 15,
            UseLlmForSummaries = false
        };

        // Assert - Only specified properties should be set
        dto.DefaultCheckIntervalMinutes.ShouldBeNull();
        dto.MaxConcurrentChecks.ShouldBe(15);
        dto.SnapshotRetentionDays.ShouldBeNull();
        dto.UseLlmForSummaries.ShouldBe(false);
    }

    [Fact]
    public void GeneralSettingsUpdateDto_CanSetAllProperties()
    {
        // Act
        var dto = new GeneralSettingsUpdateDto
        {
            DefaultCheckIntervalMinutes = 30,
            MaxConcurrentChecks = 8,
            SnapshotRetentionDays = 14,
            ChangeEventRetentionDays = 45,
            MaxPlaywrightInstances = 2,
            UseLlmForSummaries = true,
            DefaultUserAgent = "TestAgent/2.0",
            DefaultFetchTimeoutSeconds = 60,
            EnableLlmDebugLogging = true,
            MaxRetryAttempts = 1,
            RetryDelaySeconds = 30
        };

        // Assert
        dto.DefaultCheckIntervalMinutes.ShouldBe(30);
        dto.MaxConcurrentChecks.ShouldBe(8);
        dto.SnapshotRetentionDays.ShouldBe(14);
        dto.ChangeEventRetentionDays.ShouldBe(45);
        dto.MaxPlaywrightInstances.ShouldBe(2);
        dto.UseLlmForSummaries.ShouldBe(true);
        dto.DefaultUserAgent.ShouldBe("TestAgent/2.0");
        dto.DefaultFetchTimeoutSeconds.ShouldBe(60);
        dto.EnableLlmDebugLogging.ShouldBe(true);
        dto.MaxRetryAttempts.ShouldBe(1);
        dto.RetryDelaySeconds.ShouldBe(30);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    public void GeneralSettingsUpdateDto_AcceptsVariousRetryAttemptValues(int retryAttempts)
    {
        // Act
        var dto = new GeneralSettingsUpdateDto
        {
            MaxRetryAttempts = retryAttempts
        };

        // Assert
        dto.MaxRetryAttempts.ShouldBe(retryAttempts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64)")]
    [InlineData("CustomBot/1.0 (+https://example.com/bot)")]
    public void GeneralSettingsUpdateDto_AcceptsVariousUserAgentValues(string userAgent)
    {
        // Act
        var dto = new GeneralSettingsUpdateDto
        {
            DefaultUserAgent = userAgent
        };

        // Assert
        dto.DefaultUserAgent.ShouldBe(userAgent);
    }
}
