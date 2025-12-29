# Job Posting Monitoring

## Overview

Users monitor job boards for new opportunities or changes to existing postings:
- **Dream company jobs** (monitoring specific companies like Google, Apple)
- **Salary transparency** (when salary ranges change or are added)
- **Remote opportunities** (filtering by work type)
- **Application deadlines** (tracking closing dates)
- **Reposted positions** (previously closed jobs reopened)

## Key Fields to Extract

| Field | Description | Examples |
|-------|-------------|----------|
| `jobTitle` | Position title | "Senior Software Engineer" |
| `company` | Company name | "Google" |
| `location` | Work location | "San Francisco, CA (Hybrid)" |
| `salary` | Compensation range | `{"min": 150000, "max": 220000}` |
| `workType` | Remote/hybrid/onsite | "Remote" |
| `experienceLevel` | Seniority | "Senior" |
| `postedDate` | When posted | "2025-01-15" |
| `closingDate` | Application deadline | "2025-02-15" |
| `applicationStatus` | Open/closed/paused | "Open" |
| `applicantCount` | Number of applicants | 847 |

---

## Scenario 1: LinkedIn Job Listing

**Context**: User monitoring for senior engineering roles at a specific company

### HTML Fixture: `LinkedInJobHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Senior Software Engineer - Google | LinkedIn</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #f3f2ef; color: #000000e6; }
        .linkedin-header { background: #fff; padding: 12px 24px; display: flex; align-items: center; gap: 20px; box-shadow: 0 1px 3px rgba(0,0,0,0.08); position: sticky; top: 0; z-index: 100; }
        .linkedin-logo { color: #0a66c2; font-size: 2rem; font-weight: 700; }
        .search-bar { flex: 1; max-width: 400px; display: flex; background: #eef3f8; border-radius: 4px; }
        .search-bar input { flex: 1; padding: 10px 15px; border: none; background: transparent; font-size: 0.9rem; }
        .nav-links { display: flex; gap: 25px; margin-left: auto; }
        .nav-links a { color: #666; text-decoration: none; font-size: 0.8rem; text-align: center; }
        .main-container { max-width: 1128px; margin: 0 auto; padding: 24px; display: grid; grid-template-columns: 1fr 320px; gap: 24px; }
        .job-card { background: #fff; border-radius: 8px; box-shadow: 0 0 0 1px rgba(0,0,0,0.08); padding: 24px; margin-bottom: 16px; }
        .job-header { display: flex; gap: 16px; margin-bottom: 20px; }
        .company-logo { width: 72px; height: 72px; background: linear-gradient(135deg, #4285f4, #34a853); border-radius: 8px; display: flex; align-items: center; justify-content: center; font-size: 2rem; color: #fff; }
        .job-title-section { flex: 1; }
        .job-title { font-size: 1.5rem; font-weight: 600; color: #000000e6; margin-bottom: 4px; }
        .company-name { font-size: 1rem; color: #0a66c2; text-decoration: none; margin-bottom: 4px; display: block; }
        .job-location { color: #666; font-size: 0.9rem; margin-bottom: 8px; }
        .job-meta { display: flex; flex-wrap: wrap; gap: 8px; }
        .meta-badge { background: #e8f4f8; color: #0a66c2; padding: 4px 10px; border-radius: 12px; font-size: 0.75rem; font-weight: 500; }
        .meta-badge.remote { background: #e7f3e8; color: #057642; }
        .meta-badge.promoted { background: #f3e8f4; color: #7b1fa2; }
        .action-buttons { display: flex; gap: 12px; margin-top: 20px; padding-top: 16px; border-top: 1px solid #e0e0e0; }
        .apply-btn { background: #0a66c2; color: #fff; border: none; padding: 12px 24px; border-radius: 24px; font-size: 1rem; font-weight: 600; cursor: pointer; display: flex; align-items: center; gap: 8px; }
        .apply-btn:disabled { background: #ccc; cursor: not-allowed; }
        .save-btn { background: #fff; color: #0a66c2; border: 1px solid #0a66c2; padding: 12px 24px; border-radius: 24px; font-size: 1rem; font-weight: 600; cursor: pointer; }
        .job-insights { display: flex; flex-wrap: wrap; gap: 20px; padding: 16px 0; border-top: 1px solid #e0e0e0; margin-top: 16px; }
        .insight-item { display: flex; align-items: center; gap: 10px; }
        .insight-icon { width: 32px; height: 32px; background: #f3f2ef; border-radius: 50%; display: flex; align-items: center; justify-content: center; }
        .insight-text { font-size: 0.85rem; }
        .insight-text strong { display: block; color: #000000e6; }
        .insight-text span { color: #666; }
        .salary-section { background: #f8faf8; border: 1px solid #d4edda; border-radius: 8px; padding: 16px; margin: 16px 0; }
        .salary-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 8px; }
        .salary-header h4 { font-size: 0.9rem; color: #057642; }
        .salary-range { font-size: 1.4rem; font-weight: 600; color: #000000e6; }
        .salary-details { color: #666; font-size: 0.8rem; }
        .applicant-count { background: #fff3e0; border-radius: 8px; padding: 12px 16px; display: flex; align-items: center; gap: 12px; margin: 16px 0; }
        .applicant-icon { font-size: 1.5rem; }
        .applicant-text { font-size: 0.9rem; }
        .applicant-text strong { color: #e65100; }
        .deadline-warning { background: #ffebee; border: 1px solid #ffcdd2; border-radius: 8px; padding: 12px 16px; display: flex; align-items: center; gap: 12px; margin: 16px 0; }
        .deadline-icon { font-size: 1.2rem; }
        .deadline-text { font-size: 0.9rem; color: #c62828; }
        .job-description { padding-top: 16px; }
        .job-description h3 { font-size: 1.1rem; margin-bottom: 12px; }
        .job-description ul { padding-left: 20px; color: #666; font-size: 0.9rem; line-height: 1.6; }
        .job-description li { margin-bottom: 8px; }
        .sidebar { display: flex; flex-direction: column; gap: 16px; }
        .sidebar-card { background: #fff; border-radius: 8px; box-shadow: 0 0 0 1px rgba(0,0,0,0.08); padding: 16px; }
        .sidebar-card h3 { font-size: 1rem; margin-bottom: 12px; }
        .company-info { display: flex; align-items: center; gap: 12px; margin-bottom: 12px; }
        .company-info .logo { width: 48px; height: 48px; background: linear-gradient(135deg, #4285f4, #34a853); border-radius: 8px; display: flex; align-items: center; justify-content: center; font-size: 1.5rem; color: #fff; }
        .company-info .details h4 { font-size: 0.95rem; }
        .company-info .details span { color: #666; font-size: 0.8rem; }
        .company-stats { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; padding-top: 12px; border-top: 1px solid #e0e0e0; }
        .stat-item .value { font-weight: 600; font-size: 1rem; }
        .stat-item .label { color: #666; font-size: 0.75rem; }
        .similar-jobs { list-style: none; }
        .similar-jobs li { padding: 12px 0; border-bottom: 1px solid #e0e0e0; }
        .similar-jobs li:last-child { border: none; }
        .similar-jobs a { color: #0a66c2; text-decoration: none; font-size: 0.9rem; }
        .similar-jobs span { display: block; color: #666; font-size: 0.8rem; }
        .closed-banner { background: #f5f5f5; border: 2px solid #bdbdbd; border-radius: 8px; padding: 20px; text-align: center; margin-bottom: 16px; }
        .closed-banner h3 { color: #616161; margin-bottom: 8px; }
        .closed-banner p { color: #9e9e9e; font-size: 0.9rem; }
        .notify-form { margin-top: 16px; }
        .notify-form input { width: 100%; padding: 10px; border: 1px solid #e0e0e0; border-radius: 4px; margin-bottom: 10px; }
        .notify-form button { width: 100%; padding: 10px; background: #0a66c2; color: #fff; border: none; border-radius: 20px; cursor: pointer; }
    </style>
</head>
<body>
    <header class="linkedin-header">
        <div class="linkedin-logo">in</div>
        <div class="search-bar">
            <input type="text" placeholder="Search jobs">
        </div>
        <nav class="nav-links">
            <a href="#">Jobs</a>
            <a href="#">Network</a>
            <a href="#">Messages</a>
        </nav>
    </header>
    
    <main class="main-container">
        <div class="job-content">
            <div class="job-card">
                <div class="job-header">
                    <div class="company-logo" data-company-id="google">G</div>
                    <div class="job-title-section">
                        <h1 class="job-title" data-job-id="3847261950">Senior Software Engineer, Cloud Infrastructure</h1>
                        <a href="#" class="company-name" data-company="Google">Google</a>
                        <div class="job-location" data-location="Mountain View, CA">
                            Mountain View, CA (Hybrid)
                        </div>
                        <div class="job-meta">
                            <span class="meta-badge" data-posted="2025-01-10">Posted 5 days ago</span>
                            <span class="meta-badge remote" data-work-type="Hybrid">Hybrid</span>
                            <span class="meta-badge" data-level="Senior">Senior level</span>
                            <span class="meta-badge" data-type="Full-time">Full-time</span>
                        </div>
                    </div>
                </div>
                
                <div class="salary-section" data-salary-min="185000" data-salary-max="274000" data-currency="USD">
                    <div class="salary-header">
                        <h4>💰 Salary Information</h4>
                        <span style="color: #057642; font-size: 0.75rem;">Verified by Google</span>
                    </div>
                    <div class="salary-range">$185,000 - $274,000/yr</div>
                    <div class="salary-details">Base salary • Stock options • Annual bonus eligible</div>
                </div>
                
                <div class="applicant-count" data-applicants="847">
                    <span class="applicant-icon">👥</span>
                    <span class="applicant-text"><strong>847 applicants</strong> • Be an early applicant for visibility</span>
                </div>
                
                <div class="deadline-warning" data-deadline="2025-01-25" data-days-left="5">
                    <span class="deadline-icon">⏰</span>
                    <span class="deadline-text">Application closes in <strong>5 days</strong> (Jan 25, 2025)</span>
                </div>
                
                <div class="job-insights">
                    <div class="insight-item">
                        <div class="insight-icon">🎓</div>
                        <div class="insight-text">
                            <strong>Bachelor's degree</strong>
                            <span>in Computer Science or related</span>
                        </div>
                    </div>
                    <div class="insight-item">
                        <div class="insight-icon">💼</div>
                        <div class="insight-text">
                            <strong data-experience="5">5+ years experience</strong>
                            <span>with distributed systems</span>
                        </div>
                    </div>
                    <div class="insight-item">
                        <div class="insight-icon">🛠️</div>
                        <div class="insight-text">
                            <strong>Key Skills</strong>
                            <span data-skills="Go,Kubernetes,GCP">Go, Kubernetes, GCP</span>
                        </div>
                    </div>
                </div>
                
                <div class="action-buttons">
                    <button class="apply-btn" data-application-status="open">
                        <span>Apply</span>
                        <span>Easy Apply</span>
                    </button>
                    <button class="save-btn">💾 Save</button>
                </div>
                
                <div class="job-description">
                    <h3>About the job</h3>
                    <ul>
                        <li>Design and develop large-scale distributed systems for Google Cloud Platform</li>
                        <li>Lead technical initiatives across multiple teams</li>
                        <li>Mentor junior engineers and conduct code reviews</li>
                        <li>Collaborate with product managers on roadmap planning</li>
                    </ul>
                </div>
            </div>
        </div>
        
        <aside class="sidebar">
            <div class="sidebar-card">
                <h3>About the company</h3>
                <div class="company-info">
                    <div class="logo">G</div>
                    <div class="details">
                        <h4>Google</h4>
                        <span>Technology • 100,000+ employees</span>
                    </div>
                </div>
                <div class="company-stats">
                    <div class="stat-item" data-open-jobs="2847">
                        <div class="value">2,847</div>
                        <div class="label">Open jobs</div>
                    </div>
                    <div class="stat-item">
                        <div class="value">4.5★</div>
                        <div class="label">Employee rating</div>
                    </div>
                </div>
            </div>
            
            <div class="sidebar-card">
                <h3>Similar jobs</h3>
                <ul class="similar-jobs">
                    <li>
                        <a href="#">Senior Backend Engineer</a>
                        <span>Meta • Menlo Park, CA</span>
                    </li>
                    <li>
                        <a href="#">Staff Software Engineer</a>
                        <span>Apple • Cupertino, CA</span>
                    </li>
                    <li>
                        <a href="#">Principal Engineer</a>
                        <span>Amazon • Seattle, WA</span>
                    </li>
                </ul>
            </div>
        </aside>
    </main>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "jobTitle": "Senior Software Engineer, Cloud Infrastructure",
  "company": "Google",
  "location": "Mountain View, CA",
  "workType": "Hybrid",
  "employmentType": "Full-time",
  "experienceLevel": "Senior",
  "salary": {"min": 185000, "max": 274000, "currency": "USD"},
  "postedDate": "2025-01-10",
  "closingDate": "2025-01-25",
  "daysLeft": 5,
  "applicantCount": 847,
  "applicationStatus": "open",
  "requiredExperience": "5+ years",
  "skills": ["Go", "Kubernetes", "GCP"],
  "companyOpenJobs": 2847
}
```

---

## Scenario 2: Indeed Job Search Results

### HTML Fixture: `IndeedJobClosedHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Product Manager - Stripe | Indeed</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Noto Sans', sans-serif; background: #f5f5f5; color: #2d2d2d; }
        .indeed-header { background: #2164f3; padding: 16px 24px; display: flex; align-items: center; gap: 30px; }
        .indeed-logo { color: #fff; font-size: 1.6rem; font-weight: 700; }
        .search-form { display: flex; gap: 10px; flex: 1; max-width: 700px; }
        .search-form input { padding: 12px 16px; border: none; border-radius: 8px; font-size: 1rem; }
        .search-form input:first-child { flex: 1; }
        .search-form input:last-child { width: 200px; }
        .search-form button { padding: 12px 24px; background: #164ac9; color: #fff; border: none; border-radius: 8px; font-weight: 600; cursor: pointer; }
        .main-layout { max-width: 1200px; margin: 0 auto; padding: 24px; }
        .job-container { background: #fff; border-radius: 12px; box-shadow: 0 1px 4px rgba(0,0,0,0.08); overflow: hidden; }
        .status-banner { background: #fff3cd; color: #856404; padding: 16px 24px; display: flex; align-items: center; gap: 12px; border-bottom: 1px solid #ffeeba; }
        .status-banner.closed { background: #f8d7da; color: #721c24; border-color: #f5c6cb; }
        .status-banner .icon { font-size: 1.5rem; }
        .status-banner .text strong { display: block; }
        .status-banner .text span { font-size: 0.9rem; }
        .job-details { padding: 24px; }
        .job-header { margin-bottom: 20px; }
        .job-title-row { display: flex; justify-content: space-between; align-items: flex-start; }
        .job-title { font-size: 1.6rem; font-weight: 700; margin-bottom: 8px; }
        .company-row { display: flex; align-items: center; gap: 12px; margin-bottom: 8px; }
        .company-logo { width: 50px; height: 50px; background: linear-gradient(135deg, #635bff, #00d4ff); border-radius: 8px; display: flex; align-items: center; justify-content: center; font-size: 1.5rem; color: #fff; font-weight: 700; }
        .company-info a { color: #2164f3; text-decoration: none; font-size: 1.1rem; font-weight: 500; }
        .company-info .rating { display: flex; align-items: center; gap: 5px; font-size: 0.85rem; color: #666; margin-top: 3px; }
        .company-info .rating .stars { color: #ffc107; }
        .job-meta-row { display: flex; flex-wrap: wrap; gap: 16px; color: #666; font-size: 0.9rem; }
        .job-meta-row span { display: flex; align-items: center; gap: 5px; }
        .salary-highlight { background: linear-gradient(135deg, #e8f5e9, #c8e6c9); border-radius: 10px; padding: 20px; margin: 20px 0; }
        .salary-highlight h4 { color: #2e7d32; font-size: 0.9rem; margin-bottom: 8px; display: flex; align-items: center; gap: 8px; }
        .salary-highlight .amount { font-size: 1.8rem; font-weight: 700; color: #1b5e20; }
        .salary-highlight .details { color: #558b2f; font-size: 0.85rem; margin-top: 5px; }
        .job-tags { display: flex; flex-wrap: wrap; gap: 8px; margin: 16px 0; }
        .tag { background: #e8f4fd; color: #2164f3; padding: 6px 14px; border-radius: 20px; font-size: 0.8rem; font-weight: 500; }
        .tag.remote { background: #e8f5e9; color: #2e7d32; }
        .tag.urgent { background: #fce4ec; color: #c2185b; }
        .closed-notice { background: #ffebee; border: 2px dashed #ef9a9a; border-radius: 10px; padding: 30px; text-align: center; margin: 20px 0; }
        .closed-notice .icon { font-size: 3rem; margin-bottom: 15px; }
        .closed-notice h3 { color: #c62828; margin-bottom: 10px; }
        .closed-notice p { color: #666; font-size: 0.95rem; margin-bottom: 20px; }
        .notify-similar { background: #f5f5f5; border-radius: 8px; padding: 20px; }
        .notify-similar h4 { margin-bottom: 12px; font-size: 0.95rem; }
        .notify-similar .form-row { display: flex; gap: 10px; }
        .notify-similar input { flex: 1; padding: 12px; border: 1px solid #ddd; border-radius: 6px; }
        .notify-similar button { padding: 12px 20px; background: #2164f3; color: #fff; border: none; border-radius: 6px; cursor: pointer; }
        .job-requirements { padding: 20px; background: #fafafa; border-top: 1px solid #eee; }
        .job-requirements h3 { font-size: 1.1rem; margin-bottom: 15px; }
        .requirements-list { list-style: none; }
        .requirements-list li { display: flex; align-items: flex-start; gap: 10px; padding: 10px 0; border-bottom: 1px solid #eee; font-size: 0.9rem; }
        .requirements-list li:last-child { border: none; }
        .requirements-list .check { color: #4caf50; }
        .timeline-info { display: flex; gap: 20px; padding: 20px; background: #f5f5f5; border-top: 1px solid #eee; }
        .timeline-item { display: flex; align-items: center; gap: 10px; font-size: 0.85rem; color: #666; }
        .timeline-item .icon { font-size: 1.2rem; }
    </style>
</head>
<body>
    <header class="indeed-header">
        <div class="indeed-logo">indeed</div>
        <div class="search-form">
            <input type="text" placeholder="Job title, keywords, or company">
            <input type="text" placeholder="City, state, or remote">
            <button>Find jobs</button>
        </div>
    </header>
    
    <main class="main-layout">
        <div class="job-container">
            <div class="status-banner closed" data-status="closed">
                <span class="icon">🚫</span>
                <div class="text">
                    <strong>This job is no longer accepting applications</strong>
                    <span>Position closed on January 12, 2025</span>
                </div>
            </div>
            
            <div class="job-details">
                <div class="job-header">
                    <div class="job-title-row">
                        <h1 class="job-title" data-job-id="IND-7482961">Senior Product Manager, Payments</h1>
                    </div>
                    <div class="company-row">
                        <div class="company-logo">S</div>
                        <div class="company-info">
                            <a href="#" data-company="Stripe">Stripe</a>
                            <div class="rating">
                                <span class="stars">★★★★☆</span>
                                <span>4.2 (1,847 reviews)</span>
                            </div>
                        </div>
                    </div>
                    <div class="job-meta-row">
                        <span data-location="San Francisco, CA">📍 San Francisco, CA</span>
                        <span data-work-type="Remote">🏠 Remote eligible</span>
                        <span data-type="Full-time">💼 Full-time</span>
                    </div>
                </div>
                
                <div class="salary-highlight" data-salary-min="180000" data-salary-max="260000">
                    <h4>💵 Estimated Salary</h4>
                    <div class="amount">$180,000 - $260,000 a year</div>
                    <div class="details">Based on Indeed data • Includes equity compensation</div>
                </div>
                
                <div class="job-tags">
                    <span class="tag remote" data-work-style="Remote">🌐 Remote OK</span>
                    <span class="tag" data-level="Senior">Senior Level</span>
                    <span class="tag">Product Management</span>
                    <span class="tag">Fintech</span>
                </div>
                
                <div class="closed-notice" data-closed-date="2025-01-12" data-reopen-likelihood="low">
                    <div class="icon">📋</div>
                    <h3>Position Filled</h3>
                    <p>This role has been filled. We'll notify you when similar positions open at Stripe.</p>
                    <div class="notify-similar">
                        <h4>🔔 Get notified about similar jobs</h4>
                        <div class="form-row">
                            <input type="email" placeholder="Enter your email" data-job-alert="true">
                            <button>Create Alert</button>
                        </div>
                    </div>
                </div>
            </div>
            
            <div class="job-requirements">
                <h3>Job Requirements (when posted)</h3>
                <ul class="requirements-list">
                    <li><span class="check">✓</span> 7+ years of product management experience</li>
                    <li><span class="check">✓</span> Experience with payments or financial services</li>
                    <li><span class="check">✓</span> Strong technical background</li>
                    <li><span class="check">✓</span> MBA or equivalent preferred</li>
                </ul>
            </div>
            
            <div class="timeline-info">
                <div class="timeline-item" data-posted="2024-12-05">
                    <span class="icon">📅</span>
                    <span>Posted: December 5, 2024</span>
                </div>
                <div class="timeline-item" data-closed="2025-01-12">
                    <span class="icon">🔒</span>
                    <span>Closed: January 12, 2025</span>
                </div>
                <div class="timeline-item" data-applicants="1247">
                    <span class="icon">👥</span>
                    <span>1,247 applicants</span>
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
  "jobTitle": "Senior Product Manager, Payments",
  "company": "Stripe",
  "companyRating": 4.2,
  "location": "San Francisco, CA",
  "workType": "Remote",
  "employmentType": "Full-time",
  "experienceLevel": "Senior",
  "salary": {"min": 180000, "max": 260000},
  "postedDate": "2024-12-05",
  "closedDate": "2025-01-12",
  "applicationStatus": "closed",
  "applicantCount": 1247,
  "reopenLikelihood": "low",
  "alertAvailable": true
}
```

---

## Scenario 3: Greenhouse Job Board

### HTML Fixture: `GreenhouseRemoteJobHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Staff Engineer - Figma Careers</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Inter', -apple-system, sans-serif; background: #fafafa; color: #333; }
        .site-header { background: #0d0d0d; padding: 20px 40px; display: flex; justify-content: space-between; align-items: center; }
        .logo { display: flex; align-items: center; gap: 12px; color: #fff; font-size: 1.4rem; font-weight: 700; }
        .logo-icon { width: 32px; height: 32px; background: linear-gradient(135deg, #f24e1e, #a259ff, #1abcfe, #0acf83); border-radius: 8px; }
        .nav-links { display: flex; gap: 30px; }
        .nav-links a { color: #999; text-decoration: none; font-size: 0.9rem; transition: color 0.2s; }
        .nav-links a:hover { color: #fff; }
        .page-container { max-width: 900px; margin: 0 auto; padding: 50px 20px; }
        .breadcrumb { font-size: 0.85rem; color: #666; margin-bottom: 20px; }
        .breadcrumb a { color: #a259ff; text-decoration: none; }
        .job-header { margin-bottom: 40px; }
        .department { color: #a259ff; font-size: 0.9rem; font-weight: 600; text-transform: uppercase; letter-spacing: 1px; margin-bottom: 10px; }
        .job-title { font-size: 2.5rem; font-weight: 700; color: #0d0d0d; margin-bottom: 15px; line-height: 1.2; }
        .location-info { display: flex; flex-wrap: wrap; gap: 20px; color: #666; font-size: 1rem; }
        .location-info span { display: flex; align-items: center; gap: 8px; }
        .location-badge { background: #e8f5e9; color: #2e7d32; padding: 6px 14px; border-radius: 20px; font-size: 0.85rem; font-weight: 500; }
        .apply-section { background: #fff; border-radius: 16px; padding: 30px; box-shadow: 0 2px 10px rgba(0,0,0,0.06); margin-bottom: 30px; }
        .apply-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; }
        .apply-header h3 { font-size: 1.2rem; }
        .posting-status { display: flex; align-items: center; gap: 8px; font-size: 0.85rem; }
        .status-dot { width: 10px; height: 10px; border-radius: 50%; }
        .status-dot.open { background: #4caf50; }
        .status-dot.paused { background: #ff9800; }
        .salary-display { background: linear-gradient(135deg, #f3e5f5, #e1f5fe); border-radius: 12px; padding: 20px; margin-bottom: 20px; }
        .salary-display h4 { font-size: 0.85rem; color: #666; margin-bottom: 8px; }
        .salary-display .range { font-size: 1.6rem; font-weight: 700; color: #0d0d0d; }
        .salary-display .equity { color: #666; font-size: 0.9rem; margin-top: 5px; }
        .apply-btn { width: 100%; padding: 16px; background: #0d0d0d; color: #fff; border: none; border-radius: 12px; font-size: 1rem; font-weight: 600; cursor: pointer; transition: background 0.2s; }
        .apply-btn:hover { background: #333; }
        .apply-note { text-align: center; color: #666; font-size: 0.85rem; margin-top: 15px; }
        .job-content { background: #fff; border-radius: 16px; padding: 40px; box-shadow: 0 2px 10px rgba(0,0,0,0.06); }
        .content-section { margin-bottom: 30px; }
        .content-section h3 { font-size: 1.2rem; margin-bottom: 15px; color: #0d0d0d; }
        .content-section p { color: #555; line-height: 1.7; margin-bottom: 15px; }
        .content-section ul { padding-left: 25px; color: #555; line-height: 1.8; }
        .content-section li { margin-bottom: 8px; }
        .perks-grid { display: grid; grid-template-columns: repeat(2, 1fr); gap: 15px; margin-top: 15px; }
        .perk-item { display: flex; align-items: center; gap: 12px; padding: 15px; background: #f8f9fa; border-radius: 10px; }
        .perk-icon { font-size: 1.5rem; }
        .perk-text { font-size: 0.9rem; }
        .team-info { background: #f8f9fa; border-radius: 12px; padding: 25px; margin-top: 30px; }
        .team-info h4 { margin-bottom: 15px; }
        .team-members { display: flex; gap: 15px; }
        .team-member { text-align: center; }
        .member-avatar { width: 60px; height: 60px; background: linear-gradient(135deg, #667eea, #764ba2); border-radius: 50%; margin-bottom: 8px; }
        .member-name { font-size: 0.85rem; font-weight: 500; }
        .member-role { font-size: 0.75rem; color: #666; }
        .urgency-banner { background: linear-gradient(135deg, #fff3e0, #ffe0b2); border: 1px solid #ffb74d; border-radius: 10px; padding: 16px 20px; margin-bottom: 20px; display: flex; align-items: center; gap: 12px; }
        .urgency-banner .icon { font-size: 1.3rem; }
        .urgency-banner .text { font-size: 0.9rem; color: #e65100; }
    </style>
</head>
<body>
    <header class="site-header">
        <div class="logo">
            <div class="logo-icon"></div>
            <span>Figma Careers</span>
        </div>
        <nav class="nav-links">
            <a href="#">All Jobs</a>
            <a href="#">Teams</a>
            <a href="#">Locations</a>
            <a href="#">Life at Figma</a>
        </nav>
    </header>
    
    <main class="page-container">
        <div class="breadcrumb">
            <a href="#">Careers</a> / <a href="#">Engineering</a> / Staff Engineer
        </div>
        
        <div class="job-header">
            <div class="department" data-department="Engineering">Engineering</div>
            <h1 class="job-title" data-job-id="FIG-4829156">Staff Engineer, Multiplayer Infrastructure</h1>
            <div class="location-info">
                <span data-locations="Remote US,Remote UK,San Francisco">🌍 Remote (US, UK) or San Francisco</span>
                <span class="location-badge" data-work-type="Remote">Fully Remote</span>
                <span data-type="Full-time">Full-time</span>
            </div>
        </div>
        
        <div class="apply-section">
            <div class="apply-header">
                <h3>Apply for this role</h3>
                <div class="posting-status" data-status="open">
                    <span class="status-dot open"></span>
                    <span>Actively hiring</span>
                </div>
            </div>
            
            <div class="urgency-banner" data-urgency="high" data-headcount="2">
                <span class="icon">⚡</span>
                <span class="text">High priority hire - Looking to fill <strong>2 positions</strong> in the next 30 days</span>
            </div>
            
            <div class="salary-display" data-salary-min="220000" data-salary-max="340000" data-currency="USD">
                <h4>Compensation Range</h4>
                <div class="range">$220,000 — $340,000 USD</div>
                <div class="equity" data-equity="0.05-0.15">+ 0.05% - 0.15% equity</div>
            </div>
            
            <button class="apply-btn" data-application-open="true">Apply Now</button>
            <p class="apply-note">Resume and cover letter required</p>
        </div>
        
        <div class="job-content">
            <div class="content-section">
                <h3>About the Role</h3>
                <p data-posted="2025-01-08">Posted on January 8, 2025</p>
                <p>We're looking for a Staff Engineer to lead the development of Figma's real-time multiplayer infrastructure. You'll work on some of the most challenging distributed systems problems at scale.</p>
            </div>
            
            <div class="content-section">
                <h3>What You'll Do</h3>
                <ul>
                    <li>Architect and build systems that handle millions of concurrent collaborative sessions</li>
                    <li>Lead technical strategy for the multiplayer platform team</li>
                    <li>Mentor senior engineers and establish best practices</li>
                    <li>Partner with product to shape the future of collaborative design</li>
                </ul>
            </div>
            
            <div class="content-section">
                <h3>Requirements</h3>
                <ul data-requirements>
                    <li data-experience="8">8+ years of software engineering experience</li>
                    <li>Deep expertise in distributed systems and real-time collaboration</li>
                    <li data-skills="Rust,WebSocket,CRDT">Proficiency with Rust, WebSockets, or CRDTs</li>
                    <li>Track record of leading large-scale technical initiatives</li>
                </ul>
            </div>
            
            <div class="content-section">
                <h3>Benefits &amp; Perks</h3>
                <div class="perks-grid">
                    <div class="perk-item">
                        <span class="perk-icon">🏥</span>
                        <span class="perk-text">100% health coverage</span>
                    </div>
                    <div class="perk-item">
                        <span class="perk-icon">🏡</span>
                        <span class="perk-text">Remote-first culture</span>
                    </div>
                    <div class="perk-item">
                        <span class="perk-icon">📚</span>
                        <span class="perk-text">$5,000 learning budget</span>
                    </div>
                    <div class="perk-item">
                        <span class="perk-icon">🌴</span>
                        <span class="perk-text">Unlimited PTO</span>
                    </div>
                </div>
            </div>
            
            <div class="team-info">
                <h4>Meet the team</h4>
                <div class="team-members">
                    <div class="team-member">
                        <div class="member-avatar"></div>
                        <div class="member-name">Sarah Chen</div>
                        <div class="member-role">Engineering Director</div>
                    </div>
                    <div class="team-member">
                        <div class="member-avatar"></div>
                        <div class="member-name">Marcus Johnson</div>
                        <div class="member-role">Staff Engineer</div>
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
  "jobTitle": "Staff Engineer, Multiplayer Infrastructure",
  "company": "Figma",
  "department": "Engineering",
  "location": "Remote (US, UK) or San Francisco",
  "workType": "Remote",
  "employmentType": "Full-time",
  "salary": {"min": 220000, "max": 340000, "currency": "USD"},
  "equity": "0.05% - 0.15%",
  "postedDate": "2025-01-08",
  "applicationStatus": "open",
  "urgency": "high",
  "headcount": 2,
  "requiredExperience": "8+ years",
  "skills": ["Rust", "WebSocket", "CRDT"],
  "applicationOpen": true
}
```

---

## Test Implementation Notes

### Test Structure

```csharp
[Test]
[Category("LlmCached")]
public async Task ExtractJob_LinkedInPosting_IdentifiesSalaryAndDeadline()
{
    var llmProvider = await CreateRealLlmProvider();
    var service = CreateJobExtractionService(llmProvider);
    
    var result = await service.ExtractJobInfoAsync(LinkedInJobHtml);
    
    result.ShouldNotBeNull();
    result.JobTitle.ShouldBe("Senior Software Engineer, Cloud Infrastructure");
    result.Salary.Min.ShouldBe(185000);
    result.Salary.Max.ShouldBe(274000);
    result.DaysLeft.ShouldBe(5);
    result.ApplicationStatus.ShouldBe("open");
}

[Test]
[Category("LlmCached")]
public async Task ExtractJob_ClosedPosting_DetectsClosedStatus()
{
    var llmProvider = await CreateRealLlmProvider();
    var service = CreateJobExtractionService(llmProvider);
    
    var result = await service.ExtractJobInfoAsync(IndeedJobClosedHtml);
    
    result.ShouldNotBeNull();
    result.ApplicationStatus.ShouldBe("closed");
    result.ClosedDate.ShouldBe(new DateOnly(2025, 1, 12));
    result.AlertAvailable.ShouldBeTrue();
}
```

### Extraction Fields Schema

```json
{
  "type": "jobPosting",
  "fields": {
    "jobTitle": "string",
    "company": "string",
    "location": "string",
    "workType": "enum(remote|hybrid|onsite)",
    "employmentType": "enum(full-time|part-time|contract)",
    "salary": "object{min: number, max: number, currency: string}",
    "postedDate": "date",
    "closingDate": "date?",
    "applicationStatus": "enum(open|closed|paused)",
    "applicantCount": "number?",
    "requiredExperience": "string?",
    "skills": "string[]",
    "urgency": "enum(normal|high)?",
    "headcount": "number?",
    "alertAvailable": "boolean"
  }
}
```
