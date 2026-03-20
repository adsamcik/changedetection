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
    public async Task DiscoverAsync_KnownPlatform_DeduplicatesByDomainAndCreatesWatch()
    {
        const string userInput = "I want to watch for research assistant jobs in Copenhagen biology";
        const string portalUrl = "https://company.wd3.myworkdayjobs.com/en-US/careers";
        const string workdayApiUrl = "https://company.wd3.myworkdayjobs.com/wday/cxs/company/careers/jobs";
        var sut = CreateSut(request => request switch
        {
            { Method.Method: "GET" } when request.RequestUri!.ToString() == portalUrl => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>careers</body></html>")
            },
            { Method.Method: "POST" } when request.RequestUri!.ToString() == workdayApiUrl => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"total":0,"jobPostings":[]}""")
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var parseJson = """
            {
              "location": "Copenhagen",
              "roleTypes": ["research assistant"],
              "field": "biology",
              "searchQueries": [
                "research assistant biology jobs Copenhagen career portal"
              ]
            }
            """;
        var filterJson = """
            [
              {
                "url": "https://company.wd3.myworkdayjobs.com/en-US/careers",
                "reasoning": "Workday career listing page for the company.",
                "title": "Company Careers"
              },
              {
                "url": "https://company.wd3.myworkdayjobs.com/en-US/careers/job/123",
                "reasoning": "Duplicate domain that should be removed.",
                "title": "Duplicate"
              }
            ]
            """;

        _llmProviderChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                new LlmResponse { IsSuccess = true, Content = parseJson },
                new LlmResponse { IsSuccess = true, Content = filterJson });

        _searchProvider.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResultSet
            {
                ProviderId = "test",
                Query = "research assistant biology jobs Copenhagen career portal",
                Results =
                [
                    new SearchResult
                    {
                        Url = "https://company.wd3.myworkdayjobs.com/en-US/careers",
                        Title = "Company Careers",
                        Snippet = "Workday career listing page",
                        Position = 1
                    }
                ]
            });

        var groupId = Guid.NewGuid();
        _watchGroupService.CreateGroupAsync(Arg.Any<WatchGroupCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new WatchGroup { Id = groupId, Name = "Career portals: research assistant — biology — Copenhagen" });

        var createdWatchId = Guid.NewGuid();
        _watchService.CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<CreateWatchRequest>();
                return new WatchedSite
                {
                    Id = createdWatchId,
                    Url = request.Url,
                    GroupId = request.GroupId,
                    Name = request.Name
                };
            });

        var progress = new List<GroupWatchProgress>();
        await foreach (var item in sut.DiscoverAsync(userInput))
        {
            progress.Add(item);
        }

        await _watchService.Received(1).CreateWatchAsync(
            Arg.Is<CreateWatchRequest>(request =>
                request.GroupId == groupId &&
                request.Url == "https://company.wd3.myworkdayjobs.com/en-US/careers" &&
                request.Tags!.Contains("jobs")),
            Arg.Any<CancellationToken>());

        await _watchService.Received(1).UpdateWatchAsync(
            Arg.Is<WatchedSite>(watch =>
                watch.Id == createdWatchId &&
                !string.IsNullOrWhiteSpace(watch.PipelineDefinitionJson) &&
                watch.FetchSettings.UseJavaScript == false),
            Arg.Any<CancellationToken>());

        progress.Last().Phase.ShouldBe(GroupWatchPhase.Complete);
        progress.Last().WatchIds.ShouldBe([createdWatchId]);
        progress.Last().Portals!.Count.ShouldBe(1);
    }

    [Test]
    public async Task DiscoverAsync_UnknownPortal_UsesComposablePipelineAndContinuesAfterFailure()
    {
        const string userInput = "I want to watch for research assistant jobs in Copenhagen biology";
        var sut = CreateSut(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>careers</body></html>")
        });
        var parseJson = """
            {
              "location": "Copenhagen",
              "roleTypes": ["research assistant"],
              "field": "biology",
              "searchQueries": [
                "research assistant biology jobs Copenhagen career portal"
              ]
            }
            """;
        var filterJson = """
            [
              {
                "url": "https://portal-one.example/jobs",
                "reasoning": "Independent job board for Copenhagen biology roles.",
                "title": "Portal One"
              },
              {
                "url": "https://portal-two.example/careers",
                "reasoning": "Second portal that will fail during pipeline generation.",
                "title": "Portal Two"
              }
            ]
            """;

        _llmProviderChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                new LlmResponse { IsSuccess = true, Content = parseJson },
                new LlmResponse { IsSuccess = true, Content = filterJson });

        _searchProvider.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResultSet
            {
                ProviderId = "test",
                Query = "research assistant biology jobs Copenhagen career portal",
                Results =
                [
                    new SearchResult
                    {
                        Url = "https://portal-one.example/jobs",
                        Title = "Portal One",
                        Snippet = "Careers",
                        Position = 1
                    },
                    new SearchResult
                    {
                        Url = "https://portal-two.example/careers",
                        Title = "Portal Two",
                        Snippet = "Careers",
                        Position = 2
                    }
                ]
            });

        var groupId = Guid.NewGuid();
        _watchGroupService.CreateGroupAsync(Arg.Any<WatchGroupCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new WatchGroup { Id = groupId, Name = "test-group" });

        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition
                {
                    Id = "navigate-1",
                    Type = "Navigate",
                    Position = 0,
                    Config = JsonSerializer.SerializeToElement(new { useJavaScript = true })
                },
                new BlockDefinition
                {
                    Id = "relevancescore-1",
                    Type = "RelevanceScore",
                    Position = 1,
                    Config = JsonSerializer.SerializeToElement(new { targetFields = new[] { "title" } })
                }
            ],
            Connections = [],
            Metadata = new PipelineMetadata
            {
                DisplayTitle = "Portal One Watch",
                UserIntent = userInput,
                CreatedAt = DateTime.UtcNow
            }
        };

        _composableSetupPipeline.StartSetupAsync(
                Arg.Is<SetupRequest>(request => request.UserInput.Contains("portal-one.example", StringComparison.OrdinalIgnoreCase)),
                Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(
                new SetupProgress
                {
                    Phase = SetupPhase.Checkpoint1,
                    Type = SetupProgressType.CheckpointReached,
                    Message = "checkpoint 1",
                    SessionId = "session-one"
                }));

        _composableSetupPipeline.ConfirmIntentAsync("session-one", true, null, Arg.Any<CancellationToken>())
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

        _composableSetupPipeline.ConfirmPipelineAsync("session-one", false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable());

        _composableSetupPipeline.StartSetupAsync(
                Arg.Is<SetupRequest>(request => request.UserInput.Contains("portal-two.example", StringComparison.OrdinalIgnoreCase)),
                Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(
                new SetupProgress
                {
                    Phase = SetupPhase.IntentParsing,
                    Type = SetupProgressType.Failed,
                    Message = "failed",
                    Error = "Could not parse portal"
                }));

        var createdWatchId = Guid.NewGuid();
        _watchService.CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new WatchedSite { Id = createdWatchId, Url = "https://portal-one.example/jobs" });

        var progress = new List<GroupWatchProgress>();
        await foreach (var item in sut.DiscoverAsync(userInput))
        {
            progress.Add(item);
        }

        await _watchService.Received(1).CreateWatchAsync(
            Arg.Is<CreateWatchRequest>(request =>
                request.Url == "https://portal-one.example/jobs" &&
                request.GroupId == groupId &&
                request.UseJavaScript),
            Arg.Any<CancellationToken>());

        await _watchService.Received(1).UpdateWatchAsync(
            Arg.Is<WatchedSite>(watch =>
                !string.IsNullOrWhiteSpace(watch.PipelineDefinitionJson) &&
                watch.FetchSettings.UseJavaScript),
            Arg.Any<CancellationToken>());

        progress.ShouldContain(item =>
            item.Phase == GroupWatchPhase.CreatingWatches &&
            item.Message.Contains("Skipped portal-two.example", StringComparison.OrdinalIgnoreCase));

        progress.Last().Phase.ShouldBe(GroupWatchPhase.Complete);
        progress.Last().WatchIds.ShouldBe([createdWatchId]);
    }

    [Test]
    public async Task DiscoverAsync_InvalidPortalUrl_SkipsWatchCreation()
    {
        const string userInput = "Watch biology jobs in Copenhagen";
        var sut = CreateSut(request => request.RequestUri!.Host switch
        {
            "dead.example" => new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("not found")
            },
            "live.example" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>jobs</body></html>")
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        _llmProviderChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                    {
                      "location": "Copenhagen",
                      "roleTypes": ["scientist"],
                      "field": "biology",
                      "searchQueries": []
                    }
                    """
            });

        _watchGroupService.CreateGroupAsync(Arg.Any<WatchGroupCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new WatchGroup { Id = Guid.NewGuid(), Name = "test-group" });

        _watchService.CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new WatchedSite { Id = Guid.NewGuid(), Url = "https://live.example/jobs" });

        _composableSetupPipeline.StartSetupAsync(Arg.Any<SetupRequest>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(
                new SetupProgress
                {
                    Phase = SetupPhase.Checkpoint1,
                    Type = SetupProgressType.CheckpointReached,
                    Message = "checkpoint 1",
                    SessionId = "session-live"
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
                DisplayTitle = "Live Portal Watch",
                UserIntent = userInput,
                CreatedAt = DateTime.UtcNow
            }
        };

        _composableSetupPipeline.ConfirmIntentAsync("session-live", true, null, Arg.Any<CancellationToken>())
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

        _composableSetupPipeline.ConfirmPipelineAsync("session-live", false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable());

        var progress = new List<GroupWatchProgress>();
        await foreach (var item in sut.DiscoverAsync(userInput))
        {
            progress.Add(item);
        }

        await _watchService.Received(1).CreateWatchAsync(
            Arg.Is<CreateWatchRequest>(request => request.Url == "https://live.example/jobs"),
            Arg.Any<CancellationToken>());

        await _watchService.DidNotReceive().CreateWatchAsync(
            Arg.Is<CreateWatchRequest>(request => request.Url == "https://dead.example/jobs"),
            Arg.Any<CancellationToken>());

        progress.ShouldContain(item =>
            item.Phase == GroupWatchPhase.CreatingWatches &&
            item.Message.Contains("Skipped dead.example", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task DiscoverAsync_LoginRequiredPortal_StillCreatesWatch()
    {
        const string userInput = "Watch biology jobs in Copenhagen";
        var sut = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("<html><body>Please sign in to continue</body></html>")
        });

        _llmProviderChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                    {
                      "location": "Copenhagen",
                      "roleTypes": ["scientist"],
                      "field": "biology",
                      "searchQueries": []
                    }
                    """
            });

        _watchGroupService.CreateGroupAsync(Arg.Any<WatchGroupCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new WatchGroup { Id = Guid.NewGuid(), Name = "test-group" });

        var createdWatchId = Guid.NewGuid();
        _watchService.CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new WatchedSite { Id = createdWatchId, Url = "https://login.example/jobs" });

        _composableSetupPipeline.StartSetupAsync(Arg.Any<SetupRequest>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(
                new SetupProgress
                {
                    Phase = SetupPhase.Checkpoint1,
                    Type = SetupProgressType.CheckpointReached,
                    Message = "checkpoint 1",
                    SessionId = "session-login"
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
                DisplayTitle = "Login Portal Watch",
                UserIntent = userInput,
                CreatedAt = DateTime.UtcNow
            }
        };

        _composableSetupPipeline.ConfirmIntentAsync("session-login", true, null, Arg.Any<CancellationToken>())
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

        _composableSetupPipeline.ConfirmPipelineAsync("session-login", false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable());

        var progress = new List<GroupWatchProgress>();
        await foreach (var item in sut.DiscoverAsync(userInput))
        {
            progress.Add(item);
        }

        await _watchService.Received(1).CreateWatchAsync(
            Arg.Is<CreateWatchRequest>(request => request.Url == "https://login.example/jobs"),
            Arg.Any<CancellationToken>());
        progress.Last().WatchIds.ShouldBe([createdWatchId]);
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
                          "searchQueries": ["biology jobs Copenhagen careers"],
                          "selectedIndices": [0],
                          "reasoning": {
                            "0": "Novo Nordisk careers is relevant"
                          }
                        }
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
