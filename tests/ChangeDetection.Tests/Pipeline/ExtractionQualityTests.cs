using System.Text.Json;
using ChangeDetection.Services.Background;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline;

/// <summary>
/// Tests for the extraction quality validation logic in ChangeCheckBackgroundService.
/// Exercises the internal static ValidateExtractionQuality and IsValidExtractedItem methods
/// that filter out navigation garbage, DNS strings, single-digit items, etc.
/// </summary>
[Category("Unit")]
public class ExtractionQualityTests : TestBase
{
    [Test]
    public async Task NavItems_AreRejected()
    {
        // Arrange: items with navigation-like titles
        var json = JsonDocument.Parse("""
            [
                {"title": "ABOUT", "url": "/about"},
                {"title": "CONTACT", "url": "/contact"},
                {"title": "home", "url": "/"},
                {"title": "login", "url": "/login"},
                {"title": "Senior Scientist, Protein Engineering", "url": "/jobs/123"}
            ]
            """).RootElement;

        var logger = CreateLogger<ExtractionQualityTests>();
        var watchId = Guid.NewGuid();

        // Act
        var (filteredOutput, totalItems, rejectedCount) =
            ChangeCheckBackgroundService.ValidateExtractionQuality(json, logger, watchId);

        // Assert
        totalItems.ShouldBe(5);
        rejectedCount.ShouldBeGreaterThanOrEqualTo(3,
            "ABOUT, CONTACT, home should all be rejected as nav items");

        // The valid job title should survive
        filteredOutput.ShouldNotBeNull();
        var filtered = filteredOutput!.Value;
        filtered.GetArrayLength().ShouldBeGreaterThanOrEqualTo(1,
            "Real job title should pass the filter");
        await Task.CompletedTask;
    }

    [Test]
    public async Task AllCapsNavItems_AreRejected()
    {
        // Arrange: single ALL-CAPS words without spaces — typical nav/button text
        var json = JsonDocument.Parse("""
            [
                {"title": "ABOUT"},
                {"title": "NEWS"},
                {"title": "CONTACT"},
                {"title": "SEARCH"},
                {"title": "Research Associate - Cell Biology", "url": "/jobs/456"}
            ]
            """).RootElement;

        var logger = CreateLogger<ExtractionQualityTests>();

        // Act
        var (filteredOutput, _, rejectedCount) =
            ChangeCheckBackgroundService.ValidateExtractionQuality(json, logger, Guid.NewGuid());

        // Assert: ALL-CAPS single words should be rejected
        rejectedCount.ShouldBeGreaterThanOrEqualTo(4,
            "ALL-CAPS single words should be rejected as nav items");

        filteredOutput.ShouldNotBeNull();
        var filtered = filteredOutput!.Value;
        filtered.GetArrayLength().ShouldBe(1,
            "Only the real job title should survive");
        filtered[0].GetProperty("title").GetString()
            .ShouldContain("Research Associate");
        await Task.CompletedTask;
    }

