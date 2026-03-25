using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.GroupWatch;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.GroupWatch;

[Category("Unit")]
public class OutreachLlmEnrichmentTests
{
    private readonly OutreachSignalDetector _sut = new();

    // --- Score boundary tests: LLM should only run in 3.0–7.0 range ---

    [Test]
    public async Task EnrichWithLlm_ScoreBelow3_ReturnsUnchanged()
    {
        var assessment = new OutreachAssessment(false, [], 1.5f);
        var llm = Substitute.For<ILlmProviderChain>();

        var result = await _sut.EnrichWithLlmAsync(
            assessment, "some content", "TestCo", llm, CancellationToken.None);

        result.ShouldBeSameAs(assessment);
        await llm.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnrichWithLlm_ScoreAbove7_ReturnsUnchanged()
    {
        var assessment = new OutreachAssessment(true,
            [new OutreachSignal("GeneralApplication", "general application", 0.95f)], 8.5f);
        var llm = Substitute.For<ILlmProviderChain>();

        var result = await _sut.EnrichWithLlmAsync(
            assessment, "some content", "TestCo", llm, CancellationToken.None);

        result.ShouldBeSameAs(assessment);
        await llm.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnrichWithLlm_ScoreExactly3_CallsLlm()
    {
        var assessment = new OutreachAssessment(true,
            [new OutreachSignal("AlwaysHiring", "always looking", 0.8f)], 3.0f);
        var llm = CreateMockLlm("""{"receptive": true, "confidence": 0.8, "reason": "Approachable tone"}""");

        var result = await _sut.EnrichWithLlmAsync(
            assessment, "page content", "TestCo", llm, CancellationToken.None);

        result.ShouldNotBeSameAs(assessment);
        result.Signals.ShouldContain(s => s.Type == "LlmAssessment");
    }

    [Test]
    public async Task EnrichWithLlm_ScoreExactly7_CallsLlm()
    {
        var assessment = new OutreachAssessment(true,
            [new OutreachSignal("GeneralApplication", "general application", 0.95f)], 7.0f);
        var llm = CreateMockLlm("""{"receptive": true, "confidence": 0.9, "reason": "Very welcoming"}""");

        var result = await _sut.EnrichWithLlmAsync(
            assessment, "page content", "TestCo", llm, CancellationToken.None);

        result.ShouldNotBeSameAs(assessment);
        result.Signals.ShouldContain(s => s.Type == "LlmAssessment");
    }

    // --- LLM confirms receptive: score should increase ---

    [Test]
    public async Task EnrichWithLlm_LlmConfirmsReceptive_BoostsScore()
    {
        var assessment = new OutreachAssessment(true,
            [new OutreachSignal("TalentCommunity", "talent community", 0.85f)], 5.0f);
        var llm = CreateMockLlm("""{"receptive": true, "confidence": 0.9, "reason": "Open to cold applications"}""");

        var result = await _sut.EnrichWithLlmAsync(
            assessment, "page content", "TestCo", llm, CancellationToken.None);

        result.OverallScore.ShouldBeGreaterThan(5.0f);
        result.IsOutreachFriendly.ShouldBeTrue();
        result.Signals.ShouldContain(s => s.Type == "LlmAssessment");
        result.Signals.First(s => s.Type == "LlmAssessment").Evidence
            .ShouldContain("Open to cold applications");
    }

    // --- LLM says not receptive: score should decrease ---

    [Test]
    public async Task EnrichWithLlm_LlmSaysNotReceptive_ReducesScore()
    {
        var assessment = new OutreachAssessment(true,
            [new OutreachSignal("AlwaysHiring", "always looking", 0.8f)], 4.0f);
        var llm = CreateMockLlm("""{"receptive": false, "confidence": 0.85, "reason": "Corporate tone, no invitations"}""");

        var result = await _sut.EnrichWithLlmAsync(
            assessment, "page content", "TestCo", llm, CancellationToken.None);

        result.OverallScore.ShouldBeLessThan(4.0f);
        result.Signals.ShouldContain(s => s.Type == "LlmAssessment");
    }

    // --- Low confidence LLM: score should not change much ---

    [Test]
    public async Task EnrichWithLlm_LowConfidence_DoesNotChangeScore()
    {
        var assessment = new OutreachAssessment(true,
            [new OutreachSignal("TalentCommunity", "talent community", 0.85f)], 5.0f);
        var llm = CreateMockLlm("""{"receptive": true, "confidence": 0.3, "reason": "Unclear signals"}""");

        var result = await _sut.EnrichWithLlmAsync(
            assessment, "page content", "TestCo", llm, CancellationToken.None);

        // Low confidence (< 0.6) should not adjust the score
        result.OverallScore.ShouldBe(5.0f);
        // But LLM signal should still be added
        result.Signals.ShouldContain(s => s.Type == "LlmAssessment");
    }

    // --- LLM failure scenarios: should return regex assessment unchanged ---

    [Test]
    public async Task EnrichWithLlm_LlmReturnsFailure_ReturnsRegexAssessment()
    {
        var assessment = new OutreachAssessment(true,
            [new OutreachSignal("AlwaysHiring", "always looking", 0.8f)], 5.0f);
        var llm = Substitute.For<ILlmProviderChain>();
        llm.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = false, ErrorMessage = "Provider unavailable" });

        var result = await _sut.EnrichWithLlmAsync(
            assessment, "page content", "TestCo", llm, CancellationToken.None);

        result.ShouldBeSameAs(assessment);
    }

