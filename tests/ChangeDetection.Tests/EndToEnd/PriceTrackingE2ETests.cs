using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services;
using ChangeDetection.Services.LLM;
using ChangeDetection.Tests.Llm.Cache;
using ChangeDetection.Tests.Llm.TestHelpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;


namespace ChangeDetection.Tests.EndToEnd;

/// <summary>
/// E2E tests for price tracking functionality.
/// Tests LLM extraction → threshold evaluation → notification flow.
/// 
/// Unit tests (mocked LLM) run on every PR.
/// E2E tests (real LLM) use [Category("LlmCached")] for cached LLM responses.
/// </summary>
public class PriceTrackingE2ETests
{
    #region Test HTML Fixtures

    /// <summary>
    /// Sample HTML from fyft.cz - Czech game store.
    /// Price: 2 499 Kč, Status: UKONČENO (Discontinued)
    /// </summary>
    private const string FyftCzProductHtml = """
        <!DOCTYPE html>
        <html lang="cs">
        <head>
            <meta charset="UTF-8">
            <title>ZOMBICIDE: UNDEAD OR ALIVE (CMON) - FYFT</title>
        </head>
        <body>
            <div class="product-detail">
                <h1 class="product-title">ZOMBICIDE: UNDEAD OR ALIVE (CMON)</h1>
                
                <div class="product-price">
                    <span class="price-value" data-price="2499">2 499 Kč</span>
                </div>
                
                <div class="product-availability">
                    <span class="availability-status discontinued">UKONČENO</span>
                </div>
                
                <div class="product-info">
                    <table class="parameters">
                        <tr><td>EAN:</td><td>889696013538</td></tr>
                        <tr><td>Výrobce:</td><td>CMON (Cool Mini or Not)</td></tr>
                        <tr><td>Jazyk:</td><td>Čeština</td></tr>
                        <tr><td>Počet hráčů:</td><td>1-6</td></tr>
                    </table>
                </div>
                
                <div class="product-description">
                    <p>Na divokém západě vypukla zombie apokalypsa!</p>
                </div>
            </div>
        </body>
        </html>
        """;

    /// <summary>
    /// Sample HTML from alza.cz - Czech electronics retailer.
    /// Price: 657 Kč (was 939 Kč), Status: Skladem > 5 ks (In Stock)
    /// </summary>
    private const string AlzaCzProductHtml = """
        <!DOCTYPE html>
        <html lang="cs">
        <head>
            <meta charset="UTF-8">
            <title>Bitzee Harry Potter - Alza.cz</title>
        </head>
        <body>
            <div class="product-page" data-product-id="12909816">
                <h1 class="product-name">Bitzee Harry Potter</h1>
                
                <div class="price-box">
                    <span class="price-original strikethrough">939,-</span>
                    <span class="price-current" data-price="657">657,-</span>
                    <span class="price-currency">Kč</span>
                    <span class="price-saving">Ušetříte 282 Kč</span>
                </div>
                
                <div class="availability">
                    <span class="stock-status in-stock">
                        <i class="icon-check"></i>
                        Skladem &gt; 5 ks
                    </span>
                    <span class="delivery-info">Do půlnoci objednáš, ráno v AlzaBoxu máš.</span>
                </div>
                
                <div class="product-rating">
                    <span class="rating-value">5.0</span>
                    <span class="rating-count">53 hodnocení</span>
                </div>
            </div>
        </body>
        </html>
        """;

    /// <summary>
    /// Sample HTML from Amazon DE - German Amazon.
    /// Price: €29.99, Status: In Stock
    /// </summary>
    private const string AmazonDeProductHtml = """
        <!DOCTYPE html>
        <html lang="de">
        <head>
            <meta charset="UTF-8">
            <title>Magnetic Organiser - Amazon.de</title>
        </head>
        <body>
            <div id="dp-container">
                <h1 id="productTitle" class="a-size-large">
                    NatldGs Magnetic Organiser Organizer Refrigerator
                </h1>
                
                <div id="corePrice_feature_div">
                    <span class="a-price" data-a-color="price">
                        <span class="a-offscreen">29,99 €</span>
                        <span class="a-price-whole">29</span>
                        <span class="a-price-decimal">,</span>
                        <span class="a-price-fraction">99</span>
                        <span class="a-price-symbol">€</span>
                    </span>
                </div>
                
                <div id="availability">
                    <span class="a-size-medium a-color-success">
                        Auf Lager
                    </span>
                </div>
                
                <div id="merchant-info">
                    <span>Verkauf und Versand durch Amazon.</span>
                </div>
            </div>
        </body>
        </html>
        """;

