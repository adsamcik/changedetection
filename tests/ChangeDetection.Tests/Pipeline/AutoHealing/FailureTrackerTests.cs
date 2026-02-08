using ChangeDetection.Services.AutoHealing;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.AutoHealing;

[Category("Unit")]
public class FailureTrackerTests
{
    private readonly FailureTracker _sut = new();

    [Test]
    public async Task RecordFailure_IncrementsCount()
    {
        var watchId = Guid.NewGuid();
        var blockId = "filter-1";

        var count1 = await _sut.RecordFailureAsync(watchId, blockId, "Error 1");
        var count2 = await _sut.RecordFailureAsync(watchId, blockId, "Error 2");
        var count3 = await _sut.RecordFailureAsync(watchId, blockId, "Error 3");

        count1.ShouldBe(1);
        count2.ShouldBe(2);
        count3.ShouldBe(3);
    }

    [Test]
    public async Task ResetFailures_ClearsCount()
    {
        var watchId = Guid.NewGuid();
        var blockId = "filter-1";

        await _sut.RecordFailureAsync(watchId, blockId, "Error");
        await _sut.RecordFailureAsync(watchId, blockId, "Error");
        await _sut.ResetFailuresAsync(watchId, blockId);

        var count = await _sut.GetConsecutiveFailuresAsync(watchId, blockId);
        count.ShouldBe(0);
    }

    [Test]
    public async Task GetConsecutiveFailures_ReturnsZeroWhenNone()
    {
        var count = await _sut.GetConsecutiveFailuresAsync(Guid.NewGuid(), "nonexistent-block");
        count.ShouldBe(0);
    }

    [Test]
    public async Task RecordFailure_TracksBlocksIndependently()
    {
        var watchId = Guid.NewGuid();

        await _sut.RecordFailureAsync(watchId, "block-a", "Error");
        await _sut.RecordFailureAsync(watchId, "block-a", "Error");
        await _sut.RecordFailureAsync(watchId, "block-b", "Error");

        var countA = await _sut.GetConsecutiveFailuresAsync(watchId, "block-a");
        var countB = await _sut.GetConsecutiveFailuresAsync(watchId, "block-b");

        countA.ShouldBe(2);
        countB.ShouldBe(1);
    }

    [Test]
    public async Task RecordFailure_TracksWatchesIndependently()
    {
        var watch1 = Guid.NewGuid();
        var watch2 = Guid.NewGuid();
        var blockId = "filter-1";

        await _sut.RecordFailureAsync(watch1, blockId, "Error");
        await _sut.RecordFailureAsync(watch1, blockId, "Error");
        await _sut.RecordFailureAsync(watch2, blockId, "Error");

        var count1 = await _sut.GetConsecutiveFailuresAsync(watch1, blockId);
        var count2 = await _sut.GetConsecutiveFailuresAsync(watch2, blockId);

        count1.ShouldBe(2);
        count2.ShouldBe(1);
    }

    [Test]
    public async Task ResetFailures_DoesNotAffectOtherBlocks()
    {
        var watchId = Guid.NewGuid();

        await _sut.RecordFailureAsync(watchId, "block-a", "Error");
        await _sut.RecordFailureAsync(watchId, "block-b", "Error");
        await _sut.ResetFailuresAsync(watchId, "block-a");

        var countA = await _sut.GetConsecutiveFailuresAsync(watchId, "block-a");
        var countB = await _sut.GetConsecutiveFailuresAsync(watchId, "block-b");

        countA.ShouldBe(0);
        countB.ShouldBe(1);
    }
}
