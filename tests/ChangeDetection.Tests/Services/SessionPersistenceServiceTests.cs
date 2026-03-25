using System.Linq.Expressions;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Persistence;
using LiteDB;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services;

[Category("Unit")]
public class SessionPersistenceServiceTests : TestBase
{
    private readonly ILiteCollection<PersistedSession> _collection;
    private readonly SessionPersistenceService _sut;

    public SessionPersistenceServiceTests()
    {
        _collection = Substitute.For<ILiteCollection<PersistedSession>>();
        var mockDatabase = Substitute.For<ILiteDatabase>();
        mockDatabase.GetCollection<PersistedSession>("persisted_sessions").Returns(_collection);

        var dbContext = Substitute.ForPartsOf<LiteDbContext>("Filename=:memory:");
        dbContext.Database.Returns(mockDatabase);

        _sut = new SessionPersistenceService(new ThreadSafeLiteDbContext(dbContext));
    }

    private static ConversationSession CreateTestSession(Guid? sessionId = null)
    {
        return new ConversationSession
        {
            SessionId = sessionId ?? Guid.NewGuid()
        };
    }

    private static PersistedSession CreateTestPersistedSession(Guid sessionId, Guid? ownerId = null)
    {
        return new PersistedSession
        {
            SessionId = sessionId,
            OwnerId = ownerId ?? Guid.Empty,
            DisplayName = "Test Session",
            CurrentStage = SetupStage.Initial,
            LastActivityAt = DateTimeOffset.UtcNow,
            MessagesJson = "[]",
            OriginalInputsJson = "[]",
            ConfigurationJson = "{}",
            PresentedOptionsJson = "[]",
            StateHistoryJson = "[]"
        };
    }

    [Test]
    public async Task SaveSessionAsync_NewSession_UpsertsIntoCollection()
    {
        // Arrange
        var session = CreateTestSession();
        var ownerId = Guid.NewGuid();
        _collection.FindOne(Arg.Any<Expression<Func<PersistedSession, bool>>>())
            .Returns((PersistedSession?)null);

        // Act
        await _sut.SaveSessionAsync(session, ownerId);

        // Assert
        _collection.Received(1).Upsert(Arg.Is<PersistedSession>(p =>
            p.SessionId == session.SessionId && p.OwnerId == ownerId));
        _collection.DidNotReceive().Update(Arg.Any<PersistedSession>());
        _collection.DidNotReceive().Insert(Arg.Any<PersistedSession>());
    }

    [Test]
    public async Task SaveSessionAsync_ExistingSession_UpsertsPreservingExistingId()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = CreateTestSession(sessionId);
        var ownerId = Guid.NewGuid();
        var existing = CreateTestPersistedSession(sessionId);
        existing.Id = Guid.NewGuid();

        _collection.FindOne(Arg.Any<Expression<Func<PersistedSession, bool>>>())
            .Returns(existing);

        // Act
        await _sut.SaveSessionAsync(session, ownerId);

