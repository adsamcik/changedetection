using ChangeDetection.Core.Entities;
using Shouldly;

namespace ChangeDetection.Core.Tests.Entities;

[Category("Unit")]
public class ChangeSnapshotTests
{
    [Test]
    public async Task RequiredProperties_ShouldBeSet()
    {
        var snapshot = new ChangeSnapshot
        {
            ContentHash = "abc123",
            Content = "<html>Hello</html>"
        };

        snapshot.ContentHash.ShouldBe("abc123");
        snapshot.Content.ShouldBe("<html>Hello</html>");
        await Task.CompletedTask;
    }

    [Test]
    public async Task DefaultValues_ShouldBeCorrect()
    {
        var snapshot = new ChangeSnapshot
        {
            ContentHash = "hash",
            Content = "content"
        };

        snapshot.Id.ShouldNotBe(Guid.Empty);
        snapshot.OwnerId.ShouldBe(Guid.Empty);
        snapshot.HttpStatusCode.ShouldBe(0);
        snapshot.FetchDurationMs.ShouldBe(0);
        snapshot.ContentSizeBytes.ShouldBe(0);
        snapshot.SchemaDriftDetected.ShouldBeFalse();
        snapshot.HasLlmEnrichment.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task NullableProperties_ShouldDefaultToNull()
    {
        var snapshot = new ChangeSnapshot
        {
            ContentHash = "hash",
            Content = "content"
        };

        snapshot.ScreenshotPath.ShouldBeNull();
        snapshot.ElementScreenshotPath.ShouldBeNull();
        snapshot.ElementBoundingBoxJson.ShouldBeNull();
        snapshot.ExtractedObjectsJson.ShouldBeNull();
        snapshot.SchemaVersion.ShouldBeNull();
        snapshot.ExtractionError.ShouldBeNull();
        snapshot.ContentSummary.ShouldBeNull();
        snapshot.ContentType.ShouldBeNull();
        snapshot.Language.ShouldBeNull();
        snapshot.EnrichmentConfidence.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task AmbiguousIdentityWarnings_ShouldDefaultToEmpty()
    {
        var snapshot = new ChangeSnapshot
        {
            ContentHash = "hash",
            Content = "content"
        };

        snapshot.AmbiguousIdentityWarnings.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task CapturedAt_ShouldBeRecentUtc()
    {
        var before = DateTime.UtcNow;
        var snapshot = new ChangeSnapshot
        {
            ContentHash = "hash",
            Content = "content"
        };
        var after = DateTime.UtcNow;

        snapshot.CapturedAt.ShouldBeGreaterThanOrEqualTo(before);
        snapshot.CapturedAt.ShouldBeLessThanOrEqualTo(after);
        await Task.CompletedTask;
    }
}
