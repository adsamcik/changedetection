using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.GroupWatch;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services;

[Category("Unit")]
public class PortalDiscoveryAnalyzerTests : TestBase
{
    private readonly IRepository<WatchedSite> _watchRepo = Substitute.For<IRepository<WatchedSite>>();

    [Test]
    public async Task AnalyzeForNewPortalsAsync_FindsExternalCareerPortalAndDeduplicates()
    {
        var sourceWatchId = Guid.NewGuid();
        _watchRepo.GetByIdAsync(sourceWatchId, Arg.Any<CancellationToken>())
            .Returns(new WatchedSite
            {
                Id = sourceWatchId,
                Url = "https://jobs.example.com/search"
            });
        _watchRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([
                new WatchedSite
                {
                    Id = sourceWatchId,
                    Url = "https://jobs.example.com/search"
                }
            ]);

        using var document = JsonDocument.Parse("""
            {
              "items": [
                {
                  "url": "https://company.wd3.myworkdayjobs.com/en-US/careers/job/123",
                  "descriptionHtml": "<a href=\"https://company.wd3.myworkdayjobs.com/en-US/careers/job/456\">Apply</a>"
                }
              ]
            }
            """);

        var sut = new PortalDiscoveryAnalyzer(_watchRepo, CreateLogger<PortalDiscoveryAnalyzer>());

        var suggestions = await sut.AnalyzeForNewPortalsAsync(sourceWatchId, document.RootElement);

        suggestions.Count.ShouldBe(1);
        suggestions[0].Url.ShouldBe("https://company.wd3.myworkdayjobs.com/en-US/careers");
        suggestions[0].Domain.ShouldBe("company.wd3.myworkdayjobs.com");
        suggestions[0].DetectedPlatform.ShouldBe("workday");
    }

    [Test]
    public async Task AnalyzeForNewPortalsAsync_FiltersExistingWatchesSocialLinksAndDocuments()
    {
        var sourceWatchId = Guid.NewGuid();
        _watchRepo.GetByIdAsync(sourceWatchId, Arg.Any<CancellationToken>())
            .Returns(new WatchedSite
            {
                Id = sourceWatchId,
                Url = "https://aggregator.example/jobs"
            });
        _watchRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([
                new WatchedSite
                {
                    Id = sourceWatchId,
                    Url = "https://aggregator.example/jobs"
                },
                new WatchedSite
                {
                    Id = Guid.NewGuid(),
                    Url = "https://boards.greenhouse.io/existing-company"
                }
            ]);

        using var document = JsonDocument.Parse("""
            {
              "items": [
                {
                  "links": [
                    "https://boards.greenhouse.io/existing-company/jobs/1",
                    "https://linkedin.com/company/example",
                    "https://company.example/files/job-spec.pdf",
                    "https://new-company.example/careers"
                  ]
                }
              ]
            }
            """);

        var sut = new PortalDiscoveryAnalyzer(_watchRepo, CreateLogger<PortalDiscoveryAnalyzer>());

        var suggestions = await sut.AnalyzeForNewPortalsAsync(sourceWatchId, document.RootElement);

        suggestions.Count.ShouldBe(1);
        suggestions[0].Url.ShouldBe("https://new-company.example/careers");
    }
}
