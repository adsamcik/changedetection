# Content &amp; Document Change Monitoring

## Overview

Users monitor textual content for updates and modifications:
- **News articles** (corrections, updates, retractions)
- **Terms of Service/Privacy Policy** (legal changes)
- **Documentation** (API docs, guides, tutorials)
- **Government notices** (regulations, announcements)
- **Blog posts** (edits, additions)
- **Scientific papers** (version updates, corrections)

## Key Fields to Extract

| Field | Description | Examples |
|-------|-------------|----------|
| `title` | Document title | "Terms of Service" |
| `version` | Version identifier | "v2.3.1", "Rev 4" |
| `lastModified` | Last update date | "2025-01-15" |
| `effectiveDate` | When changes take effect | "2025-02-01" |
| `author` | Document author/editor | "Legal Team" |
| `changeType` | Type of change | "correction", "update" |
| `changeCount` | Number of changes | 15 |
| `sections` | Modified sections | ["Privacy", "Data"] |

---

## Scenario 1: News Article with Corrections

**Context**: User tracking article for factual corrections or updates

### HTML Fixture: `NewsArticleCorrectionHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Major Tech Company Announces Layoffs - Updated | The Daily Chronicle</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: Georgia, 'Times New Roman', serif; background: #fff; color: #222; line-height: 1.7; }
        .site-header { border-bottom: 3px solid #222; padding: 15px 0; text-align: center; }
        .logo { font-size: 2.5rem; font-weight: 700; font-family: 'Playfair Display', Georgia, serif; letter-spacing: -1px; }
        .nav-bar { display: flex; justify-content: center; gap: 30px; padding: 12px 0; border-bottom: 1px solid #ddd; font-size: 0.85rem; text-transform: uppercase; }
        .nav-bar a { color: #555; text-decoration: none; }
        .article-container { max-width: 720px; margin: 0 auto; padding: 40px 20px; }
        .article-meta { margin-bottom: 25px; }
        .category { color: #c41e3a; font-weight: 600; font-size: 0.9rem; text-transform: uppercase; letter-spacing: 1px; margin-bottom: 10px; }
        .headline { font-size: 2.5rem; font-weight: 700; line-height: 1.2; margin-bottom: 15px; font-family: 'Playfair Display', Georgia, serif; }
        .subhead { font-size: 1.3rem; color: #555; font-style: italic; margin-bottom: 20px; }
        .byline { display: flex; align-items: center; gap: 15px; padding: 15px 0; border-top: 1px solid #ddd; border-bottom: 1px solid #ddd; }
        .author-avatar { width: 50px; height: 50px; background: linear-gradient(135deg, #667eea, #764ba2); border-radius: 50%; }
        .author-info { }
        .author-name { font-weight: 600; font-size: 1rem; }
        .author-name a { color: #222; text-decoration: none; }
        .publish-info { color: #666; font-size: 0.85rem; }
        .update-banner { background: #fff3cd; border: 1px solid #ffc107; border-radius: 6px; padding: 15px 20px; margin: 25px 0; }
        .update-banner.correction { background: #f8d7da; border-color: #f5c6cb; }
        .update-banner.breaking { background: #cce5ff; border-color: #b8daff; }
        .update-header { display: flex; align-items: center; gap: 10px; font-weight: 700; margin-bottom: 8px; font-size: 0.95rem; }
        .update-header .icon { font-size: 1.2rem; }
        .update-header.correction { color: #721c24; }
        .update-header.update { color: #856404; }
        .update-content { font-size: 0.9rem; color: #555; }
        .update-content strong { color: #222; }
        .update-timestamp { font-size: 0.8rem; color: #888; margin-top: 8px; }
        .article-body { font-size: 1.15rem; }
        .article-body p { margin-bottom: 20px; }
        .article-body .lead { font-size: 1.3rem; font-weight: 500; }
        .article-body blockquote { border-left: 4px solid #c41e3a; padding-left: 20px; margin: 25px 0; font-style: italic; color: #555; }
        .correction-inline { background: #ffeeba; padding: 2px 4px; border-radius: 3px; }
        .deleted-text { text-decoration: line-through; color: #999; }
        .inserted-text { background: #d4edda; padding: 2px 4px; border-radius: 3px; }
        .related-articles { margin-top: 40px; padding-top: 25px; border-top: 2px solid #ddd; }
        .related-articles h3 { font-size: 1.2rem; margin-bottom: 15px; text-transform: uppercase; letter-spacing: 1px; font-family: Arial, sans-serif; }
        .related-list { list-style: none; }
        .related-list li { padding: 10px 0; border-bottom: 1px solid #eee; }
        .related-list a { color: #222; text-decoration: none; font-size: 1rem; }
        .version-history { background: #f8f9fa; padding: 20px; border-radius: 6px; margin-top: 30px; }
        .version-history h4 { margin-bottom: 15px; font-size: 0.9rem; text-transform: uppercase; letter-spacing: 1px; color: #666; }
        .version-item { display: flex; gap: 15px; padding: 10px 0; border-bottom: 1px solid #e0e0e0; font-size: 0.9rem; }
        .version-item:last-child { border: none; }
        .version-date { color: #666; min-width: 120px; }
        .version-desc { color: #333; }
    </style>
</head>
<body>
    <header class="site-header">
        <div class="logo">The Daily Chronicle</div>
        <nav class="nav-bar">
            <a href="#">Business</a>
            <a href="#">Technology</a>
            <a href="#">Politics</a>
            <a href="#">Opinion</a>
        </nav>
    </header>
    
    <article class="article-container" data-article-id="dc-2025-01-15-tech-layoffs">
        <header class="article-meta">
            <div class="category" data-category="Technology">Technology</div>
            <h1 class="headline" data-title>Major Tech Company Announces Layoffs Affecting 15,000 Workers</h1>
            <p class="subhead" data-subhead>The restructuring comes amid broader industry slowdown and shifting priorities toward AI</p>
            
            <div class="byline">
                <div class="author-avatar"></div>
                <div class="author-info">
                    <div class="author-name">By <a href="#" data-author="Sarah Chen">Sarah Chen</a></div>
                    <div class="publish-info">
                        <span data-published="2025-01-15T09:30:00">Published: January 15, 2025, 9:30 AM EST</span>
                        <span data-updated="2025-01-16T14:45:00"> | Updated: January 16, 2025, 2:45 PM EST</span>
                    </div>
                </div>
            </div>
        </header>
        
        <div class="update-banner correction" data-update-type="correction" data-update-time="2025-01-16T14:45:00">
            <div class="update-header correction">
                <span class="icon">⚠️</span>
                <span>CORRECTION</span>
            </div>
            <div class="update-content" data-correction-text="This article originally stated that 20,000 workers would be affected. The correct number is 15,000">
                <strong>Correction:</strong> An earlier version of this article incorrectly stated that 20,000 workers would be affected by the layoffs. The correct number is <strong>15,000</strong>. We regret the error.
            </div>
            <div class="update-timestamp">Corrected January 16, 2025, 2:45 PM EST</div>
        </div>
        
        <div class="update-banner" data-update-type="update" data-update-time="2025-01-16T11:30:00">
            <div class="update-header update">
                <span class="icon">🔄</span>
                <span>UPDATE</span>
            </div>
            <div class="update-content" data-update-text="Added statement from employee union representative">
                This article has been updated to include a statement from the company's employee union representative and additional context about severance packages.
            </div>
            <div class="update-timestamp">Updated January 16, 2025, 11:30 AM EST</div>
        </div>
        
        <div class="article-body">
            <p class="lead" data-paragraph="lead">
                In a sweeping restructuring move, TechGiant Corp announced yesterday that it would lay off approximately <span class="correction-inline" data-corrected-value="15000"><span class="deleted-text">20,000</span> <span class="inserted-text">15,000</span></span> employees across its global operations, representing about 12% of its workforce.
            </p>
            
            <p data-paragraph="1">
                The layoffs, which will primarily affect the company's cloud computing and advertising divisions, come as the technology sector continues to grapple with slowing growth and increased competition. CEO Michael Roberts announced the decision in an all-hands meeting and a subsequent blog post.
            </p>
            
            <blockquote data-quote="ceo-statement">
                "This was an incredibly difficult decision, but one that is necessary to position our company for long-term success. We must focus our resources on the areas where we can have the greatest impact, particularly in artificial intelligence."
            </blockquote>
            
            <p data-paragraph="2">
                The company said affected employees in the United States would receive at least 16 weeks of severance pay, plus two additional weeks for every year of service. Health insurance coverage will continue for six months, and career transition services will be provided.
            </p>
            
            <p data-paragraph="3" data-section-added="2025-01-16T11:30:00">
                <strong>[Added]</strong> In response to the announcement, the TechGiant Workers Alliance released a statement expressing disappointment with the decision. "While we appreciate the severance packages offered, we believe the company could have explored alternatives such as hiring freezes or reduced executive compensation," said union representative David Park.
            </p>
            
            <p data-paragraph="4">
                Industry analysts have noted that TechGiant's layoffs are part of a broader trend in the technology sector. Over the past year, major tech companies have collectively laid off more than 200,000 workers as they adjust to post-pandemic realities.
            </p>
        </div>
        
        <aside class="version-history" data-version-history>
            <h4>Article History</h4>
            <div class="version-item" data-version="3" data-version-time="2025-01-16T14:45:00">
                <span class="version-date">Jan 16, 2:45 PM</span>
                <span class="version-desc">Corrected employee count from 20,000 to 15,000</span>
            </div>
            <div class="version-item" data-version="2" data-version-time="2025-01-16T11:30:00">
                <span class="version-date">Jan 16, 11:30 AM</span>
                <span class="version-desc">Added union statement and severance details</span>
            </div>
            <div class="version-item" data-version="1" data-version-time="2025-01-15T09:30:00">
                <span class="version-date">Jan 15, 9:30 AM</span>
                <span class="version-desc">Original publication</span>
            </div>
        </aside>
        
        <aside class="related-articles">
            <h3>Related Coverage</h3>
            <ul class="related-list">
                <li><a href="#">Tech Sector Layoffs: A Year in Review</a></li>
                <li><a href="#">How to Navigate a Tech Career Transition</a></li>
                <li><a href="#">AI Investment Surges as Companies Restructure</a></li>
            </ul>
        </aside>
    </article>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "articleId": "dc-2025-01-15-tech-layoffs",
  "title": "Major Tech Company Announces Layoffs Affecting 15,000 Workers",
  "author": "Sarah Chen",
  "category": "Technology",
  "publishedDate": "2025-01-15T09:30:00",
  "lastUpdated": "2025-01-16T14:45:00",
  "updateCount": 2,
  "updates": [
    {
      "type": "correction",
      "timestamp": "2025-01-16T14:45:00",
      "description": "Corrected employee count from 20,000 to 15,000"
    },
    {
      "type": "update",
      "timestamp": "2025-01-16T11:30:00",
      "description": "Added union statement and severance details"
    }
  ],
  "hasCorrection": true,
  "correctionDetails": "Original stated 20,000 workers, corrected to 15,000",
  "versionCount": 3,
  "currentVersion": 3
}
```

---

## Scenario 2: Terms of Service Update

**Context**: User monitoring legal documents for policy changes

### HTML Fixture: `TermsOfServiceHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Terms of Service | CloudApp Platform</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Inter', -apple-system, sans-serif; background: #f8fafc; color: #1e293b; line-height: 1.7; }
        .legal-header { background: #1e40af; color: #fff; padding: 20px 40px; }
        .header-content { max-width: 1000px; margin: 0 auto; display: flex; justify-content: space-between; align-items: center; }
        .logo { font-size: 1.5rem; font-weight: 700; display: flex; align-items: center; gap: 10px; }
        .logo-icon { width: 35px; height: 35px; background: #60a5fa; border-radius: 8px; display: flex; align-items: center; justify-content: center; font-size: 1.2rem; }
        .legal-nav a { color: #fff; text-decoration: none; margin-left: 25px; font-size: 0.9rem; opacity: 0.8; }
        .legal-nav a:hover { opacity: 1; }
        .document-container { max-width: 900px; margin: 0 auto; padding: 40px 20px; }
        .doc-header { margin-bottom: 30px; }
        .doc-title { font-size: 2.2rem; font-weight: 700; margin-bottom: 10px; }
        .doc-meta { display: flex; gap: 30px; flex-wrap: wrap; font-size: 0.9rem; color: #64748b; }
        .meta-item { display: flex; align-items: center; gap: 8px; }
        .update-alert { background: linear-gradient(135deg, #fef3c7, #fde68a); border: 2px solid #f59e0b; border-radius: 10px; padding: 25px; margin-bottom: 30px; }
        .update-alert-header { display: flex; align-items: center; gap: 12px; margin-bottom: 15px; }
        .update-alert-header .icon { font-size: 2rem; }
        .update-alert-header h3 { color: #92400e; font-size: 1.2rem; }
        .update-summary { margin-bottom: 15px; }
        .update-summary p { color: #78350f; font-size: 0.95rem; margin-bottom: 10px; }
        .change-list { list-style: none; margin: 15px 0; }
        .change-list li { padding: 8px 0 8px 25px; position: relative; font-size: 0.9rem; color: #92400e; }
        .change-list li::before { content: "→"; position: absolute; left: 0; color: #f59e0b; font-weight: 700; }
        .effective-notice { background: #fff; border-radius: 6px; padding: 15px; display: flex; align-items: center; gap: 15px; }
        .effective-notice .calendar { font-size: 2rem; }
        .effective-notice .text { font-size: 0.9rem; color: #78350f; }
        .effective-notice .date { font-weight: 700; color: #b45309; }
        .toc-section { background: #fff; border-radius: 10px; padding: 25px; margin-bottom: 30px; box-shadow: 0 1px 3px rgba(0,0,0,0.1); }
        .toc-section h3 { font-size: 1rem; text-transform: uppercase; letter-spacing: 1px; color: #64748b; margin-bottom: 15px; }
        .toc-list { list-style: none; display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
        .toc-list li a { color: #1e40af; text-decoration: none; font-size: 0.95rem; display: flex; align-items: center; gap: 8px; }
        .toc-list li a:hover { text-decoration: underline; }
        .toc-list li.modified a::after { content: "MODIFIED"; background: #fef3c7; color: #92400e; padding: 2px 6px; border-radius: 4px; font-size: 0.7rem; font-weight: 600; margin-left: 8px; }
        .toc-list li.new a::after { content: "NEW"; background: #dcfce7; color: #166534; padding: 2px 6px; border-radius: 4px; font-size: 0.7rem; font-weight: 600; margin-left: 8px; }
        .legal-content { background: #fff; border-radius: 10px; padding: 40px; box-shadow: 0 1px 3px rgba(0,0,0,0.1); }
        .section { margin-bottom: 35px; padding-bottom: 35px; border-bottom: 1px solid #e2e8f0; }
        .section:last-child { border: none; margin-bottom: 0; padding-bottom: 0; }
        .section-header { display: flex; align-items: flex-start; gap: 15px; margin-bottom: 15px; }
        .section-num { background: #1e40af; color: #fff; width: 30px; height: 30px; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-weight: 600; font-size: 0.85rem; flex-shrink: 0; }
        .section-title { font-size: 1.3rem; font-weight: 600; }
        .section-badge { display: inline-block; padding: 3px 10px; border-radius: 15px; font-size: 0.7rem; font-weight: 600; text-transform: uppercase; margin-left: 10px; }
        .section-badge.modified { background: #fef3c7; color: #92400e; }
        .section-badge.new { background: #dcfce7; color: #166534; }
        .section-body { padding-left: 45px; }
        .section-body p { margin-bottom: 12px; color: #475569; }
        .diff-highlight { background: #fef9c3; border-left: 3px solid #eab308; padding: 15px; margin: 15px 0; border-radius: 0 6px 6px 0; }
        .diff-highlight .old { color: #991b1b; text-decoration: line-through; display: block; margin-bottom: 8px; }
        .diff-highlight .new { color: #166534; display: block; }
        .diff-label { font-size: 0.75rem; text-transform: uppercase; font-weight: 600; color: #64748b; margin-bottom: 5px; }
        .version-sidebar { position: fixed; right: 20px; top: 120px; width: 200px; background: #fff; border-radius: 8px; padding: 20px; box-shadow: 0 4px 6px rgba(0,0,0,0.1); }
        .version-sidebar h4 { font-size: 0.85rem; color: #64748b; margin-bottom: 15px; text-transform: uppercase; letter-spacing: 1px; }
        .version-list { list-style: none; }
        .version-list li { padding: 8px 0; border-bottom: 1px solid #f1f5f9; font-size: 0.85rem; }
        .version-list li:last-child { border: none; }
        .version-list .version { font-weight: 600; color: #1e40af; }
        .version-list .date { color: #64748b; }
        .download-section { margin-top: 30px; padding: 25px; background: #f1f5f9; border-radius: 8px; text-align: center; }
        .download-section h4 { margin-bottom: 15px; }
        .download-btn { display: inline-flex; align-items: center; gap: 8px; padding: 12px 24px; background: #1e40af; color: #fff; border-radius: 6px; text-decoration: none; font-weight: 600; }
    </style>
</head>
<body>
    <header class="legal-header">
        <div class="header-content">
            <div class="logo">
                <span class="logo-icon">☁️</span>
                <span>CloudApp</span>
            </div>
            <nav class="legal-nav">
                <a href="#">Terms of Service</a>
                <a href="#">Privacy Policy</a>
                <a href="#">Cookie Policy</a>
                <a href="#">SLA</a>
            </nav>
        </div>
    </header>
    
    <main class="document-container">
        <header class="doc-header">
            <h1 class="doc-title" data-document-title="Terms of Service">Terms of Service</h1>
            <div class="doc-meta">
                <span class="meta-item" data-version="3.2.0">
                    📋 Version 3.2.0
                </span>
                <span class="meta-item" data-last-updated="2025-01-15">
                    📅 Last Updated: January 15, 2025
                </span>
                <span class="meta-item" data-effective-date="2025-02-01">
                    ⏰ Effective: February 1, 2025
                </span>
            </div>
        </header>
        
        <div class="update-alert" data-pending-changes="true">
            <div class="update-alert-header">
                <span class="icon">📢</span>
                <h3>Important Updates to Terms of Service</h3>
            </div>
            <div class="update-summary">
                <p data-change-summary="We've made significant updates to our Terms of Service regarding data handling, AI features, and dispute resolution.">
                    We've made significant updates to our Terms of Service. Please review the changes below carefully.
                </p>
            </div>
            <ul class="change-list" data-key-changes>
                <li data-change="data-retention">Updated data retention policies (Section 4)</li>
                <li data-change="ai-terms">New AI and machine learning terms (Section 7 - NEW)</li>
                <li data-change="arbitration">Modified dispute resolution process (Section 12)</li>
                <li data-change="liability">Clarified liability limitations (Section 9)</li>
            </ul>
            <div class="effective-notice">
                <span class="calendar">📆</span>
                <div class="text">
                    These changes will take effect on <span class="date" data-effective-date="2025-02-01">February 1, 2025</span>. 
                    Continued use of the service after this date constitutes acceptance.
                </div>
            </div>
        </div>
        
        <nav class="toc-section" data-table-of-contents>
            <h3>Table of Contents</h3>
            <ol class="toc-list">
                <li><a href="#s1">1. Acceptance of Terms</a></li>
                <li><a href="#s2">2. Account Registration</a></li>
                <li><a href="#s3">3. Permitted Use</a></li>
                <li class="modified"><a href="#s4" data-section-status="modified">4. Data &amp; Privacy</a></li>
                <li><a href="#s5">5. Payment Terms</a></li>
                <li><a href="#s6">6. Intellectual Property</a></li>
                <li class="new"><a href="#s7" data-section-status="new">7. AI Services</a></li>
                <li><a href="#s8">8. User Content</a></li>
                <li class="modified"><a href="#s9" data-section-status="modified">9. Limitation of Liability</a></li>
                <li><a href="#s10">10. Indemnification</a></li>
                <li><a href="#s11">11. Term &amp; Termination</a></li>
                <li class="modified"><a href="#s12" data-section-status="modified">12. Dispute Resolution</a></li>
            </ol>
        </nav>
        
        <div class="legal-content">
            <section class="section" id="s4" data-section="4" data-section-modified="true">
                <div class="section-header">
                    <span class="section-num">4</span>
                    <h2 class="section-title">Data &amp; Privacy<span class="section-badge modified" data-change-type="modified">Modified</span></h2>
                </div>
                <div class="section-body">
                    <p>CloudApp processes personal data in accordance with our Privacy Policy and applicable data protection laws.</p>
                    
                    <div class="diff-highlight" data-diff-block="data-retention">
                        <div class="diff-label">Changed Text:</div>
                        <span class="old" data-old-text="Data will be retained for 90 days after account termination.">Data will be retained for 90 days after account termination.</span>
                        <span class="new" data-new-text="Data will be retained for 30 days after account termination, after which it will be permanently deleted.">Data will be retained for 30 days after account termination, after which it will be permanently deleted.</span>
                    </div>
                    
                    <p>Users may request data export at any time through the account settings or by contacting support.</p>
                </div>
            </section>
            
            <section class="section" id="s7" data-section="7" data-section-new="true">
                <div class="section-header">
                    <span class="section-num">7</span>
                    <h2 class="section-title">AI Services<span class="section-badge new" data-change-type="new">New Section</span></h2>
                </div>
                <div class="section-body" data-new-section-content>
                    <p data-paragraph="7.1"><strong>7.1 AI-Powered Features.</strong> CloudApp offers various features powered by artificial intelligence and machine learning ("AI Services"). By using AI Services, you acknowledge and agree to the terms in this section.</p>
                    
                    <p data-paragraph="7.2"><strong>7.2 Data Usage for AI.</strong> Content you upload or create may be used to train and improve our AI models. You can opt out of this in your privacy settings without affecting your use of AI Services.</p>
                    
                    <p data-paragraph="7.3"><strong>7.3 No Guarantee of Accuracy.</strong> AI-generated content is provided "as is" without warranties of accuracy or fitness for any particular purpose. You are responsible for reviewing and verifying any AI-generated outputs.</p>
                    
                    <p data-paragraph="7.4"><strong>7.4 Prohibited Uses.</strong> You may not use AI Services to generate content that violates our Acceptable Use Policy, including but not limited to content that is illegal, harmful, deceptive, or infringes on third-party rights.</p>
                </div>
            </section>
            
            <section class="section" id="s12" data-section="12" data-section-modified="true">
                <div class="section-header">
                    <span class="section-num">12</span>
                    <h2 class="section-title">Dispute Resolution<span class="section-badge modified" data-change-type="modified">Modified</span></h2>
                </div>
                <div class="section-body">
                    <p><strong>12.1 Informal Resolution.</strong> Before initiating formal dispute resolution, you agree to contact us and attempt to resolve any dispute informally.</p>
                    
                    <div class="diff-highlight" data-diff-block="arbitration-change">
                        <div class="diff-label">Changed Text:</div>
                        <span class="old" data-old-text="All disputes will be resolved through binding arbitration under AAA Commercial Arbitration Rules.">All disputes will be resolved through binding arbitration under AAA Commercial Arbitration Rules.</span>
                        <span class="new" data-new-text="Disputes under $10,000 may be resolved in small claims court. Disputes over $10,000 will be resolved through binding arbitration under AAA Commercial Arbitration Rules, with the option for either party to appeal arbitration decisions.">Disputes under $10,000 may be resolved in small claims court. Disputes over $10,000 will be resolved through binding arbitration under AAA Commercial Arbitration Rules, with the option for either party to appeal arbitration decisions.</span>
                    </div>
                    
                    <p><strong>12.3 Class Action Waiver.</strong> You agree to resolve disputes on an individual basis and waive the right to participate in class actions.</p>
                </div>
            </section>
        </div>
        
        <div class="download-section">
            <h4>Need a Copy?</h4>
            <a href="#" class="download-btn" data-download-available="true">
                📥 Download PDF (v3.2.0)
            </a>
        </div>
    </main>
    
    <aside class="version-sidebar" data-version-history>
        <h4>Version History</h4>
        <ul class="version-list">
            <li data-version="3.2.0">
                <span class="version">v3.2.0</span>
                <span class="date">Jan 15, 2025</span>
            </li>
            <li data-version="3.1.0">
                <span class="version">v3.1.0</span>
                <span class="date">Sep 1, 2024</span>
            </li>
            <li data-version="3.0.0">
                <span class="version">v3.0.0</span>
                <span class="date">Mar 15, 2024</span>
            </li>
            <li data-version="2.5.0">
                <span class="version">v2.5.0</span>
                <span class="date">Oct 1, 2023</span>
            </li>
        </ul>
    </aside>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "documentTitle": "Terms of Service",
  "version": "3.2.0",
  "lastUpdated": "2025-01-15",
  "effectiveDate": "2025-02-01",
  "pendingChanges": true,
  "changeSummary": "Significant updates regarding data handling, AI features, and dispute resolution",
  "modifiedSections": [
    {"section": 4, "title": "Data & Privacy", "changeType": "modified"},
    {"section": 7, "title": "AI Services", "changeType": "new"},
    {"section": 9, "title": "Limitation of Liability", "changeType": "modified"},
    {"section": 12, "title": "Dispute Resolution", "changeType": "modified"}
  ],
  "keyChanges": [
    "Updated data retention policies",
    "New AI and machine learning terms",
    "Modified dispute resolution process",
    "Clarified liability limitations"
  ],
  "versionHistory": [
    {"version": "3.2.0", "date": "Jan 15, 2025"},
    {"version": "3.1.0", "date": "Sep 1, 2024"},
    {"version": "3.0.0", "date": "Mar 15, 2024"}
  ]
}
```

---

## Scenario 3: Government Regulation Notice

### HTML Fixture: `GovernmentNoticeHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Federal Notice - Proposed Rulemaking | Federal Register</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Source Sans Pro', Arial, sans-serif; background: #f1f1f1; color: #212121; line-height: 1.6; }
        .gov-banner { background: #112e51; color: #fff; padding: 10px 20px; font-size: 0.8rem; }
        .gov-banner .flag { margin-right: 10px; }
        .site-header { background: #fff; border-bottom: 4px solid #0071bc; padding: 15px 40px; }
        .header-content { max-width: 1100px; margin: 0 auto; display: flex; justify-content: space-between; align-items: center; }
        .site-title { font-size: 1.5rem; font-weight: 700; color: #112e51; }
        .site-title span { color: #0071bc; }
        .header-nav a { color: #0071bc; text-decoration: none; margin-left: 25px; font-size: 0.9rem; }
        .breadcrumb { background: #fff; padding: 10px 40px; font-size: 0.85rem; color: #5b616b; }
        .breadcrumb a { color: #0071bc; text-decoration: none; }
        .document-page { max-width: 900px; margin: 0 auto; padding: 30px 20px; }
        .doc-type-banner { background: #fad980; color: #212121; padding: 12px 20px; border-radius: 4px 4px 0 0; font-weight: 700; text-transform: uppercase; letter-spacing: 1px; font-size: 0.85rem; }
        .document-card { background: #fff; border-radius: 0 0 4px 4px; box-shadow: 0 1px 4px rgba(0,0,0,0.1); }
        .doc-header { padding: 25px; border-bottom: 1px solid #d6d7d9; }
        .doc-header h1 { font-size: 1.6rem; color: #112e51; margin-bottom: 15px; line-height: 1.4; }
        .agency-info { display: flex; align-items: center; gap: 15px; margin-bottom: 20px; }
        .agency-seal { width: 60px; height: 60px; background: #112e51; border-radius: 50%; display: flex; align-items: center; justify-content: center; color: #fff; font-size: 1.5rem; }
        .agency-details { }
        .agency-name { font-weight: 700; color: #112e51; font-size: 1.1rem; }
        .agency-sub { color: #5b616b; font-size: 0.9rem; }
        .doc-meta-grid { display: grid; grid-template-columns: repeat(2, 1fr); gap: 15px; }
        .meta-box { background: #f1f1f1; padding: 12px 15px; border-radius: 4px; }
        .meta-box .label { color: #5b616b; font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.5px; margin-bottom: 3px; }
        .meta-box .value { font-weight: 600; color: #212121; }
        .comment-period { background: #e1f3f8; border: 2px solid #02bfe7; border-radius: 6px; padding: 20px; margin: 25px; }
        .comment-header { display: flex; align-items: center; gap: 12px; margin-bottom: 12px; }
        .comment-header .icon { font-size: 1.5rem; }
        .comment-header h3 { color: #046b99; font-size: 1.1rem; }
        .comment-dates { display: flex; gap: 30px; margin-bottom: 15px; }
        .comment-date { }
        .comment-date .label { color: #5b616b; font-size: 0.8rem; }
        .comment-date .date { font-weight: 600; color: #212121; }
        .days-remaining { background: #02bfe7; color: #fff; padding: 8px 15px; border-radius: 20px; font-weight: 700; display: inline-flex; align-items: center; gap: 8px; }
        .submit-comment-btn { display: inline-block; background: #0071bc; color: #fff; padding: 12px 25px; border-radius: 4px; text-decoration: none; font-weight: 600; margin-top: 15px; }
        .doc-body { padding: 25px; }
        .section-heading { font-size: 1.2rem; color: #112e51; margin: 25px 0 15px; padding-bottom: 10px; border-bottom: 2px solid #0071bc; }
        .section-heading:first-child { margin-top: 0; }
        .doc-body p { margin-bottom: 15px; text-align: justify; }
        .summary-box { background: #f1f1f1; border-left: 4px solid #0071bc; padding: 20px; margin: 20px 0; }
        .summary-box h4 { color: #0071bc; margin-bottom: 10px; }
        .key-provisions { margin: 20px 0; }
        .key-provisions h4 { color: #112e51; margin-bottom: 15px; }
        .provision-list { list-style: none; }
        .provision-list li { padding: 12px 0 12px 30px; border-bottom: 1px solid #d6d7d9; position: relative; }
        .provision-list li::before { content: "§"; position: absolute; left: 0; color: #0071bc; font-weight: 700; font-size: 1.1rem; }
        .provision-list li:last-child { border: none; }
        .cfr-citation { background: #fad980; padding: 3px 8px; border-radius: 3px; font-family: monospace; font-size: 0.85rem; }
        .document-footer { background: #f1f1f1; padding: 20px 25px; border-top: 1px solid #d6d7d9; }
        .footer-meta { display: flex; justify-content: space-between; font-size: 0.85rem; color: #5b616b; }
        .related-docs { margin-top: 25px; background: #fff; border-radius: 4px; padding: 25px; box-shadow: 0 1px 4px rgba(0,0,0,0.1); }
        .related-docs h3 { color: #112e51; margin-bottom: 15px; font-size: 1.1rem; }
        .related-list { list-style: none; }
        .related-list li { padding: 10px 0; border-bottom: 1px solid #d6d7d9; }
        .related-list li:last-child { border: none; }
        .related-list a { color: #0071bc; text-decoration: none; }
    </style>
</head>
<body>
    <div class="gov-banner">
        <span class="flag">🇺🇸</span>
        An official website of the United States government
    </div>
    
    <header class="site-header">
        <div class="header-content">
            <div class="site-title">Federal<span>Register</span></div>
            <nav class="header-nav">
                <a href="#">Browse</a>
                <a href="#">Search</a>
                <a href="#">My FR</a>
            </nav>
        </div>
    </header>
    
    <div class="breadcrumb">
        <a href="#">Home</a> › <a href="#">Documents</a> › <a href="#">Environmental Protection Agency</a> › Proposed Rule
    </div>
    
    <main class="document-page">
        <div class="doc-type-banner" data-document-type="Proposed Rule">📋 Proposed Rule</div>
        
        <article class="document-card">
            <header class="doc-header">
                <h1 data-title="Standards for Emissions of Greenhouse Gases From Light-Duty Vehicles">
                    Standards for Emissions of Greenhouse Gases From Light-Duty Vehicles for Model Years 2027-2032
                </h1>
                
                <div class="agency-info">
                    <div class="agency-seal">🏛️</div>
                    <div class="agency-details">
                        <div class="agency-name" data-agency="Environmental Protection Agency">Environmental Protection Agency</div>
                        <div class="agency-sub" data-sub-agency="Office of Transportation and Air Quality">Office of Transportation and Air Quality</div>
                    </div>
                </div>
                
                <div class="doc-meta-grid">
                    <div class="meta-box" data-document-number="2025-00234">
                        <div class="label">Document Number</div>
                        <div class="value">2025-00234</div>
                    </div>
                    <div class="meta-box" data-publication-date="2025-01-15">
                        <div class="label">Publication Date</div>
                        <div class="value">January 15, 2025</div>
                    </div>
                    <div class="meta-box" data-cfr-citation="40 CFR 86">
                        <div class="label">CFR Citation</div>
                        <div class="value">40 CFR Part 86</div>
                    </div>
                    <div class="meta-box" data-rin="2060-AV57">
                        <div class="label">RIN</div>
                        <div class="value">2060-AV57</div>
                    </div>
                </div>
            </header>
            
            <div class="comment-period" data-comment-period-open="true">
                <div class="comment-header">
                    <span class="icon">💬</span>
                    <h3>Public Comment Period Open</h3>
                </div>
                <div class="comment-dates">
                    <div class="comment-date" data-comment-start="2025-01-15">
                        <div class="label">Opens</div>
                        <div class="date">January 15, 2025</div>
                    </div>
                    <div class="comment-date" data-comment-end="2025-03-16">
                        <div class="label">Closes</div>
                        <div class="date">March 16, 2025</div>
                    </div>
                </div>
                <div class="days-remaining" data-days-remaining="60">
                    <span>⏰</span>
                    60 days remaining
                </div>
                <a href="#" class="submit-comment-btn" data-docket="EPA-HQ-OAR-2024-0456">Submit Comment</a>
            </div>
            
            <div class="doc-body">
                <h2 class="section-heading">Summary</h2>
                
                <div class="summary-box" data-summary>
                    <h4>At a Glance</h4>
                    <p data-summary-text="EPA proposes new greenhouse gas emission standards for light-duty vehicles that would require significant reductions in tailpipe emissions for model years 2027-2032, accelerating the transition to electric vehicles.">
                        The Environmental Protection Agency (EPA) is proposing new greenhouse gas (GHG) emission standards for light-duty vehicles that would require significant reductions in tailpipe emissions for model years 2027-2032. These standards would accelerate the transition to zero-emission vehicles while providing flexibility for automakers to meet requirements through various technology pathways.
                    </p>
                </div>
                
                <h2 class="section-heading">Key Provisions</h2>
                
                <div class="key-provisions" data-key-provisions>
                    <ul class="provision-list">
                        <li data-provision="emission-targets">
                            <strong>Emission Reduction Targets:</strong> Requires 56% reduction in fleet average GHG emissions by MY 2032 compared to MY 2026 levels
                        </li>
                        <li data-provision="ev-adoption">
                            <strong>EV Adoption Pathway:</strong> Projects that 67% of new light-duty vehicle sales will be electric by MY 2032
                        </li>
                        <li data-provision="credit-system">
                            <strong>Credit Banking:</strong> Manufacturers can bank and trade emission credits for compliance flexibility
                        </li>
                        <li data-provision="small-manufacturers">
                            <strong>Small Manufacturer Exemption:</strong> Companies producing fewer than 50,000 vehicles annually may apply for alternative standards
                        </li>
                        <li data-provision="enforcement">
                            <strong>Enforcement Mechanism:</strong> Civil penalties of up to $50,000 per non-compliant vehicle
                        </li>
                    </ul>
                </div>
                
                <h2 class="section-heading">Regulatory History</h2>
                
                <p data-regulatory-history>
                    This proposed rule builds upon previous emission standards established under the Clean Air Act. The current rule (finalized in 2021) set standards through MY 2026. This proposal would extend and strengthen those standards for MY 2027-2032, responding to advances in vehicle technology and updated scientific understanding of climate impacts.
                </p>
            </div>
            
            <footer class="document-footer">
                <div class="footer-meta">
                    <span data-page-range="Pages 4521-4687">Pages 4521-4687</span>
                    <span data-fr-volume="90 FR 4521">90 FR 4521</span>
                </div>
            </footer>
        </article>
        
        <aside class="related-docs" data-related-documents>
            <h3>Related Documents</h3>
            <ul class="related-list">
                <li><a href="#" data-related="2021-final-rule">Final Rule: MY 2023-2026 Standards (2021)</a></li>
                <li><a href="#" data-related="economic-analysis">Economic Analysis Report</a></li>
                <li><a href="#" data-related="technical-support">Technical Support Document</a></li>
                <li><a href="#" data-related="public-hearing">Notice of Public Hearing</a></li>
            </ul>
        </aside>
    </main>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "documentType": "Proposed Rule",
  "title": "Standards for Emissions of Greenhouse Gases From Light-Duty Vehicles for Model Years 2027-2032",
  "agency": "Environmental Protection Agency",
  "subAgency": "Office of Transportation and Air Quality",
  "documentNumber": "2025-00234",
  "publicationDate": "2025-01-15",
  "cfrCitation": "40 CFR Part 86",
  "rin": "2060-AV57",
  "commentPeriodOpen": true,
  "commentStart": "2025-01-15",
  "commentEnd": "2025-03-16",
  "daysRemaining": 60,
  "docket": "EPA-HQ-OAR-2024-0456",
  "summary": "EPA proposes new greenhouse gas emission standards for light-duty vehicles requiring significant reductions for MY 2027-2032",
  "keyProvisions": [
    "56% reduction in fleet average GHG emissions by MY 2032",
    "67% of new vehicle sales to be electric by MY 2032",
    "Credit banking and trading for compliance flexibility",
    "Small manufacturer exemption for under 50,000 vehicles",
    "$50,000 penalty per non-compliant vehicle"
  ],
  "pageRange": "4521-4687",
  "frVolume": "90 FR 4521"
}
```

---

## Test Implementation Notes

### Test Structure

```csharp
[Test]
[Category("LlmCached")]
public async Task ExtractContent_NewsArticle_DetectsCorrectionAndUpdates()
{
    var llmProvider = await CreateRealLlmProvider();
    var service = CreateContentExtractionService(llmProvider);
    
    var result = await service.ExtractContentInfoAsync(NewsArticleCorrectionHtml);
    
    result.ShouldNotBeNull();
    result.HasCorrection.ShouldBeTrue();
    result.UpdateCount.ShouldBe(2);
    result.VersionCount.ShouldBe(3);
}

[Test]
[Category("LlmCached")]
public async Task ExtractContent_TermsOfService_DetectsNewSectionAndModifications()
{
    var llmProvider = await CreateRealLlmProvider();
    var service = CreateContentExtractionService(llmProvider);
    
    var result = await service.ExtractContentInfoAsync(TermsOfServiceHtml);
    
    result.ShouldNotBeNull();
    result.PendingChanges.ShouldBeTrue();
    result.EffectiveDate.ShouldBe(new DateOnly(2025, 2, 1));
    result.ModifiedSections.ShouldContain(s => s.ChangeType == "new" && s.Section == 7);
}
```

### Extraction Fields Schema

```json
{
  "type": "contentDocument",
  "fields": {
    "title": "string",
    "version": "string?",
    "lastModified": "datetime",
    "effectiveDate": "date?",
    "author": "string?",
    "hasCorrection": "boolean",
    "updateCount": "number",
    "modifiedSections": "array<{section: number, title: string, changeType: string}>",
    "keyChanges": "array<string>",
    "pendingChanges": "boolean",
    "commentPeriodOpen": "boolean?",
    "commentEnd": "date?"
  }
}
```
