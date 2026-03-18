using System.Text.Json;
using ChangeDetection.Services.BlockExecution;
using ChangeDetection.Services.Persistence;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks;

[Category("Unit")]
public class BlockStateStoreTests : TestBase
{
    private string _tempDbPath = null!;
    private LiteDbContext _context = null!;
    private LiteDbBlockStateStore _store = null!;

    [Before(Test)]
    public Task SetUp()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"blockstate_test_{Guid.NewGuid():N}.db");
        _context = new LiteDbContext($"Filename={_tempDbPath};Connection=shared");
        _store = new LiteDbBlockStateStore(_context, CreateLogger<LiteDbBlockStateStore>());
        return Task.CompletedTask;
    }

    [After(Test)]
    public Task CleanUp()
    {
        _context.Dispose();
        if (File.Exists(_tempDbPath))
            File.Delete(_tempDbPath);
        return Task.CompletedTask;
    }

    private static JsonElement CreateJsonElement(object value)
    {
        var json = JsonSerializer.Serialize(value);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Test]
    public async Task SaveAndRetrieve_SingleSnapshot_RoundTrips()
    {
        var watchId = "watch-1";
        var blockId = "block-1";
        var output = CreateJsonElement(new { price = 29.99, currency = "USD" });

        await _store.SaveOutputAsync(watchId, blockId, output);
        var result = await _store.GetPreviousOutputAsync(watchId, blockId);

        result.ShouldNotBeNull();
        result.Value.GetProperty("price").GetDouble().ShouldBe(29.99);
        result.Value.GetProperty("currency").GetString().ShouldBe("USD");
    }

    [Test]
    public async Task GetCachedOutput_MatchingHashes_ReturnsMostRecentMatch()
    {
        var watchId = "watch-1";
        var blockId = "block-1";
        var cachedOutput = CreateJsonElement(new { price = 29.99 });

        await _store.SaveOutputAsync(watchId, blockId, cachedOutput, "input-a", "pipeline-a");
        await Task.Delay(200);
        await _store.SaveOutputAsync(watchId, blockId, CreateJsonElement(new { price = 99.99 }), "input-b", "pipeline-a");

        var result = await _store.GetCachedOutputAsync(watchId, blockId, "input-a", "pipeline-a");

        result.ShouldNotBeNull();
        result.Value.GetProperty("price").GetDouble().ShouldBe(29.99);
    }

    [Test]
    public async Task GetCachedOutput_NonMatchingHashes_ReturnsNull()
    {
        var watchId = "watch-1";
        var blockId = "block-1";

        await _store.SaveOutputAsync(watchId, blockId, CreateJsonElement(new { price = 29.99 }), "input-a", "pipeline-a");

        var result = await _store.GetCachedOutputAsync(watchId, blockId, "input-a", "pipeline-b");

        result.ShouldBeNull();
    }

    [Test]
    public async Task GetPreviousOutput_NoData_ReturnsNull()
    {
        var result = await _store.GetPreviousOutputAsync("nonexistent-watch", "nonexistent-block");

        result.ShouldBeNull();
    }

    [Test]
    public async Task GetPreviousOutput_MultipleSnapshots_ReturnsMostRecent()
    {
        var watchId = "watch-1";
        var blockId = "block-1";

        // Delays required: GetPreviousOutputAsync orders by DateTime.UtcNow-based Timestamp,
        // so each save must have a distinct timestamp. 200ms ensures timer resolution safety.
        await _store.SaveOutputAsync(watchId, blockId, CreateJsonElement(new { value = 1 }));
        await Task.Delay(200);
        await _store.SaveOutputAsync(watchId, blockId, CreateJsonElement(new { value = 2 }));
        await Task.Delay(200);
        await _store.SaveOutputAsync(watchId, blockId, CreateJsonElement(new { value = 3 }));

        var result = await _store.GetPreviousOutputAsync(watchId, blockId);

        result.ShouldNotBeNull();
        result.Value.GetProperty("value").GetInt32().ShouldBe(3);
    }

    [Test]
    public async Task GetHistory_ReturnsInReverseChronological()
    {
        var watchId = "watch-1";
        var blockId = "block-1";

        // Delays required: GetHistoryAsync orders by DateTime.UtcNow-based Timestamp,
        // so each save must have a distinct timestamp. 200ms ensures timer resolution safety.
        for (var i = 1; i <= 5; i++)
        {
            await _store.SaveOutputAsync(watchId, blockId, CreateJsonElement(new { value = i }));
            if (i < 5) await Task.Delay(200);
        }

        var history = await _store.GetHistoryAsync(watchId, blockId);

        history.Count.ShouldBe(5);
        history[0].Output.GetProperty("value").GetInt32().ShouldBe(5);
        history[1].Output.GetProperty("value").GetInt32().ShouldBe(4);
        history[2].Output.GetProperty("value").GetInt32().ShouldBe(3);
        history[3].Output.GetProperty("value").GetInt32().ShouldBe(2);
        history[4].Output.GetProperty("value").GetInt32().ShouldBe(1);
    }

    [Test]
    public async Task GetHistory_LimitsResults()
    {
        var watchId = "watch-1";
        var blockId = "block-1";

        // Delays required: GetHistoryAsync orders by DateTime.UtcNow-based Timestamp,
        // so each save must have a distinct timestamp. 200ms ensures timer resolution safety.
        for (var i = 1; i <= 20; i++)
        {
            await _store.SaveOutputAsync(watchId, blockId, CreateJsonElement(new { value = i }));
            if (i < 20) await Task.Delay(200);
        }

        var history = await _store.GetHistoryAsync(watchId, blockId, maxResults: 5);

        history.Count.ShouldBe(5);
        // Most recent 5 should be values 20, 19, 18, 17, 16
        history[0].Output.GetProperty("value").GetInt32().ShouldBe(20);
        history[4].Output.GetProperty("value").GetInt32().ShouldBe(16);
    }

    [Test]
    public async Task SaveOutput_DifferentBlocks_IsolatedByBlockId()
    {
        var watchId = "watch-1";

        await _store.SaveOutputAsync(watchId, "block-a", CreateJsonElement(new { source = "a" }));
        await _store.SaveOutputAsync(watchId, "block-b", CreateJsonElement(new { source = "b" }));

        var resultA = await _store.GetPreviousOutputAsync(watchId, "block-a");
        var resultB = await _store.GetPreviousOutputAsync(watchId, "block-b");

        resultA.ShouldNotBeNull();
        resultA.Value.GetProperty("source").GetString().ShouldBe("a");

        resultB.ShouldNotBeNull();
        resultB.Value.GetProperty("source").GetString().ShouldBe("b");

        var historyA = await _store.GetHistoryAsync(watchId, "block-a");
        var historyB = await _store.GetHistoryAsync(watchId, "block-b");
        historyA.Count.ShouldBe(1);
        historyB.Count.ShouldBe(1);
    }

    [Test]
    public async Task SaveOutput_DifferentWatches_IsolatedByWatchId()
    {
        var blockId = "block-1";

        await _store.SaveOutputAsync("watch-x", blockId, CreateJsonElement(new { owner = "x" }));
        await _store.SaveOutputAsync("watch-y", blockId, CreateJsonElement(new { owner = "y" }));

        var resultX = await _store.GetPreviousOutputAsync("watch-x", blockId);
        var resultY = await _store.GetPreviousOutputAsync("watch-y", blockId);

        resultX.ShouldNotBeNull();
        resultX.Value.GetProperty("owner").GetString().ShouldBe("x");

        resultY.ShouldNotBeNull();
        resultY.Value.GetProperty("owner").GetString().ShouldBe("y");

        var historyX = await _store.GetHistoryAsync("watch-x", blockId);
        var historyY = await _store.GetHistoryAsync("watch-y", blockId);
        historyX.Count.ShouldBe(1);
        historyY.Count.ShouldBe(1);
    }
}
