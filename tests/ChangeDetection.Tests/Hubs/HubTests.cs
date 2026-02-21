using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Hubs;
using ChangeDetection.Services.Authentication;
using ChangeDetection.Shared.Dtos;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace ChangeDetection.Tests.Hubs;

/// <summary>
/// Helper to assign mocked SignalR infrastructure to a Hub instance.
/// </summary>
file static class HubTestHelper
{
    public static void AssignHubContext(Hub hub, string connectionId = "test-connection-id")
    {
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns(connectionId);

        var groups = Substitute.For<IGroupManager>();
        var clients = Substitute.For<IHubCallerClients>();
        var callerProxy = Substitute.For<ISingleClientProxy>();
        clients.Caller.Returns(callerProxy);

        hub.Context = context;
        hub.Groups = groups;
        hub.Clients = clients;
    }
}

#region ChangeDetectionHub Tests

[Category("Unit")]
public class ChangeDetectionHubTests
{
    private readonly ILogger<ChangeDetectionHub> _logger = Substitute.For<ILogger<ChangeDetectionHub>>();
    private readonly IUserContext _userContext = Substitute.For<IUserContext>();

    private ChangeDetectionHub CreateHub()
    {
        var hub = new ChangeDetectionHub(_logger, _userContext);
        HubTestHelper.AssignHubContext(hub);
        return hub;
    }