    /// <summary>
    /// Sample HTML with out-of-stock product.
    /// Price: $199.99, Status: Out of Stock
    /// </summary>
    private const string OutOfStockProductHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <title>Widget Pro - Example Store</title>
        </head>
        <body>
            <div class="product-container">
                <h1>Widget Pro 3000</h1>
                
                <div class="price-section">
                    <span class="current-price">$199.99</span>
                </div>
                
                <div class="availability-section">
                    <span class="out-of-stock-badge">
                        Currently Unavailable
                    </span>
                    <p>This item is out of stock. Enter your email to be notified when available.</p>
                </div>
            </div>
        </body>
        </html>
        """;

    /// <summary>
    /// UK retailer with large price using thousands separator.
    /// Price: £1,299.99, Status: Limited Stock (Only 2 left)
    /// </summary>
    private const string UkRetailerLargePriceHtml = """
        <!DOCTYPE html>
        <html lang="en-GB">
        <head>
            <meta charset="UTF-8">
            <title>Premium Laptop - UK Electronics</title>
        </head>
        <body>
            <div class="product-page">
                <h1 class="product-title">Premium Laptop Pro 15</h1>
                
                <div class="price-block">
                    <span class="price">£1,299.99</span>
                    <span class="vat-info">inc. VAT</span>
                </div>
                
                <div class="stock-info">
                    <span class="low-stock-warning">Only 2 left in stock</span>
                </div>
            </div>
        </body>
        </html>
        """;

    /// <summary>
    /// EU retailer with large price in European format.
    /// Price: €1.299,00 (1299.00), Status: In Stock
    /// </summary>
    private const string EuLargePriceHtml = """
        <!DOCTYPE html>
        <html lang="de">
        <head>
            <meta charset="UTF-8">
            <title>Waschmaschine - MediaMarkt</title>
        </head>
        <body>
            <div class="product-detail">
                <h1>Samsung Waschmaschine WW90T</h1>
                
                <div class="price-container">
                    <span class="current-price">1.299,00 €</span>
                    <span class="shipping">Kostenloser Versand</span>
                </div>
                
                <div class="availability">
                    <span class="in-stock">Sofort lieferbar</span>
                </div>
            </div>
        </body>
        </html>
        """;

    /// <summary>
    /// Price without decimal places (whole number).
    /// Price: $50, Status: Pre-order
    /// </summary>
    private const string WholeNumberPriceHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <title>Upcoming Game - GameStore</title>
        </head>
        <body>
            <div class="product">
                <h1>Awesome Game 2025</h1>
                
                <div class="price-area">
                    <span class="price">$50</span>
                </div>
                
                <div class="availability">
                    <span class="preorder-badge">Pre-order Now</span>
                    <span class="release-date">Releases March 15, 2025</span>
                </div>
            </div>
        </body>
        </html>
        """;

    /// <summary>
    /// Swiss retailer with apostrophe as thousands separator.
    /// Price: CHF 1'499.00 (1499.00), Status: In Stock
    /// </summary>
    private const string SwissPriceHtml = """
        <!DOCTYPE html>
        <html lang="de-CH">
        <head>
            <meta charset="UTF-8">
            <title>iPhone - Digitec</title>
        </head>
        <body>
            <div class="product-page">
                <h1>iPhone 15 Pro 256GB</h1>
                
                <div class="pricing">
                    <span class="price">CHF 1'499.00</span>
                </div>
                
                <div class="stock">
                    <span class="available">Verfügbar</span>
                </div>
            </div>
        </body>
        </html>
        """;

