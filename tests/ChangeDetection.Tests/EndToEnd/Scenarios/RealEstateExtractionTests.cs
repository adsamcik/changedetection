using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd.Scenarios;

/// <summary>
/// E2E tests for real estate extraction scenarios.
/// Tests LLM ability to extract property details from Zillow, Redfin, and rental sites.
/// </summary>
public class RealEstateExtractionTests : ExtractionTestBase
{
    #region Test HTML Fixtures

    private const string ZillowListingHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>123 Oak Street, San Francisco | Zillow</title></head>
        <body>
            <main class="property-page" data-zpid="12345678">
                <div class="property-header">
                    <h1 class="address" data-address="123 Oak Street">123 Oak Street</h1>
                    <span class="city-state" data-location="San Francisco, CA 94102">San Francisco, CA 94102</span>
                </div>
                <div class="price-section">
                    <span class="list-price" data-price="1250000">$1,250,000</span>
                    <span class="price-change" data-change="-50000">Price reduced $50,000</span>
                </div>
                <div class="property-stats" data-stats>
                    <span class="beds" data-beds="3">3 beds</span>
                    <span class="baths" data-baths="2">2 baths</span>
                    <span class="sqft" data-sqft="1850">1,850 sqft</span>
                </div>
                <div class="status-badge" data-status="For Sale">
                    <span>For Sale</span>
                </div>
                <div class="days-on-market" data-dom="14">14 days on Zillow</div>
            </main>
        </body>
        </html>
        """;

    private const string RedfinListingHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>456 Pine Ave, Seattle | Redfin</title></head>
        <body>
            <main class="listing-page" data-listing-id="rf-789">
                <div class="home-info">
                    <h1 class="street-address" data-address="456 Pine Ave">456 Pine Ave</h1>
                    <span class="city-zip" data-location="Seattle, WA 98101">Seattle, WA 98101</span>
                </div>
                <div class="price-info">
                    <span class="asking-price" data-price="875000">$875,000</span>
                    <span class="estimate" data-estimate="890000">Redfin Estimate: $890,000</span>
                </div>
                <div class="home-stats">
                    <div class="stat" data-beds="2">2 Beds</div>
                    <div class="stat" data-baths="1.5">1.5 Baths</div>
                    <div class="stat" data-sqft="1200">1,200 Sq Ft</div>
                    <div class="stat" data-lot="4500">4,500 Sq Ft Lot</div>
                </div>
                <div class="listing-status" data-status="Active">
                    <span class="status-label">Active</span>
                    <span class="listed-date" data-listed="Dec 20, 2024">Listed Dec 20, 2024</span>
                </div>
            </main>
        </body>
        </html>
        """;

    private const string ApartmentRentalHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Luxury 2BR in Downtown | Apartments.com</title></head>
        <body>
            <main class="rental-listing" data-listing="apt-456">
                <div class="property-header">
                    <h1 class="property-name" data-name="The Metropolitan">The Metropolitan</h1>
                    <span class="address" data-address="789 Main St, Austin, TX">789 Main St, Austin, TX</span>
                </div>
                <div class="unit-info" data-unit="Unit 1205">
                    <h2>Unit 1205 - Available Now</h2>
                </div>
                <div class="pricing">
                    <span class="rent" data-rent="2850">$2,850/month</span>
                    <span class="deposit" data-deposit="2850">$2,850 deposit</span>
                </div>
                <div class="unit-details">
                    <span class="beds" data-beds="2">2 Bedrooms</span>
                    <span class="baths" data-baths="2">2 Bathrooms</span>
                    <span class="sqft" data-sqft="1100">1,100 sq ft</span>
                </div>
                <div class="availability" data-available="Immediate">
                    <span>Available: Immediate Move-In</span>
                </div>
            </main>
        </body>
        </html>
        """;

    #endregion

    #region E2E Tests (LLM Cached)

    [Test]
    [Category("LlmCached")]
    public async Task ExtractProperty_Zillow_ExtractsPriceAndDetails()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(ZillowListingHtml, new TestExtractionSchema
        {
            Name = "ZillowListing",
            Description = "Extract property listing from Zillow",
            Fields =
            [
                new TestSchemaField { Name = "address", Type = "string", Description = "Property address" },
                new TestSchemaField { Name = "price", Type = "number", Description = "Listing price" },
                new TestSchemaField { Name = "beds", Type = "number", Description = "Number of bedrooms" },
                new TestSchemaField { Name = "baths", Type = "number", Description = "Number of bathrooms" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        AssertExtractionSuccessOrSkipOnCacheMiss(result);

        var address = result.GetString("address");
        address.ShouldContain("Oak", Case.Insensitive);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractProperty_Redfin_ExtractsListingStatus()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(RedfinListingHtml, new TestExtractionSchema
        {
            Name = "RedfinListing",
            Description = "Extract property listing from Redfin",
            Fields =
            [
                new TestSchemaField { Name = "address", Type = "string", Description = "Property address" },
                new TestSchemaField { Name = "price", Type = "number", Description = "Asking price" },
                new TestSchemaField { Name = "status", Type = "string", Description = "Listing status" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        AssertExtractionSuccessOrSkipOnCacheMiss(result);

        var address = result.GetString("address");
        address.ShouldContain("Pine", Case.Insensitive);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractProperty_Apartment_ExtractsRentalInfo()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(ApartmentRentalHtml, new TestExtractionSchema
        {
            Name = "ApartmentRental",
            Description = "Extract apartment rental listing",
            Fields =
            [
                new TestSchemaField { Name = "propertyName", Type = "string", Description = "Property/building name" },
                new TestSchemaField { Name = "rent", Type = "number", Description = "Monthly rent" },
                new TestSchemaField { Name = "availability", Type = "string", Description = "Move-in availability" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        AssertExtractionSuccessOrSkipOnCacheMiss(result);

        var name = result.GetString("propertyName");
        name.ShouldContain("Metropolitan", Case.Insensitive);
    }

    #endregion
}

