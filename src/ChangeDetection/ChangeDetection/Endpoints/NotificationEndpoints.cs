using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Shared.Dtos;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Endpoints;

/// <summary>
/// API endpoints for notification settings and templates.
/// </summary>
public static class NotificationEndpoints
{
    public static RouteGroupBuilder MapNotificationEndpoints(this RouteGroupBuilder group)
    {
        // Templates
        group.MapGet("/templates", GetAllTemplates)
            .WithName("GetNotificationTemplates")
            .Produces<List<NotificationTemplateDto>>();

        group.MapGet("/templates/{id:guid}", GetTemplateById)
            .WithName("GetNotificationTemplate")
            .Produces<NotificationTemplateDto>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/templates", CreateTemplate)
            .WithName("CreateNotificationTemplate")
            .Produces<NotificationTemplateDto>(StatusCodes.Status201Created);

        group.MapPut("/templates/{id:guid}", UpdateTemplate)
            .WithName("UpdateNotificationTemplate")
            .Produces<NotificationTemplateDto>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/templates/{id:guid}", DeleteTemplate)
            .WithName("DeleteNotificationTemplate")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        // Placeholders
        group.MapGet("/placeholders", GetPlaceholders)
            .WithName("GetNotificationPlaceholders")
            .Produces<List<PlaceholderInfoDto>>();

        group.MapPost("/templates/validate", ValidateTemplate)
            .WithName("ValidateNotificationTemplate")
            .Produces<TemplateValidationResultDto>();

        // SMTP Settings
        group.MapGet("/smtp", GetSmtpSettings)
            .WithName("GetSmtpSettings")
            .Produces<SmtpSettingsDto>();

        group.MapPut("/smtp", UpdateSmtpSettings)
            .WithName("UpdateSmtpSettings")
            .Produces<SmtpSettingsDto>();

        // Test notification
        group.MapPost("/test", SendTestNotification)
            .WithName("SendTestNotification")
            .Produces<TestNotificationResultDto>();

        return group;
    }

    private static async Task<IResult> GetAllTemplates(
        IRepository<NotificationTemplate> templateRepo,
        INotificationTemplateEngine templateEngine,
        CancellationToken ct)
    {
        var templates = await templateRepo.GetAllAsync(ct);
        
        // Include built-in defaults that aren't overridden
        var allTypes = Enum.GetValues<NotificationTemplateType>();
        var templateDtos = new List<NotificationTemplateDto>();
        
        foreach (var type in allTypes)
        {
            var effectiveTemplate = await templateEngine.GetEffectiveTemplateAsync(type, ct: ct);
            templateDtos.Add(MapToDto(effectiveTemplate));
        }

        // Also add any custom templates that don't override defaults
        foreach (var template in templates.Where(t => !t.IsBuiltIn))
        {
            if (!templateDtos.Any(t => t.Id == template.Id))
            {
                templateDtos.Add(MapToDto(template));
            }
        }

        return Results.Ok(templateDtos.OrderBy(t => t.Type).ThenBy(t => t.Name).ToList());
    }

    private static async Task<IResult> GetTemplateById(
        Guid id,
        IRepository<NotificationTemplate> templateRepo,
        INotificationTemplateEngine templateEngine,
        CancellationToken ct)
    {
        // Check if it's a built-in template ID
        foreach (var type in Enum.GetValues<NotificationTemplateType>())
        {
            var defaultTemplate = await templateEngine.GetEffectiveTemplateAsync(type, ct: ct);
            if (defaultTemplate.Id == id)
            {
                return Results.Ok(MapToDto(defaultTemplate));
            }
        }

        var template = await templateRepo.GetByIdAsync(id, ct);
        if (template == null)
            return Results.NotFound();

        return Results.Ok(MapToDto(template));
    }

