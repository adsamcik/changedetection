using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services;

public class NotificationTemplateEngineTests
{
    private readonly IRepository<NotificationTemplate> _templateRepo;
    private readonly ILlmProviderChain _llmProvider;
    private readonly ILogger<NotificationTemplateEngine> _logger;
    private readonly NotificationTemplateEngine _engine;

    public NotificationTemplateEngineTests()
    {
        _templateRepo = Substitute.For<IRepository<NotificationTemplate>>();
        _llmProvider = Substitute.For<ILlmProviderChain>();
        _logger = Substitute.For<ILogger<NotificationTemplateEngine>>();
        _engine = new NotificationTemplateEngine(_templateRepo, _llmProvider, _logger);
    }

    [Test]
    public async Task RenderAsync_PriceDropAlert_SubstitutesPlaceholders()
    {
        // Arrange
        var template = "Price changed from {OldPrice} to {Price} {Currency}";
        var context = new NotificationContext
        {
            Watch = new WatchedSite { Name = "Test Product", Url = "https://test.com" },
            OldPrice = 939m,
            NewPrice = 657m,
            Currency = "CZK"
        };

        // Act
        var result = await _engine.RenderAsync(template, context);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("939");
        result.ShouldContain("657");
        result.ShouldContain("CZK");
    }

    [Test]
    public async Task RenderAsync_WithWatchProperties_SubstitutesCorrectly()
    {
        // Arrange
        var template = "Alert for {Watch.Name} at {Watch.Url}";
        var context = new NotificationContext
        {
            Watch = new WatchedSite { Name = "My Widget", Url = "https://example.com/widget" }
        };

        // Act
        var result = await _engine.RenderAsync(template, context);

        // Assert
        result.ShouldContain("My Widget");
        result.ShouldContain("https://example.com/widget");
    }

    [Test]
    public async Task RenderAsync_UnknownPlaceholder_LeavesAsLiteral()
    {
        // Arrange
        var template = "Value: {UnknownPlaceholder}";
        var context = new NotificationContext
        {
            Watch = new WatchedSite { Name = "Test", Url = "https://test.com" }
        };

        // Act
        var result = await _engine.RenderAsync(template, context);

        // Assert
        // Unknown placeholders are left as-is (graceful fallback)
        result.ShouldContain("{UnknownPlaceholder}");
    }

    [Test]
    public async Task RenderAsync_StockStatus_SubstitutesCorrectly()
    {
        // Arrange
        var template = "Stock changed from {OldStockStatus} to {StockStatus}";
        var context = new NotificationContext
        {
            Watch = new WatchedSite { Name = "Widget", Url = "https://test.com" },
            OldStockStatus = StockStatus.OutOfStock,
            NewStockStatus = StockStatus.InStock
        };

        // Act
        var result = await _engine.RenderAsync(template, context);

        // Assert - the engine formats stock status as "Out of Stock" / "In Stock"
        result.ShouldContain("Out of Stock");
        result.ShouldContain("In Stock");
    }

    [Test]
    public async Task RenderAsync_ChangeMetrics_SubstitutesCorrectly()
    {
        // Arrange
        var template = "Change: {ChangePercent}% ({ChangeAbsolute})";
        var context = new NotificationContext
        {
            Watch = new WatchedSite { Name = "Test", Url = "https://test.com" },
            ChangePercent = -30.03,
            ChangeAbsolute = -282.0
        };

        // Act
        var result = await _engine.RenderAsync(template, context);

        // Assert - values are rendered with some formatting
        result.ShouldContain("-30");
        result.ShouldContain("-282");
    }

    [Test]
    public async Task GetEffectiveTemplateAsync_NoCustomTemplate_ReturnsDefault()
    {
        // Arrange
        _templateRepo.FirstOrDefaultAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<NotificationTemplate, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<NotificationTemplate?>(null));

        // Act
        var template = await _engine.GetEffectiveTemplateAsync(NotificationTemplateType.PriceAlert);

