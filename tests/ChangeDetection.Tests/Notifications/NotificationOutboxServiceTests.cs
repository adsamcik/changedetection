using System.Net;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Notifications;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Notifications;

/// <summary>
/// Tests for <see cref="NotificationOutboxService"/>.
/// Verifies queuing, processing, delivery, and error handling for notifications.
/// </summary>
[Category("Unit")]
public class NotificationOutboxServiceTests : TestBase
{
    private readonly INotificationOutboxRepository _outboxRepo;
    private readonly IRepository<AppSettings> _settingsRepo;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FakeLogger<NotificationOutboxService> _logger;

    public NotificationOutboxServiceTests()
    {
        _outboxRepo = Substitute.For<INotificationOutboxRepository>();
        _settingsRepo = Substitute.For<IRepository<AppSettings>>();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _logger = CreateLogger<NotificationOutboxService>();

        // Default: return empty settings
        _settingsRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AppSettings>());
    }

    private NotificationOutboxService CreateSut() =>
        new(_outboxRepo, _settingsRepo, _httpClientFactory, _logger);

    #region Test Helpers

    private static WatchedSite CreateWatch(
        bool emailEnabled = false,
        bool webhookEnabled = false,
        bool discordEnabled = false,
        string? email = null,
        string? webhookUrl = null,
        string? discordUrl = null) => new()
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            Url = "https://example.com",
            Name = "Test Watch",
            Notifications = new NotificationSettings
            {
                EmailEnabled = emailEnabled,
                EmailAddress = email ?? (emailEnabled ? "test@example.com" : null),
                WebhookEnabled = webhookEnabled,
                WebhookUrl = webhookUrl ?? (webhookEnabled ? "https://webhook.example.com" : null),
                DiscordEnabled = discordEnabled,
                DiscordWebhookUrl = discordUrl ?? (discordEnabled ? "https://discord.com/api/webhooks/123" : null)
            }
        };

    private static ChangeEvent CreateChangeEvent() => new()
    {
        Id = Guid.NewGuid(),
        WatchedSiteId = Guid.NewGuid(),
        DetectedAt = DateTime.UtcNow,
        Importance = ChangeImportance.Medium,
        LinesAdded = 10,
        LinesRemoved = 5,
        DiffSummary = "Test diff summary"
    };

    private static AlertEvaluationResult CreateAlertResult(bool hasAlerts = true)
    {
        if (!hasAlerts)
            return new AlertEvaluationResult { TriggeredThresholds = [] };

        return new AlertEvaluationResult
        {
            HighestImportance = ChangeImportance.High,
            CombinedMessage = "Price dropped significantly",
            TriggeredThresholds =
            [
                new TriggeredThreshold
                {
                    Threshold = new FieldAlertThreshold { Name = "Price Drop" },
                    Field = new SchemaField { Name = "price", Selector = ".price" },
                    Message = "Price dropped by 20%",
                    OldValue = 100,
                    NewValue = 80,
                    CalculatedChange = -20
                }
            ]
        };
    }

    private static NotificationContext CreateNotificationContext(WatchedSite watch) =>
        new() { Watch = watch };

    private static NotificationOutboxEntry CreateOutboxEntry(
        NotificationType type = NotificationType.Email,
        string destination = "test@example.com") => new()
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            WatchedSiteId = Guid.NewGuid(),
            ChangeEventId = Guid.NewGuid(),
            NotificationType = type,
            Destination = destination,
            PayloadJson = new ChangeNotificationPayload(
                Guid.NewGuid(), "Test Watch", "https://example.com",
                Guid.NewGuid(), DateTime.UtcNow, "Medium",
                10, 5, "Summary", "Diff").ToJson(),
            Status = NotificationStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

    #endregion

    #region QueueChangeNotificationAsync Tests

    [Test]
    public async Task QueueChangeNotificationAsync_EmailEnabled_QueuesEmailEntry()
    {
        // Arrange
        var watch = CreateWatch(emailEnabled: true);
        var change = CreateChangeEvent();
        var sut = CreateSut();

        _outboxRepo.AddAsync(Arg.Any<NotificationOutboxEntry>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<NotificationOutboxEntry>());

        // Act
        await sut.QueueChangeNotificationAsync(watch, change);

        // Assert
        await _outboxRepo.Received(1).AddAsync(
            Arg.Is<NotificationOutboxEntry>(e =>
                e.NotificationType == NotificationType.Email &&
                e.Destination == watch.Notifications.EmailAddress),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task QueueChangeNotificationAsync_WebhookEnabled_QueuesWebhookEntry()
    {
        // Arrange
        var watch = CreateWatch(webhookEnabled: true);
        var change = CreateChangeEvent();
        var sut = CreateSut();

        _outboxRepo.AddAsync(Arg.Any<NotificationOutboxEntry>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<NotificationOutboxEntry>());

        // Act
        await sut.QueueChangeNotificationAsync(watch, change);

        // Assert
        await _outboxRepo.Received(1).AddAsync(
            Arg.Is<NotificationOutboxEntry>(e =>
                e.NotificationType == NotificationType.Webhook &&
                e.Destination == watch.Notifications.WebhookUrl),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task QueueChangeNotificationAsync_DiscordEnabled_QueuesDiscordEntry()
    {
        // Arrange
        var watch = CreateWatch(discordEnabled: true);
        var change = CreateChangeEvent();
        var sut = CreateSut();

        _outboxRepo.AddAsync(Arg.Any<NotificationOutboxEntry>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<NotificationOutboxEntry>());

        // Act
        await sut.QueueChangeNotificationAsync(watch, change);

        // Assert
        await _outboxRepo.Received(1).AddAsync(
            Arg.Is<NotificationOutboxEntry>(e =>
                e.NotificationType == NotificationType.Discord &&
                e.Destination == watch.Notifications.DiscordWebhookUrl),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task QueueChangeNotificationAsync_AllEnabled_QueuesAllThreeEntries()
    {
        // Arrange
        var watch = CreateWatch(emailEnabled: true, webhookEnabled: true, discordEnabled: true);
        var change = CreateChangeEvent();
        var sut = CreateSut();

        _outboxRepo.AddAsync(Arg.Any<NotificationOutboxEntry>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<NotificationOutboxEntry>());

        // Act
        await sut.QueueChangeNotificationAsync(watch, change);

        // Assert
        await _outboxRepo.Received(3).AddAsync(
            Arg.Any<NotificationOutboxEntry>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task QueueChangeNotificationAsync_NoneEnabled_QueuesNothing()
    {
        // Arrange
        var watch = CreateWatch(); // All disabled by default
        var change = CreateChangeEvent();
        var sut = CreateSut();

        // Act
        await sut.QueueChangeNotificationAsync(watch, change);

        // Assert
        await _outboxRepo.DidNotReceive().AddAsync(
            Arg.Any<NotificationOutboxEntry>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task QueueChangeNotificationAsync_EmailEnabledButNoAddress_QueuesNothing()
    {
        // Arrange
        var watch = CreateWatch(emailEnabled: true, email: null);
        watch.Notifications.EmailAddress = null; // Explicitly clear
        var change = CreateChangeEvent();
        var sut = CreateSut();

        // Act
        await sut.QueueChangeNotificationAsync(watch, change);

        // Assert
        await _outboxRepo.DidNotReceive().AddAsync(
            Arg.Any<NotificationOutboxEntry>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task QueueChangeNotificationAsync_IncludesSummaryInPayload()
    {
        // Arrange
        var watch = CreateWatch(emailEnabled: true);
        var change = CreateChangeEvent();
        const string customSummary = "Custom AI summary";
        var sut = CreateSut();

        NotificationOutboxEntry? capturedEntry = null;
        _outboxRepo.AddAsync(Arg.Any<NotificationOutboxEntry>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedEntry = ci.Arg<NotificationOutboxEntry>();
                return capturedEntry;
            });

        // Act
        await sut.QueueChangeNotificationAsync(watch, change, customSummary);

        // Assert
        capturedEntry.ShouldNotBeNull();
        capturedEntry.PayloadJson.ShouldContain(customSummary);
    }

    #endregion

    #region QueueAlertNotificationAsync Tests

    [Test]
    public async Task QueueAlertNotificationAsync_HasTriggeredAlerts_QueuesEntries()
    {
        // Arrange
        var watch = CreateWatch(emailEnabled: true);
        var alertResult = CreateAlertResult(hasAlerts: true);
        var context = CreateNotificationContext(watch);
        var sut = CreateSut();

        _outboxRepo.AddAsync(Arg.Any<NotificationOutboxEntry>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<NotificationOutboxEntry>());

        // Act
        await sut.QueueAlertNotificationAsync(watch, alertResult, context);

        // Assert
        await _outboxRepo.Received(1).AddAsync(
            Arg.Is<NotificationOutboxEntry>(e => e.NotificationType == NotificationType.Alert),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task QueueAlertNotificationAsync_NoTriggeredAlerts_QueuesNothing()
    {
        // Arrange
        var watch = CreateWatch(emailEnabled: true);
        var alertResult = CreateAlertResult(hasAlerts: false);
        var context = CreateNotificationContext(watch);
        var sut = CreateSut();

        // Act
        await sut.QueueAlertNotificationAsync(watch, alertResult, context);

        // Assert
        await _outboxRepo.DidNotReceive().AddAsync(
            Arg.Any<NotificationOutboxEntry>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task QueueAlertNotificationAsync_AllChannelsEnabled_QueuesAllThree()
    {
        // Arrange
        var watch = CreateWatch(emailEnabled: true, webhookEnabled: true, discordEnabled: true);
        var alertResult = CreateAlertResult(hasAlerts: true);
        var context = CreateNotificationContext(watch);
        var sut = CreateSut();

        _outboxRepo.AddAsync(Arg.Any<NotificationOutboxEntry>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<NotificationOutboxEntry>());

        // Act
        await sut.QueueAlertNotificationAsync(watch, alertResult, context);

        // Assert
        await _outboxRepo.Received(3).AddAsync(
            Arg.Any<NotificationOutboxEntry>(),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region ProcessPendingAsync Tests

    [Test]
    public async Task ProcessPendingAsync_WithPendingEntries_ProcessesAll()
    {
        // Arrange
        var entries = new List<NotificationOutboxEntry>
        {
            CreateOutboxEntry(NotificationType.Webhook, "https://webhook.example.com"),
            CreateOutboxEntry(NotificationType.Webhook, "https://webhook2.example.com")
        };

        _outboxRepo.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(entries);
        _outboxRepo.TryClaimForProcessingAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Setup HTTP client to succeed
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactory.CreateClient().Returns(httpClient);

        var sut = CreateSut();

        // Act
        var result = await sut.ProcessPendingAsync();

        // Assert
        result.ShouldBe(2);
        await _outboxRepo.Received(2).MarkSentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessPendingAsync_ClaimFails_SkipsEntry()
    {
        // Arrange
        var entries = new List<NotificationOutboxEntry>
        {
            CreateOutboxEntry(NotificationType.Webhook, "https://webhook.example.com")
        };

        _outboxRepo.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(entries);
        _outboxRepo.TryClaimForProcessingAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(false); // Another processor claimed it

        var sut = CreateSut();

        // Act
        var result = await sut.ProcessPendingAsync();

        // Assert
        result.ShouldBe(0);
        await _outboxRepo.DidNotReceive().MarkSentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessPendingAsync_SendFails_MarksAsFailed()
    {
        // Arrange
        var entries = new List<NotificationOutboxEntry>
        {
            CreateOutboxEntry(NotificationType.Email, "test@example.com")
        };

        _outboxRepo.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(entries);
        _outboxRepo.TryClaimForProcessingAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Email will fail because no SMTP settings are configured
        var sut = CreateSut();

        // Act
        var result = await sut.ProcessPendingAsync();

        // Assert
        result.ShouldBe(0);
        await _outboxRepo.Received(1).MarkFailedAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessPendingAsync_NoPendingEntries_ReturnsZero()
    {
        // Arrange
        _outboxRepo.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<NotificationOutboxEntry>());

        var sut = CreateSut();

        // Act
        var result = await sut.ProcessPendingAsync();

        // Assert
        result.ShouldBe(0);
    }

    [Test]
    public async Task ProcessPendingAsync_CancellationRequested_StopsProcessing()
    {
        // Arrange
        var entries = new List<NotificationOutboxEntry>
        {
            CreateOutboxEntry(),
            CreateOutboxEntry()
        };

        _outboxRepo.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(entries);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var sut = CreateSut();

        // Act
        var result = await sut.ProcessPendingAsync(ct: cts.Token);

        // Assert - should return 0 because it breaks on first iteration
        result.ShouldBe(0);
    }

    #endregion

    #region ProcessRetryAsync Tests

    [Test]
    public async Task ProcessRetryAsync_WithRetryEntries_RetriesAll()
    {
        // Arrange
        var entries = new List<NotificationOutboxEntry>
        {
            CreateOutboxEntry(NotificationType.Webhook, "https://webhook.example.com")
        };
        entries[0].Status = NotificationStatus.RetryPending;
        entries[0].RetryCount = 2;

        _outboxRepo.GetReadyForRetryAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(entries);
        _outboxRepo.TryClaimForProcessingAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactory.CreateClient().Returns(httpClient);

        var sut = CreateSut();

        // Act
        var result = await sut.ProcessRetryAsync();

        // Assert
        result.ShouldBe(1);
        await _outboxRepo.Received(1).MarkSentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessRetryAsync_RetryFails_MarksFailedAgain()
    {
        // Arrange
        var entries = new List<NotificationOutboxEntry>
        {
            CreateOutboxEntry(NotificationType.Webhook, "https://webhook.example.com")
        };
        entries[0].Status = NotificationStatus.RetryPending;
        entries[0].RetryCount = 2;

        _outboxRepo.GetReadyForRetryAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(entries);
        _outboxRepo.TryClaimForProcessingAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // HTTP call will fail
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactory.CreateClient().Returns(httpClient);

        var sut = CreateSut();

        // Act
        var result = await sut.ProcessRetryAsync();

        // Assert
        result.ShouldBe(0);
        await _outboxRepo.Received(1).MarkFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region RecoverStaleAsync Tests

    [Test]
    public async Task RecoverStaleAsync_WithStaleEntries_CallsRepository()
    {
        // Arrange
        _outboxRepo.RecoverStaleProcessingAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(3);

        var sut = CreateSut();

        // Act
        var result = await sut.RecoverStaleAsync();

        // Assert
        result.ShouldBe(3);
        await _outboxRepo.Received(1).RecoverStaleProcessingAsync(
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RecoverStaleAsync_RecoversEntries_LogsWarning()
    {
        // Arrange
        _outboxRepo.RecoverStaleProcessingAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(5);

        var sut = CreateSut();

        // Act
        await sut.RecoverStaleAsync();

        // Assert
        var logs = LogCollector.GetSnapshot();
        logs.ShouldContain(l =>
            l.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
            l.Message.Contains("5"));
    }

    #endregion

    #region GetStatsAsync Tests

    [Test]
    public async Task GetStatsAsync_ReturnsRepositoryStats()
    {
        // Arrange
        var expectedStats = new NotificationOutboxStats(10, 2, 3, 1, 50);
        _outboxRepo.GetStatsAsync(Arg.Any<CancellationToken>()).Returns(expectedStats);

        var sut = CreateSut();

        // Act
        var result = await sut.GetStatsAsync();

        // Assert
        result.ShouldBe(expectedStats);
    }

    #endregion

    #region CleanupOldNotificationsAsync Tests

    [Test]
    public async Task CleanupOldNotificationsAsync_DeletesOldEntries()
    {
        // Arrange
        var olderThan = TimeSpan.FromDays(7);
        _outboxRepo.DeleteOldSentAsync(olderThan, Arg.Any<CancellationToken>()).Returns(25);

        var sut = CreateSut();

        // Act
        var result = await sut.CleanupOldNotificationsAsync(olderThan);

        // Assert
        result.ShouldBe(25);
        await _outboxRepo.Received(1).DeleteOldSentAsync(olderThan, Arg.Any<CancellationToken>());
    }

    #endregion

    #region Webhook Delivery Tests

    [Test]
    public async Task SendWebhook_ValidUrl_PostsJsonPayload()
    {
        // Arrange
        var entry = CreateOutboxEntry(NotificationType.Webhook, "https://webhook.example.com");
        var entries = new List<NotificationOutboxEntry> { entry };

        _outboxRepo.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(entries);
        _outboxRepo.TryClaimForProcessingAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        HttpRequestMessage? capturedRequest = null;
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK), req =>
        {
            capturedRequest = req;
        });
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactory.CreateClient().Returns(httpClient);

        var sut = CreateSut();

        // Act
        await sut.ProcessPendingAsync();

        // Assert
        capturedRequest.ShouldNotBeNull();
        capturedRequest.Method.ShouldBe(HttpMethod.Post);
        capturedRequest.RequestUri!.ToString().ShouldBe("https://webhook.example.com/");
    }

    [Test]
    public async Task SendWebhook_Non2xxResponse_ThrowsAndMarksFailed()
    {
        // Arrange
        var entry = CreateOutboxEntry(NotificationType.Webhook, "https://webhook.example.com");
        var entries = new List<NotificationOutboxEntry> { entry };

        _outboxRepo.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(entries);
        _outboxRepo.TryClaimForProcessingAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.BadGateway));
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactory.CreateClient().Returns(httpClient);

        var sut = CreateSut();

        // Act
        await sut.ProcessPendingAsync();

        // Assert
        await _outboxRepo.Received(1).MarkFailedAsync(
            entry.Id,
            Arg.Is<string>(s => s.Contains("502") || s.Contains("BadGateway")),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region Discord Delivery Tests

    [Test]
    public async Task SendDiscord_ValidUrl_PostsEmbedPayload()
    {
        // Arrange
        var entry = CreateOutboxEntry(NotificationType.Discord, "https://discord.com/api/webhooks/123");
        var entries = new List<NotificationOutboxEntry> { entry };

        _outboxRepo.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(entries);
        _outboxRepo.TryClaimForProcessingAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        string? capturedContent = null;
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK), async req =>
        {
            capturedContent = await req.Content!.ReadAsStringAsync();
        });
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactory.CreateClient().Returns(httpClient);

        var sut = CreateSut();

        // Act
        await sut.ProcessPendingAsync();

        // Assert
        capturedContent.ShouldNotBeNull();
        capturedContent.ShouldContain("embeds");
    }

    #endregion

    #region Email Delivery Tests

    [Test]
    public async Task SendEmail_NoSmtpSettings_ThrowsInvalidOperationAndMarksFailed()
    {
        // Arrange
        var entry = CreateOutboxEntry(NotificationType.Email, "test@example.com");
        var entries = new List<NotificationOutboxEntry> { entry };

        _outboxRepo.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(entries);
        _outboxRepo.TryClaimForProcessingAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // No SMTP settings configured (empty settings list)
        _settingsRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AppSettings>());

        var sut = CreateSut();

        // Act
        await sut.ProcessPendingAsync();

        // Assert
        await _outboxRepo.Received(1).MarkFailedAsync(
            entry.Id,
            Arg.Is<string>(s => s.Contains("not configured")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendEmail_SmtpHostConfigured_AttemptsToConnect()
    {
        // Arrange
        var entry = CreateOutboxEntry(NotificationType.Email, "test@example.com");
        var entries = new List<NotificationOutboxEntry> { entry };

        _outboxRepo.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(entries);
        _outboxRepo.TryClaimForProcessingAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Configure SMTP settings
        var appSettings = new AppSettings
        {
            Email = new EmailSettings
            {
                SmtpHost = "smtp.example.com",
                SmtpPort = 587,
                UseSsl = true,
                FromAddress = "noreply@example.com"
            }
        };
        _settingsRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AppSettings> { appSettings });

        var sut = CreateSut();

        // Act - This will fail to connect to SMTP, but that's expected in unit test
        await sut.ProcessPendingAsync();

        // Assert - Should have attempted and failed (connection refused or similar)
        await _outboxRepo.Received(1).MarkFailedAsync(
            entry.Id,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region Alert Type Routing Tests

    [Test]
    public async Task SendAlert_DestinationContainsAt_RoutesToEmail()
    {
        // Arrange - Alert with email destination
        var entry = new NotificationOutboxEntry
        {
            Id = Guid.NewGuid(),
            NotificationType = NotificationType.Alert,
            Destination = "test@example.com",
            PayloadJson = new AlertNotificationPayload(
                Guid.NewGuid(), "Test", "https://example.com",
                "Alert message", "High",
                [new TriggeredThresholdPayload("price", "Price Drop", "Price dropped", "80", -20)]).ToJson()
        };
        var entries = new List<NotificationOutboxEntry> { entry };

        _outboxRepo.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(entries);
        _outboxRepo.TryClaimForProcessingAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // No SMTP configured, so it will fail - but we're testing the routing
        var sut = CreateSut();

        // Act
        await sut.ProcessPendingAsync();

        // Assert - Should fail trying to send email (no SMTP config)
        await _outboxRepo.Received(1).MarkFailedAsync(
            entry.Id,
            Arg.Is<string>(s => s.Contains("not configured")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendAlert_DestinationContainsDiscord_RoutesToDiscord()
    {
        // Arrange - Alert with discord destination
        var entry = new NotificationOutboxEntry
        {
            Id = Guid.NewGuid(),
            NotificationType = NotificationType.Alert,
            Destination = "https://discord.com/api/webhooks/123",
            PayloadJson = new AlertNotificationPayload(
                Guid.NewGuid(), "Test", "https://example.com",
                "Alert message", "High",
                [new TriggeredThresholdPayload("price", "Price Drop", "Price dropped", "80", -20)]).ToJson()
        };
        var entries = new List<NotificationOutboxEntry> { entry };

        _outboxRepo.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(entries);
        _outboxRepo.TryClaimForProcessingAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        string? capturedContent = null;
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK), async req =>
        {
            capturedContent = await req.Content!.ReadAsStringAsync();
        });
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactory.CreateClient().Returns(httpClient);

        var sut = CreateSut();

        // Act
        await sut.ProcessPendingAsync();

        // Assert - Should route to Discord (embed format)
        capturedContent.ShouldNotBeNull();
        capturedContent.ShouldContain("embeds");
        capturedContent.ShouldContain("Alert:");
    }

    [Test]
    public async Task SendAlert_DefaultDestination_RoutesToWebhook()
    {
        // Arrange - Alert with generic webhook destination
        var entry = new NotificationOutboxEntry
        {
            Id = Guid.NewGuid(),
            NotificationType = NotificationType.Alert,
            Destination = "https://webhook.example.com/alerts",
            PayloadJson = new AlertNotificationPayload(
                Guid.NewGuid(), "Test", "https://example.com",
                "Alert message", "High",
                [new TriggeredThresholdPayload("price", "Price Drop", "Price dropped", "80", -20)]).ToJson()
        };
        var entries = new List<NotificationOutboxEntry> { entry };

        _outboxRepo.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(entries);
        _outboxRepo.TryClaimForProcessingAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        string? capturedContent = null;
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK), async req =>
        {
            capturedContent = await req.Content!.ReadAsStringAsync();
        });
        var httpClient = new HttpClient(mockHandler);
        _httpClientFactory.CreateClient().Returns(httpClient);

        var sut = CreateSut();

        // Act
        await sut.ProcessPendingAsync();

        // Assert - Should route to webhook (JSON format without embeds)
        capturedContent.ShouldNotBeNull();
        capturedContent.ShouldContain("type");
        capturedContent.ShouldContain("alert");
    }

    #endregion
}

/// <summary>
/// Mock HTTP message handler for testing HTTP client calls.
/// </summary>
public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage _response;
    private readonly Action<HttpRequestMessage>? _onRequest;
    private readonly Func<HttpRequestMessage, Task>? _onRequestAsync;

    public MockHttpMessageHandler(HttpResponseMessage response, Action<HttpRequestMessage>? onRequest = null)
    {
        _response = response;
        _onRequest = onRequest;
    }

    public MockHttpMessageHandler(HttpResponseMessage response, Func<HttpRequestMessage, Task> onRequestAsync)
    {
        _response = response;
        _onRequestAsync = onRequestAsync;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _onRequest?.Invoke(request);
        if (_onRequestAsync != null)
            await _onRequestAsync(request);
        return _response;
    }
}