    [Test]
    public async Task DnsStrings_AreRejected()
    {
        // Arrange: items that look like domain names (≥2 dots, no spaces)
        var json = JsonDocument.Parse("""
            [
                {"title": "antibiotika.ssi.dk"},
                {"title": "www.example.com"},
                {"title": "api.jobs.company.io"},
                {"title": "Clinical Research Associate", "url": "/jobs/789"}
            ]
            """).RootElement;

        var logger = CreateLogger<ExtractionQualityTests>();

        // Act
        var (filteredOutput, _, rejectedCount) =
            ChangeCheckBackgroundService.ValidateExtractionQuality(json, logger, Guid.NewGuid());

        // Assert
        rejectedCount.ShouldBeGreaterThanOrEqualTo(3,
            "Domain-like strings should be rejected (≥2 dots, no spaces)");

        filteredOutput.ShouldNotBeNull();
        filteredOutput!.Value.GetArrayLength().ShouldBe(1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task SingleDigitItems_AreRejected()
    {
        // Arrange: items that are pure numbers or very short numeric strings
        var json = JsonDocument.Parse("""
            [
                {"title": "17"},
                {"title": "2"},
                {"title": "123"},
                {"title": "3.5"},
                {"title": "Scientist II - Process Development", "url": "/jobs/100"}
            ]
            """).RootElement;

        var logger = CreateLogger<ExtractionQualityTests>();

        // Act
        var (filteredOutput, _, rejectedCount) =
            ChangeCheckBackgroundService.ValidateExtractionQuality(json, logger, Guid.NewGuid());

        // Assert
        rejectedCount.ShouldBeGreaterThanOrEqualTo(4,
            "Pure numeric strings and short digit-only items should be rejected");

        filteredOutput.ShouldNotBeNull();
        filteredOutput!.Value.GetArrayLength().ShouldBe(1);
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("Senior Scientist, MSAT")]
    [Arguments("Research Associate - Cell Biology")]
    [Arguments("Lab Technician III - Quality Control")]
    [Arguments("Principal Engineer, Bioprocess Development")]
    [Arguments("Director of Clinical Operations")]
    public async Task RealJobTitles_AreAccepted(string title)
    {
        // Arrange: legitimate job titles that should pass validation
        var json = JsonDocument.Parse($@"[{{""title"":""{title}"",""url"":""/jobs/1""}}]").RootElement;
        var logger = CreateLogger<ExtractionQualityTests>();

        // Act
        var (filteredOutput, totalItems, rejectedCount) =
            ChangeCheckBackgroundService.ValidateExtractionQuality(json, logger, Guid.NewGuid());

        // Assert
        totalItems.ShouldBe(1);
        rejectedCount.ShouldBe(0, $"Real job title '{title}' should NOT be rejected");
        filteredOutput.ShouldNotBeNull();
        filteredOutput!.Value.GetArrayLength().ShouldBe(1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task EmptyTitle_IsRejected()
    {
        var json = JsonDocument.Parse("""
            [
                {"title": "", "url": "/jobs/1"},
                {"title": "  ", "url": "/jobs/2"},
                {"url": "/jobs/3"}
            ]
            """).RootElement;

        var logger = CreateLogger<ExtractionQualityTests>();

        // Act
        var (filteredOutput, totalItems, rejectedCount) =
            ChangeCheckBackgroundService.ValidateExtractionQuality(json, logger, Guid.NewGuid());

        // Assert
        totalItems.ShouldBe(3);
        rejectedCount.ShouldBe(3, "Empty/whitespace/missing titles should all be rejected");
        filteredOutput.ShouldNotBeNull();
        filteredOutput!.Value.GetArrayLength().ShouldBe(0);
        await Task.CompletedTask;
    }

    [Test]
    public async Task LongTitle_Over200Chars_IsRejected()
    {
        // Arrange: a title exceeding the 200 character limit (likely HTML fragments)
        var longTitle = new string('A', 201);
        var json = JsonDocument.Parse($@"[{{""title"":""{longTitle}"",""url"":""/jobs/1""}}]").RootElement;
        var logger = CreateLogger<ExtractionQualityTests>();

        // Act
        var (filteredOutput, totalItems, rejectedCount) =
            ChangeCheckBackgroundService.ValidateExtractionQuality(json, logger, Guid.NewGuid());

        // Assert
        totalItems.ShouldBe(1);
        rejectedCount.ShouldBe(1, "Titles > 200 chars should be rejected");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ShortTitle_Under5Chars_IsRejected()
    {
        // Arrange: titles too short to be meaningful
        var json = JsonDocument.Parse("""
            [
                {"title": "abc"},
                {"title": "Hi"},
                {"title": "OK"},
                {"title": "Valid Job Title Here", "url": "/ok"}
            ]
            """).RootElement;

        var logger = CreateLogger<ExtractionQualityTests>();

        // Act
        var (filteredOutput, _, rejectedCount) =
            ChangeCheckBackgroundService.ValidateExtractionQuality(json, logger, Guid.NewGuid());

        // Assert
        rejectedCount.ShouldBeGreaterThanOrEqualTo(3,
            "Titles < 5 chars should be rejected");
        filteredOutput.ShouldNotBeNull();
        filteredOutput!.Value.GetArrayLength().ShouldBe(1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task AllItemsRejected_ReturnsEmptyArrayWithWarning()
    {
        // Arrange: all items are garbage
        var json = JsonDocument.Parse("""
            [
                {"title": "ABOUT"},
                {"title": "17"},
                {"title": "api.test.com"},
                {"title": ""},
                {"title": "OK"}
            ]
            """).RootElement;

        var logger = CreateLogger<ExtractionQualityTests>();

        // Act
        var (filteredOutput, totalItems, rejectedCount) =
            ChangeCheckBackgroundService.ValidateExtractionQuality(json, logger, Guid.NewGuid());

        // Assert: all rejected → empty array returned
        totalItems.ShouldBe(5);
        rejectedCount.ShouldBe(5);
        filteredOutput.ShouldNotBeNull();
        filteredOutput!.Value.GetArrayLength().ShouldBe(0);

        // Should log a warning about all items being rejected
        var logs = LogCollector.GetSnapshot();
        logs.ShouldContain(l => l.Message.Contains("rejected ALL", StringComparison.OrdinalIgnoreCase),
            "All-items-rejected scenario should produce a specific warning");
        await Task.CompletedTask;
    }

    [Test]
    public async Task NonArrayInput_PassesThroughUnchanged()
    {
        // Arrange: a non-array JSON value (e.g., single object) should bypass filtering
        var json = JsonDocument.Parse("""{"title": "Single Item", "value": 42}""").RootElement;
        var logger = CreateLogger<ExtractionQualityTests>();

        // Act
        var (filteredOutput, totalItems, rejectedCount) =
            ChangeCheckBackgroundService.ValidateExtractionQuality(json, logger, Guid.NewGuid());

        // Assert: non-array input passed through without filtering
        totalItems.ShouldBe(0);
        rejectedCount.ShouldBe(0);
        filteredOutput.ShouldNotBeNull();
        filteredOutput!.Value.ValueKind.ShouldBe(JsonValueKind.Object);
        await Task.CompletedTask;
    }

    [Test]
    public async Task JavascriptPseudoUrls_AreRejected()
    {
        // Arrange: items with javascript: pseudo-URLs
        var json = JsonDocument.Parse("""
            [
                {"title": "Click Here For Details", "url": "javascript:void(0)"},
                {"title": "Toggle Menu Item", "url": "javascript:toggleMenu()"},
                {"title": "Senior Scientist, MSAT", "url": "https://jobs.example.com/123"}
            ]
            """).RootElement;

        var logger = CreateLogger<ExtractionQualityTests>();

        // Act
        var (filteredOutput, _, rejectedCount) =
            ChangeCheckBackgroundService.ValidateExtractionQuality(json, logger, Guid.NewGuid());

        // Assert
        rejectedCount.ShouldBeGreaterThanOrEqualTo(2,
            "Items with javascript: pseudo-URLs should be rejected");
        filteredOutput.ShouldNotBeNull();
        filteredOutput!.Value.GetArrayLength().ShouldBe(1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task FragmentUrls_AreRejected()
    {
        // Arrange: items with fragment-only or root-only URLs (nav items)
        var json = JsonDocument.Parse("""
            [
                {"title": "Navigate To Section", "url": "#section-1"},
                {"title": "Back to Top Link", "url": "#"},
                {"title": "Home Page Button", "url": "/"},
                {"title": "Molecular Biologist - R&D Team", "url": "https://careers.example.com/apply/456"}
            ]
            """).RootElement;

        var logger = CreateLogger<ExtractionQualityTests>();

        // Act
        var (filteredOutput, _, rejectedCount) =
            ChangeCheckBackgroundService.ValidateExtractionQuality(json, logger, Guid.NewGuid());

        // Assert
        rejectedCount.ShouldBeGreaterThanOrEqualTo(3,
            "Fragment-only and root-only URLs should be rejected");
        filteredOutput.ShouldNotBeNull();
        filteredOutput!.Value.GetArrayLength().ShouldBe(1);
        await Task.CompletedTask;
    }
}