        // Assert
        template.ShouldNotBeNull();
        template.Type.ShouldBe(NotificationTemplateType.PriceAlert);
        template.IsBuiltIn.ShouldBeTrue();
        template.EmailSubjectTemplate.ShouldNotBeNullOrEmpty();
    }

    [Test]
    public async Task GetEffectiveTemplateAsync_CustomTemplate_ReturnsCustom()
    {
        // Arrange
        var customTemplate = new NotificationTemplate
        {
            Name = "Custom Price Alert",
            Type = NotificationTemplateType.PriceAlert,
            EmailSubjectTemplate = "Custom: {Watch.Name} price changed!",
            EmailBodyTextTemplate = "The price is now {Price} {Currency}"
        };

        _templateRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<NotificationTemplate?>(customTemplate));

        // Act
        var template = await _engine.GetEffectiveTemplateAsync(
            NotificationTemplateType.PriceAlert,
            customTemplateId: Guid.NewGuid());

        // Assert
        template.ShouldNotBeNull();
        template.EmailSubjectTemplate.ShouldBe("Custom: {Watch.Name} price changed!");
    }

    [Test]
    public async Task ValidatePlaceholders_ValidTemplate_ReturnsNoWarnings()
    {
        // Arrange
        var template = "Alert: {Watch.Name} - Price: {Price} {Currency}";

        // Act
        var result = _engine.ValidatePlaceholders(template);

        // Assert
        result.IsValid.ShouldBeTrue();
        result.Warnings.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ValidatePlaceholders_InvalidPlaceholder_ReturnsWarning()
    {
        // Arrange
        var template = "Alert: {InvalidPlaceholder}";

        // Act
        var result = _engine.ValidatePlaceholders(template);

        // Assert
        result.Warnings.ShouldNotBeEmpty();
        result.Warnings.ShouldContain(w => w.Contains("InvalidPlaceholder"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetAvailablePlaceholders_ReturnsExpectedPlaceholders()
    {
        // Act
        var placeholders = _engine.GetAvailablePlaceholders();

        // Assert
        placeholders.ShouldContainKey("Watch.Name");
        placeholders.ShouldContainKey("Watch.Url");
        placeholders.ShouldContainKey("OldPrice");
        placeholders.ShouldContainKey("Price");
        placeholders.ShouldContainKey("Currency");
        placeholders.ShouldContainKey("ChangePercent");
        placeholders.ShouldContainKey("ChangeAbsolute");
        placeholders.ShouldContainKey("StockStatus");
        placeholders.ShouldContainKey("OldStockStatus");
        await Task.CompletedTask;
    }

    [Test]
    public async Task RenderAsync_WithLlmSummary_GeneratesAndSubstitutes()
    {
        // Arrange
        _llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LlmResponse
            {
                IsSuccess = true,
                Content = "Great news! The product price dropped significantly."
            }));

        var template = "{LlmSummary}";
        var context = new NotificationContext
        {
            Watch = new WatchedSite { Name = "Test", Url = "https://test.com" },
            GenerateLlmSummary = true,
            OldPrice = 100m,
            NewPrice = 70m
        };

        // Act
        var result = await _engine.RenderAsync(template, context);

        // Assert
        result.ShouldNotContain("{LlmSummary}");
        // The result should contain some content (either LLM summary or fallback)
        result.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task RenderAsync_LlmFails_FallsBackGracefully()
    {
        // Arrange
        _llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LlmResponse
            {
                IsSuccess = false,
                Content = null
            }));

        var template = "{LlmSummary}";
        var context = new NotificationContext
        {
            Watch = new WatchedSite { Name = "Test", Url = "https://test.com" },
            GenerateLlmSummary = true
        };

        // Act
        var result = await _engine.RenderAsync(template, context);

        // Assert
        // Should not leave the raw placeholder
        result.ShouldNotBe("{LlmSummary}");
    }
}
