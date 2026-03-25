using System.Net;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Services;
using ChangeDetection.Services.GroupWatch;
using ChangeDetection.Services.Pipeline;
using ChangeDetection.Services.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.GroupWatch;

[Category("Unit")]
public class DiscoveryFlowTests : TestBase
{
    private readonly ISearchProvider _searchProvider = Substitute.For<ISearchProvider>();
    private readonly ILlmProviderChain _llmProviderChain = Substitute.For<ILlmProviderChain>();
    private readonly IWatchGroupService _watchGroupService = Substitute.For<IWatchGroupService>();
    private readonly IWatchService _watchService = Substitute.For<IWatchService>();
    private readonly IComposableSetupPipeline _composableSetupPipeline = Substitute.For<IComposableSetupPipeline>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();

    [Before(Test)]
    public void Setup()
    {
        _searchProvider.ProviderId.Returns("test");
        _searchProvider.DisplayName.Returns("Test");
        _searchProvider.IsAvailable.Returns(true);
        _watchService.GetAllAsync(Arg.Any<CancellationToken>()).Returns([]);
    }

    [Test]
    public async Task DiscoverAsync_SearchAndCatalogBothContributePortals()
    {
        const string searchUrl = "https://search.example.com/jobs";
        const string catalogUrl = "https://catalog.example.com/careers";

        _watchService.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([
                new WatchedSite
                {
                    Id = Guid.NewGuid(),
                    Url = catalogUrl,
                    Name = "Catalog Portal",
                    PipelineDefinitionJson = "{}",
                    CatalogStatus = CatalogVerificationStatus.Verified,
                    IsEnabled = true,
                    Tags = ["copenhagen"]
                }
            ]);

        _searchProvider.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResultSet
            {
                ProviderId = "test",
                Query = "scientist jobs copenhagen careers",
                IsSuccess = true,
                Results =
                [
                    new SearchResult
                    {
                        Url = searchUrl,
                        Title = "Search Portal",
                        Snippet = "Web result",
                        Position = 1
                    }
                ]
            });

        ConfigureIntentAndClassification(
            """
            {
              "location": "Copenhagen",
              "roleTypes": ["scientist"],
              "field": "biology",
              "searchQueries": ["scientist jobs copenhagen careers"]
            }
            """,
            $$"""
            [
              {"url":"{{searchUrl}}","title":"Search Portal","reasoning":"web result career portal"},
              {"url":"{{catalogUrl}}","title":"Catalog Portal","reasoning":"known verified portal"}
            ]
            """);

        var sut = CreateSut();

        var progress = await DiscoverAsync(sut, "scientist jobs in Copenhagen");
        var filtering = progress.Last(item => item.Phase == GroupWatchPhase.Filtering);
        var portalsReady = progress.Single(item => item.Phase == GroupWatchPhase.PortalsReady);

