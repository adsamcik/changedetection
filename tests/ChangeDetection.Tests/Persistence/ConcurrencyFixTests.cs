using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Persistence;
using ChangeDetection.Services.Pipeline;
using LiteDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Persistence;

/// <summary>
/// Regression tests for D1: ThreadSafeLiteDbContext concurrent access.
/// Verifies that concurrent Insert+Read+Update from multiple threads
/// does not cause corruption or LiteException.
/// </summary>
public class ThreadSafeLiteDbContextTests
{
    [Test]
    public async Task ConcurrentWritesAndReads_DoNotCorrupt()
    {
        // Arrange
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_threadsafe_{Guid.NewGuid()}.db");
        try
        {
            using var context = new LiteDbContext(dbPath);
            using var safeContext = new ThreadSafeLiteDbContext(context);
            const string collectionName = "test_items";
            const int taskCount = 10;
            const int operationsPerTask = 20;

            // Act: 10 concurrent tasks each doing Insert+Read+Update in a loop
            var tasks = Enumerable.Range(0, taskCount).Select(taskIndex => Task.Run(() =>
            {
                for (int i = 0; i < operationsPerTask; i++)
                {
                    var site = new WatchedSite
                    {
                        Url = $"https://task{taskIndex}-item{i}.com",
                        Name = $"Task{taskIndex}_Item{i}"
                    };

                    // Insert
                    safeContext.Execute(db =>
                    {
                        db.GetCollection<WatchedSite>(collectionName).Insert(site);
                    });

                    // Read back
                    var found = safeContext.Execute(db =>
                        db.GetCollection<WatchedSite>(collectionName).FindById(site.Id));
                    found.ShouldNotBeNull($"Failed to read back site {site.Id} from task {taskIndex}");

                    // Update
                    found.Name = $"Updated_{found.Name}";
                    safeContext.Execute(db =>
                        db.GetCollection<WatchedSite>(collectionName).Update(found));
                }
            })).ToArray();

            await Task.WhenAll(tasks);

            // Assert: all items present and updated
            var totalCount = safeContext.Execute(db =>
                db.GetCollection<WatchedSite>(collectionName).Count());
            totalCount.ShouldBe(taskCount * operationsPerTask);

            var allItems = safeContext.Execute(db =>
                db.GetCollection<WatchedSite>(collectionName).FindAll().ToList());
            allItems.ShouldAllBe(item => item.Name!.StartsWith("Updated_"));
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Test]
    public async Task ExecuteAsync_WithCancellation_ThrowsOperationCanceled()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_threadsafe_cancel_{Guid.NewGuid()}.db");
        try
        {
            using var context = new LiteDbContext(dbPath);
            using var safeContext = new ThreadSafeLiteDbContext(context);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Should.ThrowAsync<OperationCanceledException>(async () =>
                await safeContext.ExecuteAsync(db => 1, cts.Token));
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    private static void CleanupDb(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        try { if (File.Exists(path + "-log")) File.Delete(path + "-log"); } catch { }
    }
}

/// <summary>
/// Regression tests for D2: PipelineQueueRepository atomic claim.
/// Two concurrent ClaimNextAsync calls with one item → exactly one succeeds.
/// </summary>
public class PipelineQueueClaimRaceTests
{
    private static FakeLogCollector CreateCollector() => FakeLogCollector.Create(new FakeLogCollectorOptions());

    [Test]
    public async Task ClaimNextAsync_TwoConcurrentCallers_OnlyOneSucceeds()
    {
        // Arrange
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_claim_{Guid.NewGuid()}.db");
        try
        {
            using var context = new LiteDbContext(dbPath);
            using var safeContext = new ThreadSafeLiteDbContext(context);
            var logger = new FakeLogger<PipelineQueueRepository>(CreateCollector());
            var repo = new PipelineQueueRepository(safeContext, logger);

            var item = new PipelineQueueItem
            {
                Id = Guid.NewGuid(),
                SessionId = Guid.NewGuid(),
                OwnerId = Guid.NewGuid(),
                OperationType = PipelineOperationType.Process,
                Status = PipelineQueueStatus.Pending,
                EnqueuedAt = DateTimeOffset.UtcNow,
                Priority = 0
            };
            await repo.EnqueueAsync(item);

            // Act: two concurrent claims
            var barrier = new Barrier(2);
            var claim1Task = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await repo.ClaimNextAsync();
            });
            var claim2Task = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await repo.ClaimNextAsync();
            });

