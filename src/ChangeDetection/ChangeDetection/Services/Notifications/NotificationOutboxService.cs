using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using MailKit.Net.Smtp;
using MimeKit;

namespace ChangeDetection.Services.Notifications;

/// <summary>
/// Service that queues notifications to a persistent outbox and processes them reliably.
/// Ensures no notifications are lost even if the app crashes mid-send.
/// </summary>
public class NotificationOutboxService(
    INotificationOutboxRepository outboxRepo,
    IRepository<AppSettings> settingsRepo,
    IHttpClientFactory httpClientFactory,
    ILogger<NotificationOutboxService> logger) : INotificationOutboxService
{
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(5);

    public async Task QueueChangeNotificationAsync(
        WatchedSite watch,
        ChangeEvent change,
        string? summary = null,
        CancellationToken ct = default)
    {
        var settings = watch.Notifications;
        var payload = ChangeNotificationPayload.FromEntities(watch, change, summary);
        var payloadJson = payload.ToJson();

        var tasks = new List<Task>();

        if (settings.EmailEnabled && !string.IsNullOrEmpty(settings.EmailAddress))
        {
            tasks.Add(outboxRepo.AddAsync(new NotificationOutboxEntry
            {
                OwnerId = watch.OwnerId,
                WatchedSiteId = watch.Id,
                ChangeEventId = change.Id,
                NotificationType = NotificationType.Email,
                Destination = settings.EmailAddress,
                PayloadJson = payloadJson
            }, ct));
        }

        if (settings.WebhookEnabled && !string.IsNullOrEmpty(settings.WebhookUrl))
        {
            tasks.Add(outboxRepo.AddAsync(new NotificationOutboxEntry
            {
                OwnerId = watch.OwnerId,
                WatchedSiteId = watch.Id,
                ChangeEventId = change.Id,
                NotificationType = NotificationType.Webhook,
                Destination = settings.WebhookUrl,
                PayloadJson = payloadJson
            }, ct));
        }

        if (settings.DiscordEnabled && !string.IsNullOrEmpty(settings.DiscordWebhookUrl))
        {
            tasks.Add(outboxRepo.AddAsync(new NotificationOutboxEntry
            {
                OwnerId = watch.OwnerId,
                WatchedSiteId = watch.Id,
                ChangeEventId = change.Id,
                NotificationType = NotificationType.Discord,
                Destination = settings.DiscordWebhookUrl,
                PayloadJson = payloadJson
            }, ct));
        }

        await Task.WhenAll(tasks);

        logger.LogDebug(
            "Queued {Count} notifications for watch {WatchId} change {ChangeId}",
            tasks.Count, watch.Id, change.Id);
    }

    public async Task QueueAlertNotificationAsync(
        WatchedSite watch,
        AlertEvaluationResult alertResult,
        NotificationContext context,
        CancellationToken ct = default)
    {
        if (!alertResult.HasTriggeredAlerts)
            return;

        var settings = watch.Notifications;
        var payload = AlertNotificationPayload.FromEntities(watch, alertResult);
        var payloadJson = payload.ToJson();

        var tasks = new List<Task>();

        if (settings.EmailEnabled && !string.IsNullOrEmpty(settings.EmailAddress))
        {
            tasks.Add(outboxRepo.AddAsync(new NotificationOutboxEntry
            {
                OwnerId = watch.OwnerId,
                WatchedSiteId = watch.Id,
                NotificationType = NotificationType.Alert,
                Destination = settings.EmailAddress,
                PayloadJson = payloadJson
            }, ct));
        }

        if (settings.WebhookEnabled && !string.IsNullOrEmpty(settings.WebhookUrl))
        {
            tasks.Add(outboxRepo.AddAsync(new NotificationOutboxEntry
            {
                OwnerId = watch.OwnerId,
                WatchedSiteId = watch.Id,
                NotificationType = NotificationType.Alert,
                Destination = settings.WebhookUrl,
                PayloadJson = payloadJson
            }, ct));
        }

        if (settings.DiscordEnabled && !string.IsNullOrEmpty(settings.DiscordWebhookUrl))
        {
            tasks.Add(outboxRepo.AddAsync(new NotificationOutboxEntry
            {
                OwnerId = watch.OwnerId,
                WatchedSiteId = watch.Id,
                NotificationType = NotificationType.Alert,
                Destination = settings.DiscordWebhookUrl,
                PayloadJson = payloadJson
            }, ct));
        }

        await Task.WhenAll(tasks);

        logger.LogDebug(
            "Queued {Count} alert notifications for watch {WatchId}",
            tasks.Count, watch.Id);
    }

    public async Task<int> ProcessPendingAsync(int batchSize = 50, CancellationToken ct = default)
    {
        var pending = await outboxRepo.GetPendingAsync(batchSize, ct);
        var processed = 0;

        foreach (var entry in pending)
        {
            if (ct.IsCancellationRequested)
                break;

            var claimed = await outboxRepo.TryClaimForProcessingAsync(entry.Id, ct);
            if (!claimed)
                continue;

            try
            {
                await SendNotificationAsync(entry, ct);
                await outboxRepo.MarkSentAsync(entry.Id, ct);
                processed++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to send notification {EntryId} to {Destination}",
                    entry.Id, entry.Destination);
                await outboxRepo.MarkFailedAsync(entry.Id, ex.Message, ct);
            }
        }

        if (processed > 0)
            logger.LogInformation("Processed {Count} pending notifications", processed);

        return processed;
    }

    public async Task<int> ProcessRetryAsync(int batchSize = 20, CancellationToken ct = default)
    {
        var retries = await outboxRepo.GetReadyForRetryAsync(batchSize, ct);
        var processed = 0;

        foreach (var entry in retries)
        {
            if (ct.IsCancellationRequested)
                break;

            var claimed = await outboxRepo.TryClaimForProcessingAsync(entry.Id, ct);
            if (!claimed)
                continue;

            try
            {
                await SendNotificationAsync(entry, ct);
                await outboxRepo.MarkSentAsync(entry.Id, ct);
                processed++;
                logger.LogInformation(
                    "Retry succeeded for notification {EntryId} (attempt {Attempt})",
                    entry.Id, entry.RetryCount + 1);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Retry failed for notification {EntryId} (attempt {Attempt})",
                    entry.Id, entry.RetryCount + 1);
                await outboxRepo.MarkFailedAsync(entry.Id, ex.Message, ct);
            }
        }

        if (processed > 0)
            logger.LogInformation("Processed {Count} retry notifications", processed);

        return processed;
    }

    public async Task<int> RecoverStaleAsync(CancellationToken ct = default)
    {
        var recovered = await outboxRepo.RecoverStaleProcessingAsync(ProcessingTimeout, ct);
        if (recovered > 0)
            logger.LogWarning("Recovered {Count} stale notifications stuck in processing", recovered);
        return recovered;
    }

    public Task<NotificationOutboxStats> GetStatsAsync(CancellationToken ct = default)
    {
        return outboxRepo.GetStatsAsync(ct);
    }

    public Task<int> CleanupOldNotificationsAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        return outboxRepo.DeleteOldSentAsync(olderThan, ct);
    }

    private async Task SendNotificationAsync(NotificationOutboxEntry entry, CancellationToken ct)
    {
        switch (entry.NotificationType)
        {
            case NotificationType.Email:
                await SendEmailAsync(entry, ct);
                break;
            case NotificationType.Webhook:
                await SendWebhookAsync(entry, ct);
                break;
            case NotificationType.Discord:
                await SendDiscordAsync(entry, ct);
                break;
            case NotificationType.Alert:
                // Alerts go to the same destination type as regular notifications
                // but with different formatting - determine by destination format
                if (entry.Destination.Contains('@'))
                    await SendAlertEmailAsync(entry, ct);
                else if (entry.Destination.Contains("discord"))
                    await SendAlertDiscordAsync(entry, ct);
                else
                    await SendAlertWebhookAsync(entry, ct);
                break;
        }
    }

    private async Task SendEmailAsync(NotificationOutboxEntry entry, CancellationToken ct)
    {
        var payload = ChangeNotificationPayload.FromJson(entry.PayloadJson);
        if (payload == null)
            throw new InvalidOperationException("Invalid notification payload");

        var appSettings = (await settingsRepo.GetAllAsync(ct)).FirstOrDefault();
        var emailSettings = appSettings?.Email;

        if (emailSettings == null || string.IsNullOrEmpty(emailSettings.SmtpHost))
            throw new InvalidOperationException("Email settings not configured");

        var message = payload.Summary ?? payload.DiffSummary ?? "A change was detected";

        var email = new MimeMessage();
        email.From.Add(new MailboxAddress(
            emailSettings.FromName ?? "Change Detection",
            emailSettings.FromAddress ?? "noreply@changedetection.local"));
        email.To.Add(MailboxAddress.Parse(entry.Destination));
        email.Subject = $"Change detected: {payload.WatchName}";

        var bodyBuilder = new BodyBuilder
        {
            HtmlBody = $"""
                <h2>Change Detected</h2>
                <p><strong>Watch:</strong> {payload.WatchName}</p>
                <p><strong>URL:</strong> <a href="{payload.WatchUrl}">{payload.WatchUrl}</a></p>
                <p><strong>Detected at:</strong> {payload.DetectedAt:g}</p>
                <p><strong>Importance:</strong> {payload.Importance}</p>
                <hr>
                <p>{message}</p>
                <hr>
                <p><strong>Changes:</strong> +{payload.LinesAdded} / -{payload.LinesRemoved} lines</p>
                """,
            TextBody = $"""
                Change Detected

                Watch: {payload.WatchName}
                URL: {payload.WatchUrl}
                Detected at: {payload.DetectedAt:g}
                Importance: {payload.Importance}

                {message}

                Changes: +{payload.LinesAdded} / -{payload.LinesRemoved} lines
                """
        };

        email.Body = bodyBuilder.ToMessageBody();

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(emailSettings.SmtpHost, emailSettings.SmtpPort, emailSettings.UseSsl, ct);

        if (!string.IsNullOrEmpty(emailSettings.Username))
        {
            await smtp.AuthenticateAsync(emailSettings.Username, emailSettings.Password, ct);
        }

        await smtp.SendAsync(email, ct);
        await smtp.DisconnectAsync(true, ct);

        logger.LogDebug("Sent email notification to {Address}", entry.Destination);
    }

    private async Task SendWebhookAsync(NotificationOutboxEntry entry, CancellationToken ct)
    {
        var payload = ChangeNotificationPayload.FromJson(entry.PayloadJson);
        if (payload == null)
            throw new InvalidOperationException("Invalid notification payload");

        var client = httpClientFactory.CreateClient();
        var message = payload.Summary ?? payload.DiffSummary ?? "A change was detected";

        var webhookPayload = new
        {
            watchId = payload.WatchId,
            watchName = payload.WatchName,
            watchUrl = payload.WatchUrl,
            changeId = payload.ChangeEventId,
            detectedAt = payload.DetectedAt,
            importance = payload.Importance,
            linesAdded = payload.LinesAdded,
            linesRemoved = payload.LinesRemoved,
            summary = message
        };

        var response = await client.PostAsJsonAsync(entry.Destination, webhookPayload, ct);
        response.EnsureSuccessStatusCode();

        logger.LogDebug("Sent webhook notification to {Url}", entry.Destination);
    }

    private async Task SendDiscordAsync(NotificationOutboxEntry entry, CancellationToken ct)
    {
        var payload = ChangeNotificationPayload.FromJson(entry.PayloadJson);
        if (payload == null)
            throw new InvalidOperationException("Invalid notification payload");

        var client = httpClientFactory.CreateClient();
        var message = payload.Summary ?? payload.DiffSummary ?? "A change was detected";

        var discordPayload = new
        {
            embeds = new[]
            {
                new
                {
                    title = $"🔔 Change Detected: {payload.WatchName}",
                    url = payload.WatchUrl,
                    description = message,
                    color = GetDiscordColorForImportance(payload.Importance),
                    fields = new[]
                    {
                        new { name = "URL", value = payload.WatchUrl, inline = true },
                        new { name = "Importance", value = payload.Importance, inline = true },
                        new { name = "Changes", value = $"+{payload.LinesAdded} / -{payload.LinesRemoved} lines", inline = true }
                    },
                    timestamp = payload.DetectedAt.ToString("o")
                }
            }
        };

        var response = await client.PostAsJsonAsync(entry.Destination, discordPayload, ct);
        response.EnsureSuccessStatusCode();

        logger.LogDebug("Sent Discord notification to webhook");
    }

    private async Task SendAlertEmailAsync(NotificationOutboxEntry entry, CancellationToken ct)
    {
        var payload = AlertNotificationPayload.FromJson(entry.PayloadJson);
        if (payload == null)
            throw new InvalidOperationException("Invalid alert payload");

        var appSettings = (await settingsRepo.GetAllAsync(ct)).FirstOrDefault();
        var emailSettings = appSettings?.Email;

        if (emailSettings == null || string.IsNullOrEmpty(emailSettings.SmtpHost))
            throw new InvalidOperationException("Email settings not configured");

        var alertDetails = string.Join("\n", payload.TriggeredThresholds.Select(t =>
            $"• {t.ThresholdName}: {t.Message}"));

        var email = new MimeMessage();
        email.From.Add(new MailboxAddress(
            emailSettings.FromName ?? "Change Detection",
            emailSettings.FromAddress ?? "noreply@changedetection.local"));
        email.To.Add(MailboxAddress.Parse(entry.Destination));
        email.Subject = $"🚨 [{payload.HighestImportance}] Alert: {payload.WatchName}";
        email.Body = new TextPart("plain")
        {
            Text = $"{payload.CombinedMessage}\n\n{alertDetails}"
        };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(emailSettings.SmtpHost, emailSettings.SmtpPort, emailSettings.UseSsl, ct);

        if (!string.IsNullOrEmpty(emailSettings.Username))
        {
            await smtp.AuthenticateAsync(emailSettings.Username, emailSettings.Password, ct);
        }

        await smtp.SendAsync(email, ct);
        await smtp.DisconnectAsync(true, ct);

        logger.LogDebug("Sent alert email to {Address}", entry.Destination);
    }

    private async Task SendAlertWebhookAsync(NotificationOutboxEntry entry, CancellationToken ct)
    {
        var payload = AlertNotificationPayload.FromJson(entry.PayloadJson);
        if (payload == null)
            throw new InvalidOperationException("Invalid alert payload");

        var client = httpClientFactory.CreateClient();

        var webhookPayload = new
        {
            watchId = payload.WatchId,
            watchName = payload.WatchName,
            watchUrl = payload.WatchUrl,
            type = "alert",
            message = payload.CombinedMessage,
            importance = payload.HighestImportance,
            thresholds = payload.TriggeredThresholds.Select(t => new
            {
                field = t.FieldName,
                name = t.ThresholdName,
                message = t.Message,
                currentValue = t.CurrentValue,
                changePercent = t.ChangePercent
            })
        };

        var response = await client.PostAsJsonAsync(entry.Destination, webhookPayload, ct);
        response.EnsureSuccessStatusCode();

        logger.LogDebug("Sent alert webhook to {Url}", entry.Destination);
    }

    private async Task SendAlertDiscordAsync(NotificationOutboxEntry entry, CancellationToken ct)
    {
        var payload = AlertNotificationPayload.FromJson(entry.PayloadJson);
        if (payload == null)
            throw new InvalidOperationException("Invalid alert payload");

        var client = httpClientFactory.CreateClient();

        var alertDetails = string.Join("\n", payload.TriggeredThresholds.Select(t =>
            $"• **{t.ThresholdName}**: {t.Message}"));

        var discordPayload = new
        {
            embeds = new[]
            {
                new
                {
                    title = $"🚨 Alert: {payload.WatchName}",
                    url = payload.WatchUrl,
                    description = $"{payload.CombinedMessage}\n\n{alertDetails}",
                    color = GetDiscordColorForImportance(payload.HighestImportance)
                }
            }
        };

        var response = await client.PostAsJsonAsync(entry.Destination, discordPayload, ct);
        response.EnsureSuccessStatusCode();

        logger.LogDebug("Sent alert Discord notification");
    }

    private static int GetDiscordColorForImportance(string importance)
    {
        return importance.ToLowerInvariant() switch
        {
            "critical" => 0xFF0000,  // Red
            "high" => 0xFF6600,      // Orange
            "medium" => 0xFFCC00,    // Yellow
            "low" => 0x00CC00,       // Green
            _ => 0x0099FF            // Blue (default)
        };
    }
}
