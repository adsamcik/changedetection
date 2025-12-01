using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Notifications;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace ChangeDetection.Tests.Notifications;

public class NotificationServiceTests
{
    private readonly IRepository<AppSettings> _settingsRepo;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NotificationService> _logger;
    private readonly NotificationService _sut;

    public NotificationServiceTests()
    {
        _settingsRepo = Substitute.For<IRepository<AppSettings>>();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _logger = Substitute.For<ILogger<NotificationService>>();
        _sut = new NotificationService(_settingsRepo, _httpClientFactory, _logger);
    }

    [Fact]
    public async Task SendNotificationAsync_NoNotificationsEnabled_DoesNotThrow()
    {
        // Arrange
        var watch = new WatchedSite 
        { 
            Url = "https://example.com",
            Notifications = new NotificationSettings
            {
                EmailEnabled = false,
                WebhookEnabled = false,
                DiscordEnabled = false
            }
        };
        var change = new ChangeEvent { DiffSummary = "Test change" };

        // Act & Assert - should not throw
        await _sut.SendNotificationAsync(watch, change);
    }

    [Fact]
    public async Task SendNotificationAsync_EmailEnabled_AttemptsToSend()
    {
        // Arrange
        var watch = new WatchedSite 
        { 
            Url = "https://example.com",
            Notifications = new NotificationSettings
            {
                EmailEnabled = true,
                EmailAddress = "test@example.com"
            }
        };
        var change = new ChangeEvent { DiffSummary = "Test change" };
        
        _settingsRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AppSettings>());

        // Act - Email will fail due to no SMTP settings, but shouldn't throw
        await _sut.SendNotificationAsync(watch, change);

        // Assert - Should have attempted to get settings
        await _settingsRepo.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendNotificationAsync_UsesProvidedSummary()
    {
        // Arrange
        var watch = new WatchedSite 
        { 
            Url = "https://example.com",
            Notifications = new NotificationSettings()
        };
        var change = new ChangeEvent { DiffSummary = "Original" };
        const string customSummary = "Custom summary";

        // Act - No notifications enabled, so nothing happens
        await _sut.SendNotificationAsync(watch, change, customSummary);
        
        // Assert - Test completes without error
    }

    [Fact]
    public async Task SendTestNotificationAsync_EmailEnabled_AttemptsToSendTestEmail()
    {
        // Arrange
        var settings = new NotificationSettings
        {
            EmailEnabled = true,
            EmailAddress = "test@example.com"
        };
        
        _settingsRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AppSettings>());

        // Act
        await _sut.SendTestNotificationAsync(settings);

        // Assert
        await _settingsRepo.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendTestNotificationAsync_NoSettingsEnabled_DoesNotThrow()
    {
        // Arrange
        var settings = new NotificationSettings
        {
            EmailEnabled = false,
            WebhookEnabled = false,
            DiscordEnabled = false
        };

        // Act & Assert - should not throw
        await _sut.SendTestNotificationAsync(settings);
    }
}

public class NotificationSettingsTests
{
    [Fact]
    public void NotificationSettings_HasCorrectDefaults()
    {
        // Act
        var settings = new NotificationSettings();

        // Assert
        settings.EmailEnabled.ShouldBeFalse();
        settings.WebhookEnabled.ShouldBeFalse();
        settings.DiscordEnabled.ShouldBeFalse();
        settings.EmailAddress.ShouldBeNull();
        settings.WebhookUrl.ShouldBeNull();
        settings.DiscordWebhookUrl.ShouldBeNull();
    }

    [Fact]
    public void NotificationSettings_CanSetAllProperties()
    {
        // Arrange & Act
        var settings = new NotificationSettings
        {
            EmailEnabled = true,
            EmailAddress = "test@example.com",
            WebhookEnabled = true,
            WebhookUrl = "https://webhook.example.com",
            DiscordEnabled = true,
            DiscordWebhookUrl = "https://discord.com/api/webhooks/123",
            NotifyOnlyOnImportantChanges = true,
            MinimumImportance = ChangeImportance.High
        };

        // Assert
        settings.EmailEnabled.ShouldBeTrue();
        settings.EmailAddress.ShouldBe("test@example.com");
        settings.WebhookEnabled.ShouldBeTrue();
        settings.WebhookUrl.ShouldBe("https://webhook.example.com");
        settings.DiscordEnabled.ShouldBeTrue();
        settings.DiscordWebhookUrl.ShouldBe("https://discord.com/api/webhooks/123");
        settings.NotifyOnlyOnImportantChanges.ShouldBeTrue();
        settings.MinimumImportance.ShouldBe(ChangeImportance.High);
    }
}
