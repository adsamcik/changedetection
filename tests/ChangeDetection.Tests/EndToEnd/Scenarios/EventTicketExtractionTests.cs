using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd.Scenarios;

/// <summary>
/// E2E tests for event ticket extraction scenarios.
/// Tests LLM ability to extract ticket availability from concerts, sports, and theater.
/// </summary>
public class EventTicketExtractionTests : ExtractionTestBase
{
    #region Test HTML Fixtures

    private const string ConcertTicketsHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Taylor Swift | The Eras Tour | Ticketmaster</title></head>
        <body>
            <main class="event-page" data-event-id="evt-ts-2025">
                <div class="event-header">
                    <h1 class="event-title" data-artist="Taylor Swift">Taylor Swift</h1>
                    <h2 class="tour-name" data-tour="The Eras Tour">The Eras Tour</h2>
                </div>
                <div class="event-details">
                    <div class="venue-info">
                        <span class="venue-name" data-venue="SoFi Stadium">SoFi Stadium</span>
                        <span class="venue-location" data-location="Los Angeles, CA">Los Angeles, CA</span>
                    </div>
                    <div class="date-time" data-date="2025-08-15" data-time="19:30">
                        <span>August 15, 2025 at 7:30 PM</span>
                    </div>
                </div>
                <div class="ticket-sections" data-sections>
                    <div class="section" data-section="Floor" data-available="true">
                        <span class="section-name">Floor</span>
                        <span class="price-range" data-min="450" data-max="850">$450 - $850</span>
                        <span class="availability low">Limited</span>
                    </div>
                    <div class="section" data-section="Lower Bowl" data-available="true">
                        <span class="section-name">Lower Bowl</span>
                        <span class="price-range" data-min="250" data-max="450">$250 - $450</span>
                        <span class="availability">Available</span>
                    </div>
                    <div class="section" data-section="Upper Bowl" data-available="true">
                        <span class="section-name">Upper Bowl</span>
                        <span class="price-range" data-min="95" data-max="195">$95 - $195</span>
                        <span class="availability good">Good Availability</span>
                    </div>
                </div>
            </main>
        </body>
        </html>
        """;

    private const string SportsTicketsHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Lakers vs Warriors | NBA Tickets</title></head>
        <body>
            <main class="game-page" data-game-id="nba-2025-01-20">
                <div class="matchup">
                    <div class="team away" data-team="Golden State Warriors">
                        <span class="team-name">Golden State Warriors</span>
                    </div>
                    <span class="vs">@</span>
                    <div class="team home" data-team="Los Angeles Lakers">
                        <span class="team-name">Los Angeles Lakers</span>
                    </div>
                </div>
                <div class="game-info">
                    <span class="venue" data-venue="Crypto.com Arena">Crypto.com Arena</span>
                    <span class="datetime" data-date="2025-01-20" data-time="19:30">Mon, Jan 20 • 7:30 PM</span>
                </div>
                <div class="tickets-available" data-tickets>
                    <div class="ticket-row" data-section="Courtside" data-price="2500" data-qty="4">
                        <span class="section">Courtside</span>
                        <span class="price">$2,500</span>
                        <span class="qty">4 tickets</span>
                    </div>
                    <div class="ticket-row" data-section="Section 101" data-price="350" data-qty="2">
                        <span class="section">Section 101, Row A</span>
                        <span class="price">$350 each</span>
                        <span class="qty">2 tickets</span>
                    </div>
                </div>
            </main>
        </body>
        </html>
        """;

    private const string BroadwayTicketsHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Hamilton | Broadway Tickets</title></head>
        <body>
            <main class="show-page" data-show="hamilton">
                <div class="show-header">
                    <h1 class="show-title" data-title="Hamilton">Hamilton</h1>
                    <div class="show-info">
                        <span class="theater" data-theater="Richard Rodgers Theatre">Richard Rodgers Theatre</span>
                        <span class="location">New York, NY</span>
                    </div>
                </div>
                <div class="performance-selector" data-performances>
                    <div class="performance selected" data-date="2025-02-14" data-time="20:00">
                        <span class="date">Feb 14</span>
                        <span class="time">8:00 PM</span>
                    </div>
                </div>
                <div class="seat-map" data-seating>
                    <div class="section" data-section="Orchestra" data-status="limited">
                        <span class="name">Orchestra</span>
                        <span class="price-range">$299 - $549</span>
                        <span class="status">Few Left</span>
                    </div>
                    <div class="section" data-section="Mezzanine" data-status="available">
                        <span class="name">Mezzanine</span>
                        <span class="price-range">$199 - $349</span>
                        <span class="status">Available</span>
                    </div>
                </div>
            </main>
        </body>
        </html>
        """;

    #endregion

    #region E2E Tests (LLM Cached)

    [Test]
    [Category("LlmCached")]
    public async Task ExtractTickets_Concert_ExtractsVenueAndSections()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(ConcertTicketsHtml, new TestExtractionSchema
        {
            Name = "ConcertTickets",
            Description = "Extract concert ticket availability",
            Fields =
            [
                new TestSchemaField { Name = "artist", Type = "string", Description = "Artist name" },
                new TestSchemaField { Name = "venue", Type = "string", Description = "Venue name" },
                new TestSchemaField { Name = "date", Type = "string", Description = "Event date" },
                new TestSchemaField { Name = "sections", Type = "array", Description = "Ticket sections with prices" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        result.IsSuccess.ShouldBeTrue($"Extraction failed: {result.Error}");

        var artist = result.GetString("artist");
        artist.ShouldContain("Taylor Swift", Case.Insensitive);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractTickets_Sports_ExtractsMatchupAndPrices()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(SportsTicketsHtml, new TestExtractionSchema
        {
            Name = "SportsTickets",
            Description = "Extract sports game ticket info",
            Fields =
            [
                new TestSchemaField { Name = "homeTeam", Type = "string", Description = "Home team" },
                new TestSchemaField { Name = "awayTeam", Type = "string", Description = "Away team" },
                new TestSchemaField { Name = "venue", Type = "string", Description = "Stadium/arena" },
                new TestSchemaField { Name = "tickets", Type = "array", Description = "Available tickets" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        result.IsSuccess.ShouldBeTrue($"Extraction failed: {result.Error}");

        var homeTeam = result.GetString("homeTeam");
        homeTeam.ShouldContain("Lakers", Case.Insensitive);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractTickets_Broadway_ExtractsShowAndSeating()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(BroadwayTicketsHtml, new TestExtractionSchema
        {
            Name = "BroadwayTickets",
            Description = "Extract Broadway show ticket info",
            Fields =
            [
                new TestSchemaField { Name = "showTitle", Type = "string", Description = "Show name" },
                new TestSchemaField { Name = "theater", Type = "string", Description = "Theater name" },
                new TestSchemaField { Name = "sections", Type = "array", Description = "Seating sections" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        result.IsSuccess.ShouldBeTrue($"Extraction failed: {result.Error}");

        var showTitle = result.GetString("showTitle");
        showTitle.ShouldContain("Hamilton", Case.Insensitive);
    }

    #endregion
}

