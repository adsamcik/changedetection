using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.JobWatch;
using ChangeDetection.Shared.Dtos;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.JobWatch;

[Category("Unit")]
public class JobMatchRelevanceScorerTests : TestBase
{
    [Test]
    public async Task ScoreAsync_WhenListingsReturned_ParsesAndStoresStructuredListingsJson()
    {
        var llmChain = Substitute.For<ILlmProviderChain>();
        llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                {
                    "score": 0.8,
                    "reason": "Strong match for biotech profile",
                    "dimensions": {
                        "education": {
                            "score": 1.0,
                            "status": "PASS",
                            "reason": "MSc requirement is covered"
                        }
                    },
                    "recommendation": "APPLY",
                    "urgency_note": "Application closes soon",
                    "extracted_listings": [
                        {
                            "title": "Laboratory Assistant",
                            "company": "Department of Drug Design and Pharmacology",
                            "location": "Copenhagen",
                            "deadline": "2026-03-13",
                            "education_required": "BSc",
                            "key_skills": ["cell culture", "PCR"],
                            "url": "/all-vacancies/?show=157081",
                            "match_assessment": "PASS - all requirements met"
                        }
                    ]
                }
                """
            });

        var sut = new JobMatchRelevanceScorer(llmChain, CreateLogger<JobMatchRelevanceScorer>());
        var request = new ChangeAnalysisRequest
        {
            DiffContent = "+ Laboratory Assistant - Department of Drug Design and Pharmacology",
            Url = "https://example.com/jobs",
            UserIntent = "Find biotech lab jobs in Copenhagen",
            AnalysisProfileJson = """{"education":{"level":"BSc"},"techniques_strong":["PCR"],"target_locations":["Copenhagen"]}""",
            LinesAdded = 1,
            LinesRemoved = 0
        };

        var result = await sut.ScoreAsync(request, "New laboratory assistant role posted", CancellationToken.None);

        result.Score.ShouldBe(0.8f);
        result.Reason.ShouldContain("[APPLY]");
        result.Reason.ShouldContain("Application closes soon");
        result.DimensionsJson.ShouldContain("education");
        result.ExtractedEntitiesJson.ShouldNotBeNull();
        result.ExtractedEntitiesJson.ShouldContain("Laboratory Assistant");
        result.ExtractedEntitiesJson.ShouldContain("education_required");

        await llmChain.Received(1).ExecuteAsync(
            Arg.Is<string>(prompt => prompt.Contains("extracted_listings") && prompt.Contains("Raw diff excerpt")),
            Arg.Any<LlmRequestOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ScoreAsync_WhenNoListingsReturned_ExtractedEntitiesJsonIsNull()
    {
        var llmChain = Substitute.For<ILlmProviderChain>();
        llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                {
                    "score": 0.3,
                    "reason": "No relevant listings found",
                    "dimensions": {},
                    "recommendation": "SKIP",
                    "urgency_note": null,
                    "extracted_listings": []
                }
                """
            });

        var sut = new JobMatchRelevanceScorer(llmChain, CreateLogger<JobMatchRelevanceScorer>());
        var request = new ChangeAnalysisRequest
        {
            DiffContent = "Minor layout update",
            Url = "https://example.com/jobs",
            AnalysisProfileJson = """{"education":{"level":"BSc"}}""",
            LinesAdded = 1,
            LinesRemoved = 1
        };

        var result = await sut.ScoreAsync(request, "Layout change", CancellationToken.None);

        result.Score.ShouldBe(0.3f);
        result.ExtractedEntitiesJson.ShouldBeNull();
    }

    [Test]
    public async Task ScoreAsync_ExtractedListingsJson_DeserializesToPublicDto()
    {
        var llmChain = Substitute.For<ILlmProviderChain>();
        llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                {
                    "score": 0.9,
                    "reason": "Excellent match",
                    "dimensions": {},
                    "recommendation": "APPLY",
                    "urgency_note": null,
                    "extracted_listings": [
                        {
                            "title": "Senior Researcher",
                            "company": "Novo Nordisk",
                            "location": "Copenhagen",
                            "deadline": "2026-04-01",
                            "education_required": "PhD",
                            "key_skills": ["CRISPR", "mass spectrometry", "Python"],
                            "url": "https://jobs.example.com/12345",
                            "match_assessment": "REVIEW - PhD preferred, MSc acceptable"
                        },
                        {
                            "title": "Lab Technician",
                            "company": "LEO Pharma",
                            "location": "Ballerup",
                            "deadline": null,
                            "education_required": "BSc",
                            "key_skills": ["cell culture"],
                            "url": null,
                            "match_assessment": "PASS - meets all requirements"
                        }
                    ]
                }
                """
            });

        var sut = new JobMatchRelevanceScorer(llmChain, CreateLogger<JobMatchRelevanceScorer>());
        var request = new ChangeAnalysisRequest
        {
            DiffContent = "+ Senior Researcher at Novo Nordisk\n+ Lab Technician at LEO Pharma",
            Url = "https://example.com/jobs",
            AnalysisProfileJson = """{"education":{"level":"MSc"},"techniques_strong":["CRISPR"]}""",
            LinesAdded = 2,
            LinesRemoved = 0
        };

        var result = await sut.ScoreAsync(request, "Two new positions", CancellationToken.None);

        // Verify the JSON round-trips through the public DTO
        result.ExtractedEntitiesJson.ShouldNotBeNull();
        var listings = JsonSerializer.Deserialize<List<ExtractedJobListingDto>>(result.ExtractedEntitiesJson);

        listings.ShouldNotBeNull();
        listings.Count.ShouldBe(2);

        var first = listings[0];
        first.Title.ShouldBe("Senior Researcher");
        first.Company.ShouldBe("Novo Nordisk");
        first.Location.ShouldBe("Copenhagen");
        first.Deadline.ShouldBe("2026-04-01");
        first.EducationRequired.ShouldBe("PhD");
        first.KeySkills.ShouldContain("CRISPR");
        first.KeySkills.ShouldContain("mass spectrometry");
        first.KeySkills.ShouldContain("Python");
        first.Url.ShouldBe("https://jobs.example.com/12345");
        first.MatchAssessment.ShouldContain("REVIEW");

        var second = listings[1];
        second.Title.ShouldBe("Lab Technician");
        second.Company.ShouldBe("LEO Pharma");
        second.Deadline.ShouldBeNull();
        second.Url.ShouldBeNull();
        second.MatchAssessment.ShouldContain("PASS");
    }

    [Test]
    public async Task SanitizeProfileForPrompt_AllowlistsKnownKeys()
    {
        var input = """
        {
            "education": {"level": "MSc", "field": "Biochemistry"},
            "techniques_strong": ["HPLC", "PCR"],
            "target_locations": ["Copenhagen"],
            "malicious_key": "ignore me",
            "dealbreakers": ["relocation required"]
        }
        """;

        var sanitized = JobMatchRelevanceScorer.SanitizeProfileForPrompt(input);

        sanitized.ShouldContain("education");
        sanitized.ShouldContain("techniques_strong");
        sanitized.ShouldContain("target_locations");
        sanitized.ShouldContain("dealbreakers");
        sanitized.ShouldNotContain("malicious_key");
    }
}
