using System.Text.Json;
using ChangeDetection.Services.Blocks.Comparison;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Search;

[Category("Unit")]
public class RankingSnapshotBlockTests : TestBase
{
    [Test]
    public async Task BlockType_ReturnsRankingSnapshot()
    {
        var block = new RankingSnapshotBlock();
        block.BlockType.ShouldBe("RankingSnapshot");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ParseSearchResults_ExtractsPositionsAndUrls()
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            results = new[]
            {
                new { url = "https://a.com", title = "A", position = 1, snippet = "Snip A", engine = "google" },
                new { url = "https://b.com", title = "B", position = 2, snippet = "Snip B", engine = "brave" },
                new { url = "https://c.com", title = "C", position = 3, snippet = (string?)null, engine = (string?)null }
            }
        });

        var results = RankingSnapshotBlock.ParseSearchResults(json);

        results.Count.ShouldBe(3);
        results[0].Url.ShouldBe("https://a.com");
        results[0].Position.ShouldBe(1);
        results[0].Title.ShouldBe("A");
        results[0].Engine.ShouldBe("google");
        results[1].Position.ShouldBe(2);
        results[2].Snippet.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ParseSearchResults_SkipsEmptyUrls()
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            results = new[]
            {
                new { url = "https://valid.com", title = "Valid", position = 1 },
                new { url = "", title = "Empty", position = 2 },
                new { url = (string?)null, title = "Null", position = 3 }
            }
        });

        var results = RankingSnapshotBlock.ParseSearchResults(json);
        results.Count.ShouldBe(1);
        results[0].Url.ShouldBe("https://valid.com");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRankingText_SortsByPositionAndFormatsCorrectly()
    {
        var results = new List<RankingSnapshotBlock.RankedResult>
        {
            new() { Position = 3, Url = "https://c.com", Title = "Page C" },
            new() { Position = 1, Url = "https://a.com", Title = "Page A" },
            new() { Position = 2, Url = "https://b.com", Title = "Page B" }
        };

        var text = RankingSnapshotBlock.BuildRankingText(results);

        var lines = text.Split('\n');
        lines.Length.ShouldBe(3);
        lines[0].ShouldContain("#1");
        lines[0].ShouldContain("https://a.com");
        lines[0].ShouldContain("\"Page A\"");
        lines[1].ShouldContain("#2");
        lines[1].ShouldContain("https://b.com");
        lines[2].ShouldContain("#3");
        lines[2].ShouldContain("https://c.com");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRankingText_ProducesDiffableOutput()
    {
        // Two snapshots with a rank change: b.com moves from #2 to #1
        var snapshot1 = new List<RankingSnapshotBlock.RankedResult>
        {
            new() { Position = 1, Url = "https://a.com", Title = "Page A" },
            new() { Position = 2, Url = "https://b.com", Title = "Page B" }
        };

        var snapshot2 = new List<RankingSnapshotBlock.RankedResult>
        {
            new() { Position = 1, Url = "https://b.com", Title = "Page B" },
            new() { Position = 2, Url = "https://a.com", Title = "Page A" }
        };

        var text1 = RankingSnapshotBlock.BuildRankingText(snapshot1);
        var text2 = RankingSnapshotBlock.BuildRankingText(snapshot2);

        // The texts should differ (rank swap)
        text1.ShouldNotBe(text2);

        // Each line format should contain position and URL
        text1.ShouldContain("#1");
        text2.ShouldContain("#1");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRankingText_EmptyResults_ReturnsEmpty()
    {
        var text = RankingSnapshotBlock.BuildRankingText([]);
        text.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRankingData_ReturnsSortedByPosition()
    {
        var results = new List<RankingSnapshotBlock.RankedResult>
        {
            new() { Position = 5, Url = "https://e.com", Title = "E" },
            new() { Position = 1, Url = "https://a.com", Title = "A" },
            new() { Position = 3, Url = "https://c.com", Title = "C" }
        };

        var ranked = RankingSnapshotBlock.BuildRankingData(results);

        ranked[0].Position.ShouldBe(1);
        ranked[1].Position.ShouldBe(3);
        ranked[2].Position.ShouldBe(5);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ParseSearchResults_NoResultsProperty_ReturnsEmpty()
    {
        var json = JsonSerializer.SerializeToElement(new { text = "no results here" });
        var results = RankingSnapshotBlock.ParseSearchResults(json);
        results.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ParseSearchResults_FallsBackToSequentialPosition()
    {
        // When position is missing from JSON, use sequential numbering
        var json = JsonSerializer.SerializeToElement(new
        {
            results = new[]
            {
                new { url = "https://a.com", title = "A" },
                new { url = "https://b.com", title = "B" }
            }
        });

        var results = RankingSnapshotBlock.ParseSearchResults(json);

        results.Count.ShouldBe(2);
        results[0].Position.ShouldBe(1);
        results[1].Position.ShouldBe(2);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExecuteAsync_WithValidInput_ReturnsRankingOutput()
    {
        var block = new RankingSnapshotBlock();
        var searchOutput = JsonSerializer.SerializeToElement(new
        {
            results = new[]
            {
                new { url = "https://a.com", title = "Page A", position = 1 },
                new { url = "https://b.com", title = "Page B", position = 2 }
            }
        });

        var context = new ChangeDetection.Tests.Pipeline.Blocks.BlockContextBuilder()
            .WithBlockInstanceId("ranking-1")
            .WithInput("searchResults", (object)searchOutput)
            .Build();

        var result = await block.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output.ShouldNotBeNull();

        var output = result.Output.Value;
        output.TryGetProperty("rankingText", out var text).ShouldBeTrue();
        text.GetString().ShouldContain("#1");
        text.GetString().ShouldContain("https://a.com");
        text.GetString().ShouldContain("#2");
        text.GetString().ShouldContain("https://b.com");

        output.TryGetProperty("resultCount", out var count).ShouldBeTrue();
        count.GetInt32().ShouldBe(2);
    }

    [Test]
    public async Task ExecuteAsync_MissingInput_ReturnsFailed()
    {
        var block = new RankingSnapshotBlock();

        var context = new ChangeDetection.Tests.Pipeline.Blocks.BlockContextBuilder()
            .WithBlockInstanceId("ranking-1")
            .Build();

        var result = await block.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Error.ShouldContain("searchResults");
    }

    [Test]
    public async Task ExecuteAsync_EmptyResults_ReturnsFailed()
    {
        var block = new RankingSnapshotBlock();
        var searchOutput = JsonSerializer.SerializeToElement(new
        {
            results = Array.Empty<object>()
        });

        var context = new ChangeDetection.Tests.Pipeline.Blocks.BlockContextBuilder()
            .WithBlockInstanceId("ranking-1")
            .WithInput("searchResults", (object)searchOutput)
            .Build();

        var result = await block.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Error.ShouldContain("No search results");
    }
}
