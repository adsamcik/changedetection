# Real Estate Listing Monitoring

## Overview

Users monitor property listings for changes in:
- **Price drops** (watching for motivated sellers)
- **New listings** (first to market advantage)
- **Status changes** (pending, sold, back on market)
- **Open house schedules** (planning visits)
- **Days on market** (negotiation leverage)

## Key Fields to Extract

| Field | Description | Examples |
|-------|-------------|----------|
| `address` | Property address | "123 Oak Street, Austin, TX 78701" |
| `price` | List price | 750000 |
| `priceHistory` | Price changes | `[{"date": "2025-01-01", "price": 800000}]` |
| `status` | Listing status | "Active", "Pending", "Sold" |
| `bedrooms` | Bed count | 4 |
| `bathrooms` | Bath count | 3 |
| `sqft` | Square footage | 2450 |
| `daysOnMarket` | DOM count | 45 |
| `openHouse` | Next open house | "2025-01-18 1:00 PM - 4:00 PM" |

---

## Scenario 1: Zillow Property Listing

**Context**: User monitoring a property for price drops

### HTML Fixture: `ZillowListingHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>123 Oak Street, Austin, TX 78701 | Zillow</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Open Sans', sans-serif; background: #f0f0f0; color: #333; }
        .zillow-header { background: #006aff; padding: 14px 30px; display: flex; justify-content: space-between; align-items: center; }
        .zillow-logo { color: #fff; font-size: 1.6rem; font-weight: 700; }
        .search-bar { flex: 1; max-width: 500px; margin: 0 30px; }
        .search-bar input { width: 100%; padding: 12px 20px; border: none; border-radius: 8px; font-size: 1rem; }
        .nav-links a { color: #fff; text-decoration: none; margin-left: 25px; font-size: 0.9rem; }
        .property-hero { height: 450px; background: linear-gradient(to bottom, transparent 60%, rgba(0,0,0,0.7)), linear-gradient(135deg, #667eea, #764ba2); display: flex; align-items: flex-end; padding: 30px; position: relative; }
        .photo-count { position: absolute; top: 20px; right: 20px; background: rgba(0,0,0,0.7); color: #fff; padding: 10px 20px; border-radius: 8px; }
        .hero-content { color: #fff; width: 100%; display: flex; justify-content: space-between; align-items: flex-end; }
        .hero-left { max-width: 60%; }
        .property-status { display: inline-block; background: #d4edda; color: #155724; padding: 6px 15px; border-radius: 4px; font-weight: 600; font-size: 0.85rem; margin-bottom: 10px; }
        .property-status.pending { background: #fff3cd; color: #856404; }
        .property-status.sold { background: #f8d7da; color: #721c24; }
        .property-address { font-size: 1.8rem; font-weight: 700; margin-bottom: 5px; }
        .property-city { font-size: 1.1rem; opacity: 0.9; }
        .hero-right { text-align: right; }
        .hero-price { font-size: 2.5rem; font-weight: 700; }
        .price-change { font-size: 0.9rem; margin-top: 5px; }
        .price-change.down { color: #90EE90; }
        .price-change.up { color: #ffcccb; }
        .main-content { max-width: 1200px; margin: 0 auto; padding: 30px; display: grid; grid-template-columns: 2fr 1fr; gap: 30px; }
        .property-details { background: #fff; border-radius: 12px; padding: 30px; box-shadow: 0 2px 8px rgba(0,0,0,0.1); }
        .key-facts { display: grid; grid-template-columns: repeat(4, 1fr); gap: 20px; padding-bottom: 25px; border-bottom: 1px solid #eee; margin-bottom: 25px; }
        .fact-item { text-align: center; }
        .fact-value { font-size: 1.8rem; font-weight: 700; color: #006aff; }
        .fact-label { font-size: 0.85rem; color: #666; margin-top: 5px; }
        .description-section { margin-bottom: 25px; }
        .description-section h3 { font-size: 1.2rem; margin-bottom: 15px; }
        .description-section p { color: #555; line-height: 1.7; }
        .features-grid { display: grid; grid-template-columns: repeat(2, 1fr); gap: 12px; margin-top: 15px; }
        .feature-item { display: flex; align-items: center; gap: 10px; font-size: 0.9rem; color: #555; }
        .feature-item .check { color: #4caf50; }
        .price-history { margin-top: 25px; padding-top: 25px; border-top: 1px solid #eee; }
        .price-history h3 { margin-bottom: 15px; display: flex; align-items: center; gap: 10px; }
        .history-list { list-style: none; }
        .history-item { display: flex; justify-content: space-between; padding: 12px 0; border-bottom: 1px solid #f0f0f0; font-size: 0.9rem; }
        .history-item:last-child { border: none; }
        .history-item .date { color: #666; }
        .history-item .event { font-weight: 500; }
        .history-item .price { font-weight: 600; }
        .history-item .change { font-size: 0.8rem; }
        .history-item .change.down { color: #4caf50; }
        .history-item .change.up { color: #f44336; }
        .sidebar { display: flex; flex-direction: column; gap: 20px; }
        .agent-card { background: #fff; border-radius: 12px; padding: 25px; box-shadow: 0 2px 8px rgba(0,0,0,0.1); }
        .agent-card h3 { margin-bottom: 20px; font-size: 1.1rem; }
        .agent-info { display: flex; gap: 15px; margin-bottom: 20px; }
        .agent-photo { width: 60px; height: 60px; background: linear-gradient(135deg, #667eea, #764ba2); border-radius: 50%; }
        .agent-details h4 { font-size: 1rem; margin-bottom: 3px; }
        .agent-details span { color: #666; font-size: 0.85rem; }
        .contact-btn { width: 100%; padding: 14px; background: #006aff; color: #fff; border: none; border-radius: 8px; font-size: 1rem; font-weight: 600; cursor: pointer; margin-bottom: 10px; }
        .contact-btn.outline { background: #fff; color: #006aff; border: 2px solid #006aff; }
        .open-house-card { background: linear-gradient(135deg, #e3f2fd, #bbdefb); border: 2px solid #2196f3; border-radius: 12px; padding: 25px; }
        .open-house-card h3 { color: #1565c0; margin-bottom: 15px; display: flex; align-items: center; gap: 10px; }
        .open-house-date { font-size: 1.2rem; font-weight: 600; margin-bottom: 5px; }
        .open-house-time { color: #555; margin-bottom: 15px; }
        .rsvp-btn { width: 100%; padding: 12px; background: #2196f3; color: #fff; border: none; border-radius: 8px; font-weight: 600; cursor: pointer; }
        .market-stats { background: #fff; border-radius: 12px; padding: 25px; box-shadow: 0 2px 8px rgba(0,0,0,0.1); }
        .market-stats h3 { margin-bottom: 20px; }
        .stat-row { display: flex; justify-content: space-between; padding: 12px 0; border-bottom: 1px solid #f0f0f0; font-size: 0.9rem; }
        .stat-row:last-child { border: none; }
        .stat-row .label { color: #666; }
        .stat-row .value { font-weight: 600; }
        .stat-row .value.highlight { color: #ff9800; }
        .save-alert { background: #fff; border-radius: 12px; padding: 25px; box-shadow: 0 2px 8px rgba(0,0,0,0.1); text-align: center; }
        .save-alert h4 { margin-bottom: 10px; }
        .save-alert p { color: #666; font-size: 0.9rem; margin-bottom: 15px; }
        .alert-btn { padding: 12px 25px; background: #fff; color: #006aff; border: 2px solid #006aff; border-radius: 8px; font-weight: 600; cursor: pointer; }
    </style>
</head>
<body>
    <header class="zillow-header">
        <div class="zillow-logo">zillow</div>
        <div class="search-bar">
            <input type="text" placeholder="Enter an address, city, or ZIP code">
        </div>
        <nav class="nav-links">
            <a href="#">Buy</a>
            <a href="#">Rent</a>
            <a href="#">Sell</a>
            <a href="#">Saved</a>
        </nav>
    </header>
    
    <section class="property-hero">
        <div class="photo-count">📷 47 Photos</div>
        <div class="hero-content">
            <div class="hero-left">
                <span class="property-status" data-status="For Sale">For Sale</span>
                <h1 class="property-address" data-address="123 Oak Street">123 Oak Street</h1>
                <div class="property-city" data-city="Austin" data-state="TX" data-zip="78701">Austin, TX 78701</div>
            </div>
            <div class="hero-right">
                <div class="hero-price" data-price="725000" data-currency="USD">$725,000</div>
                <div class="price-change down" data-price-change="-75000" data-change-date="2025-01-10">
                    ▼ $75,000 (Jan 10) - Price Cut!
                </div>
            </div>
        </div>
    </section>
    
    <main class="main-content">
        <div class="property-details">
            <div class="key-facts">
                <div class="fact-item" data-beds="4">
                    <div class="fact-value">4</div>
                    <div class="fact-label">Bedrooms</div>
                </div>
                <div class="fact-item" data-baths="3">
                    <div class="fact-value">3</div>
                    <div class="fact-label">Bathrooms</div>
                </div>
                <div class="fact-item" data-sqft="2450">
                    <div class="fact-value">2,450</div>
                    <div class="fact-label">Sq Ft</div>
                </div>
                <div class="fact-item" data-price-per-sqft="296">
                    <div class="fact-value">$296</div>
                    <div class="fact-label">Price/Sq Ft</div>
                </div>
            </div>
            
            <div class="description-section">
                <h3>About This Home</h3>
                <p data-listing-id="ZL-284719365">Stunning modern farmhouse in the heart of Austin! This beautifully renovated 4-bedroom home features an open floor plan, chef's kitchen with quartz countertops, and a gorgeous primary suite with spa-like bathroom. Situated on a quiet tree-lined street, just minutes from downtown.</p>
            </div>
            
            <div class="description-section">
                <h3>Home Features</h3>
                <div class="features-grid">
                    <div class="feature-item"><span class="check">✓</span> Central A/C</div>
                    <div class="feature-item"><span class="check">✓</span> Hardwood Floors</div>
                    <div class="feature-item"><span class="check">✓</span> 2-Car Garage</div>
                    <div class="feature-item"><span class="check">✓</span> Pool</div>
                    <div class="feature-item"><span class="check">✓</span> Updated Kitchen</div>
                    <div class="feature-item"><span class="check">✓</span> Smart Home</div>
                </div>
            </div>
            
            <div class="price-history" data-price-history>
                <h3>📈 Price History</h3>
                <ul class="history-list">
                    <li class="history-item" data-event-date="2025-01-10">
                        <span class="date">Jan 10, 2025</span>
                        <span class="event">Price Cut</span>
                        <span class="price">$725,000</span>
                        <span class="change down">-$75,000 (-9.4%)</span>
                    </li>
                    <li class="history-item" data-event-date="2024-12-15">
                        <span class="date">Dec 15, 2024</span>
                        <span class="event">Listed</span>
                        <span class="price">$800,000</span>
                        <span class="change">Original</span>
                    </li>
                    <li class="history-item" data-event-date="2019-03-22">
                        <span class="date">Mar 22, 2019</span>
                        <span class="event">Sold</span>
                        <span class="price">$520,000</span>
                        <span class="change">—</span>
                    </li>
                </ul>
            </div>
        </div>
        
        <aside class="sidebar">
            <div class="open-house-card" data-open-house="true">
                <h3>🏠 Open House</h3>
                <div class="open-house-date" data-oh-date="2025-01-18">Saturday, Jan 18</div>
                <div class="open-house-time" data-oh-time="13:00-16:00">1:00 PM - 4:00 PM</div>
                <button class="rsvp-btn">RSVP for Open House</button>
            </div>
            
            <div class="agent-card">
                <h3>Listed By</h3>
                <div class="agent-info">
                    <div class="agent-photo"></div>
                    <div class="agent-details">
                        <h4>Sarah Mitchell</h4>
                        <span>Compass Real Estate</span>
                    </div>
                </div>
                <button class="contact-btn">Contact Agent</button>
                <button class="contact-btn outline">Request a Tour</button>
            </div>
            
            <div class="market-stats">
                <h3>Market Insights</h3>
                <div class="stat-row" data-dom="27">
                    <span class="label">Days on Zillow</span>
                    <span class="value highlight">27 days</span>
                </div>
                <div class="stat-row" data-views="1847">
                    <span class="label">Views</span>
                    <span class="value">1,847</span>
                </div>
                <div class="stat-row" data-saves="89">
                    <span class="label">Saves</span>
                    <span class="value">89</span>
                </div>
                <div class="stat-row" data-zestimate="745000">
                    <span class="label">Zestimate®</span>
                    <span class="value">$745,000</span>
                </div>
            </div>
            
            <div class="save-alert" data-alert-available="true">
                <h4>🔔 Get Price Alerts</h4>
                <p>Get notified when the price changes on this home</p>
                <button class="alert-btn">Save &amp; Get Alerts</button>
            </div>
        </aside>
    </main>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "address": "123 Oak Street",
  "city": "Austin",
  "state": "TX",
  "zip": "78701",
  "price": 725000,
  "currency": "USD",
  "status": "For Sale",
  "bedrooms": 4,
  "bathrooms": 3,
  "sqft": 2450,
  "pricePerSqft": 296,
  "priceChange": -75000,
  "priceChangeDate": "2025-01-10",
  "daysOnMarket": 27,
  "views": 1847,
  "saves": 89,
  "zestimate": 745000,
  "openHouse": {
    "date": "2025-01-18",
    "time": "1:00 PM - 4:00 PM"
  },
  "priceHistory": [
    {"date": "2025-01-10", "event": "Price Cut", "price": 725000},
    {"date": "2024-12-15", "event": "Listed", "price": 800000},
    {"date": "2019-03-22", "event": "Sold", "price": 520000}
  ],
  "alertAvailable": true
}
```

---

## Scenario 2: Redfin Pending Sale

### HTML Fixture: `RedfinPendingHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>456 Maple Avenue - Pending | Redfin</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Source Sans Pro', sans-serif; background: #fff; color: #333; }
        .redfin-header { background: #a02021; padding: 12px 30px; display: flex; justify-content: space-between; align-items: center; }
        .redfin-logo { color: #fff; font-size: 1.5rem; font-weight: 700; }
        .header-nav { display: flex; gap: 25px; }
        .header-nav a { color: #fff; text-decoration: none; font-size: 0.9rem; }
        .status-ribbon { background: #fff3cd; color: #856404; padding: 12px 30px; text-align: center; font-weight: 600; display: flex; align-items: center; justify-content: center; gap: 10px; }
        .status-ribbon.pending { background: #fff3cd; color: #856404; }
        .status-ribbon.sold { background: #f8d7da; color: #721c24; }
        .status-ribbon.contingent { background: #e2e3e5; color: #383d41; }
        .property-header { padding: 30px; background: #f8f9fa; border-bottom: 1px solid #e0e0e0; }
        .header-content { max-width: 1200px; margin: 0 auto; display: flex; justify-content: space-between; align-items: flex-start; }
        .address-section { }
        .address-section h1 { font-size: 1.8rem; font-weight: 600; margin-bottom: 8px; }
        .address-section .location { color: #666; font-size: 1.1rem; }
        .price-section { text-align: right; }
        .price-section .price { font-size: 2rem; font-weight: 700; color: #333; }
        .price-section .estimate { color: #666; font-size: 0.9rem; margin-top: 5px; }
        .price-section .estimate strong { color: #a02021; }
        .gallery-section { display: grid; grid-template-columns: 2fr 1fr 1fr; grid-template-rows: 1fr 1fr; gap: 5px; height: 400px; background: #f0f0f0; }
        .gallery-section .main-photo { grid-row: span 2; background: linear-gradient(135deg, #667eea, #764ba2); display: flex; align-items: center; justify-content: center; font-size: 6rem; }
        .gallery-section .thumb { background: linear-gradient(135deg, #764ba2, #667eea); display: flex; align-items: center; justify-content: center; font-size: 3rem; }
        .main-content { max-width: 1200px; margin: 0 auto; padding: 30px; display: grid; grid-template-columns: 1fr 400px; gap: 40px; }
        .property-info { }
        .stats-row { display: flex; gap: 30px; padding: 20px 0; border-bottom: 1px solid #e0e0e0; }
        .stat { text-align: center; }
        .stat .value { font-size: 1.5rem; font-weight: 700; }
        .stat .label { color: #666; font-size: 0.85rem; }
        .pending-notice { background: #fff3cd; border: 2px solid #ffc107; border-radius: 10px; padding: 25px; margin: 25px 0; }
        .pending-notice h3 { color: #856404; margin-bottom: 10px; display: flex; align-items: center; gap: 10px; }
        .pending-notice p { color: #666; font-size: 0.95rem; margin-bottom: 15px; }
        .pending-notice .offer-date { font-weight: 600; color: #333; }
        .backup-form { background: #f8f9fa; border-radius: 8px; padding: 20px; }
        .backup-form h4 { margin-bottom: 12px; font-size: 0.95rem; }
        .backup-form input { width: 100%; padding: 12px; border: 1px solid #ddd; border-radius: 6px; margin-bottom: 12px; }
        .backup-form button { width: 100%; padding: 12px; background: #a02021; color: #fff; border: none; border-radius: 6px; font-weight: 600; cursor: pointer; }
        .timeline-section { margin: 30px 0; }
        .timeline-section h3 { margin-bottom: 20px; }
        .timeline { position: relative; padding-left: 30px; }
        .timeline::before { content: ""; position: absolute; left: 8px; top: 0; bottom: 0; width: 2px; background: #e0e0e0; }
        .timeline-item { position: relative; padding-bottom: 25px; }
        .timeline-item::before { content: ""; position: absolute; left: -26px; width: 14px; height: 14px; border-radius: 50%; border: 2px solid #a02021; background: #fff; }
        .timeline-item.current::before { background: #a02021; }
        .timeline-item .date { color: #666; font-size: 0.85rem; margin-bottom: 5px; }
        .timeline-item .event { font-weight: 600; }
        .timeline-item .details { color: #666; font-size: 0.9rem; margin-top: 5px; }
        .comparable-sales { margin: 30px 0; }
        .comparable-sales h3 { margin-bottom: 20px; }
        .comp-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 15px; }
        .comp-card { background: #f8f9fa; border-radius: 10px; padding: 15px; }
        .comp-card .address { font-weight: 600; font-size: 0.9rem; margin-bottom: 5px; }
        .comp-card .price { color: #a02021; font-weight: 600; }
        .comp-card .details { color: #666; font-size: 0.8rem; margin-top: 5px; }
        .sidebar { }
        .contact-card { background: #f8f9fa; border-radius: 12px; padding: 25px; margin-bottom: 20px; }
        .contact-card h3 { margin-bottom: 20px; }
        .agent-row { display: flex; gap: 15px; margin-bottom: 20px; }
        .agent-avatar { width: 60px; height: 60px; background: linear-gradient(135deg, #a02021, #d4424e); border-radius: 50%; }
        .agent-info h4 { font-size: 1rem; margin-bottom: 3px; }
        .agent-info span { color: #666; font-size: 0.85rem; }
        .contact-btn { width: 100%; padding: 14px; margin-bottom: 10px; border-radius: 8px; font-weight: 600; cursor: pointer; }
        .contact-btn.primary { background: #a02021; color: #fff; border: none; }
        .contact-btn.secondary { background: #fff; color: #a02021; border: 2px solid #a02021; }
        .activity-card { background: #f8f9fa; border-radius: 12px; padding: 25px; }
        .activity-card h3 { margin-bottom: 20px; }
        .activity-stat { display: flex; justify-content: space-between; padding: 12px 0; border-bottom: 1px solid #e0e0e0; }
        .activity-stat:last-child { border: none; }
        .activity-stat .label { color: #666; }
        .activity-stat .value { font-weight: 600; }
        .activity-stat .value.hot { color: #e53935; }
    </style>
</head>
<body>
    <header class="redfin-header">
        <div class="redfin-logo">redfin</div>
        <nav class="header-nav">
            <a href="#">Buy</a>
            <a href="#">Sell</a>
            <a href="#">Rent</a>
            <a href="#">Mortgage</a>
        </nav>
    </header>
    
    <div class="status-ribbon pending" data-status="Pending">
        <span>⏳</span>
        <span>Sale Pending - Offer Accepted January 8, 2025</span>
    </div>
    
    <section class="property-header">
        <div class="header-content">
            <div class="address-section">
                <h1 data-address="456 Maple Avenue">456 Maple Avenue</h1>
                <div class="location" data-city="Seattle" data-state="WA" data-zip="98103">Seattle, WA 98103</div>
            </div>
            <div class="price-section">
                <div class="price" data-price="895000">$895,000</div>
                <div class="estimate">
                    Redfin Estimate: <strong data-estimate="915000">$915,000</strong>
                </div>
            </div>
        </div>
    </section>
    
    <section class="gallery-section">
        <div class="main-photo">🏡</div>
        <div class="thumb">🏡</div>
        <div class="thumb">🏡</div>
        <div class="thumb">🏡</div>
        <div class="thumb">🏡</div>
    </section>
    
    <main class="main-content">
        <div class="property-info">
            <div class="stats-row">
                <div class="stat" data-beds="3">
                    <div class="value">3</div>
                    <div class="label">Beds</div>
                </div>
                <div class="stat" data-baths="2.5">
                    <div class="value">2.5</div>
                    <div class="label">Baths</div>
                </div>
                <div class="stat" data-sqft="1875">
                    <div class="value">1,875</div>
                    <div class="label">Sq Ft</div>
                </div>
                <div class="stat" data-year-built="1962">
                    <div class="value">1962</div>
                    <div class="label">Year Built</div>
                </div>
                <div class="stat" data-lot-size="5200">
                    <div class="value">5,200</div>
                    <div class="label">Lot Sq Ft</div>
                </div>
            </div>
            
            <div class="pending-notice" data-offer-accepted="2025-01-08" data-expected-close="2025-02-10">
                <h3>⏳ Sale Pending</h3>
                <p>This home has an accepted offer. The sale is expected to close on <span class="offer-date" data-close-date="2025-02-10">February 10, 2025</span>.</p>
                <p>Deals can still fall through! Get notified if this home comes back on the market.</p>
                <div class="backup-form" data-backup-offer="true">
                    <h4>🔔 Get Back on Market Alerts</h4>
                    <input type="email" placeholder="Enter your email">
                    <button>Notify Me If Available</button>
                </div>
            </div>
            
            <div class="timeline-section" data-listing-timeline>
                <h3>Listing Timeline</h3>
                <div class="timeline">
                    <div class="timeline-item current" data-event-date="2025-01-08">
                        <div class="date">Jan 8, 2025</div>
                        <div class="event">Pending</div>
                        <div class="details">Offer accepted after 12 days on market</div>
                    </div>
                    <div class="timeline-item" data-event-date="2025-01-05">
                        <div class="date">Jan 5, 2025</div>
                        <div class="event">Price Reduced</div>
                        <div class="details">$895,000 → Previous: $925,000 (-$30,000)</div>
                    </div>
                    <div class="timeline-item" data-event-date="2024-12-27">
                        <div class="date">Dec 27, 2024</div>
                        <div class="event">Listed for Sale</div>
                        <div class="details">Original list price: $925,000</div>
                    </div>
                </div>
            </div>
            
            <div class="comparable-sales">
                <h3>Recent Comparable Sales</h3>
                <div class="comp-grid">
                    <div class="comp-card">
                        <div class="address">448 Maple Avenue</div>
                        <div class="price">$878,000</div>
                        <div class="details">3 bd | 2 ba | 1,750 sqft | Sold Dec 15</div>
                    </div>
                    <div class="comp-card">
                        <div class="address">512 Oak Street</div>
                        <div class="price">$920,000</div>
                        <div class="details">3 bd | 2.5 ba | 1,920 sqft | Sold Jan 3</div>
                    </div>
                    <div class="comp-card">
                        <div class="address">389 Pine Lane</div>
                        <div class="price">$865,000</div>
                        <div class="details">3 bd | 2 ba | 1,680 sqft | Sold Dec 20</div>
                    </div>
                </div>
            </div>
        </div>
        
        <aside class="sidebar">
            <div class="contact-card">
                <h3>Listed By</h3>
                <div class="agent-row">
                    <div class="agent-avatar"></div>
                    <div class="agent-info">
                        <h4>Michael Chen</h4>
                        <span>Redfin Agent</span>
                    </div>
                </div>
                <button class="contact-btn primary">Ask a Question</button>
                <button class="contact-btn secondary">Schedule Tour</button>
            </div>
            
            <div class="activity-card">
                <h3>Property Activity</h3>
                <div class="activity-stat" data-dom="12">
                    <span class="label">Days on Market</span>
                    <span class="value">12</span>
                </div>
                <div class="activity-stat" data-original-price="925000">
                    <span class="label">Original Price</span>
                    <span class="value">$925,000</span>
                </div>
                <div class="activity-stat" data-price-drops="1">
                    <span class="label">Price Drops</span>
                    <span class="value">1</span>
                </div>
                <div class="activity-stat" data-total-reduction="30000">
                    <span class="label">Total Reduction</span>
                    <span class="value">-$30,000</span>
                </div>
                <div class="activity-stat" data-page-views="2341">
                    <span class="label">Page Views</span>
                    <span class="value hot">2,341</span>
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
  "address": "456 Maple Avenue",
  "city": "Seattle",
  "state": "WA",
  "zip": "98103",
  "price": 895000,
  "status": "Pending",
  "bedrooms": 3,
  "bathrooms": 2.5,
  "sqft": 1875,
  "yearBuilt": 1962,
  "lotSize": 5200,
  "redfinEstimate": 915000,
  "offerAcceptedDate": "2025-01-08",
  "expectedCloseDate": "2025-02-10",
  "daysOnMarket": 12,
  "originalPrice": 925000,
  "priceDrops": 1,
  "totalReduction": 30000,
  "pageViews": 2341,
  "backOnMarketAlertAvailable": true,
  "timeline": [
    {"date": "2025-01-08", "event": "Pending"},
    {"date": "2025-01-05", "event": "Price Reduced"},
    {"date": "2024-12-27", "event": "Listed for Sale"}
  ]
}
```

---

## Scenario 3: Back on Market Property

### HTML Fixture: `BackOnMarketHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>789 Cedar Lane - BACK ON MARKET | Realtor.com</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Roboto', sans-serif; background: #f5f5f5; color: #333; }
        .realtor-header { background: #d92228; padding: 12px 30px; display: flex; align-items: center; gap: 30px; }
        .realtor-logo { color: #fff; font-size: 1.4rem; font-weight: 700; }
        .search-wrapper { flex: 1; max-width: 500px; }
        .search-wrapper input { width: 100%; padding: 12px 20px; border: none; border-radius: 6px; }
        .back-on-market-banner { background: linear-gradient(135deg, #4caf50, #2e7d32); color: #fff; padding: 20px 30px; display: flex; align-items: center; justify-content: center; gap: 15px; font-weight: 600; }
        .banner-icon { font-size: 2rem; }
        .banner-text { font-size: 1.2rem; }
        .banner-subtext { opacity: 0.9; font-size: 0.9rem; font-weight: 400; margin-top: 3px; }
        .main-grid { max-width: 1200px; margin: 0 auto; padding: 30px; display: grid; grid-template-columns: 1fr 380px; gap: 30px; }
        .property-card { background: #fff; border-radius: 12px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); overflow: hidden; }
        .photo-section { height: 350px; background: linear-gradient(135deg, #667eea, #764ba2); display: flex; align-items: center; justify-content: center; font-size: 8rem; position: relative; }
        .status-tag { position: absolute; top: 20px; left: 20px; background: #4caf50; color: #fff; padding: 10px 20px; border-radius: 6px; font-weight: 700; display: flex; align-items: center; gap: 8px; }
        .info-section { padding: 25px; }
        .price-row { display: flex; justify-content: space-between; align-items: flex-start; margin-bottom: 15px; }
        .price { font-size: 2rem; font-weight: 700; }
        .price-note { background: #e8f5e9; color: #2e7d32; padding: 5px 12px; border-radius: 4px; font-size: 0.8rem; font-weight: 600; }
        .address-row { margin-bottom: 20px; }
        .address { font-size: 1.3rem; font-weight: 600; margin-bottom: 5px; }
        .city-state { color: #666; }
        .specs-row { display: flex; gap: 25px; padding: 15px 0; border-top: 1px solid #eee; border-bottom: 1px solid #eee; }
        .spec { }
        .spec-value { font-weight: 700; font-size: 1.1rem; }
        .spec-label { color: #666; font-size: 0.8rem; }
        .opportunity-section { background: linear-gradient(135deg, #e8f5e9, #c8e6c9); border-radius: 10px; padding: 20px; margin: 20px 0; }
        .opportunity-section h4 { color: #2e7d32; margin-bottom: 10px; display: flex; align-items: center; gap: 10px; }
        .opportunity-section ul { padding-left: 20px; color: #555; font-size: 0.9rem; }
        .opportunity-section li { margin-bottom: 8px; }
        .history-section { margin-top: 20px; }
        .history-section h4 { margin-bottom: 15px; display: flex; align-items: center; gap: 10px; }
        .history-timeline { position: relative; }
        .history-event { display: flex; gap: 20px; padding: 15px 0; border-bottom: 1px solid #eee; }
        .history-event:last-child { border: none; }
        .event-date { width: 100px; color: #666; font-size: 0.85rem; }
        .event-info { flex: 1; }
        .event-title { font-weight: 600; margin-bottom: 3px; }
        .event-title.active { color: #4caf50; }
        .event-title.pending { color: #ff9800; }
        .event-title.listed { color: #2196f3; }
        .event-detail { color: #666; font-size: 0.85rem; }
        .sidebar { display: flex; flex-direction: column; gap: 20px; }
        .cta-card { background: #fff; border-radius: 12px; padding: 25px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }
        .cta-card h3 { margin-bottom: 20px; }
        .tour-btn { width: 100%; padding: 16px; background: #d92228; color: #fff; border: none; border-radius: 8px; font-size: 1rem; font-weight: 600; cursor: pointer; margin-bottom: 12px; }
        .contact-btn { width: 100%; padding: 14px; background: #fff; color: #d92228; border: 2px solid #d92228; border-radius: 8px; font-size: 1rem; font-weight: 600; cursor: pointer; }
        .urgency-card { background: linear-gradient(135deg, #fff3e0, #ffe0b2); border: 2px solid #ff9800; border-radius: 12px; padding: 20px; }
        .urgency-card h4 { color: #e65100; margin-bottom: 10px; display: flex; align-items: center; gap: 10px; }
        .urgency-stat { display: flex; justify-content: space-between; margin-top: 12px; }
        .urgency-stat .label { color: #666; font-size: 0.9rem; }
        .urgency-stat .value { font-weight: 600; color: #e65100; }
        .market-card { background: #fff; border-radius: 12px; padding: 25px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }
        .market-card h3 { margin-bottom: 20px; }
        .market-stat { display: flex; justify-content: space-between; padding: 12px 0; border-bottom: 1px solid #eee; }
        .market-stat:last-child { border: none; }
        .market-stat .label { color: #666; }
        .market-stat .value { font-weight: 600; }
    </style>
</head>
<body>
    <header class="realtor-header">
        <div class="realtor-logo">realtor.com</div>
        <div class="search-wrapper">
            <input type="text" placeholder="Search by address, city, ZIP...">
        </div>
    </header>
    
    <div class="back-on-market-banner" data-status="Back on Market" data-back-on-market-date="2025-01-14">
        <span class="banner-icon">🎉</span>
        <div>
            <div class="banner-text">This Home is Back on the Market!</div>
            <div class="banner-subtext">Previous deal fell through on January 14, 2025</div>
        </div>
    </div>
    
    <main class="main-grid">
        <div class="property-card">
            <div class="photo-section">
                <div class="status-tag" data-status="Active">
                    <span>✓</span>
                    ACTIVE
                </div>
                🏠
            </div>
            
            <div class="info-section">
                <div class="price-row">
                    <div class="price" data-price="549000" data-currency="USD">$549,000</div>
                    <div class="price-note" data-price-change="-26000">$26K Below Previous List!</div>
                </div>
                
                <div class="address-row">
                    <div class="address" data-address="789 Cedar Lane">789 Cedar Lane</div>
                    <div class="city-state" data-city="Denver" data-state="CO" data-zip="80205">Denver, CO 80205</div>
                </div>
                
                <div class="specs-row">
                    <div class="spec" data-beds="4">
                        <div class="spec-value">4</div>
                        <div class="spec-label">Beds</div>
                    </div>
                    <div class="spec" data-baths="2">
                        <div class="spec-value">2</div>
                        <div class="spec-label">Baths</div>
                    </div>
                    <div class="spec" data-sqft="2100">
                        <div class="spec-value">2,100</div>
                        <div class="spec-label">Sq Ft</div>
                    </div>
                    <div class="spec" data-lot="0.18">
                        <div class="spec-value">0.18</div>
                        <div class="spec-label">Acres</div>
                    </div>
                </div>
                
                <div class="opportunity-section" data-opportunity="true">
                    <h4>💡 Why This Could Be Your Opportunity</h4>
                    <ul>
                        <li>Previous buyer's financing fell through - not related to home condition</li>
                        <li>Seller is <strong data-motivation="highly-motivated">highly motivated</strong> after 2nd failed sale</li>
                        <li>Price reduced <strong>$26,000</strong> below previous listing</li>
                        <li>Inspection report available from prior buyer</li>
                    </ul>
                </div>
                
                <div class="history-section" data-listing-history>
                    <h4>📋 Listing History</h4>
                    <div class="history-timeline">
                        <div class="history-event" data-event-date="2025-01-14">
                            <div class="event-date">Jan 14, 2025</div>
                            <div class="event-info">
                                <div class="event-title active">Back on Market</div>
                                <div class="event-detail">Relisted at $549,000 (was $575,000)</div>
                            </div>
                        </div>
                        <div class="history-event" data-event-date="2024-12-20">
                            <div class="event-date">Dec 20, 2024</div>
                            <div class="event-info">
                                <div class="event-title pending">Sale Pending</div>
                                <div class="event-detail">Offer accepted at $565,000</div>
                            </div>
                        </div>
                        <div class="history-event" data-event-date="2024-12-05">
                            <div class="event-date">Dec 5, 2024</div>
                            <div class="event-info">
                                <div class="event-title listed">Price Reduced</div>
                                <div class="event-detail">$575,000 → $569,000</div>
                            </div>
                        </div>
                        <div class="history-event" data-event-date="2024-11-15">
                            <div class="event-date">Nov 15, 2024</div>
                            <div class="event-info">
                                <div class="event-title listed">Original Listing</div>
                                <div class="event-detail">Listed at $575,000</div>
                            </div>
                        </div>
                    </div>
                </div>
            </div>
        </div>
        
        <aside class="sidebar">
            <div class="cta-card">
                <h3>Interested in This Home?</h3>
                <button class="tour-btn">Schedule a Tour</button>
                <button class="contact-btn">Contact Agent</button>
            </div>
            
            <div class="urgency-card" data-demand="high">
                <h4>🔥 High Demand</h4>
                <p style="font-size: 0.9rem; color: #666; margin-bottom: 15px;">Back on market listings typically sell fast!</p>
                <div class="urgency-stat" data-views-today="147">
                    <span class="label">Views Today</span>
                    <span class="value">147</span>
                </div>
                <div class="urgency-stat" data-saves="23">
                    <span class="label">Saved by</span>
                    <span class="value">23 buyers</span>
                </div>
            </div>
            
            <div class="market-card">
                <h3>Market Analysis</h3>
                <div class="market-stat" data-total-dom="60">
                    <span class="label">Total Days on Market</span>
                    <span class="value">60</span>
                </div>
                <div class="market-stat" data-original-price="575000">
                    <span class="label">Original List Price</span>
                    <span class="value">$575,000</span>
                </div>
                <div class="market-stat" data-total-reduction="26000">
                    <span class="label">Total Price Reduction</span>
                    <span class="value">-$26,000</span>
                </div>
                <div class="market-stat" data-times-pending="1">
                    <span class="label">Times Pending</span>
                    <span class="value">1</span>
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
  "address": "789 Cedar Lane",
  "city": "Denver",
  "state": "CO",
  "zip": "80205",
  "price": 549000,
  "status": "Active",
  "isBackOnMarket": true,
  "backOnMarketDate": "2025-01-14",
  "bedrooms": 4,
  "bathrooms": 2,
  "sqft": 2100,
  "lotAcres": 0.18,
  "originalPrice": 575000,
  "totalPriceReduction": 26000,
  "totalDaysOnMarket": 60,
  "timesPending": 1,
  "sellerMotivation": "highly-motivated",
  "viewsToday": 147,
  "savedByBuyers": 23,
  "listingHistory": [
    {"date": "2025-01-14", "event": "Back on Market", "price": 549000},
    {"date": "2024-12-20", "event": "Sale Pending", "price": 565000},
    {"date": "2024-12-05", "event": "Price Reduced", "price": 569000},
    {"date": "2024-11-15", "event": "Original Listing", "price": 575000}
  ]
}
```

---

## Test Implementation Notes

### Test Structure

```csharp
[Test]
[Category("LlmCached")]
public async Task ExtractProperty_ZillowListing_DetectsPriceDropAndOpenHouse()
{
    var llmProvider = await CreateRealLlmProvider();
    var service = CreatePropertyExtractionService(llmProvider);
    
    var result = await service.ExtractPropertyInfoAsync(ZillowListingHtml);
    
    result.ShouldNotBeNull();
    result.Price.ShouldBe(725000);
    result.PriceChange.ShouldBe(-75000);
    result.OpenHouse.Date.ShouldBe("2025-01-18");
    result.DaysOnMarket.ShouldBe(27);
}

[Test]
[Category("LlmCached")]
public async Task ExtractProperty_PendingSale_DetectsStatusAndExpectedClose()
{
    var llmProvider = await CreateRealLlmProvider();
    var service = CreatePropertyExtractionService(llmProvider);
    
    var result = await service.ExtractPropertyInfoAsync(RedfinPendingHtml);
    
    result.ShouldNotBeNull();
    result.Status.ShouldBe("Pending");
    result.ExpectedCloseDate.ShouldBe(new DateOnly(2025, 2, 10));
    result.BackOnMarketAlertAvailable.ShouldBeTrue();
}
```

### Extraction Fields Schema

```json
{
  "type": "realEstateListing",
  "fields": {
    "address": "string",
    "city": "string",
    "state": "string",
    "zip": "string",
    "price": "number",
    "status": "enum(Active|Pending|Sold|Back on Market)",
    "bedrooms": "number",
    "bathrooms": "number",
    "sqft": "number",
    "daysOnMarket": "number",
    "priceChange": "number?",
    "priceHistory": "array<{date: string, event: string, price: number}>",
    "openHouse": "object{date: string, time: string}?",
    "estimate": "number?",
    "alertAvailable": "boolean"
  }
}
```