    /// <summary>
    /// Product with Yen currency (typically no decimals, comma is thousands separator).
    /// Price: ¥12,800 (12800), Status: In Stock
    /// </summary>
    private const string JapaneseYenHtml = """
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="UTF-8">
            <title>Nintendo Switch Game - Amazon.co.jp</title>
        </head>
        <body>
            <div class="product-page" data-product-id="B0C123456">
                <h1 class="product-name">Legend of Zelda: Tears of the Kingdom - Nintendo Switch</h1>
                
                <div class="price-box">
                    <span class="price-label">Price:</span>
                    <span class="price-current" data-price="12800">¥12,800</span>
                    <span class="tax-note">(tax included)</span>
                </div>
                
                <div class="availability">
                    <span class="stock-status in-stock">
                        <i class="icon-check"></i>
                        In Stock
                    </span>
                    <span class="delivery-info">Ships within 24 hours</span>
                </div>
                
                <div class="product-info">
                    <table>
                        <tr><td>Platform:</td><td>Nintendo Switch</td></tr>
                        <tr><td>Publisher:</td><td>Nintendo</td></tr>
                        <tr><td>Release Date:</td><td>May 12, 2023</td></tr>
                    </table>
                </div>
            </div>
        </body>
        </html>
        """;

    /// <summary>
    /// Subscription-based pricing (monthly).
    /// Price: $9.99/month, Status: Available
    /// </summary>
    private const string SubscriptionPriceHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <title>Premium Plan - StreamingService</title>
        </head>
        <body>
            <div class="plan-details" data-plan-id="premium-monthly">
                <h1>Premium Plan</h1>
                
                <div class="pricing-box">
                    <span class="price-label">Price:</span>
                    <span class="price-current" data-price="9.99">$9.99</span>
                    <span class="period">/month</span>
                </div>
                
                <div class="plan-availability">
                    <span class="status available in-stock">
                        <i class="icon-check"></i>
                        Available
                    </span>
                    <button class="subscribe-btn">Subscribe Now</button>
                </div>
                
                <div class="plan-info">
                    <ul class="features">
                        <li>4K Ultra HD streaming</li>
                        <li>Ad-free experience</li>
                        <li>Download for offline viewing</li>
                    </ul>
                </div>
            </div>
        </body>
        </html>
        """;

    /// <summary>
    /// Price range (min-max pricing).
    /// Price: $29.99 - $49.99, Status: In Stock
    /// </summary>
    private const string PriceRangeHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <title>T-Shirt - Fashion Store</title>
        </head>
        <body>
            <div class="product" data-product-id="TSHIRT-001">
                <h1 class="product-name">Classic Cotton T-Shirt</h1>
                
                <div class="price-section">
                    <span class="price-label">Price:</span>
                    <span class="price-range">
                        <span class="from-price" data-price="29.99">$29.99</span>
                        <span class="separator"> - </span>
                        <span class="to-price" data-price="49.99">$49.99</span>
                    </span>
                    <span class="variants-note">Price varies by size and color</span>
                </div>
                
                <div class="availability">
                    <span class="stock-status in-stock">
                        <i class="icon-check"></i>
                        In Stock
                    </span>
                    <span class="delivery">Free shipping on orders over $50</span>
                </div>
                
                <div class="product-info">
                    <table>
                        <tr><td>Material:</td><td>100% Cotton</td></tr>
                        <tr><td>Sizes:</td><td>XS, S, M, L, XL</td></tr>
                    </table>
                </div>
            </div>
        </body>
        </html>
        """;

    /// <summary>
    /// Backorder status with expected date.
    /// Price: $299.00, Status: Backorder
    /// </summary>
    private const string BackorderHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <title>Popular Gadget - TechStore</title>
        </head>
        <body>
            <div class="product-page" data-product-id="GADGET-X100">
                <h1 class="product-name">Super Popular Gadget X</h1>
                
                <div class="price-box">
                    <span class="price-label">Price:</span>
                    <span class="price-current" data-price="299.00">$299.00</span>
                </div>
                
                <div class="availability">
                    <span class="stock-status backorder">
                        <i class="icon-clock"></i>
                        Backordered
                    </span>
                    <span class="expected-date">Expected to ship in 2-3 weeks</span>
                </div>
                
