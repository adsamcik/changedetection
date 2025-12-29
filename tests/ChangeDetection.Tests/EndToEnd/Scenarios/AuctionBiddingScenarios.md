# Auction &amp; Bidding Monitoring

## Overview

Users monitor online auctions and bidding platforms:
- **eBay auctions** (rare collectibles, vintage items)
- **Art auctions** (Christie's, Sotheby's online)
- **Government auctions** (surplus, seized property)
- **Domain auctions** (GoDaddy, Sedo)
- **Car auctions** (Bring a Trailer, Cars &amp; Bids)
- **Real estate auctions** (foreclosures, estates)

## Key Fields to Extract

| Field | Description | Examples |
|-------|-------------|----------|
| `itemTitle` | Auction item name | "1969 Ford Mustang Boss 429" |
| `currentBid` | Current high bid | 125000 |
| `bidCount` | Number of bids | 47 |
| `timeRemaining` | Time until end | "2h 15m" |
| `endTime` | Auction end time | "2025-01-17T20:00:00" |
| `buyItNow` | Buy it now price | 150000 |
| `reserveMet` | Reserve price met | true |
| `highBidder` | Current winner | "j***n" |

---

## Scenario 1: eBay Collectible Auction

**Context**: User monitoring a rare collectible auction

### HTML Fixture: `EbayAuctionHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Vintage 1967 Rolex Submariner - eBay</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Market Sans', Arial, sans-serif; background: #f5f5f5; color: #191919; }
        .ebay-header { background: #fff; border-bottom: 1px solid #e5e5e5; padding: 10px 20px; display: flex; align-items: center; gap: 30px; }
        .ebay-logo { color: #e53238; font-weight: 700; font-size: 1.8rem; }
        .ebay-logo span:nth-child(1) { color: #e53238; }
        .ebay-logo span:nth-child(2) { color: #0064d2; }
        .ebay-logo span:nth-child(3) { color: #f5af02; }
        .ebay-logo span:nth-child(4) { color: #86b817; }
        .search-bar { flex: 1; display: flex; gap: 10px; }
        .search-bar input { flex: 1; padding: 12px 15px; border: 2px solid #191919; border-radius: 25px; font-size: 1rem; }
        .search-bar button { padding: 12px 30px; background: #3665f3; color: #fff; border: none; border-radius: 25px; font-weight: 600; }
        .listing-page { max-width: 1200px; margin: 0 auto; padding: 20px; display: grid; grid-template-columns: 1fr 400px; gap: 30px; }
        .image-section { }
        .main-image { width: 100%; aspect-ratio: 1; background: #fff; border-radius: 8px; display: flex; align-items: center; justify-content: center; font-size: 8rem; margin-bottom: 15px; }
        .thumbnail-row { display: flex; gap: 10px; }
        .thumbnail { width: 80px; height: 80px; background: #fff; border-radius: 4px; display: flex; align-items: center; justify-content: center; font-size: 2rem; border: 2px solid transparent; cursor: pointer; }
        .thumbnail:hover { border-color: #3665f3; }
        .listing-details { }
        .listing-title { font-size: 1.5rem; font-weight: 400; line-height: 1.4; margin-bottom: 15px; }
        .seller-info { display: flex; align-items: center; gap: 10px; margin-bottom: 20px; font-size: 0.9rem; }
        .seller-info .name { color: #3665f3; font-weight: 600; }
        .seller-info .rating { color: #767676; }
        .seller-info .stars { color: #f5af02; }
        .auction-box { background: #fff; border-radius: 12px; padding: 20px; margin-bottom: 20px; }
        .time-remaining { background: #ffcc00; padding: 15px; border-radius: 8px; margin-bottom: 20px; text-align: center; }
        .time-remaining .label { font-size: 0.85rem; margin-bottom: 5px; }
        .time-remaining .time { font-size: 1.8rem; font-weight: 700; display: flex; justify-content: center; gap: 20px; }
        .time-remaining .unit { font-size: 0.75rem; font-weight: 400; display: block; }
        .time-remaining.ending-soon { background: #e53238; color: #fff; animation: pulse 1s infinite; }
        @keyframes pulse { 0%, 100% { opacity: 1; } 50% { opacity: 0.8; } }
        .current-bid { margin-bottom: 20px; }
        .bid-label { color: #767676; font-size: 0.85rem; margin-bottom: 5px; }
        .bid-amount { font-size: 2rem; font-weight: 700; color: #191919; }
        .bid-count { color: #767676; font-size: 0.9rem; margin-top: 5px; }
        .bid-count a { color: #3665f3; text-decoration: none; }
        .reserve-status { padding: 10px 15px; border-radius: 6px; margin-bottom: 20px; font-size: 0.9rem; display: flex; align-items: center; gap: 10px; }
        .reserve-status.met { background: #e8f4ea; color: #0e6b0e; }
        .reserve-status.not-met { background: #ffeaea; color: #e53238; }
        .bid-section { margin-bottom: 20px; }
        .bid-input-row { display: flex; gap: 10px; margin-bottom: 10px; }
        .bid-input { flex: 1; padding: 15px; border: 2px solid #e5e5e5; border-radius: 8px; font-size: 1.1rem; }
        .bid-btn { padding: 15px 30px; background: #3665f3; color: #fff; border: none; border-radius: 25px; font-weight: 600; font-size: 1rem; cursor: pointer; }
        .min-bid { color: #767676; font-size: 0.85rem; }
        .buy-now-section { padding-top: 20px; border-top: 1px solid #e5e5e5; }
        .buy-now-price { display: flex; align-items: center; justify-content: space-between; margin-bottom: 15px; }
        .buy-now-label { color: #767676; font-size: 0.9rem; }
        .buy-now-amount { font-size: 1.5rem; font-weight: 600; }
        .buy-now-btn { width: 100%; padding: 15px; background: #191919; color: #fff; border: none; border-radius: 25px; font-weight: 600; font-size: 1rem; cursor: pointer; }
        .watchers { display: flex; align-items: center; gap: 8px; margin-top: 20px; color: #767676; font-size: 0.9rem; }
        .watchers .icon { color: #e53238; }
        .item-details { background: #fff; border-radius: 12px; padding: 20px; }
        .details-header { font-size: 1.1rem; font-weight: 600; margin-bottom: 15px; padding-bottom: 15px; border-bottom: 1px solid #e5e5e5; }
        .detail-row { display: flex; justify-content: space-between; padding: 10px 0; border-bottom: 1px solid #f5f5f5; font-size: 0.9rem; }
        .detail-row:last-child { border: none; }
        .detail-label { color: #767676; }
        .detail-value { font-weight: 500; }
        .bid-history { margin-top: 20px; }
        .bid-history-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 15px; }
        .bid-history-header h3 { font-size: 1rem; }
        .bid-history-header a { color: #3665f3; text-decoration: none; font-size: 0.9rem; }
        .bid-list { list-style: none; }
        .bid-list li { display: flex; justify-content: space-between; padding: 10px 0; border-bottom: 1px solid #f5f5f5; font-size: 0.9rem; }
        .bid-list .bidder { color: #3665f3; }
        .bid-list .amount { font-weight: 600; }
        .bid-list .time { color: #767676; font-size: 0.8rem; }
    </style>
</head>
<body>
    <header class="ebay-header">
        <div class="ebay-logo">
            <span>e</span><span>B</span><span>a</span><span>y</span>
        </div>
        <div class="search-bar">
            <input type="text" placeholder="Search for anything">
            <button>Search</button>
        </div>
    </header>
    
    <main class="listing-page">
        <section class="image-section">
            <div class="main-image">⌚</div>
            <div class="thumbnail-row">
                <div class="thumbnail">⌚</div>
                <div class="thumbnail">📐</div>
                <div class="thumbnail">📦</div>
                <div class="thumbnail">📄</div>
            </div>
        </section>
        
        <section class="listing-details">
            <h1 class="listing-title" data-title="Vintage 1967 Rolex Submariner 5513 - Original Patina, All Original">
                Vintage 1967 Rolex Submariner 5513 - Original Patina, All Original Parts, Box &amp; Papers
            </h1>
            
            <div class="seller-info">
                <span class="name" data-seller="watchcollector_nyc">watchcollector_nyc</span>
                <span class="rating" data-seller-rating="99.8">(99.8% positive)</span>
                <span class="stars">★★★★★</span>
                <span data-seller-feedback="2,847">2,847 reviews</span>
            </div>
            
            <div class="auction-box" data-auction-info>
                <div class="time-remaining ending-soon" data-time-remaining>
                    <div class="label">⏰ Time left:</div>
                    <div class="time">
                        <span data-hours="2">2<span class="unit">hours</span></span>
                        <span data-minutes="15">15<span class="unit">mins</span></span>
                        <span data-seconds="42">42<span class="unit">secs</span></span>
                    </div>
                </div>
                
                <div class="current-bid">
                    <div class="bid-label">Current bid:</div>
                    <div class="bid-amount" data-current-bid="47250" data-currency="USD">$47,250.00</div>
                    <div class="bid-count">
                        <a href="#" data-bid-count="67">67 bids</a> • 
                        <span data-high-bidder="j***n">High bidder: j***n</span>
                    </div>
                </div>
                
                <div class="reserve-status met" data-reserve-met="true">
                    <span>✓</span>
                    <span>Reserve price has been met</span>
                </div>
                
                <div class="bid-section">
                    <div class="bid-input-row">
                        <input type="text" class="bid-input" placeholder="$47,350.00 or more" data-min-bid="47350">
                        <button class="bid-btn">Place bid</button>
                    </div>
                    <div class="min-bid" data-bid-increment="100">Enter $47,350.00 or more (Bid increment: $100)</div>
                </div>
                
                <div class="buy-now-section" data-buy-it-now-available="true">
                    <div class="buy-now-price">
                        <span class="buy-now-label">Buy It Now:</span>
                        <span class="buy-now-amount" data-buy-it-now="55000">$55,000.00</span>
                    </div>
                    <button class="buy-now-btn">Buy It Now</button>
                </div>
                
                <div class="watchers" data-watchers="142">
                    <span class="icon">👁️</span>
                    <span>142 watching</span>
                </div>
            </div>
            
            <div class="item-details">
                <div class="details-header">Item specifics</div>
                <div class="detail-row" data-brand="Rolex">
                    <span class="detail-label">Brand</span>
                    <span class="detail-value">Rolex</span>
                </div>
                <div class="detail-row" data-model="Submariner">
                    <span class="detail-label">Model</span>
                    <span class="detail-value">Submariner 5513</span>
                </div>
                <div class="detail-row" data-year="1967">
                    <span class="detail-label">Year</span>
                    <span class="detail-value">1967</span>
                </div>
                <div class="detail-row" data-condition="Pre-owned">
                    <span class="detail-label">Condition</span>
                    <span class="detail-value">Pre-owned (Excellent)</span>
                </div>
                <div class="detail-row" data-case-material="Stainless Steel">
                    <span class="detail-label">Case Material</span>
                    <span class="detail-value">Stainless Steel</span>
                </div>
                <div class="detail-row" data-includes="Box, Papers, Service History">
                    <span class="detail-label">Includes</span>
                    <span class="detail-value">Box, Papers, Service History</span>
                </div>
                
                <div class="bid-history" data-bid-history>
                    <div class="bid-history-header">
                        <h3>Recent Bids</h3>
                        <a href="#">View all</a>
                    </div>
                    <ul class="bid-list">
                        <li data-bid-entry="1">
                            <span class="bidder" data-bidder="j***n">j***n</span>
                            <span class="amount" data-bid-amount="47250">$47,250</span>
                            <span class="time" data-bid-time="2m ago">2m ago</span>
                        </li>
                        <li data-bid-entry="2">
                            <span class="bidder" data-bidder="c***k">c***k</span>
                            <span class="amount" data-bid-amount="47000">$47,000</span>
                            <span class="time" data-bid-time="8m ago">8m ago</span>
                        </li>
                        <li data-bid-entry="3">
                            <span class="bidder" data-bidder="j***n">j***n</span>
                            <span class="amount" data-bid-amount="46500">$46,500</span>
                            <span class="time" data-bid-time="15m ago">15m ago</span>
                        </li>
                        <li data-bid-entry="4">
                            <span class="bidder" data-bidder="m***r">m***r</span>
                            <span class="amount" data-bid-amount="45000">$45,000</span>
                            <span class="time" data-bid-time="1h ago">1h ago</span>
                        </li>
                    </ul>
                </div>
            </div>
        </section>
    </main>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "itemTitle": "Vintage 1967 Rolex Submariner 5513 - Original Patina, All Original Parts, Box & Papers",
  "seller": "watchcollector_nyc",
  "sellerRating": 99.8,
  "sellerFeedback": 2847,
  "currentBid": 47250,
  "currency": "USD",
  "bidCount": 67,
  "highBidder": "j***n",
  "timeRemaining": "2h 15m 42s",
  "hoursRemaining": 2,
  "minutesRemaining": 15,
  "reserveMet": true,
  "buyItNowAvailable": true,
  "buyItNowPrice": 55000,
  "minNextBid": 47350,
  "bidIncrement": 100,
  "watchers": 142,
  "brand": "Rolex",
  "model": "Submariner 5513",
  "year": 1967,
  "condition": "Pre-owned (Excellent)",
  "recentBids": [
    {"bidder": "j***n", "amount": 47250, "time": "2m ago"},
    {"bidder": "c***k", "amount": 47000, "time": "8m ago"},
    {"bidder": "j***n", "amount": 46500, "time": "15m ago"}
  ]
}
```

---

## Scenario 2: Classic Car Auction

**Context**: User monitoring a collector car auction

### HTML Fixture: `CarAuctionHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>1969 Ford Mustang Boss 429 | Bring a Trailer</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Graphik', -apple-system, sans-serif; background: #1a1a1a; color: #fff; }
        .bat-header { background: #000; padding: 15px 40px; display: flex; justify-content: space-between; align-items: center; border-bottom: 3px solid #d4af37; }
        .bat-logo { font-size: 1.3rem; font-weight: 700; letter-spacing: 2px; }
        .bat-logo span { color: #d4af37; }
        .header-nav a { color: #999; text-decoration: none; margin-left: 30px; font-size: 0.9rem; }
        .auction-page { max-width: 1100px; margin: 0 auto; padding: 30px 20px; }
        .auction-header { margin-bottom: 30px; }
        .auction-title { font-size: 2rem; font-weight: 600; margin-bottom: 10px; }
        .auction-subtitle { color: #999; font-size: 1rem; }
        .gallery { margin-bottom: 30px; }
        .gallery-main { width: 100%; aspect-ratio: 16/9; background: linear-gradient(135deg, #2a2a2a, #1a1a1a); border-radius: 8px; display: flex; align-items: center; justify-content: center; font-size: 6rem; margin-bottom: 15px; }
        .gallery-thumbs { display: flex; gap: 10px; overflow-x: auto; }
        .gallery-thumb { width: 120px; height: 80px; background: #2a2a2a; border-radius: 4px; flex-shrink: 0; display: flex; align-items: center; justify-content: center; font-size: 1.5rem; cursor: pointer; }
        .gallery-thumb:hover { outline: 2px solid #d4af37; }
        .auction-content { display: grid; grid-template-columns: 1fr 380px; gap: 40px; }
        .auction-main { }
        .auction-sidebar { }
        .bid-module { background: #2a2a2a; border-radius: 12px; padding: 25px; margin-bottom: 25px; }
        .bid-status-bar { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; padding-bottom: 20px; border-bottom: 1px solid #444; }
        .status-badge { padding: 6px 15px; border-radius: 20px; font-size: 0.85rem; font-weight: 600; text-transform: uppercase; letter-spacing: 1px; }
        .status-badge.live { background: #c62828; animation: pulse 1.5s infinite; }
        .status-badge.ending { background: #d4af37; color: #000; }
        .time-left { text-align: right; }
        .time-left .label { color: #999; font-size: 0.8rem; margin-bottom: 3px; }
        .time-left .countdown { font-size: 1.3rem; font-weight: 600; color: #d4af37; }
        .current-bid-section { text-align: center; margin-bottom: 25px; }
        .current-bid-section .label { color: #999; font-size: 0.9rem; margin-bottom: 8px; }
        .current-bid-section .amount { font-size: 3rem; font-weight: 700; color: #d4af37; }
        .bid-activity { display: flex; justify-content: center; gap: 30px; margin-bottom: 25px; font-size: 0.9rem; color: #999; }
        .bid-activity span { display: flex; align-items: center; gap: 6px; }
        .bid-form { display: flex; flex-direction: column; gap: 15px; }
        .bid-input-group { display: flex; gap: 10px; }
        .bid-input-group input { flex: 1; padding: 15px; background: #1a1a1a; border: 2px solid #444; border-radius: 8px; color: #fff; font-size: 1.1rem; }
        .bid-input-group input:focus { border-color: #d4af37; outline: none; }
        .bid-button { padding: 18px; background: #d4af37; color: #000; border: none; border-radius: 8px; font-size: 1.1rem; font-weight: 700; cursor: pointer; text-transform: uppercase; letter-spacing: 1px; }
        .bid-button:hover { background: #e5c04b; }
        .bid-info { font-size: 0.85rem; color: #999; text-align: center; }
        .reserve-indicator { padding: 15px; border-radius: 8px; margin-bottom: 25px; text-align: center; font-weight: 600; }
        .reserve-indicator.met { background: rgba(76, 175, 80, 0.2); color: #4caf50; border: 1px solid #4caf50; }
        .reserve-indicator.not-met { background: rgba(244, 67, 54, 0.2); color: #f44336; border: 1px solid #f44336; }
        .details-section { background: #2a2a2a; border-radius: 12px; padding: 25px; margin-bottom: 25px; }
        .details-section h3 { font-size: 1.1rem; margin-bottom: 20px; padding-bottom: 15px; border-bottom: 1px solid #444; }
        .details-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 15px; }
        .detail-item { }
        .detail-item .label { color: #999; font-size: 0.8rem; margin-bottom: 3px; }
        .detail-item .value { font-weight: 500; }
        .seller-card { background: #2a2a2a; border-radius: 12px; padding: 25px; }
        .seller-header { display: flex; align-items: center; gap: 15px; margin-bottom: 20px; }
        .seller-avatar { width: 60px; height: 60px; background: #444; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 1.5rem; }
        .seller-info h4 { font-size: 1rem; margin-bottom: 3px; }
        .seller-info .joined { color: #999; font-size: 0.85rem; }
        .seller-stats { display: flex; gap: 20px; }
        .stat { text-align: center; }
        .stat .num { font-size: 1.3rem; font-weight: 600; color: #d4af37; }
        .stat .label { color: #999; font-size: 0.75rem; }
        .bid-history-section { background: #2a2a2a; border-radius: 12px; padding: 25px; margin-bottom: 25px; }
        .bid-history-section h3 { font-size: 1.1rem; margin-bottom: 20px; display: flex; justify-content: space-between; align-items: center; }
        .bid-history-section h3 span { color: #d4af37; font-size: 0.9rem; }
        .bid-timeline { }
        .bid-entry { display: flex; align-items: center; gap: 15px; padding: 15px 0; border-bottom: 1px solid #333; }
        .bid-entry:last-child { border: none; }
        .bid-entry .position { width: 30px; height: 30px; background: #d4af37; color: #000; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-weight: 700; font-size: 0.9rem; }
        .bid-entry .position.other { background: #444; color: #999; }
        .bid-entry .details { flex: 1; }
        .bid-entry .bidder { font-weight: 600; margin-bottom: 2px; }
        .bid-entry .time { color: #999; font-size: 0.85rem; }
        .bid-entry .amount { font-size: 1.2rem; font-weight: 600; color: #d4af37; }
    </style>
</head>
<body>
    <header class="bat-header">
        <div class="bat-logo">BRING A <span>TRAILER</span></div>
        <nav class="header-nav">
            <a href="#">Auctions</a>
            <a href="#">Buy Now</a>
            <a href="#">Journal</a>
        </nav>
    </header>
    
    <main class="auction-page">
        <header class="auction-header">
            <h1 class="auction-title" data-title="1969 Ford Mustang Boss 429">1969 Ford Mustang Boss 429</h1>
            <p class="auction-subtitle" data-subtitle="Numbers-Matching, 23K Miles, Candy Apple Red">Numbers-Matching, 23K Miles, Candy Apple Red</p>
        </header>
        
        <div class="gallery">
            <div class="gallery-main">🚗</div>
            <div class="gallery-thumbs">
                <div class="gallery-thumb">🚗</div>
                <div class="gallery-thumb">🔧</div>
                <div class="gallery-thumb">📊</div>
                <div class="gallery-thumb">📄</div>
                <div class="gallery-thumb">🏷️</div>
            </div>
        </div>
        
        <div class="auction-content">
            <div class="auction-main">
                <div class="details-section" data-vehicle-details>
                    <h3>Vehicle Details</h3>
                    <div class="details-grid">
                        <div class="detail-item" data-make="Ford">
                            <div class="label">Make</div>
                            <div class="value">Ford</div>
                        </div>
                        <div class="detail-item" data-model="Mustang Boss 429">
                            <div class="label">Model</div>
                            <div class="value">Mustang Boss 429</div>
                        </div>
                        <div class="detail-item" data-year="1969">
                            <div class="label">Year</div>
                            <div class="value">1969</div>
                        </div>
                        <div class="detail-item" data-mileage="23450">
                            <div class="label">Mileage</div>
                            <div class="value">23,450</div>
                        </div>
                        <div class="detail-item" data-engine="429ci V8">
                            <div class="label">Engine</div>
                            <div class="value">429ci V8</div>
                        </div>
                        <div class="detail-item" data-transmission="4-Speed Manual">
                            <div class="label">Transmission</div>
                            <div class="value">4-Speed Manual</div>
                        </div>
                        <div class="detail-item" data-color="Candy Apple Red">
                            <div class="label">Exterior Color</div>
                            <div class="value">Candy Apple Red</div>
                        </div>
                        <div class="detail-item" data-interior="Black Leather">
                            <div class="label">Interior</div>
                            <div class="value">Black Leather</div>
                        </div>
                        <div class="detail-item" data-vin="9F02Z177523">
                            <div class="label">VIN</div>
                            <div class="value">9F02Z177523</div>
                        </div>
                        <div class="detail-item" data-location="Scottsdale, AZ">
                            <div class="label">Location</div>
                            <div class="value">Scottsdale, AZ</div>
                        </div>
                    </div>
                </div>
                
                <div class="bid-history-section" data-bid-history>
                    <h3>Bid History <span data-total-bids="89">89 bids</span></h3>
                    <div class="bid-timeline">
                        <div class="bid-entry" data-bid-rank="1">
                            <div class="position">1</div>
                            <div class="details">
                                <div class="bidder" data-bidder="musclecar_mike">musclecar_mike</div>
                                <div class="time" data-bid-time="2025-01-17T14:32:00">Just now</div>
                            </div>
                            <div class="amount" data-bid-amount="287000">$287,000</div>
                        </div>
                        <div class="bid-entry" data-bid-rank="2">
                            <div class="position other">2</div>
                            <div class="details">
                                <div class="bidder" data-bidder="classic_collector">classic_collector</div>
                                <div class="time" data-bid-time="2025-01-17T14:28:00">4 min ago</div>
                            </div>
                            <div class="amount">$285,000</div>
                        </div>
                        <div class="bid-entry" data-bid-rank="3">
                            <div class="position other">3</div>
                            <div class="details">
                                <div class="bidder" data-bidder="musclecar_mike">musclecar_mike</div>
                                <div class="time">8 min ago</div>
                            </div>
                            <div class="amount">$280,000</div>
                        </div>
                        <div class="bid-entry" data-bid-rank="4">
                            <div class="position other">4</div>
                            <div class="details">
                                <div class="bidder" data-bidder="boss429fan">boss429fan</div>
                                <div class="time">15 min ago</div>
                            </div>
                            <div class="amount">$275,000</div>
                        </div>
                    </div>
                </div>
            </div>
            
            <div class="auction-sidebar">
                <div class="bid-module" data-auction-status>
                    <div class="bid-status-bar">
                        <div class="status-badge live" data-status="Live">🔴 LIVE</div>
                        <div class="time-left">
                            <div class="label">Ends in</div>
                            <div class="countdown" data-time-remaining="4h 28m">4h 28m</div>
                        </div>
                    </div>
                    
                    <div class="current-bid-section">
                        <div class="label">Current Bid</div>
                        <div class="amount" data-current-bid="287000" data-currency="USD">$287,000</div>
                    </div>
                    
                    <div class="bid-activity">
                        <span data-bid-count="89">🔨 89 bids</span>
                        <span data-comment-count="234">💬 234 comments</span>
                    </div>
                    
                    <div class="reserve-indicator met" data-reserve-met="true">
                        ✓ Reserve Met
                    </div>
                    
                    <div class="bid-form">
                        <div class="bid-input-group">
                            <input type="text" placeholder="$289,000 or more" data-min-bid="289000">
                        </div>
                        <button class="bid-button">Place Bid</button>
                        <div class="bid-info" data-bid-increment="2000">Bid increments: $2,000</div>
                    </div>
                </div>
                
                <div class="seller-card" data-seller-info>
                    <div class="seller-header">
                        <div class="seller-avatar">👤</div>
                        <div class="seller-info">
                            <h4 data-seller="arizona_classics">arizona_classics</h4>
                            <div class="joined" data-seller-joined="2018">Member since 2018</div>
                        </div>
                    </div>
                    <div class="seller-stats">
                        <div class="stat" data-seller-sales="47">
                            <div class="num">47</div>
                            <div class="label">Sold</div>
                        </div>
                        <div class="stat" data-seller-bought="12">
                            <div class="num">12</div>
                            <div class="label">Bought</div>
                        </div>
                        <div class="stat" data-seller-comments="892">
                            <div class="num">892</div>
                            <div class="label">Comments</div>
                        </div>
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
  "itemTitle": "1969 Ford Mustang Boss 429",
  "subtitle": "Numbers-Matching, 23K Miles, Candy Apple Red",
  "currentBid": 287000,
  "currency": "USD",
  "bidCount": 89,
  "commentCount": 234,
  "status": "Live",
  "timeRemaining": "4h 28m",
  "reserveMet": true,
  "minNextBid": 289000,
  "bidIncrement": 2000,
  "highBidder": "musclecar_mike",
  "seller": "arizona_classics",
  "sellerJoined": 2018,
  "sellerSales": 47,
  "vehicle": {
    "make": "Ford",
    "model": "Mustang Boss 429",
    "year": 1969,
    "mileage": 23450,
    "engine": "429ci V8",
    "transmission": "4-Speed Manual",
    "color": "Candy Apple Red",
    "interior": "Black Leather",
    "vin": "9F02Z177523",
    "location": "Scottsdale, AZ"
  },
  "recentBids": [
    {"rank": 1, "bidder": "musclecar_mike", "amount": 287000},
    {"rank": 2, "bidder": "classic_collector", "amount": 285000},
    {"rank": 3, "bidder": "musclecar_mike", "amount": 280000}
  ]
}
```

---

## Scenario 3: Auction Ended - Sold

### HTML Fixture: `AuctionSoldHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>SOLD: Rare Baseball Card | Heritage Auctions</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Museo Sans', Arial, sans-serif; background: #f8f8f8; color: #333; }
        .heritage-header { background: #1e3a5f; padding: 15px 40px; display: flex; justify-content: space-between; align-items: center; }
        .heritage-logo { color: #d4af37; font-size: 1.5rem; font-weight: 700; }
        .header-nav a { color: #fff; text-decoration: none; margin-left: 25px; font-size: 0.9rem; }
        .auction-page { max-width: 1000px; margin: 0 auto; padding: 30px 20px; }
        .sold-banner { background: linear-gradient(135deg, #4caf50, #2e7d32); color: #fff; padding: 20px 30px; border-radius: 12px; margin-bottom: 30px; display: flex; justify-content: space-between; align-items: center; }
        .sold-left { display: flex; align-items: center; gap: 20px; }
        .sold-icon { font-size: 3rem; }
        .sold-text h2 { font-size: 1.5rem; margin-bottom: 5px; }
        .sold-text p { opacity: 0.9; }
        .sold-right { text-align: right; }
        .sold-right .label { font-size: 0.85rem; opacity: 0.9; margin-bottom: 5px; }
        .sold-right .amount { font-size: 2rem; font-weight: 700; }
        .lot-content { display: grid; grid-template-columns: 1fr 350px; gap: 30px; }
        .lot-image { background: #fff; border-radius: 12px; padding: 20px; }
        .lot-image .main { width: 100%; aspect-ratio: 3/4; background: linear-gradient(135deg, #eee, #ddd); border-radius: 8px; display: flex; align-items: center; justify-content: center; font-size: 6rem; margin-bottom: 15px; }
        .lot-image .grade-badge { display: inline-flex; align-items: center; gap: 8px; background: #1e3a5f; color: #fff; padding: 8px 15px; border-radius: 6px; font-weight: 600; }
        .lot-sidebar { }
        .result-card { background: #fff; border-radius: 12px; padding: 25px; margin-bottom: 20px; border: 2px solid #4caf50; }
        .result-header { display: flex; align-items: center; gap: 10px; margin-bottom: 20px; }
        .result-header .badge { background: #4caf50; color: #fff; padding: 5px 12px; border-radius: 20px; font-weight: 600; font-size: 0.85rem; }
        .result-header .date { color: #666; font-size: 0.9rem; }
        .result-price { text-align: center; padding: 20px 0; border-top: 1px solid #eee; border-bottom: 1px solid #eee; margin-bottom: 20px; }
        .result-price .label { color: #666; font-size: 0.9rem; margin-bottom: 8px; }
        .result-price .amount { font-size: 2.5rem; font-weight: 700; color: #4caf50; }
        .result-price .hammer { font-size: 0.9rem; color: #666; margin-top: 5px; }
        .result-stats { display: grid; grid-template-columns: 1fr 1fr; gap: 15px; }
        .stat-box { background: #f5f5f5; padding: 15px; border-radius: 8px; text-align: center; }
        .stat-box .num { font-size: 1.5rem; font-weight: 700; color: #1e3a5f; }
        .stat-box .label { font-size: 0.8rem; color: #666; margin-top: 3px; }
        .lot-details { background: #fff; border-radius: 12px; padding: 25px; }
        .lot-details h3 { font-size: 1.2rem; margin-bottom: 20px; padding-bottom: 15px; border-bottom: 1px solid #eee; }
        .detail-row { display: flex; justify-content: space-between; padding: 12px 0; border-bottom: 1px solid #f5f5f5; font-size: 0.95rem; }
        .detail-row:last-child { border: none; }
        .detail-row .label { color: #666; }
        .detail-row .value { font-weight: 500; }
        .winner-section { margin-top: 20px; padding-top: 20px; border-top: 1px solid #eee; }
        .winner-section h4 { font-size: 0.95rem; color: #666; margin-bottom: 10px; }
        .winner-badge { display: inline-flex; align-items: center; gap: 10px; background: #fef7e0; padding: 12px 20px; border-radius: 8px; font-weight: 600; }
        .winner-badge .trophy { font-size: 1.2rem; }
        .price-history { background: #fff; border-radius: 12px; padding: 25px; margin-top: 20px; }
        .price-history h3 { font-size: 1.1rem; margin-bottom: 20px; }
        .history-table { width: 100%; font-size: 0.9rem; }
        .history-table th { text-align: left; padding: 10px; background: #f5f5f5; color: #666; font-weight: 600; }
        .history-table td { padding: 12px 10px; border-bottom: 1px solid #eee; }
        .history-table tr:last-child td { border: none; }
        .compare-link { color: #1e3a5f; text-decoration: none; font-weight: 600; }
    </style>
</head>
<body>
    <header class="heritage-header">
        <div class="heritage-logo">HERITAGE AUCTIONS</div>
        <nav class="header-nav">
            <a href="#">Auctions</a>
            <a href="#">Archives</a>
            <a href="#">Consign</a>
        </nav>
    </header>
    
    <main class="auction-page">
        <div class="sold-banner" data-auction-result="sold">
            <div class="sold-left">
                <span class="sold-icon">🏆</span>
                <div class="sold-text">
                    <h2 data-result-status="Sold">SOLD!</h2>
                    <p data-sold-date="2025-01-16T20:45:00">Auction ended January 16, 2025 at 8:45 PM EST</p>
                </div>
            </div>
            <div class="sold-right">
                <div class="label">Final Price</div>
                <div class="amount" data-final-price="1265000" data-currency="USD">$1,265,000</div>
            </div>
        </div>
        
        <div class="lot-content">
            <div class="lot-image">
                <div class="main">⚾</div>
                <div class="grade-badge" data-grade="PSA 9">
                    <span>🏅</span>
                    PSA 9 (Mint)
                </div>
            </div>
            
            <div class="lot-sidebar">
                <div class="result-card">
                    <div class="result-header">
                        <span class="badge">SOLD</span>
                        <span class="date" data-auction-date="2025-01-16">January 16, 2025</span>
                    </div>
                    
                    <div class="result-price">
                        <div class="label">Final Price (with Buyer's Premium)</div>
                        <div class="amount" data-total-price="1265000">$1,265,000</div>
                        <div class="hammer" data-hammer-price="1100000">Hammer Price: $1,100,000</div>
                    </div>
                    
                    <div class="result-stats">
                        <div class="stat-box" data-bid-count="47">
                            <div class="num">47</div>
                            <div class="label">Total Bids</div>
                        </div>
                        <div class="stat-box" data-bidder-count="12">
                            <div class="num">12</div>
                            <div class="label">Bidders</div>
                        </div>
                        <div class="stat-box" data-page-views="15842">
                            <div class="num">15.8K</div>
                            <div class="label">Page Views</div>
                        </div>
                        <div class="stat-box" data-watchers="328">
                            <div class="num">328</div>
                            <div class="label">Watchers</div>
                        </div>
                    </div>
                    
                    <div class="winner-section" data-winner-info>
                        <h4>Winning Bidder</h4>
                        <div class="winner-badge">
                            <span class="trophy">🏆</span>
                            <span data-winning-bidder="b***8">b***8</span>
                        </div>
                    </div>
                </div>
                
                <div class="lot-details">
                    <h3 data-lot-title="1952 Topps Mickey Mantle #311">1952 Topps Mickey Mantle #311</h3>
                    <div class="detail-row" data-lot-number="50234">
                        <span class="label">Lot Number</span>
                        <span class="value">#50234</span>
                    </div>
                    <div class="detail-row" data-category="Sports Cards">
                        <span class="label">Category</span>
                        <span class="value">Sports Cards</span>
                    </div>
                    <div class="detail-row" data-grading-service="PSA">
                        <span class="label">Grading Service</span>
                        <span class="value">PSA</span>
                    </div>
                    <div class="detail-row" data-grade="9">
                        <span class="label">Grade</span>
                        <span class="value">9 (Mint)</span>
                    </div>
                    <div class="detail-row" data-cert-number="12345678">
                        <span class="label">Cert Number</span>
                        <span class="value">12345678</span>
                    </div>
                    <div class="detail-row" data-estimate-low="800000" data-estimate-high="1200000">
                        <span class="label">Estimate</span>
                        <span class="value">$800,000 - $1,200,000</span>
                    </div>
                    <div class="detail-row" data-buyers-premium="15">
                        <span class="label">Buyer's Premium</span>
                        <span class="value">15%</span>
                    </div>
                </div>
            </div>
        </div>
        
        <div class="price-history" data-price-history>
            <h3>Price History - 1952 Topps Mantle PSA 9</h3>
            <table class="history-table">
                <thead>
                    <tr>
                        <th>Date</th>
                        <th>Auction House</th>
                        <th>Price</th>
                        <th>Change</th>
                    </tr>
                </thead>
                <tbody>
                    <tr data-history-entry="current">
                        <td>Jan 2025</td>
                        <td>Heritage Auctions</td>
                        <td>$1,265,000</td>
                        <td style="color: #4caf50;">+12.4%</td>
                    </tr>
                    <tr data-history-entry="previous">
                        <td>Aug 2023</td>
                        <td>Goldin Auctions</td>
                        <td>$1,125,000</td>
                        <td style="color: #4caf50;">+8.7%</td>
                    </tr>
                    <tr data-history-entry="older">
                        <td>Nov 2021</td>
                        <td>PWCC</td>
                        <td>$1,035,000</td>
                        <td>—</td>
                    </tr>
                </tbody>
            </table>
        </div>
    </main>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "lotTitle": "1952 Topps Mickey Mantle #311",
  "lotNumber": "50234",
  "auctionResult": "sold",
  "soldDate": "2025-01-16T20:45:00",
  "finalPrice": 1265000,
  "hammerPrice": 1100000,
  "currency": "USD",
  "buyersPremium": 15,
  "bidCount": 47,
  "bidderCount": 12,
  "pageViews": 15842,
  "watchers": 328,
  "winningBidder": "b***8",
  "category": "Sports Cards",
  "gradingService": "PSA",
  "grade": 9,
  "certNumber": "12345678",
  "estimateLow": 800000,
  "estimateHigh": 1200000,
  "priceHistory": [
    {"date": "Jan 2025", "auctionHouse": "Heritage Auctions", "price": 1265000, "change": "+12.4%"},
    {"date": "Aug 2023", "auctionHouse": "Goldin Auctions", "price": 1125000, "change": "+8.7%"},
    {"date": "Nov 2021", "auctionHouse": "PWCC", "price": 1035000}
  ]
}
```

---

## Test Implementation Notes

### Test Structure

```csharp
[Test]
[Category("LlmCached")]
public async Task ExtractAuction_EbayLiveAuction_DetectsEndingSoon()
{
    var llmProvider = await CreateRealLlmProvider();
    var service = CreateAuctionExtractionService(llmProvider);
    
    var result = await service.ExtractAuctionInfoAsync(EbayAuctionHtml);
    
    result.ShouldNotBeNull();
    result.CurrentBid.ShouldBe(47250m);
    result.BidCount.ShouldBe(67);
    result.ReserveMet.ShouldBeTrue();
    result.HoursRemaining.ShouldBeLessThan(3);
}

[Test]
[Category("LlmCached")]
public async Task ExtractAuction_CarAuction_ExtractsVehicleDetails()
{
    var llmProvider = await CreateRealLlmProvider();
    var service = CreateAuctionExtractionService(llmProvider);
    
    var result = await service.ExtractAuctionInfoAsync(CarAuctionHtml);
    
    result.ShouldNotBeNull();
    result.Vehicle.Make.ShouldBe("Ford");
    result.Vehicle.Model.ShouldBe("Mustang Boss 429");
    result.Vehicle.Year.ShouldBe(1969);
    result.CurrentBid.ShouldBeGreaterThan(200000m);
}

[Test]
[Category("LlmCached")]
public async Task ExtractAuction_SoldItem_ExtractsFinalPriceAndWinner()
{
    var llmProvider = await CreateRealLlmProvider();
    var service = CreateAuctionExtractionService(llmProvider);
    
    var result = await service.ExtractAuctionInfoAsync(AuctionSoldHtml);
    
    result.ShouldNotBeNull();
    result.AuctionResult.ShouldBe("sold");
    result.FinalPrice.ShouldBe(1265000m);
    result.WinningBidder.ShouldNotBeNullOrEmpty();
}
```

### Extraction Fields Schema

```json
{
  "type": "auction",
  "fields": {
    "itemTitle": "string",
    "currentBid": "decimal",
    "bidCount": "number",
    "highBidder": "string?",
    "timeRemaining": "string?",
    "endTime": "datetime?",
    "reserveMet": "boolean?",
    "buyItNowAvailable": "boolean",
    "buyItNowPrice": "decimal?",
    "watchers": "number?",
    "auctionResult": "enum(active|sold|unsold|cancelled)?",
    "finalPrice": "decimal?",
    "winningBidder": "string?",
    "seller": "string",
    "sellerRating": "number?"
  }
}
```
