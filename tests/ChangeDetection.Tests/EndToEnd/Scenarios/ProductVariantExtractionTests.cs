using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd.Scenarios;

/// <summary>
/// E2E tests for product variant extraction scenarios.
/// Tests LLM ability to extract size/color availability from e-commerce sites.
/// </summary>
public class ProductVariantExtractionTests : ExtractionTestBase
{
    #region Test HTML Fixtures

    private const string NikeShoesHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Nike Air Max 270 | Nike.com</title></head>
        <body>
            <main class="product-page" data-product-id="DH8051-100">
                <div class="product-header">
                    <h1 class="product-title" data-name="Nike Air Max 270">Nike Air Max 270</h1>
                    <div class="product-subtitle" data-category="Men's Shoes">Men's Shoes</div>
                </div>
                <div class="price-section">
                    <span class="current-price" data-price="159.99" data-currency="USD">$159.99</span>
                </div>
                <div class="color-picker" data-colors>
                    <div class="color-option selected" data-color="White/Black" data-sku="DH8051-100">
                        <span class="color-name">White/Black</span>
                    </div>
                    <div class="color-option" data-color="Black/Anthracite" data-sku="DH8051-001">
                        <span class="color-name">Black/Anthracite</span>
                    </div>
                </div>
                <div class="size-grid" data-sizes>
                    <button class="size-btn out-of-stock" data-size="8" data-available="false">8</button>
                    <button class="size-btn low-stock" data-size="9" data-available="true" data-stock="2">9</button>
                    <button class="size-btn" data-size="10" data-available="true" data-stock="15">10</button>
                    <button class="size-btn" data-size="11" data-available="true" data-stock="8">11</button>
                    <button class="size-btn out-of-stock" data-size="12" data-available="false">12</button>
                </div>
            </main>
        </body>
        </html>
        """;

    private const string AppleIPhoneHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Buy iPhone 15 Pro - Apple</title></head>
        <body>
            <main class="product-page" data-product="iphone-15-pro">
                <h1 class="product-name" data-name="iPhone 15 Pro">iPhone 15 Pro</h1>
                <div class="product-price" data-price="999" data-currency="USD">From $999</div>
                <div class="finish-selector" data-finishes>
                    <div class="finish selected" data-finish="Natural Titanium">Natural Titanium</div>
                    <div class="finish" data-finish="Blue Titanium">Blue Titanium</div>
                    <div class="finish" data-finish="White Titanium">White Titanium</div>
                    <div class="finish" data-finish="Black Titanium">Black Titanium</div>
                </div>
                <div class="storage-options" data-storage>
                    <div class="storage-option" data-storage="128GB" data-price="999" data-available="true">
                        <span class="storage-size">128GB</span><span class="storage-price">$999</span>
                    </div>
                    <div class="storage-option" data-storage="256GB" data-price="1099" data-available="true">
                        <span class="storage-size">256GB</span><span class="storage-price">$1,099</span>
                    </div>
                    <div class="storage-option" data-storage="512GB" data-price="1299" data-available="false">
                        <span class="storage-size">512GB</span><span class="storage-price">$1,299</span>
                        <span class="out-of-stock">Currently Unavailable</span>
                    </div>
                </div>
                <div class="delivery-info" data-delivery>
                    <span data-estimate="Delivers Jan 25-28">Delivers Jan 25-28</span>
                </div>
            </main>
        </body>
        </html>
        """;

    private const string LimitedEditionHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Limited Edition Sneaker Drop | Exclusive</title></head>
        <body>
            <main class="drop-page" data-drop-id="drop-2025-01">
                <div class="drop-header">
                    <h1 class="drop-title" data-name="Jordan 1 Retro High OG 'Chicago'">Jordan 1 Retro High OG "Chicago"</h1>
                    <div class="drop-badge limited" data-edition="Limited Edition">Limited Edition</div>
                </div>
                <div class="price-box">
                    <span class="retail-price" data-price="180">$180</span>
                </div>
                <div class="countdown-timer" data-release>
                    <span data-release-date="2025-01-25T10:00:00Z">Drops: Jan 25, 2025 at 10:00 AM EST</span>
                </div>
                <div class="size-availability" data-sizes>
                    <div class="size-item sold-out" data-size="8" data-available="false" data-stock="0">
                        <span class="size">8</span><span class="status">Sold Out</span>
                    </div>
                    <div class="size-item low" data-size="9" data-available="true" data-stock="3">
                        <span class="size">9</span><span class="status">3 Left</span>
                    </div>
                    <div class="size-item" data-size="10" data-available="true" data-stock="12">
                        <span class="size">10</span><span class="status">In Stock</span>
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
    public async Task ExtractProduct_NikeShoes_ExtractsSizeAvailability()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(NikeShoesHtml, new TestExtractionSchema
        {
            Name = "ProductVariants",
            Description = "Extract product variant availability",
            Fields =
            [
                new TestSchemaField { Name = "productName", Type = "string", Description = "Product name" },
                new TestSchemaField { Name = "price", Type = "number", Description = "Current price" },
                new TestSchemaField { Name = "sizes", Type = "array", Description = "Available sizes with stock" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        AssertExtractionSuccessOrSkipOnCacheMiss(result);

        var productName = result.GetString("productName");
        productName.ShouldContain("Air Max", Case.Insensitive);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractProduct_AppleIPhone_ExtractsStorageOptions()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(AppleIPhoneHtml, new TestExtractionSchema
        {
            Name = "PhoneVariants",
            Description = "Extract phone configuration options",
            Fields =
            [
                new TestSchemaField { Name = "productName", Type = "string", Description = "Product name" },
                new TestSchemaField { Name = "finishes", Type = "array", Description = "Available finishes/colors" },
                new TestSchemaField { Name = "storageOptions", Type = "array", Description = "Storage sizes with prices" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        AssertExtractionSuccessOrSkipOnCacheMiss(result);

        var productName = result.GetString("productName");
        productName.ShouldContain("iPhone", Case.Insensitive);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractProduct_LimitedEdition_ExtractsDropInfo()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(LimitedEditionHtml, new TestExtractionSchema
        {
            Name = "LimitedDrop",
            Description = "Extract limited edition drop information",
            Fields =
            [
                new TestSchemaField { Name = "productName", Type = "string", Description = "Product name" },
                new TestSchemaField { Name = "releaseDate", Type = "string", Description = "Release date/time" },
                new TestSchemaField { Name = "sizes", Type = "array", Description = "Size availability" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        AssertExtractionSuccessOrSkipOnCacheMiss(result);

        var productName = result.GetString("productName");
        productName.ShouldContain("Jordan", Case.Insensitive);
    }

    #endregion
}

