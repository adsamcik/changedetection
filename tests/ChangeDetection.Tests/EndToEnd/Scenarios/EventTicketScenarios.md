# Event Ticket Availability Monitoring

## Overview

Users monitor ticketing platforms to catch when tickets become available:
- **Concert tickets** (presales, general availability, resale)
- **Sports events** (playoff games, rivalry matches)
- **Theater shows** (Broadway, West End)
- **Festivals** (Coachella, Glastonbury)
- **Conference passes** (tech conferences, industry events)

## Key Fields to Extract

| Field | Description | Examples |
|-------|-------------|----------|
| `eventName` | Full event name | "Taylor Swift - The Eras Tour" |
| `venue` | Location/venue name | "Madison Square Garden" |
| `eventDate` | Date and time | "2025-03-15T19:30:00" |
| `availableSections` | Sections with tickets | `["Floor", "Section 101", "Section 205"]` |
| `soldOutSections` | Unavailable sections | `["VIP Package", "Front Row"]` |
| `priceRange` | Min-max price | `{"min": 150, "max": 850}` |
| `ticketsRemaining` | Quantity if shown | `{"Section 101": 4}` |
| `isWaitlist` | Waitlist available | `true` |
| `saleStatus` | Current sale phase | "General On Sale" |

---

## Scenario 1: Ticketmaster Concert Page

**Context**: User monitoring for Taylor Swift concert tickets

