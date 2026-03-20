using ChangeDetection.Services.GroupWatch;
using ChangeDetection.Shared.Dtos;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.GroupWatch;

[Category("Unit")]
public class JobDeduplicationRealDataTests
{
    private readonly JobDeduplicationService _sut = new();

    [Test]
    public void DeduplicateAcrossSources_WithCompanyEmbeddedInTitle_MergesRealisticCrossSourceJobPosts()
    {
        var workdayItem = new GroupResultItemDto
        {
            Title = "Research Scientist",
            Company = "Novo Nordisk",
            Location = "Copenhagen",
            Url = "https://novonordisk.wd3.myworkdayjobs.com/en-US/careers/job/Research-Scientist_12345",
            Source = "workday.com",
            SourceWatchId = "watch-workday",
            Sources = ["https://novonordisk.wd3.myworkdayjobs.com/en-US/careers/job/Research-Scientist_12345"],
            SourceNames = ["workday.com"],
            SourceWatchIds = ["watch-workday"],
            RelevanceScore = 0.82f,
            FirstSeen = new DateTime(2026, 3, 18, 8, 0, 0, DateTimeKind.Utc),
            IsNew = true
        };

        var jobindexItem = new GroupResultItemDto
        {
            Title = "Research Scientist, Novo Nordisk",
            Company = "Novo Nordisk",
            Location = "Copenhagen",
            Url = "https://www.jobindex.dk/jobannonce/567890/research-scientist-novo-nordisk",
            Source = "jobindex.dk",
            SourceWatchId = "watch-jobindex",
            Sources = ["https://www.jobindex.dk/jobannonce/567890/research-scientist-novo-nordisk"],
            SourceNames = ["jobindex.dk"],
            SourceWatchIds = ["watch-jobindex"],
            RelevanceScore = 0.79f,
            FirstSeen = new DateTime(2026, 3, 18, 9, 0, 0, DateTimeKind.Utc),
            IsNew = true
        };

        var result = _sut.DeduplicateAcrossSources([workdayItem, jobindexItem]);

        result.Count.ShouldBe(1);
        result[0].SourceNames.ShouldBe(["workday.com", "jobindex.dk"], ignoreOrder: true);
        result[0].Sources.ShouldBe(
            [
                "https://novonordisk.wd3.myworkdayjobs.com/en-US/careers/job/Research-Scientist_12345",
                "https://www.jobindex.dk/jobannonce/567890/research-scientist-novo-nordisk"
            ],
            ignoreOrder: true);
        result[0].IsMultiSource.ShouldBeTrue();
    }
}
