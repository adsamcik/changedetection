using System.Diagnostics;
using System.Linq.Expressions;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Authentication;
using ChangeDetection.Services.Background;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Performance;

[Category("Unit")]
public class CleanupPerformanceTests
{
    [Test]
    public async Task SnapshotCleanup_With1000Snapshots_CompletesUnder500ms()
    {
        var settingsRepo = Substitute.For<IRepository<AppSettings>>();
        var snapshotRepo = Substitute.For<IRepository<ChangeSnapshot>>();
        var watchRepo = Substitute.For<IRepository<WatchedSite>>();
        var eventRepo = Substitute.For<IRepository<ChangeEvent>>();
        var logger = Substitute.For<ILogger<SnapshotCleanupService>>();

        var scopeFactory = Substitute.For<IBackgroundServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IRepository<AppSettings>)).Returns(settingsRepo);
        provider.GetService(typeof(IRepository<ChangeSnapshot>)).Returns(snapshotRepo);
        provider.GetService(typeof(IRepository<WatchedSite>)).Returns(watchRepo);
        provider.GetService(typeof(IRepository<ChangeEvent>)).Returns(eventRepo);
        scope.ServiceProvider.Returns(provider);
        scopeFactory.CreateBackgroundScope().Returns(scope);

        settingsRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([new AppSettings { SnapshotRetentionDays = 30, MaxRetentionDays = 180 }]);

        var watches = Enumerable.Range(0, 10).Select(_ => new WatchedSite
        {
            Id = Guid.NewGuid(),
            Url = "https://example.com"
        }).ToList();
        watchRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(watches);

        var snapshots = watches.SelectMany(w =>
            Enumerable.Range(0, 100).Select(i => new ChangeSnapshot
            {
                Id = Guid.NewGuid(),
                WatchedSiteId = w.Id,
                ContentHash = $"hash-{i}",
                Content = $"content-{i}",
                CapturedAt = DateTime.UtcNow.AddDays(-31 - i)
            })).ToList();

        snapshotRepo.FindAsync(
                Arg.Any<Expression<Func<ChangeSnapshot, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(snapshots);

        var sut = new SnapshotCleanupService(scopeFactory, logger);

        var sw = Stopwatch.StartNew();
        var method = typeof(SnapshotCleanupService)
            .GetMethod("CleanupOldSnapshotsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(sut, [CancellationToken.None])!;
        sw.Stop();

        sw.ElapsedMilliseconds.ShouldBeLessThan(500, $"Cleanup of 1000 snapshots took {sw.ElapsedMilliseconds}ms");
    }

    [Test]
    public async Task RetentionDaysCalculation_IsCorrect()
    {
        SnapshotCleanupService.GetEffectiveRetentionDays(null, 30, 180).ShouldBe(30);
        SnapshotCleanupService.GetEffectiveRetentionDays(60, 30, 180).ShouldBe(60);
        SnapshotCleanupService.GetEffectiveRetentionDays(365, 30, 180).ShouldBe(180);
        SnapshotCleanupService.GetEffectiveRetentionDays(null, 30, 0).ShouldBe(30);
        await Task.CompletedTask;
    }
}
