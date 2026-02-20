using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd.Scenarios;

/// <summary>
/// E2E tests for appointment slot extraction scenarios.
/// Tests LLM ability to extract availability from visa, doctor, and restaurant systems.
/// </summary>
public class AppointmentSlotExtractionTests : ExtractionTestBase
{
    #region Test HTML Fixtures

    private const string VisaAppointmentHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <title>Available Appointments | US Visa Scheduling</title>
            <style>
                .slot-card { background: white; border-radius: 12px; padding: 25px; }
                .slot-date { font-size: 1.3rem; font-weight: 600; color: #1a365d; }
                .time-slot .spots.low { background: #fef3c7; color: #92400e; }
            </style>
        </head>
        <body>
            <header class="header">
                <h1>US Visa Appointment Scheduling</h1>
            </header>
            <main class="container">
                <div class="location-info" data-location>
                    <h3 data-consulate="US Embassy London">US Embassy London</h3>
                    <p data-visa-type="B1/B2 Tourist Visa">Visa Type: B1/B2 Tourist Visa</p>
                </div>
                <div class="slots-grid" data-appointments>
                    <div class="slot-card" data-date="2025-02-15" data-available="true">
                        <div class="slot-date">February 15, 2025</div>
                        <div class="slot-times">
                            <div class="time-slot" data-time="09:00" data-spots="2">
                                <span class="time">9:00 AM</span>
                                <span class="spots low">2 spots left</span>
                            </div>
                            <div class="time-slot" data-time="10:30" data-spots="1">
                                <span class="time">10:30 AM</span>
                                <span class="spots low">1 spot left</span>
                            </div>
                        </div>
                    </div>
                    <div class="slot-card" data-date="2025-02-22" data-available="true">
                        <div class="slot-date">February 22, 2025</div>
                        <div class="slot-times">
                            <div class="time-slot" data-time="08:00" data-spots="5">
                                <span class="time">8:00 AM</span>
                                <span class="spots">5 spots</span>
                            </div>
                        </div>
                    </div>
                </div>
            </main>
        </body>
        </html>
        """;

    private const string DoctorAppointmentHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <title>Book Appointment | HealthFirst Medical Group</title>
        </head>
        <body>
            <header class="header">
                <div class="logo">HealthFirst Medical Group</div>
            </header>
            <main class="container">
                <div class="providers-list" data-providers>
                    <div class="provider-card" data-provider="dr-chen">
                        <div class="provider-info">
                            <h3 data-name="Dr. Sarah Chen, MD">Dr. Sarah Chen, MD</h3>
                            <div class="specialty" data-specialty="Primary Care Physician">Primary Care Physician</div>
                            <div class="rating">
                                <span data-rating="4.9">4.9</span>
                                <span data-reviews="127">(127 reviews)</span>
                            </div>
                        </div>
                        <div class="availability-section" data-availability>
                            <div class="date-slots">
                                <div class="date-row" data-date="2025-01-16">
                                    <span class="date">Thu, Jan 16</span>
                                    <button class="time-btn" data-time="09:00">9:00 AM</button>
                                    <button class="time-btn" data-time="14:30">2:30 PM</button>
                                </div>
                            </div>
                        </div>
                    </div>
                    <div class="provider-card" data-provider="dr-patel">
                        <div class="provider-info">
                            <h3 data-name="Dr. Raj Patel, DO">Dr. Raj Patel, DO</h3>
                            <div class="specialty" data-specialty="Family Medicine">Family Medicine</div>
                        </div>
                    </div>
                </div>
            </main>
        </body>
        </html>
        """;

    private const string RestaurantReservationHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <title>Reservations | The Golden Fork</title>
        </head>
        <body>
            <header class="hero">
                <h1 data-restaurant="The Golden Fork">The Golden Fork</h1>
                <p class="tagline">Fine Dining Experience Since 1987</p>
            </header>
            <main class="container">
                <div class="date-selector" data-date-selector>
                    <button class="date-btn active" data-date="2025-01-18" data-selected="true">Sat, Jan 18</button>
                </div>
                <div class="party-size" data-party-size>
                    <select data-selected-size="2">
                        <option value="2" selected>2 Guests</option>
                    </select>
                </div>
                <div class="times-grid" data-times data-selected-date="2025-01-18">
                    <div class="time-slot unavailable" data-time="17:00" data-available="false">
                        <div class="time">5:00 PM</div>
                        <div class="status full">Fully Booked</div>
                    </div>
                    <div class="time-slot" data-time="18:00" data-available="true" data-tables-left="1">
                        <div class="time">6:00 PM</div>
                        <div class="status warning">1 table left</div>
                    </div>
                    <div class="time-slot" data-time="19:00" data-available="true" data-tables-left="4">
                        <div class="time">7:00 PM</div>
                        <div class="status">Available</div>
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
    public async Task ExtractAppointment_VisaBooking_ExtractsAvailableSlots()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(VisaAppointmentHtml, new TestExtractionSchema
        {
            Name = "VisaAppointments",
            Description = "Extract visa appointment availability",
            Fields =
            [
                new TestSchemaField { Name = "consulate", Type = "string", Description = "Consulate name" },
                new TestSchemaField { Name = "visaType", Type = "string", Description = "Type of visa" },
                new TestSchemaField { Name = "dates", Type = "array", Description = "Available appointment dates" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");
        TestContext.Current?.OutputWriter?.WriteLine($"Raw response: {result.RawResponse}");

        result.ShouldNotBeNull();
        AssertExtractionSuccessOrSkipOnCacheMiss(result);
        result.Data.ShouldNotBeNull();

        // Log extracted keys for debugging
        foreach (var kv in result.Data)
        {
            TestContext.Current?.OutputWriter?.WriteLine($"  {kv.Key} = {kv.Value}");
        }

        var consulate = result.GetString("consulate");
        consulate.ShouldNotBeNullOrEmpty("consulate field not found in extraction result");
        consulate.ShouldContain("London", Case.Insensitive);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractAppointment_DoctorPortal_ExtractsProviderAvailability()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(DoctorAppointmentHtml, new TestExtractionSchema
        {
            Name = "DoctorAppointments",
            Description = "Extract doctor appointment availability",
            Fields =
            [
                new TestSchemaField { Name = "clinicName", Type = "string", Description = "Medical clinic name" },
                new TestSchemaField { Name = "providers", Type = "array", Description = "List of doctors with availability" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        AssertExtractionSuccessOrSkipOnCacheMiss(result);
        result.Data.ShouldNotBeNull();

        var clinicName = result.GetString("clinicName");
        clinicName.ShouldContain("HealthFirst", Case.Insensitive);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractAppointment_Restaurant_ExtractsAvailableTimes()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(RestaurantReservationHtml, new TestExtractionSchema
        {
            Name = "RestaurantReservation",
            Description = "Extract restaurant reservation availability",
            Fields =
            [
                new TestSchemaField { Name = "restaurantName", Type = "string", Description = "Restaurant name" },
                new TestSchemaField { Name = "selectedDate", Type = "string", Description = "Selected date" },
                new TestSchemaField { Name = "timeSlots", Type = "array", Description = "Available time slots" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        AssertExtractionSuccessOrSkipOnCacheMiss(result);
        result.Data.ShouldNotBeNull();

        var restaurantName = result.GetString("restaurantName");
        restaurantName.ShouldContain("Golden Fork", Case.Insensitive);
    }

    #endregion
}

