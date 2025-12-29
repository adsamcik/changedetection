using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Pipeline;
using ChangeDetection.Shared.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd;

/// <summary>
/// Integration tests for the streaming conversation flow.
/// Tests the multi-turn dialogue pattern used in interactive watch setup.
/// </summary>
public class ConversationFlowIntegrationTests
{
    #region Multi-Turn Conversation Tests

    [Test]
    public async Task ConversationSession_SupportsMultiTurnDialogue()
    {
        var session = new ConversationSession { SessionId = Guid.NewGuid() };

        // Turn 1: User provides URL
        session.AddUserMessage("https://example.com/events");
        session.AddAssistantMessage("I found the URL. What would you like to monitor on this page?");

        // Turn 2: User specifies intent
        session.AddUserMessage("I want to watch for new events");
        session.AddAssistantMessage("I found 3 event cards on the page. Would you like to monitor these?");

        // Turn 3: User confirms
        session.AddUserMessage("Yes, that looks right");

        session.Messages.Count.ShouldBe(5);
        session.OriginalInputs.Count.ShouldBe(3);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ConversationSession_TracksAwaitingInput()
    {
        var session = new ConversationSession { SessionId = Guid.NewGuid() };

        session.AwaitingUserInput = true;
        session.CurrentPrompt = "Which selector would you like to use?";

        session.AwaitingUserInput.ShouldBeTrue();
        session.CurrentPrompt.ShouldNotBeNullOrWhiteSpace();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ConversationSession_MaintainsDisplayName()
    {
        var session = new ConversationSession { SessionId = Guid.NewGuid() };

        session.DisplayName = "example.com/events";

        session.DisplayName.ShouldBe("example.com/events");
        await Task.CompletedTask;
    }

    #endregion

    #region Presented Options Tracking Tests

    [Test]
    public async Task ConversationSession_TracksSelectorOptions()
    {
        var session = new ConversationSession { SessionId = Guid.NewGuid() };

        session.RecordPresentedOption("sel-1", "Event cards (.event-card)", ".event-card");
        session.RecordPresentedOption("sel-2", "Event titles (.event-title)", ".event-title");
        session.RecordPresentedOption("sel-3", "Full page", null);

        session.PresentedOptions.Count.ShouldBe(3);
        session.PresentedOptions[0].OptionId.ShouldBe("sel-1");
        session.PresentedOptions[1].DisplayText.ShouldBe("Event titles (.event-title)");
        session.PresentedOptions[2].Value.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ConversationSession_TracksUrlSelectionOptions()
    {
        var session = new ConversationSession { SessionId = Guid.NewGuid() };

        session.RecordPresentedOption("url-1", "https://example.com/events", "https://example.com/events");
        session.RecordPresentedOption("url-2", "https://example.com/news", "https://example.com/news");

        session.PresentedOptions.Count.ShouldBe(2);
        await Task.CompletedTask;
    }

    #endregion

    #region Input Anchor Validation Integration Tests

    [Test]
    public async Task InputAnchorValidator_ValidatesAgainstConversationHistory()
    {
        var logger = Substitute.For<ILogger<InputAnchorValidator>>();
        var validator = new InputAnchorValidator(logger);

        var session = new ConversationSession { SessionId = Guid.NewGuid() };
        session.AddUserMessage("Watch https://example.com/events for conferences");
        session.AddUserMessage("Focus on the event cards");

        // Validate URL extraction
        var urlResult = validator.ValidateUrl("https://example.com/events", session.OriginalInputs);
        urlResult.IsValid.ShouldBeTrue();

        // Validate that hallucinated URL fails
        var invalidResult = validator.ValidateUrl("https://other-site.com", session.OriginalInputs);
        invalidResult.IsValid.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task InputAnchorValidator_ValidatesAgainstPresentedOptions()
    {
        var logger = Substitute.For<ILogger<InputAnchorValidator>>();
        var validator = new InputAnchorValidator(logger);

        var session = new ConversationSession { SessionId = Guid.NewGuid() };
        session.RecordPresentedOption("opt-1", "Event cards", ".event-card");
        session.RecordPresentedOption("opt-2", "Event titles", ".event-title");

        // Valid selection
        var validResult = validator.ValidateSelection("Event cards", session.PresentedOptions);
        validResult.IsValid.ShouldBeTrue();
        validResult.MatchedOption.ShouldNotBeNull();
        validResult.MatchedOption!.OptionId.ShouldBe("opt-1");

        // Invalid selection (not presented)
        var invalidResult = validator.ValidateSelection("Product prices", session.PresentedOptions);
        invalidResult.IsValid.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task InputAnchorValidator_ValidatesPartialConfiguration()
    {
        var logger = Substitute.For<ILogger<InputAnchorValidator>>();
        var validator = new InputAnchorValidator(logger);

        var session = new ConversationSession { SessionId = Guid.NewGuid() };
        session.AddUserMessage("https://example.com/events monitor events");
        session.AddUserMessage("Add tag: conferences");

        var config = new PartialWatchConfiguration
        {
            Url = "https://example.com/events",
            Tags = { "conferences" }
        };

        var result = validator.ValidateConfiguration(config, session);

        result.IsValid.ShouldBeTrue();
        result.InvalidFields.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    #endregion

    #region Session Manager Integration Tests

    [Test]
    public async Task ConversationSessionManager_HandlesMultipleConcurrentSessions()
    {
        var logger = Substitute.For<ILogger<ConversationSessionManager>>();
        var manager = new ConversationSessionManager(Substitute.For<IServiceScopeFactory>(), logger);

        var session1 = manager.CreateSession();
        var session2 = manager.CreateSession();
        var session3 = manager.CreateSession();

        session1.AddUserMessage("https://site1.com");
        session2.AddUserMessage("https://site2.com");
        session3.AddUserMessage("https://site3.com");

        manager.ActiveSessionCount.ShouldBe(3);

        var retrieved1 = manager.GetSession(session1.SessionId);
        var retrieved2 = manager.GetSession(session2.SessionId);

        retrieved1.ShouldNotBeNull();
        retrieved2.ShouldNotBeNull();
        retrieved1!.OriginalInputs[0].ShouldContain("site1.com");
        retrieved2!.OriginalInputs[0].ShouldContain("site2.com");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ConversationSessionManager_RemovesSession()
    {
        var logger = Substitute.For<ILogger<ConversationSessionManager>>();
        var manager = new ConversationSessionManager(Substitute.For<IServiceScopeFactory>(), logger);

        var session = manager.CreateSession();
        var sessionId = session.SessionId;

        manager.ActiveSessionCount.ShouldBe(1);

        manager.RemoveSession(sessionId);

        manager.ActiveSessionCount.ShouldBe(0);
        manager.GetSession(sessionId).ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ConversationSessionManager_UpdatesSession()
    {
        var logger = Substitute.For<ILogger<ConversationSessionManager>>();
        var manager = new ConversationSessionManager(Substitute.For<IServiceScopeFactory>(), logger);

        var session = manager.CreateSession();
        session.Configuration.Url = "https://example.com";
        session.CurrentStage = SetupStage.Analyzing;

        manager.UpdateSession(session);

        var retrieved = manager.GetSession(session.SessionId);
        retrieved.ShouldNotBeNull();
        retrieved!.Configuration.Url.ShouldBe("https://example.com");
        retrieved.CurrentStage.ShouldBe(SetupStage.Analyzing);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ConversationSessionManager_GetsSessionsAwaitingInput()
    {
        var logger = Substitute.For<ILogger<ConversationSessionManager>>();
        var manager = new ConversationSessionManager(Substitute.For<IServiceScopeFactory>(), logger);

        var session1 = manager.CreateSession();
        session1.AwaitingUserInput = true;
        session1.CurrentPrompt = "Select a selector";
        manager.UpdateSession(session1);

        var session2 = manager.CreateSession();
        session2.AwaitingUserInput = false;
        manager.UpdateSession(session2);

        var session3 = manager.CreateSession();
        session3.AwaitingUserInput = true;
        session3.CurrentPrompt = "Confirm configuration";
        manager.UpdateSession(session3);

        var awaitingInput = manager.GetSessionsAwaitingInput();

        awaitingInput.Count.ShouldBe(2);
        awaitingInput.ShouldAllBe(s => s.AwaitingUserInput);
        await Task.CompletedTask;
    }

    #endregion

    #region Streaming Chunk DTO Tests

    [Test]
    public async Task AgentStreamChunkDto_HasCorrectProperties()
    {
        var chunk = new AgentStreamChunkDto
        {
            AgentName = "selector-generation",
            ChunkType = ChunkTypeNames.Thinking,
            Content = "Analyzing page structure...",
            Confidence = 0.85,
            IsCollapsible = true,
            Timestamp = DateTimeOffset.UtcNow
        };

        chunk.AgentName.ShouldBe("selector-generation");
        chunk.ChunkType.ShouldBe("thinking");
        chunk.Confidence.ShouldBe(0.85);
        chunk.IsCollapsible.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ChunkTypeNames_HasAllExpectedTypes()
    {
        ChunkTypeNames.Thinking.ShouldBe("thinking");
        ChunkTypeNames.Intermediate.ShouldBe("intermediate");
        ChunkTypeNames.Question.ShouldBe("question");
        ChunkTypeNames.Result.ShouldBe("result");
        ChunkTypeNames.Validation.ShouldBe("validation");
        ChunkTypeNames.Error.ShouldBe("error");
        ChunkTypeNames.Started.ShouldBe("started");
        ChunkTypeNames.Completed.ShouldBe("completed");
        await Task.CompletedTask;
    }

    [Test]
    public async Task AgentNamesDtos_HasAllExpectedAgents()
    {
        AgentNamesDtos.UrlExtraction.ShouldBe("url-extraction");
        AgentNamesDtos.ContentAnalysis.ShouldBe("content-analysis");
        AgentNamesDtos.SelectorGeneration.ShouldBe("selector-generation");
        AgentNamesDtos.Validation.ShouldBe("validation");
        AgentNamesDtos.Synthesis.ShouldBe("synthesis");
        AgentNamesDtos.Resolution.ShouldBe("resolution");
        AgentNamesDtos.SchemaDiscovery.ShouldBe("schema-discovery");
        await Task.CompletedTask;
    }

    [Test]
    public async Task SetupStageNames_HasAllExpectedStages()
    {
        SetupStageNames.Initial.ShouldBe("initial");
        SetupStageNames.Processing.ShouldBe("processing");
        SetupStageNames.UrlSelection.ShouldBe("url-selection");
        SetupStageNames.Fetching.ShouldBe("fetching");
        SetupStageNames.Analyzing.ShouldBe("analyzing");
        SetupStageNames.SelectorRefinement.ShouldBe("selector-refinement");
        SetupStageNames.SchemaDiscovery.ShouldBe("schema-discovery");
        SetupStageNames.SchemaRefinement.ShouldBe("schema-refinement");
        SetupStageNames.Confirmation.ShouldBe("confirmation");
        SetupStageNames.Completed.ShouldBe("completed");
        SetupStageNames.Error.ShouldBe("error");
        await Task.CompletedTask;
    }

    #endregion

    #region Session State DTO Tests

    [Test]
    public async Task SetupSessionStateDto_MapsFromConversationSession()
    {
        var session = new ConversationSession { SessionId = Guid.NewGuid() };
        session.CurrentStage = SetupStage.SelectorRefinement;
        session.AwaitingUserInput = true;
        session.CurrentPrompt = "Select a selector";
        session.Configuration.Url = "https://example.com";
        session.Configuration.CssSelector = ".event-card";

        var dto = new SetupSessionStateDto
        {
            SessionId = session.SessionId,
            Stage = session.CurrentStage.ToString(),
            AwaitingInput = session.AwaitingUserInput,
            CurrentPrompt = session.CurrentPrompt,
            Configuration = new PartialWatchConfigurationDto
            {
                Url = session.Configuration.Url,
                CssSelector = session.Configuration.CssSelector
            }
        };

        dto.SessionId.ShouldBe(session.SessionId);
        dto.Stage.ShouldBe("SelectorRefinement");
        dto.AwaitingInput.ShouldBeTrue();
        dto.CurrentPrompt.ShouldNotBeNull();
        dto.Configuration.ShouldNotBeNull();
        dto.Configuration!.Url.ShouldBe("https://example.com");
        await Task.CompletedTask;
    }

    #endregion

    #region Error Flow Tests

    [Test]
    public async Task ConversationSession_HandlesErrorState()
    {
        var session = new ConversationSession { SessionId = Guid.NewGuid() };

        session.AddUserMessage("https://invalid-url");
        session.CurrentStage = SetupStage.Error;
        session.CurrentPrompt = "Could not fetch the URL. Please check if the URL is correct.";
        session.AwaitingUserInput = true;

        session.CurrentStage.ShouldBe(SetupStage.Error);
        session.AwaitingUserInput.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ConversationSession_CanRecoverFromError()
    {
        var session = new ConversationSession { SessionId = Guid.NewGuid() };

        // Initial error state
        session.CurrentStage = SetupStage.Error;
        session.AddUserMessage("https://example.com/events");

        // User provides corrected input
        session.AddUserMessage("https://correct-url.com/events");
        session.CurrentStage = SetupStage.Fetching;

        session.CurrentStage.ShouldBe(SetupStage.Fetching);
        session.OriginalInputs.Count.ShouldBe(2);
        await Task.CompletedTask;
    }

    #endregion
}

/// <summary>
/// Tests for the pipeline result structure and final configuration building.
/// </summary>
public class PipelineResultIntegrationTests
{
    #region Pipeline Result Structure Tests

    [Test]
    public async Task PipelineResult_SuccessfulCompletion()
    {
        var session = new PipelineSession
        {
            OriginalInput = "https://example.com/events watch events",
            SelectedUrl = new ExtractedUrl
            {
                Url = "https://example.com/events",
                NormalizedUrl = "https://example.com/events",
                IsValid = true
            },
            BestSelector = new GeneratedSelector
            {
                Selector = ".event-card",
                Type = SelectorType.CssSelector,
                Confidence = 0.9f
            }
        };

        var result = new PipelineResult
        {
            IsSuccess = true,
            CurrentStage = PipelineStage.Complete,
            Session = session,
            NeedsUserInput = false,
            FinalConfiguration = new WatchConfiguration
            {
                Url = "https://example.com/events",
                Name = "Example Events",
                CssSelector = ".event-card",
                Confidence = 0.9f
            }
        };

        result.IsSuccess.ShouldBeTrue();
        result.CurrentStage.ShouldBe(PipelineStage.Complete);
        result.NeedsUserInput.ShouldBeFalse();
        result.FinalConfiguration.ShouldNotBeNull();
        result.FinalConfiguration.CssSelector.ShouldBe(".event-card");
        await Task.CompletedTask;
    }

    [Test]
    public async Task PipelineResult_NeedsUserInput()
    {
        var result = new PipelineResult
        {
            IsSuccess = true,
            CurrentStage = PipelineStage.SelectorValidation,
            NeedsUserInput = true,
            UserPrompts = ["Please select which content you want to monitor:"],
            SuggestedOptions =
            [
                new SelectorOption { Label = "Event cards", Value = ".event-card", IsRecommended = true },
                new SelectorOption { Label = "Event titles", Value = ".event-title" },
                new SelectorOption { Label = "Full page", Value = "fullpage" }
            ]
        };

        result.NeedsUserInput.ShouldBeTrue();
        result.UserPrompts.ShouldNotBeEmpty();
        result.SuggestedOptions.Count.ShouldBe(3);
        result.SuggestedOptions.ShouldContain(o => o.IsRecommended);
        await Task.CompletedTask;
    }

    [Test]
    public async Task PipelineResult_FailureWithError()
    {
        var result = new PipelineResult
        {
            IsSuccess = false,
            CurrentStage = PipelineStage.ContentFetching,
            ErrorMessage = "Failed to fetch content: Connection timeout",
            Summary = "Could not access the website"
        };

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        result.Summary.ShouldNotBeNullOrWhiteSpace();
        await Task.CompletedTask;
    }

    #endregion

    #region Watch Configuration Tests

    [Test]
    public async Task WatchConfiguration_HasAllRequiredFields()
    {
        var config = new WatchConfiguration
        {
            Url = "https://example.com/events",
            Name = "Event Monitor",
            Description = "Monitors for new events",
            CssSelector = ".event-card",
            UseJavaScript = true,
            CheckInterval = TimeSpan.FromHours(1),
            Tags = ["events", "monitoring"],
            Confidence = 0.92f
        };

        config.Url.ShouldBe("https://example.com/events");
        config.Name.ShouldBe("Event Monitor");
        config.CssSelector.ShouldBe(".event-card");
        config.UseJavaScript.ShouldBeTrue();
        config.CheckInterval.ShouldBe(TimeSpan.FromHours(1));
        config.Tags.Count.ShouldBe(2);
        config.Confidence.ShouldBe(0.92f);
        await Task.CompletedTask;
    }

    [Test]
    public async Task WatchConfiguration_SupportsXPathSelector()
    {
        var config = new WatchConfiguration
        {
            Url = "https://example.com/events",
            XPathSelector = "//div[@class='event-card']"
        };

        config.CssSelector.ShouldBeNull();
        config.XPathSelector.ShouldNotBeNullOrWhiteSpace();
        await Task.CompletedTask;
    }

    [Test]
    public async Task WatchConfiguration_SupportsTextPattern()
    {
        var config = new WatchConfiguration
        {
            Url = "https://example.com/status",
            TextPattern = "Status: (Online|Offline)"
        };

        config.TextPattern.ShouldNotBeNullOrWhiteSpace();
        await Task.CompletedTask;
    }

    #endregion

    #region Selector Option Tests

    [Test]
    public async Task SelectorOption_HasPreviewContent()
    {
        var option = new SelectorOption
        {
            Label = "Event cards",
            Value = ".event-card",
            Preview = "Annual Conference 2025\nMarch 15, 2025\nPrague Convention Center",
            Confidence = 0.9f,
            IsRecommended = true
        };

        option.Preview.ShouldNotBeNullOrWhiteSpace();
        option.Preview.ShouldContain("Conference");
        option.IsRecommended.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task SelectorOption_SortsCorrectly()
    {
        var options = new List<SelectorOption>
        {
            new() { Label = "Low confidence", Value = ".low", Confidence = 0.5f },
            new() { Label = "High confidence", Value = ".high", Confidence = 0.9f },
            new() { Label = "Medium confidence", Value = ".medium", Confidence = 0.7f }
        };

        var sorted = options.OrderByDescending(o => o.Confidence).ToList();

        sorted[0].Label.ShouldBe("High confidence");
        sorted[1].Label.ShouldBe("Medium confidence");
        sorted[2].Label.ShouldBe("Low confidence");
        await Task.CompletedTask;
    }

    #endregion
}


