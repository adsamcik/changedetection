# Academic &amp; Education Portal Monitoring

## Overview

Users monitor educational portals and academic systems:
- **Course registration** (open seats, waitlist)
- **Admission decisions** (pending, accepted, waitlisted)
- **Grade postings** (final grades, exam results)
- **Financial aid** (award letters, disbursement)
- **Class schedules** (room changes, cancellations)
- **Scholarship deadlines** (applications, awards)

## Key Fields to Extract

| Field | Description | Examples |
|-------|-------------|----------|
| `courseName` | Course title | "CS 201 - Data Structures" |
| `seatsAvailable` | Open seats | 3 |
| `enrollmentStatus` | Registration state | "Open", "Waitlist" |
| `decisionStatus` | Admission decision | "Pending", "Accepted" |
| `grade` | Posted grade | "A-" |
| `deadline` | Important date | "2025-01-20" |
| `financialAward` | Aid amount | 25000 |

---

## Scenario 1: Course Registration Portal

**Context**: User monitoring course availability for open seats

### HTML Fixture: `CourseRegistrationHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Course Search | State University Student Portal</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Segoe UI', Tahoma, sans-serif; background: #f4f6f9; color: #333; }
        .portal-header { background: #1e3a5f; color: #fff; padding: 15px 30px; display: flex; justify-content: space-between; align-items: center; }
        .portal-logo { display: flex; align-items: center; gap: 15px; }
        .portal-logo .crest { width: 45px; height: 45px; background: #fff; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 1.5rem; }
        .portal-logo h1 { font-size: 1.2rem; font-weight: 600; }
        .user-menu { display: flex; align-items: center; gap: 15px; font-size: 0.9rem; }
        .user-menu .avatar { width: 35px; height: 35px; background: #4a90d9; border-radius: 50%; display: flex; align-items: center; justify-content: center; }
        .nav-tabs { background: #fff; border-bottom: 1px solid #ddd; padding: 0 30px; display: flex; gap: 5px; }
        .nav-tabs a { padding: 15px 20px; text-decoration: none; color: #666; font-size: 0.9rem; border-bottom: 3px solid transparent; }
        .nav-tabs a.active { color: #1e3a5f; border-color: #1e3a5f; font-weight: 600; }
        .content-area { max-width: 1200px; margin: 0 auto; padding: 30px; }
        .page-header { margin-bottom: 25px; }
        .page-header h2 { font-size: 1.6rem; color: #1e3a5f; margin-bottom: 5px; }
        .page-header .term { color: #666; font-size: 0.95rem; }
        .search-filters { background: #fff; border-radius: 8px; padding: 20px; margin-bottom: 25px; box-shadow: 0 1px 3px rgba(0,0,0,0.1); }
        .filter-row { display: flex; gap: 15px; align-items: flex-end; }
        .filter-group { flex: 1; }
        .filter-group label { display: block; font-size: 0.85rem; color: #666; margin-bottom: 5px; }
        .filter-group select, .filter-group input { width: 100%; padding: 10px 12px; border: 1px solid #ddd; border-radius: 6px; font-size: 0.95rem; }
        .search-btn { padding: 10px 25px; background: #1e3a5f; color: #fff; border: none; border-radius: 6px; font-weight: 600; cursor: pointer; }
        .results-info { margin-bottom: 20px; font-size: 0.9rem; color: #666; }
        .course-list { display: flex; flex-direction: column; gap: 15px; }
        .course-card { background: #fff; border-radius: 8px; padding: 20px; box-shadow: 0 1px 3px rgba(0,0,0,0.1); display: grid; grid-template-columns: 1fr 180px 150px; gap: 20px; align-items: center; }
        .course-card.closed { opacity: 0.7; background: #f9f9f9; }
        .course-info { }
        .course-code { font-size: 1.1rem; font-weight: 600; color: #1e3a5f; margin-bottom: 5px; display: flex; align-items: center; gap: 10px; }
        .course-code .new-badge { background: #4caf50; color: #fff; padding: 2px 8px; border-radius: 10px; font-size: 0.7rem; font-weight: 600; }
        .course-title { font-size: 0.95rem; color: #333; margin-bottom: 8px; }
        .course-meta { display: flex; gap: 20px; font-size: 0.85rem; color: #666; }
        .course-meta span { display: flex; align-items: center; gap: 5px; }
        .enrollment-info { text-align: center; }
        .seats-display { margin-bottom: 10px; }
        .seats-display .available { font-size: 2rem; font-weight: 700; }
        .seats-display .available.low { color: #f44336; }
        .seats-display .available.medium { color: #ff9800; }
        .seats-display .available.good { color: #4caf50; }
        .seats-display .total { font-size: 0.9rem; color: #666; }
        .seat-bar { height: 6px; background: #e0e0e0; border-radius: 3px; overflow: hidden; }
        .seat-bar .filled { height: 100%; background: #1e3a5f; }
        .seat-bar .filled.almost-full { background: #f44336; }
        .enrollment-status { margin-top: 10px; }
        .status-badge { display: inline-block; padding: 4px 12px; border-radius: 15px; font-size: 0.8rem; font-weight: 600; }
        .status-badge.open { background: #e8f5e9; color: #2e7d32; }
        .status-badge.waitlist { background: #fff3e0; color: #e65100; }
        .status-badge.closed { background: #ffebee; color: #c62828; }
        .action-column { }
        .action-btn { width: 100%; padding: 12px; border-radius: 6px; font-weight: 600; cursor: pointer; font-size: 0.9rem; margin-bottom: 8px; }
        .action-btn.primary { background: #1e3a5f; color: #fff; border: none; }
        .action-btn.secondary { background: #fff; color: #1e3a5f; border: 2px solid #1e3a5f; }
        .action-btn.waitlist { background: #ff9800; color: #fff; border: none; }
        .action-btn:disabled { background: #ccc; color: #666; cursor: not-allowed; border: none; }
        .prereq-warning { background: #fff3e0; border: 1px solid #ffcc80; border-radius: 6px; padding: 10px 15px; margin-top: 10px; font-size: 0.85rem; color: #e65100; display: flex; align-items: center; gap: 8px; }
        .waitlist-info { background: #e3f2fd; border-radius: 6px; padding: 10px; font-size: 0.85rem; color: #1565c0; text-align: center; }
        .legend { background: #fff; border-radius: 8px; padding: 15px 20px; margin-top: 25px; display: flex; gap: 25px; font-size: 0.85rem; }
        .legend-item { display: flex; align-items: center; gap: 8px; }
        .legend-dot { width: 12px; height: 12px; border-radius: 50%; }
        .legend-dot.open { background: #4caf50; }
        .legend-dot.waitlist { background: #ff9800; }
        .legend-dot.closed { background: #f44336; }
    </style>
</head>
<body>
    <header class="portal-header">
        <div class="portal-logo">
            <span class="crest">🎓</span>
            <h1>State University<br><small style="font-weight: 400; font-size: 0.8rem;">Student Portal</small></h1>
        </div>
        <div class="user-menu">
            <span data-student-name="John Smith">John Smith (jsmith2025)</span>
            <span class="avatar">JS</span>
        </div>
    </header>
    
    <nav class="nav-tabs">
        <a href="#">Dashboard</a>
        <a href="#" class="active">Course Search</a>
        <a href="#">My Schedule</a>
        <a href="#">Grades</a>
        <a href="#">Financial Aid</a>
    </nav>
    
    <main class="content-area">
        <header class="page-header">
            <h2>Course Search &amp; Registration</h2>
            <div class="term" data-term="Spring 2025">Spring 2025 • Registration Open</div>
        </header>
        
        <div class="search-filters">
            <div class="filter-row">
                <div class="filter-group">
                    <label>Department</label>
                    <select data-filter="department">
                        <option value="CS" selected>Computer Science</option>
                        <option value="MATH">Mathematics</option>
                    </select>
                </div>
                <div class="filter-group">
                    <label>Course Level</label>
                    <select data-filter="level">
                        <option value="200" selected>200-Level</option>
                        <option value="300">300-Level</option>
                    </select>
                </div>
                <div class="filter-group">
                    <label>Show Only</label>
                    <select data-filter="availability">
                        <option value="all">All Courses</option>
                        <option value="open" selected>Open Courses</option>
                    </select>
                </div>
                <button class="search-btn">Search</button>
            </div>
        </div>
        
        <div class="results-info" data-results-count="4">Showing 4 courses matching your criteria</div>
        
        <div class="course-list" data-course-results>
            <!-- Course with seats available -->
            <div class="course-card" data-course="CS201-001" data-status="open">
                <div class="course-info">
                    <div class="course-code">
                        <span data-course-code="CS 201">CS 201</span> - Section 001
                    </div>
                    <div class="course-title" data-course-title="Data Structures and Algorithms">Data Structures and Algorithms</div>
                    <div class="course-meta">
                        <span data-instructor="Dr. Sarah Chen">👩‍🏫 Dr. Sarah Chen</span>
                        <span data-schedule="MWF 10:00-10:50 AM">🕐 MWF 10:00-10:50 AM</span>
                        <span data-location="Science Hall 204">📍 Science Hall 204</span>
                        <span data-credits="3">📚 3 Credits</span>
                    </div>
                </div>
                <div class="enrollment-info">
                    <div class="seats-display">
                        <span class="available low" data-seats-available="2">2</span>
                        <span class="total" data-seats-total="35">/ 35 seats</span>
                    </div>
                    <div class="seat-bar">
                        <div class="filled almost-full" style="width: 94%;" data-enrollment-percent="94"></div>
                    </div>
                    <div class="enrollment-status">
                        <span class="status-badge open" data-enrollment-status="Open">Open</span>
                    </div>
                </div>
                <div class="action-column">
                    <button class="action-btn primary" data-action="enroll">Add to Schedule</button>
                    <button class="action-btn secondary">View Details</button>
                </div>
            </div>
            
            <!-- Course on waitlist -->
            <div class="course-card" data-course="CS201-002" data-status="waitlist">
                <div class="course-info">
                    <div class="course-code">
                        <span data-course-code="CS 201">CS 201</span> - Section 002
                    </div>
                    <div class="course-title" data-course-title="Data Structures and Algorithms">Data Structures and Algorithms</div>
                    <div class="course-meta">
                        <span data-instructor="Dr. Michael Park">👨‍🏫 Dr. Michael Park</span>
                        <span data-schedule="TTh 1:00-2:15 PM">🕐 TTh 1:00-2:15 PM</span>
                        <span data-location="Engineering 105">📍 Engineering 105</span>
                        <span data-credits="3">📚 3 Credits</span>
                    </div>
                </div>
                <div class="enrollment-info">
                    <div class="seats-display">
                        <span class="available" style="color: #666;" data-seats-available="0">0</span>
                        <span class="total" data-seats-total="35">/ 35 seats</span>
                    </div>
                    <div class="seat-bar">
                        <div class="filled" style="width: 100%;" data-enrollment-percent="100"></div>
                    </div>
                    <div class="enrollment-status">
                        <span class="status-badge waitlist" data-enrollment-status="Waitlist">Waitlist (8)</span>
                    </div>
                    <div class="waitlist-info" data-waitlist-position="8">
                        8 students on waitlist
                    </div>
                </div>
                <div class="action-column">
                    <button class="action-btn waitlist" data-action="waitlist">Join Waitlist</button>
                    <button class="action-btn secondary">View Details</button>
                </div>
            </div>
            
            <!-- Course with good availability -->
            <div class="course-card" data-course="CS210-001" data-status="open">
                <div class="course-info">
                    <div class="course-code">
                        <span data-course-code="CS 210">CS 210</span> - Section 001
                        <span class="new-badge">New Course</span>
                    </div>
                    <div class="course-title" data-course-title="Introduction to Machine Learning">Introduction to Machine Learning</div>
                    <div class="course-meta">
                        <span data-instructor="Dr. Emily Watson">👩‍🏫 Dr. Emily Watson</span>
                        <span data-schedule="MWF 2:00-2:50 PM">🕐 MWF 2:00-2:50 PM</span>
                        <span data-location="Tech Center 301">📍 Tech Center 301</span>
                        <span data-credits="4">📚 4 Credits</span>
                    </div>
                </div>
                <div class="enrollment-info">
                    <div class="seats-display">
                        <span class="available good" data-seats-available="18">18</span>
                        <span class="total" data-seats-total="40">/ 40 seats</span>
                    </div>
                    <div class="seat-bar">
                        <div class="filled" style="width: 55%;" data-enrollment-percent="55"></div>
                    </div>
                    <div class="enrollment-status">
                        <span class="status-badge open" data-enrollment-status="Open">Open</span>
                    </div>
                </div>
                <div class="action-column">
                    <button class="action-btn primary" data-action="enroll">Add to Schedule</button>
                    <button class="action-btn secondary">View Details</button>
                    <div class="prereq-warning" data-prereq-required="CS 101">
                        ⚠️ Requires: CS 101
                    </div>
                </div>
            </div>
            
            <!-- Closed course -->
            <div class="course-card closed" data-course="CS201-003" data-status="closed">
                <div class="course-info">
                    <div class="course-code">
                        <span data-course-code="CS 201">CS 201</span> - Section 003
                    </div>
                    <div class="course-title" data-course-title="Data Structures and Algorithms">Data Structures and Algorithms</div>
                    <div class="course-meta">
                        <span data-instructor="Dr. James Miller">👨‍🏫 Dr. James Miller</span>
                        <span data-schedule="TTh 9:00-10:15 AM">🕐 TTh 9:00-10:15 AM</span>
                        <span data-location="Science Hall 110">📍 Science Hall 110</span>
                        <span data-credits="3">📚 3 Credits</span>
                    </div>
                </div>
                <div class="enrollment-info">
                    <div class="seats-display">
                        <span class="available" style="color: #999;" data-seats-available="0">0</span>
                        <span class="total" data-seats-total="30">/ 30 seats</span>
                    </div>
                    <div class="seat-bar">
                        <div class="filled" style="width: 100%;"></div>
                    </div>
                    <div class="enrollment-status">
                        <span class="status-badge closed" data-enrollment-status="Closed">Closed</span>
                    </div>
                </div>
                <div class="action-column">
                    <button class="action-btn" disabled>Section Full</button>
                    <button class="action-btn secondary">View Details</button>
                </div>
            </div>
        </div>
        
        <div class="legend">
            <div class="legend-item">
                <span class="legend-dot open"></span>
                <span>Open - Seats Available</span>
            </div>
            <div class="legend-item">
                <span class="legend-dot waitlist"></span>
                <span>Waitlist - Join Queue</span>
            </div>
            <div class="legend-item">
                <span class="legend-dot closed"></span>
                <span>Closed - No Enrollment</span>
            </div>
        </div>
    </main>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "term": "Spring 2025",
  "resultsCount": 4,
  "courses": [
    {
      "courseCode": "CS 201",
      "section": "001",
      "courseTitle": "Data Structures and Algorithms",
      "instructor": "Dr. Sarah Chen",
      "schedule": "MWF 10:00-10:50 AM",
      "location": "Science Hall 204",
      "credits": 3,
      "seatsAvailable": 2,
      "seatsTotal": 35,
      "enrollmentPercent": 94,
      "enrollmentStatus": "Open"
    },
    {
      "courseCode": "CS 201",
      "section": "002",
      "courseTitle": "Data Structures and Algorithms",
      "instructor": "Dr. Michael Park",
      "schedule": "TTh 1:00-2:15 PM",
      "location": "Engineering 105",
      "credits": 3,
      "seatsAvailable": 0,
      "seatsTotal": 35,
      "enrollmentStatus": "Waitlist",
      "waitlistSize": 8
    },
    {
      "courseCode": "CS 210",
      "section": "001",
      "courseTitle": "Introduction to Machine Learning",
      "instructor": "Dr. Emily Watson",
      "schedule": "MWF 2:00-2:50 PM",
      "location": "Tech Center 301",
      "credits": 4,
      "seatsAvailable": 18,
      "seatsTotal": 40,
      "enrollmentPercent": 55,
      "enrollmentStatus": "Open",
      "prerequisite": "CS 101",
      "isNewCourse": true
    },
    {
      "courseCode": "CS 201",
      "section": "003",
      "courseTitle": "Data Structures and Algorithms",
      "instructor": "Dr. James Miller",
      "schedule": "TTh 9:00-10:15 AM",
      "location": "Science Hall 110",
      "credits": 3,
      "seatsAvailable": 0,
      "seatsTotal": 30,
      "enrollmentStatus": "Closed"
    }
  ]
}
```

---

## Scenario 2: Admission Decision Portal

**Context**: User checking admission status for college application

### HTML Fixture: `AdmissionPortalHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Application Status | Prestige University Admissions</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Crimson Pro', Georgia, serif; background: linear-gradient(135deg, #1a1a2e, #16213e); color: #333; min-height: 100vh; }
        .admit-header { background: #800020; color: #fff; padding: 15px 40px; display: flex; justify-content: space-between; align-items: center; }
        .university-name { font-size: 1.4rem; font-weight: 600; display: flex; align-items: center; gap: 15px; }
        .university-name .shield { font-size: 2rem; }
        .header-nav a { color: #fff; text-decoration: none; margin-left: 25px; font-size: 0.9rem; opacity: 0.9; }
        .portal-main { display: flex; justify-content: center; align-items: center; padding: 60px 20px; }
        .decision-card { background: #fff; border-radius: 16px; box-shadow: 0 20px 60px rgba(0,0,0,0.3); max-width: 600px; width: 100%; overflow: hidden; }
        .card-header { background: linear-gradient(135deg, #800020, #a52a2a); color: #fff; padding: 30px; text-align: center; }
        .card-header .university { font-size: 1.8rem; font-weight: 600; margin-bottom: 5px; }
        .card-header .office { font-size: 0.95rem; opacity: 0.9; }
        .card-body { padding: 40px; }
        .applicant-info { text-align: center; margin-bottom: 30px; padding-bottom: 30px; border-bottom: 1px solid #eee; }
        .applicant-info .name { font-size: 1.5rem; font-weight: 600; color: #333; margin-bottom: 5px; }
        .applicant-info .id { color: #666; font-size: 0.9rem; }
        .decision-section { text-align: center; margin-bottom: 30px; }
        .decision-section .label { color: #666; font-size: 0.9rem; margin-bottom: 15px; text-transform: uppercase; letter-spacing: 2px; }
        .decision-badge { display: inline-block; padding: 15px 40px; border-radius: 30px; font-size: 1.4rem; font-weight: 700; text-transform: uppercase; letter-spacing: 2px; }
        .decision-badge.admitted { background: linear-gradient(135deg, #4caf50, #2e7d32); color: #fff; }
        .decision-badge.pending { background: linear-gradient(135deg, #ff9800, #f57c00); color: #fff; }
        .decision-badge.waitlisted { background: linear-gradient(135deg, #2196f3, #1565c0); color: #fff; }
        .decision-badge.denied { background: linear-gradient(135deg, #9e9e9e, #616161); color: #fff; }
        .confetti { text-align: center; margin: 20px 0; font-size: 2.5rem; }
        .decision-message { background: #f8f9fa; padding: 25px; border-radius: 10px; margin-bottom: 30px; }
        .decision-message p { line-height: 1.8; font-size: 1.05rem; color: #444; }
        .program-info { background: #fff3e0; padding: 20px; border-radius: 10px; margin-bottom: 30px; }
        .program-info h4 { color: #e65100; margin-bottom: 15px; font-size: 1rem; }
        .program-details { display: grid; grid-template-columns: 1fr 1fr; gap: 15px; }
        .program-item { }
        .program-item .label { color: #666; font-size: 0.8rem; margin-bottom: 3px; }
        .program-item .value { font-weight: 600; color: #333; }
        .deadlines-section { margin-bottom: 30px; }
        .deadlines-section h4 { color: #800020; margin-bottom: 15px; font-size: 1rem; display: flex; align-items: center; gap: 8px; }
        .deadline-list { list-style: none; }
        .deadline-list li { display: flex; justify-content: space-between; padding: 12px 0; border-bottom: 1px solid #eee; }
        .deadline-list li:last-child { border: none; }
        .deadline-list .task { color: #333; }
        .deadline-list .date { font-weight: 600; color: #800020; }
        .deadline-list .date.urgent { color: #c62828; }
        .action-buttons { display: flex; gap: 15px; }
        .action-btn { flex: 1; padding: 15px; border-radius: 8px; font-weight: 600; cursor: pointer; font-size: 1rem; text-align: center; text-decoration: none; }
        .action-btn.primary { background: #800020; color: #fff; border: none; }
        .action-btn.secondary { background: #fff; color: #800020; border: 2px solid #800020; }
        .scholarship-alert { background: linear-gradient(135deg, #e8f5e9, #c8e6c9); border: 2px solid #4caf50; border-radius: 10px; padding: 20px; margin-top: 25px; }
        .scholarship-alert h4 { color: #2e7d32; margin-bottom: 10px; display: flex; align-items: center; gap: 10px; }
        .scholarship-alert p { color: #1b5e20; font-size: 0.95rem; }
        .scholarship-alert .amount { font-size: 1.5rem; font-weight: 700; color: #2e7d32; }
        .status-history { margin-top: 30px; padding-top: 25px; border-top: 1px solid #eee; }
        .status-history h4 { color: #666; font-size: 0.9rem; margin-bottom: 15px; text-transform: uppercase; letter-spacing: 1px; }
        .history-list { list-style: none; }
        .history-list li { display: flex; gap: 15px; padding: 10px 0; font-size: 0.9rem; }
        .history-list .date { color: #666; min-width: 100px; }
        .history-list .event { color: #333; }
    </style>
</head>
<body>
    <header class="admit-header">
        <div class="university-name">
            <span class="shield">🛡️</span>
            Prestige University
        </div>
        <nav class="header-nav">
            <a href="#">Application Status</a>
            <a href="#">Financial Aid</a>
            <a href="#">Contact Us</a>
        </nav>
    </header>
    
    <main class="portal-main">
        <div class="decision-card">
            <div class="card-header">
                <div class="university">Prestige University</div>
                <div class="office">Office of Undergraduate Admissions</div>
            </div>
            
            <div class="card-body">
                <div class="applicant-info">
                    <div class="name" data-applicant-name="Emma Rodriguez">Emma Rodriguez</div>
                    <div class="id" data-application-id="APP-2025-78432">Application ID: APP-2025-78432</div>
                </div>
                
                <div class="decision-section" data-decision>
                    <div class="label">Admission Decision</div>
                    <div class="decision-badge admitted" data-decision-status="Admitted">✨ Admitted ✨</div>
                </div>
                
                <div class="confetti">🎉🎊🎉</div>
                
                <div class="decision-message" data-decision-message>
                    <p>
                        <strong>Congratulations, Emma!</strong> On behalf of the entire Prestige University community, 
                        we are thrilled to offer you admission to the Class of 2029. Your exceptional academic 
                        achievements, leadership, and unique perspective stood out among thousands of talented applicants.
                    </p>
                </div>
                
                <div class="program-info" data-program-info>
                    <h4>📚 Admitted Program</h4>
                    <div class="program-details">
                        <div class="program-item" data-major="Computer Science">
                            <div class="label">Major</div>
                            <div class="value">Computer Science</div>
                        </div>
                        <div class="program-item" data-college="School of Engineering">
                            <div class="label">College</div>
                            <div class="value">School of Engineering</div>
                        </div>
                        <div class="program-item" data-start-term="Fall 2025">
                            <div class="label">Start Term</div>
                            <div class="value">Fall 2025</div>
                        </div>
                        <div class="program-item" data-campus="Main Campus">
                            <div class="label">Campus</div>
                            <div class="value">Main Campus</div>
                        </div>
                    </div>
                </div>
                
                <div class="scholarship-alert" data-scholarship>
                    <h4>🏆 Merit Scholarship Awarded</h4>
                    <p>You have been selected for the <strong data-scholarship-name="Presidential Scholars Program">Presidential Scholars Program</strong></p>
                    <div class="amount" data-scholarship-amount="25000" data-scholarship-period="per year">$25,000/year</div>
                </div>
                
                <div class="deadlines-section" data-deadlines>
                    <h4>📅 Important Deadlines</h4>
                    <ul class="deadline-list">
                        <li data-deadline="enrollment-deposit">
                            <span class="task">Enrollment Deposit ($500)</span>
                            <span class="date urgent" data-deadline-date="2025-05-01">May 1, 2025</span>
                        </li>
                        <li data-deadline="housing-application">
                            <span class="task">Housing Application</span>
                            <span class="date" data-deadline-date="2025-05-15">May 15, 2025</span>
                        </li>
                        <li data-deadline="orientation-registration">
                            <span class="task">Orientation Registration</span>
                            <span class="date" data-deadline-date="2025-06-01">June 1, 2025</span>
                        </li>
                        <li data-deadline="final-transcript">
                            <span class="task">Submit Final Transcript</span>
                            <span class="date" data-deadline-date="2025-07-15">July 15, 2025</span>
                        </li>
                    </ul>
                </div>
                
                <div class="action-buttons">
                    <a href="#" class="action-btn primary" data-action="accept">Accept Offer</a>
                    <a href="#" class="action-btn secondary" data-action="decline">Decline</a>
                </div>
                
                <div class="status-history" data-status-history>
                    <h4>Application Timeline</h4>
                    <ul class="history-list">
                        <li data-event="decision">
                            <span class="date">Jan 15, 2025</span>
                            <span class="event">Decision Released: Admitted</span>
                        </li>
                        <li data-event="review">
                            <span class="date">Dec 20, 2024</span>
                            <span class="event">Application Under Review</span>
                        </li>
                        <li data-event="complete">
                            <span class="date">Nov 5, 2024</span>
                            <span class="event">Application Complete</span>
                        </li>
                        <li data-event="received">
                            <span class="date">Nov 1, 2024</span>
                            <span class="event">Application Received</span>
                        </li>
                    </ul>
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
  "applicantName": "Emma Rodriguez",
  "applicationId": "APP-2025-78432",
  "decisionStatus": "Admitted",
  "major": "Computer Science",
  "college": "School of Engineering",
  "startTerm": "Fall 2025",
  "campus": "Main Campus",
  "scholarship": {
    "name": "Presidential Scholars Program",
    "amount": 25000,
    "period": "per year"
  },
  "deadlines": [
    {"task": "Enrollment Deposit", "date": "2025-05-01", "urgent": true},
    {"task": "Housing Application", "date": "2025-05-15"},
    {"task": "Orientation Registration", "date": "2025-06-01"},
    {"task": "Submit Final Transcript", "date": "2025-07-15"}
  ],
  "statusHistory": [
    {"date": "2025-01-15", "event": "Decision Released: Admitted"},
    {"date": "2024-12-20", "event": "Application Under Review"},
    {"date": "2024-11-05", "event": "Application Complete"},
    {"date": "2024-11-01", "event": "Application Received"}
  ]
}
```

---

## Scenario 3: Grade Posting Portal

**Context**: User checking for final grades after exams

### HTML Fixture: `GradePortalHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Final Grades | Student Portal</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Segoe UI', Tahoma, sans-serif; background: #f0f2f5; color: #333; }
        .portal-header { background: #2c3e50; color: #fff; padding: 12px 30px; display: flex; justify-content: space-between; align-items: center; }
        .portal-logo { font-size: 1.2rem; font-weight: 600; display: flex; align-items: center; gap: 10px; }
        .portal-logo .icon { font-size: 1.5rem; }
        .user-info { display: flex; align-items: center; gap: 15px; font-size: 0.9rem; }
        .breadcrumb { background: #fff; padding: 12px 30px; font-size: 0.85rem; color: #666; border-bottom: 1px solid #ddd; }
        .breadcrumb a { color: #3498db; text-decoration: none; }
        .main-content { max-width: 1000px; margin: 0 auto; padding: 30px 20px; }
        .page-title { font-size: 1.6rem; color: #2c3e50; margin-bottom: 5px; }
        .page-subtitle { color: #666; font-size: 0.95rem; margin-bottom: 25px; }
        .gpa-summary { display: grid; grid-template-columns: repeat(3, 1fr); gap: 20px; margin-bottom: 30px; }
        .gpa-card { background: #fff; border-radius: 10px; padding: 25px; text-align: center; box-shadow: 0 2px 5px rgba(0,0,0,0.1); }
        .gpa-card.highlight { background: linear-gradient(135deg, #3498db, #2980b9); color: #fff; }
        .gpa-card .label { font-size: 0.85rem; margin-bottom: 8px; opacity: 0.8; }
        .gpa-card .value { font-size: 2.2rem; font-weight: 700; }
        .gpa-card .sub { font-size: 0.85rem; margin-top: 5px; opacity: 0.8; }
        .grades-table-container { background: #fff; border-radius: 10px; box-shadow: 0 2px 5px rgba(0,0,0,0.1); overflow: hidden; }
        .table-header { padding: 20px 25px; border-bottom: 1px solid #eee; display: flex; justify-content: space-between; align-items: center; }
        .table-header h3 { font-size: 1.1rem; color: #2c3e50; }
        .table-header .term-select { padding: 8px 15px; border: 1px solid #ddd; border-radius: 6px; font-size: 0.9rem; }
        .grades-table { width: 100%; border-collapse: collapse; }
        .grades-table th { background: #f8f9fa; padding: 15px 20px; text-align: left; font-size: 0.85rem; color: #666; text-transform: uppercase; letter-spacing: 0.5px; border-bottom: 1px solid #eee; }
        .grades-table td { padding: 18px 20px; border-bottom: 1px solid #f5f5f5; font-size: 0.95rem; }
        .grades-table tr:last-child td { border: none; }
        .grades-table tr:hover { background: #f8f9fa; }
        .course-info { }
        .course-info .code { font-weight: 600; color: #2c3e50; margin-bottom: 3px; }
        .course-info .title { color: #666; font-size: 0.9rem; }
        .grade-cell { text-align: center; }
        .grade-badge { display: inline-block; padding: 6px 15px; border-radius: 20px; font-weight: 600; font-size: 0.95rem; }
        .grade-badge.excellent { background: #e8f5e9; color: #2e7d32; }
        .grade-badge.good { background: #e3f2fd; color: #1565c0; }
        .grade-badge.average { background: #fff3e0; color: #e65100; }
        .grade-badge.poor { background: #ffebee; color: #c62828; }
        .grade-badge.pending { background: #f5f5f5; color: #666; font-style: italic; }
        .credits-cell { text-align: center; color: #666; }
        .points-cell { text-align: center; font-weight: 600; color: #2c3e50; }
        .status-cell { }
        .status-indicator { display: flex; align-items: center; gap: 8px; font-size: 0.85rem; }
        .status-indicator .dot { width: 8px; height: 8px; border-radius: 50%; }
        .status-indicator .dot.posted { background: #4caf50; }
        .status-indicator .dot.pending { background: #ff9800; }
        .status-indicator .dot.in-progress { background: #2196f3; }
        .table-footer { padding: 20px 25px; background: #f8f9fa; border-top: 1px solid #eee; }
        .totals-row { display: flex; justify-content: flex-end; gap: 40px; font-size: 0.95rem; }
        .totals-row .item { display: flex; gap: 10px; }
        .totals-row .label { color: #666; }
        .totals-row .value { font-weight: 600; color: #2c3e50; }
        .grade-legend { background: #fff; border-radius: 10px; padding: 20px 25px; margin-top: 25px; box-shadow: 0 2px 5px rgba(0,0,0,0.1); }
        .grade-legend h4 { font-size: 0.95rem; color: #2c3e50; margin-bottom: 15px; }
        .legend-grid { display: grid; grid-template-columns: repeat(5, 1fr); gap: 10px; font-size: 0.85rem; }
        .legend-item { display: flex; align-items: center; gap: 8px; }
        .legend-item .grade { font-weight: 600; min-width: 25px; }
        .legend-item .points { color: #666; }
        .pending-notice { background: #fff3e0; border: 1px solid #ffcc80; border-radius: 8px; padding: 15px 20px; margin-bottom: 25px; display: flex; align-items: center; gap: 12px; }
        .pending-notice .icon { font-size: 1.5rem; }
        .pending-notice .text { font-size: 0.9rem; color: #e65100; }
    </style>
</head>
<body>
    <header class="portal-header">
        <div class="portal-logo">
            <span class="icon">🎓</span>
            State University Student Portal
        </div>
        <div class="user-info">
            <span data-student-id="12345678">ID: 12345678</span>
            <span data-student-name="Alex Johnson">Alex Johnson</span>
        </div>
    </header>
    
    <nav class="breadcrumb">
        <a href="#">Home</a> › <a href="#">Academics</a> › Final Grades
    </nav>
    
    <main class="main-content">
        <h1 class="page-title">Final Grades</h1>
        <p class="page-subtitle" data-term="Fall 2024">Fall 2024 Semester</p>
        
        <div class="pending-notice" data-grades-pending="2">
            <span class="icon">⏳</span>
            <span class="text"><strong>2 grades pending.</strong> Your instructor has until January 20, 2025 to submit final grades.</span>
        </div>
        
        <div class="gpa-summary" data-gpa-summary>
            <div class="gpa-card highlight" data-term-gpa="3.78">
                <div class="label">Term GPA</div>
                <div class="value">3.78</div>
                <div class="sub">Fall 2024</div>
            </div>
            <div class="gpa-card" data-cumulative-gpa="3.65">
                <div class="label">Cumulative GPA</div>
                <div class="value">3.65</div>
                <div class="sub">All Terms</div>
            </div>
            <div class="gpa-card" data-credits-earned="78">
                <div class="label">Credits Earned</div>
                <div class="value">78</div>
                <div class="sub">of 120 required</div>
            </div>
        </div>
        
        <div class="grades-table-container" data-grades-table>
            <div class="table-header">
                <h3>Course Grades</h3>
                <select class="term-select" data-term-selector>
                    <option value="fall2024" selected>Fall 2024</option>
                    <option value="spring2024">Spring 2024</option>
                </select>
            </div>
            
            <table class="grades-table">
                <thead>
                    <tr>
                        <th>Course</th>
                        <th style="text-align: center;">Credits</th>
                        <th style="text-align: center;">Grade</th>
                        <th style="text-align: center;">Points</th>
                        <th>Status</th>
                    </tr>
                </thead>
                <tbody>
                    <tr data-course="CS301">
                        <td class="course-info">
                            <div class="code" data-course-code="CS 301">CS 301</div>
                            <div class="title" data-course-title="Algorithms and Data Structures">Algorithms and Data Structures</div>
                        </td>
                        <td class="credits-cell" data-credits="4">4</td>
                        <td class="grade-cell">
                            <span class="grade-badge excellent" data-grade="A">A</span>
                        </td>
                        <td class="points-cell" data-grade-points="16.0">16.0</td>
                        <td class="status-cell">
                            <div class="status-indicator">
                                <span class="dot posted"></span>
                                <span data-grade-status="Posted">Posted</span>
                            </div>
                        </td>
                    </tr>
                    <tr data-course="CS315">
                        <td class="course-info">
                            <div class="code" data-course-code="CS 315">CS 315</div>
                            <div class="title" data-course-title="Database Systems">Database Systems</div>
                        </td>
                        <td class="credits-cell" data-credits="3">3</td>
                        <td class="grade-cell">
                            <span class="grade-badge good" data-grade="A-">A-</span>
                        </td>
                        <td class="points-cell" data-grade-points="11.1">11.1</td>
                        <td class="status-cell">
                            <div class="status-indicator">
                                <span class="dot posted"></span>
                                <span data-grade-status="Posted">Posted</span>
                            </div>
                        </td>
                    </tr>
                    <tr data-course="MATH240">
                        <td class="course-info">
                            <div class="code" data-course-code="MATH 240">MATH 240</div>
                            <div class="title" data-course-title="Linear Algebra">Linear Algebra</div>
                        </td>
                        <td class="credits-cell" data-credits="4">4</td>
                        <td class="grade-cell">
                            <span class="grade-badge good" data-grade="B+">B+</span>
                        </td>
                        <td class="points-cell" data-grade-points="13.2">13.2</td>
                        <td class="status-cell">
                            <div class="status-indicator">
                                <span class="dot posted"></span>
                                <span data-grade-status="Posted">Posted</span>
                            </div>
                        </td>
                    </tr>
                    <tr data-course="PHYS201">
                        <td class="course-info">
                            <div class="code" data-course-code="PHYS 201">PHYS 201</div>
                            <div class="title" data-course-title="Physics for Engineers I">Physics for Engineers I</div>
                        </td>
                        <td class="credits-cell" data-credits="4">4</td>
                        <td class="grade-cell">
                            <span class="grade-badge pending" data-grade="Pending">Pending</span>
                        </td>
                        <td class="points-cell">—</td>
                        <td class="status-cell">
                            <div class="status-indicator">
                                <span class="dot pending"></span>
                                <span data-grade-status="Pending" data-expected-date="2025-01-18">Expected: Jan 18</span>
                            </div>
                        </td>
                    </tr>
                    <tr data-course="ENGL102">
                        <td class="course-info">
                            <div class="code" data-course-code="ENGL 102">ENGL 102</div>
                            <div class="title" data-course-title="Academic Writing II">Academic Writing II</div>
                        </td>
                        <td class="credits-cell" data-credits="3">3</td>
                        <td class="grade-cell">
                            <span class="grade-badge pending" data-grade="Pending">Pending</span>
                        </td>
                        <td class="points-cell">—</td>
                        <td class="status-cell">
                            <div class="status-indicator">
                                <span class="dot in-progress"></span>
                                <span data-grade-status="In Progress">Grading in progress</span>
                            </div>
                        </td>
                    </tr>
                </tbody>
            </table>
            
            <div class="table-footer">
                <div class="totals-row" data-term-totals>
                    <div class="item">
                        <span class="label">Term Credits (Posted):</span>
                        <span class="value" data-term-credits-posted="11">11</span>
                    </div>
                    <div class="item">
                        <span class="label">Term Credits (Total):</span>
                        <span class="value" data-term-credits-total="18">18</span>
                    </div>
                    <div class="item">
                        <span class="label">Quality Points:</span>
                        <span class="value" data-quality-points="40.3">40.3</span>
                    </div>
                </div>
            </div>
        </div>
        
        <div class="grade-legend">
            <h4>Grade Scale</h4>
            <div class="legend-grid">
                <div class="legend-item"><span class="grade">A</span><span class="points">4.0</span></div>
                <div class="legend-item"><span class="grade">A-</span><span class="points">3.7</span></div>
                <div class="legend-item"><span class="grade">B+</span><span class="points">3.3</span></div>
                <div class="legend-item"><span class="grade">B</span><span class="points">3.0</span></div>
                <div class="legend-item"><span class="grade">B-</span><span class="points">2.7</span></div>
                <div class="legend-item"><span class="grade">C+</span><span class="points">2.3</span></div>
                <div class="legend-item"><span class="grade">C</span><span class="points">2.0</span></div>
                <div class="legend-item"><span class="grade">C-</span><span class="points">1.7</span></div>
                <div class="legend-item"><span class="grade">D+</span><span class="points">1.3</span></div>
                <div class="legend-item"><span class="grade">D</span><span class="points">1.0</span></div>
            </div>
        </div>
    </main>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "studentName": "Alex Johnson",
  "studentId": "12345678",
  "term": "Fall 2024",
  "termGpa": 3.78,
  "cumulativeGpa": 3.65,
  "creditsEarned": 78,
  "gradesPending": 2,
  "courses": [
    {
      "courseCode": "CS 301",
      "courseTitle": "Algorithms and Data Structures",
      "credits": 4,
      "grade": "A",
      "gradePoints": 16.0,
      "status": "Posted"
    },
    {
      "courseCode": "CS 315",
      "courseTitle": "Database Systems",
      "credits": 3,
      "grade": "A-",
      "gradePoints": 11.1,
      "status": "Posted"
    },
    {
      "courseCode": "MATH 240",
      "courseTitle": "Linear Algebra",
      "credits": 4,
      "grade": "B+",
      "gradePoints": 13.2,
      "status": "Posted"
    },
    {
      "courseCode": "PHYS 201",
      "courseTitle": "Physics for Engineers I",
      "credits": 4,
      "grade": "Pending",
      "status": "Pending",
      "expectedDate": "2025-01-18"
    },
    {
      "courseCode": "ENGL 102",
      "courseTitle": "Academic Writing II",
      "credits": 3,
      "grade": "Pending",
      "status": "In Progress"
    }
  ],
  "termCreditsPosted": 11,
  "termCreditsTotal": 18,
  "qualityPoints": 40.3
}
```

---

## Test Implementation Notes

### Test Structure

```csharp
[Test]
[Category("LlmCached")]
public async Task ExtractAcademic_CourseRegistration_DetectsLowSeats()
{
    var llmProvider = await CreateRealLlmProvider();
    var service = CreateAcademicExtractionService(llmProvider);
    
    var result = await service.ExtractCourseInfoAsync(CourseRegistrationHtml);
    
    result.ShouldNotBeNull();
    var cs201 = result.Courses.First(c => c.Section == "001");
    cs201.SeatsAvailable.ShouldBe(2);
    cs201.EnrollmentStatus.ShouldBe("Open");
}

[Test]
[Category("LlmCached")]
public async Task ExtractAcademic_AdmissionDecision_ExtractsScholarship()
{
    var llmProvider = await CreateRealLlmProvider();
    var service = CreateAcademicExtractionService(llmProvider);
    
    var result = await service.ExtractAdmissionInfoAsync(AdmissionPortalHtml);
    
    result.ShouldNotBeNull();
    result.DecisionStatus.ShouldBe("Admitted");
    result.Scholarship.Amount.ShouldBe(25000m);
    result.Scholarship.Period.ShouldBe("per year");
}

[Test]
[Category("LlmCached")]
public async Task ExtractAcademic_GradePortal_IdentifiesPendingGrades()
{
    var llmProvider = await CreateRealLlmProvider();
    var service = CreateAcademicExtractionService(llmProvider);
    
    var result = await service.ExtractGradesAsync(GradePortalHtml);
    
    result.ShouldNotBeNull();
    result.GradesPending.ShouldBe(2);
    result.TermGpa.ShouldBe(3.78m);
    result.Courses.Count(c => c.Status == "Pending").ShouldBe(2);
}
```

### Extraction Fields Schema

```json
{
  "type": "academic",
  "variants": {
    "courseRegistration": {
      "fields": {
        "term": "string",
        "courses": "array<{courseCode, section, title, instructor, schedule, credits, seatsAvailable, seatsTotal, enrollmentStatus, waitlistSize?}>"
      }
    },
    "admissionDecision": {
      "fields": {
        "applicantName": "string",
        "applicationId": "string",
        "decisionStatus": "enum(Pending|Admitted|Waitlisted|Denied)",
        "major": "string?",
        "startTerm": "string?",
        "scholarship": "{name, amount, period}?",
        "deadlines": "array<{task, date, urgent?}>"
      }
    },
    "gradePosting": {
      "fields": {
        "term": "string",
        "termGpa": "decimal",
        "cumulativeGpa": "decimal",
        "gradesPending": "number",
        "courses": "array<{courseCode, courseTitle, credits, grade, status, expectedDate?}>"
      }
    }
  }
}
```