            var results = await Task.WhenAll(claim1Task, claim2Task);

            // Assert: exactly one got the item, the other got null
            var successCount = results.Count(r => r != null);
            successCount.ShouldBe(1, "Exactly one caller should claim the item");

            var nullCount = results.Count(r => r == null);
            nullCount.ShouldBe(1, "The other caller should get null");

            // Verify the item is in Processing state
            var fetched = await repo.GetByIdAsync(item.Id);
            fetched.ShouldNotBeNull();
            fetched.Status.ShouldBe(PipelineQueueStatus.Processing);
            fetched.Attempts.ShouldBe(1);
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Test]
    public async Task ClaimNextAsync_MultipleConcurrentCallers_NoDoubleProcessing()
    {
        // Arrange
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_claim_multi_{Guid.NewGuid()}.db");
        try
        {
            using var context = new LiteDbContext(dbPath);
            using var safeContext = new ThreadSafeLiteDbContext(context);
            var logger = new FakeLogger<PipelineQueueRepository>(CreateCollector());
            var repo = new PipelineQueueRepository(safeContext, logger);

            // Enqueue 5 items
            for (int i = 0; i < 5; i++)
            {
                await repo.EnqueueAsync(new PipelineQueueItem
                {
                    Id = Guid.NewGuid(),
                    SessionId = Guid.NewGuid(),
                    OwnerId = Guid.NewGuid(),
                    OperationType = PipelineOperationType.Process,
                    Status = PipelineQueueStatus.Pending,
                    EnqueuedAt = DateTimeOffset.UtcNow.AddSeconds(i),
                    Priority = 0
                });
            }

            // Act: 10 concurrent claims for 5 items
            var barrier = new Barrier(10);
            var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await repo.ClaimNextAsync();
            })).ToArray();

            var results = await Task.WhenAll(tasks);

            // Assert: exactly 5 successes, 5 nulls, all unique items
            var claimed = results.Where(r => r != null).ToList();
            claimed.Count.ShouldBe(5);
            claimed.Select(c => c!.Id).Distinct().Count().ShouldBe(5, "Each item should be claimed exactly once");
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    private static void CleanupDb(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        try { if (File.Exists(path + "-log")) File.Delete(path + "-log"); } catch { }
    }
}

