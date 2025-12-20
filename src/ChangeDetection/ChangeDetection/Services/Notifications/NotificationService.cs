using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using MailKit.Net.Smtp;
using MimeKit;

namespace ChangeDetection.Services.Notifications;

/// <summary>
/// Service for sending notifications about detected changes.
/// </summary>
public class NotificationService : INotificationService
{
    private readonly IRepository<AppSettings> _settingsRepo;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        IRepository<AppSettings> settingsRepo,
        IHttpClientFactory httpClientFactory,
        ILogger<NotificationService> logger)
    {
        _settingsRepo = settingsRepo;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task SendNotificationAsync(WatchedSite watch, ChangeEvent change, string? summary = null, CancellationToken ct = default)
    {
        var settings = watch.Notifications;
        var message = summary ?? change.DiffSummary ?? "A change was detected";

        var tasks = new List<Task>();

        if (settings.EmailEnabled && !string.IsNullOrEmpty(settings.EmailAddress))
        {
            tasks.Add(SendEmailAsync(watch, change, message, settings.EmailAddress, ct));
        }

        if (settings.WebhookEnabled && !string.IsNullOrEmpty(settings.WebhookUrl))
        {
            tasks.Add(SendWebhookAsync(watch, change, message, settings.WebhookUrl, ct));
        }

        if (settings.DiscordEnabled && !string.IsNullOrEmpty(settings.DiscordWebhookUrl))
        {
            tasks.Add(SendDiscordAsync(watch, change, message, settings.DiscordWebhookUrl, ct));
        }

        await Task.WhenAll(tasks);
    }

    public async Task SendTestNotificationAsync(NotificationSettings settings, CancellationToken ct = default)
    {
        var testWatch = new WatchedSite
        {
            Url = "https://example.com",
            Name = "Test Watch"
        };

        var testChange = new ChangeEvent
        {
            DiffSummary = "This is a test notification",
            Importance = ChangeImportance.Medium
        };

        if (settings.EmailEnabled && !string.IsNullOrEmpty(settings.EmailAddress))
        {
            await SendEmailAsync(testWatch, testChange, "Test notification", settings.EmailAddress, ct);
        }

        if (settings.WebhookEnabled && !string.IsNullOrEmpty(settings.WebhookUrl))
        {
            await SendWebhookAsync(testWatch, testChange, "Test notification", settings.WebhookUrl, ct);
        }

        if (settings.DiscordEnabled && !string.IsNullOrEmpty(settings.DiscordWebhookUrl))
        {
            await SendDiscordAsync(testWatch, testChange, "Test notification", settings.DiscordWebhookUrl, ct);
        }
    }

    private async Task SendEmailAsync(WatchedSite watch, ChangeEvent change, string message, string toAddress, CancellationToken ct)
    {
        try
        {
            var appSettings = (await _settingsRepo.GetAllAsync(ct)).FirstOrDefault();
            var emailSettings = appSettings?.Email;

            if (emailSettings == null || string.IsNullOrEmpty(emailSettings.SmtpHost))
            {
                _logger.LogWarning("Email settings not configured");
                return;
            }

            var email = new MimeMessage();
            email.From.Add(new MailboxAddress(
                emailSettings.FromName ?? "Change Detection",
                emailSettings.FromAddress ?? "noreply@changedetection.local"));
            email.To.Add(MailboxAddress.Parse(toAddress));
            email.Subject = $"Change detected: {watch.Name ?? watch.Url}";

            var bodyBuilder = new BodyBuilder
            {
                HtmlBody = $"""
                    <h2>Change Detected</h2>
                    <p><strong>Watch:</strong> {watch.Name ?? watch.Url}</p>
                    <p><strong>URL:</strong> <a href="{watch.Url}">{watch.Url}</a></p>
                    <p><strong>Detected at:</strong> {change.DetectedAt:g}</p>
                    <p><strong>Importance:</strong> {change.Importance}</p>
                    <hr>
                    <p>{message}</p>
                    <hr>
                    <p><strong>Changes:</strong> +{change.LinesAdded} / -{change.LinesRemoved} lines</p>
                    """,
                TextBody = $"""
                    Change Detected
                    
                    Watch: {watch.Name ?? watch.Url}
                    URL: {watch.Url}
                    Detected at: {change.DetectedAt:g}
                    Importance: {change.Importance}
                    
                    {message}
                    
                    Changes: +{change.LinesAdded} / -{change.LinesRemoved} lines
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

            _logger.LogInformation("Email notification sent to {Address}", toAddress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email notification to {Address}", toAddress);
            throw;
        }
    }

    private async Task SendWebhookAsync(WatchedSite watch, ChangeEvent change, string message, string webhookUrl, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();

            var payload = new
            {
                watchId = watch.Id,
                watchName = watch.Name ?? watch.Url,
                watchUrl = watch.Url,
                changeId = change.Id,
                detectedAt = change.DetectedAt,
                importance = change.Importance.ToString(),
                linesAdded = change.LinesAdded,
                linesRemoved = change.LinesRemoved,
                summary = message
            };

            var response = await client.PostAsJsonAsync(webhookUrl, payload, ct);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation("Webhook notification sent to {Url}", webhookUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send webhook notification to {Url}", webhookUrl);
            throw;
        }
    }

    public async Task SendAlertAsync(WatchedSite watch, AlertEvaluationResult alertResult, NotificationContext context, CancellationToken ct = default)
    {
        if (!alertResult.HasTriggeredAlerts)
        {
            return;
        }

        var settings = watch.Notifications;
        var message = alertResult.CombinedMessage ?? "Alert threshold triggered";

        // Build a rich message with all triggered thresholds
        var alertDetails = string.Join("\n", alertResult.TriggeredThresholds.Select(t =>
            $"• {t.Threshold.Name ?? t.Field.Name}: {t.Message}"));

        var fullMessage = $"{message}\n\n{alertDetails}";

        var tasks = new List<Task>();

        if (settings.EmailEnabled && !string.IsNullOrEmpty(settings.EmailAddress))
        {
            tasks.Add(SendAlertEmailAsync(watch, alertResult, fullMessage, settings.EmailAddress, ct));
        }

        if (settings.WebhookEnabled && !string.IsNullOrEmpty(settings.WebhookUrl))
        {
            tasks.Add(SendAlertWebhookAsync(watch, alertResult, fullMessage, settings.WebhookUrl, ct));
        }

        if (settings.DiscordEnabled && !string.IsNullOrEmpty(settings.DiscordWebhookUrl))
        {
            tasks.Add(SendAlertDiscordAsync(watch, alertResult, fullMessage, settings.DiscordWebhookUrl, ct));
        }

        await Task.WhenAll(tasks);
    }

    private async Task SendAlertEmailAsync(WatchedSite watch, AlertEvaluationResult alertResult, string message, string emailAddress, CancellationToken ct)
    {
        try
        {
            var appSettings = (await _settingsRepo.GetAllAsync(ct)).FirstOrDefault();
            var emailSettings = appSettings?.Email;

            if (emailSettings == null || string.IsNullOrEmpty(emailSettings.SmtpHost))
            {
                _logger.LogWarning("Email settings not configured, skipping alert email");
                return;
            }

            var importance = alertResult.HighestImportance ?? ChangeImportance.Medium;
            var alertCount = alertResult.TriggeredThresholds.Count;
            var subject = $"🚨 [{importance}] Alert: {watch.Name ?? watch.Url} - {alertCount} threshold(s) triggered";

            var email = new MimeMessage();
            email.From.Add(new MailboxAddress(
                emailSettings.FromName ?? "Change Detection",
                emailSettings.FromAddress ?? "noreply@changedetection.local"));
            email.To.Add(MailboxAddress.Parse(emailAddress));
            email.Subject = subject;
            email.Body = new TextPart("plain") { Text = message };

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(emailSettings.SmtpHost, emailSettings.SmtpPort, emailSettings.UseSsl, ct);

            if (!string.IsNullOrEmpty(emailSettings.Username))
            {
                await smtp.AuthenticateAsync(emailSettings.Username, emailSettings.Password, ct);
            }

            await smtp.SendAsync(email, ct);
            await smtp.DisconnectAsync(true, ct);

            _logger.LogInformation("Alert email sent to {Email} for {AlertCount} thresholds", emailAddress, alertCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send alert email to {Email}", emailAddress);
            throw;
        }
    }

    private async Task SendAlertWebhookAsync(WatchedSite watch, AlertEvaluationResult alertResult, string message, string webhookUrl, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();

            var payload = new
            {
                type = "alert",
                watch = new
                {
                    id = watch.Id,
                    name = watch.Name,
                    url = watch.Url
                },
                alert = new
                {
                    message,
                    importance = alertResult.HighestImportance?.ToString() ?? "Medium",
                    thresholdCount = alertResult.TriggeredThresholds.Count,
                    thresholds = alertResult.TriggeredThresholds.Select(t => new
                    {
                        fieldName = t.Field.Name,
                        thresholdName = t.Threshold.Name,
                        conditionType = t.Threshold.ConditionType.ToString(),
                        oldValue = t.OldValue,
                        newValue = t.NewValue,
                        change = t.CalculatedChange,
                        message = t.Message
                    })
                },
                timestamp = DateTime.UtcNow
            };

            var response = await client.PostAsJsonAsync(webhookUrl, payload, ct);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation("Alert webhook sent to {Url}", webhookUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send alert webhook to {Url}", webhookUrl);
            throw;
        }
    }

    private async Task SendAlertDiscordAsync(WatchedSite watch, AlertEvaluationResult alertResult, string message, string webhookUrl, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();

            var importance = alertResult.HighestImportance ?? ChangeImportance.Medium;
            var color = importance switch
            {
                ChangeImportance.Critical => 15158332, // Red
                ChangeImportance.High => 15105570,     // Orange
                ChangeImportance.Medium => 16776960,   // Yellow
                _ => 3066993                            // Green
            };

            var fields = alertResult.TriggeredThresholds.Select(t => new
            {
                name = t.Threshold.Name ?? t.Field.Name,
                value = $"{t.Message}\nOld: {t.OldValue:F2} → New: {t.NewValue:F2}",
                inline = true
            }).ToArray();

            var payload = new
            {
                embeds = new[]
                {
                    new
                    {
                        title = $"🚨 Alert: {watch.Name ?? watch.Url}",
                        url = watch.Url,
                        color,
                        description = alertResult.CombinedMessage ?? "Alert thresholds triggered",
                        fields,
                        timestamp = DateTime.UtcNow.ToString("o")
                    }
                }
            };

            var response = await client.PostAsJsonAsync(webhookUrl, payload, ct);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation("Alert Discord notification sent");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send alert Discord notification");
            throw;
        }
    }

    private async Task SendDiscordAsync(WatchedSite watch, ChangeEvent change, string message, string webhookUrl, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();

            var color = change.Importance switch
            {
                ChangeImportance.Critical => 15158332, // Red
                ChangeImportance.High => 15105570,     // Orange
                ChangeImportance.Medium => 16776960,   // Yellow
                _ => 3066993                            // Green
            };

            var payload = new
            {
                embeds = new[]
                {
                    new
                    {
                        title = $"🔔 Change Detected: {watch.Name ?? watch.Url}",
                        url = watch.Url,
                        color,
                        description = message,
                        fields = new[]
                        {
                            new { name = "URL", value = watch.Url, inline = true },
                            new { name = "Importance", value = change.Importance.ToString(), inline = true },
                            new { name = "Changes", value = $"+{change.LinesAdded} / -{change.LinesRemoved}", inline = true }
                        },
                        timestamp = change.DetectedAt.ToString("o")
                    }
                }
            };

            var response = await client.PostAsJsonAsync(webhookUrl, payload, ct);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation("Discord notification sent");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Discord notification");
            throw;
        }
    }
}
