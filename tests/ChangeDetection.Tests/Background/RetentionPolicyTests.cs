using System.Linq.Expressions;
using System.Reflection;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Authentication;
using ChangeDetection.Services.Background;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Background;

[Category("Unit")]
public class RetentionPolicyTests
{
    [Test]
    public async Task GetEffectiveRetentionDays_NullPerWatch_UsesGlobalDefault()
    {
        SnapshotCleanupService.GetEffectiveRetentionDays(null, 30, 180).ShouldBe(30);
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetEffectiveRetentionDays_PerWatchOverride_UsesOverride()
    {
        SnapshotCleanupService.GetEffectiveRetentionDays(60, 30, 180).ShouldBe(60);
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetEffectiveRetentionDays_ExceedsCeiling_ClampsToCeiling()
    {
        SnapshotCleanupService.GetEffectiveRetentionDays(365, 30, 180).ShouldBe(180);
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetEffectiveRetentionDays_ZeroCeiling_NoClamping()
    {
        SnapshotCleanupService.GetEffectiveRetentionDays(365, 30, 0).ShouldBe(365);
        await Task.CompletedTask;
    }

    [Test]
    public async Task CleanupOldChangeEvents_DeletesExpiredEvents()
    {
        var settingsRepo = Substitute.For<IRepository<AppSettings>>();
        var snapshotRepo = Substitute.For<IRepository<ChangeSnapshot>>();
        var eventRepo = Substitute.For<IRepository<ChangeEvent>>();
        var logger = Substitute.For<ILogger<SnapshotCleanupService>>();

        var scopeFactory = Substitute.For<IBackgroundServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IRepository<AppSettings>)).Returns(settingsRepo);
        provider.GetService(typeof(IRepository<ChangeSnapshot>)).Returns(snapshotRepo);
        provider.GetService(typeof(IRepository<ChangeEvent>)).Returns(eventRepo);
        scope.ServiceProvider.Returns(provider);
        scopeFactory.CreateBackgroundScope().Returns(scope);

        settingsRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([new AppSettings { ChangeEventRetentionDays = 30 }]);

        var oldEvents = new List<ChangeEvent>
        {
            new() { Id = Guid.NewGuid(), WatchedSiteId = Guid.NewGuid(), DetectedAt = DateTime.UtcNow.AddDays(-45) },
            new() { Id = Guid.NewGuid(), WatchedSiteId = Guid.NewGuid(), DetectedAt = DateTime.UtcNow.AddDays(-60) }
        };

        eventRepo.FindAsync(Arg.Any<Expression<Func<ChangeEvent, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(oldEvents);

        var sut = new SnapshotCleanupService(scopeFactory, logger);
        var method = typeof(SnapshotCleanupService)
            .GetMethod("CleanupOldChangeEventsAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        await (Task)method!.Invoke(sut, [CancellationToken.None])!;

        await eventRepo.Received(1).DeleteManyAsync(
            Arg.Any<Expression<Func<ChangeEvent, bool>>>(),
            Arg.Any<CancellationToken>());
    }
}
