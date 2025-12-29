# Product Variant Availability Monitoring

## Overview

Users monitor e-commerce sites to catch when specific sizes, colors, or variants come back in stock:
- **Shoe sizes** (Nike, Adidas limited releases)
- **Clothing sizes** (Zara, H&amp;M restocks)
- **Electronics variants** (iPhone storage options)
- **Limited editions** (Collectibles, special releases)

## Key Fields to Extract

| Field | Description | Examples |
|-------|-------------|----------|
| `productName` | Full product name | "Air Jordan 1 Retro High OG" |
| `availableVariants` | In-stock options | `["US 9", "US 10", "US 11"]` |
| `outOfStockVariants` | Unavailable options | `["US 8", "US 12"]` |
| `lowStockVariants` | Low quantity warnings | `[{"size": "US 10", "remaining": 2}]` |
| `variantPrices` | Price per variant | `{"128GB": 999, "256GB": 1099}` |
| `restockDate` | Expected restock | "January 20, 2025" |
| `notifyAvailable` | Notify button present | `true` |

---

## Scenario 1: Nike Sneaker Limited Release

**Context**: User monitoring for their size in a limited sneaker drop

### HTML Fixture: `NikeSneakerHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Air Jordan 1 Retro High OG "Chicago" - Nike</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Helvetica Neue', Helvetica, Arial, sans-serif; background: #fff; }
        .nike-header { background: #111; color: #fff; padding: 12px 40px; display: flex; justify-content: space-between; align-items: center; }
        .nike-logo { font-size: 1.8rem; font-weight: 900; font-style: italic; }
        .nav-links { display: flex; gap: 30px; }
        .nav-links a { color: #fff; text-decoration: none; font-size: 0.9rem; }
        .product-container { max-width: 1200px; margin: 0 auto; padding: 40px; display: grid; grid-template-columns: 1fr 1fr; gap: 60px; }
        .product-gallery { position: relative; }
        .main-image { width: 100%; aspect-ratio: 1; background: #f5f5f5; border-radius: 12px; display: flex; align-items: center; justify-content: center; font-size: 8rem; }
        .thumbnail-row { display: flex; gap: 10px; margin-top: 15px; }
        .thumbnail { width: 80px; height: 80px; background: #f5f5f5; border-radius: 8px; border: 2px solid transparent; cursor: pointer; }
        .thumbnail.active { border-color: #111; }
        .member-badge { position: absolute; top: 15px; left: 15px; background: #111; color: #fff; padding: 8px 15px; font-size: 0.75rem; font-weight: 600; }
        .product-info { padding-top: 20px; }
        .product-type { color: #9e3500; font-weight: 600; font-size: 0.9rem; margin-bottom: 5px; }
        .product-title { font-size: 1.8rem; font-weight: 700; margin-bottom: 5px; color: #111; }
        .product-subtitle { color: #707072; margin-bottom: 20px; }
        .product-price { font-size: 1.3rem; font-weight: 600; margin-bottom: 25px; }
        .color-section { margin-bottom: 25px; }
        .color-section h4 { font-size: 0.9rem; margin-bottom: 12px; }
        .color-options { display: flex; gap: 10px; }
        .color-swatch { width: 50px; height: 50px; border-radius: 50%; border: 2px solid transparent; cursor: pointer; }
        .color-swatch.selected { border-color: #111; }
        .color-swatch.red { background: linear-gradient(135deg, #c41e3a 50%, #fff 50%); }
        .color-swatch.blue { background: linear-gradient(135deg, #1e40af 50%, #fff 50%); }
        .color-swatch.black { background: linear-gradient(135deg, #111 50%, #fff 50%); }
        .size-section { margin-bottom: 25px; }
        .size-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 12px; }
        .size-header h4 { font-size: 0.9rem; }
        .size-guide { color: #707072; font-size: 0.85rem; text-decoration: underline; cursor: pointer; }
        .size-grid { display: grid; grid-template-columns: repeat(5, 1fr); gap: 8px; }
        .size-btn { padding: 18px 10px; border: 1px solid #e5e5e5; border-radius: 6px; background: #fff; cursor: pointer; font-size: 0.95rem; text-align: center; transition: all 0.15s; position: relative; }
        .size-btn:hover:not(.unavailable) { border-color: #111; }
        .size-btn.selected { border-color: #111; border-width: 2px; }
        .size-btn.unavailable { background: #f5f5f5; color: #ccc; cursor: not-allowed; text-decoration: line-through; }
        .size-btn.low-stock::after { content: "Low"; position: absolute; top: 3px; right: 5px; font-size: 0.6rem; color: #9e3500; font-weight: 600; }
        .low-stock-warning { display: flex; align-items: center; gap: 8px; color: #9e3500; font-size: 0.85rem; margin-top: 12px; }
        .action-buttons { display: flex; flex-direction: column; gap: 12px; margin-top: 25px; }
        .add-to-bag { background: #111; color: #fff; border: none; padding: 18px; border-radius: 30px; font-size: 1rem; font-weight: 600; cursor: pointer; }
        .add-to-bag:disabled { background: #ccc; cursor: not-allowed; }
        .favorite-btn { background: #fff; color: #111; border: 1px solid #ccc; padding: 18px; border-radius: 30px; font-size: 1rem; font-weight: 600; cursor: pointer; display: flex; align-items: center; justify-content: center; gap: 8px; }
        .notify-section { background: #f5f5f5; border-radius: 12px; padding: 20px; margin-top: 25px; }
        .notify-section h4 { font-size: 0.95rem; margin-bottom: 10px; }
        .notify-section p { color: #707072; font-size: 0.85rem; margin-bottom: 15px; }
        .notify-btn { background: #fff; border: 1px solid #111; padding: 12px 25px; border-radius: 30px; cursor: pointer; font-weight: 600; }
        .product-details { margin-top: 30px; border-top: 1px solid #e5e5e5; padding-top: 25px; }
        .product-details h4 { margin-bottom: 15px; }
        .product-details ul { color: #707072; font-size: 0.9rem; padding-left: 20px; }
        .product-details li { margin-bottom: 8px; }
    </style>
</head>
<body>
    <header class="nike-header">
        <div class="nike-logo">NIKE</div>
        <nav class="nav-links">
            <a href="#">New Releases</a>
            <a href="#">Men</a>
            <a href="#">Women</a>
            <a href="#">Kids</a>
            <a href="#">Jordan</a>
            <a href="#">Sale</a>
        </nav>
    </header>
    
    <main class="product-container">
        <div class="product-gallery">
            <div class="member-badge">Member Exclusive</div>
            <div class="main-image">👟</div>
            <div class="thumbnail-row">
                <div class="thumbnail active"></div>
                <div class="thumbnail"></div>
                <div class="thumbnail"></div>
                <div class="thumbnail"></div>
            </div>
        </div>
        
        <div class="product-info">
            <div class="product-type">Jordan</div>
            <h1 class="product-title" data-product-id="DZ5485-612">Air Jordan 1 Retro High OG</h1>
            <div class="product-subtitle">"Chicago" - Men's Shoes</div>
            <div class="product-price" data-price="180" data-currency="USD">$180</div>
            
            <div class="color-section">
                <h4>Select Color: <strong>Varsity Red/Black/White</strong></h4>
                <div class="color-options">
                    <div class="color-swatch red selected" data-color="varsity-red"></div>
                    <div class="color-swatch blue" data-color="royal-blue"></div>
                    <div class="color-swatch black" data-color="shadow"></div>
                </div>
            </div>
            
            <div class="size-section">
                <div class="size-header">
                    <h4>Select Size</h4>
                    <span class="size-guide">Size Guide</span>
                </div>
                <div class="size-grid" data-variant-type="size">
                    <button class="size-btn unavailable" data-size="US 7" data-available="false">US 7</button>
                    <button class="size-btn unavailable" data-size="US 7.5" data-available="false">US 7.5</button>
                    <button class="size-btn unavailable" data-size="US 8" data-available="false">US 8</button>
                    <button class="size-btn unavailable" data-size="US 8.5" data-available="false">US 8.5</button>
                    <button class="size-btn" data-size="US 9" data-available="true">US 9</button>
                    <button class="size-btn low-stock" data-size="US 9.5" data-available="true" data-stock="2">US 9.5</button>
                    <button class="size-btn" data-size="US 10" data-available="true">US 10</button>
                    <button class="size-btn unavailable" data-size="US 10.5" data-available="false">US 10.5</button>
                    <button class="size-btn low-stock" data-size="US 11" data-available="true" data-stock="1">US 11</button>
                    <button class="size-btn unavailable" data-size="US 11.5" data-available="false">US 11.5</button>
                    <button class="size-btn unavailable" data-size="US 12" data-available="false">US 12</button>
                    <button class="size-btn" data-size="US 13" data-available="true">US 13</button>
                    <button class="size-btn unavailable" data-size="US 14" data-available="false">US 14</button>
                    <button class="size-btn unavailable" data-size="US 15" data-available="false">US 15</button>
                </div>
                
                <div class="low-stock-warning">
                    <span>⚡</span>
                    <span data-low-stock-message>Just a few left! Select sizes selling fast.</span>
                </div>
            </div>
            
            <div class="action-buttons">
                <button class="add-to-bag">Add to Bag</button>
                <button class="favorite-btn">♡ Favorite</button>
            </div>
            
            <div class="notify-section" data-notify-available="true">
                <h4>Sold Out In Your Size?</h4>
                <p>Enter your email and we'll notify you when your size is back in stock.</p>
                <button class="notify-btn">Notify Me</button>
            </div>
            
            <div class="product-details">
                <h4>Product Details</h4>
                <ul>
                    <li>Style: DZ5485-612</li>
                    <li>Colorway: Varsity Red/Black/White</li>
                    <li>Release Date: January 15, 2025</li>
                    <li>Retail Price: $180</li>
                </ul>
            </div>
        </div>
    </main>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "productName": "Air Jordan 1 Retro High OG",
  "subtitle": "Chicago - Men's Shoes",
  "price": 180,
  "currency": "USD",
  "color": "Varsity Red/Black/White",
  "availableSizes": ["US 9", "US 9.5", "US 10", "US 11", "US 13"],
  "outOfStockSizes": ["US 7", "US 7.5", "US 8", "US 8.5", "US 10.5", "US 11.5", "US 12", "US 14", "US 15"],
  "lowStockSizes": [
    {"size": "US 9.5", "remaining": 2},
    {"size": "US 11", "remaining": 1}
  ],
  "notifyAvailable": true
}
```

---

## Scenario 2: Apple iPhone Storage Variants

### HTML Fixture: `AppleIPhoneVariantsHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Buy iPhone 15 Pro - Apple</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: -apple-system, BlinkMacSystemFont, 'SF Pro Display', sans-serif; background: #fff; color: #1d1d1f; }
        .apple-nav { background: rgba(251,251,253,0.8); backdrop-filter: blur(20px); padding: 12px 40px; position: sticky; top: 0; z-index: 100; border-bottom: 1px solid #d2d2d7; }
        .nav-content { max-width: 1024px; margin: 0 auto; display: flex; justify-content: space-between; align-items: center; }
        .apple-logo { font-size: 1.2rem; }
        .nav-links { display: flex; gap: 25px; }
        .nav-links a { color: #1d1d1f; text-decoration: none; font-size: 0.85rem; }
        .product-page { max-width: 1024px; margin: 0 auto; padding: 60px 20px; }
        .product-header { text-align: center; margin-bottom: 50px; }
        .product-header h1 { font-size: 2.5rem; font-weight: 600; margin-bottom: 10px; }
        .product-header .tagline { color: #86868b; font-size: 1.2rem; }
        .price-range { font-size: 1.1rem; margin-top: 15px; }
        .configurator { display: grid; grid-template-columns: 1fr 1fr; gap: 60px; margin-top: 40px; }
        .product-image { text-align: center; }
        .product-image .phone-display { font-size: 15rem; }
        .color-name { margin-top: 20px; font-weight: 600; }
        .config-options { padding-top: 20px; }
        .option-group { margin-bottom: 40px; }
        .option-group h3 { font-size: 1.4rem; font-weight: 600; margin-bottom: 20px; }
        .option-subtitle { color: #86868b; font-size: 0.9rem; margin-bottom: 20px; }
        .color-grid { display: flex; gap: 20px; }
        .color-option { text-align: center; cursor: pointer; }
        .color-circle { width: 32px; height: 32px; border-radius: 50%; margin: 0 auto 8px; border: 2px solid transparent; }
        .color-circle.natural-titanium { background: #8f8a81; }
        .color-circle.blue-titanium { background: #3d4856; }
        .color-circle.white-titanium { background: #f5f5f0; border-color: #d2d2d7; }
        .color-circle.black-titanium { background: #1d1d1f; }
        .color-option.selected .color-circle { box-shadow: 0 0 0 3px #0071e3; }
        .color-option span { font-size: 0.75rem; color: #86868b; }
        .storage-grid { display: flex; flex-direction: column; gap: 12px; }
        .storage-option { display: flex; justify-content: space-between; align-items: center; padding: 20px 25px; border: 2px solid #d2d2d7; border-radius: 12px; cursor: pointer; transition: all 0.2s; }
        .storage-option:hover { border-color: #0071e3; }
        .storage-option.selected { border-color: #0071e3; background: #f5f5f7; }
        .storage-option.unavailable { background: #f5f5f7; border-style: dashed; cursor: not-allowed; opacity: 0.6; }
        .storage-info { display: flex; align-items: center; gap: 15px; }
        .storage-size { font-size: 1.2rem; font-weight: 600; }
        .storage-tag { background: #f56300; color: white; padding: 3px 10px; border-radius: 4px; font-size: 0.7rem; font-weight: 600; }
        .storage-tag.out { background: #86868b; }
        .storage-price { text-align: right; }
        .storage-price .price { font-size: 1.1rem; font-weight: 600; }
        .storage-price .monthly { color: #86868b; font-size: 0.85rem; }
        .delivery-info { background: #f5f5f7; border-radius: 12px; padding: 25px; margin-top: 30px; }
        .delivery-info h4 { margin-bottom: 15px; display: flex; align-items: center; gap: 10px; }
        .delivery-option { display: flex; justify-content: space-between; padding: 12px 0; border-bottom: 1px solid #d2d2d7; }
        .delivery-option:last-child { border: none; }
        .delivery-option .method { font-weight: 500; }
        .delivery-option .date { color: #86868b; }
        .delivery-option .date strong { color: #1d1d1f; }
        .add-to-bag-section { margin-top: 30px; }
        .total-price { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; }
        .total-price .label { font-size: 1.1rem; }
        .total-price .amount { font-size: 1.4rem; font-weight: 600; }
        .add-btn { width: 100%; padding: 18px; background: #0071e3; color: white; border: none; border-radius: 12px; font-size: 1.1rem; font-weight: 600; cursor: pointer; }
        .add-btn:disabled { background: #86868b; cursor: not-allowed; }
        .notify-restock { text-align: center; margin-top: 20px; }
        .notify-restock a { color: #0071e3; text-decoration: none; }
        .restock-note { background: #fef3e2; border: 1px solid #f5a623; border-radius: 10px; padding: 15px 20px; margin-top: 20px; display: flex; align-items: center; gap: 12px; font-size: 0.9rem; }
    </style>
</head>
<body>
    <nav class="apple-nav">
        <div class="nav-content">
            <span class="apple-logo"></span>
            <div class="nav-links">
                <a href="#">Store</a>
                <a href="#">Mac</a>
                <a href="#">iPad</a>
                <a href="#">iPhone</a>
                <a href="#">Watch</a>
                <a href="#">AirPods</a>
            </div>
        </div>
    </nav>
    
    <main class="product-page">
        <div class="product-header">
            <h1 data-product-id="iphone-15-pro">iPhone 15 Pro</h1>
            <div class="tagline">Titanium. So strong. So light. So Pro.</div>
            <div class="price-range">From <strong>$999</strong> or $41.62/mo. for 24 mo.</div>
        </div>
        
        <div class="configurator">
            <div class="product-image">
                <div class="phone-display">📱</div>
                <div class="color-name">Natural Titanium</div>
            </div>
            
            <div class="config-options">
                <div class="option-group">
                    <h3>Finish</h3>
                    <div class="option-subtitle">Pick your favorite</div>
                    <div class="color-grid">
                        <div class="color-option selected" data-color="natural-titanium">
                            <div class="color-circle natural-titanium"></div>
                            <span>Natural</span>
                        </div>
                        <div class="color-option" data-color="blue-titanium">
                            <div class="color-circle blue-titanium"></div>
                            <span>Blue</span>
                        </div>
                        <div class="color-option" data-color="white-titanium">
                            <div class="color-circle white-titanium"></div>
                            <span>White</span>
                        </div>
                        <div class="color-option" data-color="black-titanium">
                            <div class="color-circle black-titanium"></div>
                            <span>Black</span>
                        </div>
                    </div>
                </div>
                
                <div class="option-group">
                    <h3>Storage</h3>
                    <div class="option-subtitle">How much space do you need?</div>
                    <div class="storage-grid" data-variant-type="storage">
                        <div class="storage-option" data-storage="128GB" data-available="true" data-price="999">
                            <div class="storage-info">
                                <span class="storage-size">128GB</span>
                            </div>
                            <div class="storage-price">
                                <div class="price">$999</div>
                                <div class="monthly">$41.62/mo.</div>
                            </div>
                        </div>
                        <div class="storage-option selected" data-storage="256GB" data-available="true" data-price="1099">
                            <div class="storage-info">
                                <span class="storage-size">256GB</span>
                                <span class="storage-tag">Most Popular</span>
                            </div>
                            <div class="storage-price">
                                <div class="price">$1,099</div>
                                <div class="monthly">$45.79/mo.</div>
                            </div>
                        </div>
                        <div class="storage-option unavailable" data-storage="512GB" data-available="false" data-price="1299">
                            <div class="storage-info">
                                <span class="storage-size">512GB</span>
                                <span class="storage-tag out">Out of Stock</span>
                            </div>
                            <div class="storage-price">
                                <div class="price">$1,299</div>
                                <div class="monthly">$54.12/mo.</div>
                            </div>
                        </div>
                        <div class="storage-option" data-storage="1TB" data-available="true" data-price="1499">
                            <div class="storage-info">
                                <span class="storage-size">1TB</span>
                            </div>
                            <div class="storage-price">
                                <div class="price">$1,499</div>
                                <div class="monthly">$62.45/mo.</div>
                            </div>
                        </div>
                    </div>
                </div>
                
                <div class="restock-note" data-restock-variant="512GB">
                    <span>📦</span>
                    <span>512GB in Natural Titanium expected to be back in stock <strong data-restock-date="2025-01-25">January 25, 2025</strong></span>
                </div>
                
                <div class="delivery-info">
                    <h4>📦 Delivery</h4>
                    <div class="delivery-option">
                        <span class="method">Free Delivery</span>
                        <span class="date">Arrives <strong data-delivery-date="2025-01-18">Jan 18 - Jan 20</strong></span>
                    </div>
                    <div class="delivery-option">
                        <span class="method">Pickup</span>
                        <span class="date">Available <strong>Today</strong> at Apple Store</span>
                    </div>
                </div>
                
                <div class="add-to-bag-section">
                    <div class="total-price">
                        <span class="label">Total</span>
                        <span class="amount" data-total-price="1099">$1,099.00</span>
                    </div>
                    <button class="add-btn">Add to Bag</button>
                    <div class="notify-restock">
                        <a href="#" data-notify="true">Get notified when 512GB is available</a>
                    </div>
                </div>
            </div>
        </div>
    </main>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "productName": "iPhone 15 Pro",
  "color": "Natural Titanium",
  "availableStorage": ["128GB", "256GB", "1TB"],
  "outOfStockStorage": ["512GB"],
  "prices": {
    "128GB": 999,
    "256GB": 1099,
    "512GB": 1299,
    "1TB": 1499
  },
  "currency": "USD",
  "restockDate": "2025-01-25",
  "restockVariant": "512GB",
  "deliveryDate": "Jan 18 - Jan 20"
}
```

---

## Scenario 3: Clothing Size Matrix (Zara Style)

### HTML Fixture: `ZaraClothingHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>OVERSIZED WOOL BLEND COAT - ZARA</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Helvetica Neue', Arial, sans-serif; background: #fff; color: #1a1a1a; }
        .zara-header { padding: 20px 40px; display: flex; justify-content: space-between; align-items: center; border-bottom: 1px solid #e5e5e5; }
        .zara-logo { font-size: 1.6rem; letter-spacing: 8px; font-weight: 400; }
        .header-links a { color: #1a1a1a; text-decoration: none; margin-left: 30px; font-size: 0.8rem; letter-spacing: 1px; }
        .product-layout { display: grid; grid-template-columns: 2fr 1fr; min-height: calc(100vh - 80px); }
        .image-gallery { background: #f7f7f7; display: flex; flex-direction: column; }
        .gallery-image { flex: 1; display: flex; align-items: center; justify-content: center; font-size: 12rem; }
        .product-sidebar { padding: 40px; border-left: 1px solid #e5e5e5; display: flex; flex-direction: column; }
        .product-name { font-size: 0.85rem; letter-spacing: 1px; margin-bottom: 15px; text-transform: uppercase; }
        .product-price-section { margin-bottom: 30px; }
        .product-price { font-size: 1.1rem; margin-bottom: 5px; }
        .product-price.original { color: #999; text-decoration: line-through; font-size: 0.9rem; }
        .product-price.sale { color: #c00; }
        .color-section { margin-bottom: 30px; }
        .color-label { font-size: 0.75rem; letter-spacing: 1px; color: #666; margin-bottom: 12px; text-transform: uppercase; }
        .color-swatches { display: flex; gap: 8px; }
        .color-swatch { width: 24px; height: 24px; border-radius: 50%; cursor: pointer; border: 1px solid #e5e5e5; }
        .color-swatch.selected { outline: 1px solid #1a1a1a; outline-offset: 2px; }
        .color-swatch.camel { background: #c19a6b; }
        .color-swatch.black { background: #1a1a1a; }
        .color-swatch.grey { background: #808080; }
        .size-section { margin-bottom: 30px; }
        .size-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 15px; }
        .size-label { font-size: 0.75rem; letter-spacing: 1px; color: #666; text-transform: uppercase; }
        .size-guide-link { font-size: 0.75rem; color: #1a1a1a; text-decoration: underline; cursor: pointer; }
        .size-buttons { display: flex; gap: 8px; flex-wrap: wrap; }
        .size-btn { min-width: 55px; padding: 14px 12px; border: 1px solid #1a1a1a; background: #fff; cursor: pointer; font-size: 0.85rem; text-align: center; transition: all 0.2s; }
        .size-btn:hover:not(.unavailable) { background: #1a1a1a; color: #fff; }
        .size-btn.selected { background: #1a1a1a; color: #fff; }
        .size-btn.unavailable { border-style: dashed; color: #ccc; cursor: not-allowed; }
        .size-btn.few-left { position: relative; }
        .size-btn.few-left::after { content: ""; position: absolute; bottom: 3px; left: 50%; transform: translateX(-50%); width: 4px; height: 4px; background: #c00; border-radius: 50%; }
        .stock-warning { font-size: 0.75rem; color: #c00; margin-top: 12px; display: flex; align-items: center; gap: 5px; }
        .add-to-cart { width: 100%; padding: 16px; background: #1a1a1a; color: #fff; border: none; font-size: 0.85rem; letter-spacing: 2px; cursor: pointer; text-transform: uppercase; margin-top: 20px; }
        .add-to-cart:disabled { background: #ccc; cursor: not-allowed; }
        .notify-container { margin-top: 20px; padding: 20px; background: #f7f7f7; }
        .notify-container p { font-size: 0.8rem; color: #666; margin-bottom: 12px; }
        .notify-form { display: flex; gap: 10px; }
        .notify-form input { flex: 1; padding: 12px; border: 1px solid #e5e5e5; font-size: 0.85rem; }
        .notify-form button { padding: 12px 20px; background: #fff; border: 1px solid #1a1a1a; cursor: pointer; font-size: 0.8rem; letter-spacing: 1px; }
        .product-details { margin-top: auto; padding-top: 30px; border-top: 1px solid #e5e5e5; }
        .details-toggle { display: flex; justify-content: space-between; font-size: 0.75rem; letter-spacing: 1px; cursor: pointer; padding: 15px 0; border-bottom: 1px solid #e5e5e5; }
    </style>
</head>
<body>
    <header class="zara-header">
        <div class="zara-logo">ZARA</div>
        <nav class="header-links">
            <a href="#">WOMAN</a>
            <a href="#">MAN</a>
            <a href="#">KIDS</a>
            <a href="#">HOME</a>
        </nav>
    </header>
    
    <main class="product-layout">
        <div class="image-gallery">
            <div class="gallery-image">🧥</div>
        </div>
        
        <aside class="product-sidebar">
            <h1 class="product-name" data-product-id="8491/240">Oversized Wool Blend Coat</h1>
            
            <div class="product-price-section">
                <div class="product-price original">$199.00</div>
                <div class="product-price sale" data-price="129" data-currency="USD">$129.00</div>
            </div>
            
            <div class="color-section">
                <div class="color-label">Color: <strong data-selected-color="Camel">Camel</strong></div>
                <div class="color-swatches">
                    <div class="color-swatch camel selected" data-color="camel" title="Camel"></div>
                    <div class="color-swatch black" data-color="black" title="Black"></div>
                    <div class="color-swatch grey" data-color="grey" title="Grey Marl"></div>
                </div>
            </div>
            
            <div class="size-section">
                <div class="size-header">
                    <span class="size-label">Size</span>
                    <span class="size-guide-link">Size Guide</span>
                </div>
                <div class="size-buttons" data-color="camel">
                    <button class="size-btn unavailable" data-size="XS" data-available="false">XS</button>
                    <button class="size-btn few-left" data-size="S" data-available="true" data-stock="2">S</button>
                    <button class="size-btn" data-size="M" data-available="true">M</button>
                    <button class="size-btn" data-size="L" data-available="true">L</button>
                    <button class="size-btn unavailable" data-size="XL" data-available="false">XL</button>
                    <button class="size-btn unavailable" data-size="XXL" data-available="false">XXL</button>
                </div>
                <div class="stock-warning" data-low-stock="S">
                    <span>●</span> Size S - Only 2 left
                </div>
            </div>
            
            <button class="add-to-cart">Add to Cart</button>
            
            <div class="notify-container" data-notify-sizes="XS,XL,XXL">
                <p>Your size is out of stock? We'll let you know when it's back.</p>
                <div class="notify-form">
                    <input type="email" placeholder="Email">
                    <button>NOTIFY ME</button>
                </div>
            </div>
            
            <div class="product-details">
                <div class="details-toggle">
                    <span>COMPOSITION & CARE</span>
                    <span>+</span>
                </div>
                <div class="details-toggle">
                    <span>SHIPPING & RETURNS</span>
                    <span>+</span>
                </div>
            </div>
        </aside>
    </main>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "productName": "Oversized Wool Blend Coat",
  "originalPrice": 199,
  "salePrice": 129,
  "currency": "USD",
  "selectedColor": "Camel",
  "availableSizes": ["S", "M", "L"],
  "outOfStockSizes": ["XS", "XL", "XXL"],
  "lowStockSizes": [{"size": "S", "remaining": 2}],
  "notifyAvailable": true,
  "notifySizes": ["XS", "XL", "XXL"]
}
```

---

## Scenario 4: Limited Edition Collectible

### HTML Fixture: `CollectibleLimitedEditionHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Hot Toys Iron Man Mark LXXXV - Sideshow Collectibles</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Inter', sans-serif; background: #0a0a0a; color: #fff; }
        .site-header { background: #111; padding: 15px 40px; display: flex; justify-content: space-between; align-items: center; }
        .logo { font-size: 1.3rem; font-weight: 700; color: #e53935; }
        .nav-links a { color: #888; text-decoration: none; margin-left: 25px; font-size: 0.85rem; }
        .product-container { max-width: 1200px; margin: 0 auto; padding: 50px 20px; display: grid; grid-template-columns: 1.2fr 1fr; gap: 50px; }
        .product-images { position: relative; }
        .main-image { background: linear-gradient(135deg, #1a1a1a, #2d2d2d); border-radius: 12px; aspect-ratio: 1; display: flex; align-items: center; justify-content: center; font-size: 10rem; }
        .exclusive-badge { position: absolute; top: 20px; left: 20px; background: linear-gradient(135deg, #e53935, #b71c1c); padding: 8px 20px; border-radius: 4px; font-size: 0.75rem; font-weight: 700; letter-spacing: 2px; }
        .edition-info { position: absolute; bottom: 20px; right: 20px; background: rgba(0,0,0,0.8); padding: 10px 20px; border-radius: 4px; font-size: 0.8rem; }
        .edition-info strong { color: #ffd700; }
        .product-info { padding: 20px 0; }
        .brand { color: #888; font-size: 0.85rem; margin-bottom: 8px; text-transform: uppercase; letter-spacing: 2px; }
        .product-title { font-size: 2rem; font-weight: 700; margin-bottom: 10px; line-height: 1.2; }
        .product-subtitle { color: #888; font-size: 1rem; margin-bottom: 25px; }
        .rating-row { display: flex; align-items: center; gap: 15px; margin-bottom: 25px; }
        .stars { color: #ffd700; }
        .review-count { color: #888; font-size: 0.85rem; }
        .price-box { background: #1a1a1a; border-radius: 10px; padding: 25px; margin-bottom: 25px; }
        .price { font-size: 2rem; font-weight: 700; color: #fff; }
        .payment-options { color: #888; font-size: 0.85rem; margin-top: 10px; }
        .payment-options strong { color: #4caf50; }
        .stock-status { display: flex; align-items: center; gap: 12px; padding: 15px 20px; border-radius: 8px; margin-bottom: 25px; }
        .stock-status.limited { background: linear-gradient(135deg, rgba(255,193,7,0.2), rgba(255,152,0,0.1)); border: 1px solid #ffc107; }
        .stock-status .icon { font-size: 1.5rem; }
        .stock-status .text strong { color: #ffc107; display: block; }
        .stock-status .text span { color: #888; font-size: 0.85rem; }
        .edition-selector { margin-bottom: 25px; }
        .edition-selector h4 { font-size: 0.9rem; color: #888; margin-bottom: 12px; }
        .edition-options { display: flex; flex-direction: column; gap: 10px; }
        .edition-option { display: flex; justify-content: space-between; align-items: center; padding: 18px 20px; border: 2px solid #333; border-radius: 10px; cursor: pointer; transition: all 0.2s; }
        .edition-option:hover { border-color: #e53935; }
        .edition-option.selected { border-color: #e53935; background: rgba(229,57,53,0.1); }
        .edition-option.sold-out { opacity: 0.5; cursor: not-allowed; border-style: dashed; }
        .edition-option .name { font-weight: 600; }
        .edition-option .details { font-size: 0.8rem; color: #888; }
        .edition-option .right { text-align: right; }
        .edition-option .price { font-size: 1.1rem; font-weight: 600; }
        .edition-option .stock { font-size: 0.75rem; color: #ffc107; }
        .edition-option .stock.out { color: #888; }
        .action-buttons { display: flex; flex-direction: column; gap: 12px; }
        .preorder-btn { background: linear-gradient(135deg, #e53935, #b71c1c); color: #fff; border: none; padding: 18px; border-radius: 10px; font-size: 1rem; font-weight: 700; cursor: pointer; display: flex; align-items: center; justify-content: center; gap: 10px; }
        .preorder-btn:disabled { background: #333; color: #666; cursor: not-allowed; }
        .wishlist-btn { background: transparent; color: #fff; border: 2px solid #333; padding: 16px; border-radius: 10px; font-size: 0.95rem; cursor: pointer; }
        .shipping-note { background: #1a1a1a; border-radius: 8px; padding: 15px 20px; margin-top: 25px; font-size: 0.85rem; color: #888; }
        .shipping-note strong { color: #fff; }
        .waitlist-box { background: #1a1a1a; border-radius: 10px; padding: 25px; margin-top: 20px; text-align: center; }
        .waitlist-box h4 { margin-bottom: 10px; }
        .waitlist-box p { color: #888; font-size: 0.85rem; margin-bottom: 15px; }
        .waitlist-form { display: flex; gap: 10px; }
        .waitlist-form input { flex: 1; padding: 14px; border: 1px solid #333; border-radius: 8px; background: #0a0a0a; color: #fff; }
        .waitlist-form button { padding: 14px 25px; background: #333; border: none; border-radius: 8px; color: #fff; cursor: pointer; }
    </style>
</head>
<body>
    <header class="site-header">
        <div class="logo">SIDESHOW</div>
        <nav class="nav-links">
            <a href="#">Hot Toys</a>
            <a href="#">Statues</a>
            <a href="#">Figures</a>
            <a href="#">Art Prints</a>
        </nav>
    </header>
    
    <main class="product-container">
        <div class="product-images">
            <div class="exclusive-badge">EXCLUSIVE</div>
            <div class="main-image">🦸</div>
            <div class="edition-info">
                Limited to <strong data-edition-size="2000">2,000</strong> pieces worldwide
            </div>
        </div>
        
        <div class="product-info">
            <div class="brand">Hot Toys</div>
            <h1 class="product-title" data-product-id="HT-904599">Iron Man Mark LXXXV</h1>
            <div class="product-subtitle">Avengers: Endgame - 1/6 Scale Collectible Figure</div>
            
            <div class="rating-row">
                <span class="stars">★★★★★</span>
                <span class="review-count">4.9 (247 reviews)</span>
            </div>
            
            <div class="price-box">
                <div class="price" data-price="385" data-currency="USD">$385.00</div>
                <div class="payment-options">
                    or 4 payments of <strong>$96.25</strong> with Klarna
                </div>
            </div>
            
            <div class="stock-status limited" data-availability="limited">
                <span class="icon">⚡</span>
                <div class="text">
                    <strong>Low Stock Alert!</strong>
                    <span data-remaining="47">Only 47 units remaining</span>
                </div>
            </div>
            
            <div class="edition-selector">
                <h4>Select Edition</h4>
                <div class="edition-options" data-variant-type="edition">
                    <div class="edition-option sold-out" data-edition="exclusive" data-available="false">
                        <div>
                            <div class="name">Exclusive Edition</div>
                            <div class="details">Includes bonus Nano Lightning Refocuser</div>
                        </div>
                        <div class="right">
                            <div class="price">$415.00</div>
                            <div class="stock out">Sold Out</div>
                        </div>
                    </div>
                    <div class="edition-option selected" data-edition="regular" data-available="true" data-stock="47">
                        <div>
                            <div class="name">Regular Edition</div>
                            <div class="details">Standard release</div>
                        </div>
                        <div class="right">
                            <div class="price">$385.00</div>
                            <div class="stock">47 Available</div>
                        </div>
                    </div>
                    <div class="edition-option sold-out" data-edition="deluxe" data-available="false">
                        <div>
                            <div class="name">Deluxe Edition</div>
                            <div class="details">Full armor set + LED base</div>
                        </div>
                        <div class="right">
                            <div class="price">$495.00</div>
                            <div class="stock out">Sold Out</div>
                        </div>
                    </div>
                </div>
            </div>
            
            <div class="action-buttons">
                <button class="preorder-btn">
                    <span>🛒</span>
                    Add to Cart - $385.00
                </button>
                <button class="wishlist-btn">♡ Add to Wishlist</button>
            </div>
            
            <div class="shipping-note">
                <strong>Estimated Ship Date:</strong> Q2 2025 (Apr - Jun)
                <br>Free shipping on orders over $150
            </div>
            
            <div class="waitlist-box" data-waitlist-editions="exclusive,deluxe">
                <h4>Sold out edition? Join the waitlist</h4>
                <p>Get notified if Exclusive or Deluxe editions become available</p>
                <div class="waitlist-form">
                    <input type="email" placeholder="Enter your email">
                    <button>Join Waitlist</button>
                </div>
            </div>
        </div>
    </main>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "productName": "Iron Man Mark LXXXV",
  "brand": "Hot Toys",
  "price": 385,
  "currency": "USD",
  "editionSize": 2000,
  "availability": "limited",
  "remainingStock": 47,
  "availableEditions": ["Regular Edition"],
  "soldOutEditions": ["Exclusive Edition", "Deluxe Edition"],
  "editionPrices": {
    "Exclusive Edition": 415,
    "Regular Edition": 385,
    "Deluxe Edition": 495
  },
  "estimatedShipDate": "Q2 2025",
  "waitlistAvailable": true
}
```

---

## Test Implementation Notes

### Test Structure

```csharp
[Test]
[Category("LlmCached")]
public async Task ExtractVariant_NikeSneaker_DetectsAvailableSizes()
{
    var llmProvider = await CreateRealLlmProvider();
    var service = CreateVariantExtractionService(llmProvider);
    
    var result = await service.ExtractVariantInfoAsync(NikeSneakerHtml);
    
    result.ShouldNotBeNull();
    result.AvailableSizes.ShouldContain("US 9");
    result.AvailableSizes.ShouldContain("US 10");
    result.OutOfStockSizes.ShouldContain("US 8");
    result.LowStockSizes.ShouldContain(s => s.Size == "US 9.5" && s.Remaining == 2);
}
```

### Extraction Fields Schema

```json
{
  "type": "productVariant",
  "fields": {
    "productName": "string",
    "price": "number",
    "currency": "string",
    "variantType": "enum(size|storage|color|edition)",
    "availableVariants": "string[]",
    "outOfStockVariants": "string[]",
    "lowStockVariants": "array<{variant: string, remaining: number}>",
    "variantPrices": "object",
    "restockDate": "date?",
    "notifyAvailable": "boolean"
  }
}
```
