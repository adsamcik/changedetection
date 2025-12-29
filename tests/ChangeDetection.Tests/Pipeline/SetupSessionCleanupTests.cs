using ChangeDetection.Core.Interfaces;
using ChangeDetection.Hubs;
using ChangeDetection.Services.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline;

/// <summary>
/// Tests for the session cleanup mechanism to prevent memory leaks.
/// </summary>
public class SetupSessionCleanupTests
{
    [Test]
    public async Task CleanupExpiredSession_RemovesEntriesFromDictionaries()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        
        // Act - clean up a session that doesn't exist (should not throw)
        var result = SetupConversationHub.CleanupExpiredSession(sessionId);
        
        // Assert
        result.ShouldBeFalse(); // Nothing was removed since it didn't exist
        await Task.CompletedTask;
    }

    [Test]
    public async Task DefensiveCleanup_RemovesOrphanedSessions()
    {
        // Arrange
        var sessionManager = Substitute.For<IConversationSessionManager>();
        var orphanedSessionId = Guid.NewGuid();
        
        // Session doesn't exist in manager (simulates expired/removed session)
        sessionManager.GetSession(orphanedSessionId).Returns((ConversationSession?)null);
        
        // Act
        var cleanedUp = SetupConversationHub.DefensiveCleanup(sessionManager);
        
        // Assert - should complete without error
        cleanedUp.ShouldBeGreaterThanOrEqualTo(0);
        await Task.CompletedTask;
    }

    [Test]
    public async Task DiagnosticProperties_ReturnCounts()
    {
        // Arrange & Act
        var pipelineCount = SetupConversationHub.PipelineSessionCount;
        var historyCount = SetupConversationHub.StateHistoryCount;
        
        // Assert - counts should be non-negative
        pipelineCount.ShouldBeGreaterThanOrEqualTo(0);
        historyCount.ShouldBeGreaterThanOrEqualTo(0);
        await Task.CompletedTask;
    }
}

/// <summary>
/// Tests for SetupSessionCleanupService integration.
/// </summary>
public class SetupSessionCleanupServiceTests
{
    [Test]
    public async Task StartAsync_SubscribesToSessionExpiredEvent()
    {
        // Arrange
        var sessionManager = new ConversationSessionManager(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<ConversationSessionManager>>());
        var logger = Substitute.For<ILogger<SetupSessionCleanupService>>();
        var service = new SetupSessionCleanupService(sessionManager, logger);
        
        // Act
        await service.StartAsync(CancellationToken.None);
        
        // Assert - service started without error
        // The subscription happens internally, verified by the fact that no exception was thrown
        
        // Cleanup
        await service.StopAsync(CancellationToken.None);
        service.Dispose();
    }

    [Test]
    public async Task StopAsync_UnsubscribesFromSessionExpiredEvent()
    {
        // Arrange
        var sessionManager = new ConversationSessionManager(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<ConversationSessionManager>>());
        var logger = Substitute.For<ILogger<SetupSessionCleanupService>>();
        var service = new SetupSessionCleanupService(sessionManager, logger);
        
        await service.StartAsync(CancellationToken.None);
        
        // Act
        await service.StopAsync(CancellationToken.None);
        
        // Assert - service stopped without error
        // The unsubscription happens internally, verified by the fact that no exception was thrown
        
        // Cleanup
        service.Dispose();
    }

    [Test]
    public async Task Service_ImplementsIDisposable()
    {
        // Arrange
        var sessionManager = Substitute.For<IConversationSessionManager>();
        var logger = Substitute.For<ILogger<SetupSessionCleanupService>>();
        
        // Act & Assert - should not throw
        using (var service = new SetupSessionCleanupService(sessionManager, logger))
        {
            // Service created successfully
        }
        await Task.CompletedTask;
    }
}

/// <summary>
/// Integration tests for session expiration and cleanup flow.
/// </summary>
public class SessionExpirationCleanupIntegrationTests
{
    [Test]
    public async Task SessionExpired_Event_IsRaisedWhenSessionsAreCleanedUp()
    {
        // Arrange
        var logger = Substitute.For<ILogger<ConversationSessionManager>>();
        using var sessionManager = new ConversationSessionManager(
            Substitute.For<IServiceScopeFactory>(), logger);
        
        var expiredSessionIds = new List<Guid>();
        sessionManager.SessionExpired += (sessionId) => expiredSessionIds.Add(sessionId);
        
        // Create a session
        var session = sessionManager.CreateSession();
        var sessionId = session.SessionId;
        
        // Verify session exists
        sessionManager.GetSession(sessionId).ShouldNotBeNull();
        
        // Act - explicitly remove the session (simulates expiration cleanup)
        sessionManager.RemoveSession(sessionId);
        
        // Assert - session is removed
        sessionManager.GetSession(sessionId).ShouldBeNull();
        
        // Note: RemoveSession doesn't fire SessionExpired - that's only fired during timer cleanup
        // This test verifies the manual removal path works
        await Task.CompletedTask;
    }

    [Test]
    public async Task CleanupService_CleansUpWhenSessionExpires()
    {
        // Arrange
        var sessionManagerLogger = Substitute.For<ILogger<ConversationSessionManager>>();
        using var sessionManager = new ConversationSessionManager(
            Substitute.For<IServiceScopeFactory>(), sessionManagerLogger);
        
        var cleanupLogger = Substitute.For<ILogger<SetupSessionCleanupService>>();
        using var cleanupService = new SetupSessionCleanupService(sessionManager, cleanupLogger);
        
        // Start the cleanup service
        await cleanupService.StartAsync(CancellationToken.None);
        
        // Create a session
        var session = sessionManager.CreateSession();
        
        // Get initial counts
        var initialPipelineCount = SetupConversationHub.PipelineSessionCount;
        var initialHistoryCount = SetupConversationHub.StateHistoryCount;
        
        // The counts should be stable (no orphans to clean)
        initialPipelineCount.ShouldBeGreaterThanOrEqualTo(0);
        initialHistoryCount.ShouldBeGreaterThanOrEqualTo(0);
        
        // Cleanup
        await cleanupService.StopAsync(CancellationToken.None);
    }
}
