using System.Collections.Concurrent;
using ChangeDetection.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ChangeDetection.Services.Persistence;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace ChangeDetection.Tests.EndToEnd;

/// <summary>
/// Lightweight WebApplicationFactory for endpoint integration tests.
/// Uses an isolated LiteDB per test, removes hosted services and external dependencies.
/// </summary>
public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath;

    public TestWebApplicationFactory()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cd-test-{Guid.NewGuid():N}.db");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LiteDb:Path"] = _dbPath
            });
        });

        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Replace LiteDbContext with isolated test database
            services.RemoveAll<LiteDbContext>();
            services.AddSingleton(new LiteDbContext($"Filename={_dbPath};Connection=shared"));

            // Remove hosted services to keep tests deterministic
            services.RemoveAll<IHostedService>();

            // Replace IContentFetcher with a no-op stub
            services.RemoveAll<IContentFetcher>();
            services.AddSingleton<IContentFetcher>(new StubContentFetcher());

            // Stub IChangeAnalyzer to avoid LLM calls
            var analyzer = Substitute.For<IChangeAnalyzer>();
            analyzer.AnalyzeChangeAsync(Arg.Any<ChangeAnalysisRequest>(), Arg.Any<CancellationToken>())
                .Returns(new ChangeAnalysisResult { IsSuccess = false, ErrorMessage = "LLM disabled in tests" });
            analyzer.AnalyzeChangeStreamingAsync(Arg.Any<ChangeAnalysisRequest>(), Arg.Any<CancellationToken>())
                .Returns(AsyncEnumerable.Empty<ChangeAnalysisProgress>());
            analyzer.DetectAnomaliesAsync(Arg.Any<AnomalyDetectionRequest>(), Arg.Any<CancellationToken>())
                .Returns(new AnomalyDetectionResult { HasAnomalies = false, AnomalyScore = 0, Anomalies = [] });
            services.RemoveAll<IChangeAnalyzer>();
            services.AddSingleton(analyzer);

            // Stub IContentEnricher to avoid LLM calls
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
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            // LiteDB may create journal files
            var journal = _dbPath + "-journal";
            if (File.Exists(journal)) File.Delete(journal);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    /// <summary>
    /// Minimal IContentFetcher that returns static HTML.
    /// </summary>
    private sealed class StubContentFetcher : IContentFetcher
    {
        public Task<FetchResult> FetchAsync(string url, FetchOptions options, CancellationToken ct = default)
        {
            return Task.FromResult(new FetchResult
            {
                IsSuccess = true,
                Html = "<html><body><div>stub</div></body></html>",
                HttpStatusCode = 200,
                DurationMs = 1
            });
        }
    }
}