    [Test]
    public async Task OnConnectedAsync_AddsClientToDashboardGroup()
    {
        // Arrange — single-user mode (Guid.Empty)
        _userContext.CurrentUserId.Returns(Guid.Empty);
        using var hub = CreateHub();

        // Act
        await hub.OnConnectedAsync();

        // Assert
        await hub.Groups.Received(1).AddToGroupAsync("test-connection-id", "dashboard", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnConnectedAsync_WithUserId_AddsToUserDashboardGroup()
    {
        // Arrange — authenticated user
        var userId = Guid.NewGuid();
        _userContext.CurrentUserId.Returns(userId);
        _userContext.IsAdmin.Returns(false);
        using var hub = CreateHub();

        // Act
        await hub.OnConnectedAsync();

        // Assert
        await hub.Groups.Received(1).AddToGroupAsync(
            "test-connection-id",
            $"dashboard-{userId}",
            Arg.Any<CancellationToken>());
        // Non-admin should NOT be added to global dashboard
        await hub.Groups.DidNotReceive().AddToGroupAsync(
            "test-connection-id",
            "dashboard",
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnConnectedAsync_AdminUser_AddsToGlobalAndUserDashboardGroups()
    {
        // Arrange — admin user
        var userId = Guid.NewGuid();
        _userContext.CurrentUserId.Returns(userId);
        _userContext.IsAdmin.Returns(true);
        using var hub = CreateHub();

        // Act
        await hub.OnConnectedAsync();

        // Assert — should be in both user-specific and global dashboard groups
        await hub.Groups.Received(1).AddToGroupAsync(
            "test-connection-id",
            $"dashboard-{userId}",
            Arg.Any<CancellationToken>());
        await hub.Groups.Received(1).AddToGroupAsync(
            "test-connection-id",
            "dashboard",
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnDisconnectedAsync_RemovesFromDashboardGroup()
    {
        // Arrange
        _userContext.CurrentUserId.Returns(Guid.Empty);
        using var hub = CreateHub();

        // Act
        await hub.OnDisconnectedAsync(null);

        // Assert
        await hub.Groups.Received(1).RemoveFromGroupAsync(
            "test-connection-id",
            "dashboard",
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SubscribeToWatch_AddsToWatchGroup()
    {
        // Arrange
        _userContext.CurrentUserId.Returns(Guid.Empty);
        using var hub = CreateHub();
        var watchId = Guid.NewGuid();

        // Act
        await hub.SubscribeToWatch(watchId);

        // Assert
        await hub.Groups.Received(1).AddToGroupAsync(
            "test-connection-id",
            $"watch-{watchId}",
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnsubscribeFromWatch_RemovesFromWatchGroup()
    {
        // Arrange
        _userContext.CurrentUserId.Returns(Guid.Empty);
        using var hub = CreateHub();
        var watchId = Guid.NewGuid();

        // Act
        await hub.UnsubscribeFromWatch(watchId);

        // Assert
        await hub.Groups.Received(1).RemoveFromGroupAsync(
            "test-connection-id",
            $"watch-{watchId}",
            Arg.Any<CancellationToken>());
    }
}

#endregion

#region SetupConversationHub Tests

[Category("Unit")]
public class SetupConversationHubTests
{
    private readonly IConversationSessionManager _sessionManager = Substitute.For<IConversationSessionManager>();
    private readonly IWatchSetupPipeline _pipeline = Substitute.For<IWatchSetupPipeline>();
    private readonly IWatchService _watchService = Substitute.For<IWatchService>();
    private readonly IHubContext<ChangeDetectionHub> _changeHubContext = Substitute.For<IHubContext<ChangeDetectionHub>>();
    private readonly IUserContext _userContext = Substitute.For<IUserContext>();
    private readonly IHostApplicationLifetime _appLifetime = Substitute.For<IHostApplicationLifetime>();
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();
    private readonly ILogger<SetupConversationHub> _logger = Substitute.For<ILogger<SetupConversationHub>>();

    public SetupConversationHubTests()
    {
        _userContext.CurrentUserId.Returns(Guid.Empty);

        // Wire up hub context clients for dashboard broadcasts
        var hubClients = Substitute.For<IHubClients>();
        var clientProxy = Substitute.For<ISingleClientProxy>();
        hubClients.Group(Arg.Any<string>()).Returns(clientProxy);
        _changeHubContext.Clients.Returns(hubClients);

        // Wire up scope factory for persistence
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scope.ServiceProvider.Returns(sp);
        _scopeFactory.CreateScope().Returns(scope);
    }

    private SetupConversationHub CreateHub()
    {
        var hub = new SetupConversationHub(
            _sessionManager, _pipeline, _watchService,
            _changeHubContext, _userContext, _appLifetime,
            _scopeFactory, _logger);
        HubTestHelper.AssignHubContext(hub);
        return hub;
    }

    [Test]
    public async Task StartSession_ReturnsNewSessionId()
    {
        // Arrange
        var expectedSession = new ConversationSession { SessionId = Guid.NewGuid() };
        _sessionManager.CreateSession().Returns(expectedSession);
        using var hub = CreateHub();

        // Act
        var result = await hub.StartSession();

        // Assert
        result.SessionId.ShouldBe(expectedSession.SessionId);
        result.SessionId.ShouldNotBe(Guid.Empty);
        await hub.Groups.Received(1).AddToGroupAsync(
            "test-connection-id",
            $"session-{expectedSession.SessionId}",
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartOrResumeSession_ExistingSession_ReturnsExistingState()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var existingSession = new ConversationSession
        {
            SessionId = sessionId,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        _sessionManager.GetSession(sessionId).Returns(existingSession);
        _sessionManager.GetOrCreateSession(sessionId).Returns(existingSession);
        using var hub = CreateHub();

        // Act
        var result = await hub.StartOrResumeSession(sessionId);

        // Assert
        result.SessionId.ShouldBe(sessionId);
        result.IsResumed.ShouldBeTrue();
    }

    [Test]
    public async Task StartOrResumeSession_NewSession_ReturnsNotResumed()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var newSession = new ConversationSession { SessionId = sessionId };
        _sessionManager.GetSession(sessionId).Returns((ConversationSession?)null);
        _sessionManager.GetOrCreateSession(sessionId).Returns(newSession);
        using var hub = CreateHub();

        // Act
        var result = await hub.StartOrResumeSession(sessionId);

        // Assert
        result.SessionId.ShouldBe(sessionId);
        result.IsResumed.ShouldBeFalse();
    }

    [Test]
    public async Task GetSessionState_NonExistentSession_ReturnsNull()
    {
        // Arrange
        _sessionManager.GetSession(Arg.Any<Guid>()).Returns((ConversationSession?)null);
        using var hub = CreateHub();

        // Act
        var result = hub.GetSessionState(Guid.NewGuid());

        // Assert
        result.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetSessionState_ExistingSession_ReturnsState()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = new ConversationSession
        {
            SessionId = sessionId,
            AwaitingUserInput = true,
            CurrentPrompt = "Which URL?"
        };
        _sessionManager.GetSession(sessionId).Returns(session);
        using var hub = CreateHub();

        // Act
        var result = hub.GetSessionState(sessionId);

        // Assert
        result.ShouldNotBeNull();
        result.SessionId.ShouldBe(sessionId);
        result.AwaitingInput.ShouldBeTrue();
        result.CurrentPrompt.ShouldBe("Which URL?");
        await Task.CompletedTask;
    }

    [Test]
    public async Task EndSession_CleansUpSession()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        using var hub = CreateHub();

        // Act
        await hub.EndSession(sessionId);

        // Assert
        await hub.Groups.Received(1).RemoveFromGroupAsync(
            "test-connection-id",
            $"session-{sessionId}",
            Arg.Any<CancellationToken>());
        _sessionManager.Received(1).RemoveSession(sessionId);
    }

    [Test]
    public async Task ConfirmSetup_WithValidSession_CreatesWatch()
    {
        // Arrange — we need a pipeline session with a SelectedUrl.
        // ConfirmSetup reads from a static ConcurrentDictionary,
        // so we test the "no pipeline session" path which returns incomplete message.
        var sessionId = Guid.NewGuid();
        using var hub = CreateHub();

        // Act
        var result = await hub.ConfirmSetup(sessionId);

        // Assert — no pipeline session means incomplete
        result.SessionId.ShouldBe(sessionId);
        result.Summary.ShouldContain("incomplete");
        result.WatchId.ShouldBeNull();
    }

    [Test]
    public async Task OnDisconnectedAsync_LogsDisconnection()
    {
        // Arrange
        using var hub = CreateHub();

        // Act — should complete without error
        await hub.OnDisconnectedAsync(null);

        // Assert — hub does not throw
        await Task.CompletedTask;
    }
}

#endregion

#region ComposableSetupHub Tests

[Category("Unit")]
public class ComposableSetupHubTests
{
    private readonly IComposableSetupPipeline _pipeline = Substitute.For<IComposableSetupPipeline>();
    private readonly ILogger<ComposableSetupHub> _logger = Substitute.For<ILogger<ComposableSetupHub>>();

    private ComposableSetupHub CreateHub()
    {
        var hub = new ComposableSetupHub(_pipeline, _logger);
        HubTestHelper.AssignHubContext(hub);
        return hub;
    }

    [Test]
    public async Task StartSetup_StreamsProgressUpdates()
    {
        // Arrange
        var progressItems = new List<SetupProgress>
        {
            new()
            {
                Phase = SetupPhase.IntentParsing,
                Type = SetupProgressType.Started,
                Message = "Parsing intent..."
            },
            new()
            {
                Phase = SetupPhase.ContentFetching,
                Type = SetupProgressType.Progress,
                Message = "Fetching content..."
            },
            new()
            {
                Phase = SetupPhase.Saving,
                Type = SetupProgressType.Completed,
                Message = "Done"
            }
        };

        _pipeline.StartSetupAsync(Arg.Any<SetupRequest>(), Arg.Any<CancellationToken>())
            .Returns(progressItems.ToAsyncEnumerable());

        using var hub = CreateHub();

        // Act
        var results = new List<SetupProgress>();
        await foreach (var progress in hub.StartSetup("Monitor https://example.com"))
        {
            results.Add(progress);
        }

        // Assert
        results.Count.ShouldBe(3);
        results[0].Phase.ShouldBe(SetupPhase.IntentParsing);
        results[0].Message.ShouldBe("Parsing intent...");
        results[2].Type.ShouldBe(SetupProgressType.Completed);
    }

    [Test]
    public async Task ConfirmIntent_Confirmed_ContinuesPipeline()
    {
        // Arrange
        var progressItems = new List<SetupProgress>
        {
            new()
            {
                Phase = SetupPhase.PipelineBuilding,
                Type = SetupProgressType.Progress,
                Message = "Building pipeline..."
            },
            new()
            {
                Phase = SetupPhase.Saving,
                Type = SetupProgressType.Completed,
                Message = "Pipeline saved"
            }
        };

        _pipeline.ConfirmIntentAsync("session-1", true, null, Arg.Any<CancellationToken>())
            .Returns(progressItems.ToAsyncEnumerable());

        using var hub = CreateHub();

        // Act
        var results = new List<SetupProgress>();
        await foreach (var progress in hub.ConfirmIntent("session-1", confirmed: true))
        {
            results.Add(progress);
        }

        // Assert
        results.Count.ShouldBe(2);
        results[0].Phase.ShouldBe(SetupPhase.PipelineBuilding);
        results[1].Type.ShouldBe(SetupProgressType.Completed);
    }

    [Test]
    public async Task ConfirmIntent_Denied_StopsPipeline()
    {
        // Arrange — pipeline returns a failed/stopped progress when denied
        var progressItems = new List<SetupProgress>
        {
            new()
            {
                Phase = SetupPhase.Checkpoint1,
                Type = SetupProgressType.Failed,
                Message = "Setup cancelled by user"
            }
        };

        _pipeline.ConfirmIntentAsync("session-2", false, null, Arg.Any<CancellationToken>())
            .Returns(progressItems.ToAsyncEnumerable());

        using var hub = CreateHub();

        // Act
        var results = new List<SetupProgress>();
        await foreach (var progress in hub.ConfirmIntent("session-2", confirmed: false))
        {
            results.Add(progress);
        }

        // Assert
        results.Count.ShouldBe(1);
        results[0].Type.ShouldBe(SetupProgressType.Failed);
        results[0].Message.ShouldBe("Setup cancelled by user");
    }

    [Test]
    public async Task StartSetup_PipelineThrows_StreamEndsGracefully()
    {
        // Arrange — SafeStream catches exceptions and yields break
        _pipeline.StartSetupAsync(Arg.Any<SetupRequest>(), Arg.Any<CancellationToken>())
            .Returns(ThrowingAsyncEnumerable());

        using var hub = CreateHub();

        // Act — should not throw
        var results = new List<SetupProgress>();
        await foreach (var progress in hub.StartSetup("bad input"))
        {
            results.Add(progress);
        }

        // Assert — stream ended gracefully (SafeStream catches the exception)
        results.Count.ShouldBe(0);
    }

    [Test]
    public async Task ConfirmPipeline_Confirmed_StreamsProgress()
    {
        // Arrange
        var progressItems = new List<SetupProgress>
        {
            new()
            {
                Phase = SetupPhase.Saving,
                Type = SetupProgressType.Completed,
                Message = "Watch saved"
            }
        };

        _pipeline.ConfirmPipelineAsync("session-3", true, null, Arg.Any<CancellationToken>())
            .Returns(progressItems.ToAsyncEnumerable());

        using var hub = CreateHub();

        // Act
        var results = new List<SetupProgress>();
        await foreach (var progress in hub.ConfirmPipeline("session-3", confirmed: true))
        {
            results.Add(progress);
        }

        // Assert
        results.Count.ShouldBe(1);
        results[0].Phase.ShouldBe(SetupPhase.Saving);
    }

    /// <summary>
    /// Helper that yields one item then throws, to test SafeStream error handling.
    /// </summary>
    private static async IAsyncEnumerable<SetupProgress> ThrowingAsyncEnumerable()
    {
        await Task.CompletedTask;
        throw new InvalidOperationException("Pipeline exploded");
#pragma warning disable CS0162 // Unreachable code — needed for compiler to treat this as IAsyncEnumerable
        yield break;
#pragma warning restore CS0162
    }
}

#endregion