    [Test]
    public async Task EnrichWithLlm_LlmReturnsEmptyContent_ReturnsRegexAssessment()
    {
        var assessment = new OutreachAssessment(true,
            [new OutreachSignal("AlwaysHiring", "always looking", 0.8f)], 5.0f);
        var llm = Substitute.For<ILlmProviderChain>();
        llm.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = true, Content = "" });

        var result = await _sut.EnrichWithLlmAsync(
            assessment, "page content", "TestCo", llm, CancellationToken.None);

        result.ShouldBeSameAs(assessment);
    }

    [Test]
    public async Task EnrichWithLlm_LlmReturnsInvalidJson_ReturnsRegexAssessment()
    {
        var assessment = new OutreachAssessment(true,
            [new OutreachSignal("AlwaysHiring", "always looking", 0.8f)], 5.0f);
        var llm = CreateMockLlm("not valid json at all");

        var result = await _sut.EnrichWithLlmAsync(
            assessment, "page content", "TestCo", llm, CancellationToken.None);

        result.ShouldBeSameAs(assessment);
    }

    [Test]
    public async Task EnrichWithLlm_Timeout_ReturnsRegexAssessment()
    {
        var assessment = new OutreachAssessment(true,
            [new OutreachSignal("AlwaysHiring", "always looking", 0.8f)], 5.0f);
        var llm = Substitute.For<ILlmProviderChain>();
        llm.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns<LlmResponse>(_ => throw new OperationCanceledException("Timed out"));

        var result = await _sut.EnrichWithLlmAsync(
            assessment, "page content", "TestCo", llm, CancellationToken.None);

        result.ShouldBeSameAs(assessment);
    }

    // --- Content truncation test ---

    [Test]
    public async Task EnrichWithLlm_LongContent_TruncatesTo2000Chars()
    {
        var assessment = new OutreachAssessment(true,
            [new OutreachSignal("TalentCommunity", "talent community", 0.85f)], 5.0f);
        var longContent = new string('x', 5000);
        string? capturedPrompt = null;

        var llm = Substitute.For<ILlmProviderChain>();
        llm.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedPrompt = callInfo.Arg<string>();
                return new LlmResponse
                {
                    IsSuccess = true,
                    Content = """{"receptive": true, "confidence": 0.7, "reason": "Test"}"""
                };
            });

        await _sut.EnrichWithLlmAsync(
            assessment, longContent, "TestCo", llm, CancellationToken.None);

        capturedPrompt.ShouldNotBeNull();
        // The prompt should NOT contain 5000 x's — only 2000
        capturedPrompt.ShouldNotContain(new string('x', 2001));
    }

    // --- Score clamping tests ---

    [Test]
    public async Task EnrichWithLlm_BoostDoesNotExceed10()
    {
        var assessment = new OutreachAssessment(true,
            [new OutreachSignal("GeneralApplication", "general app", 0.95f)], 7.0f);
        var llm = CreateMockLlm("""{"receptive": true, "confidence": 1.0, "reason": "Very welcoming"}""");

        var result = await _sut.EnrichWithLlmAsync(
            assessment, "page content", "TestCo", llm, CancellationToken.None);

        result.OverallScore.ShouldBeLessThanOrEqualTo(10f);
    }

    [Test]
    public async Task EnrichWithLlm_ReductionDoesNotGoBelowZero()
    {
        var assessment = new OutreachAssessment(true,
            [new OutreachSignal("AlwaysHiring", "always looking", 0.8f)], 3.0f);
        var llm = CreateMockLlm("""{"receptive": false, "confidence": 1.0, "reason": "Very corporate"}""");

        var result = await _sut.EnrichWithLlmAsync(
            assessment, "page content", "TestCo", llm, CancellationToken.None);

        result.OverallScore.ShouldBeGreaterThanOrEqualTo(0f);
    }

    // --- Preserves existing signals ---

    [Test]
    public async Task EnrichWithLlm_PreservesExistingSignals()
    {
        var existingSignals = new List<OutreachSignal>
        {
            new("GeneralApplication", "general application link", 0.95f),
            new("TalentCommunity", "talent pool", 0.85f)
        };
        var assessment = new OutreachAssessment(true, existingSignals, 5.0f);
        var llm = CreateMockLlm("""{"receptive": true, "confidence": 0.8, "reason": "Looks good"}""");

        var result = await _sut.EnrichWithLlmAsync(
            assessment, "page content", "TestCo", llm, CancellationToken.None);

        result.Signals.Count.ShouldBe(3); // 2 existing + 1 LLM
        result.Signals.ShouldContain(s => s.Type == "GeneralApplication");
        result.Signals.ShouldContain(s => s.Type == "TalentCommunity");
        result.Signals.ShouldContain(s => s.Type == "LlmAssessment");
    }

    // --- Helper ---

    private static ILlmProviderChain CreateMockLlm(string responseContent)
    {
        var llm = Substitute.For<ILlmProviderChain>();
        llm.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = true, Content = responseContent });
        return llm;
    }
}
