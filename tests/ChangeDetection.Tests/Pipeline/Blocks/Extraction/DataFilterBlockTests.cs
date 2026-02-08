using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Extraction;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Extraction;

[Category("Unit")]
public class DataFilterBlockTests : TestBase
{
    private readonly DataFilterBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_EqFilter_ReturnsMatchingItems()
    {
        var data = new[]
        {
            new { name = "Alice", role = "admin" },
            new { name = "Bob", role = "user" },
            new { name = "Carol", role = "admin" }
        };

        var config = new { conditions = new[] { new { field = "role", @operator = "eq", value = "admin" } } };
        var context = BuildContext("df-1", data, config);

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        var items = result.Output!.Value.EnumerateArray().ToList();
        items.Count.ShouldBe(2);
        items[0].GetProperty("name").GetString().ShouldBe("Alice");
        items[1].GetProperty("name").GetString().ShouldBe("Carol");
    }

    [Test]
    public async Task ExecuteAsync_GtFilter_ReturnsItemsAboveThreshold()
    {
        var data = new[]
        {
            new { product = "A", price = 50 },
            new { product = "B", price = 150 },
            new { product = "C", price = 99 }
        };

        var config = new { conditions = new[] { new { field = "price", @operator = "gt", value = 100 } } };
        var context = BuildContext("df-1", data, config);

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        var items = result.Output!.Value.EnumerateArray().ToList();
        items.Count.ShouldBe(1);
        items[0].GetProperty("product").GetString().ShouldBe("B");
    }

    [Test]
    public async Task ExecuteAsync_ContainsFilter_CaseInsensitive()
    {
        var data = new[]
        {
            new { title = "Breaking News: Market Surges" },
            new { title = "Weather Update" },
            new { title = "BREAKING: Election Results" }
        };

        var config = new { conditions = new[] { new { field = "title", @operator = "contains", value = "breaking" } } };
        var context = BuildContext("df-1", data, config);

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        var items = result.Output!.Value.EnumerateArray().ToList();
        items.Count.ShouldBe(2);
    }

    [Test]
    public async Task ExecuteAsync_AllMode_RequiresAllConditions()
    {
        var data = new[]
        {
            new { name = "Alice", role = "admin", active = true },
            new { name = "Bob", role = "admin", active = false },
            new { name = "Carol", role = "user", active = true }
        };

        var config = new
        {
            mode = "all",
            conditions = new object[]
            {
                new { field = "role", @operator = "eq", value = "admin" },
                new { field = "active", @operator = "eq", value = true }
            }
        };
        var context = BuildContext("df-1", data, config);

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        var items = result.Output!.Value.EnumerateArray().ToList();
        items.Count.ShouldBe(1);
        items[0].GetProperty("name").GetString().ShouldBe("Alice");
    }

    [Test]
    public async Task ExecuteAsync_AnyMode_MatchesAnyCondition()
    {
        var data = new[]
        {
            new { name = "Alice", role = "admin", score = 30 },
            new { name = "Bob", role = "user", score = 95 },
            new { name = "Carol", role = "user", score = 40 }
        };

        var config = new
        {
            mode = "any",
            conditions = new object[]
            {
                new { field = "role", @operator = "eq", value = "admin" },
                new { field = "score", @operator = "gte", value = 90 }
            }
        };
        var context = BuildContext("df-1", data, config);

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        var items = result.Output!.Value.EnumerateArray().ToList();
        items.Count.ShouldBe(2);
        items[0].GetProperty("name").GetString().ShouldBe("Alice");
        items[1].GetProperty("name").GetString().ShouldBe("Bob");
    }

    [Test]
    public async Task ExecuteAsync_EmptyConditions_ReturnsAllItems()
    {
        var data = new[] { new { a = 1 }, new { a = 2 } };
        var config = new { conditions = Array.Empty<object>() };
        var context = BuildContext("df-1", data, config);

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output!.Value.EnumerateArray().Count().ShouldBe(2);
    }

    [Test]
    public async Task ExecuteAsync_ObjectInput_ReturnsObjectWhenMatches()
    {
        var data = new { name = "Widget", price = 25 };
        var config = new { conditions = new[] { new { field = "price", @operator = "lt", value = 50 } } };
        var context = BuildContext("df-1", data, config);

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output!.Value.GetProperty("name").GetString().ShouldBe("Widget");
    }

    [Test]
    public async Task ExecuteAsync_ObjectInput_ReturnsEmptyArrayWhenNoMatch()
    {
        var data = new { name = "Widget", price = 75 };
        var config = new { conditions = new[] { new { field = "price", @operator = "lt", value = 50 } } };
        var context = BuildContext("df-1", data, config);

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output!.Value.ValueKind.ShouldBe(JsonValueKind.Array);
        result.Output!.Value.GetArrayLength().ShouldBe(0);
    }

