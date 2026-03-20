using ChangeDetection.Services.GroupWatch;
using ChangeDetection.Shared.Dtos;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.JobWatch;

[Category("Unit")]
public class JobDeduplicationServiceTests
{
    private readonly JobDeduplicationService _sut = new();

    [Test]
    public void DeduplicateAcrossSources_WithExactUrlMatch_MergesSourcesAndKeepsBestSignals()
    {
        var earlier = new GroupResultItemDto
        {
            Title = "Scientist",
            Url = "https://jobs.example.com/123",
            Company = "Novo Nordisk",
            Location = "Copenhagen",
            Source = "Workday",
            SourceWatchId = "watch-a",
            SourceNames = ["Workday"],
            SourceWatchIds = ["watch-a"],
            Sources = ["https://jobs.example.com/123"],
            RelevanceScore = 0.62f,
            FirstSeen = new DateTime(2026, 3, 10, 8, 0, 0, DateTimeKind.Utc),
            IsNew = false
        };

        var higherScore = new GroupResultItemDto
        {
            Title = "Scientist",
            Url = "https://jobs.example.com/123",
            Company = "Novo Nordisk",
            Location = "Copenhagen",
            Source = "Jobindex",
            SourceWatchId = "watch-b",
            SourceNames = ["Jobindex"],
            SourceWatchIds = ["watch-b"],
            Sources = ["https://jobs.example.com/123"],
            RelevanceScore = 0.91f,
            FirstSeen = new DateTime(2026, 3, 12, 8, 0, 0, DateTimeKind.Utc),
            IsNew = true
        };

        var result = _sut.DeduplicateAcrossSources([earlier, higherScore]);

        result.Count.ShouldBe(1);
        result[0].RelevanceScore.ShouldBe(0.91f);
        result[0].FirstSeen.ShouldBe(earlier.FirstSeen);
        result[0].IsNew.ShouldBeFalse();
        result[0].Source.ShouldBe("Jobindex");
        result[0].SourceNames.ShouldBe(["Jobindex", "Workday"], ignoreOrder: true);
        result[0].SourceWatchIds.ShouldBe(["watch-a", "watch-b"], ignoreOrder: true);
        result[0].IsMultiSource.ShouldBeTrue();
    }

    [Test]
    public void DeduplicateAcrossSources_WithSimilarTitleAndCompany_MergesAcrossPortals()
    {
        var first = CreateItem(
            title: "Senior Scientist (m/f/d)",
            company: "Novo Nordisk",
            location: "Copenhagen",
            source: "Workday",
            watchId: "watch-a",
            url: "https://company.example/job/1");

        var second = CreateItem(
            title: "Senior Scientist - Copenhagen",
            company: "Novo Nordisk",
            location: "Copenhagen",
            source: "Jobindex",
            watchId: "watch-b",
            url: "https://jobindex.dk/job/123");

        var result = _sut.DeduplicateAcrossSources([first, second]);

        result.Count.ShouldBe(1);
        result[0].Sources.ShouldBe(["https://company.example/job/1", "https://jobindex.dk/job/123"], ignoreOrder: true);
        result[0].SourceNames.ShouldBe(["Workday", "Jobindex"], ignoreOrder: true);
        result[0].IsMultiSource.ShouldBeTrue();
    }

    [Test]
    public void DeduplicateAcrossSources_UsesLocationFallbackWhenCompanyMissing()
    {
        var first = CreateItem(
            title: "Research Assistant - Copenhagen",
            company: null,
            location: "Copenhagen",
            source: "Portal A",
            watchId: "watch-a",
            url: "https://porta.example/job/1");

        var second = CreateItem(
            title: "Research Assistant",
            company: null,
            location: "Copenhagen",
            source: "Portal B",
            watchId: "watch-b",
            url: "https://portb.example/job/1");

        var result = _sut.DeduplicateAcrossSources([first, second]);

        result.Count.ShouldBe(1);
        result[0].IsMultiSource.ShouldBeTrue();
    }

    [Test]
    public void DeduplicateAcrossSources_WithDifferentCompanies_DoesNotOverMerge()
    {
        var first = CreateItem(
            title: "Scientist",
            company: "Novo Nordisk",
            location: "Copenhagen",
            source: "Workday",
            watchId: "watch-a",
            url: "https://company-a.example/job/1");

        var second = CreateItem(
            title: "Scientist",
            company: "AGC Biologics",
            location: "Copenhagen",
            source: "Jobindex",
            watchId: "watch-b",
            url: "https://company-b.example/job/1");

        var result = _sut.DeduplicateAcrossSources([first, second]);

        result.Count.ShouldBe(2);
        result.All(item => !item.IsMultiSource).ShouldBeTrue();
    }

    private static GroupResultItemDto CreateItem(
        string title,
        string? company,
        string? location,
        string source,
        string watchId,
        string url) => new()
        {
            Title = title,
            Url = url,
            Company = company,
            Location = location,
            Source = source,
            SourceWatchId = watchId,
            Sources = [url],
            SourceNames = [source],
            SourceWatchIds = [watchId],
            RelevanceScore = 0.8f,
            FirstSeen = new DateTime(2026, 3, 12, 8, 0, 0, DateTimeKind.Utc),
            IsNew = true
        };
}