                <div class="product-info">
                    <table>
                        <tr><td>Brand:</td><td>TechCorp</td></tr>
                        <tr><td>Model:</td><td>X-100</td></tr>
                    </table>
                </div>
            </div>
        </body>
        </html>
        """;

    #endregion

    #region Unit Tests (Mocked LLM - Run on every PR)

    [Test]
    public async Task ExtractPrice_WithMockedLlm_ParsesJsonResponse()
    {
        // Arrange
        var llmProvider = Substitute.For<ILlmProviderChain>();
        llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                    {
                        "price": { "value": 2499, "currency": "CZK", "rawText": "2 499 Kč" },
                        "stock": { "status": "Discontinued", "rawText": "UKONČENO" },
                        "productName": "ZOMBICIDE: UNDEAD OR ALIVE",
                        "confidence": 0.95
                    }
                    """
            }));

        var service = CreateServiceWithMocks(llmProvider);

        // Act
        var result = await service.ExtractPriceAsync(FyftCzProductHtml);

        // Assert
        result.ShouldNotBeNull();
        result.Price.ShouldNotBeNull();
        result.Price.Value.ShouldBe(2499m);
        result.Price.Currency.ShouldBe("CZK");
        result.Stock.ShouldNotBeNull();
        result.Stock.Status.ShouldBe(StockStatus.Discontinued);
        result.ProductName.ShouldBe("ZOMBICIDE: UNDEAD OR ALIVE");
        result.Confidence.ShouldNotBeNull();
        result.Confidence.Value.ShouldBeGreaterThan(0.9f);
    }

    [Test]
    public async Task ExtractPrice_WithMockedLlm_HandlesEuroFormat()
    {
        // Arrange
        var llmProvider = Substitute.For<ILlmProviderChain>();
        llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                    {
                        "price": { "value": 29.99, "currency": "EUR", "rawText": "29,99 €" },
                        "stock": { "status": "InStock", "rawText": "Auf Lager" },
                        "productName": "Magnetic Organiser",
                        "confidence": 0.92
                    }
                    """
            }));

        var service = CreateServiceWithMocks(llmProvider);

        // Act
        var result = await service.ExtractPriceAsync(AmazonDeProductHtml);

        // Assert
        result.ShouldNotBeNull();
        result.Price!.Value.ShouldBe(29.99m);
        result.Price.Currency.ShouldBe("EUR");
        result.Stock!.Status.ShouldBe(StockStatus.InStock);
    }

    [Test]
    public async Task ProcessPriceCheck_WithPriceDropAlert_SendsNotification()
    {
        // Arrange
        var llmProvider = Substitute.For<ILlmProviderChain>();
        llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                    {
                        "price": { "value": 657, "currency": "CZK", "rawText": "657,-" },
                        "stock": { "status": "InStock", "rawText": "Skladem > 5 ks" },
                        "productName": "Bitzee Harry Potter",
                        "confidence": 0.95
                    }
                    """
            }));

        var priceHistoryRepo = Substitute.For<IPriceHistoryRepository>();
        priceHistoryRepo.GetLatestAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PriceHistoryEntry?>(new PriceHistoryEntry
            {
                WatchId = Guid.NewGuid(),
                FieldName = "Price",
                Value = 939m,
                Currency = "CZK",
                Timestamp = DateTime.UtcNow.AddDays(-1)
            }));

        var alertEvaluator = Substitute.For<IAlertThresholdEvaluator>();
        var alertResult = new AlertEvaluationResult
        {
            TriggeredThresholds = [
                new TriggeredThreshold
                {
                    Threshold = new FieldAlertThreshold 
                    { 
                        ConditionType = AlertConditionType.DropsByPercent, 
                        Value = 20 
                    },
                    Field = new SchemaField { Name = "Price", Selector = ".price" },
                    Message = "Price dropped by 30%",
                    OldValue = 939,
                    NewValue = 657
                }
            ]
        };
        alertEvaluator.Evaluate(Arg.Any<SchemaField>(), Arg.Any<double?>(), Arg.Any<double>(), Arg.Any<double?>())
            .Returns(alertResult);

        var notificationService = Substitute.For<INotificationService>();
        var logger = Substitute.For<ILogger<PriceTrackingService>>();

        var service = new PriceTrackingService(
            llmProvider,
            priceHistoryRepo,
            alertEvaluator,
            notificationService,
            logger);

        var watch = new WatchedSite
        {
            Url = "https://alza.cz/product",
            Schema = new ExtractionSchema
            {
                ItemSelector = ".product",
                Fields = [
                    new SchemaField
                    {
                        Name = "Price",
                        Selector = ".price-current",
                        Type = FieldType.Currency,
                        AlertThresholds = [
                            new FieldAlertThreshold
                            {
                                ConditionType = AlertConditionType.DropsByPercent,
                                Value = 20
                            }
                        ]
                    }
                ]
            }
        };

        // Act
        var result = await service.ProcessPriceCheckAsync(watch, AlzaCzProductHtml);

        // Assert
        result.Success.ShouldBeTrue();
        result.CurrentPrice.ShouldBe(657m);
        result.PreviousPrice.ShouldBe(939m);
        result.AlertResult.ShouldNotBeNull();
        result.AlertResult.HasTriggeredAlerts.ShouldBeTrue();

        // Verify notification was sent
        await notificationService.Received(1).SendAlertAsync(
            Arg.Any<WatchedSite>(),
            Arg.Is<AlertEvaluationResult>(r => r.HasTriggeredAlerts),
            Arg.Any<NotificationContext>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClassifyStockStatus_WithMockedLlm_ClassifiesCzechText()
    {
        // Arrange
        var llmProvider = Substitute.For<ILlmProviderChain>();
        llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LlmResponse
            {
                IsSuccess = true,
                Content = "Discontinued"
            }));

        var service = CreateServiceWithMocks(llmProvider);

        // Act
        var status = await service.ClassifyStockStatusAsync("UKONČENO");

        // Assert
        status.ShouldBe(StockStatus.Discontinued);
    }

    [Test]
    public async Task ClassifyStockStatus_WithMockedLlm_ClassifiesGermanText()
    {
        // Arrange
        var llmProvider = Substitute.For<ILlmProviderChain>();
        llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LlmResponse
            {
                IsSuccess = true,
                Content = "InStock"
            }));

        var service = CreateServiceWithMocks(llmProvider);

        // Act
        var status = await service.ClassifyStockStatusAsync("Auf Lager");

        // Assert
        status.ShouldBe(StockStatus.InStock);
    }

    [Test]
    public async Task ExtractPrice_LlmReturnsEmpty_ReturnsNull()
    {
        // Arrange
        var llmProvider = Substitute.For<ILlmProviderChain>();
        llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LlmResponse
            {
                IsSuccess = false,
                Content = null
            }));

        var service = CreateServiceWithMocks(llmProvider);

        // Act
        var result = await service.ExtractPriceAsync(FyftCzProductHtml);

        // Assert
        result.ShouldBeNull();
    }

    [Test]
    public async Task ParsePriceAsync_WithMockedLlm_ParsesLocaleFormat()
    {
        // Arrange
        var llmProvider = Substitute.For<ILlmProviderChain>();
        llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LlmResponse
            {
                IsSuccess = true,
                Content = """{ "value": 2499.00, "currency": "CZK" }"""
            }));

        var service = CreateServiceWithMocks(llmProvider);

        // Act
        var result = await service.ParsePriceAsync("2 499 Kč", "cs-CZ");

        // Assert
        result.ShouldNotBeNull();
        result.Value.Value.ShouldBe(2499m);
        result.Value.Currency.ShouldBe("CZK");
    }

    #endregion

    #region E2E Tests (Real LLM - Nightly runs only)

    [Test]
    [Category("LlmCached")]
    public async Task ExtractPrice_FyftCz_RealLlm_ExtractsCzechPrice()
    {
        var llmProvider = await CreateRealLlmProvider();

        var service = CreateServiceWithMocks(llmProvider);

        // Act
        var result = await service.ExtractPriceAsync(FyftCzProductHtml);

        // Assert
        TestContext.Current?.OutputWriter?.WriteLine($"Extracted price: {result?.Price?.Value} {result?.Price?.Currency}");
        TestContext.Current?.OutputWriter?.WriteLine($"Extracted stock: {result?.Stock?.Status} ({result?.Stock?.RawText})");
        TestContext.Current?.OutputWriter?.WriteLine($"Product name: {result?.ProductName}");
        TestContext.Current?.OutputWriter?.WriteLine($"Confidence: {result?.Confidence}");

        result.ShouldNotBeNull();
        result.Price.ShouldNotBeNull();
        result.Price.Value.ShouldNotBeNull();
        Math.Abs(result.Price.Value.Value - 2499m).ShouldBeLessThan(10m); // Allow small tolerance
        result.Stock?.Status.ShouldBe(StockStatus.Discontinued);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractPrice_AlzaCz_RealLlm_ExtractsSalePrice()
    {
        var llmProvider = await CreateRealLlmProvider();

        var service = CreateServiceWithMocks(llmProvider);

        // Act
        var result = await service.ExtractPriceAsync(AlzaCzProductHtml);

        // Assert
        TestContext.Current?.OutputWriter?.WriteLine($"Extracted price: {result?.Price?.Value} {result?.Price?.Currency}");
        TestContext.Current?.OutputWriter?.WriteLine($"Stock status: {result?.Stock?.Status}");

        result.ShouldNotBeNull();
        result.Price.ShouldNotBeNull();
        result.Price.Value.ShouldNotBeNull();
        // Should extract the current/sale price, not the original
        Math.Abs(result.Price.Value.Value - 657m).ShouldBeLessThan(10m);
        result.Stock?.Status.ShouldBe(StockStatus.InStock);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractPrice_AmazonDe_RealLlm_ExtractsEuroPrice()
    {
        var llmProvider = await CreateRealLlmProvider();

        var service = CreateServiceWithMocks(llmProvider);

        // Act
        var result = await service.ExtractPriceAsync(AmazonDeProductHtml);

        // Assert
        TestContext.Current?.OutputWriter?.WriteLine($"Extracted price: {result?.Price?.Value} {result?.Price?.Currency}");
        TestContext.Current?.OutputWriter?.WriteLine($"Stock status: {result?.Stock?.Status}");

        result.ShouldNotBeNull();
        result.Price.ShouldNotBeNull();
        result.Price.Value.ShouldNotBeNull();
        Math.Abs(result.Price.Value.Value - 29.99m).ShouldBeLessThan(1m);
        result.Stock?.Status.ShouldBe(StockStatus.InStock);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractPrice_OutOfStock_RealLlm_DetectsUnavailable()
    {
        var llmProvider = await CreateRealLlmProvider();

        var service = CreateServiceWithMocks(llmProvider);

        // Act
        var result = await service.ExtractPriceAsync(OutOfStockProductHtml);

        // Assert
        TestContext.Current?.OutputWriter?.WriteLine($"Extracted price: {result?.Price?.Value} {result?.Price?.Currency}");
        TestContext.Current?.OutputWriter?.WriteLine($"Stock status: {result?.Stock?.Status}");

        result.ShouldNotBeNull();
        result.Price.ShouldNotBeNull();
        result.Price.Value.ShouldNotBeNull();
        Math.Abs(result.Price.Value.Value - 199.99m).ShouldBeLessThan(1m);
        result.Stock?.Status.ShouldBe(StockStatus.OutOfStock);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractPrice_UkLargePrice_RealLlm_ParsesThousandsSeparator()
    {
        // Tests: £1,299.99 with comma as thousands separator, period as decimal
        var llmProvider = await CreateRealLlmProvider();
        var service = CreateServiceWithMocks(llmProvider);

        // Act
        var result = await service.ExtractPriceAsync(UkRetailerLargePriceHtml);

        // Assert
        TestContext.Current?.OutputWriter?.WriteLine($"Extracted price: {result?.Price?.Value} {result?.Price?.Currency}");
        TestContext.Current?.OutputWriter?.WriteLine($"Stock status: {result?.Stock?.Status}");

        result.ShouldNotBeNull();
        result.Price.ShouldNotBeNull();
        result.Price.Value.ShouldNotBeNull();
        Math.Abs(result.Price.Value.Value - 1299.99m).ShouldBeLessThan(1m);
        result.Price.Currency.ShouldBe("GBP");
        result.Stock?.Status.ShouldBe(StockStatus.LimitedStock);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractPrice_EuLargePrice_RealLlm_ParsesEuropeanFormat()
    {
        // Tests: 1.299,00 € with period as thousands separator, comma as decimal
        var llmProvider = await CreateRealLlmProvider();
        var service = CreateServiceWithMocks(llmProvider);

        // Act
        var result = await service.ExtractPriceAsync(EuLargePriceHtml);

        // Assert
        TestContext.Current?.OutputWriter?.WriteLine($"Extracted price: {result?.Price?.Value} {result?.Price?.Currency}");
        TestContext.Current?.OutputWriter?.WriteLine($"Stock status: {result?.Stock?.Status}");

        result.ShouldNotBeNull();
        result.Price.ShouldNotBeNull();
        result.Price.Value.ShouldNotBeNull();
        Math.Abs(result.Price.Value.Value - 1299.00m).ShouldBeLessThan(1m);
        result.Price.Currency.ShouldBe("EUR");
        result.Stock?.Status.ShouldBe(StockStatus.InStock);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractPrice_WholeNumber_RealLlm_ParsesWithoutDecimals()
    {
        // Tests: $50 without any decimal separator
        var llmProvider = await CreateRealLlmProvider();
        var service = CreateServiceWithMocks(llmProvider);

        // Act
        var result = await service.ExtractPriceAsync(WholeNumberPriceHtml);

        // Assert
        TestContext.Current?.OutputWriter?.WriteLine($"Extracted price: {result?.Price?.Value} {result?.Price?.Currency}");
        TestContext.Current?.OutputWriter?.WriteLine($"Stock status: {result?.Stock?.Status}");

        result.ShouldNotBeNull();
        result.Price.ShouldNotBeNull();
        result.Price.Value.ShouldNotBeNull();
        Math.Abs(result.Price.Value.Value - 50m).ShouldBeLessThan(1m);
        result.Price.Currency.ShouldBe("USD");
        result.Stock?.Status.ShouldBe(StockStatus.PreOrder);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractPrice_SwissFranc_RealLlm_ParsesApostropheSeparator()
    {
        // Tests: CHF 1'499.00 with apostrophe as thousands separator
        var llmProvider = await CreateRealLlmProvider();
        var service = CreateServiceWithMocks(llmProvider);

        // Act
        var result = await service.ExtractPriceAsync(SwissPriceHtml);

        // Assert
        TestContext.Current?.OutputWriter?.WriteLine($"Extracted price: {result?.Price?.Value} {result?.Price?.Currency}");
        TestContext.Current?.OutputWriter?.WriteLine($"Stock status: {result?.Stock?.Status}");

        result.ShouldNotBeNull();
        result.Price.ShouldNotBeNull();
        result.Price.Value.ShouldNotBeNull();
        Math.Abs(result.Price.Value.Value - 1499.00m).ShouldBeLessThan(1m);
        result.Price.Currency.ShouldBe("CHF");
        result.Stock?.Status.ShouldBe(StockStatus.InStock);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractPrice_JapaneseYen_RealLlm_ParsesNoDecimalCurrency()
    {
        // Tests: ¥12,800 - Yen typically has no decimal places
        var llmProvider = await CreateRealLlmProvider();
        var service = CreateServiceWithMocks(llmProvider);

        // Act
        var result = await service.ExtractPriceAsync(JapaneseYenHtml);

        // Assert
        TestContext.Current?.OutputWriter?.WriteLine($"Extracted price: {result?.Price?.Value} {result?.Price?.Currency}");
        TestContext.Current?.OutputWriter?.WriteLine($"Stock status: {result?.Stock?.Status}");

        result.ShouldNotBeNull();
        result.Price.ShouldNotBeNull();
        result.Price.Value.ShouldNotBeNull();
        Math.Abs(result.Price.Value.Value - 12800m).ShouldBeLessThan(1m);
        result.Price.Currency.ShouldBe("JPY");
        result.Stock?.Status.ShouldBe(StockStatus.InStock);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractPrice_Subscription_RealLlm_ParsesMonthlyPrice()
    {
        // Tests: $9.99/month - subscription pricing
        var llmProvider = await CreateRealLlmProvider();
        var service = CreateServiceWithMocks(llmProvider);

        // Act
        var result = await service.ExtractPriceAsync(SubscriptionPriceHtml);

        // Assert
        TestContext.Current?.OutputWriter?.WriteLine($"Extracted price: {result?.Price?.Value} {result?.Price?.Currency}");
        TestContext.Current?.OutputWriter?.WriteLine($"Stock status: {result?.Stock?.Status}");

        result.ShouldNotBeNull();
        result.Price.ShouldNotBeNull();
        result.Price.Value.ShouldNotBeNull();
        Math.Abs(result.Price.Value.Value - 9.99m).ShouldBeLessThan(1m);
        result.Price.Currency.ShouldBe("USD");
        result.Stock?.Status.ShouldBe(StockStatus.InStock);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractPrice_PriceRange_RealLlm_ParsesMinPrice()
    {
        // Tests: $29.99 - $49.99 - should extract the lower price
        var llmProvider = await CreateRealLlmProvider();
        var service = CreateServiceWithMocks(llmProvider);

        // Act
        var result = await service.ExtractPriceAsync(PriceRangeHtml);

        // Assert
        TestContext.Current?.OutputWriter?.WriteLine($"Extracted price: {result?.Price?.Value} {result?.Price?.Currency}");
        TestContext.Current?.OutputWriter?.WriteLine($"Stock status: {result?.Stock?.Status}");

        result.ShouldNotBeNull();
        result.Price.ShouldNotBeNull();
        result.Price.Value.ShouldNotBeNull();
        // Accept either the min or max price as valid
        var price = result.Price.Value.Value;
        (price == 29.99m || price == 49.99m).ShouldBeTrue($"Expected 29.99 or 49.99, got {price}");
        result.Price.Currency.ShouldBe("USD");
        result.Stock?.Status.ShouldBe(StockStatus.InStock);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractPrice_Backorder_RealLlm_DetectsBackorderStatus()
    {
        // Tests: Backorder status detection
        var llmProvider = await CreateRealLlmProvider();
        var service = CreateServiceWithMocks(llmProvider);

        // Act
        var result = await service.ExtractPriceAsync(BackorderHtml);

        // Assert
        TestContext.Current?.OutputWriter?.WriteLine($"Extracted price: {result?.Price?.Value} {result?.Price?.Currency}");
        TestContext.Current?.OutputWriter?.WriteLine($"Stock status: {result?.Stock?.Status}");

        result.ShouldNotBeNull();
        result.Price.ShouldNotBeNull();
        result.Price.Value.ShouldNotBeNull();
        Math.Abs(result.Price.Value.Value - 299.00m).ShouldBeLessThan(1m);
        result.Price.Currency.ShouldBe("USD");
        result.Stock?.Status.ShouldBe(StockStatus.Backorder);
    }

    #endregion

    #region Helper Methods

    private static PriceTrackingService CreateServiceWithMocks(ILlmProviderChain llmProvider)
    {
        var priceHistoryRepo = Substitute.For<IPriceHistoryRepository>();
        var alertEvaluator = Substitute.For<IAlertThresholdEvaluator>();
        var notificationService = Substitute.For<INotificationService>();
        var logger = Substitute.For<ILogger<PriceTrackingService>>();

        return new PriceTrackingService(
            llmProvider,
            priceHistoryRepo,
            alertEvaluator,
            notificationService,
            logger);
    }

    // Store the factory at class level so it can be disposed
    private CachingHttpClientFactory? _httpClientFactory;

    private async Task<ILlmProviderChain> CreateRealLlmProvider()
    {
        // Create a real LLM provider chain configured for Ollama with caching
        // The caching layer handles cache mode automatically:
        // - CacheOnly mode (CI) will throw meaningful errors on cache miss
        // - CacheAndNetwork mode (dev) will call Ollama and cache responses
        var providerRepo = new InMemoryRepository<LlmProviderConfig>();
        var usageRepo = new InMemoryRepository<LlmUsageRecord>();
        
        await providerRepo.InsertAsync(new LlmProviderConfig
        {
            Id = Guid.NewGuid(),
            Name = "Ollama-Test",
            ProviderType = LlmProviderType.Ollama,
            Model = "ministral-3:3b",
            Endpoint = "http://localhost:11434",
            Priority = 1,
            IsEnabled = true,
            IsHealthy = true,
            TimeoutSeconds = 60
        });
        
        var logger = Substitute.For<ILogger<LlmProviderChain>>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var llmLogService = Substitute.For<ILlmLogService>();
        
        // Create caching HTTP client factory for deterministic LLM responses
        var cacheMode = CachedLlmKernelFactory.GetDefaultCacheMode();
        _httpClientFactory = new CachingHttpClientFactory(cacheMode, Console.Out);
        TestContext.Current?.OutputWriter?.WriteLine($"=== LLM Cache Mode: {cacheMode} ===");
        
        return new LlmProviderChain(providerRepo, usageRepo, logger, serviceProvider, llmLogService, _httpClientFactory);
    }

    #endregion
}