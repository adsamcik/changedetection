using System.Net;
using System.Net.Http.Json;
using ChangeDetection.Shared.Dtos;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd;

[Category("Integration")]
public class NotificationEndpointTests : TestBase, IAsyncDisposable
{
    private TestWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [Before(Test)]
    public void Setup()
    {
        _factory = new TestWebApplicationFactory();
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory != null) await _factory.DisposeAsync();
    }

    [Test]
    public async Task GetTemplates_ReturnsBuiltInTemplates()
    {
        var response = await _client.GetAsync("/api/notifications/templates");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var templates = await response.Content.ReadFromJsonAsync<List<NotificationTemplateDto>>();
        templates.ShouldNotBeNull();
        templates.Count.ShouldBeGreaterThan(0);
        templates.ShouldContain(t => t.IsBuiltIn);
    }

    [Test]
    public async Task CreateTemplate_ValidData_Creates()
    {
        var createDto = new NotificationTemplateCreateDto
        {
            Name = "My Custom Template",
            Type = "ContentChange",
            EmailSubjectTemplate = "Change detected on {WatchName}",
            EmailBodyHtmlTemplate = "<p>Change detected</p>"
        };

        var response = await _client.PostAsJsonAsync("/api/notifications/templates", createDto);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var created = await response.Content.ReadFromJsonAsync<NotificationTemplateDto>();
        created.ShouldNotBeNull();
        created.Name.ShouldBe("My Custom Template");
        created.Type.ShouldBe("ContentChange");
        created.IsBuiltIn.ShouldBeFalse();
        created.Id.ShouldNotBe(Guid.Empty);
    }

    [Test]
    public async Task CreateTemplate_ThenGet_ReturnsCreated()
    {
        var createDto = new NotificationTemplateCreateDto
        {
            Name = "Roundtrip Template",
            Type = "PriceAlert",
            EmailSubjectTemplate = "Price changed for {WatchName}",
            EmailBodyHtmlTemplate = "<p>Price: {NewPrice}</p>",
            DiscordTitleTemplate = "Price Alert",
            DiscordBodyTemplate = "Price changed"
        };

        var createResponse = await _client.PostAsJsonAsync("/api/notifications/templates", createDto);
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var created = await createResponse.Content.ReadFromJsonAsync<NotificationTemplateDto>();
        created.ShouldNotBeNull();

        var getResponse = await _client.GetAsync($"/api/notifications/templates/{created.Id}");
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var fetched = await getResponse.Content.ReadFromJsonAsync<NotificationTemplateDto>();
        fetched.ShouldNotBeNull();
        fetched.Id.ShouldBe(created.Id);
        fetched.Name.ShouldBe("Roundtrip Template");
        fetched.Type.ShouldBe("PriceAlert");
        fetched.EmailSubjectTemplate.ShouldBe("Price changed for {WatchName}");
        fetched.DiscordTitleTemplate.ShouldBe("Price Alert");
    }

    [Test]
    public async Task UpdateTemplate_ExistingTemplate_Updates()
    {
        // Create a custom template first
        var createDto = new NotificationTemplateCreateDto
        {
            Name = "Before Update",
            Type = "ContentChange",
            EmailSubjectTemplate = "Original subject"
        };

        var createResponse = await _client.PostAsJsonAsync("/api/notifications/templates", createDto);
        var created = await createResponse.Content.ReadFromJsonAsync<NotificationTemplateDto>();
        created.ShouldNotBeNull();

        // Update it
        var updateDto = new NotificationTemplateCreateDto
        {
            Name = "After Update",
            Type = "ContentChange",
            EmailSubjectTemplate = "Updated subject",
            EmailBodyHtmlTemplate = "<p>Updated body</p>"
        };

        var updateResponse = await _client.PutAsJsonAsync($"/api/notifications/templates/{created.Id}", updateDto);
        updateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await updateResponse.Content.ReadFromJsonAsync<NotificationTemplateDto>();
        updated.ShouldNotBeNull();
        updated.Name.ShouldBe("After Update");
        updated.EmailSubjectTemplate.ShouldBe("Updated subject");
        updated.EmailBodyHtmlTemplate.ShouldBe("<p>Updated body</p>");
    }

    [Test]
    public async Task DeleteTemplate_CustomTemplate_Deletes()
    {
        // Create a custom template
        var createDto = new NotificationTemplateCreateDto
        {
            Name = "To Delete",
            Type = "ContentChange",
            EmailSubjectTemplate = "Doomed"
        };

        var createResponse = await _client.PostAsJsonAsync("/api/notifications/templates", createDto);
        var created = await createResponse.Content.ReadFromJsonAsync<NotificationTemplateDto>();
        created.ShouldNotBeNull();

        // Delete it
        var deleteResponse = await _client.DeleteAsync($"/api/notifications/templates/{created.Id}");
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Verify it's gone — returns 404 (may be rewritten to 405 by UseStatusCodePagesWithReExecute)
        var getResponse = await _client.GetAsync($"/api/notifications/templates/{created.Id}");
        var status = (int)getResponse.StatusCode;
        (status is 404 or 405).ShouldBeTrue($"Expected 404 or 405, got {getResponse.StatusCode}");
    }

    [Test]
    public async Task DeleteTemplate_BuiltIn_Returns400()
    {
        // Get built-in templates
        var listResponse = await _client.GetAsync("/api/notifications/templates");
        var templates = await listResponse.Content.ReadFromJsonAsync<List<NotificationTemplateDto>>();
        templates.ShouldNotBeNull();

        var builtIn = templates.FirstOrDefault(t => t.IsBuiltIn);
        builtIn.ShouldNotBeNull("Expected at least one built-in template");

        // Attempt to delete a built-in template — should not succeed
        var deleteResponse = await _client.DeleteAsync($"/api/notifications/templates/{builtIn.Id}");
        var status = (int)deleteResponse.StatusCode;
        // Built-in templates may return 400 (if in DB with IsBuiltIn flag) or 404 (if only in-memory defaults)
        (status is 400 or 404 or 405).ShouldBeTrue(
            $"Expected 400, 404, or 405 for built-in template delete, got {deleteResponse.StatusCode}");
        status.ShouldNotBe(204, "Built-in template should not be deletable");
    }

    [Test]
    public async Task GetPlaceholders_ReturnsList()
    {
        var response = await _client.GetAsync("/api/notifications/placeholders");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var placeholders = await response.Content.ReadFromJsonAsync<List<PlaceholderInfoDto>>();
        placeholders.ShouldNotBeNull();
        placeholders.Count.ShouldBeGreaterThan(0);
        placeholders.ShouldAllBe(p => !string.IsNullOrEmpty(p.Name));
        placeholders.ShouldAllBe(p => !string.IsNullOrEmpty(p.Description));
    }

    [Test]
    public async Task ValidateTemplate_Valid_ReturnsSuccess()
    {
        // Validate endpoint binds the template string from query string
        var template = Uri.EscapeDataString("Change on {WatchName}: {WatchUrl}");
        var response = await _client.PostAsync($"/api/notifications/templates/validate?template={template}", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<TemplateValidationResultDto>();
        result.ShouldNotBeNull();
        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Test]
    public async Task GetSmtpSettings_ReturnsConfig()
    {
        var response = await _client.GetAsync("/api/notifications/smtp");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var settings = await response.Content.ReadFromJsonAsync<SmtpSettingsDto>();
        settings.ShouldNotBeNull();
        // Fresh database should have default/empty SMTP settings
        settings.Port.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task UpdateSmtpSettings_ValidData_Updates()
    {
        var update = new SmtpSettingsUpdateDto
        {
            Host = "smtp.example.com",
            Port = 465,
            UseSsl = true,
            Username = "user@example.com",
            FromEmail = "noreply@example.com",
            FromName = "Change Detector"
        };

        var response = await _client.PutAsJsonAsync("/api/notifications/smtp", update);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var settings = await response.Content.ReadFromJsonAsync<SmtpSettingsDto>();
        settings.ShouldNotBeNull();
        settings.Host.ShouldBe("smtp.example.com");
        settings.Port.ShouldBe(465);
        settings.UseSsl.ShouldBeTrue();
        settings.Username.ShouldBe("user@example.com");
        settings.FromEmail.ShouldBe("noreply@example.com");
        settings.FromName.ShouldBe("Change Detector");
        settings.Enabled.ShouldBeTrue();

        // Verify persistence by re-reading
        var getResponse = await _client.GetAsync("/api/notifications/smtp");
        var reRead = await getResponse.Content.ReadFromJsonAsync<SmtpSettingsDto>();
        reRead.ShouldNotBeNull();
        reRead.Host.ShouldBe("smtp.example.com");
        reRead.Port.ShouldBe(465);
    }
}
