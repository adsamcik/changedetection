# Appointment Slot Availability Monitoring

## Overview

Users monitor appointment scheduling systems to catch newly released slots for:
- **Visa interviews** (US Embassy, Schengen, UK visa)
- **Medical appointments** (Specialists, DMV eye exams)
- **Restaurant reservations** (OpenTable, Resy, Tock)
- **Service appointments** (DMV, passport offices, government services)

## Key Fields to Extract

| Field | Description | Examples |
|-------|-------------|----------|
| `availableDates` | Dates with open slots | `["2025-01-15", "2025-01-16"]` |
| `availableTimes` | Time slots available | `["09:00", "10:30", "14:00"]` |
| `location` | Office/venue name | "US Embassy Madrid", "Dr. Smith's Office" |
| `slotCount` | Number of open slots | 3, "Multiple available" |
| `nextAvailable` | Earliest available date | "January 15, 2025" |
| `waitlistStatus` | Waitlist availability | "Join waitlist", "Waitlist full" |
| `appointmentType` | Type of appointment | "B1/B2 Visa Interview", "Annual Physical" |

---

## Scenario 1: US Visa Embassy Appointment (No Slots)

**Context**: User monitoring for visa interview slots at US Embassy

### HTML Fixture: `VisaNoSlotsHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Schedule Appointment - US Visa Services</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; background: #f5f5f5; }
        .header { background: #1a365d; color: white; padding: 15px 30px; }
        .header h1 { font-size: 1.2rem; }
        .nav { background: #2c5282; padding: 10px 30px; }
        .nav a { color: white; text-decoration: none; margin-right: 20px; font-size: 0.9rem; }
        .container { max-width: 900px; margin: 30px auto; background: white; border-radius: 8px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }
        .page-title { background: #e2e8f0; padding: 20px 30px; border-bottom: 1px solid #cbd5e0; }
        .page-title h2 { color: #2d3748; font-size: 1.3rem; }
        .content { padding: 30px; }
        .location-info { background: #f7fafc; border: 1px solid #e2e8f0; border-radius: 6px; padding: 20px; margin-bottom: 25px; }
        .location-info h3 { color: #2d3748; margin-bottom: 10px; }
        .location-info p { color: #718096; font-size: 0.9rem; }
        .visa-type { display: flex; align-items: center; gap: 10px; margin-bottom: 20px; }
        .visa-type label { font-weight: 600; color: #4a5568; }
        .visa-type span { background: #4299e1; color: white; padding: 5px 15px; border-radius: 4px; font-size: 0.9rem; }
        .calendar-section { margin-top: 25px; }
        .calendar-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 15px; }
        .calendar-header h4 { color: #2d3748; }
        .month-nav { display: flex; gap: 10px; }
        .month-nav button { background: #e2e8f0; border: none; padding: 8px 15px; border-radius: 4px; cursor: pointer; }
        .no-appointments { background: #fed7d7; border: 1px solid #fc8181; border-radius: 6px; padding: 25px; text-align: center; margin: 20px 0; }
        .no-appointments .icon { font-size: 2rem; margin-bottom: 10px; }
        .no-appointments h4 { color: #c53030; margin-bottom: 10px; }
        .no-appointments p { color: #742a2a; font-size: 0.9rem; }
        .waitlist-btn { background: #4299e1; color: white; border: none; padding: 12px 25px; border-radius: 6px; cursor: pointer; margin-top: 15px; font-size: 1rem; }
        .tips { background: #fefce8; border: 1px solid #fbbf24; border-radius: 6px; padding: 20px; margin-top: 25px; }
        .tips h5 { color: #92400e; margin-bottom: 10px; }
        .tips ul { color: #78350f; font-size: 0.85rem; padding-left: 20px; }
        .tips li { margin-bottom: 5px; }
        .footer { background: #1a365d; color: #a0aec0; padding: 20px 30px; text-align: center; font-size: 0.8rem; margin-top: 30px; }
    </style>
</head>
<body>
    <header class="header">
        <h1>U.S. Visa Appointment Service</h1>
    </header>
    <nav class="nav">
        <a href="#">Home</a>
        <a href="#">My Applications</a>
        <a href="#">Schedule Appointment</a>
        <a href="#">Documents</a>
        <a href="#">Contact</a>
    </nav>
    
    <main class="container">
        <div class="page-title">
            <h2>Schedule Your Visa Interview</h2>
        </div>
        
        <div class="content">
            <div class="location-info">
                <h3 data-location-id="MAD">U.S. Embassy Madrid, Spain</h3>
                <p>Calle de Serrano, 75, 28006 Madrid</p>
                <p>Consular Section Hours: Monday - Friday, 8:00 AM - 5:00 PM</p>
            </div>
            
            <div class="visa-type">
                <label>Visa Category:</label>
                <span data-visa-type="B1B2">B1/B2 Tourist/Business Visa</span>
            </div>
            
            <div class="calendar-section">
                <div class="calendar-header">
                    <h4>Available Appointments - January 2025</h4>
                    <div class="month-nav">
                        <button>&larr; December</button>
                        <button>February &rarr;</button>
                    </div>
                </div>
                
                <div class="no-appointments" data-availability="none">
                    <div class="icon">📅</div>
                    <h4>No Appointments Available</h4>
                    <p>There are currently no available appointment slots for your selected location and visa category.</p>
                    <p class="next-check">Appointments are released periodically. Please check back frequently.</p>
                    <button class="waitlist-btn" data-waitlist="available">Join Waitlist for Cancellations</button>
                </div>
                
                <div class="tips">
                    <h5>💡 Tips for Finding Appointments</h5>
                    <ul>
                        <li>Check early morning (6-8 AM local time) when new slots are often released</li>
                        <li>Appointments may become available due to cancellations</li>
                        <li>Consider alternate embassy locations if your travel plans allow</li>
                        <li>Expedited appointments may be available for urgent travel</li>
                    </ul>
                </div>
            </div>
        </div>
    </main>
    
    <footer class="footer">
        <p>&copy; 2025 U.S. Department of State. All rights reserved.</p>
    </footer>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "location": "U.S. Embassy Madrid, Spain",
  "visaType": "B1/B2 Tourist/Business Visa",
  "availability": "none",
  "availableDates": [],
  "waitlistAvailable": true,
  "nextAvailable": null
}
```

---

## Scenario 2: US Visa Embassy Appointment (Slots Available)

### HTML Fixture: `VisaSlotsAvailableHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Schedule Appointment - US Visa Services</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; background: #f5f5f5; }
        .header { background: #1a365d; color: white; padding: 15px 30px; }
        .container { max-width: 900px; margin: 30px auto; background: white; border-radius: 8px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }
        .page-title { background: #e2e8f0; padding: 20px 30px; border-bottom: 1px solid #cbd5e0; }
        .content { padding: 30px; }
        .location-info { background: #f7fafc; border: 1px solid #e2e8f0; border-radius: 6px; padding: 20px; margin-bottom: 25px; }
        .slots-available { background: #c6f6d5; border: 1px solid #68d391; border-radius: 6px; padding: 15px 20px; margin-bottom: 20px; display: flex; align-items: center; gap: 10px; }
        .slots-available .badge { background: #38a169; color: white; padding: 5px 12px; border-radius: 20px; font-weight: 600; }
        .calendar { display: grid; grid-template-columns: repeat(7, 1fr); gap: 8px; margin: 20px 0; }
        .calendar-day { padding: 12px 8px; text-align: center; border-radius: 6px; font-size: 0.9rem; }
        .calendar-day.header { background: #e2e8f0; font-weight: 600; color: #4a5568; }
        .calendar-day.unavailable { background: #f7fafc; color: #a0aec0; }
        .calendar-day.available { background: #c6f6d5; color: #22543d; cursor: pointer; font-weight: 600; border: 2px solid #68d391; }
        .calendar-day.available:hover { background: #9ae6b4; }
        .calendar-day.past { background: #f7fafc; color: #cbd5e0; text-decoration: line-through; }
        .time-slots { margin-top: 25px; }
        .time-slots h4 { margin-bottom: 15px; color: #2d3748; }
        .time-grid { display: grid; grid-template-columns: repeat(4, 1fr); gap: 10px; }
        .time-slot { padding: 12px; text-align: center; border: 2px solid #e2e8f0; border-radius: 6px; cursor: pointer; }
        .time-slot.available { border-color: #68d391; background: #f0fff4; }
        .time-slot.available:hover { background: #c6f6d5; }
        .time-slot.booked { background: #f7fafc; color: #a0aec0; cursor: not-allowed; text-decoration: line-through; }
        .continue-btn { background: #38a169; color: white; border: none; padding: 15px 40px; border-radius: 6px; font-size: 1.1rem; cursor: pointer; margin-top: 25px; }
    </style>
</head>
<body>
    <header class="header">
        <h1>U.S. Visa Appointment Service</h1>
    </header>
    
    <main class="container">
        <div class="page-title">
            <h2>Schedule Your Visa Interview</h2>
        </div>
        
        <div class="content">
            <div class="location-info">
                <h3 data-location-id="MAD">U.S. Embassy Madrid, Spain</h3>
                <p>Calle de Serrano, 75, 28006 Madrid</p>
            </div>
            
            <div class="slots-available" data-availability="available">
                <span class="badge">✓ Appointments Available</span>
                <span data-slot-count="5">5 slots available in the next 30 days</span>
            </div>
            
            <div class="calendar-section">
                <h4>January 2025</h4>
                <div class="calendar">
                    <div class="calendar-day header">Sun</div>
                    <div class="calendar-day header">Mon</div>
                    <div class="calendar-day header">Tue</div>
                    <div class="calendar-day header">Wed</div>
                    <div class="calendar-day header">Thu</div>
                    <div class="calendar-day header">Fri</div>
                    <div class="calendar-day header">Sat</div>
                    
                    <div class="calendar-day past">1</div>
                    <div class="calendar-day past">2</div>
                    <div class="calendar-day past">3</div>
                    <div class="calendar-day past">4</div>
                    <div class="calendar-day past">5</div>
                    <div class="calendar-day past">6</div>
                    <div class="calendar-day past">7</div>
                    
                    <div class="calendar-day past">8</div>
                    <div class="calendar-day past">9</div>
                    <div class="calendar-day past">10</div>
                    <div class="calendar-day past">11</div>
                    <div class="calendar-day past">12</div>
                    <div class="calendar-day past">13</div>
                    <div class="calendar-day past">14</div>
                    
                    <div class="calendar-day available" data-date="2025-01-15">15</div>
                    <div class="calendar-day unavailable">16</div>
                    <div class="calendar-day available" data-date="2025-01-17">17</div>
                    <div class="calendar-day unavailable">18</div>
                    <div class="calendar-day unavailable">19</div>
                    <div class="calendar-day available" data-date="2025-01-20">20</div>
                    <div class="calendar-day unavailable">21</div>
                    
                    <div class="calendar-day unavailable">22</div>
                    <div class="calendar-day available" data-date="2025-01-23">23</div>
                    <div class="calendar-day unavailable">24</div>
                    <div class="calendar-day unavailable">25</div>
                    <div class="calendar-day unavailable">26</div>
                    <div class="calendar-day available" data-date="2025-01-27">27</div>
                    <div class="calendar-day unavailable">28</div>
                </div>
            </div>
            
            <div class="time-slots">
                <h4>Available Times for January 15, 2025</h4>
                <div class="time-grid">
                    <div class="time-slot available" data-time="09:00">9:00 AM</div>
                    <div class="time-slot booked">9:30 AM</div>
                    <div class="time-slot available" data-time="10:00">10:00 AM</div>
                    <div class="time-slot booked">10:30 AM</div>
                    <div class="time-slot booked">11:00 AM</div>
                    <div class="time-slot available" data-time="11:30">11:30 AM</div>
                    <div class="time-slot booked">2:00 PM</div>
                    <div class="time-slot booked">2:30 PM</div>
                </div>
            </div>
            
            <button class="continue-btn">Continue to Confirmation</button>
        </div>
    </main>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "location": "U.S. Embassy Madrid, Spain",
  "availability": "available",
  "slotCount": 5,
  "availableDates": ["2025-01-15", "2025-01-17", "2025-01-20", "2025-01-23", "2025-01-27"],
  "availableTimes": ["9:00 AM", "10:00 AM", "11:30 AM"],
  "nextAvailable": "2025-01-15"
}
```

---

## Scenario 3: Medical Appointment Scheduling

### HTML Fixture: `DoctorAppointmentHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Book Appointment - CityHealth Medical Center</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #f8fafc; }
        .header { background: linear-gradient(135deg, #0ea5e9, #0284c7); color: white; padding: 20px 40px; }
        .header-content { max-width: 1200px; margin: 0 auto; display: flex; justify-content: space-between; align-items: center; }
        .logo { font-size: 1.5rem; font-weight: 700; }
        .nav a { color: white; text-decoration: none; margin-left: 30px; }
        .main { max-width: 1000px; margin: 40px auto; padding: 0 20px; }
        .doctor-card { background: white; border-radius: 12px; box-shadow: 0 4px 15px rgba(0,0,0,0.08); overflow: hidden; margin-bottom: 30px; }
        .doctor-header { display: flex; gap: 25px; padding: 25px; border-bottom: 1px solid #e2e8f0; }
        .doctor-photo { width: 120px; height: 120px; border-radius: 50%; background: linear-gradient(135deg, #e0e7ff, #c7d2fe); display: flex; align-items: center; justify-content: center; font-size: 3rem; }
        .doctor-info h2 { color: #1e293b; margin-bottom: 8px; }
        .specialty { color: #0ea5e9; font-weight: 600; margin-bottom: 5px; }
        .rating { display: flex; align-items: center; gap: 5px; color: #64748b; font-size: 0.9rem; }
        .stars { color: #fbbf24; }
        .location { color: #64748b; font-size: 0.9rem; margin-top: 8px; }
        .availability-section { padding: 25px; }
        .availability-section h3 { color: #1e293b; margin-bottom: 20px; }
        .next-available { background: #ecfdf5; border: 1px solid #6ee7b7; border-radius: 8px; padding: 15px 20px; margin-bottom: 20px; display: flex; align-items: center; gap: 15px; }
        .next-available .icon { font-size: 1.5rem; }
        .next-available-text { flex: 1; }
        .next-available-text strong { color: #059669; display: block; }
        .next-available-text span { color: #047857; font-size: 0.9rem; }
        .date-picker { display: flex; gap: 10px; flex-wrap: wrap; margin-bottom: 25px; }
        .date-option { padding: 15px 20px; border: 2px solid #e2e8f0; border-radius: 10px; text-align: center; cursor: pointer; min-width: 90px; }
        .date-option.available { border-color: #6ee7b7; background: #f0fdf4; }
        .date-option.available:hover { background: #dcfce7; }
        .date-option.selected { border-color: #0ea5e9; background: #e0f2fe; }
        .date-option.unavailable { background: #f8fafc; color: #94a3b8; cursor: not-allowed; }
        .date-option .day { font-size: 0.8rem; color: #64748b; }
        .date-option .date { font-size: 1.2rem; font-weight: 700; color: #1e293b; }
        .date-option .month { font-size: 0.8rem; color: #64748b; }
        .date-option .slots { font-size: 0.75rem; margin-top: 5px; color: #059669; font-weight: 600; }
        .time-slots-container { background: #f8fafc; border-radius: 10px; padding: 20px; }
        .time-slots-container h4 { color: #475569; margin-bottom: 15px; font-size: 0.95rem; }
        .time-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(100px, 1fr)); gap: 10px; }
        .time-btn { padding: 12px; border: 2px solid #e2e8f0; border-radius: 8px; background: white; cursor: pointer; font-size: 0.95rem; transition: all 0.2s; }
        .time-btn:hover { border-color: #0ea5e9; background: #f0f9ff; }
        .time-btn.unavailable { background: #f1f5f9; color: #94a3b8; cursor: not-allowed; border-style: dashed; }
        .book-btn { background: #0ea5e9; color: white; border: none; padding: 15px 50px; border-radius: 10px; font-size: 1.1rem; cursor: pointer; margin-top: 25px; width: 100%; font-weight: 600; }
        .insurance-note { background: #fefce8; border: 1px solid #fde047; border-radius: 8px; padding: 15px; margin-top: 20px; font-size: 0.9rem; color: #854d0e; }
    </style>
</head>
<body>
    <header class="header">
        <div class="header-content">
            <div class="logo">🏥 CityHealth Medical</div>
            <nav class="nav">
                <a href="#">Find a Doctor</a>
                <a href="#">Services</a>
                <a href="#">Locations</a>
                <a href="#">Patient Portal</a>
            </nav>
        </div>
    </header>
    
    <main class="main">
        <div class="doctor-card">
            <div class="doctor-header">
                <div class="doctor-photo">👨‍⚕️</div>
                <div class="doctor-info">
                    <h2 data-doctor-id="DR-4521">Dr. Sarah Chen, MD</h2>
                    <div class="specialty" data-specialty="dermatology">Dermatology</div>
                    <div class="rating">
                        <span class="stars">★★★★★</span>
                        <span>4.9 (127 reviews)</span>
                    </div>
                    <div class="location" data-location-id="LOC-102">
                        📍 CityHealth Downtown Clinic - 450 Market Street, Suite 200
                    </div>
                </div>
            </div>
            
            <div class="availability-section">
                <h3>Schedule an Appointment</h3>
                
                <div class="next-available" data-next-available="2025-01-16">
                    <span class="icon">⚡</span>
                    <div class="next-available-text">
                        <strong>Next Available: Thursday, January 16</strong>
                        <span>New patient appointments available</span>
                    </div>
                </div>
                
                <div class="date-picker">
                    <div class="date-option unavailable">
                        <div class="day">Mon</div>
                        <div class="date">13</div>
                        <div class="month">Jan</div>
                        <div class="slots">Full</div>
                    </div>
                    <div class="date-option unavailable">
                        <div class="day">Tue</div>
                        <div class="date">14</div>
                        <div class="month">Jan</div>
                        <div class="slots">Full</div>
                    </div>
                    <div class="date-option unavailable">
                        <div class="day">Wed</div>
                        <div class="date">15</div>
                        <div class="month">Jan</div>
                        <div class="slots">Full</div>
                    </div>
                    <div class="date-option available selected" data-date="2025-01-16">
                        <div class="day">Thu</div>
                        <div class="date">16</div>
                        <div class="month">Jan</div>
                        <div class="slots" data-slots="3">3 slots</div>
                    </div>
                    <div class="date-option available" data-date="2025-01-17">
                        <div class="day">Fri</div>
                        <div class="date">17</div>
                        <div class="month">Jan</div>
                        <div class="slots" data-slots="1">1 slot</div>
                    </div>
                    <div class="date-option available" data-date="2025-01-20">
                        <div class="day">Mon</div>
                        <div class="date">20</div>
                        <div class="month">Jan</div>
                        <div class="slots" data-slots="5">5 slots</div>
                    </div>
                    <div class="date-option available" data-date="2025-01-21">
                        <div class="day">Tue</div>
                        <div class="date">21</div>
                        <div class="month">Jan</div>
                        <div class="slots" data-slots="2">2 slots</div>
                    </div>
                </div>
                
                <div class="time-slots-container">
                    <h4>Available times for Thursday, January 16</h4>
                    <div class="time-grid">
                        <button class="time-btn unavailable" disabled>8:00 AM</button>
                        <button class="time-btn unavailable" disabled>8:30 AM</button>
                        <button class="time-btn" data-time="09:00">9:00 AM</button>
                        <button class="time-btn unavailable" disabled>9:30 AM</button>
                        <button class="time-btn" data-time="10:00">10:00 AM</button>
                        <button class="time-btn unavailable" disabled>10:30 AM</button>
                        <button class="time-btn unavailable" disabled>11:00 AM</button>
                        <button class="time-btn unavailable" disabled>11:30 AM</button>
                        <button class="time-btn unavailable" disabled>1:00 PM</button>
                        <button class="time-btn unavailable" disabled>1:30 PM</button>
                        <button class="time-btn" data-time="14:00">2:00 PM</button>
                        <button class="time-btn unavailable" disabled>2:30 PM</button>
                    </div>
                </div>
                
                <button class="book-btn">Book Appointment</button>
                
                <div class="insurance-note">
                    ℹ️ This provider accepts most major insurance plans. Please verify coverage with your insurance provider before your visit.
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
  "doctorName": "Dr. Sarah Chen, MD",
  "specialty": "Dermatology",
  "location": "CityHealth Downtown Clinic - 450 Market Street, Suite 200",
  "nextAvailable": "2025-01-16",
  "availableDates": ["2025-01-16", "2025-01-17", "2025-01-20", "2025-01-21"],
  "availableTimes": ["9:00 AM", "10:00 AM", "2:00 PM"],
  "totalSlots": 11
}
```

---

## Scenario 4: Restaurant Reservation (OpenTable Style)

### HTML Fixture: `RestaurantReservationHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>The French Laundry - Make a Reservation</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Georgia', serif; background: #1a1a1a; color: #fff; }
        .hero { height: 300px; background: linear-gradient(rgba(0,0,0,0.5), rgba(0,0,0,0.7)), url('restaurant.jpg'); background-size: cover; display: flex; align-items: flex-end; padding: 40px; }
        .hero h1 { font-size: 2.5rem; font-weight: 400; letter-spacing: 2px; }
        .hero .cuisine { color: #d4af37; font-size: 1rem; margin-top: 10px; text-transform: uppercase; letter-spacing: 3px; }
        .container { max-width: 900px; margin: -50px auto 50px; position: relative; }
        .reservation-card { background: #2a2a2a; border-radius: 12px; padding: 35px; box-shadow: 0 20px 60px rgba(0,0,0,0.5); }
        .card-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 30px; border-bottom: 1px solid #444; padding-bottom: 20px; }
        .rating { display: flex; align-items: center; gap: 10px; }
        .rating .stars { color: #d4af37; font-size: 1.2rem; }
        .rating .count { color: #888; font-size: 0.9rem; }
        .michelin { background: #c41e3a; color: white; padding: 5px 12px; border-radius: 4px; font-size: 0.85rem; font-family: sans-serif; }
        .search-bar { display: grid; grid-template-columns: 1fr 1fr 1fr auto; gap: 15px; margin-bottom: 30px; }
        .search-bar input, .search-bar select { padding: 15px; border: 1px solid #444; border-radius: 8px; background: #333; color: white; font-size: 1rem; }
        .search-bar button { background: #c41e3a; color: white; border: none; padding: 15px 30px; border-radius: 8px; cursor: pointer; font-size: 1rem; font-weight: 600; }
        .availability-status { padding: 20px; border-radius: 10px; margin-bottom: 25px; text-align: center; }
        .availability-status.limited { background: linear-gradient(135deg, #92400e, #78350f); }
        .availability-status.limited h3 { color: #fbbf24; margin-bottom: 5px; }
        .availability-status p { font-size: 0.9rem; color: #fcd34d; }
        .time-slots-section h4 { color: #d4af37; margin-bottom: 20px; font-weight: 400; font-size: 1.1rem; }
        .time-grid { display: flex; flex-wrap: wrap; gap: 12px; margin-bottom: 25px; }
        .time-slot { padding: 15px 25px; border: 1px solid #444; border-radius: 8px; cursor: pointer; transition: all 0.3s; text-align: center; min-width: 100px; }
        .time-slot.available { border-color: #d4af37; }
        .time-slot.available:hover { background: #d4af37; color: #1a1a1a; }
        .time-slot .time { font-size: 1.1rem; display: block; }
        .time-slot .type { font-size: 0.75rem; color: #888; margin-top: 3px; }
        .time-slot.booked { background: #333; color: #666; cursor: not-allowed; border-style: dashed; }
        .waitlist-section { background: #333; border-radius: 10px; padding: 25px; margin-top: 25px; }
        .waitlist-section h4 { color: #fff; margin-bottom: 15px; }
        .waitlist-form { display: flex; gap: 15px; }
        .waitlist-form input { flex: 1; padding: 15px; border: 1px solid #444; border-radius: 8px; background: #2a2a2a; color: white; }
        .waitlist-form button { background: #444; color: white; border: none; padding: 15px 30px; border-radius: 8px; cursor: pointer; }
        .next-available-info { margin-top: 20px; padding: 20px; background: #1a1a1a; border-radius: 8px; border-left: 4px solid #d4af37; }
        .next-available-info strong { color: #d4af37; }
    </style>
</head>
<body>
    <div class="hero">
        <div>
            <h1 data-restaurant-id="french-laundry">The French Laundry</h1>
            <div class="cuisine" data-cuisine="french">French Fine Dining</div>
        </div>
    </div>
    
    <div class="container">
        <div class="reservation-card">
            <div class="card-header">
                <div class="rating">
                    <span class="stars">★★★★★</span>
                    <span class="count">4.9 (2,847 reviews)</span>
                </div>
                <span class="michelin">★★★ Michelin</span>
            </div>
            
            <div class="search-bar">
                <input type="date" value="2025-02-14" data-selected-date="2025-02-14">
                <select data-party-size="2">
                    <option>2 guests</option>
                    <option>4 guests</option>
                    <option>6 guests</option>
                </select>
                <select>
                    <option>Dinner (5:00 PM - 9:00 PM)</option>
                    <option>Lunch (11:30 AM - 1:30 PM)</option>
                </select>
                <button>Search</button>
            </div>
            
            <div class="availability-status limited" data-availability="limited">
                <h3>⚠️ Limited Availability</h3>
                <p>Valentine's Day reservations are in high demand. Only 2 time slots remaining.</p>
            </div>
            
            <div class="time-slots-section">
                <h4>Available times for Friday, February 14, 2025</h4>
                <div class="time-grid">
                    <div class="time-slot booked">
                        <span class="time">5:00 PM</span>
                        <span class="type">Booked</span>
                    </div>
                    <div class="time-slot booked">
                        <span class="time">5:30 PM</span>
                        <span class="type">Booked</span>
                    </div>
                    <div class="time-slot available" data-time="18:00" data-table-type="indoor">
                        <span class="time">6:00 PM</span>
                        <span class="type">Indoor</span>
                    </div>
                    <div class="time-slot booked">
                        <span class="time">6:30 PM</span>
                        <span class="type">Booked</span>
                    </div>
                    <div class="time-slot booked">
                        <span class="time">7:00 PM</span>
                        <span class="type">Booked</span>
                    </div>
                    <div class="time-slot booked">
                        <span class="time">7:30 PM</span>
                        <span class="type">Booked</span>
                    </div>
                    <div class="time-slot available" data-time="20:00" data-table-type="bar">
                        <span class="time">8:00 PM</span>
                        <span class="type">Bar Seating</span>
                    </div>
                    <div class="time-slot booked">
                        <span class="time">8:30 PM</span>
                        <span class="type">Booked</span>
                    </div>
                </div>
            </div>
            
            <div class="next-available-info" data-next-full-availability="2025-02-16">
                <strong>Better availability on Sunday, February 16</strong> - 6 time slots open for dinner
            </div>
            
            <div class="waitlist-section" data-waitlist="available">
                <h4>Can't find a time that works?</h4>
                <p style="color: #888; margin-bottom: 15px;">Join our waitlist and we'll notify you if a table opens up.</p>
                <div class="waitlist-form">
                    <input type="email" placeholder="Enter your email">
                    <button>Join Waitlist</button>
                </div>
            </div>
        </div>
    </div>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "restaurantName": "The French Laundry",
  "cuisine": "French Fine Dining",
  "date": "2025-02-14",
  "partySize": 2,
  "availability": "limited",
  "availableTimes": ["6:00 PM", "8:00 PM"],
  "tableTypes": ["Indoor", "Bar Seating"],
  "slotsRemaining": 2,
  "nextFullAvailability": "2025-02-16",
  "waitlistAvailable": true
}
```

---

## Test Implementation Notes

### Test Structure

```csharp
[Test]
[Category("LlmCached")]
public async Task ExtractAppointment_VisaNoSlots_DetectsNoAvailability()
{
    var llmProvider = await CreateRealLlmProvider();
    var service = CreateAppointmentExtractionService(llmProvider);
    
    var result = await service.ExtractAppointmentInfoAsync(VisaNoSlotsHtml);
    
    result.ShouldNotBeNull();
    result.Availability.ShouldBe("none");
    result.AvailableDates.ShouldBeEmpty();
    result.WaitlistAvailable.ShouldBeTrue();
}
```

### Extraction Fields Schema

```json
{
  "type": "appointment",
  "fields": {
    "location": "string",
    "appointmentType": "string",
    "availability": "enum(none|limited|available)",
    "availableDates": "string[]",
    "availableTimes": "string[]",
    "nextAvailable": "date?",
    "slotCount": "number?",
    "waitlistAvailable": "boolean"
  }
}
```