### HTML Fixture: `TicketmasterConcertHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Taylor Swift | The Eras Tour - Ticketmaster</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; background: #0a1929; color: #fff; }
        .tm-header { background: #026cdf; padding: 12px 30px; display: flex; justify-content: space-between; align-items: center; }
        .tm-logo { font-size: 1.4rem; font-weight: 700; }
        .header-nav a { color: #fff; text-decoration: none; margin-left: 25px; font-size: 0.9rem; }
        .event-hero { background: linear-gradient(135deg, #1a1a2e 0%, #16213e 100%); padding: 40px 50px; display: flex; gap: 40px; align-items: center; border-bottom: 4px solid #026cdf; }
        .event-image { width: 280px; height: 280px; background: linear-gradient(135deg, #667eea, #764ba2); border-radius: 12px; display: flex; align-items: center; justify-content: center; font-size: 8rem; box-shadow: 0 20px 40px rgba(0,0,0,0.3); }
        .event-details { flex: 1; }
        .event-type { color: #026cdf; font-size: 0.85rem; font-weight: 600; text-transform: uppercase; letter-spacing: 2px; margin-bottom: 10px; }
        .event-title { font-size: 2.5rem; font-weight: 700; margin-bottom: 8px; }
        .event-subtitle { color: #8899a6; font-size: 1.2rem; margin-bottom: 20px; }
        .event-meta { display: flex; gap: 30px; flex-wrap: wrap; }
        .meta-item { display: flex; align-items: center; gap: 10px; }
        .meta-icon { font-size: 1.5rem; }
        .meta-text { font-size: 0.95rem; }
        .meta-text strong { display: block; color: #fff; }
        .meta-text span { color: #8899a6; font-size: 0.85rem; }
        .main-content { max-width: 1200px; margin: 0 auto; padding: 30px; display: grid; grid-template-columns: 1fr 350px; gap: 40px; }
        .venue-map { background: #1a1a2e; border-radius: 12px; padding: 25px; }
        .map-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; }
        .map-header h3 { font-size: 1.1rem; }
        .view-toggle { display: flex; gap: 5px; }
        .view-btn { padding: 8px 15px; border: 1px solid #333; border-radius: 5px; background: transparent; color: #fff; cursor: pointer; font-size: 0.8rem; }
        .view-btn.active { background: #026cdf; border-color: #026cdf; }
        .seating-chart { background: #16213e; border-radius: 10px; height: 350px; display: flex; flex-direction: column; align-items: center; justify-content: center; position: relative; }
        .stage { background: #026cdf; padding: 15px 80px; border-radius: 5px; font-weight: 600; margin-bottom: 30px; }
        .sections-grid { display: grid; grid-template-columns: repeat(5, 1fr); gap: 8px; width: 80%; }
        .section-block { padding: 12px; border-radius: 5px; text-align: center; font-size: 0.75rem; cursor: pointer; transition: all 0.2s; }
        .section-block.available { background: #4caf50; }
        .section-block.limited { background: #ff9800; }
        .section-block.sold-out { background: #333; color: #666; cursor: not-allowed; }
        .section-block:hover:not(.sold-out) { transform: scale(1.1); }
        .ticket-sidebar { display: flex; flex-direction: column; gap: 20px; }
        .sale-status { background: linear-gradient(135deg, #4caf50, #2e7d32); border-radius: 10px; padding: 20px; text-align: center; }
        .sale-status h4 { font-size: 0.85rem; text-transform: uppercase; letter-spacing: 2px; margin-bottom: 8px; }
        .sale-status .status-text { font-size: 1.3rem; font-weight: 700; }
        .price-summary { background: #1a1a2e; border-radius: 10px; padding: 20px; }
        .price-summary h4 { margin-bottom: 15px; font-size: 0.95rem; color: #8899a6; }
        .price-range { display: flex; justify-content: space-between; margin-bottom: 15px; padding-bottom: 15px; border-bottom: 1px solid #333; }
        .price-label { color: #8899a6; font-size: 0.85rem; }
        .price-value { font-weight: 700; font-size: 1.1rem; }
        .fees-note { color: #8899a6; font-size: 0.75rem; }
        .ticket-sections { background: #1a1a2e; border-radius: 10px; padding: 20px; max-height: 400px; overflow-y: auto; }
        .ticket-sections h4 { margin-bottom: 15px; font-size: 0.95rem; }
        .section-list { display: flex; flex-direction: column; gap: 10px; }
        .section-item { display: flex; justify-content: space-between; align-items: center; padding: 15px; background: #16213e; border-radius: 8px; cursor: pointer; transition: all 0.2s; }
        .section-item:hover:not(.unavailable) { background: #1e3a5f; }
        .section-item.unavailable { opacity: 0.5; cursor: not-allowed; }
        .section-info .name { font-weight: 600; margin-bottom: 3px; }
        .section-info .detail { color: #8899a6; font-size: 0.8rem; }
        .section-right { text-align: right; }
        .section-price { font-weight: 700; color: #4caf50; }
        .section-qty { color: #ff9800; font-size: 0.8rem; }
        .section-sold { color: #888; font-size: 0.8rem; }
        .find-tickets-btn { background: #026cdf; color: #fff; border: none; padding: 18px; border-radius: 10px; font-size: 1rem; font-weight: 700; cursor: pointer; width: 100%; display: flex; align-items: center; justify-content: center; gap: 10px; }
        .notify-box { background: #1a1a2e; border-radius: 10px; padding: 20px; text-align: center; }
        .notify-box h4 { margin-bottom: 10px; }
        .notify-box p { color: #8899a6; font-size: 0.85rem; margin-bottom: 15px; }
        .notify-btn { background: transparent; border: 2px solid #026cdf; color: #026cdf; padding: 12px 25px; border-radius: 8px; cursor: pointer; font-weight: 600; }
        .alert-banner { background: linear-gradient(135deg, #ff9800, #f57c00); padding: 12px 30px; display: flex; align-items: center; justify-content: center; gap: 15px; }
        .alert-banner .icon { font-size: 1.3rem; }
        .alert-banner .text { font-weight: 600; }
        .countdown { background: rgba(0,0,0,0.2); padding: 5px 15px; border-radius: 5px; font-family: monospace; }
    </style>
</head>
<body>
    <header class="tm-header">
        <div class="tm-logo">ticketmaster</div>
        <nav class="header-nav">
            <a href="#">Concerts</a>
            <a href="#">Sports</a>
            <a href="#">Arts &amp; Theater</a>
            <a href="#">Family</a>
        </nav>
    </header>
    
    <div class="alert-banner" data-sale-phase="general">
        <span class="icon">🎫</span>
        <span class="text">General On Sale Now!</span>
        <span class="countdown" data-sale-ends="2025-01-20T23:59:00">Tickets selling fast</span>
    </div>
    
    <section class="event-hero">
        <div class="event-image">🎤</div>
        <div class="event-details">
            <div class="event-type">Concert</div>
            <h1 class="event-title" data-event-id="vvG1iZ4KGv1NJw">Taylor Swift | The Eras Tour</h1>
            <div class="event-subtitle">With Special Guest Gracie Abrams</div>
            <div class="event-meta">
                <div class="meta-item">
                    <span class="meta-icon">📅</span>
                    <div class="meta-text">
                        <strong data-event-date="2025-03-15T19:30:00">Sat, Mar 15, 2025</strong>
                        <span>7:30 PM</span>
                    </div>
                </div>
                <div class="meta-item">
                    <span class="meta-icon">📍</span>
                    <div class="meta-text">
                        <strong data-venue-id="KovZpZAEdntA">Madison Square Garden</strong>
                        <span>New York, NY</span>
                    </div>
                </div>
                <div class="meta-item">
                    <span class="meta-icon">🚪</span>
                    <div class="meta-text">
                        <strong>Doors Open</strong>
                        <span>6:00 PM</span>
                    </div>
                </div>
            </div>
        </div>
    </section>
    
    <main class="main-content">
        <div class="venue-map">
            <div class="map-header">
                <h3>Select Your Seats</h3>
                <div class="view-toggle">
                    <button class="view-btn active">Map</button>
                    <button class="view-btn">List</button>
                </div>
            </div>
            <div class="seating-chart">
                <div class="stage">STAGE</div>
                <div class="sections-grid" data-sections-availability>
                    <div class="section-block sold-out" data-section="Floor A" data-available="false">Floor A</div>
                    <div class="section-block sold-out" data-section="Floor B" data-available="false">Floor B</div>
                    <div class="section-block limited" data-section="Floor C" data-available="true" data-qty="4">Floor C</div>
                    <div class="section-block sold-out" data-section="VIP" data-available="false">VIP</div>
                    <div class="section-block sold-out" data-section="Floor D" data-available="false">Floor D</div>
                    <div class="section-block available" data-section="101" data-available="true">101</div>
                    <div class="section-block available" data-section="102" data-available="true">102</div>
                    <div class="section-block limited" data-section="103" data-available="true" data-qty="2">103</div>
                    <div class="section-block available" data-section="104" data-available="true">104</div>
                    <div class="section-block available" data-section="105" data-available="true">105</div>
                    <div class="section-block available" data-section="201" data-available="true">201</div>
                    <div class="section-block available" data-section="202" data-available="true">202</div>
                    <div class="section-block available" data-section="203" data-available="true">203</div>
                    <div class="section-block available" data-section="204" data-available="true">204</div>
                    <div class="section-block available" data-section="205" data-available="true">205</div>
                </div>
            </div>
        </div>
        
        <aside class="ticket-sidebar">
            <div class="sale-status" data-sale-status="on-sale">
                <h4>Sale Status</h4>
                <div class="status-text">On Sale Now</div>
            </div>
            
            <div class="price-summary">
                <h4>Price Range</h4>
                <div class="price-range" data-price-min="149.50" data-price-max="849.50">
                    <div>
                        <div class="price-label">Starting from</div>
                        <div class="price-value">$149.50</div>
                    </div>
                    <div>
                        <div class="price-label">Up to</div>
                        <div class="price-value">$849.50</div>
                    </div>
                </div>
                <div class="fees-note">+ Service fees and facility charges</div>
            </div>
            
            <div class="ticket-sections">
                <h4>Available Sections</h4>
                <div class="section-list">
                    <div class="section-item unavailable" data-section="VIP Package" data-available="false">
                        <div class="section-info">
                            <div class="name">VIP Package</div>
                            <div class="detail">Meet &amp; Greet included</div>
                        </div>
                        <div class="section-right">
                            <div class="section-sold">Sold Out</div>
                        </div>
                    </div>
                    <div class="section-item unavailable" data-section="Floor A-B" data-available="false">
                        <div class="section-info">
                            <div class="name">Floor Sections A-B</div>
                            <div class="detail">General Admission Standing</div>
                        </div>
                        <div class="section-right">
                            <div class="section-sold">Sold Out</div>
                        </div>
                    </div>
                    <div class="section-item" data-section="Floor C" data-available="true" data-qty="4" data-price="549.50">
                        <div class="section-info">
                            <div class="name">Floor Section C</div>
                            <div class="detail">General Admission Standing</div>
                        </div>
                        <div class="section-right">
                            <div class="section-price">$549.50</div>
                            <div class="section-qty">Only 4 left!</div>
                        </div>
                    </div>
                    <div class="section-item" data-section="100 Level" data-available="true" data-price="349.50">
                        <div class="section-info">
                            <div class="name">100 Level (101-105)</div>
                            <div class="detail">Lower Bowl Reserved</div>
                        </div>
                        <div class="section-right">
                            <div class="section-price">$349.50</div>
                        </div>
                    </div>
                    <div class="section-item" data-section="200 Level" data-available="true" data-price="149.50">
                        <div class="section-info">
                            <div class="name">200 Level (201-205)</div>
                            <div class="detail">Upper Bowl Reserved</div>
                        </div>
                        <div class="section-right">
                            <div class="section-price">$149.50</div>
                        </div>
                    </div>
                </div>
            </div>
            
            <button class="find-tickets-btn">
                <span>🎫</span>
                Find Tickets
            </button>
            
            <div class="notify-box" data-notify-sections="VIP Package,Floor A-B">
                <h4>Sold Out Sections?</h4>
                <p>Get notified if more tickets become available</p>
                <button class="notify-btn">🔔 Notify Me</button>
            </div>
        </aside>
    </main>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "eventName": "Taylor Swift | The Eras Tour",
  "venue": "Madison Square Garden",
  "location": "New York, NY",
  "eventDate": "2025-03-15T19:30:00",
  "doorsOpen": "6:00 PM",
  "saleStatus": "On Sale Now",
  "priceRange": {"min": 149.50, "max": 849.50},
  "availableSections": ["Floor C", "101", "102", "103", "104", "105", "201", "202", "203", "204", "205"],
  "soldOutSections": ["VIP Package", "Floor A", "Floor B", "Floor D", "VIP"],
  "limitedSections": [
    {"section": "Floor C", "remaining": 4},
    {"section": "103", "remaining": 2}
  ],
  "notifyAvailable": true
}
```

---

## Scenario 2: Sports Playoff Tickets

### HTML Fixture: `SportsPlayoffHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>NBA Finals Game 7 - Lakers vs Celtics | StubHub</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Inter', sans-serif; background: #f5f5f5; color: #222; }
        .stubhub-header { background: #3e1c7a; color: #fff; padding: 12px 30px; display: flex; justify-content: space-between; align-items: center; }
        .logo { font-size: 1.5rem; font-weight: 700; }
        .nav-links a { color: #fff; text-decoration: none; margin-left: 20px; font-size: 0.9rem; }
        .event-banner { background: linear-gradient(135deg, #552583, #fdb927); padding: 40px 50px; color: #fff; }
        .event-banner-content { max-width: 1200px; margin: 0 auto; display: flex; justify-content: space-between; align-items: center; }
        .matchup { display: flex; align-items: center; gap: 30px; }
        .team { display: flex; flex-direction: column; align-items: center; gap: 10px; }
        .team-logo { width: 100px; height: 100px; background: rgba(255,255,255,0.2); border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 3rem; }
        .team-name { font-weight: 700; font-size: 1.1rem; }
        .vs { font-size: 2rem; font-weight: 700; color: rgba(255,255,255,0.7); }
        .event-info { text-align: right; }
        .event-info h1 { font-size: 1.8rem; margin-bottom: 10px; }
        .event-info .venue { font-size: 1rem; opacity: 0.9; margin-bottom: 5px; }
        .event-info .date { font-size: 1.1rem; font-weight: 600; }
        .demand-indicator { display: inline-flex; align-items: center; gap: 8px; background: rgba(0,0,0,0.3); padding: 8px 15px; border-radius: 20px; margin-top: 10px; font-size: 0.85rem; }
        .demand-indicator .fire { color: #ff6b00; }
        .main-container { max-width: 1200px; margin: 0 auto; padding: 30px; display: grid; grid-template-columns: 1fr 380px; gap: 30px; }
        .listings-section { background: #fff; border-radius: 12px; padding: 25px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }
        .listings-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; padding-bottom: 15px; border-bottom: 1px solid #eee; }
        .listings-header h2 { font-size: 1.2rem; }
        .sort-select { padding: 8px 15px; border: 1px solid #ddd; border-radius: 6px; font-size: 0.9rem; }
        .ticket-listing { display: flex; justify-content: space-between; align-items: center; padding: 20px; border: 1px solid #eee; border-radius: 10px; margin-bottom: 12px; transition: all 0.2s; cursor: pointer; }
        .ticket-listing:hover { border-color: #3e1c7a; box-shadow: 0 4px 15px rgba(62,28,122,0.1); }
        .ticket-listing.featured { border-color: #4caf50; background: linear-gradient(135deg, #e8f5e9, #fff); }
        .ticket-listing.hot { border-color: #ff6b00; background: linear-gradient(135deg, #fff3e0, #fff); }
        .listing-info { flex: 1; }
        .section-name { font-weight: 700; font-size: 1.1rem; margin-bottom: 5px; display: flex; align-items: center; gap: 8px; }
        .badge { font-size: 0.7rem; padding: 3px 8px; border-radius: 4px; font-weight: 600; }
        .badge.deal { background: #4caf50; color: #fff; }
        .badge.hot { background: #ff6b00; color: #fff; }
        .listing-details { color: #666; font-size: 0.85rem; }
        .listing-details span { margin-right: 15px; }
        .listing-price { text-align: right; }
        .price-each { font-size: 1.4rem; font-weight: 700; color: #3e1c7a; }
        .price-total { color: #666; font-size: 0.8rem; }
        .view-btn { margin-top: 10px; padding: 10px 25px; background: #3e1c7a; color: #fff; border: none; border-radius: 6px; cursor: pointer; font-weight: 600; }
        .sold-listing { opacity: 0.5; background: #f5f5f5 !important; border-style: dashed !important; cursor: not-allowed; }
        .sold-listing .section-name { color: #888; }
        .sidebar { display: flex; flex-direction: column; gap: 20px; }
        .price-alert-box { background: #fff; border-radius: 12px; padding: 25px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }
        .price-alert-box h3 { font-size: 1.1rem; margin-bottom: 15px; display: flex; align-items: center; gap: 10px; }
        .price-alert-box .description { color: #666; font-size: 0.9rem; margin-bottom: 15px; }
        .threshold-input { width: 100%; padding: 12px 15px; border: 2px solid #eee; border-radius: 8px; font-size: 1rem; margin-bottom: 10px; }
        .set-alert-btn { width: 100%; padding: 14px; background: #3e1c7a; color: #fff; border: none; border-radius: 8px; font-weight: 600; cursor: pointer; }
        .price-history { background: #fff; border-radius: 12px; padding: 25px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }
        .price-history h3 { margin-bottom: 15px; }
        .history-chart { height: 120px; background: linear-gradient(to right, #e8f5e9, #fff3e0, #ffebee); border-radius: 8px; display: flex; align-items: flex-end; justify-content: space-around; padding: 15px; }
        .chart-bar { width: 30px; background: #3e1c7a; border-radius: 4px 4px 0 0; }
        .history-note { color: #666; font-size: 0.85rem; margin-top: 15px; display: flex; align-items: center; gap: 8px; }
        .history-note .trend-up { color: #e53935; }
        .quick-facts { background: #fff; border-radius: 12px; padding: 25px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }
        .quick-facts h3 { margin-bottom: 15px; }
        .fact-item { display: flex; justify-content: space-between; padding: 10px 0; border-bottom: 1px solid #eee; font-size: 0.9rem; }
        .fact-item:last-child { border: none; }
        .fact-item .label { color: #666; }
        .fact-item .value { font-weight: 600; }
        .fact-item .value.highlight { color: #4caf50; }
    </style>
</head>
<body>
    <header class="stubhub-header">
        <div class="logo">StubHub</div>
        <nav class="nav-links">
            <a href="#">Sports</a>
            <a href="#">Concerts</a>
            <a href="#">Theater</a>
            <a href="#">Sell</a>
        </nav>
    </header>
    
    <section class="event-banner">
        <div class="event-banner-content">
            <div class="matchup">
                <div class="team">
                    <div class="team-logo" data-team="LAL">🏀</div>
                    <div class="team-name">Los Angeles Lakers</div>
                </div>
                <div class="vs">VS</div>
                <div class="team">
                    <div class="team-logo" data-team="BOS">🏀</div>
                    <div class="team-name">Boston Celtics</div>
                </div>
            </div>
            <div class="event-info">
                <h1 data-event-id="NBA-Finals-G7-2025">NBA Finals - Game 7</h1>
                <div class="venue" data-venue="Crypto.com Arena">Crypto.com Arena</div>
                <div class="date" data-event-date="2025-06-22T20:00:00">Sun, Jun 22 • 8:00 PM</div>
                <div class="demand-indicator" data-demand="extreme">
                    <span class="fire">🔥🔥🔥</span>
                    <span>Extreme Demand</span>
                </div>
            </div>
        </div>
    </section>
    
    <main class="main-container">
        <section class="listings-section">
            <div class="listings-header">
                <h2 data-total-listings="247">247 Listings Available</h2>
                <select class="sort-select">
                    <option>Best Value</option>
                    <option>Lowest Price</option>
                    <option>Best Seats</option>
                </select>
            </div>
            
            <div class="ticket-listing featured" data-section="PR1" data-row="A" data-qty="2" data-price="3250" data-available="true">
                <div class="listing-info">
                    <div class="section-name">
                        Section PR1, Row A
                        <span class="badge deal">Great Deal</span>
                    </div>
                    <div class="listing-details">
                        <span>🎫 2 tickets</span>
                        <span>📍 Courtside</span>
                        <span>📱 Mobile Entry</span>
                    </div>
                </div>
                <div class="listing-price">
                    <div class="price-each">$3,250</div>
                    <div class="price-total">$6,500 total</div>
                    <button class="view-btn">View Seats</button>
                </div>
            </div>
            
            <div class="ticket-listing hot" data-section="101" data-row="5" data-qty="4" data-price="1875" data-available="true">
                <div class="listing-info">
                    <div class="section-name">
                        Section 101, Row 5
                        <span class="badge hot">🔥 Selling Fast</span>
                    </div>
                    <div class="listing-details">
                        <span>🎫 4 tickets</span>
                        <span>📍 Lower Level</span>
                        <span>📱 Mobile Entry</span>
                    </div>
                </div>
                <div class="listing-price">
                    <div class="price-each">$1,875</div>
                    <div class="price-total">$7,500 total</div>
                    <button class="view-btn">View Seats</button>
                </div>
            </div>
            
            <div class="ticket-listing sold-listing" data-section="Floor" data-row="1" data-qty="2" data-price="8500" data-available="false">
                <div class="listing-info">
                    <div class="section-name">Section Floor, Row 1</div>
                    <div class="listing-details">
                        <span>🎫 2 tickets</span>
                        <span>📍 Floor Seats</span>
                        <span style="color: #e53935; font-weight: 600;">SOLD</span>
                    </div>
                </div>
                <div class="listing-price">
                    <div class="price-each" style="color: #888; text-decoration: line-through;">$8,500</div>
                </div>
            </div>
            
            <div class="ticket-listing" data-section="115" data-row="12" data-qty="3" data-price="985" data-available="true">
                <div class="listing-info">
                    <div class="section-name">Section 115, Row 12</div>
                    <div class="listing-details">
                        <span>🎫 3 tickets</span>
                        <span>📍 Lower Level</span>
                        <span>📱 Mobile Entry</span>
                    </div>
                </div>
                <div class="listing-price">
                    <div class="price-each">$985</div>
                    <div class="price-total">$2,955 total</div>
                    <button class="view-btn">View Seats</button>
                </div>
            </div>
            
            <div class="ticket-listing" data-section="305" data-row="8" data-qty="2" data-price="425" data-available="true">
                <div class="listing-info">
                    <div class="section-name">Section 305, Row 8</div>
                    <div class="listing-details">
                        <span>🎫 2 tickets</span>
                        <span>📍 Upper Level</span>
                        <span>📱 Mobile Entry</span>
                    </div>
                </div>
                <div class="listing-price">
                    <div class="price-each">$425</div>
                    <div class="price-total">$850 total</div>
                    <button class="view-btn">View Seats</button>
                </div>
            </div>
        </section>
        
        <aside class="sidebar">
            <div class="price-alert-box">
                <h3>🔔 Set Price Alert</h3>
                <p class="description">Get notified when tickets drop below your target price</p>
                <input type="text" class="threshold-input" placeholder="Enter target price (e.g., $500)" data-alert-available="true">
                <button class="set-alert-btn">Set Alert</button>
            </div>
            
            <div class="price-history">
                <h3>Price Trend</h3>
                <div class="history-chart">
                    <div class="chart-bar" style="height: 40%;"></div>
                    <div class="chart-bar" style="height: 55%;"></div>
                    <div class="chart-bar" style="height: 65%;"></div>
                    <div class="chart-bar" style="height: 80%;"></div>
                    <div class="chart-bar" style="height: 100%;"></div>
                </div>
                <div class="history-note" data-price-trend="rising">
                    <span class="trend-up">↑</span>
                    <span>Prices up 35% in last 24 hours</span>
                </div>
            </div>
            
            <div class="quick-facts">
                <h3>Quick Facts</h3>
                <div class="fact-item" data-lowest-price="425">
                    <span class="label">Lowest Price</span>
                    <span class="value">$425</span>
                </div>
                <div class="fact-item" data-avg-price="1240">
                    <span class="label">Average Price</span>
                    <span class="value">$1,240</span>
                </div>
                <div class="fact-item" data-tickets-sold-24h="156">
                    <span class="label">Sold (24h)</span>
                    <span class="value">156 tickets</span>
                </div>
                <div class="fact-item" data-inventory-remaining="247">
                    <span class="label">Available</span>
                    <span class="value highlight">247 listings</span>
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
  "eventName": "NBA Finals - Game 7",
  "teams": ["Los Angeles Lakers", "Boston Celtics"],
  "venue": "Crypto.com Arena",
  "eventDate": "2025-06-22T20:00:00",
  "demand": "extreme",
  "totalListings": 247,
  "lowestPrice": 425,
  "averagePrice": 1240,
  "priceTrend": "rising",
  "priceChange": "+35%",
  "ticketsSoldLast24h": 156,
  "availableListings": [
    {"section": "PR1", "row": "A", "qty": 2, "priceEach": 3250},
    {"section": "101", "row": "5", "qty": 4, "priceEach": 1875},
    {"section": "115", "row": "12", "qty": 3, "priceEach": 985},
    {"section": "305", "row": "8", "qty": 2, "priceEach": 425}
  ],
  "soldListings": [
    {"section": "Floor", "row": "1", "qty": 2, "priceEach": 8500}
  ],
  "alertAvailable": true
}
```

---

## Scenario 3: Broadway Show Tickets

### HTML Fixture: `BroadwayShowHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Hamilton - Broadway | Telecharge</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Playfair Display', Georgia, serif; background: #0d0d0d; color: #fff; }
        .site-header { background: linear-gradient(180deg, #1a1a1a, transparent); padding: 20px 50px; display: flex; justify-content: space-between; align-items: center; position: absolute; width: 100%; z-index: 10; }
        .logo { font-size: 1.5rem; font-weight: 700; letter-spacing: 3px; }
        .nav-links a { color: #fff; text-decoration: none; margin-left: 30px; font-size: 0.9rem; letter-spacing: 1px; }
        .hero-section { background: linear-gradient(135deg, #1a0a00, #331a00); min-height: 500px; display: flex; align-items: center; padding: 80px 50px; position: relative; overflow: hidden; }
        .hero-bg { position: absolute; right: 0; top: 0; width: 50%; height: 100%; background: linear-gradient(135deg, transparent, rgba(212,175,55,0.1)); display: flex; align-items: center; justify-content: center; font-size: 20rem; opacity: 0.3; }
        .hero-content { max-width: 600px; position: relative; z-index: 5; }
        .show-category { color: #d4af37; font-size: 0.9rem; letter-spacing: 4px; text-transform: uppercase; margin-bottom: 15px; }
        .show-title { font-size: 4rem; font-weight: 700; margin-bottom: 10px; line-height: 1.1; }
        .show-subtitle { color: #ccc; font-size: 1.2rem; margin-bottom: 25px; }
        .show-meta { display: flex; gap: 30px; color: #999; font-size: 0.95rem; margin-bottom: 30px; }
        .show-meta span { display: flex; align-items: center; gap: 8px; }
        .rating-stars { color: #d4af37; }
        .cta-buttons { display: flex; gap: 15px; }
        .primary-btn { background: linear-gradient(135deg, #d4af37, #b8962e); color: #000; border: none; padding: 18px 40px; font-size: 1rem; font-weight: 700; cursor: pointer; letter-spacing: 2px; text-transform: uppercase; }
        .secondary-btn { background: transparent; color: #d4af37; border: 2px solid #d4af37; padding: 16px 35px; font-size: 1rem; font-weight: 600; cursor: pointer; letter-spacing: 1px; }
        .calendar-section { max-width: 1200px; margin: 0 auto; padding: 50px; }
        .calendar-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 30px; }
        .calendar-header h2 { font-size: 1.8rem; }
        .month-nav { display: flex; align-items: center; gap: 20px; }
        .month-nav button { background: transparent; border: 1px solid #333; color: #fff; padding: 10px 20px; cursor: pointer; }
        .month-nav .current-month { font-size: 1.1rem; font-weight: 600; }
        .calendar-grid { display: grid; grid-template-columns: repeat(7, 1fr); gap: 10px; }
        .day-header { text-align: center; color: #666; font-size: 0.8rem; padding: 10px; letter-spacing: 1px; }
        .calendar-day { background: #1a1a1a; border-radius: 8px; padding: 15px; min-height: 100px; position: relative; }
        .calendar-day .date { font-size: 1.2rem; font-weight: 600; margin-bottom: 10px; }
        .calendar-day.past { opacity: 0.3; }
        .calendar-day.today { border: 2px solid #d4af37; }
        .show-time { display: block; padding: 8px 10px; border-radius: 5px; margin-bottom: 5px; font-size: 0.8rem; cursor: pointer; transition: all 0.2s; }
        .show-time.available { background: #1e3a1e; color: #4caf50; }
        .show-time.limited { background: #3a2e1e; color: #ff9800; }
        .show-time.sold-out { background: #2a1a1a; color: #888; cursor: not-allowed; text-decoration: line-through; }
        .show-time.available:hover { background: #2e5a2e; }
        .ticket-count { font-size: 0.7rem; display: block; margin-top: 3px; }
        .performance-detail { max-width: 1200px; margin: 0 auto; padding: 30px 50px; display: grid; grid-template-columns: 1fr 400px; gap: 40px; }
        .seating-map { background: #1a1a1a; border-radius: 12px; padding: 30px; }
        .seating-map h3 { margin-bottom: 20px; font-size: 1.3rem; }
        .theater-layout { position: relative; background: #0d0d0d; border-radius: 10px; padding: 20px; height: 300px; }
        .stage-area { background: #d4af37; color: #000; text-align: center; padding: 15px; font-weight: 700; border-radius: 5px 5px 50% 50%; margin-bottom: 20px; }
        .seating-sections { display: grid; grid-template-columns: 1fr 1fr 1fr; gap: 10px; }
        .seat-section { padding: 20px; border-radius: 8px; text-align: center; font-size: 0.85rem; cursor: pointer; }
        .seat-section.available { background: #1e3a1e; border: 1px solid #4caf50; }
        .seat-section.limited { background: #3a2e1e; border: 1px solid #ff9800; }
        .seat-section.sold-out { background: #1a1a1a; border: 1px dashed #333; color: #666; cursor: not-allowed; }
        .seat-section .section-name { font-weight: 600; margin-bottom: 5px; }
        .seat-section .price-from { font-size: 0.75rem; color: #999; }
        .booking-summary { background: #1a1a1a; border-radius: 12px; padding: 30px; height: fit-content; }
        .booking-summary h3 { margin-bottom: 20px; }
        .selected-show { background: #0d0d0d; border-radius: 8px; padding: 20px; margin-bottom: 20px; }
        .selected-show .show-name { font-weight: 600; margin-bottom: 10px; color: #d4af37; }
        .selected-show .details { color: #999; font-size: 0.9rem; }
        .selected-show .details span { display: block; margin-bottom: 5px; }
        .price-breakdown { border-top: 1px solid #333; padding-top: 20px; margin-bottom: 20px; }
        .price-row { display: flex; justify-content: space-between; margin-bottom: 10px; font-size: 0.95rem; }
        .price-row.total { font-weight: 700; font-size: 1.1rem; border-top: 1px solid #333; padding-top: 15px; margin-top: 15px; }
        .lottery-box { background: linear-gradient(135deg, #1a0a2e, #0a1a2e); border: 1px solid #6b4ce6; border-radius: 10px; padding: 20px; margin-top: 20px; }
        .lottery-box h4 { color: #a78bfa; margin-bottom: 10px; display: flex; align-items: center; gap: 10px; }
        .lottery-box p { color: #999; font-size: 0.85rem; margin-bottom: 15px; }
        .lottery-btn { width: 100%; background: #6b4ce6; color: #fff; border: none; padding: 14px; border-radius: 8px; cursor: pointer; font-weight: 600; }
    </style>
</head>
<body>
    <header class="site-header">
        <div class="logo">TELECHARGE</div>
        <nav class="nav-links">
            <a href="#">Broadway</a>
            <a href="#">Off-Broadway</a>
            <a href="#">Concerts</a>
            <a href="#">Special Events</a>
        </nav>
    </header>
    
    <section class="hero-section">
        <div class="hero-bg">⭐</div>
        <div class="hero-content">
            <div class="show-category">Musical</div>
            <h1 class="show-title" data-show-id="hamilton-broadway">Hamilton</h1>
            <div class="show-subtitle">An American Musical</div>
            <div class="show-meta">
                <span>📍 Richard Rodgers Theatre</span>
                <span>⏱️ 2 hrs 45 min</span>
                <span class="rating-stars">★★★★★</span>
            </div>
            <div class="cta-buttons">
                <button class="primary-btn">Get Tickets</button>
                <button class="secondary-btn">Enter Lottery</button>
            </div>
        </div>
    </section>
    
    <section class="calendar-section">
        <div class="calendar-header">
            <h2>Select a Performance</h2>
            <div class="month-nav">
                <button>◀</button>
                <span class="current-month" data-month="2025-02">February 2025</span>
                <button>▶</button>
            </div>
        </div>
        
        <div class="calendar-grid" data-calendar-availability>
            <div class="day-header">SUN</div>
            <div class="day-header">MON</div>
            <div class="day-header">TUE</div>
            <div class="day-header">WED</div>
            <div class="day-header">THU</div>
            <div class="day-header">FRI</div>
            <div class="day-header">SAT</div>
            
            <div class="calendar-day past"><div class="date">26</div></div>
            <div class="calendar-day past"><div class="date">27</div></div>
            <div class="calendar-day past"><div class="date">28</div></div>
            <div class="calendar-day past"><div class="date">29</div></div>
            <div class="calendar-day past"><div class="date">30</div></div>
            <div class="calendar-day past"><div class="date">31</div></div>
            <div class="calendar-day today" data-date="2025-02-01">
                <div class="date">1</div>
                <span class="show-time available" data-time="14:00" data-available="true">2:00 PM<span class="ticket-count">12 left</span></span>
                <span class="show-time limited" data-time="20:00" data-available="true" data-remaining="3">8:00 PM<span class="ticket-count">3 left!</span></span>
            </div>
            
            <div class="calendar-day" data-date="2025-02-02">
                <div class="date">2</div>
                <span class="show-time sold-out" data-time="14:00" data-available="false">2:00 PM</span>
                <span class="show-time sold-out" data-time="19:00" data-available="false">7:00 PM</span>
            </div>
            <div class="calendar-day" data-date="2025-02-03"><div class="date">3</div></div>
            <div class="calendar-day" data-date="2025-02-04">
                <div class="date">4</div>
                <span class="show-time available" data-time="19:00" data-available="true">7:00 PM<span class="ticket-count">28 left</span></span>
            </div>
            <div class="calendar-day" data-date="2025-02-05">
                <div class="date">5</div>
                <span class="show-time available" data-time="14:00" data-available="true">2:00 PM<span class="ticket-count">15 left</span></span>
                <span class="show-time available" data-time="20:00" data-available="true">8:00 PM<span class="ticket-count">22 left</span></span>
            </div>
            <div class="calendar-day" data-date="2025-02-06">
                <div class="date">6</div>
                <span class="show-time available" data-time="19:00" data-available="true">7:00 PM<span class="ticket-count">18 left</span></span>
            </div>
            <div class="calendar-day" data-date="2025-02-07">
                <div class="date">7</div>
                <span class="show-time limited" data-time="20:00" data-available="true" data-remaining="5">8:00 PM<span class="ticket-count">5 left!</span></span>
            </div>
            <div class="calendar-day" data-date="2025-02-08">
                <div class="date">8</div>
                <span class="show-time sold-out" data-time="14:00" data-available="false">2:00 PM</span>
                <span class="show-time limited" data-time="20:00" data-available="true" data-remaining="2">8:00 PM<span class="ticket-count">2 left!</span></span>
            </div>
        </div>
    </section>
    
    <section class="performance-detail">
        <div class="seating-map">
            <h3>Theater Seating - Feb 1, 8:00 PM</h3>
            <div class="theater-layout">
                <div class="stage-area">STAGE</div>
                <div class="seating-sections" data-sections-availability>
                    <div class="seat-section sold-out" data-section="Orchestra Premium" data-available="false">
                        <div class="section-name">Orchestra Premium</div>
                        <div class="price-from">Sold Out</div>
                    </div>
                    <div class="seat-section limited" data-section="Orchestra" data-available="true" data-price="299" data-remaining="3">
                        <div class="section-name">Orchestra</div>
                        <div class="price-from">From $299</div>
                    </div>
                    <div class="seat-section sold-out" data-section="Front Mezzanine" data-available="false">
                        <div class="section-name">Front Mezzanine</div>
                        <div class="price-from">Sold Out</div>
                    </div>
                    <div class="seat-section available" data-section="Rear Mezzanine" data-available="true" data-price="179">
                        <div class="section-name">Rear Mezzanine</div>
                        <div class="price-from">From $179</div>
                    </div>
                    <div class="seat-section available" data-section="Balcony" data-available="true" data-price="99">
                        <div class="section-name">Balcony</div>
                        <div class="price-from">From $99</div>
                    </div>
                    <div class="seat-section sold-out" data-section="Box Seats" data-available="false">
                        <div class="section-name">Box Seats</div>
                        <div class="price-from">Sold Out</div>
                    </div>
                </div>
            </div>
        </div>
        
        <div class="booking-summary">
            <h3>Booking Summary</h3>
            <div class="selected-show">
                <div class="show-name">Hamilton</div>
                <div class="details">
                    <span data-venue="Richard Rodgers Theatre">📍 Richard Rodgers Theatre</span>
                    <span data-date="2025-02-01">📅 Saturday, February 1, 2025</span>
                    <span data-time="20:00">🕗 8:00 PM</span>
                </div>
            </div>
            
            <div class="price-breakdown">
                <div class="price-row" data-price-min="99">
                    <span>Tickets from</span>
                    <span>$99.00</span>
                </div>
                <div class="price-row">
                    <span>Facility Fee</span>
                    <span>$3.50</span>
                </div>
                <div class="price-row">
                    <span>Service Fee</span>
                    <span>$15.00</span>
                </div>
            </div>
            
            <div class="lottery-box" data-lottery-available="true">
                <h4>🎰 Digital Lottery</h4>
                <p>Enter for a chance to win $10 tickets! Drawing at 9 AM on performance day.</p>
                <button class="lottery-btn" data-lottery-deadline="2025-02-01T09:00:00">Enter Lottery</button>
            </div>
        </div>
    </section>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "showName": "Hamilton",
  "venue": "Richard Rodgers Theatre",
  "duration": "2 hrs 45 min",
  "currentMonth": "February 2025",
  "performances": [
    {"date": "2025-02-01", "time": "14:00", "available": true, "remaining": 12},
    {"date": "2025-02-01", "time": "20:00", "available": true, "remaining": 3},
    {"date": "2025-02-02", "time": "14:00", "available": false},
    {"date": "2025-02-02", "time": "19:00", "available": false},
    {"date": "2025-02-04", "time": "19:00", "available": true, "remaining": 28},
    {"date": "2025-02-05", "time": "14:00", "available": true, "remaining": 15},
    {"date": "2025-02-05", "time": "20:00", "available": true, "remaining": 22},
    {"date": "2025-02-06", "time": "19:00", "available": true, "remaining": 18},
    {"date": "2025-02-07", "time": "20:00", "available": true, "remaining": 5},
    {"date": "2025-02-08", "time": "14:00", "available": false},
    {"date": "2025-02-08", "time": "20:00", "available": true, "remaining": 2}
  ],
  "availableSections": ["Orchestra", "Rear Mezzanine", "Balcony"],
  "soldOutSections": ["Orchestra Premium", "Front Mezzanine", "Box Seats"],
  "priceRange": {"min": 99, "max": 299},
  "lotteryAvailable": true,
  "lotteryDeadline": "2025-02-01T09:00:00"
}
```

---

## Test Implementation Notes

### Test Structure

```csharp
[Test]
[Category("LlmCached")]
public async Task ExtractTickets_TaylorSwiftConcert_IdentifiesSoldOutSections()
{
    var llmProvider = await CreateRealLlmProvider();
    var service = CreateTicketExtractionService(llmProvider);
    
    var result = await service.ExtractTicketInfoAsync(TicketmasterConcertHtml);
    
    result.ShouldNotBeNull();
    result.EventName.ShouldBe("Taylor Swift | The Eras Tour");
    result.SaleStatus.ShouldBe("On Sale Now");
    result.SoldOutSections.ShouldContain("VIP Package");
    result.LimitedSections.ShouldContain(s => s.Section == "Floor C" && s.Remaining == 4);
}
```

### Extraction Fields Schema

```json
{
  "type": "eventTicket",
  "fields": {
    "eventName": "string",
    "venue": "string",
    "eventDate": "datetime",
    "saleStatus": "enum(presale|on-sale|sold-out|lottery)",
    "priceRange": "object{min: number, max: number}",
    "availableSections": "string[]",
    "soldOutSections": "string[]",
    "limitedSections": "array<{section: string, remaining: number}>",
    "totalListings": "number?",
    "lotteryAvailable": "boolean",
    "notifyAvailable": "boolean"
  }
}
```
