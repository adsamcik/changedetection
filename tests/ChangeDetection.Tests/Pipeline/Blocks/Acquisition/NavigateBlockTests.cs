using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Acquisition;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Acquisition;

[Category("Unit")]
public class NavigateBlockTests : TestBase
{
    private readonly NavigateBlock _sut = new();
    private readonly IContentFetcher _fetcher = Substitute.For<IContentFetcher>();

    private IServiceProvider CreateServices()
    {
        var validator = Substitute.For<IUrlValidator>();
        validator.Validate(Arg.Any<string>()).Returns((string?)null);

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IContentFetcher)).Returns(_fetcher);
        services.GetService(typeof(IUrlValidator)).Returns(validator);
        return services;
    }

    [Test]
    public async Task ExecuteAsync_WithValidUrl_FetchesContent()
    {
        _fetcher
            .FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = true, Html = "<html><body>Hello</body></html>" });

        var context = new BlockContextBuilder()
            .WithInput("url", (object)"https://example.com")
            .WithServices(CreateServices())
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("html").GetString().ShouldBe("<html><body>Hello</body></html>");
        result.Output!.Value.GetProperty("url").GetString().ShouldBe("https://example.com");

        await _fetcher.Received(1)
            .FetchAsync("https://example.com", Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_FetchFails_ReturnsFailed()
    {
        _fetcher
            .FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = false, ErrorMessage = "Connection refused" });

        var context = new BlockContextBuilder()
            .WithInput("url", (object)"https://down.example.com")
            .WithServices(CreateServices())
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error!.ShouldContain("Connection refused");
    }

    [Test]
    public async Task ExecuteAsync_WithJavaScript_PassesFetchOptions()
    {
        _fetcher
            .FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = true, Html = "<html></html>" });

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("nav-1", "Navigate", new { useJavaScript = true, timeout = 60000 });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("nav-1")
            .WithInput("url", (object)"https://example.com")
            .WithServices(CreateServices())
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();

        await _fetcher.Received(1).FetchAsync(
            "https://example.com",
            Arg.Is<FetchOptions>(o => o.UseJavaScript && o.TimeoutSeconds == 60),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_WithMissingUrlInput_ReturnsFailed()
    {
        var context = new BlockContextBuilder()
            .WithServices(CreateServices())
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("url");
    }

    [Test]
    public async Task ExecuteAsync_WithUrlFromJsonObject_ExtractsUrlProperty()
    {
        _fetcher
            .FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = true, Html = "<html></html>" });

        var context = new BlockContextBuilder()
            .WithInput("url", (object)new { url = "https://nested.example.com", config = new { } })
            .WithServices(CreateServices())
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        await _fetcher.Received(1)
            .FetchAsync("https://nested.example.com", Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BlockType_ReturnsNavigate()
    {
        _sut.BlockType.ShouldBe("Navigate");
        await Task.CompletedTask;
    }

    [Test]
    public async Task CriticalityTier_ReturnsInfrastructure()
    {
        _sut.CriticalityTier.ShouldBe(BlockCriticalityTier.Infrastructure);
        await Task.CompletedTask;
    }
}