/// <summary>
/// Regression tests for D3: SessionPersistenceService atomic upsert.
/// Two concurrent SaveSessionAsync calls with same session ID → no duplicate, no exception.
/// </summary>
public class SessionPersistenceAtomicUpsertTests
{
    [Test]
    public async Task SaveSessionAsync_ConcurrentSameSessionId_NoDuplicate()
    {
        // Arrange
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_session_{Guid.NewGuid()}.db");
        try
        {
            using var context = new LiteDbContext(dbPath);
            using var safeContext = new ThreadSafeLiteDbContext(context);
            var service = new SessionPersistenceService(safeContext);

            var sessionId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();

            // Act: two concurrent saves for the same session
            var barrier = new Barrier(2);
            var save1 = Task.Run(async () =>
            {
                var session = new ConversationSession { SessionId = sessionId };
                session.Touch();
                barrier.SignalAndWait();
                await service.SaveSessionAsync(session, ownerId);
            });
            var save2 = Task.Run(async () =>
            {
                var session = new ConversationSession { SessionId = sessionId };
                session.Touch();
                barrier.SignalAndWait();
                await service.SaveSessionAsync(session, ownerId);
            });

            // Should not throw
            await Task.WhenAll(save1, save2);

            // Assert: exactly one record exists
            var sessions = await service.GetActiveSessionsAsync();
            var matching = sessions.Where(s => s.SessionId == sessionId).ToList();
            matching.Count.ShouldBe(1, "Should have exactly one session record, not a duplicate");
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Test]
    public async Task SaveSessionAsync_UpdateExisting_PreservesData()
    {
        // Arrange
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_session_update_{Guid.NewGuid()}.db");
        try
        {
            using var context = new LiteDbContext(dbPath);
            using var safeContext = new ThreadSafeLiteDbContext(context);
            var service = new SessionPersistenceService(safeContext);

            var sessionId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();

            // First save
            var session1 = new ConversationSession { SessionId = sessionId };
            session1.DisplayName = "First";
            await service.SaveSessionAsync(session1, ownerId);

            // Second save (update)
            var session2 = new ConversationSession { SessionId = sessionId };
            session2.DisplayName = "Second";
            await service.SaveSessionAsync(session2, ownerId);

            // Assert: only one record, with updated data
            var loaded = await service.LoadSessionAsync(sessionId);
            loaded.ShouldNotBeNull();
            loaded.DisplayName.ShouldBe("Second");

            var all = await service.GetActiveSessionsAsync();
            all.Count(s => s.SessionId == sessionId).ShouldBe(1);
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    private static void CleanupDb(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        try { if (File.Exists(path + "-log")) File.Delete(path + "-log"); } catch { }
    }
}

/// <summary>
/// Regression tests for D4: ConversationSessionManager FlushAsync and error resilience.
/// </summary>
public class ConversationSessionManagerFlushTests
{
    [Test]
    public async Task FlushAsync_PersistsPendingChanges()
    {
        // Arrange
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_flush_{Guid.NewGuid()}.db");
        try
        {
            using var context = new LiteDbContext(dbPath);
            using var safeContext = new ThreadSafeLiteDbContext(context);
            var persistence = new SessionPersistenceService(safeContext);

            var services = new ServiceCollection();
            services.AddSingleton<ISessionPersistenceService>(persistence);
            services.AddLogging();
            var sp = services.BuildServiceProvider();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            var logger = new FakeLogger<ConversationSessionManager>(FakeLogCollector.Create(new FakeLogCollectorOptions()));

            using var manager = new ConversationSessionManager(scopeFactory, logger);

            // Act: create a session and update it (triggers async persistence)
            var session = manager.CreateSession();
            session.DisplayName = "FlushTest";
            var ownerId = Guid.NewGuid();
            manager.UpdateSession(session, ownerId);

            // Flush: should process the pending save
            await manager.FlushAsync();

            // Assert: session is persisted
            var loaded = await persistence.LoadSessionAsync(session.SessionId);
            loaded.ShouldNotBeNull("Session should be persisted after FlushAsync");
            loaded.DisplayName.ShouldBe("FlushTest");
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Test]
    public async Task UpdateSession_ExceptionInPersistence_DoesNotCrashManager()
    {
        // Arrange: use a scope factory that returns a persistence service that always throws
        var services = new ServiceCollection();
        services.AddSingleton<ISessionPersistenceService>(new ThrowingPersistenceService());
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var logger = new FakeLogger<ConversationSessionManager>(FakeLogCollector.Create(new FakeLogCollectorOptions()));

        using var manager = new ConversationSessionManager(scopeFactory, logger);

        // Act: should not throw
        var session = manager.CreateSession();
        session.DisplayName = "ExceptionTest";
        manager.UpdateSession(session, Guid.NewGuid());

        // Give the background loop time to process
        await Task.Delay(200);

        // Assert: manager is still functional
        var retrieved = manager.GetSession(session.SessionId);
        retrieved.ShouldNotBeNull("Manager should still serve sessions after persistence failure");

        // Flush should complete without throwing
        await manager.FlushAsync();
    }

    /// <summary>
    /// A persistence service that always throws, to test error resilience.
    /// </summary>
    private class ThrowingPersistenceService : ISessionPersistenceService
    {
        public Task SaveSessionAsync(ConversationSession session, Guid ownerId, CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated persistence failure");
        public ConversationSession? LoadSession(Guid sessionId) => null;
        public Task<ConversationSession?> LoadSessionAsync(Guid sessionId, CancellationToken ct = default)
            => Task.FromResult<ConversationSession?>(null);
        public void DeleteSession(Guid sessionId) { }
        public Task DeleteSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<PersistedSession>> GetActiveSessionsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PersistedSession>>(Array.Empty<PersistedSession>());
        public Task<IReadOnlyList<PersistedSession>> GetActiveSessionsForOwnerAsync(Guid ownerId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PersistedSession>>(Array.Empty<PersistedSession>());
        public Task<int> DeleteExpiredSessionsAsync(TimeSpan maxAge, CancellationToken ct = default)
            => Task.FromResult(0);
        public Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task SaveStateHistoryAsync(Guid sessionId, string stateHistoryJson, CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated persistence failure");
        public Task<string> LoadStateHistoryAsync(Guid sessionId, CancellationToken ct = default)
            => Task.FromResult("[]");
    }

    private static void CleanupDb(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        try { if (File.Exists(path + "-log")) File.Delete(path + "-log"); } catch { }
    }
}
