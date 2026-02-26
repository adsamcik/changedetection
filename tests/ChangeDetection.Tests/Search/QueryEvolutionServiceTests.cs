using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Search;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Search;

[Category("Unit")]
public class QueryEvolutionServiceTests : TestBase
{
    [Test]
    public async Task EvolveQueryAsync_MaxIterationsReached_ReturnsNull()
    {
        var llm = Substitute.For<ILlmProviderChain>();
        var sut = new QueryEvolutionService(llm, CreateLogger<QueryEvolutionService>());

        var request = new QueryEvolutionRequest
        {
            OriginalQuery = "test",
            UserIntent = "Find test results",
            Results = [new SearchResult { Url = "https://a.com", Title = "A", Position = 1 }],
            IterationCount = 3,
            MaxIterations = 3
        };

        var result = await sut.EvolveQueryAsync(request);
        result.ShouldBeNull();
        await llm.DidNotReceive().ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EvolveQueryAsync_EmptyResults_SuggestsBroaderQuery()
    {
        var llm = Substitute.For<ILlmProviderChain>();
        var sut = new QueryEvolutionService(llm, CreateLogger<QueryEvolutionService>());

        var request = new QueryEvolutionRequest
        {
            OriginalQuery = "\"very specific phrase\" site:obscure.com",
            UserIntent = "Find specific info",
            Results = [],
            IterationCount = 0
        };

        var result = await sut.EvolveQueryAsync(request);
        result.ShouldNotBeNull();
        result.QualityScore.ShouldBe(0f);
        result.ShouldEvolve.ShouldBeTrue();
        result.SuggestedQueries.Count.ShouldBeGreaterThan(0);
        result.SuggestedQueries[0].Technique.ShouldBe("broaden_topic");
    }

    [Test]
    public async Task EvolveQueryAsync_LlmUnavailable_ReturnsNull()
    {
        var llm = Substitute.For<ILlmProviderChain>();
        llm.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = false, ErrorMessage = "No providers available" });

        var sut = new QueryEvolutionService(llm, CreateLogger<QueryEvolutionService>());
        var request = new QueryEvolutionRequest
        {
            OriginalQuery = "test query",
            UserIntent = "Find test info",
            Results = [new SearchResult { Url = "https://a.com", Title = "A", Position = 1 }]
        };

