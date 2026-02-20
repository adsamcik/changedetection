using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd.Scenarios;

/// <summary>
/// E2E tests for release date extraction scenarios.
/// Tests LLM ability to extract release info for games, movies, and software.
/// </summary>
public class ReleaseDateExtractionTests : ExtractionTestBase
{
    #region Test HTML Fixtures

    private const string SteamGameHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Elden Ring: Shadow of the Erdtree on Steam</title></head>
        <body>
            <main class="game-page" data-appid="1245620">
                <div class="game-header">
                    <h1 class="game-title" data-title="Elden Ring: Shadow of the Erdtree">Elden Ring: Shadow of the Erdtree</h1>
                    <div class="developer" data-developer="FromSoftware">FromSoftware</div>
                    <div class="publisher" data-publisher="Bandai Namco">Bandai Namco</div>
                </div>
                <div class="release-info" data-release>
                    <span class="release-label">Release Date:</span>
                    <span class="release-date" data-date="June 21, 2024">June 21, 2024</span>
                </div>
                <div class="price-section">
                    <span class="price" data-price="39.99">$39.99</span>
                </div>
                <div class="tags" data-tags>
                    <span class="tag">Action RPG</span>
                    <span class="tag">Souls-like</span>
                    <span class="tag">DLC</span>
                </div>
                <div class="requirements" data-requirements>
                    <div class="minimum">
                        <h3>Minimum Requirements</h3>
                        <p>OS: Windows 10</p>
                        <p>Processor: Intel Core i5-8400</p>
                    </div>
                </div>
            </main>
        </body>
        </html>
        """;

    private const string MovieReleaseHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Dune: Part Three | Coming Soon</title></head>
        <body>
            <main class="movie-page" data-movie-id="dune-3">
                <div class="movie-header">
                    <h1 class="movie-title" data-title="Dune: Part Three">Dune: Part Three</h1>
                    <div class="director" data-director="Denis Villeneuve">Directed by Denis Villeneuve</div>
                </div>
                <div class="release-info" data-release>
                    <span class="coming-soon">Coming to Theaters</span>
                    <span class="release-date" data-date="December 18, 2026">December 18, 2026</span>
                </div>
                <div class="cast" data-cast>
                    <span class="actor" data-actor="Timothée Chalamet">Timothée Chalamet</span>
                    <span class="actor" data-actor="Zendaya">Zendaya</span>
                </div>
                <div class="synopsis" data-synopsis="The epic conclusion to the Dune saga">
                    <p>The epic conclusion to the Dune saga...</p>
                </div>
                <div class="formats" data-formats>
                    <span>IMAX</span>
                    <span>Dolby Cinema</span>
                    <span>Standard</span>
                </div>
            </main>
        </body>
        </html>
        """;

    private const string SoftwareReleaseHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Visual Studio 2025 Preview | Microsoft</title></head>
        <body>
            <main class="product-page" data-product="vs2025">
                <div class="product-header">
                    <h1 class="product-name" data-name="Visual Studio 2025">Visual Studio 2025</h1>
                    <span class="edition" data-edition="Preview">Preview</span>
                </div>
                <div class="release-status" data-status="Preview">
                    <span class="badge preview">Preview Available</span>
                </div>
                <div class="version-info">
                    <span class="version" data-version="17.12.0 Preview 1">Version 17.12.0 Preview 1</span>
                    <span class="build" data-build="35001.123">Build 35001.123</span>
                </div>
                <div class="release-schedule" data-schedule>
                    <div class="milestone" data-milestone="GA">
                        <span class="label">General Availability:</span>
                        <span class="date" data-date="Q2 2025">Q2 2025</span>
                    </div>
                </div>
                <div class="highlights" data-features>
                    <h2>What's New</h2>
                    <ul>
                        <li>AI-powered code completion</li>
                        <li>Enhanced .NET 10 support</li>
                        <li>Improved performance</li>
                    </ul>
                </div>
                <a class="download-btn" data-action="download" href="#">Download Preview</a>
            </main>
        </body>
        </html>
        """;

    #endregion

    #region E2E Tests (LLM Cached)

    [Test]
    [Category("LlmCached")]
    public async Task ExtractRelease_Game_ExtractsReleaseDate()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(SteamGameHtml, new TestExtractionSchema
        {
            Name = "GameRelease",
            Description = "Extract game release information",
            Fields =
            [
                new TestSchemaField { Name = "title", Type = "string", Description = "Game title" },
                new TestSchemaField { Name = "developer", Type = "string", Description = "Game developer" },
                new TestSchemaField { Name = "releaseDate", Type = "string", Description = "Release date" },
                new TestSchemaField { Name = "price", Type = "number", Description = "Price in USD" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        AssertExtractionSuccessOrSkipOnCacheMiss(result);

        var title = result.GetString("title");
        title.ShouldContain("Elden Ring", Case.Insensitive);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractRelease_Movie_ExtractsTheaterDate()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(MovieReleaseHtml, new TestExtractionSchema
        {
            Name = "MovieRelease",
            Description = "Extract movie release information",
            Fields =
            [
                new TestSchemaField { Name = "title", Type = "string", Description = "Movie title" },
                new TestSchemaField { Name = "director", Type = "string", Description = "Director name" },
                new TestSchemaField { Name = "releaseDate", Type = "string", Description = "Theatrical release date" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        AssertExtractionSuccessOrSkipOnCacheMiss(result);

        var title = result.GetString("title");
        title.ShouldContain("Dune", Case.Insensitive);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractRelease_Software_ExtractsVersionInfo()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(SoftwareReleaseHtml, new TestExtractionSchema
        {
            Name = "SoftwareRelease",
            Description = "Extract software release information",
            Fields =
            [
                new TestSchemaField { Name = "productName", Type = "string", Description = "Software name" },
                new TestSchemaField { Name = "version", Type = "string", Description = "Version number" },
                new TestSchemaField { Name = "releaseStatus", Type = "string", Description = "Release status (Preview/GA)" },
                new TestSchemaField { Name = "gaDate", Type = "string", Description = "General availability date" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        AssertExtractionSuccessOrSkipOnCacheMiss(result);

        var name = result.GetString("productName");
        name.ShouldContain("Visual Studio", Case.Insensitive);
    }

    #endregion
}

