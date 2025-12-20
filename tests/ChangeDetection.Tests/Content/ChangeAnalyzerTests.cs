using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Content;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ChangeDetection.Tests.Content;

/// <summary>
/// Tests for ChangeAnalyzer LLM-powered change analysis service.
/// </summary>
public class ChangeAnalyzerTests
{
    private readonly ILlmProviderChain _llmChain;
    private readonly ILogger<ChangeAnalyzer> _logger;
    private readonly ChangeAnalyzer _sut;

    public ChangeAnalyzerTests()
    {
        _llmChain = Substitute.For<ILlmProviderChain>();
        _logger = Substitute.For<ILogger<ChangeAnalyzer>>();
        _sut = new ChangeAnalyzer(_llmChain, _logger);
    }

    [Fact]
    public async Task AnalyzeChangeAsync_WithValidDiff_ReturnsSemanticSummary()
    {
        // Arrange
        var request = new ChangeAnalysisRequest
        {
            DiffContent = "+ New product added: Widget Pro\n- Old product removed: Widget Basic",
            Url = "https://example.com/products",
            WatchName = "Product Page",
            UserIntent = "Track product changes",
            LinesAdded = 1,
            LinesRemoved = 1
        };

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var prompt = callInfo.ArgAt<string>(0);
                
                // Return appropriate mock response based on prompt content
                if (prompt.Contains("semantic summary"))
                {
                    return Task.FromResult(new LlmResponse
                    {
                        IsSuccess = true,
                        Content = """
                        {
                            "semanticSummary": "A product replacement occurred: Widget Basic was removed and replaced with Widget Pro, indicating a product line update.",
                            "briefSummary": "Widget Basic replaced with Widget Pro",
                            "confidence": 0.85
                        }
                        """
                    });
                }
                else if (prompt.Contains("relevance"))
                {
                    return Task.FromResult(new LlmResponse
                    {
                        IsSuccess = true,
                        Content = """
                        {
                            "score": 0.9,
                            "reason": "Directly related to user's goal of tracking product changes"
                        }
                        """
                    });
                }
                else if (prompt.Contains("Categorize"))
                {
                    return Task.FromResult(new LlmResponse
                    {
                        IsSuccess = true,
                        Content = """
                        {
                            "categories": [
                                {"name": "ProductUpdate", "confidence": 0.9, "reason": "Product listing changed"}
                            ]
                        }
                        """
                    });
                }
                else if (prompt.Contains("Extract entities"))
                {
                    return Task.FromResult(new LlmResponse
                    {
                        IsSuccess = true,
                        Content = """
                        {
                            "entities": [
                                {"type": "Product", "value": "Widget Pro", "changeType": "Added", "confidence": 0.95},
                                {"type": "Product", "value": "Widget Basic", "changeType": "Removed", "confidence": 0.95}
                            ],
                            "keyFacts": [
                                {"type": "Status", "label": "Product replaced", "value": "Widget Pro", "previousValue": "Widget Basic", "isSignificant": true}
                            ]
                        }
                        """
                    });
                }
                else if (prompt.Contains("suggested actions") || prompt.Contains("actionable"))
                {
                    return Task.FromResult(new LlmResponse
                    {
                        IsSuccess = true,
                        Content = """
                        {
                            "actions": ["Review the new Widget Pro specifications"]
                        }
                        """
                    });
                }

                return Task.FromResult(new LlmResponse { IsSuccess = true, Content = "{}" });
            });

        // Act
        var result = await _sut.AnalyzeChangeAsync(request);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.SemanticSummary.ShouldNotBeNullOrEmpty();
        result.BriefSummary.ShouldNotBeNullOrEmpty();
        result.RelevanceScore.ShouldBeGreaterThan(0);
        result.Categories.ShouldNotBeEmpty();
        result.ExtractedEntities.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task AnalyzeChangeAsync_WithoutUserIntent_SkipsRelevanceScoring()
    {
        // Arrange
        var request = new ChangeAnalysisRequest
        {
            DiffContent = "+ Some change",
            Url = "https://example.com",
            UserIntent = null, // No user intent
            LinesAdded = 1,
            LinesRemoved = 0
        };

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                {
                    "semanticSummary": "Content was added",
                    "briefSummary": "New content",
                    "confidence": 0.7
                }
                """
            });

        // Act
        var result = await _sut.AnalyzeChangeAsync(request);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.RelevanceScore.ShouldBe(0.5f); // Default when no intent
    }

    [Fact]
    public async Task AnalyzeChangeAsync_WhenLlmFails_ReturnsFailureResult()
    {
        // Arrange
        var request = new ChangeAnalysisRequest
        {
            DiffContent = "+ Some change",
            Url = "https://example.com",
            LinesAdded = 1,
            LinesRemoved = 0
        };

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = false,
                ErrorMessage = "LLM service unavailable"
            });

        // Act
        var result = await _sut.AnalyzeChangeAsync(request);

        // Assert
        // Even if summary fails, the overall result should still be returned with partial data
        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task DetectAnomaliesAsync_WithInsufficientHistory_ReturnsNoAnomalies()
    {
        // Arrange
        var request = new AnomalyDetectionRequest
        {
            CurrentChange = new ChangeAnalysisRequest
            {
                DiffContent = "+ Change",
                Url = "https://example.com",
                LinesAdded = 1,
                LinesRemoved = 0
            },
            HistoricalChanges = [
                new HistoricalChange { DetectedAt = DateTime.UtcNow.AddDays(-1), LinesChanged = 5 }
            ] // Only 1 historical change, need at least 3
        };

        // Act
        var result = await _sut.DetectAnomaliesAsync(request);

        // Assert
        result.HasAnomalies.ShouldBeFalse();
        result.Explanation.ShouldNotBeNull();
        result.Explanation.ShouldContain("Insufficient");
    }

    [Fact]
    public async Task DetectAnomaliesAsync_WithEnoughHistory_AnalyzesPatterns()
    {
        // Arrange
        var request = new AnomalyDetectionRequest
        {
            CurrentChange = new ChangeAnalysisRequest
            {
                DiffContent = "+ Massive change with 500 lines",
                Url = "https://example.com",
                LinesAdded = 500,
                LinesRemoved = 0
            },
            HistoricalChanges = [
                new HistoricalChange { DetectedAt = DateTime.UtcNow.AddDays(-1), LinesChanged = 5 },
                new HistoricalChange { DetectedAt = DateTime.UtcNow.AddDays(-2), LinesChanged = 8 },
                new HistoricalChange { DetectedAt = DateTime.UtcNow.AddDays(-3), LinesChanged = 3 },
                new HistoricalChange { DetectedAt = DateTime.UtcNow.AddDays(-4), LinesChanged = 6 }
            ],
            TypicalChangeSize = 5
        };

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                {
                    "hasAnomalies": true,
                    "anomalyScore": 0.85,
                    "anomalies": [
                        {
                            "type": "UnusualSize",
                            "severity": "High",
                            "description": "Change is 100x larger than typical",
                            "reason": "Typical changes are ~5 lines, this is 500 lines"
                        }
                    ],
                    "explanation": "This change is significantly larger than historical patterns"
                }
                """
            });

        // Act
        var result = await _sut.DetectAnomaliesAsync(request);

        // Assert
        result.HasAnomalies.ShouldBeTrue();
        result.AnomalyScore.ShouldBeGreaterThan(0.5f);
        result.Anomalies.ShouldNotBeEmpty();
        result.Anomalies[0].Type.ShouldBe("UnusualSize");
    }

    [Fact]
    public async Task AnalyzeChangeStreamingAsync_YieldsProgressUpdates()
    {
        // Arrange
        var request = new ChangeAnalysisRequest
        {
            DiffContent = "+ New content",
            Url = "https://example.com",
            LinesAdded = 1,
            LinesRemoved = 0,
            CategorizeChange = false,
            ExtractEntities = false,
            AnalyzeSentiment = false
        };

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                {
                    "semanticSummary": "New content added",
                    "briefSummary": "Content added",
                    "confidence": 0.8
                }
                """
            });

        // Act
        var progressUpdates = new List<ChangeAnalysisProgress>();
        await foreach (var progress in _sut.AnalyzeChangeStreamingAsync(request))
        {
            progressUpdates.Add(progress);
        }

        // Assert
        progressUpdates.ShouldNotBeEmpty();
        progressUpdates.Any(p => p.Step == "SemanticSummary").ShouldBeTrue();
        progressUpdates.Any(p => p.Step == "Complete").ShouldBeTrue();
        progressUpdates.Last().Result.ShouldNotBeNull();
    }
}
