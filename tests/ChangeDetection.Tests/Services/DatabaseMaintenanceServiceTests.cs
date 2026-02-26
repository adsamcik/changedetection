using ChangeDetection.Services.Background;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services;

[Category("Unit")]
public class DatabaseMaintenanceServiceTests
{
    [Test]
    public async Task GetFileSizeBytes_NonExistentFile_ReturnsZero()
    {
        var result = DatabaseMaintenanceService.GetFileSizeBytes("nonexistent_file_xyz.db");
        result.ShouldBe(0);
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetFileSizeBytes_ExistingFile_ReturnsSize()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "test content for size check");
            var result = DatabaseMaintenanceService.GetFileSizeBytes(tempFile);
            result.ShouldBeGreaterThan(0);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task DatabaseHealthInfo_RecordProperties_AreCorrect()
    {
        var collections = new List<CollectionStat>
        {
            new("watches", 50),
            new("snapshots", 2400),
            new("events", 240)
        };

        var info = new DatabaseHealthInfo(
            FilePath: "test.db",
            SizeBytes: 314572800, // 300 MB
            SizeMb: 300.0,
            WarningThresholdMb: 500,
            IsOverWarningThreshold: false,
            CompactionIntervalDays: 7,
            Collections: collections);

        info.SizeMb.ShouldBe(300.0);
        info.IsOverWarningThreshold.ShouldBeFalse();
        info.Collections.Count.ShouldBe(3);
        info.Collections[0].DocumentCount.ShouldBe(50);
        await Task.CompletedTask;
    }

    [Test]
    public async Task DatabaseHealthInfo_OverThreshold_IsTrue()
    {
        var info = new DatabaseHealthInfo(
            FilePath: "test.db",
            SizeBytes: 629145600, // 600 MB
            SizeMb: 600.0,
            WarningThresholdMb: 500,
            IsOverWarningThreshold: true,
            CompactionIntervalDays: 7,
            Collections: []);

        info.IsOverWarningThreshold.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task CollectionStat_Record_Works()
    {
        var stat = new CollectionStat("watches", 42);
        stat.Name.ShouldBe("watches");
        stat.DocumentCount.ShouldBe(42);
        await Task.CompletedTask;
    }
}