        filtering.Portals!.Select(p => p.Url).ShouldContain(searchUrl);
        filtering.Portals!.Select(p => p.Url).ShouldContain(catalogUrl);
        portalsReady.Portals!.Select(p => p.Url).ShouldBe([searchUrl], ignoreOrder: true);
    }

    [Test]
    public async Task DiscoverAsync_DeduplicatesAcrossSourcesByDomain()
    {
        const string searchUrl = "https://dup.example.com/jobs";
        const string catalogUrl = "https://dup.example.com/careers";

        _watchService.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([
                new WatchedSite
                {
                    Id = Guid.NewGuid(),
                    Url = catalogUrl,
                    Name = "Catalog Portal",
                    PipelineDefinitionJson = "{}",
                    CatalogStatus = CatalogVerificationStatus.Verified,
                    IsEnabled = true,
                    Tags = ["copenhagen"]
                }
            ]);

        _searchProvider.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResultSet
            {
                ProviderId = "test",
                Query = "scientist jobs copenhagen careers",
                IsSuccess = true,
                Results =
                [
                    new SearchResult
                    {
                        Url = searchUrl,
                        Title = "Search Portal",
                        Snippet = "Web result",
                        Position = 1
                    }
                ]
            });

        ConfigureIntentAndClassification(
            """
            {
              "location": "Copenhagen",
              "roleTypes": ["scientist"],
              "field": "biology",
              "searchQueries": ["scientist jobs copenhagen careers"]
            }
            """,
            $$"""
            [
              {"url":"{{searchUrl}}","title":"Search Portal","reasoning":"deduped domain"}
            ]
            """);

        var sut = CreateSut();

        var progress = await DiscoverAsync(sut, "scientist jobs in Copenhagen");
        var filtering = progress.Last(item => item.Phase == GroupWatchPhase.Filtering);

        filtering.Portals!.Count.ShouldBe(1);
        filtering.Portals[0].Domain.ShouldBe("dup.example.com");
    }

    [Test]
    public async Task DiscoverAsync_LlmClassifierFiltersNonCareerUrls()
    {
        const string careersUrl = "https://careers.example.com/jobs";
        const string newsUrl = "https://news.example.com/article";

        _searchProvider.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResultSet
            {
                ProviderId = "test",
                Query = "scientist jobs copenhagen careers",
                IsSuccess = true,
                Results =
                [
                    new SearchResult { Url = careersUrl, Title = "Careers", Snippet = "Open roles", Position = 1 },
                    new SearchResult { Url = newsUrl, Title = "News", Snippet = "Article", Position = 2 }
                ]
            });

        ConfigureIntentAndClassification(
            """
            {
              "location": "Copenhagen",
              "roleTypes": ["scientist"],
              "field": "biology",
              "searchQueries": ["scientist jobs copenhagen careers"]
            }
            """,
            $$"""
            [
              {"url":"{{careersUrl}}","title":"Careers","reasoning":"actual portal"}
            ]
            """);

        var sut = CreateSut();

        var progress = await DiscoverAsync(sut, "scientist jobs in Copenhagen");
        var portalsReady = progress.Single(item => item.Phase == GroupWatchPhase.PortalsReady);

        portalsReady.Portals!.Count.ShouldBe(1);
        portalsReady.Portals[0].Url.ShouldBe(careersUrl);
    }

    [Test]
    public async Task DiscoverAsync_EmptySearchResults_StillUsesCatalog()
    {
        const string catalogUrl = "https://catalog.example.com/careers";

        _watchService.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([
                new WatchedSite
                {
                    Id = Guid.NewGuid(),
                    Url = catalogUrl,
                    Name = "Catalog Portal",
                    PipelineDefinitionJson = "{}",
                    CatalogStatus = CatalogVerificationStatus.Verified,
                    IsEnabled = true,
                    Tags = ["copenhagen"]
                }
            ]);

        _searchProvider.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResultSet
            {
                ProviderId = "test",
                Query = "scientist jobs copenhagen careers",
                IsSuccess = true,
                Results = []
            });

        ConfigureIntentAndClassification(
            """
            {
              "location": "Copenhagen",
              "roleTypes": ["scientist"],
              "field": "biology",
              "searchQueries": ["scientist jobs copenhagen careers"]
            }
            """,
            $$"""
            [
              {"url":"{{catalogUrl}}","title":"Catalog Portal","reasoning":"catalog still contributes"}
            ]
            """);

        var sut = CreateSut();

        var progress = await DiscoverAsync(sut, "scientist jobs in Copenhagen");
        var filtering = progress.Last(item => item.Phase == GroupWatchPhase.Filtering);
        var complete = progress.Last(item => item.Phase == GroupWatchPhase.Complete);

        filtering.Portals!.Count.ShouldBe(1);
        filtering.Portals[0].Url.ShouldBe(catalogUrl);
        complete.Message.ShouldContain("already");
    }

    [Test]
    public async Task DiscoverAsync_EmptyCatalog_StillUsesSearch()
    {
        const string searchUrl = "https://search.example.com/jobs";

        _watchService.GetAllAsync(Arg.Any<CancellationToken>()).Returns([]);
        _searchProvider.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResultSet
            {
                ProviderId = "test",
                Query = "scientist jobs copenhagen careers",
                IsSuccess = true,
                Results =
                [
                    new SearchResult
                    {
                        Url = searchUrl,
                        Title = "Search Portal",
                        Snippet = "Web result",
                        Position = 1
                    }
                ]
            });

        ConfigureIntentAndClassification(
            """
            {
              "location": "Copenhagen",
              "roleTypes": ["scientist"],
              "field": "biology",
              "searchQueries": ["scientist jobs copenhagen careers"]
            }
            """,
            $$"""
            [
              {"url":"{{searchUrl}}","title":"Search Portal","reasoning":"search still contributes"}
            ]
            """);

        var sut = CreateSut();

        var progress = await DiscoverAsync(sut, "scientist jobs in Copenhagen");
        var portalsReady = progress.Single(item => item.Phase == GroupWatchPhase.PortalsReady);

        portalsReady.Portals!.Count.ShouldBe(1);
        portalsReady.Portals[0].Url.ShouldBe(searchUrl);
    }

    private void ConfigureIntentAndClassification(string intentJson, string classificationJson)
    {
        _llmProviderChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                new LlmResponse { IsSuccess = true, Content = intentJson },
                new LlmResponse { IsSuccess = true, Content = classificationJson });
    }

    private GroupWatchDiscoveryService CreateSut()
    {
        _httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));

        var multiSearch = new MultiProviderSearchService([_searchProvider], CreateLogger<MultiProviderSearchService>());
        var setupFlowEnhancements = new SetupFlowEnhancements(CreateLogger<SetupFlowEnhancements>(), _httpClientFactory);

        return new GroupWatchDiscoveryService(
            multiSearch,
            _llmProviderChain,
            _watchGroupService,
            _watchService,
            setupFlowEnhancements,
            _composableSetupPipeline,
            _httpClientFactory,
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<IWatchExecutionLock>(),
            CreateLogger<GroupWatchDiscoveryService>(),
            Options.Create(new GroupWatchDiscoveryOptions()));
    }

    private static async Task<List<GroupWatchProgress>> DiscoverAsync(GroupWatchDiscoveryService sut, string userInput)
    {
        var progress = new List<GroupWatchProgress>();
        await foreach (var item in sut.DiscoverAsync(userInput))
            progress.Add(item);
        return progress;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
