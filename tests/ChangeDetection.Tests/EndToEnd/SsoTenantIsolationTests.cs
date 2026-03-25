using System.Collections.Concurrent;
using System.Net.Http.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services;
using ChangeDetection.Services.Authentication;
using ChangeDetection.Services.Persistence;
using ChangeDetection.Shared.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd;

public class SsoTenantIsolationTests : IAsyncDisposable
{
    private SsoFixture _fixture = null!;

    [Before(Test)]
    public Task SetUp()
    {
        _fixture = new SsoFixture();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _fixture?.Dispose();
        return ValueTask.CompletedTask;
    }

    [Test]
    public async Task WatchesList_OutputCache_DoesNotLeakAcrossUsers()
    {
        var url = "https://example.com/cache-isolation-" + Guid.NewGuid().ToString("N")[..8];
        _fixture.Fetcher.SetHtml(url, "<html><body><div>v1</div></body></html>");

        using var clientA = _fixture.CreateClientForUser("alice");
        using var clientB = _fixture.CreateClientForUser("bob");

        // Create watch as Alice (also triggers initial check)
        var createResponse = await clientA.PostAsJsonAsync("/api/watches", new WatchCreateDto
        {
            Url = url,
            Title = "Alice watch",
            CheckInterval = TimeSpan.FromMinutes(60)
        });
        var createContent = await createResponse.Content.ReadAsStringAsync();
        createResponse.IsSuccessStatusCode.ShouldBeTrue(createContent);

        // Prime cache for /api/watches
        var aliceWatches = await clientA.GetFromJsonAsync<List<WatchListItemDto>>("/api/watches");
        aliceWatches.ShouldNotBeNull();
        aliceWatches.ShouldContain(w => w.Url == url);

        // Bob must not see Alice's watch (this would fail if OutputCache is not varied by user)
        var bobWatches = await clientB.GetFromJsonAsync<List<WatchListItemDto>>("/api/watches");
        bobWatches.ShouldNotBeNull();
        bobWatches.ShouldNotContain(w => w.Url == url);
    }

    [Test]
    public async Task BackgroundScope_CheckPersistsEventsVisibleToWatchOwner()
    {
        var url = "https://example.com/background-ownership";
        _fixture.Fetcher.SetHtml(url, "<html><body><div>v1</div></body></html>");

        using var clientA = _fixture.CreateClientForUser("alice");

        // Create watch as Alice (initial snapshot)
        var createResponse = await clientA.PostAsJsonAsync("/api/watches", new WatchCreateDto
        {
            Url = url,
            Title = "Alice background watch",
            CheckInterval = TimeSpan.FromMinutes(60)
        });
        createResponse.IsSuccessStatusCode.ShouldBeTrue(await createResponse.Content.ReadAsStringAsync());

        var createdWatch = await createResponse.Content.ReadFromJsonAsync<WatchDetailDto>();
        createdWatch.ShouldNotBeNull();

        // Change content and run a check under the background-service scope (admin context)
        _fixture.Fetcher.SetHtml(url, "<html><body><div>v2</div></body></html>");

        using (var bgScope = _fixture.Services.GetRequiredService<IBackgroundServiceScopeFactory>().CreateBackgroundScope())
        {
            var watchService = bgScope.ServiceProvider.GetRequiredService<IWatchService>();
            var change = await watchService.CheckForChangesAsync(Guid.Parse(createdWatch.Id));
            change.ShouldNotBeNull("Background check should detect the change");
        }

        // Alice should be able to query the resulting change event via tenant-scoped endpoints
        var changes = await clientA.GetFromJsonAsync<List<ChangeListItemDto>>($"/api/changes?watchId={createdWatch.Id}&limit=10");
        changes.ShouldNotBeNull();
        changes.ShouldNotBeEmpty();
    }

    public sealed class SsoFixture : IDisposable
    {
        private readonly WebApplicationFactory<Program> _factory;
        private readonly string _dbPath;

        public IServiceProvider Services => _factory.Services;
        public MutableContentFetcher Fetcher { get; }

        public SsoFixture()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"changedetection-tests-{Guid.NewGuid():N}.db");
            Fetcher = new MutableContentFetcher();

            _factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Testing");

