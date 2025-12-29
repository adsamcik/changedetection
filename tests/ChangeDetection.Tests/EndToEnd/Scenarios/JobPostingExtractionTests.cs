using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd.Scenarios;

/// <summary>
/// E2E tests for job posting extraction scenarios.
/// Tests LLM ability to extract job details from LinkedIn, Indeed, and Greenhouse.
/// </summary>
public class JobPostingExtractionTests : ExtractionTestBase
{
    #region Test HTML Fixtures

    private const string LinkedInJobHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Senior Software Engineer at Google | LinkedIn</title></head>
        <body>
            <main class="job-view" data-job-id="job-12345">
                <div class="job-header">
                    <h1 class="job-title" data-title="Senior Software Engineer">Senior Software Engineer</h1>
                    <div class="company-info">
                        <span class="company-name" data-company="Google">Google</span>
                        <span class="location" data-location="Mountain View, CA">Mountain View, CA</span>
                    </div>
                </div>
                <div class="job-details">
                    <span class="posted-date" data-posted="2 days ago">Posted 2 days ago</span>
                    <span class="applicants" data-applicants="142">142 applicants</span>
                    <span class="job-type" data-type="Full-time">Full-time</span>
                    <span class="level" data-level="Senior level">Senior level</span>
                </div>
                <div class="salary-info" data-salary>
                    <span data-range="$180,000 - $250,000">$180,000 - $250,000 per year</span>
                </div>
                <div class="job-description" data-description>
                    <h2>About the role</h2>
                    <p>We're looking for a Senior Software Engineer to join our Cloud team...</p>
                </div>
                <div class="requirements" data-requirements>
                    <h3>Requirements</h3>
                    <ul>
                        <li>5+ years of software development experience</li>
                        <li>Strong experience with distributed systems</li>
                        <li>BS/MS in Computer Science or equivalent</li>
                    </ul>
                </div>
            </main>
        </body>
        </html>
        """;

    private const string IndeedSearchHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Software Engineer Jobs | Indeed</title></head>
        <body>
            <main class="search-results">
                <h1 class="search-title">Software Engineer jobs in San Francisco, CA</h1>
                <div class="results-count" data-count="2,543">2,543 jobs</div>
                <div class="job-list" data-jobs>
                    <div class="job-card" data-job-id="indeed-001">
                        <h2 class="job-title" data-title="Backend Engineer">Backend Engineer</h2>
                        <span class="company" data-company="Stripe">Stripe</span>
                        <span class="location" data-location="San Francisco, CA">San Francisco, CA</span>
                        <span class="salary" data-salary="$150K - $200K">$150K - $200K a year</span>
                        <span class="posted" data-posted="Just posted">Just posted</span>
                    </div>
                    <div class="job-card" data-job-id="indeed-002">
                        <h2 class="job-title" data-title="Full Stack Developer">Full Stack Developer</h2>
                        <span class="company" data-company="Airbnb">Airbnb</span>
                        <span class="location" data-location="San Francisco, CA (Remote)">San Francisco, CA (Remote)</span>
                        <span class="salary" data-salary="$140K - $180K">$140K - $180K a year</span>
                        <span class="posted" data-posted="3 days ago">3 days ago</span>
                    </div>
                </div>
            </main>
        </body>
        </html>
        """;

    private const string GreenhouseJobHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Product Manager | Notion Careers</title></head>
        <body>
            <main class="job-posting" data-job="pm-2025">
                <div class="company-header">
                    <img class="logo" src="notion-logo.png" alt="Notion">
                    <span class="company-name" data-company="Notion">Notion</span>
                </div>
                <h1 class="position-title" data-title="Product Manager, Enterprise">Product Manager, Enterprise</h1>
                <div class="job-meta">
                    <span class="location" data-location="San Francisco, CA">📍 San Francisco, CA</span>
                    <span class="department" data-dept="Product">Product</span>
                    <span class="type" data-type="Full-time">Full-time</span>
                </div>
                <div class="deadline-banner" data-deadline>
                    <span data-closes="January 31, 2025">Applications close: January 31, 2025</span>
                </div>
                <section class="description">
                    <h2>About the Role</h2>
                    <p data-summary="Lead product strategy for enterprise customers">
                        Lead product strategy for our enterprise customers...
                    </p>
                </section>
                <button class="apply-btn" data-action="apply">Apply Now</button>
            </main>
        </body>
        </html>
        """;

    #endregion

    #region E2E Tests (LLM Cached)

    [Test]
    [Category("LlmCached")]
    public async Task ExtractJob_LinkedIn_ExtractsJobDetails()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(LinkedInJobHtml, new TestExtractionSchema
        {
            Name = "LinkedInJob",
            Description = "Extract job posting details from LinkedIn",
            Fields =
            [
                new TestSchemaField { Name = "title", Type = "string", Description = "Job title" },
                new TestSchemaField { Name = "company", Type = "string", Description = "Company name" },
                new TestSchemaField { Name = "location", Type = "string", Description = "Job location" },
                new TestSchemaField { Name = "salary", Type = "string", Description = "Salary range" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        result.IsSuccess.ShouldBeTrue($"Extraction failed: {result.Error}");

        var company = result.GetString("company");
        company.ShouldContain("Google", Case.Insensitive);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractJob_Indeed_ExtractsSearchResults()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(IndeedSearchHtml, new TestExtractionSchema
        {
            Name = "IndeedSearch",
            Description = "Extract job search results from Indeed",
            Fields =
            [
                new TestSchemaField { Name = "totalJobs", Type = "string", Description = "Total job count" },
                new TestSchemaField { Name = "jobs", Type = "array", Description = "List of job postings" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        result.IsSuccess.ShouldBeTrue($"Extraction failed: {result.Error}");
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractJob_Greenhouse_ExtractsApplicationDeadline()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(GreenhouseJobHtml, new TestExtractionSchema
        {
            Name = "GreenhouseJob",
            Description = "Extract job posting from Greenhouse ATS",
            Fields =
            [
                new TestSchemaField { Name = "title", Type = "string", Description = "Position title" },
                new TestSchemaField { Name = "company", Type = "string", Description = "Company name" },
                new TestSchemaField { Name = "deadline", Type = "string", Description = "Application deadline" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        result.IsSuccess.ShouldBeTrue($"Extraction failed: {result.Error}");

        var company = result.GetString("company");
        company.ShouldContain("Notion", Case.Insensitive);
    }

    #endregion
}