        // Assert
        _collection.Received(1).Upsert(Arg.Is<PersistedSession>(p =>
            p.Id == existing.Id && p.SessionId == sessionId));
        _collection.DidNotReceive().Insert(Arg.Any<PersistedSession>());
        _collection.DidNotReceive().Update(Arg.Any<PersistedSession>());
    }

    [Test]
    public async Task LoadSessionAsync_ExistingSession_ReturnsConversationSession()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var persisted = CreateTestPersistedSession(sessionId);

        _collection.FindOne(Arg.Any<Expression<Func<PersistedSession, bool>>>())
            .Returns(persisted);

        // Act
        var result = await _sut.LoadSessionAsync(sessionId);

        // Assert
        result.ShouldNotBeNull();
        result.SessionId.ShouldBe(sessionId);
    }

    [Test]
    public async Task LoadSessionAsync_NonExistingSession_ReturnsNull()
    {
        // Arrange
        _collection.FindOne(Arg.Any<Expression<Func<PersistedSession, bool>>>())
            .Returns((PersistedSession?)null);

        // Act
        var result = await _sut.LoadSessionAsync(Guid.NewGuid());

        // Assert
        result.ShouldBeNull();
    }

    [Test]
    public async Task LoadSessionAsync_DeserializationFails_ReturnsNull()
    {
        // Arrange
        var persisted = new PersistedSession
        {
            SessionId = Guid.NewGuid(),
            MessagesJson = "INVALID JSON{{{",
            OriginalInputsJson = "INVALID",
            ConfigurationJson = "INVALID",
            PresentedOptionsJson = "INVALID"
        };

        _collection.FindOne(Arg.Any<Expression<Func<PersistedSession, bool>>>())
            .Returns(persisted);

        // Act
        var result = await _sut.LoadSessionAsync(persisted.SessionId);

        // Assert
        result.ShouldBeNull();
    }

    [Test]
    public async Task DeleteSessionAsync_DeletesFromCollection()
    {
        // Arrange
        var sessionId = Guid.NewGuid();

        // Act
        await _sut.DeleteSessionAsync(sessionId);

        // Assert
        _collection.Received(1).DeleteMany(Arg.Any<Expression<Func<PersistedSession, bool>>>());
    }

    [Test]
    public async Task SessionExistsAsync_ExistingSession_ReturnsTrue()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _collection.Exists(Arg.Any<Expression<Func<PersistedSession, bool>>>()).Returns(true);

        // Act
        var result = await _sut.SessionExistsAsync(sessionId);

        // Assert
        result.ShouldBeTrue();
    }

    [Test]
    public async Task SessionExistsAsync_NonExistingSession_ReturnsFalse()
    {
        // Arrange
        _collection.Exists(Arg.Any<Expression<Func<PersistedSession, bool>>>()).Returns(false);

        // Act
        var result = await _sut.SessionExistsAsync(Guid.NewGuid());

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public async Task DeleteExpiredSessionsAsync_ReturnsDeletedCount()
    {
        // Arrange
        _collection.DeleteMany(Arg.Any<Expression<Func<PersistedSession, bool>>>()).Returns(3);

        // Act
        var result = await _sut.DeleteExpiredSessionsAsync(TimeSpan.FromHours(1));

        // Assert
        result.ShouldBe(3);
    }

    [Test]
    public async Task SaveStateHistoryAsync_ExistingSession_UpdatesStateHistory()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var persisted = CreateTestPersistedSession(sessionId);
        _collection.FindOne(Arg.Any<Expression<Func<PersistedSession, bool>>>())
            .Returns(persisted);
        var stateJson = """[{"state":"Processing","timestamp":"2024-01-01T00:00:00Z"}]""";

        // Act
        await _sut.SaveStateHistoryAsync(sessionId, stateJson);

        // Assert
        persisted.StateHistoryJson.ShouldBe(stateJson);
        _collection.Received(1).Update(persisted);
    }

    [Test]
    public async Task SaveStateHistoryAsync_NonExistingSession_DoesNothing()
    {
        // Arrange
        _collection.FindOne(Arg.Any<Expression<Func<PersistedSession, bool>>>())
            .Returns((PersistedSession?)null);

        // Act
        await _sut.SaveStateHistoryAsync(Guid.NewGuid(), "[]");

        // Assert
        _collection.DidNotReceive().Update(Arg.Any<PersistedSession>());
    }

    [Test]
    public async Task LoadStateHistoryAsync_ExistingSession_ReturnsStateHistory()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var persisted = CreateTestPersistedSession(sessionId);
        persisted.StateHistoryJson = """[{"state":"Completed"}]""";
        _collection.FindOne(Arg.Any<Expression<Func<PersistedSession, bool>>>())
            .Returns(persisted);

        // Act
        var result = await _sut.LoadStateHistoryAsync(sessionId);

        // Assert
        result.ShouldBe("""[{"state":"Completed"}]""");
    }

    [Test]
    public async Task LoadStateHistoryAsync_NonExistingSession_ReturnsEmptyArray()
    {
        // Arrange
        _collection.FindOne(Arg.Any<Expression<Func<PersistedSession, bool>>>())
            .Returns((PersistedSession?)null);

        // Act
        var result = await _sut.LoadStateHistoryAsync(Guid.NewGuid());

        // Assert
        result.ShouldBe("[]");
    }

    // Note: GetActiveSessionsAsync and GetActiveSessionsForOwnerAsync tests omitted
    // because LiteDB's fluent query API (Query().OrderByDescending().ToList()) is
    // not reliably mockable with NSubstitute. These methods are covered by
    // integration tests against real LiteDB instances.

    [Test]
    public async Task SaveSessionAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        var session = CreateTestSession();
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(
            () => _sut.SaveSessionAsync(session, Guid.Empty, cts.Token));
    }
}
