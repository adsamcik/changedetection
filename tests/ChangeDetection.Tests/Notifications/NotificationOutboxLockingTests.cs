using ChangeDetection.Core.Entities;
using ChangeDetection.Services.Persistence;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Notifications;

[Category("Unit")]
public class NotificationOutboxLockingTests : TestBase
{
    private static (string dbPath, LiteDbContext context, NotificationOutboxRepository repo, Action cleanup) CreateTempRepo()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_outbox_{Guid.NewGuid()}.db");
        var context = new LiteDbContext(dbPath);
        var repo = new NotificationOutboxRepository(new ThreadSafeLiteDbContext(context));
        return (dbPath, context, repo, () =>
        {
            try { context.Dispose(); } catch { }
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        });
    }

    [Test]
    public async Task TryClaimForProcessingAsync_ConcurrentCalls_OnlyOneSucceeds()
    {
        var (_, _, repo, cleanup) = CreateTempRepo();
        try
        {
            // Arrange - insert a pending notification
            var entry = new NotificationOutboxEntry
            {
                Id = Guid.NewGuid(),
                NotificationType = NotificationType.Email,
                Destination = "test@example.com",
                PayloadJson = "{}",
                Status = NotificationStatus.Pending
            };
            await repo.AddAsync(entry);

            // Act - launch 10 concurrent claim attempts
            const int concurrency = 10;
            var tasks = Enumerable.Range(0, concurrency)
                .Select(_ => Task.Run(() => repo.TryClaimForProcessingAsync(entry.Id)))
                .ToArray();

            var results = await Task.WhenAll(tasks);

            // Assert - exactly one should succeed
            var successCount = results.Count(r => r);
            var failCount = results.Count(r => !r);

            Log("Success: {0}, Fail: {1}", successCount, failCount);

            successCount.ShouldBe(1);
            failCount.ShouldBe(concurrency - 1);

            // Verify the entry is now in Processing state
            var claimed = await repo.GetByIdAsync(entry.Id);
            claimed.ShouldNotBeNull();
            claimed!.Status.ShouldBe(NotificationStatus.Processing);
            claimed.ProcessingStartedAt.ShouldNotBeNull();
        }
        finally
        {
            cleanup();
        }
    }
}