    private static async Task<IResult> CreateTemplate(
        NotificationTemplateCreateDto dto,
        IRepository<NotificationTemplate> templateRepo,
        CancellationToken ct)
    {
        if (!Enum.TryParse<NotificationTemplateType>(dto.Type, ignoreCase: true, out var type))
        {
            return Results.BadRequest($"Invalid template type: {dto.Type}");
        }

        var template = new NotificationTemplate
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            Type = type,
            IsBuiltIn = false,
            GenerateLlmSummary = dto.GenerateLlmSummary,
            EmailSubjectTemplate = dto.EmailSubjectTemplate,
            EmailBodyHtmlTemplate = dto.EmailBodyHtmlTemplate,
            EmailBodyTextTemplate = dto.EmailBodyTextTemplate,
            DiscordTitleTemplate = dto.DiscordTitleTemplate,
            DiscordBodyTemplate = dto.DiscordBodyTemplate,
            WebhookPayloadTemplate = dto.WebhookPayloadTemplate,
            CreatedAt = DateTime.UtcNow
        };

        await templateRepo.InsertAsync(template, ct);

        return Results.Created($"/api/notifications/templates/{template.Id}", MapToDto(template));
    }

    private static async Task<IResult> UpdateTemplate(
        Guid id,
        NotificationTemplateCreateDto dto,
        IRepository<NotificationTemplate> templateRepo,
        CancellationToken ct)
    {
        var template = await templateRepo.GetByIdAsync(id, ct);
        if (template == null)
            return Results.NotFound();

        if (template.IsBuiltIn)
        {
            // For built-in templates, create an override instead
            var newTemplate = new NotificationTemplate
            {
                Id = Guid.NewGuid(),
                Name = dto.Name,
                Type = template.Type,
                IsBuiltIn = false,
                GenerateLlmSummary = dto.GenerateLlmSummary,
                EmailSubjectTemplate = dto.EmailSubjectTemplate,
                EmailBodyHtmlTemplate = dto.EmailBodyHtmlTemplate,
                EmailBodyTextTemplate = dto.EmailBodyTextTemplate,
                DiscordTitleTemplate = dto.DiscordTitleTemplate,
                DiscordBodyTemplate = dto.DiscordBodyTemplate,
                WebhookPayloadTemplate = dto.WebhookPayloadTemplate,
                CreatedAt = DateTime.UtcNow
            };

            await templateRepo.InsertAsync(newTemplate, ct);
            return Results.Ok(MapToDto(newTemplate));
        }

        if (!Enum.TryParse<NotificationTemplateType>(dto.Type, ignoreCase: true, out var type))
        {
            return Results.BadRequest($"Invalid template type: {dto.Type}");
        }

        template.Name = dto.Name;
        template.Type = type;
        template.GenerateLlmSummary = dto.GenerateLlmSummary;
        template.EmailSubjectTemplate = dto.EmailSubjectTemplate;
        template.EmailBodyHtmlTemplate = dto.EmailBodyHtmlTemplate;
        template.EmailBodyTextTemplate = dto.EmailBodyTextTemplate;
        template.DiscordTitleTemplate = dto.DiscordTitleTemplate;
        template.DiscordBodyTemplate = dto.DiscordBodyTemplate;
        template.WebhookPayloadTemplate = dto.WebhookPayloadTemplate;
        template.ModifiedAt = DateTime.UtcNow;

        await templateRepo.UpdateAsync(template, ct);

        return Results.Ok(MapToDto(template));
    }

    private static async Task<IResult> DeleteTemplate(
        Guid id,
        IRepository<NotificationTemplate> templateRepo,
        CancellationToken ct)
    {
        var template = await templateRepo.GetByIdAsync(id, ct);
        if (template == null)
            return Results.NotFound();

        if (template.IsBuiltIn)
            return Results.BadRequest("Cannot delete built-in templates");

        await templateRepo.DeleteAsync(id, ct);
        return Results.NoContent();
    }

    private static IResult GetPlaceholders(INotificationTemplateEngine templateEngine)
    {
        var placeholders = templateEngine.GetAvailablePlaceholders()
            .Select(kvp => new PlaceholderInfoDto
            {
                Name = kvp.Key,
                Description = kvp.Value
            })
            .OrderBy(p => p.Name)
            .ToList();

        return Results.Ok(placeholders);
    }

    private static IResult ValidateTemplate(
        string template,
        INotificationTemplateEngine templateEngine)
    {
        var result = templateEngine.ValidatePlaceholders(template);
        var dto = new TemplateValidationResultDto
        {
            IsValid = result.IsValid,
            UnknownPlaceholders = result.UnknownPlaceholders,
            Warnings = result.Warnings,
            Errors = result.Errors
        };

        return Results.Ok(dto);
    }

    private static async Task<IResult> GetSmtpSettings(
        IRepository<AppSettings> settingsRepo,
        CancellationToken ct)
    {
        var allSettings = await settingsRepo.GetAllAsync(ct);
        var settings = allSettings.FirstOrDefault() ?? new AppSettings();
        var email = settings.Email ?? new EmailSettings();

        var dto = new SmtpSettingsDto
        {
            Enabled = !string.IsNullOrEmpty(email.SmtpHost),
            Host = email.SmtpHost ?? "",
            Port = email.SmtpPort,
            UseSsl = email.UseSsl,
            Username = email.Username,
            FromEmail = email.FromAddress,
            FromName = email.FromName
        };

        return Results.Ok(dto);
    }

    private static async Task<IResult> UpdateSmtpSettings(
        SmtpSettingsUpdateDto update,
        IRepository<AppSettings> settingsRepo,
        CancellationToken ct)
    {
        var allSettings = await settingsRepo.GetAllAsync(ct);
        var settings = allSettings.FirstOrDefault();
        var isNew = settings == null;
        settings ??= new AppSettings();
        settings.Email ??= new EmailSettings();

        if (update.Host is not null)
            settings.Email.SmtpHost = string.IsNullOrWhiteSpace(update.Host) ? null : update.Host;
        
        if (update.Port.HasValue)
            settings.Email.SmtpPort = update.Port.Value;
        
        if (update.UseSsl.HasValue)
            settings.Email.UseSsl = update.UseSsl.Value;
        
        if (update.Username is not null)
            settings.Email.Username = string.IsNullOrWhiteSpace(update.Username) ? null : update.Username;
        
        if (update.Password is not null && !string.IsNullOrWhiteSpace(update.Password))
            settings.Email.Password = update.Password;
        
        if (update.FromEmail is not null)
            settings.Email.FromAddress = string.IsNullOrWhiteSpace(update.FromEmail) ? null : update.FromEmail;
        
        if (update.FromName is not null)
            settings.Email.FromName = string.IsNullOrWhiteSpace(update.FromName) ? null : update.FromName;

        if (isNew)
            await settingsRepo.InsertAsync(settings, ct);
        else
            await settingsRepo.UpdateAsync(settings, ct);

        var dto = new SmtpSettingsDto
        {
            Enabled = !string.IsNullOrEmpty(settings.Email.SmtpHost),
            Host = settings.Email.SmtpHost ?? "",
            Port = settings.Email.SmtpPort,
            UseSsl = settings.Email.UseSsl,
            Username = settings.Email.Username,
            FromEmail = settings.Email.FromAddress,
            FromName = settings.Email.FromName
        };

        return Results.Ok(dto);
    }

    private static async Task<IResult> SendTestNotification(
        TestNotificationDto dto,
        IRepository<NotificationTemplate> templateRepo,
        INotificationTemplateEngine templateEngine,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("NotificationEndpoints");
        try
        {
            // Create a fake watch for the test
            var testWatch = new WatchedSite
            {
                Id = Guid.Empty,
                Url = "https://example.com/test",
                Name = "Test Watch",
                CreatedAt = DateTime.UtcNow
            };

            // Create test context
            var context = new NotificationContext
            {
                Watch = testWatch,
                Change = new ChangeEvent
                {
                    Id = Guid.Empty,
                    WatchedSiteId = Guid.Empty,
                    DetectedAt = DateTime.UtcNow,
                    LinesAdded = 5,
                    LinesRemoved = 3
                },
                OldPrice = 100.00m,
                NewPrice = 89.99m,
                Currency = "USD",
                ChangePercent = -10.01,
                ChangeAbsolute = -10.01,
                ChangeDirection = "decreased"
            };

            // Get template
            NotificationTemplate template;
            if (dto.TemplateId.HasValue)
            {
                template = await templateRepo.GetByIdAsync(dto.TemplateId.Value, ct)
                    ?? await templateEngine.GetEffectiveTemplateAsync(NotificationTemplateType.ContentChange, ct: ct);
            }
            else
            {
                template = await templateEngine.GetEffectiveTemplateAsync(NotificationTemplateType.ContentChange, ct: ct);
            }

            // Render content based on channel type to validate templates
            string renderedContent;
            switch (dto.ChannelType.ToUpperInvariant())
            {
                case "EMAIL":
                    var subject = await templateEngine.RenderAsync(template.EmailSubjectTemplate ?? "Test Notification", context, ct);
                    var body = await templateEngine.RenderAsync(template.EmailBodyHtmlTemplate ?? "This is a test notification.", context, ct);
                    renderedContent = $"Subject: {subject}\n\nBody:\n{body}";
                    logger.LogInformation("Test email rendered for {Target}: {Subject}", dto.Target, subject);
                    break;

                case "DISCORD":
                    var discordTitle = await templateEngine.RenderAsync(template.DiscordTitleTemplate ?? "Test", context, ct);
                    var discordBody = await templateEngine.RenderAsync(template.DiscordBodyTemplate ?? "This is a test notification.", context, ct);
                    renderedContent = $"Title: {discordTitle}\n\nBody:\n{discordBody}";
                    logger.LogInformation("Test Discord notification rendered for {Target}: {Title}", dto.Target, discordTitle);
                    break;

                case "WEBHOOK":
                    var webhookPayload = template.WebhookPayloadTemplate is not null
                        ? await templateEngine.RenderAsync(template.WebhookPayloadTemplate, context, ct)
                        : "{ \"type\": \"test\", \"message\": \"Test webhook notification\" }";
                    renderedContent = $"Payload:\n{webhookPayload}";
                    logger.LogInformation("Test webhook rendered for {Target}", dto.Target);
                    break;

                default:
                    return Results.BadRequest($"Unknown channel type: {dto.ChannelType}");
            }

            // Note: Actual sending would require implementing channel-specific send methods
            // For now, we just validate the template renders correctly
            return Results.Ok(new TestNotificationResultDto
            {
                Success = true,
                Details = $"Template validated successfully. Rendered content preview:\n\n{renderedContent}"
            });
        }
        catch (Exception ex)
        {
            return Results.Ok(new TestNotificationResultDto
            {
                Success = false,
                ErrorMessage = ex.Message,
                Details = ex.InnerException?.Message
            });
        }
    }

    private static NotificationTemplateDto MapToDto(NotificationTemplate template) => new()
    {
        Id = template.Id,
        Name = template.Name,
        Type = template.Type.ToString(),
        IsBuiltIn = template.IsBuiltIn,
        GenerateLlmSummary = template.GenerateLlmSummary,
        EmailSubjectTemplate = template.EmailSubjectTemplate,
        EmailBodyHtmlTemplate = template.EmailBodyHtmlTemplate,
        EmailBodyTextTemplate = template.EmailBodyTextTemplate,
        DiscordTitleTemplate = template.DiscordTitleTemplate,
        DiscordBodyTemplate = template.DiscordBodyTemplate,
        WebhookPayloadTemplate = template.WebhookPayloadTemplate,
        CreatedAt = template.CreatedAt,
        ModifiedAt = template.ModifiedAt
    };
}
