using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Blocks.Acquisition;
using ChangeDetection.Tests.Pipeline.Blocks;
using ChangeDetection.Core.Pipeline;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Search;

[Category("Unit")]
public class SearchBlockTests : TestBase
{
    private readonly SearchBlock _sut = new();

    [Test]
    public async Task BlockType_ReturnsSearch()
    {
        _sut.BlockType.ShouldBe("Search");
        _sut.CriticalityTier.ShouldBe(BlockCriticalityTier.Infrastructure);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExecuteAsync_WithQueryInput_ReturnsResults()
    {
        var mockProvider = CreateMockProvider("searxng", isAvailable: true, results:
        [
            new SearchResult { Url = "https://example.com/1", Title = "Result 1", Position = 1 },
            new SearchResult { Url = "https://example.com/2", Title = "Result 2", Position = 2 }
        ]);

        var services = CreateServicesWithProvider(mockProvider);

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("search-1")
            .WithInput("query", (object)"test query")
            .WithServices(services)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);

        result.Output.ShouldNotBeNull();
        var output = result.Output.Value;
        output.TryGetProperty("resultCount", out var count).ShouldBeTrue();
        count.GetInt32().ShouldBe(2);

        output.TryGetProperty("text", out var text).ShouldBeTrue();
        text.GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task ExecuteAsync_WithQueryInConfig_ReturnsResults()
    {
        var mockProvider = CreateMockProvider("searxng", isAvailable: true, results:
        [
            new SearchResult { Url = "https://example.com/1", Title = "Result 1", Position = 1 }
        ]);

        var services = CreateServicesWithProvider(mockProvider);

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline(
            "search-1", "Search", new { query = "config query", provider = "searxng" });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("search-1")
            .WithServices(services)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_NoQuery_ReturnsFailed()
    {
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("search-1")
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Error.ShouldContain("query");
    }

    [Test]
    public async Task ExecuteAsync_NoProviders_ReturnsFailed()
    {
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IEnumerable<ISearchProvider>))
            .Returns(Enumerable.Empty<ISearchProvider>());

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("search-1")
            .WithInput("query", (object)"test")
            .WithServices(services)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Error.ShouldContain("not found");
    }

    [Test]
    public async Task ExecuteAsync_ProviderUnavailable_ReturnsFailed()
    {
        var mockProvider = CreateMockProvider("searxng", isAvailable: false);
        var services = CreateServicesWithProvider(mockProvider);

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("search-1")
            .WithInput("query", (object)"test")
            .WithServices(services)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Error.ShouldContain("not configured");
    }

    [Test]
    public async Task ExecuteAsync_SearchFails_ReturnsFailed()
    {
        var mockProvider = CreateMockProvider("searxng", isAvailable: true, error: "Connection refused");
        var services = CreateServicesWithProvider(mockProvider);

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("search-1")
            .WithInput("query", (object)"test")
            .WithServices(services)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Error.ShouldContain("Connection refused");
    }

    [Test]
    public async Task ExecuteAsync_NoResults_ReturnsSuccessWithEmptyList()
    {
        var mockProvider = CreateMockProvider("searxng", isAvailable: true, results: []);
        var services = CreateServicesWithProvider(mockProvider);

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("search-1")
            .WithInput("query", (object)"obscure query")
            .WithServices(services)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output.ShouldNotBeNull();
        result.Output.Value.TryGetProperty("resultCount", out var count).ShouldBeTrue();
        count.GetInt32().ShouldBe(0);
    }

    [Test]
    public async Task BuildDiffableText_SortsByUrl_NotByPosition()
    {
        var resultSet = new SearchResultSet
        {
            ProviderId = "test",
            Query = "test",
            Results =
            [
                new SearchResult { Url = "https://z.com", Title = "Z Site", Position = 1 },
                new SearchResult { Url = "https://a.com", Title = "A Site", Position = 2 },
                new SearchResult { Url = "https://m.com", Title = "M Site", Position = 3 }
            ]
        };

        var text = SearchBlock.BuildDiffableText(resultSet);

        var lines = text.Split('\n');
        lines[0].ShouldContain("a.com");
        lines[1].ShouldContain("m.com");
        lines[2].ShouldContain("z.com");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExecuteAsync_WithSpecificProvider_SelectsCorrectOne()
    {
        var provider1 = CreateMockProvider("searxng", isAvailable: true, results:
            [new SearchResult { Url = "https://searxng.com", Title = "SearXNG Result", Position = 1 }]);
        var provider2 = CreateMockProvider("brave", isAvailable: true, results:
            [new SearchResult { Url = "https://brave.com", Title = "Brave Result", Position = 1 }]);

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IEnumerable<ISearchProvider>))
            .Returns(new List<ISearchProvider> { provider1, provider2 });

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline(
            "search-1", "Search", new { query = "test", provider = "brave" });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("search-1")
            .WithInput("query", (object)"test")
            .WithServices(services)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output.ShouldNotBeNull();
        result.Output.Value.TryGetProperty("provider", out var providerOutput).ShouldBeTrue();
        providerOutput.GetString().ShouldBe("brave");
    }

    private static ISearchProvider CreateMockProvider(
        string providerId,
        bool isAvailable,
        IReadOnlyList<SearchResult>? results = null,
        string? error = null)
    {
        var provider = Substitute.For<ISearchProvider>();
        provider.ProviderId.Returns(providerId);
        provider.IsAvailable.Returns(isAvailable);

        if (error is not null)
        {
            provider.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
                .Returns(new SearchResultSet
                {
                    ProviderId = providerId,
                    Query = "",
                    Results = [],
                    IsSuccess = false,
                    ErrorMessage = error
                });
        }
        else
        {
            provider.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
                .Returns(new SearchResultSet
                {
                    ProviderId = providerId,
                    Query = "",
                    Results = results ?? [],
                    IsSuccess = true
                });
        }

        return provider;
    }

    private static IServiceProvider CreateServicesWithProvider(ISearchProvider provider)
    {
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IEnumerable<ISearchProvider>))
            .Returns(new List<ISearchProvider> { provider });
        return services;
    }
}
