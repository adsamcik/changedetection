using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Services.GroupWatch;
using ChangeDetection.Services.Pipeline;
using ChangeDetection.Services.Search;
using ChangeDetection.Services.SetupPipeline;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using System.Net;
using System.Text.Json;
using TUnit.Core;
using System.IO;

namespace ChangeDetection.Tests.Services;

[Category("Unit")]
public class GroupWatchDiscoveryServiceTests : TestBase
{
    private readonly ISearchProvider _searchProvider = Substitute.For<ISearchProvider>();
    private readonly MultiProviderSearchService _multiSearch;
    private readonly ILlmProviderChain _llmProviderChain = Substitute.For<ILlmProviderChain>();
    private readonly IWatchGroupService _watchGroupService = Substitute.For<IWatchGroupService>();
    private readonly IWatchService _watchService = Substitute.For<IWatchService>();
    private readonly IComposableSetupPipeline _composableSetupPipeline = Substitute.For<IComposableSetupPipeline>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();

    public GroupWatchDiscoveryServiceTests()
    {
        _searchProvider.ProviderId.Returns("test");
        _searchProvider.DisplayName.Returns("Test");
        _searchProvider.IsAvailable.Returns(true);
        _multiSearch = new MultiProviderSearchService([_searchProvider], CreateLogger<MultiProviderSearchService>());
    }

