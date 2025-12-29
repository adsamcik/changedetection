# Shipping &amp; Package Tracking Monitoring

## Overview

Users monitor package tracking for delivery updates:
- **High-value shipments** (electronics, jewelry)
- **Time-sensitive deliveries** (medication, perishables)
- **International packages** (customs clearance)
- **Gift deliveries** (ensuring arrival before events)
- **Business shipments** (inventory, supplies)

## Key Fields to Extract

| Field | Description | Examples |
|-------|-------------|----------|
| `trackingNumber` | Package identifier | "1Z999AA10123456784" |
| `carrier` | Shipping company | "FedEx", "UPS", "USPS" |
| `status` | Current status | "In Transit", "Out for Delivery" |
| `estimatedDelivery` | Expected delivery | "2025-01-17" |
| `lastLocation` | Most recent scan | "Memphis, TN Hub" |
| `lastUpdate` | Last scan time | "2025-01-15T14:32:00" |
| `deliveryAttempts` | Failed attempts | 1 |
| `signature` | Signature required | true |

---

## Scenario 1: FedEx Package Tracking

**Context**: User monitoring an expensive electronics shipment

### HTML Fixture: `FedExTrackingHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Track Your Package | FedEx</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Roboto', Arial, sans-serif; background: #f5f5f5; color: #333; }
        .fedex-header { background: #4d148c; padding: 15px 40px; display: flex; justify-content: space-between; align-items: center; }
        .fedex-logo { color: #ff6200; font-size: 2rem; font-weight: 700; }
        .fedex-logo span { color: #fff; }
        .nav-links a { color: #fff; text-decoration: none; margin-left: 30px; font-size: 0.9rem; }
        .tracking-hero { background: linear-gradient(135deg, #4d148c, #6b1fad); color: #fff; padding: 40px; }
        .hero-content { max-width: 1000px; margin: 0 auto; }
        .tracking-input-row { display: flex; gap: 15px; margin-bottom: 30px; }
        .tracking-input-row input { flex: 1; padding: 15px 20px; border: none; border-radius: 4px; font-size: 1rem; }
        .tracking-input-row button { padding: 15px 30px; background: #ff6200; color: #fff; border: none; border-radius: 4px; font-weight: 700; cursor: pointer; }
        .status-summary { display: flex; gap: 40px; align-items: center; }
        .status-icon { width: 80px; height: 80px; background: rgba(255,255,255,0.2); border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 2.5rem; }
        .status-info h2 { font-size: 1.5rem; margin-bottom: 5px; }
        .status-info .tracking-num { opacity: 0.8; font-size: 0.9rem; }
        .main-content { max-width: 1000px; margin: 0 auto; padding: 30px 20px; }
        .delivery-card { background: #fff; border-radius: 8px; padding: 30px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); margin-bottom: 25px; }
        .delivery-header { display: flex; justify-content: space-between; align-items: flex-start; margin-bottom: 25px; }
        .delivery-date { }
        .delivery-date .label { color: #666; font-size: 0.85rem; margin-bottom: 5px; }
        .delivery-date .date { font-size: 1.6rem; font-weight: 700; color: #4d148c; }
        .delivery-date .time { color: #666; font-size: 0.95rem; margin-top: 3px; }
        .delivery-badge { background: #e8f5e9; color: #2e7d32; padding: 10px 20px; border-radius: 25px; font-weight: 600; display: flex; align-items: center; gap: 8px; }
        .delivery-badge.in-transit { background: #e3f2fd; color: #1565c0; }
        .delivery-badge.delayed { background: #fff3e0; color: #e65100; }
        .delivery-badge.delivered { background: #e8f5e9; color: #2e7d32; }
        .delivery-badge.exception { background: #ffebee; color: #c62828; }
        .progress-section { margin-bottom: 30px; }
        .progress-bar { display: flex; align-items: center; gap: 0; margin-bottom: 15px; }
        .progress-step { flex: 1; height: 6px; background: #e0e0e0; position: relative; }
        .progress-step.completed { background: #4d148c; }
        .progress-step.current { background: linear-gradient(90deg, #4d148c 50%, #e0e0e0 50%); }
        .progress-step::after { content: ""; position: absolute; right: -8px; top: -5px; width: 16px; height: 16px; border-radius: 50%; background: #e0e0e0; z-index: 1; }
        .progress-step.completed::after { background: #4d148c; }
        .progress-step.current::after { background: #ff6200; box-shadow: 0 0 0 4px rgba(255,98,0,0.3); }
        .progress-labels { display: flex; justify-content: space-between; font-size: 0.75rem; color: #666; }
        .shipment-details { display: grid; grid-template-columns: repeat(3, 1fr); gap: 25px; padding-top: 25px; border-top: 1px solid #e0e0e0; }
        .detail-item { }
        .detail-item .label { color: #666; font-size: 0.8rem; margin-bottom: 5px; }
        .detail-item .value { font-weight: 600; }
        .timeline-card { background: #fff; border-radius: 8px; padding: 30px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }
        .timeline-card h3 { margin-bottom: 25px; font-size: 1.2rem; display: flex; align-items: center; gap: 10px; }
        .timeline { position: relative; padding-left: 40px; }
        .timeline::before { content: ""; position: absolute; left: 12px; top: 0; bottom: 0; width: 2px; background: #e0e0e0; }
        .timeline-event { position: relative; padding-bottom: 25px; }
        .timeline-event:last-child { padding-bottom: 0; }
        .timeline-event::before { content: ""; position: absolute; left: -34px; width: 12px; height: 12px; border-radius: 50%; background: #e0e0e0; border: 2px solid #fff; box-shadow: 0 0 0 2px #e0e0e0; }
        .timeline-event.current::before { background: #ff6200; box-shadow: 0 0 0 2px #ff6200; }
        .timeline-event.completed::before { background: #4d148c; box-shadow: 0 0 0 2px #4d148c; }
        .event-time { color: #666; font-size: 0.8rem; margin-bottom: 5px; }
        .event-status { font-weight: 600; margin-bottom: 3px; }
        .event-location { color: #666; font-size: 0.9rem; }
        .alert-card { background: linear-gradient(135deg, #fff3e0, #ffe0b2); border: 2px solid #ff9800; border-radius: 8px; padding: 20px; margin-bottom: 25px; display: flex; gap: 15px; align-items: flex-start; }
        .alert-card.urgent { background: linear-gradient(135deg, #ffebee, #ffcdd2); border-color: #f44336; }
        .alert-icon { font-size: 2rem; }
        .alert-content h4 { color: #e65100; margin-bottom: 5px; }
        .alert-content p { color: #666; font-size: 0.9rem; }
        .notification-card { background: #fff; border-radius: 8px; padding: 25px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); text-align: center; }
        .notification-card h4 { margin-bottom: 15px; }
        .notification-card p { color: #666; font-size: 0.9rem; margin-bottom: 20px; }
        .notify-options { display: flex; gap: 15px; justify-content: center; }
        .notify-btn { padding: 12px 25px; border-radius: 6px; font-weight: 600; cursor: pointer; display: flex; align-items: center; gap: 8px; }
        .notify-btn.email { background: #4d148c; color: #fff; border: none; }
        .notify-btn.sms { background: #fff; color: #4d148c; border: 2px solid #4d148c; }
    </style>
</head>
<body>
    <header class="fedex-header">
        <div class="fedex-logo">Fed<span>Ex</span></div>
        <nav class="nav-links">
            <a href="#">Ship</a>
            <a href="#">Track</a>
            <a href="#">Support</a>
        </nav>
    </header>
    
    <section class="tracking-hero">
        <div class="hero-content">
            <div class="tracking-input-row">
                <input type="text" value="794644790138" placeholder="Enter tracking number">
                <button>TRACK</button>
            </div>
            <div class="status-summary">
                <div class="status-icon">📦</div>
                <div class="status-info">
                    <h2 data-status="In Transit">In Transit</h2>
                    <div class="tracking-num" data-tracking="794644790138">Tracking #: 794644790138</div>
                </div>
            </div>
        </div>
    </section>
    
    <main class="main-content">
        <div class="alert-card" data-alert="weather-delay">
            <span class="alert-icon">⚠️</span>
            <div class="alert-content">
                <h4>Weather Delay Notice</h4>
                <p data-delay-reason="Winter storm affecting Memphis hub">Winter storm conditions may cause delays in the Memphis area. Your package is safe and will continue transit when conditions improve.</p>
            </div>
        </div>
        
        <div class="delivery-card">
            <div class="delivery-header">
                <div class="delivery-date">
                    <div class="label">Estimated Delivery</div>
                    <div class="date" data-est-delivery="2025-01-17">Friday, January 17</div>
                    <div class="time" data-est-time="by-eod">By end of day</div>
                </div>
                <div class="delivery-badge in-transit" data-status-badge="in-transit">
                    <span>🚚</span>
                    In Transit
                </div>
            </div>
            
            <div class="progress-section">
                <div class="progress-bar">
                    <div class="progress-step completed"></div>
                    <div class="progress-step completed"></div>
                    <div class="progress-step current"></div>
                    <div class="progress-step"></div>
                </div>
                <div class="progress-labels">
                    <span>Picked Up</span>
                    <span>In Transit</span>
                    <span>At Local Facility</span>
                    <span>Delivered</span>
                </div>
            </div>
            
            <div class="shipment-details">
                <div class="detail-item" data-origin="Los Angeles, CA">
                    <div class="label">From</div>
                    <div class="value">Los Angeles, CA 90001</div>
                </div>
                <div class="detail-item" data-destination="Chicago, IL">
                    <div class="label">To</div>
                    <div class="value">Chicago, IL 60601</div>
                </div>
                <div class="detail-item" data-ship-date="2025-01-14">
                    <div class="label">Ship Date</div>
                    <div class="value">January 14, 2025</div>
                </div>
                <div class="detail-item" data-service="FedEx Express">
                    <div class="label">Service</div>
                    <div class="value">FedEx Express</div>
                </div>
                <div class="detail-item" data-weight="2.3">
                    <div class="label">Weight</div>
                    <div class="value">2.3 lbs</div>
                </div>
                <div class="detail-item" data-signature="required">
                    <div class="label">Signature</div>
                    <div class="value">Required - Adult</div>
                </div>
            </div>
        </div>
        
        <div class="timeline-card">
            <h3>📋 Shipment Progress</h3>
            <div class="timeline" data-tracking-events>
                <div class="timeline-event current" data-event-time="2025-01-16T08:45:00" data-event-location="Memphis, TN">
                    <div class="event-time">Jan 16, 2025 - 8:45 AM</div>
                    <div class="event-status" data-event-status="Departed FedEx location">Departed FedEx location</div>
                    <div class="event-location">MEMPHIS, TN</div>
                </div>
                <div class="timeline-event completed" data-event-time="2025-01-16T03:22:00" data-event-location="Memphis, TN">
                    <div class="event-time">Jan 16, 2025 - 3:22 AM</div>
                    <div class="event-status">Arrived at FedEx hub</div>
                    <div class="event-location">MEMPHIS, TN</div>
                </div>
                <div class="timeline-event completed" data-event-time="2025-01-15T22:15:00" data-event-location="Phoenix, AZ">
                    <div class="event-time">Jan 15, 2025 - 10:15 PM</div>
                    <div class="event-status">In transit</div>
                    <div class="event-location">PHOENIX, AZ</div>
                </div>
                <div class="timeline-event completed" data-event-time="2025-01-14T16:30:00" data-event-location="Los Angeles, CA">
                    <div class="event-time">Jan 14, 2025 - 4:30 PM</div>
                    <div class="event-status">Picked up</div>
                    <div class="event-location">LOS ANGELES, CA</div>
                </div>
                <div class="timeline-event completed" data-event-time="2025-01-14T09:00:00">
                    <div class="event-time">Jan 14, 2025 - 9:00 AM</div>
                    <div class="event-status">Shipment information sent to FedEx</div>
                    <div class="event-location">—</div>
                </div>
            </div>
        </div>
        
        <div class="notification-card" data-notifications-available="true">
            <h4>🔔 Get Delivery Notifications</h4>
            <p>Stay updated on your package status</p>
            <div class="notify-options">
                <button class="notify-btn email">📧 Email Updates</button>
                <button class="notify-btn sms">📱 Text Updates</button>
            </div>
        </div>
    </main>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "trackingNumber": "794644790138",
  "carrier": "FedEx",
  "status": "In Transit",
  "estimatedDelivery": "2025-01-17",
  "estimatedTime": "by-eod",
  "origin": "Los Angeles, CA",
  "destination": "Chicago, IL",
  "shipDate": "2025-01-14",
  "service": "FedEx Express",
  "weight": "2.3 lbs",
  "signatureRequired": true,
  "lastUpdate": "2025-01-16T08:45:00",
  "lastLocation": "Memphis, TN",
  "lastStatus": "Departed FedEx location",
  "alert": "weather-delay",
  "delayReason": "Winter storm affecting Memphis hub",
  "trackingEvents": [
    {"time": "2025-01-16T08:45:00", "status": "Departed FedEx location", "location": "Memphis, TN"},
    {"time": "2025-01-16T03:22:00", "status": "Arrived at FedEx hub", "location": "Memphis, TN"},
    {"time": "2025-01-15T22:15:00", "status": "In transit", "location": "Phoenix, AZ"},
    {"time": "2025-01-14T16:30:00", "status": "Picked up", "location": "Los Angeles, CA"}
  ]
}
```

---

## Scenario 2: UPS Delivery Exception

### HTML Fixture: `UpsExceptionHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>UPS Tracking | Delivery Exception</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: Arial, sans-serif; background: #f4f4f4; color: #333; }
        .ups-header { background: #351c15; padding: 15px 40px; display: flex; align-items: center; gap: 40px; }
        .ups-logo { background: #ffb500; color: #351c15; padding: 5px 15px; font-weight: 900; font-size: 1.5rem; }
        .header-nav a { color: #fff; text-decoration: none; margin-right: 25px; font-size: 0.9rem; }
        .exception-banner { background: #c62828; color: #fff; padding: 20px 40px; display: flex; align-items: center; gap: 20px; }
        .exception-banner .icon { font-size: 2.5rem; }
        .exception-banner h2 { font-size: 1.3rem; margin-bottom: 5px; }
        .exception-banner p { opacity: 0.9; font-size: 0.95rem; }
        .container { max-width: 900px; margin: 0 auto; padding: 30px 20px; }
        .tracking-card { background: #fff; border-radius: 8px; box-shadow: 0 2px 8px rgba(0,0,0,0.1); margin-bottom: 25px; overflow: hidden; }
        .card-header { background: #351c15; color: #fff; padding: 20px 25px; }
        .tracking-number { font-size: 0.9rem; opacity: 0.8; margin-bottom: 5px; }
        .tracking-number strong { color: #ffb500; }
        .card-body { padding: 25px; }
        .status-row { display: flex; justify-content: space-between; align-items: center; margin-bottom: 25px; padding-bottom: 25px; border-bottom: 1px solid #e0e0e0; }
        .status-left h3 { font-size: 1.4rem; color: #c62828; margin-bottom: 5px; }
        .status-left .sub { color: #666; font-size: 0.95rem; }
        .action-required { background: #ffebee; color: #c62828; padding: 12px 20px; border-radius: 25px; font-weight: 600; font-size: 0.9rem; }
        .exception-details { background: #fff3e0; border: 1px solid #ffb74d; border-radius: 8px; padding: 20px; margin-bottom: 25px; }
        .exception-details h4 { color: #e65100; margin-bottom: 15px; display: flex; align-items: center; gap: 10px; }
        .detail-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 15px; }
        .detail-item .label { color: #666; font-size: 0.8rem; margin-bottom: 3px; }
        .detail-item .value { font-weight: 600; }
        .resolution-section { background: #e8f5e9; border: 1px solid #a5d6a7; border-radius: 8px; padding: 25px; margin-bottom: 25px; }
        .resolution-section h4 { color: #2e7d32; margin-bottom: 15px; }
        .resolution-options { display: flex; flex-direction: column; gap: 12px; }
        .resolution-option { display: flex; align-items: center; gap: 15px; padding: 15px; background: #fff; border-radius: 8px; cursor: pointer; border: 2px solid transparent; transition: all 0.2s; }
        .resolution-option:hover { border-color: #4caf50; }
        .resolution-option .icon { font-size: 1.5rem; }
        .resolution-option .text h5 { margin-bottom: 3px; }
        .resolution-option .text p { color: #666; font-size: 0.85rem; }
        .delivery-attempts { margin-bottom: 25px; }
        .delivery-attempts h4 { margin-bottom: 15px; }
        .attempt-item { display: flex; gap: 15px; padding: 15px; background: #f5f5f5; border-radius: 8px; margin-bottom: 10px; }
        .attempt-item .num { width: 30px; height: 30px; background: #c62828; color: #fff; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-weight: 700; font-size: 0.85rem; }
        .attempt-item .info { flex: 1; }
        .attempt-item .date { font-weight: 600; margin-bottom: 3px; }
        .attempt-item .reason { color: #666; font-size: 0.9rem; }
        .shipment-summary { display: grid; grid-template-columns: repeat(2, 1fr); gap: 20px; padding-top: 20px; border-top: 1px solid #e0e0e0; }
        .summary-item .label { color: #666; font-size: 0.8rem; margin-bottom: 5px; }
        .summary-item .value { font-weight: 600; }
        .contact-card { background: #fff; border-radius: 8px; box-shadow: 0 2px 8px rgba(0,0,0,0.1); padding: 25px; text-align: center; }
        .contact-card h4 { margin-bottom: 15px; }
        .contact-card p { color: #666; margin-bottom: 20px; font-size: 0.95rem; }
        .contact-btn { padding: 14px 30px; background: #ffb500; color: #351c15; border: none; border-radius: 8px; font-weight: 700; cursor: pointer; font-size: 1rem; }
    </style>
</head>
<body>
    <header class="ups-header">
        <div class="ups-logo">UPS</div>
        <nav class="header-nav">
            <a href="#">Tracking</a>
            <a href="#">Shipping</a>
            <a href="#">Support</a>
        </nav>
    </header>
    
    <div class="exception-banner" data-exception-type="delivery-attempt-failed">
        <span class="icon">⚠️</span>
        <div>
            <h2>Delivery Exception - Action Required</h2>
            <p data-exception-message="Unable to deliver - Business closed">We attempted delivery but the business was closed. Please select a redelivery option.</p>
        </div>
    </div>
    
    <main class="container">
        <div class="tracking-card">
            <div class="card-header">
                <div class="tracking-number">
                    Tracking Number: <strong data-tracking="1Z999AA10123456784">1Z999AA10123456784</strong>
                </div>
            </div>
            <div class="card-body">
                <div class="status-row">
                    <div class="status-left">
                        <h3 data-status="Exception">Delivery Exception</h3>
                        <div class="sub" data-last-update="2025-01-16T17:45:00">Last update: Jan 16, 2025 at 5:45 PM</div>
                    </div>
                    <div class="action-required" data-action-required="true">Action Required</div>
                </div>
                
                <div class="exception-details" data-exception-info>
                    <h4>📋 Exception Details</h4>
                    <div class="detail-grid">
                        <div class="detail-item" data-exception-reason="Business Closed">
                            <div class="label">Reason</div>
                            <div class="value">Business Closed</div>
                        </div>
                        <div class="detail-item" data-exception-time="2025-01-16T17:30:00">
                            <div class="label">Time of Attempt</div>
                            <div class="value">5:30 PM</div>
                        </div>
                        <div class="detail-item" data-next-attempt="2025-01-17">
                            <div class="label">Next Attempt</div>
                            <div class="value">Tomorrow, Jan 17</div>
                        </div>
                        <div class="detail-item" data-hold-until="2025-01-23">
                            <div class="label">Package Held Until</div>
                            <div class="value">Jan 23, 2025</div>
                        </div>
                    </div>
                </div>
                
                <div class="resolution-section" data-resolution-options>
                    <h4>✅ Choose a Delivery Option</h4>
                    <div class="resolution-options">
                        <div class="resolution-option" data-option="reschedule">
                            <span class="icon">📅</span>
                            <div class="text">
                                <h5>Reschedule Delivery</h5>
                                <p>Choose a specific date and time window</p>
                            </div>
                        </div>
                        <div class="resolution-option" data-option="redirect">
                            <span class="icon">🏪</span>
                            <div class="text">
                                <h5>Redirect to UPS Store</h5>
                                <p>Pick up at a nearby UPS location</p>
                            </div>
                        </div>
                        <div class="resolution-option" data-option="neighbor">
                            <span class="icon">🏠</span>
                            <div class="text">
                                <h5>Deliver to Neighbor</h5>
                                <p>Leave with a neighbor at a different address</p>
                            </div>
                        </div>
                        <div class="resolution-option" data-option="authorize-release">
                            <span class="icon">📦</span>
                            <div class="text">
                                <h5>Authorize Release</h5>
                                <p>Leave package without signature (if allowed)</p>
                            </div>
                        </div>
                    </div>
                </div>
                
                <div class="delivery-attempts" data-attempts="2">
                    <h4>Previous Delivery Attempts</h4>
                    <div class="attempt-item" data-attempt="2">
                        <div class="num">2</div>
                        <div class="info">
                            <div class="date">January 16, 2025 - 5:30 PM</div>
                            <div class="reason">Business closed - No one available</div>
                        </div>
                    </div>
                    <div class="attempt-item" data-attempt="1">
                        <div class="num">1</div>
                        <div class="info">
                            <div class="date">January 15, 2025 - 4:15 PM</div>
                            <div class="reason">Recipient not available</div>
                        </div>
                    </div>
                </div>
                
                <div class="shipment-summary">
                    <div class="summary-item" data-origin="Seattle, WA">
                        <div class="label">From</div>
                        <div class="value">Seattle, WA 98101</div>
                    </div>
                    <div class="summary-item" data-destination="Portland, OR">
                        <div class="label">To</div>
                        <div class="value">Portland, OR 97201</div>
                    </div>
                    <div class="summary-item" data-service="UPS Ground">
                        <div class="label">Service</div>
                        <div class="value">UPS Ground</div>
                    </div>
                    <div class="summary-item" data-weight="5.2">
                        <div class="label">Weight</div>
                        <div class="value">5.2 lbs</div>
                    </div>
                </div>
            </div>
        </div>
        
        <div class="contact-card">
            <h4>Need Help?</h4>
            <p>Contact UPS Customer Service for assistance with your delivery</p>
            <button class="contact-btn">Contact UPS</button>
        </div>
    </main>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "trackingNumber": "1Z999AA10123456784",
  "carrier": "UPS",
  "status": "Exception",
  "exceptionType": "delivery-attempt-failed",
  "exceptionReason": "Business Closed",
  "exceptionMessage": "Unable to deliver - Business closed",
  "actionRequired": true,
  "lastUpdate": "2025-01-16T17:45:00",
  "nextAttempt": "2025-01-17",
  "holdUntil": "2025-01-23",
  "deliveryAttempts": 2,
  "attemptHistory": [
    {"attempt": 2, "date": "2025-01-16", "time": "17:30", "reason": "Business closed"},
    {"attempt": 1, "date": "2025-01-15", "time": "16:15", "reason": "Recipient not available"}
  ],
  "origin": "Seattle, WA",
  "destination": "Portland, OR",
  "service": "UPS Ground",
  "weight": "5.2 lbs",
  "resolutionOptions": ["reschedule", "redirect", "neighbor", "authorize-release"]
}
```

---

## Scenario 3: USPS Delivered Package

### HTML Fixture: `UspsDeliveredHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>USPS Tracking® - Delivered</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Helvetica Neue', Arial, sans-serif; background: #f5f5f5; color: #333; }
        .usps-header { background: #333366; padding: 15px 40px; display: flex; justify-content: space-between; align-items: center; }
        .usps-logo { display: flex; align-items: center; gap: 10px; }
        .usps-logo img { height: 40px; }
        .usps-logo span { color: #fff; font-size: 0.9rem; }
        .logo-text { color: #fff; font-weight: 700; font-size: 1.5rem; }
        .nav-links a { color: #fff; text-decoration: none; margin-left: 25px; font-size: 0.9rem; }
        .success-banner { background: linear-gradient(135deg, #4caf50, #2e7d32); color: #fff; padding: 30px 40px; text-align: center; }
        .success-icon { font-size: 4rem; margin-bottom: 15px; }
        .success-banner h1 { font-size: 1.8rem; margin-bottom: 10px; }
        .success-banner p { opacity: 0.9; font-size: 1.1rem; }
        .container { max-width: 900px; margin: 0 auto; padding: 30px 20px; }
        .delivery-proof { background: #fff; border-radius: 10px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); overflow: hidden; margin-bottom: 25px; }
        .proof-header { background: #333366; color: #fff; padding: 20px 25px; display: flex; justify-content: space-between; align-items: center; }
        .proof-header h3 { font-size: 1.1rem; }
        .tracking-badge { background: rgba(255,255,255,0.2); padding: 8px 15px; border-radius: 5px; font-size: 0.85rem; }
        .proof-body { padding: 25px; }
        .delivery-summary { display: grid; grid-template-columns: repeat(2, 1fr); gap: 25px; margin-bottom: 25px; }
        .summary-item { }
        .summary-item .label { color: #666; font-size: 0.8rem; text-transform: uppercase; letter-spacing: 1px; margin-bottom: 5px; }
        .summary-item .value { font-size: 1.2rem; font-weight: 600; }
        .summary-item .value.success { color: #4caf50; }
        .signature-section { background: #f9f9f9; border-radius: 8px; padding: 20px; margin-bottom: 25px; }
        .signature-section h4 { margin-bottom: 15px; display: flex; align-items: center; gap: 10px; }
        .signature-box { background: #fff; border: 2px solid #e0e0e0; border-radius: 8px; padding: 30px; text-align: center; }
        .signature-img { font-size: 3rem; margin-bottom: 10px; }
        .signature-name { font-style: italic; font-size: 1.3rem; color: #333366; }
        .signature-meta { color: #666; font-size: 0.85rem; margin-top: 10px; }
        .photo-proof { margin-bottom: 25px; }
        .photo-proof h4 { margin-bottom: 15px; display: flex; align-items: center; gap: 10px; }
        .photo-grid { display: grid; grid-template-columns: repeat(2, 1fr); gap: 15px; }
        .photo-item { background: linear-gradient(135deg, #e0e0e0, #f5f5f5); border-radius: 8px; aspect-ratio: 4/3; display: flex; align-items: center; justify-content: center; font-size: 3rem; }
        .tracking-timeline { margin-bottom: 25px; }
        .tracking-timeline h4 { margin-bottom: 20px; display: flex; align-items: center; gap: 10px; }
        .timeline { border-left: 3px solid #4caf50; padding-left: 25px; }
        .timeline-item { position: relative; padding-bottom: 20px; }
        .timeline-item:last-child { padding-bottom: 0; }
        .timeline-item::before { content: ""; position: absolute; left: -32px; top: 0; width: 14px; height: 14px; border-radius: 50%; background: #4caf50; border: 3px solid #fff; box-shadow: 0 0 0 2px #4caf50; }
        .timeline-item.delivered::before { background: #4caf50; box-shadow: 0 0 0 4px rgba(76,175,80,0.3); }
        .item-time { color: #666; font-size: 0.8rem; margin-bottom: 3px; }
        .item-status { font-weight: 600; color: #333; margin-bottom: 2px; }
        .item-status.delivered { color: #4caf50; }
        .item-location { color: #666; font-size: 0.9rem; }
        .info-cards { display: grid; grid-template-columns: repeat(2, 1fr); gap: 20px; }
        .info-card { background: #fff; border-radius: 10px; padding: 25px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }
        .info-card h4 { margin-bottom: 15px; font-size: 0.95rem; color: #333366; }
        .info-row { display: flex; justify-content: space-between; padding: 10px 0; border-bottom: 1px solid #f0f0f0; font-size: 0.9rem; }
        .info-row:last-child { border: none; }
        .info-row .label { color: #666; }
        .info-row .value { font-weight: 500; }
    </style>
</head>
<body>
    <header class="usps-header">
        <div class="usps-logo">
            <span class="logo-text">USPS</span>
        </div>
        <nav class="nav-links">
            <a href="#">Track</a>
            <a href="#">Ship</a>
            <a href="#">Informed Delivery</a>
        </nav>
    </header>
    
    <div class="success-banner" data-status="Delivered">
        <div class="success-icon">✅</div>
        <h1>Your Package Has Been Delivered!</h1>
        <p data-delivery-time="2025-01-16T14:23:00">Delivered on Thursday, January 16, 2025 at 2:23 PM</p>
    </div>
    
    <main class="container">
        <div class="delivery-proof">
            <div class="proof-header">
                <h3>Delivery Confirmation</h3>
                <span class="tracking-badge" data-tracking="9400111899223034567890">9400111899223034567890</span>
            </div>
            <div class="proof-body">
                <div class="delivery-summary">
                    <div class="summary-item" data-delivered-date="2025-01-16">
                        <div class="label">Delivered</div>
                        <div class="value success">January 16, 2025</div>
                    </div>
                    <div class="summary-item" data-delivered-time="14:23">
                        <div class="label">Time</div>
                        <div class="value">2:23 PM</div>
                    </div>
                    <div class="summary-item" data-delivered-to="Front Door">
                        <div class="label">Left At</div>
                        <div class="value">Front Door</div>
                    </div>
                    <div class="summary-item" data-delivery-city="Austin, TX">
                        <div class="label">Location</div>
                        <div class="value">Austin, TX 78701</div>
                    </div>
                </div>
                
                <div class="signature-section" data-signature-obtained="true">
                    <h4>✍️ Signature on File</h4>
                    <div class="signature-box">
                        <div class="signature-img">📝</div>
                        <div class="signature-name" data-signed-by="J. SMITH">J. SMITH</div>
                        <div class="signature-meta">Signed at 2:23 PM</div>
                    </div>
                </div>
                
                <div class="photo-proof" data-photo-proof="true">
                    <h4>📸 Delivery Photo</h4>
                    <div class="photo-grid">
                        <div class="photo-item">🏠📦</div>
                        <div class="photo-item">📦🚪</div>
                    </div>
                </div>
                
                <div class="tracking-timeline" data-tracking-complete>
                    <h4>📋 Complete Tracking History</h4>
                    <div class="timeline">
                        <div class="timeline-item delivered" data-event-time="2025-01-16T14:23:00">
                            <div class="item-time">Jan 16, 2025 - 2:23 PM</div>
                            <div class="item-status delivered">Delivered, Front Door</div>
                            <div class="item-location">AUSTIN, TX 78701</div>
                        </div>
                        <div class="timeline-item" data-event-time="2025-01-16T08:45:00">
                            <div class="item-time">Jan 16, 2025 - 8:45 AM</div>
                            <div class="item-status">Out for Delivery</div>
                            <div class="item-location">AUSTIN, TX 78701</div>
                        </div>
                        <div class="timeline-item" data-event-time="2025-01-16T05:30:00">
                            <div class="item-time">Jan 16, 2025 - 5:30 AM</div>
                            <div class="item-status">Arrived at Post Office</div>
                            <div class="item-location">AUSTIN, TX 78701</div>
                        </div>
                        <div class="timeline-item" data-event-time="2025-01-15T22:15:00">
                            <div class="item-time">Jan 15, 2025 - 10:15 PM</div>
                            <div class="item-status">Departed USPS Facility</div>
                            <div class="item-location">NORTH HOUSTON, TX</div>
                        </div>
                        <div class="timeline-item" data-event-time="2025-01-14T11:00:00">
                            <div class="item-time">Jan 14, 2025 - 11:00 AM</div>
                            <div class="item-status">USPS in possession of item</div>
                            <div class="item-location">HOUSTON, TX 77001</div>
                        </div>
                    </div>
                </div>
            </div>
        </div>
        
        <div class="info-cards">
            <div class="info-card">
                <h4>Shipment Details</h4>
                <div class="info-row" data-origin="Houston, TX">
                    <span class="label">From</span>
                    <span class="value">Houston, TX</span>
                </div>
                <div class="info-row" data-destination="Austin, TX">
                    <span class="label">To</span>
                    <span class="value">Austin, TX</span>
                </div>
                <div class="info-row" data-service="Priority Mail">
                    <span class="label">Service</span>
                    <span class="value">Priority Mail®</span>
                </div>
                <div class="info-row" data-transit-days="2">
                    <span class="label">Transit Time</span>
                    <span class="value">2 Days</span>
                </div>
            </div>
            <div class="info-card">
                <h4>Package Info</h4>
                <div class="info-row" data-weight="1.2">
                    <span class="label">Weight</span>
                    <span class="value">1.2 lbs</span>
                </div>
                <div class="info-row" data-dimensions="12x8x4">
                    <span class="label">Dimensions</span>
                    <span class="value">12" x 8" x 4"</span>
                </div>
                <div class="info-row" data-insurance="100">
                    <span class="label">Insurance</span>
                    <span class="value">$100.00</span>
                </div>
                <div class="info-row" data-signature-confirm="true">
                    <span class="label">Signature</span>
                    <span class="value">Confirmed</span>
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
  "trackingNumber": "9400111899223034567890",
  "carrier": "USPS",
  "status": "Delivered",
  "deliveryDate": "2025-01-16",
  "deliveryTime": "14:23",
  "deliveredTo": "Front Door",
  "deliveryCity": "Austin, TX 78701",
  "signatureObtained": true,
  "signedBy": "J. SMITH",
  "photoProof": true,
  "origin": "Houston, TX",
  "destination": "Austin, TX",
  "service": "Priority Mail",
  "transitDays": 2,
  "weight": "1.2 lbs",
  "dimensions": "12x8x4",
  "insurance": 100,
  "signatureConfirmed": true,
  "trackingEvents": [
    {"time": "2025-01-16T14:23:00", "status": "Delivered, Front Door", "location": "Austin, TX"},
    {"time": "2025-01-16T08:45:00", "status": "Out for Delivery", "location": "Austin, TX"},
    {"time": "2025-01-16T05:30:00", "status": "Arrived at Post Office", "location": "Austin, TX"}
  ]
}
```

---

## Test Implementation Notes

### Test Structure

```csharp
[Test]
[Category("LlmCached")]
public async Task ExtractTracking_FedExInTransit_DetectsWeatherDelay()
{
    var llmProvider = await CreateRealLlmProvider();
    var service = CreateTrackingExtractionService(llmProvider);
    
    var result = await service.ExtractTrackingInfoAsync(FedExTrackingHtml);
    
    result.ShouldNotBeNull();
    result.Status.ShouldBe("In Transit");
    result.Alert.ShouldBe("weather-delay");
    result.EstimatedDelivery.ShouldBe(new DateOnly(2025, 1, 17));
}

[Test]
[Category("LlmCached")]
public async Task ExtractTracking_UpsException_DetectsActionRequired()
{
    var llmProvider = await CreateRealLlmProvider();
    var service = CreateTrackingExtractionService(llmProvider);
    
    var result = await service.ExtractTrackingInfoAsync(UpsExceptionHtml);
    
    result.ShouldNotBeNull();
    result.Status.ShouldBe("Exception");
    result.ActionRequired.ShouldBeTrue();
    result.DeliveryAttempts.ShouldBe(2);
}
```

### Extraction Fields Schema

```json
{
  "type": "packageTracking",
  "fields": {
    "trackingNumber": "string",
    "carrier": "enum(FedEx|UPS|USPS|DHL)",
    "status": "enum(In Transit|Out for Delivery|Delivered|Exception)",
    "estimatedDelivery": "date?",
    "lastUpdate": "datetime",
    "lastLocation": "string",
    "lastStatus": "string",
    "origin": "string",
    "destination": "string",
    "service": "string",
    "signatureRequired": "boolean",
    "deliveryAttempts": "number?",
    "alert": "string?",
    "actionRequired": "boolean",
    "trackingEvents": "array<{time: datetime, status: string, location: string}>"
  }
}
```