    [Test]
    public async Task ExecuteAsync_ExistsOperator_ReturnsTrueWhenFieldPresent()
    {
        var data = new object[]
        {
            new { name = "Alice", email = "a@b.com" },
            new { name = "Bob" }
        };

        var config = new { conditions = new[] { new { field = "email", @operator = "exists" } } };
        var context = BuildContext("df-1", data, config);

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        var items = result.Output!.Value.EnumerateArray().ToList();
        items.Count.ShouldBe(1);
        items[0].GetProperty("name").GetString().ShouldBe("Alice");
    }

    [Test]
    public async Task ExecuteAsync_NotExistsOperator_ReturnsTrueWhenFieldMissing()
    {
        var data = new object[]
        {
            new { name = "Alice", email = "a@b.com" },
            new { name = "Bob" }
        };

        var config = new { conditions = new[] { new { field = "email", @operator = "notExists" } } };
        var context = BuildContext("df-1", data, config);

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        var items = result.Output!.Value.EnumerateArray().ToList();
        items.Count.ShouldBe(1);
        items[0].GetProperty("name").GetString().ShouldBe("Bob");
    }

    [Test]
    public async Task ExecuteAsync_NonArrayNonObject_ReturnsFailed()
    {
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("df-1")
            .WithInput("data", (object)"just a string")
            .WithPipelineDefinition(CreatePipeline("df-1", new { conditions = new[] { new { field = "x", @operator = "eq", value = 1 } } }))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("DataFilter expects array or object");
    }

    [Test]
    public async Task ExecuteAsync_NoConfig_ReturnsAllItems()
    {
        var data = new[] { new { a = 1 }, new { a = 2 } };
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("df-1")
            .WithInput("data", (object)data)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output!.Value.EnumerateArray().Count().ShouldBe(2);
    }

    [Test]
    public async Task ExecuteAsync_NeqFilter_ExcludesMatching()
    {
        var data = new[]
        {
            new { status = "active" },
            new { status = "inactive" },
            new { status = "active" }
        };

        var config = new { conditions = new[] { new { field = "status", @operator = "neq", value = "inactive" } } };
        var context = BuildContext("df-1", data, config);

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output!.Value.EnumerateArray().Count().ShouldBe(2);
    }

    [Test]
    public async Task ExecuteAsync_StartsWithFilter_MatchesPrefix()
    {
        var data = new[]
        {
            new { url = "https://example.com/page" },
            new { url = "http://other.com/page" }
        };

        var config = new { conditions = new[] { new { field = "url", @operator = "startsWith", value = "https://" } } };
        var context = BuildContext("df-1", data, config);

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output!.Value.EnumerateArray().Count().ShouldBe(1);
    }

    [Test]
    public async Task ExecuteAsync_CurrencyStringParsing_ComparesNumeric()
    {
        var data = new[]
        {
            new { product = "A", price = "$29.99" },
            new { product = "B", price = "$149.99" },
            new { product = "C", price = "$79.50" }
        };

        var config = new { conditions = new[] { new { field = "price", @operator = "lt", value = 100 } } };
        var context = BuildContext("df-1", data, config);

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        var items = result.Output!.Value.EnumerateArray().ToList();
        items.Count.ShouldBe(2);
        items[0].GetProperty("product").GetString().ShouldBe("A");
        items[1].GetProperty("product").GetString().ShouldBe("C");
    }

    [Test]
    public async Task BlockType_ReturnsDataFilter()
    {
        _sut.BlockType.ShouldBe("DataFilter");
        await Task.CompletedTask;
    }

    [Test]
    public async Task CriticalityTier_ReturnsExtraction()
    {
        _sut.CriticalityTier.ShouldBe(BlockCriticalityTier.Extraction);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Ports_MatchExpectedDefinition()
    {
        _sut.InputPorts.Count.ShouldBe(1);
        _sut.InputPorts[0].Name.ShouldBe("data");
        _sut.InputPorts[0].Type.ShouldBe(PortType.ExtractedObjects);
        _sut.OutputPorts.Count.ShouldBe(1);
        _sut.OutputPorts[0].Name.ShouldBe("filtered");
        _sut.OutputPorts[0].Type.ShouldBe(PortType.ExtractedObjects);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExecuteAsync_MissingDataInput_ReturnsFailed()
    {
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("df-1")
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("'data' input");
    }

    private BlockContext BuildContext(string blockId, object data, object config) =>
        new BlockContextBuilder()
            .WithBlockInstanceId(blockId)
            .WithInput("data", data)
            .WithPipelineDefinition(CreatePipeline(blockId, config))
            .Build();

    private static PipelineDefinition CreatePipeline(string blockId, object config) => new()
    {
        SchemaVersion = 1,
        Blocks =
        [
            new BlockDefinition
            {
                Id = blockId,
                Type = "DataFilter",
                Config = JsonSerializer.SerializeToElement(config)
            }
        ],
        Connections = []
    };
}
