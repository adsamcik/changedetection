using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Search;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Search;

[Category("Unit")]
public class AdversarialSearchAnalyzerTests : TestBase
{
    [Test]
    public async Task Analyze_SingleProvider_CannotAnalyze()
    {
        var sut = new AdversarialSearchAnalyzer(CreateLogger<AdversarialSearchAnalyzer>());
        var results = new MultiProviderResultSet
        {
            Query = "test",
            ProviderResults = [
                new SearchResultSet
                {
                    ProviderId = "p1", Query = "test", Results = [
                        new SearchResult { Url = "https://a.com", Title = "A", Position = 1 }
                    ]
                }
            ],
            MergedResults = []
        };

        var analysis = sut.Analyze(results);
        analysis.OverallTrustScore.ShouldBe(1.0f);
        analysis.HasAnomalies.ShouldBeFalse();
        analysis.Assessment.ShouldContain("fewer than 2");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Analyze_ConsistentResults_HighTrust()
    {
        var sut = new AdversarialSearchAnalyzer(CreateLogger<AdversarialSearchAnalyzer>());
        var results = new MultiProviderResultSet
        {
            Query = "test",
            ProviderResults = [
                new SearchResultSet
                {
                    ProviderId = "p1", Query = "test", Results = [
                        new SearchResult { Url = "https://a.com", Title = "A", Position = 1 },
                        new SearchResult { Url = "https://b.com", Title = "B", Position = 2 }
                    ]
                },
                new SearchResultSet
                {
                    ProviderId = "p2", Query = "test", Results = [
                        new SearchResult { Url = "https://a.com", Title = "A", Position = 1 },
                        new SearchResult { Url = "https://b.com", Title = "B", Position = 3 }
                    ]
                }
            ],
            MergedResults = []
        };

        var analysis = sut.Analyze(results);
        analysis.HasAnomalies.ShouldBeFalse();
        analysis.OverallTrustScore.ShouldBeGreaterThan(0.9f);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Analyze_ExclusiveTopResult_FlagsAnomaly()
    {
        var sut = new AdversarialSearchAnalyzer(CreateLogger<AdversarialSearchAnalyzer>());
        var results = new MultiProviderResultSet
        {
            Query = "test",
            ProviderResults = [
                new SearchResultSet
                {
                    ProviderId = "p1", Query = "test", Results = [
                        new SearchResult { Url = "https://suspicious.com", Title = "Suspicious", Position = 1 },
                        new SearchResult { Url = "https://shared.com", Title = "Shared", Position = 2 }
                    ]
                },
                new SearchResultSet
                {
                    ProviderId = "p2", Query = "test", Results = [
                        new SearchResult { Url = "https://shared.com", Title = "Shared", Position = 1 },
                        new SearchResult { Url = "https://other.com", Title = "Other", Position = 2 }
                    ]
                }
            ],
            MergedResults = []
        };

        var analysis = sut.Analyze(results);
        analysis.HasAnomalies.ShouldBeTrue();
        analysis.Anomalies.ShouldContain(a =>
            a.Url == "https://suspicious.com" && a.Type == AnomalyType.ExclusiveResult);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Analyze_WildlyDifferentRankings_FlagsDiscrepancy()
    {
        var sut = new AdversarialSearchAnalyzer(CreateLogger<AdversarialSearchAnalyzer>());
        var results = new MultiProviderResultSet
        {
            Query = "test",
            ProviderResults = [
                new SearchResultSet
                {
                    ProviderId = "p1", Query = "test", Results = [
                        new SearchResult { Url = "https://fluctuating.com", Title = "Fluctuating", Position = 1 }
                    ]
                },
                new SearchResultSet
                {
                    ProviderId = "p2", Query = "test", Results = [
                        new SearchResult { Url = "https://fluctuating.com", Title = "Fluctuating", Position = 15 }
                    ]
                }
            ],
            MergedResults = []
        };

        var analysis = sut.Analyze(results);
        analysis.HasAnomalies.ShouldBeTrue();
        var anomaly = analysis.Anomalies.First(a => a.Type == AnomalyType.RankingDiscrepancy);
        anomaly.Url.ShouldBe("https://fluctuating.com");
        anomaly.Description.ShouldContain("14"); // Diff of 14
        await Task.CompletedTask;
    }

    [Test]
    public async Task Analyze_ExclusiveLowRankResult_NotFlagged()
    {
        var sut = new AdversarialSearchAnalyzer(CreateLogger<AdversarialSearchAnalyzer>());
        var results = new MultiProviderResultSet
        {
            Query = "test",
            ProviderResults = [
                new SearchResultSet
                {
                    ProviderId = "p1", Query = "test", Results = [
                        new SearchResult { Url = "https://shared.com", Title = "Shared", Position = 1 },
                        new SearchResult { Url = "https://lowrank-exclusive.com", Title = "Low", Position = 10 }
                    ]
                },
                new SearchResultSet
                {
                    ProviderId = "p2", Query = "test", Results = [
                        new SearchResult { Url = "https://shared.com", Title = "Shared", Position = 1 }
                    ]
                }
            ],
            MergedResults = []
        };

        var analysis = sut.Analyze(results);
        // Low-rank exclusive results are not flagged (only top-3)
        analysis.Anomalies.ShouldNotContain(a => a.Url == "https://lowrank-exclusive.com");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Analyze_HighSeverityAnomalies_LowerTrustScore()
    {
        var sut = new AdversarialSearchAnalyzer(CreateLogger<AdversarialSearchAnalyzer>());
        var results = new MultiProviderResultSet
        {
            Query = "test",
            ProviderResults = [
                new SearchResultSet
                {
                    ProviderId = "p1", Query = "test", Results = [
                        new SearchResult { Url = "https://sus1.com", Title = "Sus 1", Position = 1 },
                        new SearchResult { Url = "https://sus2.com", Title = "Sus 2", Position = 2 }
                    ]
                },
                new SearchResultSet
                {
                    ProviderId = "p2", Query = "test", Results = [
                        new SearchResult { Url = "https://legit.com", Title = "Legit", Position = 1 }
                    ]
                }
            ],
            MergedResults = []
        };

        var analysis = sut.Analyze(results);
        analysis.OverallTrustScore.ShouldBeLessThan(1.0f);
        analysis.Assessment.ShouldContain("anomaly");
        await Task.CompletedTask;
    }

    // --- Static method tests ---

    [Test]
    public async Task BuildPositionMap_CorrectlyMapsUrlsToPositions()
    {
        var results = new MultiProviderResultSet
        {
            Query = "test",
            ProviderResults = [
                new SearchResultSet
                {
                    ProviderId = "p1", Query = "test", Results = [
                        new SearchResult { Url = "https://a.com", Title = "A", Position = 1 },
                        new SearchResult { Url = "https://b.com", Title = "B", Position = 2 }
                    ]
                },
                new SearchResultSet
                {
                    ProviderId = "p2", Query = "test", Results = [
                        new SearchResult { Url = "https://a.com", Title = "A", Position = 3 }
                    ]
                }
            ],
            MergedResults = []
        };

        var map = AdversarialSearchAnalyzer.BuildPositionMap(results);
        map.Count.ShouldBe(2);
        map["https://a.com"]["p1"].ShouldBe(1);
        map["https://a.com"]["p2"].ShouldBe(3);
        map["https://b.com"]["p1"].ShouldBe(2);
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildPositionMap_SkipsFailedProviders()
    {
        var results = new MultiProviderResultSet
        {
            Query = "test",
            ProviderResults = [
                new SearchResultSet
                {
                    ProviderId = "p1", Query = "test", IsSuccess = false, Results = [
                        new SearchResult { Url = "https://a.com", Title = "A", Position = 1 }
                    ]
                },
                new SearchResultSet
                {
                    ProviderId = "p2", Query = "test", Results = [
                        new SearchResult { Url = "https://b.com", Title = "B", Position = 1 }
                    ]
                }
            ],
            MergedResults = []
        };

        var map = AdversarialSearchAnalyzer.BuildPositionMap(results);
        map.Count.ShouldBe(1);
        map.ShouldContainKey("https://b.com");
        map.ShouldNotContainKey("https://a.com");
        await Task.CompletedTask;
    }
}
