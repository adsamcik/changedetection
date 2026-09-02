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
