using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Content;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services;

[Category("Unit")]
public class PiiPipelineIntegrationTests
{
    [Test]
    public async Task Snapshot_PiiFieldsDefaultToZero()
    {
        var snapshot = new ChangeSnapshot
        {
            ContentHash = "abc123",
            Content = "Hello world"
        };

        snapshot.PiiRedactionsApplied.ShouldBe(0);
        snapshot.PiiTypesRedacted.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Snapshot_PiiFieldsPopulatedAfterRedaction()
    {
        var redactor = new PiiRedactor();
        var content = "Contact john@example.com or call 555-123-4567";

        var result = redactor.Redact(content);

        var snapshot = new ChangeSnapshot
        {
            ContentHash = "abc123",
            Content = result.RedactedContent,
            PiiRedactionsApplied = result.RedactionsApplied,
            PiiTypesRedacted = string.Join(",", result.RedactedTypes)
        };

        snapshot.PiiRedactionsApplied.ShouldBeGreaterThan(0);
        snapshot.PiiTypesRedacted.ShouldNotBeNullOrEmpty();
        snapshot.Content.ShouldNotContain("john@example.com");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Snapshot_CleanContentPreservesOriginal()
    {
        var redactor = new PiiRedactor();
        var content = "No PII here, just regular text about products.";

        var result = redactor.Redact(content);

        result.RedactionsApplied.ShouldBe(0);
        result.RedactedContent.ShouldBe(content);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Snapshot_PiiMetadataTracksTypes()
    {
        var redactor = new PiiRedactor();
        var content = "Email: test@test.com, SSN: 123-45-6789, IP: 192.168.1.1";

        var result = redactor.Redact(content);
        var types = string.Join(",", result.RedactedTypes);

        types.ShouldContain("Email");
        types.ShouldContain("SSN");
        types.ShouldContain("IPv4");
        result.RedactionsApplied.ShouldBeGreaterThanOrEqualTo(3);
        await Task.CompletedTask;
    }
}
