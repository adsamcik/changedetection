using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd.Scenarios;

/// <summary>
/// E2E tests for content change extraction scenarios.
/// Tests LLM ability to extract changes from news, legal docs, and government notices.
/// </summary>
public class ContentChangeExtractionTests : ExtractionTestBase
{
    #region Test HTML Fixtures

    private const string NewsCorrectionHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Breaking: Tech Company Announces Merger | News Site</title></head>
        <body>
            <article class="news-article" data-article-id="art-2025-01-15">
                <header class="article-header">
                    <h1 class="headline" data-title="Tech Giant Announces Major Acquisition">Tech Giant Announces Major Acquisition</h1>
                    <div class="byline">
                        <span class="author" data-author="Sarah Johnson">By Sarah Johnson</span>
                        <span class="date" data-published="January 15, 2025">January 15, 2025</span>
                    </div>
                </header>
                <div class="correction-notice" data-correction>
                    <strong>Correction (Jan 16, 2025):</strong>
                    <span data-correction-text="An earlier version incorrectly stated the deal value as $5 billion. The correct figure is $3.5 billion.">
                        An earlier version incorrectly stated the deal value as $5 billion. The correct figure is $3.5 billion.
                    </span>
                </div>
                <div class="article-content" data-content>
                    <p class="lead" data-summary="Acme Corp has announced acquisition of StartupX for $3.5 billion">
                        Acme Corp has announced the acquisition of StartupX in a deal valued at $3.5 billion...
                    </p>
                </div>
                <div class="update-info" data-updated="January 16, 2025 at 10:30 AM">
                    <span>Last updated: January 16, 2025 at 10:30 AM</span>
                </div>
            </article>
        </body>
        </html>
        """;

    private const string TosUpdateHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Terms of Service | CloudApp</title></head>
        <body>
            <main class="legal-document" data-doc="terms-of-service">
                <header class="doc-header">
                    <h1 data-title="Terms of Service">Terms of Service</h1>
                    <div class="version-info">
                        <span class="effective" data-effective="February 1, 2025">Effective: February 1, 2025</span>
                        <span class="previous" data-previous="January 1, 2024">Previous version: January 1, 2024</span>
                    </div>
                </header>
                <div class="changes-summary" data-changes>
                    <h2>What's Changed</h2>
                    <ul class="change-list">
                        <li data-change="1">Updated data retention policy from 30 to 90 days</li>
                        <li data-change="2">Added new section on AI-generated content usage</li>
                        <li data-change="3">Modified dispute resolution procedures</li>
                    </ul>
                </div>
                <section class="section" data-section="1">
                    <h2>1. Introduction</h2>
                    <p>These Terms of Service govern your use of CloudApp services...</p>
                </section>
            </main>
        </body>
        </html>
        """;

    private const string GovernmentNoticeHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Public Notice | City of Springfield</title></head>
        <body>
            <main class="gov-notice" data-notice-id="notice-2025-0042">
                <div class="gov-header">
                    <span class="agency" data-agency="City of Springfield">City of Springfield</span>
                    <span class="dept" data-department="Planning Department">Planning Department</span>
                </div>
                <h1 class="notice-title" data-title="Zoning Change Proposal - Downtown District">
                    Zoning Change Proposal - Downtown District
                </h1>
                <div class="notice-meta">
                    <span class="notice-type" data-type="Public Hearing Notice">Public Hearing Notice</span>
                    <span class="notice-date" data-date="January 15, 2025">Posted: January 15, 2025</span>
                </div>
                <div class="notice-content">
                    <p data-summary="Proposal to rezone blocks 100-105 from commercial to mixed-use">
                        Notice is hereby given that a public hearing will be held regarding the proposal 
                        to rezone blocks 100-105 of the Downtown District from C-2 (Commercial) to MU-1 (Mixed Use)...
                    </p>
                </div>
                <div class="hearing-info" data-hearing>
                    <h2>Public Hearing</h2>
                    <p class="hearing-date" data-hearing-date="February 10, 2025">Date: February 10, 2025</p>
                    <p class="hearing-time" data-hearing-time="7:00 PM">Time: 7:00 PM</p>
                    <p class="hearing-location" data-hearing-location="City Hall, Room 201">Location: City Hall, Room 201</p>
                </div>
                <div class="comment-deadline" data-deadline="February 5, 2025">
                    <span>Comments must be received by: February 5, 2025</span>
                </div>
            </main>
        </body>
        </html>
        """;

    #endregion

    #region E2E Tests (LLM Cached)

    [Test]
    [Category("LlmCached")]
    public async Task ExtractContent_NewsCorrection_ExtractsUpdateInfo()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(NewsCorrectionHtml, new TestExtractionSchema
        {
            Name = "NewsArticle",
            Description = "Extract news article with corrections",
            Fields =
            [
                new TestSchemaField { Name = "headline", Type = "string", Description = "Article headline" },
                new TestSchemaField { Name = "author", Type = "string", Description = "Author name" },
                new TestSchemaField { Name = "correction", Type = "string", Description = "Correction notice text" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        result.IsSuccess.ShouldBeTrue($"Extraction failed: {result.Error}");

        var headline = result.GetString("headline");
        headline.ShouldContain("Acquisition", Case.Insensitive);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractContent_TosUpdate_ExtractsChanges()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(TosUpdateHtml, new TestExtractionSchema
        {
            Name = "TermsOfService",
            Description = "Extract Terms of Service changes",
            Fields =
            [
                new TestSchemaField { Name = "title", Type = "string", Description = "Document title" },
                new TestSchemaField { Name = "effectiveDate", Type = "string", Description = "When changes take effect" },
                new TestSchemaField { Name = "changes", Type = "array", Description = "List of changes" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        result.IsSuccess.ShouldBeTrue($"Extraction failed: {result.Error}");

        var title = result.GetString("title");
        title.ShouldContain("Terms", Case.Insensitive);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractContent_Government_ExtractsHearingInfo()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(GovernmentNoticeHtml, new TestExtractionSchema
        {
            Name = "GovNotice",
            Description = "Extract government public notice",
            Fields =
            [
                new TestSchemaField { Name = "title", Type = "string", Description = "Notice title" },
                new TestSchemaField { Name = "agency", Type = "string", Description = "Government agency" },
                new TestSchemaField { Name = "hearingDate", Type = "string", Description = "Public hearing date" },
                new TestSchemaField { Name = "commentDeadline", Type = "string", Description = "Comment deadline" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        result.IsSuccess.ShouldBeTrue($"Extraction failed: {result.Error}");

        var agency = result.GetString("agency");
        agency.ShouldContain("Springfield", Case.Insensitive);
    }

    #endregion
}

