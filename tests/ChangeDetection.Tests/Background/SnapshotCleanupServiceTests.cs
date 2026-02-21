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
public class SnapshotCleanupServiceTests
{
    private readonly IRepository<AppSettings> _settingsRepo;
    private readonly IRepository<ChangeSnapshot> _snapshotRepo;
    private readonly ILogger<SnapshotCleanupService> _logger;
    private readonly SnapshotCleanupService _sut;

    public SnapshotCleanupServiceTests()
    {
        _settingsRepo = Substitute.For<IRepository<AppSettings>>();
        _snapshotRepo = Substitute.For<IRepository<ChangeSnapshot>>();
        _logger = Substitute.For<ILogger<SnapshotCleanupService>>();

        var scopeFactory = Substitute.For<IBackgroundServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IRepository<AppSettings>)).Returns(_settingsRepo);
        provider.GetService(typeof(IRepository<ChangeSnapshot>)).Returns(_snapshotRepo);
        scope.ServiceProvider.Returns(provider);
        scopeFactory.CreateBackgroundScope().Returns(scope);

        _sut = new SnapshotCleanupService(scopeFactory, _logger);
    }

    private Task InvokeCleanupOldSnapshotsAsync(CancellationToken ct = default)
    {
        var method = typeof(SnapshotCleanupService)
            .GetMethod("CleanupOldSnapshotsAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        return (Task)method!.Invoke(_sut, [ct])!;
    }

    [Test]
    public async Task ExecuteAsync_OldSnapshots_DeletesThem()
    {
        _settingsRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([new AppSettings { SnapshotRetentionDays = 7 }]);

        var oldSnapshot = new ChangeSnapshot
        {
            Id = Guid.NewGuid(),
            WatchedSiteId = Guid.NewGuid(),
            ContentHash = "abc123",
            Content = "old content",
            CapturedAt = DateTime.UtcNow.AddDays(-10)
        };

        _snapshotRepo.FindAsync(
                Arg.Any<Expression<Func<ChangeSnapshot, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns([oldSnapshot]);

        await InvokeCleanupOldSnapshotsAsync();

        await _snapshotRepo.Received(1).DeleteManyAsync(
            Arg.Any<Expression<Func<ChangeSnapshot, bool>>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_RetentionDisabled_Skips()
    {
        _settingsRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([new AppSettings { SnapshotRetentionDays = 0 }]);

        await InvokeCleanupOldSnapshotsAsync();

        await _snapshotRepo.DidNotReceive().FindAsync(
            Arg.Any<Expression<Func<ChangeSnapshot, bool>>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_WithScreenshots_CleansUpFiles()
    {
        var tempFile1 = Path.GetTempFileName();
        var tempFile2 = Path.GetTempFileName();

        try
        {
            _settingsRepo.GetAllAsync(Arg.Any<CancellationToken>())
                .Returns([new AppSettings { SnapshotRetentionDays = 7 }]);

            var snapshot = new ChangeSnapshot
            {
                Id = Guid.NewGuid(),
                WatchedSiteId = Guid.NewGuid(),
                ContentHash = "abc123",
                Content = "old content",
                CapturedAt = DateTime.UtcNow.AddDays(-10),
                ScreenshotPath = tempFile1,
                ElementScreenshotPath = tempFile2
            };

            _snapshotRepo.FindAsync(
                    Arg.Any<Expression<Func<ChangeSnapshot, bool>>>(),
                    Arg.Any<CancellationToken>())
                .Returns([snapshot]);

            await InvokeCleanupOldSnapshotsAsync();

            File.Exists(tempFile1).ShouldBeFalse("Screenshot file should have been deleted");
            File.Exists(tempFile2).ShouldBeFalse("Element screenshot file should have been deleted");
        }
        finally
        {
            if (File.Exists(tempFile1)) File.Delete(tempFile1);
            if (File.Exists(tempFile2)) File.Delete(tempFile2);
        }
    }

    [Test]
    public async Task ExecuteAsync_FileDeletionFails_ContinuesCleanup()
    {
        var tempFile = Path.GetTempFileName();
        FileStream? lockStream = null;

        try
        {
            // Lock the file so File.Delete throws IOException
            lockStream = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.None);

            _settingsRepo.GetAllAsync(Arg.Any<CancellationToken>())
                .Returns([new AppSettings { SnapshotRetentionDays = 7 }]);

            var snapshot = new ChangeSnapshot
            {
                Id = Guid.NewGuid(),
                WatchedSiteId = Guid.NewGuid(),
                ContentHash = "abc123",
                Content = "old content",
                CapturedAt = DateTime.UtcNow.AddDays(-10),
                ScreenshotPath = tempFile
            };

            _snapshotRepo.FindAsync(
                    Arg.Any<Expression<Func<ChangeSnapshot, bool>>>(),
                    Arg.Any<CancellationToken>())
                .Returns([snapshot]);

            // Should not throw despite file deletion failure
            await InvokeCleanupOldSnapshotsAsync();

            // DeleteManyAsync should still be called even though file deletion failed
            await _snapshotRepo.Received(1).DeleteManyAsync(
                Arg.Any<Expression<Func<ChangeSnapshot, bool>>>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            lockStream?.Dispose();
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