    [Test]
    public async Task DiscoverAsync_ExistingWatchWithSameDomain_SkipsDuplicatePortalAndReportsExistingGroup()
    {
        const string userInput = "Watch biology jobs in Copenhagen";
        const string existingPortalUrl = "https://novonordisk.com/careers";
        const string duplicateUrl = "https://www.novonordisk.com/jobs";
        const string newPortalUrl = "https://jobs.example.com/careers";
        var sut = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>jobs</body></html>")
        });

        var existingGroupId = Guid.NewGuid();
        var existingWatchId = Guid.NewGuid();

        // LLM call 1: intent parsing
        // LLM call 2: classification — approves the search result + catalog entry
        // LLM call 3: intent re-parsing for watch creation
        _llmProviderChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                new LlmResponse
                {
                    IsSuccess = true,
                    Content = """
                        {
                          "location": "Copenhagen",
                          "roleTypes": ["scientist"],
                          "field": "biology",
                          "searchQueries": ["biology jobs Copenhagen careers"]
                        }
                        """
                },
                new LlmResponse
                {
                    IsSuccess = true,
                    Content = $$"""
                        [
                          {"url": "{{existingPortalUrl}}", "title": "Novo Nordisk Careers", "reasoning": "Major pharma careers page"},
                          {"url": "{{newPortalUrl}}", "title": "Example Jobs", "reasoning": "Job board for Copenhagen"}
                        ]
                        """
                },
                new LlmResponse
                {
                    IsSuccess = true,
                    Content = """
                        {
                          "location": "Copenhagen",
                          "roleTypes": ["scientist"],
                          "field": "biology",
                          "searchQueries": ["biology jobs Copenhagen careers"]
                        }
                        """
                });

        _watchService.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(
            [
                new WatchedSite
                {
                    Id = existingWatchId,
                    Url = existingPortalUrl,
                    GroupId = existingGroupId,
                    Name = "Novo Careers",
                    PipelineDefinitionJson = "{}",
                    Status = WatchStatus.Active,
                    IsEnabled = true
                }
            ]);

        _watchGroupService.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(
            [
                new WatchGroup
                {
                    Id = existingGroupId,
                    Name = "Biology Jobs"
                }
            ]);

        _searchProvider.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResultSet
            {
                ProviderId = "test",
                Query = "biology jobs Copenhagen careers",
                Results =
                [
                    new SearchResult
                    {
                        Url = newPortalUrl,
                        Title = "Example Jobs",
                        Snippet = "Career portal",
                        Position = 1
                    }
                ]
            });

        var newGroupId = Guid.NewGuid();
        _watchGroupService.CreateGroupAsync(Arg.Any<WatchGroupCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new WatchGroup { Id = newGroupId, Name = "test-group" });

        _composableSetupPipeline.StartSetupAsync(Arg.Any<SetupRequest>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(
                new SetupProgress
                {
                    Phase = SetupPhase.Checkpoint1,
                    Type = SetupProgressType.CheckpointReached,
                    Message = "checkpoint 1",
                    SessionId = "session-new"
                }));

        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks = [new BlockDefinition
            {
                Id = "navigate-1",
                Type = "Navigate",
                Position = 0,
                Config = JsonSerializer.SerializeToElement(new { useJavaScript = true })
            }],
            Connections = [],
            Metadata = new PipelineMetadata
            {
                DisplayTitle = "Example Jobs Watch",
                UserIntent = userInput,
                CreatedAt = DateTime.UtcNow
            }
        };

        _composableSetupPipeline.ConfirmIntentAsync("session-new", true, null, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(
                new SetupProgress
                {
                    Phase = SetupPhase.Checkpoint2,
                    Type = SetupProgressType.CheckpointReached,
                    Message = "checkpoint 2",
                    Proposal = new PipelineProposal
                    {
                        Pipeline = pipeline,
                        HumanSummary = "generated"
                    }
                }));

        _composableSetupPipeline.ConfirmPipelineAsync("session-new", false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable());

        var createdWatchId = Guid.NewGuid();
        _watchService.CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new WatchedSite { Id = createdWatchId, Url = newPortalUrl });

        var discoveryProgress = new List<GroupWatchProgress>();
        await foreach (var item in sut.DiscoverAsync(userInput))
        {
            discoveryProgress.Add(item);
        }

        await _watchService.DidNotReceive().CreateWatchAsync(
            Arg.Any<CreateWatchRequest>(),
            Arg.Any<CancellationToken>());

        discoveryProgress.ShouldContain(item =>
            item.Phase == GroupWatchPhase.Filtering &&
            item.Message.Contains("Skipped novonordisk.com", StringComparison.OrdinalIgnoreCase) &&
            item.Message.Contains("Biology Jobs", StringComparison.Ordinal));

        var filteringSummary = discoveryProgress.Last(item => item.Phase == GroupWatchPhase.Filtering);
        filteringSummary.Portals!.Count.ShouldBe(2);
        filteringSummary.Portals.Single(portal => portal.Domain == "novonordisk.com").ExistingWatchId.ShouldBe(existingWatchId);
        filteringSummary.Portals.Single(portal => portal.Domain == "novonordisk.com").ExistingGroupName.ShouldBe("Biology Jobs");

        var portalsReady = discoveryProgress.Single(item => item.Phase == GroupWatchPhase.PortalsReady);
        portalsReady.Portals!.Count.ShouldBe(1);
        portalsReady.Portals.Single().Domain.ShouldBe("jobs.example.com");

        var creationProgress = new List<GroupWatchProgress>();
        await foreach (var item in sut.CreateWatchesAsync(userInput, portalsReady.Portals))
        {
            creationProgress.Add(item);
        }

        await _watchService.Received(1).CreateWatchAsync(
            Arg.Is<CreateWatchRequest>(request => request.Url == newPortalUrl && request.GroupId == newGroupId),
            Arg.Any<CancellationToken>());

        await _watchService.DidNotReceive().CreateWatchAsync(
            Arg.Is<CreateWatchRequest>(request => request.Url == duplicateUrl),
            Arg.Any<CancellationToken>());
        creationProgress.Last().WatchIds.ShouldBe([createdWatchId]);
    }

    [Test]
    public async Task DiscoverAsync_CatalogEntryMergedIntoSearchResults_DeduplicatesByDomain()
    {
        const string userInput = "Watch scientist jobs in Copenhagen Denmark";
        var sut = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>jobs</body></html>")
        });

        _watchService.GetAllAsync(Arg.Any<CancellationToken>()).Returns([]);

        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var scannerDir = Path.Combine(repoRoot, "tools", "job-scanner");
        var sitesPath = Path.Combine(scannerDir, "sites.json");
        var backupPath = File.Exists(sitesPath) ? Path.GetTempFileName() : null;
        if (backupPath is not null)
            File.Copy(sitesPath, backupPath, overwrite: true);

        Directory.CreateDirectory(scannerDir);
        File.WriteAllText(sitesPath, """
            {
              "workday": [
                {
                  "company": "AGC Biologics",
                  "subdomain": "agcbio",
                  "instance": "wd5",
                  "site_id": "agcbio_careers",
                  "location_keywords": ["Copenhagen", "Denmark"]
                }
              ]
            }
            """);

        string? classificationPrompt = null;

        try
        {
            // LLM call 1: intent parsing → returns search queries
            // LLM call 2: classification → captures the prompt to verify catalog entries appear
            _llmProviderChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var prompt = callInfo.ArgAt<string>(0);

                    // First call is intent parsing (contains "searchQueries")
                    if (classificationPrompt is null && prompt.Contains("Candidate URLs", StringComparison.OrdinalIgnoreCase))
                    {
                        classificationPrompt = prompt;
                    }
                    else if (classificationPrompt is null)
                    {
                        // Intent parsing call
                        return new LlmResponse
                        {
                            IsSuccess = true,
                            Content = """
                                {
                                  "location": "Copenhagen, Denmark",
                                  "roleTypes": ["scientist"],
                                  "field": "biology",
                                  "searchQueries": ["scientist jobs Copenhagen careers"]
                                }
                                """
                        };
                    }

                    // Classification call — return empty array (no portals approved)
                    return new LlmResponse
                    {
                        IsSuccess = true,
                        Content = "[]"
                    };
                });

            // Search provider returns nothing — catalog is the only source
            _searchProvider.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
                .Returns(new SearchResultSet
                {
                    ProviderId = "test",
                    Query = "scientist jobs Copenhagen careers",
                    Results = []
                });

            var progress = new List<GroupWatchProgress>();
            await foreach (var item in sut.DiscoverAsync(userInput))
            {
                progress.Add(item);
            }

            // The classification prompt should contain the catalog entry URL
            classificationPrompt.ShouldNotBeNull();
            classificationPrompt.ShouldContain("https://agcbio.wd5.myworkdayjobs.com/en-US/agcbio_careers");
            progress.Last().Phase.ShouldBe(GroupWatchPhase.Complete);
        }
        finally
        {
            if (backupPath is not null)
            {
                File.Copy(backupPath, sitesPath, overwrite: true);
                File.Delete(backupPath);
            }
            else if (File.Exists(sitesPath))
            {
                File.Delete(sitesPath);
                if (!Directory.EnumerateFileSystemEntries(scannerDir).Any())
                    Directory.Delete(scannerDir);
            }
        }
    }

    [Test]
    public async Task DiscoverAsync_WebSearchIsPrimarySource_SearchProviderCalledBeforeClassification()
    {
        const string userInput = "SWE jobs in Berlin";
        const string searchResultUrl = "https://berlinstartupjobs.com/engineering";

        var sut = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>jobs</body></html>")
        });

        _watchService.GetAllAsync(Arg.Any<CancellationToken>()).Returns([]);

        var llmCallCount = 0;

        _llmProviderChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                llmCallCount++;
                if (llmCallCount == 1)
                {
                    // Intent parsing
                    return new LlmResponse
                    {
                        IsSuccess = true,
                        Content = """
                            {
                              "location": "Berlin, Germany",
                              "roleTypes": ["software engineer"],
                              "field": "software engineering",
                              "searchQueries": ["software engineer jobs Berlin careers portal", "Berlin tech companies hiring"]
                            }
                            """
                    };
                }

                // Classification — approve the search result
                return new LlmResponse
                {
                    IsSuccess = true,
                    Content = $$"""
                        [{"url": "{{searchResultUrl}}", "title": "Berlin Startup Jobs", "reasoning": "Engineering job board for Berlin"}]
                        """
                };
            });

        _searchProvider.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResultSet
            {
                ProviderId = "test",
                Query = "software engineer jobs Berlin careers portal",
                Results =
                [
                    new SearchResult
                    {
                        Url = searchResultUrl,
                        Title = "Berlin Startup Jobs - Engineering",
                        Snippet = "Find engineering jobs in Berlin startups",
                        Position = 1
                    }
                ]
            });

        var progress = new List<GroupWatchProgress>();
        await foreach (var item in sut.DiscoverAsync(userInput))
        {
            progress.Add(item);
        }

        // Search provider was called (web search is primary)
        await _searchProvider.Received().SearchAsync(
            Arg.Any<SearchQuery>(),
            Arg.Any<CancellationToken>());

        // LLM was called twice: intent parsing + classification
        llmCallCount.ShouldBe(2);

        // Portal from web search was discovered
        var portalsReady = progress.Single(item => item.Phase == GroupWatchPhase.PortalsReady);
        portalsReady.Portals!.Count.ShouldBeGreaterThanOrEqualTo(1);
        portalsReady.Portals.Select(p => p.Url).ShouldContain(searchResultUrl);
    }

    [Test]
    public async Task DiscoverAsync_NoSearchResults_FallsBackToCatalogOnly()
    {
        const string userInput = "scientist jobs in Copenhagen";
        var sut = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>jobs</body></html>")
        });

        _watchService.GetAllAsync(Arg.Any<CancellationToken>()).Returns([]);
        _searchProvider.IsAvailable.Returns(false);

        var llmCallCount = 0;

        _llmProviderChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                llmCallCount++;
                if (llmCallCount == 1)
                {
                    return new LlmResponse
                    {
                        IsSuccess = true,
                        Content = """
                            {
                              "location": "Copenhagen",
                              "roleTypes": ["scientist"],
                              "field": "biology",
                              "searchQueries": ["scientist jobs Copenhagen careers"]
                            }
                            """
                    };
                }

                // Classification — approve catalog entries
                return new LlmResponse
                {
                    IsSuccess = true,
                    Content = "[]"
                };
            });

        var progress = new List<GroupWatchProgress>();
        await foreach (var item in sut.DiscoverAsync(userInput))
        {
            progress.Add(item);
        }

        // With no search results and no catalog matches approved, should complete gracefully
        progress.ShouldContain(item => item.Phase == GroupWatchPhase.Searching);
        progress.Last().Phase.ShouldBe(GroupWatchPhase.Complete);
    }

    private static async IAsyncEnumerable<SetupProgress> ToAsyncEnumerable(params SetupProgress[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    private GroupWatchDiscoveryService CreateSut(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(new StubHttpMessageHandler(responder)));

        var setupFlowEnhancements = new SetupFlowEnhancements(CreateLogger<SetupFlowEnhancements>(), _httpClientFactory);
        return new GroupWatchDiscoveryService(
            _multiSearch,
            _llmProviderChain,
            _watchGroupService,
            _watchService,
            setupFlowEnhancements,
            _composableSetupPipeline,
            _httpClientFactory,
            CreateLogger<GroupWatchDiscoveryService>(),
            Options.Create(new GroupWatchDiscoveryOptions
            {
                MaxPortalsPerGroup = 15,
                MaxSearchQueries = 4,
                MaxSearchResultsPerQuery = 8
            }));
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
