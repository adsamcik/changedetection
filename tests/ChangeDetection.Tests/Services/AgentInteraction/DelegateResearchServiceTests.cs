using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.AgentInteraction;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.AgentInteraction;

[Category("Unit")]
public class DelegateResearchServiceTests : TestBase
{
    [Test]
    public async Task RequestResearchAsync_GeneratesPrompts_CollectsPaste_AndSummarizesCandidates()
    {
        var llmProviderChain = Substitute.For<ILlmProviderChain>();
        var askUserService = Substitute.For<IAskUserService>();

        llmProviderChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                new LlmResponse
                {
                    IsSuccess = true,
                    Content = """
                        {
                          "prompts": [
                            {
                              "title": "External shortlist",
                              "prompt": "Find the best direct portals.",
                              "expectedFormat": "- https://example.com — why"
                            }
                          ]
                        }
                        """
                },
                new LlmResponse
                {
                    IsSuccess = true,
                    Content = """
                        {
                          "summary": "One strong delegated candidate.",
                          "candidates": [
                            {
                              "url": "https://external.example.com/jobs",
                              "title": "External Jobs",
                              "reasoning": "Direct portal mentioned in the pasted research.",
                              "source": "External shortlist"
                            }
                          ]
                        }
                        """
                });

        askUserService.AskOptionalAsync(Arg.Any<AgentQuestion>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new UserResponse
            {
                QuestionId = "q-1",
                ResourceType = "paste",
                ResourceContent = "- https://external.example.com/jobs — relevant portal"
            });

        var sut = new DelegateResearchService(llmProviderChain, askUserService, CreateLogger<DelegateResearchService>());

        var result = await sut.RequestResearchAsync("pharma QC jobs in Basel", "Current candidates: none");

        result.GeneratedPrompts.Count.ShouldBe(1);
        result.Candidates.Count.ShouldBe(1);
        result.Candidates[0].Url.ShouldBe("https://external.example.com/jobs");
        result.Summary.ShouldContain("delegated");

        await askUserService.Received(1).AskOptionalAsync(
            Arg.Is<AgentQuestion>(question =>
                question.Input != null &&
                typeof(ResourceInput).IsInstanceOfType(question.Input) &&
                question.Context!.Contains("Find the best direct portals.", StringComparison.Ordinal)),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }
}
