using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks;

[Category("Unit")]
public class BlockContextBuilderTests : TestBase
{
    [Test]
    public async Task Build_WithDefaults_ReturnsValidContext()
    {
        var context = new BlockContextBuilder().Build();

        context.WatchId.ShouldNotBe(Guid.Empty);
        context.BlockInstanceId.ShouldNotBeNullOrWhiteSpace();
        context.Inputs.ShouldNotBeNull();
        context.Inputs.ShouldBeEmpty();
        context.Logger.ShouldNotBeNull();
        context.StateStore.ShouldNotBeNull();
        context.Page.ShouldBeNull();
        context.Services.ShouldNotBeNull();
        context.IsFirstRun.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task WithInput_AddsToInputDictionary()
    {
        var element = JsonDocument.Parse("""{"price": 29.99}""").RootElement.Clone();

        var context = new BlockContextBuilder()
            .WithInput("content", element)
            .Build();

        context.Inputs.ShouldContainKey("content");
        context.Inputs["content"].GetProperty("price").GetDecimal().ShouldBe(29.99m);
        await Task.CompletedTask;
    }

    [Test]
    public async Task WithInput_Object_SerializesToJsonElement()
    {
        var data = new { Name = "Test", Value = 42 };

        var context = new BlockContextBuilder()
            .WithInput("data", (object)data)
            .Build();

        context.Inputs.ShouldContainKey("data");
        context.Inputs["data"].GetProperty("Name").GetString().ShouldBe("Test");
        context.Inputs["data"].GetProperty("Value").GetInt32().ShouldBe(42);
        await Task.CompletedTask;
    }

    [Test]
    public async Task WithPreviousOutput_ConfiguresStateStore()
    {
        var previousOutput = JsonDocument.Parse("""{"hash": "abc123"}""").RootElement.Clone();

        var context = new BlockContextBuilder()
            .WithPreviousOutput(previousOutput)
            .Build();

        var result = await context.StateStore.GetPreviousOutputAsync("any", "any");
        result.ShouldNotBeNull();
        result!.Value.GetProperty("hash").GetString().ShouldBe("abc123");
    }

    [Test]
    public async Task WithPage_SetsPageOnContext()
    {
        var fakePage = new object();

        var context = new BlockContextBuilder()
            .WithPage(fakePage)
            .Build();

        context.Page.ShouldBeSameAs(fakePage);
        await Task.CompletedTask;
    }

    [Test]
    public async Task WithFirstRun_SetsFlag()
    {
        var context = new BlockContextBuilder()
            .WithFirstRun()
            .Build();

        context.IsFirstRun.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Build_MultipleTimes_ReturnsDifferentInstances()
    {
        var builder = new BlockContextBuilder();

        var context1 = builder.Build();
        var context2 = builder.Build();

        context1.ShouldNotBeSameAs(context2);
        await Task.CompletedTask;
    }
}