        var result = await sut.EvolveQueryAsync(request);
        result.ShouldBeNull();
    }

    [Test]
    public async Task EvolveQueryAsync_LlmThrows_ReturnsNull()
    {
        var llm = Substitute.For<ILlmProviderChain>();
        llm.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var sut = new QueryEvolutionService(llm, CreateLogger<QueryEvolutionService>());
        var request = new QueryEvolutionRequest
        {
            OriginalQuery = "test query",
            UserIntent = "Find info",
            Results = [new SearchResult { Url = "https://a.com", Title = "A", Position = 1 }]
        };

        var result = await sut.EvolveQueryAsync(request);
        result.ShouldBeNull();
    }

    [Test]
    public async Task EvolveQueryAsync_ValidLlmResponse_ReturnsEvolutionResult()
    {
        var llm = Substitute.For<ILlmProviderChain>();
        llm.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                {
                    "qualityScore": 0.6,
                    "qualityAssessment": "Results partially match intent but miss key aspects",
                    "shouldEvolve": true,
                    "reasoning": "Adding site operator would improve relevance",
                    "suggestions": [
                        {
                            "query": "\"dotnet change detection\" site:github.com",
                            "rationale": "Focusing on GitHub repos for code-specific results",
                            "expectedImprovement": "More relevant code repositories",
                            "technique": "site_operator"
                        }
                    ]
                }
                """
            });

        var sut = new QueryEvolutionService(llm, CreateLogger<QueryEvolutionService>());
        var request = new QueryEvolutionRequest
        {
            OriginalQuery = "dotnet change detection",
            UserIntent = "Find open source change detection tools for .NET",
            Results = [
                new SearchResult { Url = "https://a.com", Title = "Generic CD tool", Position = 1, Snippet = "A general tool" },
                new SearchResult { Url = "https://b.com", Title = "Python detector", Position = 2, Snippet = "Python based" }
            ]
        };

        var result = await sut.EvolveQueryAsync(request);
        result.ShouldNotBeNull();
        result.QualityScore.ShouldBe(0.6f);
        result.ShouldEvolve.ShouldBeTrue();
        result.SuggestedQueries.Count.ShouldBe(1);
        result.SuggestedQueries[0].Query.ShouldContain("site:github.com");
        result.SuggestedQueries[0].Technique.ShouldBe("site_operator");
    }

    [Test]
    public async Task EvolveQueryAsync_HighQualityResults_NoEvolution()
    {
        var llm = Substitute.For<ILlmProviderChain>();
        llm.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                {
                    "qualityScore": 0.9,
                    "qualityAssessment": "Excellent results matching user intent",
                    "shouldEvolve": false,
                    "reasoning": "Results are highly relevant, no improvement needed",
                    "suggestions": []
                }
                """
            });

        var sut = new QueryEvolutionService(llm, CreateLogger<QueryEvolutionService>());
        var request = new QueryEvolutionRequest
        {
            OriginalQuery = "latest dotnet 10 features",
            UserIntent = "Track new features in .NET 10",
            Results = [
                new SearchResult { Url = "https://devblogs.microsoft.com/dotnet/", Title = ".NET 10 Features", Position = 1 }
            ]
        };

        var result = await sut.EvolveQueryAsync(request);
        result.ShouldNotBeNull();
        result.QualityScore.ShouldBeGreaterThan(0.8f);
        result.ShouldEvolve.ShouldBeFalse();
        result.SuggestedQueries.ShouldBeEmpty();
    }

    // --- Static method tests ---

    [Test]
    public async Task BuildPrompt_IncludesQueryAndIntent()
    {
        var request = new QueryEvolutionRequest
        {
            OriginalQuery = "test query",
            UserIntent = "Find test information",
            Results = [
                new SearchResult { Url = "https://a.com", Title = "Result A", Position = 1, Snippet = "A snippet" },
                new SearchResult { Url = "https://b.com", Title = "Result B", Position = 2 }
            ],
            IterationCount = 0,
            MaxIterations = 3
        };

        var prompt = QueryEvolutionService.BuildPrompt(request);
        prompt.ShouldContain("test query");
        prompt.ShouldContain("Find test information");
        prompt.ShouldContain("Result A");
        prompt.ShouldContain("https://a.com");
        prompt.ShouldContain("1 of 3");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildPrompt_LimitsToTenResults()
    {
        var results = Enumerable.Range(1, 20).Select(i => new SearchResult
        {
            Url = $"https://example.com/{i}",
            Title = $"Result {i}",
            Position = i
        }).ToList();

        var request = new QueryEvolutionRequest
        {
            OriginalQuery = "test",
            UserIntent = "test",
            Results = results
        };

        var prompt = QueryEvolutionService.BuildPrompt(request);
        prompt.ShouldContain("Result 10");
        prompt.ShouldNotContain("Result 11");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ParseResponse_ValidJson_ReturnsResult()
    {
        var json = """
        {
            "qualityScore": 0.75,
            "qualityAssessment": "Good but could be better",
            "shouldEvolve": true,
            "reasoning": "Missing some key results",
            "suggestions": [
                {
                    "query": "refined query",
                    "rationale": "Better targeting",
                    "expectedImprovement": "More relevant results",
                    "technique": "add_keywords"
                }
            ]
        }
        """;

        var request = new QueryEvolutionRequest
        {
            OriginalQuery = "test",
            UserIntent = "test",
            Results = []
        };

        var result = QueryEvolutionService.ParseResponse(json, request);
        result.ShouldNotBeNull();
        result.QualityScore.ShouldBe(0.75f);
        result.ShouldEvolve.ShouldBeTrue();
        result.SuggestedQueries.Count.ShouldBe(1);
        result.SuggestedQueries[0].Query.ShouldBe("refined query");
        result.SuggestedQueries[0].Technique.ShouldBe("add_keywords");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ParseResponse_InvalidJson_ReturnsNull()
    {
        var result = QueryEvolutionService.ParseResponse("not json at all", new QueryEvolutionRequest
        {
            OriginalQuery = "test",
            UserIntent = "test",
            Results = []
        });
        result.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ParseResponse_ClampsQualityScore()
    {
        var json = """{"qualityScore": 1.5, "qualityAssessment": "test", "shouldEvolve": false, "suggestions": []}""";
        var result = QueryEvolutionService.ParseResponse(json, new QueryEvolutionRequest
        {
            OriginalQuery = "test",
            UserIntent = "test",
            Results = []
        });
        result.ShouldNotBeNull();
        result.QualityScore.ShouldBe(1.0f);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ParseResponse_SkipsSuggestionsWithMissingFields()
    {
        var json = """
        {
            "qualityScore": 0.5,
            "qualityAssessment": "test",
            "shouldEvolve": true,
            "suggestions": [
                {"query": "good query", "rationale": "good reason"},
                {"query": "missing rationale"},
                {"rationale": "missing query"}
            ]
        }
        """;

        var result = QueryEvolutionService.ParseResponse(json, new QueryEvolutionRequest
        {
            OriginalQuery = "test",
            UserIntent = "test",
            Results = []
        });
        result.ShouldNotBeNull();
        result.SuggestedQueries.Count.ShouldBe(1);
        result.SuggestedQueries[0].Query.ShouldBe("good query");
        await Task.CompletedTask;
    }
}