                    builder.ConfigureAppConfiguration((_, config) =>
                    {
                        config.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["LiteDb:Path"] = _dbPath,
                            ["Authentication:Mode"] = "SSO",
                            ["Authentication:UsernameHeader"] = "Remote-User",
                            ["Authentication:GroupsHeader"] = "Remote-Groups",
                            ["Authentication:AdminGroup"] = "changedetection-admins"
                        });
                    });

                    builder.ConfigureTestServices(services =>
                    {
                        // Override IUserContext and IUserService to use SSO mode - the configuration override
                        // happens after Program.cs has already registered SingleUserContext and SingleUserModeUserService
                        services.RemoveAll<IUserContext>();
                        services.AddScoped<IUserContext, SsoUserContext>();
                        
                        services.RemoveAll<IUserService>();
                        services.AddScoped<IUserService, UserService>();
                        
                        // Register user repository for SSO user management
                        services.AddScoped<IRepository<User>>(sp =>
                            new LiteDbRepository<User>(new ThreadSafeLiteDbContext(sp.GetRequiredService<LiteDbContext>()), "users"));
                        
                        // Remove hosted services to keep tests deterministic (no background checks, no seeders).
                        services.RemoveAll<IHostedService>();

                        // Override fetcher with a deterministic, mutable in-memory implementation.
                        services.RemoveAll<IContentFetcher>();
                        services.AddSingleton<IContentFetcher>(Fetcher);

                        // Ensure change analysis doesn't attempt any real LLM calls during tests.
                        var analyzer = Substitute.For<IChangeAnalyzer>();
                        analyzer.AnalyzeChangeAsync(Arg.Any<ChangeAnalysisRequest>(), Arg.Any<CancellationToken>())
                            .Returns(new ChangeAnalysisResult { IsSuccess = false, ErrorMessage = "LLM disabled in tests" });
                        analyzer.AnalyzeChangeStreamingAsync(Arg.Any<ChangeAnalysisRequest>(), Arg.Any<CancellationToken>())
                            .Returns(AsyncEnumerable.Empty<ChangeAnalysisProgress>());
                        analyzer.DetectAnomaliesAsync(Arg.Any<AnomalyDetectionRequest>(), Arg.Any<CancellationToken>())
                            .Returns(new AnomalyDetectionResult { HasAnomalies = false, AnomalyScore = 0, Anomalies = [] });
                        services.RemoveAll<IChangeAnalyzer>();
                        services.AddSingleton(analyzer);

                        var enricher = Substitute.For<IContentEnricher>();
                        enricher.EnrichContentAsync(Arg.Any<ContentEnrichmentRequest>(), Arg.Any<CancellationToken>())
                            .Returns(new ContentEnrichmentResult { IsSuccess = false, ErrorMessage = "LLM disabled in tests" });
                        enricher.QuickClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                            .Returns(new QuickClassificationResult
                            {
                                ContentType = "unknown",
                                Language = "en",
                                HasStructuredData = false,
                                IsTimeSensitive = false,
                                Confidence = 0
                            });
                        enricher.GenerateFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                            .Returns(new ContentFingerprint
                            {
                                SemanticHash = "test-fingerprint",
                                KeyTopics = [],
                                KeyEntities = [],
                                StructureSignature = null
                            });
                        services.RemoveAll<IContentEnricher>();
                        services.AddSingleton(enricher);
                    });
                });
        }

        public HttpClient CreateClientForUser(string username, string? groups = null)
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("Remote-User", username);
            if (!string.IsNullOrWhiteSpace(groups))
            {
                client.DefaultRequestHeaders.Add("Remote-Groups", groups);
            }
            return client;
        }

        public void Dispose()
        {
            _factory.Dispose();
            try
            {
                if (File.Exists(_dbPath))
                {
                    File.Delete(_dbPath);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    public sealed class MutableContentFetcher : IContentFetcher
    {
        private readonly ConcurrentDictionary<string, string> _htmlByUrl = new(StringComparer.OrdinalIgnoreCase);

        public void SetHtml(string url, string html)
        {
            _htmlByUrl[url] = html;
        }

        public Task<FetchResult> FetchAsync(string url, FetchOptions options, CancellationToken ct = default)
        {
            var html = _htmlByUrl.TryGetValue(url, out var stored)
                ? stored
                : "<html><body><div>default</div></body></html>";

            return Task.FromResult(new FetchResult
            {
                IsSuccess = true,
                Html = html,
                HttpStatusCode = 200,
                DurationMs = 1
            });
        }
    }
}




