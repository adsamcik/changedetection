using System.Net;
using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Notifications;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

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

    [Test]
    public async Task SendNotificationAsync_NoNotificationsEnabled_DoesNotCallAnyChannel()
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

        // Act
        await _sut.SendNotificationAsync(watch, change);

        // Assert - no notification channels were invoked
        _httpClientFactory.DidNotReceive().CreateClient(Arg.Any<string>());
        await _settingsRepo.DidNotReceive().GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendNotificationAsync_EmailEnabled_FetchesSmtpSettingsButDoesNotUseHttpClient()
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

        // Act - Email will early-return due to no SMTP settings (logs warning)
        await _sut.SendNotificationAsync(watch, change);

        // Assert - settings were fetched for email SMTP config
        await _settingsRepo.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
        // No HTTP client created (webhook/discord not enabled)
        _httpClientFactory.DidNotReceive().CreateClient(Arg.Any<string>());
    }

    [Test]
    public async Task SendNotificationAsync_WebhookEnabled_PostsToWebhookUrl()
    {
        // Arrange
        using var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        const string webhookUrl = "https://webhook.example.com/notify";
        var watch = new WatchedSite 
        { 
            Url = "https://example.com",
            Notifications = new NotificationSettings
            {
                WebhookEnabled = true,
                WebhookUrl = webhookUrl
            }
        };
        var change = new ChangeEvent { DiffSummary = "Something changed" };

        // Act
        await _sut.SendNotificationAsync(watch, change);

        // Assert - HTTP POST was sent to the webhook URL with correct payload
        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].RequestUri!.ToString().ShouldBe(webhookUrl);

        var body = handler.Requests[0].Body.ShouldNotBeNull();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("summary").GetString().ShouldBe("Something changed");
        doc.RootElement.GetProperty("watchUrl").GetString().ShouldBe("https://example.com");
    }

    [Test]
    public async Task SendNotificationAsync_UsesProvidedSummary_IncludesSummaryInWebhookPayload()
    {
        // Arrange
        using var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        var watch = new WatchedSite 
        { 
            Url = "https://example.com",
            Notifications = new NotificationSettings
            {
                WebhookEnabled = true,
                WebhookUrl = "https://webhook.example.com"
            }
        };
        var change = new ChangeEvent { DiffSummary = "Original" };
        const string customSummary = "Custom summary";

        // Act
        await _sut.SendNotificationAsync(watch, change, customSummary);
        
        // Assert - custom summary was used instead of DiffSummary
        handler.Requests.Count.ShouldBe(1);
        var body = handler.Requests[0].Body.ShouldNotBeNull();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("summary").GetString().ShouldBe("Custom summary");
    }

    [Test]
    public async Task SendTestNotificationAsync_EmailEnabled_FetchesSmtpSettingsButDoesNotUseHttpClient()
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

        // Assert - settings were fetched for email SMTP config
        await _settingsRepo.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
        // No HTTP client created (webhook/discord not enabled)
        _httpClientFactory.DidNotReceive().CreateClient(Arg.Any<string>());
    }

    [Test]
    public async Task SendTestNotificationAsync_NoSettingsEnabled_DoesNotCallAnyChannel()
    {
        // Arrange
        var settings = new NotificationSettings
        {
            EmailEnabled = false,
            WebhookEnabled = false,
            DiscordEnabled = false
        };

        // Act
        await _sut.SendTestNotificationAsync(settings);

        // Assert - no notification channels were invoked
        _httpClientFactory.DidNotReceive().CreateClient(Arg.Any<string>());
        await _settingsRepo.DidNotReceive().GetAllAsync(Arg.Any<CancellationToken>());
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : null;
            Requests.Add(new CapturedRequest(request.RequestUri, body));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private record CapturedRequest(Uri? RequestUri, string? Body);
}

public class NotificationSettingsTests
{
    [Test]
    public async Task NotificationSettings_HasCorrectDefaults()
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
        await Task.CompletedTask;
    }

    [Test]
    public async Task NotificationSettings_CanSetAllProperties()
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
            UseLlmSummary = true,
            MinimumImportance = ChangeImportance.High
        };

        // Assert
        settings.EmailEnabled.ShouldBeTrue();
        settings.EmailAddress.ShouldBe("test@example.com");
        settings.WebhookEnabled.ShouldBeTrue();
        settings.WebhookUrl.ShouldBe("https://webhook.example.com");
        settings.DiscordEnabled.ShouldBeTrue();
        settings.DiscordWebhookUrl.ShouldBe("https://discord.com/api/webhooks/123");
        settings.UseLlmSummary.ShouldBeTrue();
        settings.MinimumImportance.ShouldBe(ChangeImportance.High);
        await Task.CompletedTask;
    }
}
