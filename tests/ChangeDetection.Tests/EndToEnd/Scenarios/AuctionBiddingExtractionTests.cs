using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd.Scenarios;

/// <summary>
/// E2E tests for auction/bidding extraction scenarios.
/// Tests LLM ability to extract auction info from eBay, real estate, and art auctions.
/// </summary>
public class AuctionBiddingExtractionTests : ExtractionTestBase
{
    #region Test HTML Fixtures

    private const string EbayAuctionHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Vintage Rolex Submariner | eBay Auction</title></head>
        <body>
            <main class="listing-page" data-item-id="123456789012">
                <div class="item-header">
                    <h1 class="item-title" data-title="Vintage Rolex Submariner 5513 1969">Vintage Rolex Submariner 5513 1969</h1>
                    <span class="condition" data-condition="Pre-owned">Condition: Pre-owned</span>
                </div>
                <div class="auction-info" data-auction>
                    <div class="current-bid">
                        <span class="label">Current bid:</span>
                        <span class="amount" data-bid="12500">$12,500.00</span>
                    </div>
                    <div class="bid-count" data-bids="47">47 bids</div>
                    <div class="time-left" data-ends="2025-01-20T18:30:00Z">
                        <span class="label">Time left:</span>
                        <span class="countdown">2d 4h</span>
                    </div>
                </div>
                <div class="reserve-status" data-reserve="met">
                    <span class="reserve-met">Reserve met</span>
                </div>
                <div class="seller-info" data-seller>
                    <span class="seller-name" data-name="vintage_watches_nyc">vintage_watches_nyc</span>
                    <span class="feedback" data-rating="99.8%">99.8% positive</span>
                </div>
                <div class="shipping" data-shipping>
                    <span data-cost="Free">Free shipping</span>
                </div>
            </main>
        </body>
        </html>
        """;

    private const string RealEstateAuctionHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Luxury Estate Auction | Premium Properties</title></head>
        <body>
            <main class="auction-page" data-auction-id="re-2025-001">
                <div class="property-header">
                    <h1 class="property-title" data-title="Lakefront Estate - Lake Tahoe">Lakefront Estate - Lake Tahoe</h1>
                    <span class="address" data-address="500 Lakeshore Blvd, Tahoe City, CA">500 Lakeshore Blvd, Tahoe City, CA</span>
                </div>
                <div class="auction-status" data-status="Live">
                    <span class="status-badge live">LIVE AUCTION</span>
                </div>
                <div class="bidding-info" data-bidding>
                    <div class="current-high">
                        <span class="label">Current High Bid:</span>
                        <span class="amount" data-bid="4250000">$4,250,000</span>
                    </div>
                    <div class="starting-bid">
                        <span class="label">Opening Bid:</span>
                        <span class="amount" data-opening="3500000">$3,500,000</span>
                    </div>
                    <div class="bid-increment" data-increment="50000">
                        <span>Minimum increment: $50,000</span>
                    </div>
                </div>
                <div class="auction-timing" data-timing>
                    <span class="ends" data-ends="January 25, 2025 at 5:00 PM PST">Ends: January 25, 2025 at 5:00 PM PST</span>
                </div>
                <div class="property-details" data-details>
                    <span data-beds="5">5 Bedrooms</span>
                    <span data-baths="4">4 Bathrooms</span>
                    <span data-sqft="6500">6,500 sq ft</span>
                </div>
            </main>
        </body>
        </html>
        """;

    private const string ArtAuctionHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Contemporary Art Auction | Christie's</title></head>
        <body>
            <main class="lot-page" data-lot="lot-2025-0042">
                <div class="lot-header">
                    <span class="lot-number" data-lot-number="42">Lot 42</span>
                    <h1 class="artwork-title" data-title="Untitled (Blue Horizon)">Untitled (Blue Horizon)</h1>
                    <span class="artist" data-artist="Elena Vasquez">Elena Vasquez</span>
                    <span class="year" data-year="2023">(2023)</span>
                </div>
                <div class="artwork-details">
                    <span class="medium" data-medium="Oil on canvas">Oil on canvas</span>
                    <span class="dimensions" data-size="72 x 96 inches">72 x 96 inches</span>
                </div>
                <div class="estimate" data-estimate>
                    <span class="label">Estimate:</span>
                    <span class="range" data-low="150000" data-high="200000">$150,000 - $200,000</span>
                </div>
                <div class="current-bid-info" data-bidding>
                    <span class="current-label">Current Bid:</span>
                    <span class="current-amount" data-bid="175000">$175,000</span>
                    <span class="bid-count" data-bids="8">8 bids</span>
                </div>
                <div class="auction-info">
                    <span class="sale-name" data-sale="Contemporary Art Evening Sale">Contemporary Art Evening Sale</span>
                    <span class="sale-date" data-date="February 15, 2025">February 15, 2025</span>
                    <span class="location" data-location="New York">New York</span>
                </div>
            </main>
        </body>
        </html>
        """;

    #endregion

    #region E2E Tests (LLM Cached)

    [Test]
    [Category("LlmCached")]
    public async Task ExtractAuction_Ebay_ExtractsBidInfo()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(EbayAuctionHtml, new TestExtractionSchema
        {
            Name = "EbayAuction",
            Description = "Extract eBay auction information",
            Fields =
            [
                new TestSchemaField { Name = "title", Type = "string", Description = "Item title" },
                new TestSchemaField { Name = "currentBid", Type = "number", Description = "Current bid amount" },
                new TestSchemaField { Name = "bidCount", Type = "number", Description = "Number of bids" },
                new TestSchemaField { Name = "reserveMet", Type = "boolean", Description = "Whether reserve is met" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        AssertExtractionSuccessOrSkipOnCacheMiss(result);

        var title = result.GetString("title");
        title.ShouldContain("Rolex", Case.Insensitive);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractAuction_RealEstate_ExtractsPropertyBid()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(RealEstateAuctionHtml, new TestExtractionSchema
        {
            Name = "RealEstateAuction",
            Description = "Extract real estate auction information",
            Fields =
            [
                new TestSchemaField { Name = "propertyTitle", Type = "string", Description = "Property title" },
                new TestSchemaField { Name = "currentBid", Type = "number", Description = "Current high bid" },
                new TestSchemaField { Name = "openingBid", Type = "number", Description = "Opening bid" },
                new TestSchemaField { Name = "auctionEnds", Type = "string", Description = "Auction end date/time" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        AssertExtractionSuccessOrSkipOnCacheMiss(result);

        var title = result.GetString("propertyTitle");
        title.ShouldContain("Tahoe", Case.Insensitive);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractAuction_Art_ExtractsLotInfo()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(ArtAuctionHtml, new TestExtractionSchema
        {
            Name = "ArtAuction",
            Description = "Extract art auction lot information",
            Fields =
            [
                new TestSchemaField { Name = "artworkTitle", Type = "string", Description = "Artwork title" },
                new TestSchemaField { Name = "artist", Type = "string", Description = "Artist name" },
                new TestSchemaField { Name = "estimateLow", Type = "number", Description = "Low estimate" },
                new TestSchemaField { Name = "estimateHigh", Type = "number", Description = "High estimate" },
                new TestSchemaField { Name = "currentBid", Type = "number", Description = "Current bid" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        AssertExtractionSuccessOrSkipOnCacheMiss(result);

        var artist = result.GetString("artist");
        artist.ShouldContain("Vasquez", Case.Insensitive);
    }

    #endregion
}

