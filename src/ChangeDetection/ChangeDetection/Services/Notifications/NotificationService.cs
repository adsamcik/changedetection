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
    private sealed record ResolvedChannel(NotificationChannelType Type, string? Destination, string Name);

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

    public async Task SendNotificationAsync(
        WatchedSite watch,
        ChangeEvent change,
        string? summary = null,
        string? channelName = null,
        CancellationToken ct = default)
    {
        var settings = await ResolveSettingsAsync(watch, ct);
        var message = summary ?? change.DiffSummary ?? "A change was detected";
        var channels = ResolveChannels(settings, channelName);
        await Task.WhenAll(channels.Select(channel => SendChangeViaChannelAsync(channel, watch, change, message, ct)));
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

        var channels = ResolveChannels(settings, requestedChannelName: null);
        await Task.WhenAll(channels.Select(channel => SendChangeViaChannelAsync(channel, testWatch, testChange, "Test notification", ct)));
    }

    private async Task<NotificationSettings> ResolveSettingsAsync(WatchedSite watch, CancellationToken ct)
    {
        if (HasConfiguredDestinations(watch.Notifications))
        {
            return watch.Notifications;
        }

        var appSettings = (await _settingsRepo.GetAllAsync(ct)).FirstOrDefault();
        return appSettings?.DefaultNotifications ?? watch.Notifications;
    }

    private static bool HasConfiguredDestinations(NotificationSettings settings)
    {
        if (settings.Channels.Any(c => c.IsEnabled))
        {
            return true;
        }

        return (settings.EmailEnabled && !string.IsNullOrWhiteSpace(settings.EmailAddress))
               || (settings.WebhookEnabled && !string.IsNullOrWhiteSpace(settings.WebhookUrl))
               || (settings.DiscordEnabled && !string.IsNullOrWhiteSpace(settings.DiscordWebhookUrl));
    }

    private IEnumerable<ResolvedChannel> ResolveChannels(NotificationSettings settings, string? requestedChannelName)
    {
        var namedChannels = settings.Channels.Count > 0
            ? settings.Channels
            : BuildLegacyChannels(settings);

        IEnumerable<NotificationChannel> candidates = namedChannels
            .Where(c => c.IsEnabled)
            .ToList();

        if (!string.IsNullOrWhiteSpace(requestedChannelName))
        {
            candidates = candidates.Where(c =>
                string.Equals(c.Name, requestedChannelName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Type.ToString(), requestedChannelName, StringComparison.OrdinalIgnoreCase));
        }
        else if (!string.IsNullOrWhiteSpace(settings.DefaultChannelName))
        {
            candidates = candidates.Where(c => string.Equals(c.Name, settings.DefaultChannelName, StringComparison.OrdinalIgnoreCase));
        }

        return candidates
            .Select(channel => new ResolvedChannel(channel.Type, GetDestination(channel, settings), channel.Name))
            .Where(channel => channel.Type == NotificationChannelType.Browser || !string.IsNullOrWhiteSpace(channel.Destination))
            .ToList();
    }

    private static List<NotificationChannel> BuildLegacyChannels(NotificationSettings settings)
    {
        var channels = new List<NotificationChannel>();

        if (!string.IsNullOrWhiteSpace(settings.EmailAddress))
        {
            channels.Add(new NotificationChannel
            {
                Name = "email",
                Type = NotificationChannelType.Email,
                IsEnabled = settings.EmailEnabled,
                Config = new Dictionary<string, string> { ["address"] = settings.EmailAddress }
            });
        }

        if (!string.IsNullOrWhiteSpace(settings.WebhookUrl))
        {
            channels.Add(new NotificationChannel
            {
                Name = "webhook",
                Type = NotificationChannelType.Webhook,
                IsEnabled = settings.WebhookEnabled,
                Config = new Dictionary<string, string> { ["url"] = settings.WebhookUrl }
            });
        }

        if (!string.IsNullOrWhiteSpace(settings.DiscordWebhookUrl))
        {
            channels.Add(new NotificationChannel
            {
                Name = "discord",
                Type = NotificationChannelType.Discord,
                IsEnabled = settings.DiscordEnabled,
                Config = new Dictionary<string, string> { ["webhookUrl"] = settings.DiscordWebhookUrl }
            });
        }

        return channels;
    }

    private static string? GetDestination(NotificationChannel channel, NotificationSettings settings)
    {
        return channel.Type switch
        {
            NotificationChannelType.Email => channel.Config.GetValueOrDefault("address") ?? settings.EmailAddress,
            NotificationChannelType.Webhook => channel.Config.GetValueOrDefault("url") ?? settings.WebhookUrl,
            NotificationChannelType.Discord => channel.Config.GetValueOrDefault("webhookUrl")
                                              ?? channel.Config.GetValueOrDefault("url")
                                              ?? settings.DiscordWebhookUrl,
            NotificationChannelType.Browser => null,
            _ => null
        };
    }

    private Task SendChangeViaChannelAsync(
        ResolvedChannel channel,
        WatchedSite watch,
        ChangeEvent change,
        string message,
        CancellationToken ct)
    {
        return channel.Type switch
        {
            NotificationChannelType.Email => SendEmailAsync(watch, change, message, channel.Destination!, ct),
            NotificationChannelType.Webhook => SendWebhookAsync(watch, change, message, channel.Destination!, ct),
            NotificationChannelType.Discord => SendDiscordAsync(watch, change, message, channel.Destination!, ct),
            NotificationChannelType.Browser => LogBrowserNotificationAsync(watch, message),
            _ => Task.CompletedTask
        };
    }

    private Task SendAlertViaChannelAsync(
        ResolvedChannel channel,
        WatchedSite watch,
        AlertEvaluationResult alertResult,
        string message,
        CancellationToken ct)
    {
        return channel.Type switch
        {
            NotificationChannelType.Email => SendAlertEmailAsync(watch, alertResult, message, channel.Destination!, ct),
            NotificationChannelType.Webhook => SendAlertWebhookAsync(watch, alertResult, message, channel.Destination!, ct),
            NotificationChannelType.Discord => SendAlertDiscordAsync(watch, alertResult, message, channel.Destination!, ct),
            NotificationChannelType.Browser => LogBrowserNotificationAsync(watch, message),
            _ => Task.CompletedTask
        };
    }

    private Task LogBrowserNotificationAsync(WatchedSite watch, string message)
    {
        _logger.LogInformation(
            "Browser notification requested for watch {WatchId} ({WatchName}), but browser push delivery is not yet available. Message: {Message}",
            watch.Id,
            watch.Name ?? watch.Url,
            message);
        return Task.CompletedTask;
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

        var settings = await ResolveSettingsAsync(watch, ct);
        var message = alertResult.CombinedMessage ?? "Alert threshold triggered";

        // Build a rich message with all triggered thresholds
        var alertDetails = string.Join("\n", alertResult.TriggeredThresholds.Select(t =>
            $"• {t.Threshold.Name ?? t.Field.Name}: {t.Message}"));

        var fullMessage = $"{message}\n\n{alertDetails}";

        var channels = ResolveChannels(settings, requestedChannelName: null);
        await Task.WhenAll(channels.Select(channel => SendAlertViaChannelAsync(channel, watch, alertResult, fullMessage, ct)));
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
