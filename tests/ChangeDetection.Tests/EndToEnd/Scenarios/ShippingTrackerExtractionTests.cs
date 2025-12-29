using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd.Scenarios;

/// <summary>
/// E2E tests for shipping/tracking extraction scenarios.
/// Tests LLM ability to extract delivery status from UPS, FedEx, and Amazon.
/// </summary>
public class ShippingTrackerExtractionTests : ExtractionTestBase
{
    #region Test HTML Fixtures

    private const string UpsTrackingHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>UPS Tracking | 1Z999AA10123456784</title></head>
        <body>
            <main class="tracking-page" data-tracking="1Z999AA10123456784">
                <div class="package-header">
                    <h1>Tracking Number: <span data-tracking-number="1Z999AA10123456784">1Z999AA10123456784</span></h1>
                </div>
                <div class="delivery-status" data-status="In Transit">
                    <span class="status-icon">🚚</span>
                    <span class="status-text">In Transit</span>
                </div>
                <div class="delivery-estimate">
                    <span class="label">Scheduled Delivery:</span>
                    <span class="date" data-delivery-date="January 18, 2025">Saturday, January 18, 2025</span>
                    <span class="time" data-delivery-time="by end of day">by end of day</span>
                </div>
                <div class="shipment-progress" data-events>
                    <div class="event latest" data-event="1" data-timestamp="2025-01-16T14:30:00Z">
                        <span class="time">Jan 16, 2:30 PM</span>
                        <span class="location" data-location="Phoenix, AZ">Phoenix, AZ</span>
                        <span class="description">Departed facility</span>
                    </div>
                    <div class="event" data-event="2" data-timestamp="2025-01-16T08:15:00Z">
                        <span class="time">Jan 16, 8:15 AM</span>
                        <span class="location" data-location="Phoenix, AZ">Phoenix, AZ</span>
                        <span class="description">Arrived at facility</span>
                    </div>
                </div>
            </main>
        </body>
        </html>
        """;

    private const string FedExDelayedHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>FedEx Tracking | 794644790301</title></head>
        <body>
            <main class="track-results" data-tracking="794644790301">
                <div class="tracking-header">
                    <span class="tracking-number" data-number="794644790301">794644790301</span>
                </div>
                <div class="status-alert delayed" data-status="Delayed">
                    <span class="alert-icon">⚠️</span>
                    <span class="alert-text">Shipment Delayed</span>
                </div>
                <div class="delivery-info">
                    <div class="original-date">
                        <span class="label">Original Delivery:</span>
                        <span data-original="January 16, 2025">January 16, 2025</span>
                    </div>
                    <div class="updated-date">
                        <span class="label">Updated Delivery:</span>
                        <span data-updated="January 19, 2025">January 19, 2025</span>
                    </div>
                    <div class="delay-reason" data-reason="Weather delay">
                        <span>Reason: Weather conditions causing delays in the area</span>
                    </div>
                </div>
                <div class="current-location" data-location="Memphis, TN">
                    <span>Currently at: Memphis, TN Hub</span>
                </div>
            </main>
        </body>
        </html>
        """;

    private const string AmazonOrderHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Track Package | Amazon</title></head>
        <body>
            <main class="order-tracking" data-order="111-2345678-9012345">
                <div class="order-header">
                    <h1>Your Package</h1>
                    <span class="order-id" data-order-id="111-2345678-9012345">Order #111-2345678-9012345</span>
                </div>
                <div class="delivery-status" data-status="Out for delivery">
                    <div class="status-badge out-for-delivery">
                        <span class="icon">📦</span>
                        <span class="text">Out for delivery</span>
                    </div>
                </div>
                <div class="delivery-window">
                    <span class="arriving" data-arriving="Today by 8 PM">Arriving today by 8 PM</span>
                </div>
                <div class="tracking-events" data-events>
                    <div class="event" data-time="7:30 AM">
                        <span class="time">7:30 AM</span>
                        <span class="desc">Out for delivery</span>
                    </div>
                    <div class="event" data-time="5:15 AM">
                        <span class="time">5:15 AM</span>
                        <span class="desc">Arrived at delivery station - Seattle, WA</span>
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
    public async Task ExtractTracking_UPS_ExtractsDeliveryStatus()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(UpsTrackingHtml, new TestExtractionSchema
        {
            Name = "UPSTracking",
            Description = "Extract UPS package tracking info",
            Fields =
            [
                new TestSchemaField { Name = "trackingNumber", Type = "string", Description = "Tracking number" },
                new TestSchemaField { Name = "status", Type = "string", Description = "Current status" },
                new TestSchemaField { Name = "deliveryDate", Type = "string", Description = "Expected delivery date" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        result.IsSuccess.ShouldBeTrue($"Extraction failed: {result.Error}");

        var status = result.GetString("status");
        status.ShouldContain("Transit", Case.Insensitive);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractTracking_FedEx_ExtractsDelayInfo()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(FedExDelayedHtml, new TestExtractionSchema
        {
            Name = "FedExTracking",
            Description = "Extract FedEx tracking with delay info",
            Fields =
            [
                new TestSchemaField { Name = "trackingNumber", Type = "string", Description = "Tracking number" },
                new TestSchemaField { Name = "status", Type = "string", Description = "Current status" },
                new TestSchemaField { Name = "delayReason", Type = "string", Description = "Reason for delay" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        result.IsSuccess.ShouldBeTrue($"Extraction failed: {result.Error}");

        var status = result.GetString("status");
        status.ShouldContain("Delay", Case.Insensitive);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractTracking_Amazon_ExtractsDeliveryWindow()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(AmazonOrderHtml, new TestExtractionSchema
        {
            Name = "AmazonTracking",
            Description = "Extract Amazon order tracking",
            Fields =
            [
                new TestSchemaField { Name = "orderId", Type = "string", Description = "Order ID" },
                new TestSchemaField { Name = "status", Type = "string", Description = "Delivery status" },
                new TestSchemaField { Name = "deliveryWindow", Type = "string", Description = "Expected delivery time" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        result.IsSuccess.ShouldBeTrue($"Extraction failed: {result.Error}");

        var status = result.GetString("status");
        status.ShouldContain("delivery", Case.Insensitive);
    }

    #endregion
}

