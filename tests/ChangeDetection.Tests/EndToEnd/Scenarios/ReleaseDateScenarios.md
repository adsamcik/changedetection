# Release Date &amp; Launch Monitoring

## Overview

Users monitor anticipated releases for date changes and availability:
- **Video games** (Steam, Epic Games, console releases)
- **Movies/TV shows** (streaming releases, theatrical dates)
- **Software/apps** (version releases, beta launches)
- **Music albums** (pre-order drops, surprise releases)
- **Consumer electronics** (product launches)
- **Books** (publication dates)

## Key Fields to Extract

| Field | Description | Examples |
|-------|-------------|----------|
| `title` | Product name | "Half-Life 3" |
| `releaseDate` | Expected launch date | "2025-03-15" |
| `releaseStatus` | Current status | "Coming Soon", "Released" |
| `preorderAvailable` | Can pre-order | true |
| `platforms` | Available platforms | ["PC", "PS5", "Xbox"] |
| `price` | Launch price | 59.99 |
| `dateChanged` | Was date updated | true |
| `previousDate` | Old release date | "2025-02-01" |

---

## Scenario 1: Steam Game Coming Soon

**Context**: User monitoring an anticipated video game for release date

### HTML Fixture: `SteamGameHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Stellar Odyssey on Steam</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: Arial, sans-serif; background: #1b2838; color: #c7d5e0; }
        .steam-header { background: #171a21; padding: 10px 20px; display: flex; align-items: center; gap: 30px; }
        .steam-logo { color: #fff; font-weight: 700; font-size: 1.3rem; }
        .steam-nav a { color: #b8b6b4; text-decoration: none; font-size: 0.85rem; margin-right: 15px; }
        .game-page { max-width: 1000px; margin: 0 auto; padding: 20px; }
        .game-header { margin-bottom: 20px; }
        .game-title { font-size: 2rem; color: #fff; margin-bottom: 5px; }
        .game-developer { color: #67c1f5; font-size: 0.9rem; }
        .game-content { display: grid; grid-template-columns: 2fr 1fr; gap: 25px; }
        .media-section { }
        .main-image { width: 100%; aspect-ratio: 16/9; background: linear-gradient(135deg, #1e3a5f, #2d1b4e); border-radius: 4px; display: flex; align-items: center; justify-content: center; font-size: 4rem; margin-bottom: 15px; }
        .screenshot-row { display: flex; gap: 8px; }
        .screenshot { flex: 1; aspect-ratio: 16/9; background: linear-gradient(45deg, #1a2634, #0f1923); border-radius: 3px; display: flex; align-items: center; justify-content: center; font-size: 1.5rem; }
        .game-sidebar { }
        .game-image-box { background: linear-gradient(135deg, #667eea, #764ba2); border-radius: 4px; aspect-ratio: 2/1; margin-bottom: 15px; display: flex; align-items: center; justify-content: center; font-size: 3rem; }
        .game-description { font-size: 0.9rem; line-height: 1.6; margin-bottom: 20px; }
        .release-info { background: #16202d; border-radius: 4px; padding: 15px; margin-bottom: 15px; }
        .release-title { color: #8f98a0; font-size: 0.75rem; text-transform: uppercase; letter-spacing: 1px; margin-bottom: 8px; }
        .release-date { font-size: 1.1rem; color: #fff; font-weight: 600; display: flex; align-items: center; gap: 10px; }
        .release-date .icon { font-size: 1.2rem; }
        .coming-soon-badge { background: #67c1f5; color: #1b2838; padding: 5px 12px; border-radius: 3px; font-size: 0.8rem; font-weight: 700; text-transform: uppercase; }
        .date-updated { background: #4c6b22; margin-top: 10px; padding: 10px; border-radius: 3px; font-size: 0.85rem; display: flex; align-items: center; gap: 8px; }
        .date-updated .label { color: #a3cf06; font-weight: 600; }
        .wishlist-section { margin-bottom: 15px; }
        .wishlist-btn { width: 100%; padding: 12px; background: linear-gradient(135deg, #75b022, #588a1b); color: #fff; border: none; border-radius: 3px; font-size: 1rem; font-weight: 600; cursor: pointer; display: flex; align-items: center; justify-content: center; gap: 10px; }
        .wishlist-count { color: #8f98a0; font-size: 0.85rem; text-align: center; margin-top: 8px; }
        .preorder-section { background: #16202d; border-radius: 4px; padding: 15px; margin-bottom: 15px; }
        .preorder-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 12px; }
        .preorder-label { color: #8f98a0; font-size: 0.75rem; text-transform: uppercase; }
        .preorder-price { font-size: 1.3rem; color: #fff; font-weight: 600; }
        .preorder-btn { width: 100%; padding: 12px; background: linear-gradient(135deg, #67c1f5, #4b9fd5); color: #fff; border: none; border-radius: 3px; font-size: 0.95rem; font-weight: 600; cursor: pointer; }
        .preorder-bonus { background: #1e3a5f; padding: 10px; border-radius: 3px; margin-top: 12px; font-size: 0.85rem; }
        .preorder-bonus .title { color: #67c1f5; font-weight: 600; margin-bottom: 5px; }
        .game-tags { display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: 15px; }
        .tag { background: rgba(103, 193, 245, 0.2); color: #67c1f5; padding: 4px 8px; border-radius: 2px; font-size: 0.75rem; }
        .game-details { background: #16202d; border-radius: 4px; padding: 15px; }
        .detail-row { display: flex; justify-content: space-between; padding: 8px 0; border-bottom: 1px solid #1e2d40; font-size: 0.85rem; }
        .detail-row:last-child { border: none; }
        .detail-label { color: #8f98a0; }
        .detail-value { color: #c7d5e0; }
        .detail-value a { color: #67c1f5; text-decoration: none; }
        .editions-section { background: #16202d; border-radius: 4px; padding: 15px; margin-bottom: 15px; }
        .editions-title { color: #fff; font-size: 0.95rem; margin-bottom: 12px; }
        .edition-item { display: flex; justify-content: space-between; align-items: center; padding: 10px; background: #0f1923; border-radius: 3px; margin-bottom: 8px; }
        .edition-item:last-child { margin-bottom: 0; }
        .edition-name { color: #c7d5e0; font-size: 0.9rem; }
        .edition-price { color: #a3cf06; font-weight: 600; }
    </style>
</head>
<body>
    <header class="steam-header">
        <div class="steam-logo">STEAM</div>
        <nav class="steam-nav">
            <a href="#">Store</a>
            <a href="#">Library</a>
            <a href="#">Community</a>
        </nav>
    </header>
    
    <main class="game-page">
        <header class="game-header">
            <h1 class="game-title" data-title="Stellar Odyssey">Stellar Odyssey</h1>
            <div class="game-developer">
                <span data-developer="Cosmic Games Studio">Cosmic Games Studio</span> • 
                <span data-publisher="Galactic Entertainment">Galactic Entertainment</span>
            </div>
        </header>
        
        <div class="game-content">
            <div class="media-section">
                <div class="main-image">🚀</div>
                <div class="screenshot-row">
                    <div class="screenshot">🌌</div>
                    <div class="screenshot">🛸</div>
                    <div class="screenshot">👽</div>
                    <div class="screenshot">🪐</div>
                </div>
            </div>
            
            <div class="game-sidebar">
                <div class="game-image-box">🎮</div>
                
                <p class="game-description" data-description>
                    Embark on an epic journey across the cosmos in this groundbreaking space exploration RPG. 
                    Discover uncharted worlds, build your crew, and unravel the mysteries of a dying universe.
                </p>
                
                <div class="game-tags" data-tags>
                    <span class="tag">Open World</span>
                    <span class="tag">Space</span>
                    <span class="tag">RPG</span>
                    <span class="tag">Sci-fi</span>
                    <span class="tag">Exploration</span>
                </div>
                
                <div class="release-info" data-release-section>
                    <div class="release-title">Release Date</div>
                    <div class="release-date">
                        <span class="icon">📅</span>
                        <span data-release-date="2025-03-28">March 28, 2025</span>
                        <span class="coming-soon-badge" data-status="Coming Soon">Coming Soon</span>
                    </div>
                    <div class="date-updated" data-date-changed="true">
                        <span class="label">📢 Updated:</span>
                        <span data-previous-date="2025-02-14">Previously: February 14, 2025</span>
                    </div>
                </div>
                
                <div class="wishlist-section">
                    <button class="wishlist-btn" data-wishlist-available="true">
                        <span>❤️</span> Add to Wishlist
                    </button>
                    <div class="wishlist-count" data-wishlist-count="847523">847,523 wishlists</div>
                </div>
                
                <div class="preorder-section" data-preorder-available="true">
                    <div class="preorder-header">
                        <span class="preorder-label">Pre-Purchase</span>
                        <span class="preorder-price" data-price="59.99" data-currency="USD">$59.99</span>
                    </div>
                    <button class="preorder-btn">Pre-Purchase Now</button>
                    <div class="preorder-bonus" data-preorder-bonus>
                        <div class="title">🎁 Pre-Order Bonus</div>
                        <div>Includes: Exclusive Ship Skin + 48-Hour Early Access</div>
                    </div>
                </div>
                
                <div class="editions-section" data-editions>
                    <div class="editions-title">Available Editions</div>
                    <div class="edition-item" data-edition="standard">
                        <span class="edition-name">Standard Edition</span>
                        <span class="edition-price">$59.99</span>
                    </div>
                    <div class="edition-item" data-edition="deluxe">
                        <span class="edition-name">Deluxe Edition</span>
                        <span class="edition-price">$79.99</span>
                    </div>
                    <div class="edition-item" data-edition="ultimate">
                        <span class="edition-name">Ultimate Edition</span>
                        <span class="edition-price">$99.99</span>
                    </div>
                </div>
                
                <div class="game-details">
                    <div class="detail-row" data-platforms>
                        <span class="detail-label">Platforms</span>
                        <span class="detail-value" data-platform-list="Windows, macOS">Windows, macOS</span>
                    </div>
                    <div class="detail-row" data-genre>
                        <span class="detail-label">Genre</span>
                        <span class="detail-value"><a href="#">RPG</a>, <a href="#">Adventure</a></span>
                    </div>
                    <div class="detail-row" data-controller>
                        <span class="detail-label">Controller</span>
                        <span class="detail-value">Full Support</span>
                    </div>
                    <div class="detail-row" data-languages>
                        <span class="detail-label">Languages</span>
                        <span class="detail-value">English, Spanish, French, German, Japanese</span>
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
  "title": "Stellar Odyssey",
  "developer": "Cosmic Games Studio",
  "publisher": "Galactic Entertainment",
  "releaseDate": "2025-03-28",
  "releaseStatus": "Coming Soon",
  "dateChanged": true,
  "previousDate": "2025-02-14",
  "preorderAvailable": true,
  "price": 59.99,
  "currency": "USD",
  "preorderBonus": "Exclusive Ship Skin + 48-Hour Early Access",
  "wishlistCount": 847523,
  "platforms": ["Windows", "macOS"],
  "tags": ["Open World", "Space", "RPG", "Sci-fi", "Exploration"],
  "editions": [
    {"name": "Standard Edition", "price": 59.99},
    {"name": "Deluxe Edition", "price": 79.99},
    {"name": "Ultimate Edition", "price": 99.99}
  ]
}
```

---

## Scenario 2: Streaming Movie Release

**Context**: User monitoring movie for streaming availability

### HTML Fixture: `StreamingMovieHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>The Last Horizon | StreamFlix</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Netflix Sans', 'Helvetica Neue', sans-serif; background: #141414; color: #fff; }
        .stream-header { background: linear-gradient(180deg, #000 0%, transparent 100%); padding: 15px 40px; position: fixed; top: 0; left: 0; right: 0; z-index: 100; display: flex; justify-content: space-between; align-items: center; }
        .stream-logo { color: #e50914; font-size: 1.8rem; font-weight: 900; letter-spacing: -1px; }
        .header-nav a { color: #e5e5e5; text-decoration: none; margin-left: 25px; font-size: 0.9rem; }
        .hero-section { position: relative; height: 80vh; min-height: 500px; }
        .hero-bg { position: absolute; inset: 0; background: linear-gradient(135deg, #1a1a2e, #16213e); display: flex; align-items: center; justify-content: center; font-size: 10rem; opacity: 0.3; }
        .hero-overlay { position: absolute; inset: 0; background: linear-gradient(90deg, rgba(20,20,20,0.9) 0%, transparent 50%, transparent 100%); }
        .hero-overlay::after { content: ""; position: absolute; bottom: 0; left: 0; right: 0; height: 150px; background: linear-gradient(transparent, #141414); }
        .hero-content { position: relative; z-index: 1; padding: 0 60px; height: 100%; display: flex; flex-direction: column; justify-content: center; max-width: 600px; }
        .title-logo { font-size: 3.5rem; font-weight: 900; margin-bottom: 20px; letter-spacing: -2px; text-shadow: 2px 2px 10px rgba(0,0,0,0.5); }
        .metadata-row { display: flex; align-items: center; gap: 15px; margin-bottom: 15px; font-size: 0.9rem; color: #b3b3b3; }
        .metadata-row .match { color: #46d369; font-weight: 600; }
        .metadata-row .year { border: 1px solid #fff; padding: 1px 6px; font-size: 0.75rem; }
        .metadata-row .rating { border: 1px solid #fff; padding: 1px 6px; font-size: 0.75rem; }
        .synopsis { font-size: 1.1rem; line-height: 1.5; color: #d2d2d2; margin-bottom: 25px; }
        .cast-line { color: #777; font-size: 0.85rem; margin-bottom: 20px; }
        .cast-line span { color: #fff; }
        .release-announcement { background: linear-gradient(135deg, #e50914, #b20710); border-radius: 8px; padding: 20px; margin-bottom: 25px; }
        .release-announcement.coming-soon { background: linear-gradient(135deg, #1a1a2e, #16213e); border: 2px solid #e50914; }
        .announcement-header { display: flex; align-items: center; gap: 12px; margin-bottom: 12px; }
        .announcement-header .icon { font-size: 1.5rem; }
        .announcement-header .text { font-weight: 600; font-size: 1.1rem; }
        .announcement-date { font-size: 2rem; font-weight: 700; display: flex; align-items: center; gap: 15px; }
        .countdown { display: flex; gap: 15px; margin-top: 15px; }
        .countdown-item { background: rgba(0,0,0,0.4); padding: 12px 18px; border-radius: 6px; text-align: center; }
        .countdown-num { font-size: 1.8rem; font-weight: 700; }
        .countdown-label { font-size: 0.7rem; text-transform: uppercase; color: #999; }
        .action-buttons { display: flex; gap: 12px; margin-bottom: 25px; }
        .btn-primary { display: flex; align-items: center; gap: 10px; padding: 12px 30px; background: #fff; color: #000; border: none; border-radius: 4px; font-size: 1.1rem; font-weight: 600; cursor: pointer; }
        .btn-secondary { display: flex; align-items: center; gap: 10px; padding: 12px 30px; background: rgba(109,109,110,0.7); color: #fff; border: none; border-radius: 4px; font-size: 1.1rem; font-weight: 600; cursor: pointer; }
        .btn-reminder { background: transparent; border: 2px solid #fff; color: #fff; }
        .main-content { padding: 20px 60px; }
        .section-title { font-size: 1.4rem; font-weight: 600; margin-bottom: 20px; }
        .about-section { display: grid; grid-template-columns: 2fr 1fr; gap: 40px; margin-bottom: 40px; }
        .about-text h3 { font-size: 1.5rem; margin-bottom: 15px; }
        .about-text p { color: #d2d2d2; line-height: 1.7; margin-bottom: 15px; }
        .details-box { }
        .detail-group { margin-bottom: 20px; }
        .detail-group .label { color: #777; font-size: 0.85rem; margin-bottom: 5px; }
        .detail-group .value { font-size: 1rem; }
        .detail-group .value a { color: #fff; text-decoration: none; }
        .detail-group .value a:hover { text-decoration: underline; }
        .availability-section { background: #222; border-radius: 8px; padding: 25px; margin-bottom: 40px; }
        .availability-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; }
        .availability-header h3 { font-size: 1.2rem; }
        .platform-grid { display: grid; grid-template-columns: repeat(4, 1fr); gap: 15px; }
        .platform-card { background: #333; border-radius: 6px; padding: 20px; text-align: center; }
        .platform-card .icon { font-size: 2rem; margin-bottom: 10px; }
        .platform-card .name { font-weight: 600; margin-bottom: 5px; }
        .platform-card .status { font-size: 0.85rem; color: #46d369; }
        .platform-card .status.upcoming { color: #ffc107; }
        .platform-card .date { font-size: 0.8rem; color: #777; margin-top: 5px; }
        .similar-section { margin-bottom: 40px; }
        .similar-row { display: flex; gap: 15px; overflow-x: auto; padding-bottom: 15px; }
        .similar-card { flex: 0 0 200px; aspect-ratio: 2/3; background: linear-gradient(135deg, #333, #1a1a1a); border-radius: 4px; display: flex; align-items: center; justify-content: center; font-size: 3rem; }
    </style>
</head>
<body>
    <header class="stream-header">
        <div class="stream-logo">STREAMFLIX</div>
        <nav class="header-nav">
            <a href="#">Home</a>
            <a href="#">Movies</a>
            <a href="#">TV Shows</a>
            <a href="#">My List</a>
        </nav>
    </header>
    
    <section class="hero-section">
        <div class="hero-bg">🎬</div>
        <div class="hero-overlay"></div>
        <div class="hero-content">
            <h1 class="title-logo" data-title="The Last Horizon">The Last Horizon</h1>
            
            <div class="metadata-row">
                <span class="match" data-match-score="98">98% Match</span>
                <span class="year" data-release-year="2025">2025</span>
                <span class="rating" data-rating="PG-13">PG-13</span>
                <span data-duration="2h 28m">2h 28m</span>
                <span data-quality="4K">4K Ultra HD</span>
            </div>
            
            <p class="synopsis" data-synopsis>
                When Earth's last hope rests on a crew of unlikely heroes, they must journey to the edge of the 
                known universe to find a new home for humanity. An epic sci-fi adventure about sacrifice, hope, 
                and the unbreakable human spirit.
            </p>
            
            <div class="cast-line">
                Starring: <span data-cast="Chris Pratt, Zendaya, Oscar Isaac">Chris Pratt, Zendaya, Oscar Isaac</span>
            </div>
            
            <div class="release-announcement coming-soon" data-release-status="Coming Soon">
                <div class="announcement-header">
                    <span class="icon">🎬</span>
                    <span class="text">Streaming Exclusively on StreamFlix</span>
                </div>
                <div class="announcement-date">
                    <span data-release-date="2025-02-14">February 14, 2025</span>
                </div>
                <div class="countdown" data-countdown-active="true">
                    <div class="countdown-item">
                        <div class="countdown-num" data-days="29">29</div>
                        <div class="countdown-label">Days</div>
                    </div>
                    <div class="countdown-item">
                        <div class="countdown-num" data-hours="14">14</div>
                        <div class="countdown-label">Hours</div>
                    </div>
                    <div class="countdown-item">
                        <div class="countdown-num" data-minutes="32">32</div>
                        <div class="countdown-label">Minutes</div>
                    </div>
                </div>
            </div>
            
            <div class="action-buttons">
                <button class="btn-secondary btn-reminder" data-reminder-available="true">
                    <span>🔔</span> Remind Me
                </button>
                <button class="btn-secondary" data-add-to-list="true">
                    <span>➕</span> My List
                </button>
            </div>
        </div>
    </section>
    
    <main class="main-content">
        <section class="about-section">
            <div class="about-text" data-about>
                <h3>About This Movie</h3>
                <p data-full-description>
                    From visionary director James Cameron comes an epic tale of humanity's greatest adventure. 
                    In the year 2157, Earth has become uninhabitable, and the last 10,000 survivors embark on 
                    a desperate journey aboard the starship Horizon. When their ship is thrown off course by 
                    a cosmic anomaly, Captain Elena Vasquez must lead her crew through uncharted space to find 
                    a new home before their resources run out.
                </p>
                <p>
                    Featuring groundbreaking visual effects and a sweeping orchestral score, The Last Horizon 
                    is a testament to the human spirit and our eternal quest for the stars.
                </p>
            </div>
            
            <div class="details-box">
                <div class="detail-group" data-director="James Cameron">
                    <div class="label">Director</div>
                    <div class="value"><a href="#">James Cameron</a></div>
                </div>
                <div class="detail-group" data-cast-full>
                    <div class="label">Cast</div>
                    <div class="value">
                        <a href="#">Chris Pratt</a>, 
                        <a href="#">Zendaya</a>, 
                        <a href="#">Oscar Isaac</a>,
                        <a href="#">Cate Blanchett</a>
                    </div>
                </div>
                <div class="detail-group" data-genres="Sci-Fi, Adventure, Drama">
                    <div class="label">Genres</div>
                    <div class="value">Sci-Fi, Adventure, Drama</div>
                </div>
                <div class="detail-group" data-audio="Dolby Atmos">
                    <div class="label">Audio</div>
                    <div class="value">Dolby Atmos</div>
                </div>
                <div class="detail-group" data-subtitles>
                    <div class="label">Subtitles</div>
                    <div class="value">English, Spanish, French, German, Japanese, Korean</div>
                </div>
            </div>
        </section>
        
        <section class="availability-section" data-availability>
            <div class="availability-header">
                <h3>Where to Watch</h3>
            </div>
            <div class="platform-grid">
                <div class="platform-card" data-platform="StreamFlix">
                    <div class="icon">🎬</div>
                    <div class="name">StreamFlix</div>
                    <div class="status upcoming" data-platform-status="Coming Soon">Coming Soon</div>
                    <div class="date" data-platform-date="2025-02-14">Feb 14, 2025</div>
                </div>
                <div class="platform-card" data-platform="Theaters">
                    <div class="icon">🎭</div>
                    <div class="name">Theaters</div>
                    <div class="status" data-platform-status="Now Showing">Now Showing</div>
                    <div class="date" data-platform-date="2025-01-10">Since Jan 10</div>
                </div>
                <div class="platform-card" data-platform="Digital">
                    <div class="icon">🛒</div>
                    <div class="name">Digital Purchase</div>
                    <div class="status upcoming" data-platform-status="Coming Soon">Coming Soon</div>
                    <div class="date" data-platform-date="2025-03-01">Mar 1, 2025</div>
                </div>
                <div class="platform-card" data-platform="BluRay">
                    <div class="icon">💿</div>
                    <div class="name">Blu-ray/DVD</div>
                    <div class="status upcoming" data-platform-status="Coming Soon">Coming Soon</div>
                    <div class="date" data-platform-date="2025-04-15">Apr 15, 2025</div>
                </div>
            </div>
        </section>
        
        <section class="similar-section">
            <h2 class="section-title">More Like This</h2>
            <div class="similar-row">
                <div class="similar-card">🚀</div>
                <div class="similar-card">🌟</div>
                <div class="similar-card">🛸</div>
                <div class="similar-card">🌌</div>
            </div>
        </section>
    </main>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "title": "The Last Horizon",
  "releaseYear": 2025,
  "releaseDate": "2025-02-14",
  "releaseStatus": "Coming Soon",
  "platform": "StreamFlix",
  "duration": "2h 28m",
  "rating": "PG-13",
  "quality": "4K Ultra HD",
  "director": "James Cameron",
  "cast": ["Chris Pratt", "Zendaya", "Oscar Isaac", "Cate Blanchett"],
  "genres": ["Sci-Fi", "Adventure", "Drama"],
  "audio": "Dolby Atmos",
  "countdownActive": true,
  "daysRemaining": 29,
  "reminderAvailable": true,
  "platformAvailability": [
    {"platform": "StreamFlix", "status": "Coming Soon", "date": "2025-02-14"},
    {"platform": "Theaters", "status": "Now Showing", "date": "2025-01-10"},
    {"platform": "Digital Purchase", "status": "Coming Soon", "date": "2025-03-01"},
    {"platform": "Blu-ray/DVD", "status": "Coming Soon", "date": "2025-04-15"}
  ]
}
```

---

## Scenario 3: Software Version Release

### HTML Fixture: `SoftwareReleaseHtml`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>DevTools Pro 4.0 - Coming Soon | Official Release Page</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Inter', -apple-system, sans-serif; background: linear-gradient(135deg, #0f0f23, #1a1a3e); color: #fff; min-height: 100vh; }
        .header { padding: 20px 40px; display: flex; justify-content: space-between; align-items: center; }
        .logo { font-size: 1.5rem; font-weight: 700; display: flex; align-items: center; gap: 10px; }
        .logo-icon { width: 35px; height: 35px; background: linear-gradient(135deg, #667eea, #764ba2); border-radius: 8px; display: flex; align-items: center; justify-content: center; }
        .nav a { color: #a5b4fc; text-decoration: none; margin-left: 30px; font-size: 0.9rem; }
        .hero { text-align: center; padding: 80px 20px 60px; }
        .version-badge { display: inline-block; background: linear-gradient(135deg, #667eea, #764ba2); padding: 8px 20px; border-radius: 25px; font-weight: 600; font-size: 0.9rem; margin-bottom: 25px; }
        .hero h1 { font-size: 3.5rem; font-weight: 800; margin-bottom: 20px; background: linear-gradient(135deg, #fff, #a5b4fc); -webkit-background-clip: text; -webkit-text-fill-color: transparent; }
        .hero .subtitle { font-size: 1.3rem; color: #a5b4fc; max-width: 600px; margin: 0 auto 40px; line-height: 1.6; }
        .release-card { background: rgba(255,255,255,0.05); backdrop-filter: blur(10px); border: 1px solid rgba(255,255,255,0.1); border-radius: 20px; padding: 40px; max-width: 500px; margin: 0 auto 40px; }
        .release-card-header { text-align: center; margin-bottom: 30px; }
        .release-card-header .label { color: #a5b4fc; font-size: 0.85rem; text-transform: uppercase; letter-spacing: 2px; margin-bottom: 10px; }
        .release-card-header .date { font-size: 2rem; font-weight: 700; }
        .release-card-header .time { color: #a5b4fc; font-size: 1rem; margin-top: 5px; }
        .countdown-grid { display: grid; grid-template-columns: repeat(4, 1fr); gap: 15px; margin-bottom: 30px; }
        .countdown-box { background: rgba(102, 126, 234, 0.2); border-radius: 12px; padding: 20px 10px; text-align: center; }
        .countdown-box .num { font-size: 2.5rem; font-weight: 700; }
        .countdown-box .label { font-size: 0.75rem; color: #a5b4fc; text-transform: uppercase; margin-top: 5px; }
        .notify-form { display: flex; gap: 10px; }
        .notify-form input { flex: 1; padding: 15px 20px; background: rgba(255,255,255,0.1); border: 1px solid rgba(255,255,255,0.2); border-radius: 10px; color: #fff; font-size: 1rem; }
        .notify-form input::placeholder { color: #a5b4fc; }
        .notify-form button { padding: 15px 30px; background: linear-gradient(135deg, #667eea, #764ba2); border: none; border-radius: 10px; color: #fff; font-weight: 600; cursor: pointer; white-space: nowrap; }
        .features { padding: 60px 40px; max-width: 1000px; margin: 0 auto; }
        .features h2 { text-align: center; font-size: 2rem; margin-bottom: 40px; }
        .feature-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 25px; }
        .feature-card { background: rgba(255,255,255,0.05); border-radius: 16px; padding: 30px; text-align: center; border: 1px solid rgba(255,255,255,0.1); }
        .feature-card .icon { font-size: 2.5rem; margin-bottom: 15px; }
        .feature-card h3 { font-size: 1.1rem; margin-bottom: 10px; }
        .feature-card p { color: #a5b4fc; font-size: 0.9rem; line-height: 1.5; }
        .feature-card .new-badge { display: inline-block; background: #10b981; padding: 3px 10px; border-radius: 10px; font-size: 0.7rem; font-weight: 600; margin-left: 5px; }
        .release-notes { padding: 60px 40px; max-width: 800px; margin: 0 auto; }
        .release-notes h2 { text-align: center; font-size: 1.8rem; margin-bottom: 40px; }
        .changelog { background: rgba(255,255,255,0.05); border-radius: 16px; padding: 30px; }
        .changelog-section { margin-bottom: 25px; }
        .changelog-section:last-child { margin-bottom: 0; }
        .changelog-section h4 { display: flex; align-items: center; gap: 10px; margin-bottom: 15px; font-size: 1rem; }
        .changelog-section h4 .badge { padding: 3px 10px; border-radius: 10px; font-size: 0.7rem; }
        .changelog-section h4 .badge.new { background: #10b981; }
        .changelog-section h4 .badge.improved { background: #3b82f6; }
        .changelog-section h4 .badge.fixed { background: #f59e0b; }
        .changelog-section h4 .badge.breaking { background: #ef4444; }
        .changelog-list { list-style: none; }
        .changelog-list li { padding: 8px 0; padding-left: 25px; position: relative; color: #e2e8f0; font-size: 0.95rem; }
        .changelog-list li::before { content: "•"; position: absolute; left: 0; color: #667eea; font-weight: 700; }
        .beta-section { padding: 60px 40px; text-align: center; }
        .beta-card { background: rgba(239, 68, 68, 0.1); border: 2px solid rgba(239, 68, 68, 0.3); border-radius: 16px; padding: 40px; max-width: 600px; margin: 0 auto; }
        .beta-card h3 { font-size: 1.5rem; margin-bottom: 15px; }
        .beta-card p { color: #fca5a5; margin-bottom: 20px; }
        .beta-btn { display: inline-flex; align-items: center; gap: 10px; padding: 15px 30px; background: rgba(239, 68, 68, 0.2); border: 1px solid #ef4444; border-radius: 10px; color: #fff; text-decoration: none; font-weight: 600; }
        .pricing-preview { padding: 60px 40px; max-width: 1000px; margin: 0 auto; }
        .pricing-preview h2 { text-align: center; font-size: 1.8rem; margin-bottom: 40px; }
        .pricing-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 25px; }
        .pricing-card { background: rgba(255,255,255,0.05); border-radius: 16px; padding: 30px; text-align: center; border: 1px solid rgba(255,255,255,0.1); }
        .pricing-card.featured { border-color: #667eea; background: rgba(102,126,234,0.1); }
        .pricing-card .plan { font-size: 1.2rem; font-weight: 600; margin-bottom: 15px; }
        .pricing-card .price { font-size: 2.5rem; font-weight: 700; margin-bottom: 5px; }
        .pricing-card .price span { font-size: 1rem; color: #a5b4fc; }
        .pricing-card .billing { color: #a5b4fc; font-size: 0.9rem; margin-bottom: 20px; }
        .pricing-card ul { list-style: none; text-align: left; margin-bottom: 25px; }
        .pricing-card li { padding: 8px 0; font-size: 0.9rem; color: #e2e8f0; display: flex; align-items: center; gap: 10px; }
        .pricing-card li::before { content: "✓"; color: #10b981; font-weight: 700; }
    </style>
</head>
<body>
    <header class="header">
        <div class="logo">
            <span class="logo-icon">⚡</span>
            <span>DevTools Pro</span>
        </div>
        <nav class="nav">
            <a href="#">Features</a>
            <a href="#">Pricing</a>
            <a href="#">Docs</a>
            <a href="#">Blog</a>
        </nav>
    </header>
    
    <section class="hero">
        <div class="version-badge" data-version="4.0.0">Version 4.0.0</div>
        <h1 data-product-name="DevTools Pro">DevTools Pro 4.0</h1>
        <p class="subtitle" data-tagline>
            The most powerful developer toolkit just got even better. 
            AI-assisted coding, real-time collaboration, and blazing-fast performance.
        </p>
    </section>
    
    <div class="release-card" data-release-info>
        <div class="release-card-header">
            <div class="label">Official Release</div>
            <div class="date" data-release-date="2025-02-01">February 1, 2025</div>
            <div class="time" data-release-time="09:00 PST">9:00 AM PST</div>
        </div>
        
        <div class="countdown-grid" data-countdown>
            <div class="countdown-box">
                <div class="num" data-days="16">16</div>
                <div class="label">Days</div>
            </div>
            <div class="countdown-box">
                <div class="num" data-hours="8">08</div>
                <div class="label">Hours</div>
            </div>
            <div class="countdown-box">
                <div class="num" data-minutes="42">42</div>
                <div class="label">Minutes</div>
            </div>
            <div class="countdown-box">
                <div class="num" data-seconds="15">15</div>
                <div class="label">Seconds</div>
            </div>
        </div>
        
        <form class="notify-form" data-notify-available="true">
            <input type="email" placeholder="Enter your email">
            <button type="submit">Notify Me</button>
        </form>
    </div>
    
    <section class="features" data-features>
        <h2>What's New in 4.0</h2>
        <div class="feature-grid">
            <div class="feature-card" data-feature="ai-copilot">
                <div class="icon">🤖</div>
                <h3>AI Copilot<span class="new-badge">NEW</span></h3>
                <p>Intelligent code suggestions powered by advanced language models. Write code 3x faster.</p>
            </div>
            <div class="feature-card" data-feature="live-collab">
                <div class="icon">👥</div>
                <h3>Live Collaboration<span class="new-badge">NEW</span></h3>
                <p>Real-time pair programming with built-in video chat and shared terminals.</p>
            </div>
            <div class="feature-card" data-feature="performance">
                <div class="icon">⚡</div>
                <h3>10x Faster</h3>
                <p>Complete rewrite of the core engine. Large projects now load in milliseconds.</p>
            </div>
            <div class="feature-card" data-feature="git-integration">
                <div class="icon">🔀</div>
                <h3>Git Timeline</h3>
                <p>Visual git history with inline blame, interactive rebase, and conflict resolution.</p>
            </div>
            <div class="feature-card" data-feature="cloud-sync">
                <div class="icon">☁️</div>
                <h3>Cloud Sync<span class="new-badge">NEW</span></h3>
                <p>Seamlessly sync your settings, extensions, and projects across all devices.</p>
            </div>
            <div class="feature-card" data-feature="extensions">
                <div class="icon">🧩</div>
                <h3>Extension API v2</h3>
                <p>Rebuilt extension API with better performance and more capabilities.</p>
            </div>
        </div>
    </section>
    
    <section class="release-notes" data-changelog>
        <h2>Changelog Preview</h2>
        <div class="changelog">
            <div class="changelog-section">
                <h4><span class="badge new">New</span> New Features</h4>
                <ul class="changelog-list" data-new-features>
                    <li data-change="ai-copilot">AI Copilot with GPT-4 integration for intelligent code completion</li>
                    <li data-change="live-collab">Real-time collaboration with up to 10 simultaneous editors</li>
                    <li data-change="cloud-sync">Cloud sync for settings, extensions, and workspace state</li>
                    <li data-change="notebook">Jupyter notebook support with live cell execution</li>
                </ul>
            </div>
            <div class="changelog-section">
                <h4><span class="badge improved">Improved</span> Improvements</h4>
                <ul class="changelog-list" data-improvements>
                    <li data-change="perf">10x faster project loading and indexing</li>
                    <li data-change="memory">50% reduction in memory usage</li>
                    <li data-change="search">Improved search with regex and semantic matching</li>
                </ul>
            </div>
            <div class="changelog-section">
                <h4><span class="badge breaking">Breaking</span> Breaking Changes</h4>
                <ul class="changelog-list" data-breaking-changes>
                    <li data-change="ext-api">Extension API v1 deprecated - migrate to v2</li>
                    <li data-change="node">Minimum Node.js version increased to 18.x</li>
                </ul>
            </div>
        </div>
    </section>
    
    <section class="beta-section" data-beta>
        <div class="beta-card">
            <h3>🧪 Try the Beta Now</h3>
            <p data-beta-version="4.0.0-beta.3">Version 4.0.0-beta.3 is available for early adopters</p>
            <a href="#" class="beta-btn" data-beta-available="true">
                <span>⬇️</span> Download Beta
            </a>
        </div>
    </section>
    
    <section class="pricing-preview" data-pricing>
        <h2>Pricing (v4.0)</h2>
        <div class="pricing-grid">
            <div class="pricing-card" data-plan="free">
                <div class="plan">Free</div>
                <div class="price">$0</div>
                <div class="billing">Forever free</div>
                <ul>
                    <li>Core editor features</li>
                    <li>Basic extensions</li>
                    <li>Community support</li>
                </ul>
            </div>
            <div class="pricing-card featured" data-plan="pro">
                <div class="plan">Pro</div>
                <div class="price" data-price="15" data-currency="USD">$15<span>/mo</span></div>
                <div class="billing">Billed annually</div>
                <ul>
                    <li>Everything in Free</li>
                    <li>AI Copilot (unlimited)</li>
                    <li>Cloud sync</li>
                    <li>Priority support</li>
                </ul>
            </div>
            <div class="pricing-card" data-plan="team">
                <div class="plan">Team</div>
                <div class="price" data-price="25" data-currency="USD">$25<span>/user/mo</span></div>
                <div class="billing">Billed annually</div>
                <ul>
                    <li>Everything in Pro</li>
                    <li>Live collaboration</li>
                    <li>Admin dashboard</li>
                    <li>SSO &amp; SAML</li>
                </ul>
            </div>
        </div>
    </section>
</body>
</html>
```

**Expected Extraction**:
```json
{
  "productName": "DevTools Pro",
  "version": "4.0.0",
  "releaseDate": "2025-02-01",
  "releaseTime": "09:00 PST",
  "releaseStatus": "Coming Soon",
  "daysRemaining": 16,
  "notifyAvailable": true,
  "betaAvailable": true,
  "betaVersion": "4.0.0-beta.3",
  "newFeatures": [
    "AI Copilot with GPT-4 integration",
    "Real-time collaboration",
    "Cloud sync",
    "Jupyter notebook support"
  ],
  "improvements": [
    "10x faster project loading",
    "50% reduction in memory usage",
    "Improved search"
  ],
  "breakingChanges": [
    "Extension API v1 deprecated",
    "Minimum Node.js version increased to 18.x"
  ],
  "pricing": {
    "free": 0,
    "pro": 15,
    "team": 25,
    "currency": "USD"
  }
}
```

---

## Test Implementation Notes

### Test Structure

```csharp
[Test]
[Category("LlmCached")]
public async Task ExtractRelease_SteamGame_DetectsDateChange()
{
    var llmProvider = await CreateRealLlmProvider();
    var service = CreateReleaseExtractionService(llmProvider);
    
    var result = await service.ExtractReleaseInfoAsync(SteamGameHtml);
    
    result.ShouldNotBeNull();
    result.ReleaseStatus.ShouldBe("Coming Soon");
    result.DateChanged.ShouldBeTrue();
    result.PreviousDate.ShouldBe(new DateOnly(2025, 2, 14));
    result.ReleaseDate.ShouldBe(new DateOnly(2025, 3, 28));
}

[Test]
[Category("LlmCached")]
public async Task ExtractRelease_StreamingMovie_ExtractsPlatformAvailability()
{
    var llmProvider = await CreateRealLlmProvider();
    var service = CreateReleaseExtractionService(llmProvider);
    
    var result = await service.ExtractReleaseInfoAsync(StreamingMovieHtml);
    
    result.ShouldNotBeNull();
    result.PlatformAvailability.ShouldContain(p => 
        p.Platform == "StreamFlix" && p.Status == "Coming Soon");
    result.PlatformAvailability.ShouldContain(p => 
        p.Platform == "Theaters" && p.Status == "Now Showing");
}
```

### Extraction Fields Schema

```json
{
  "type": "releaseDate",
  "fields": {
    "title": "string",
    "version": "string?",
    "releaseDate": "date",
    "releaseStatus": "enum(Coming Soon|Released|Delayed|Cancelled)",
    "dateChanged": "boolean",
    "previousDate": "date?",
    "preorderAvailable": "boolean",
    "price": "decimal?",
    "platforms": "array<string>",
    "countdownActive": "boolean",
    "daysRemaining": "number?",
    "betaAvailable": "boolean?"
  }
}
```
