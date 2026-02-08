# Real-World Pipeline Examples

Tested with Playwright against live websites (Feb 2025). Validates the block model across 10 scenarios on 7 sites.

## Summary

| Stat | Value |
|------|-------|
| Scenarios tested | 12 |
| Need 0 runtime LLM calls | **7 of 12** |
| Need 1-2 LLM calls | 5 of 12 |
| Used Paginate block | 3 |
| Used Route block | 2 |
| Used Enrich block | 4 |
| Used Aggregate block | 3 |

### Cross-Site Patterns

| Pattern | Sites | Blocks Used | LLM Needed? |
|---------|-------|------------|-------------|
| Price/numeric tracking | Amazon | ExtractSchema → NumericDelta → Condition | No |
| New items in list | Slashdot, Lever, BBC, blog.google | ExtractSchema → ListDiff → Condition | No |
| Topic relevance filtering | BBC, Reddit, EUR-Lex | LlmEvaluate on new items only | Yes, but only on diff |
| Multi-page collection | Slashdot, EUR-Lex | Paginate → aggregate into single list | No |
| Multi-signal routing | Amazon (price+stock+reviews) | Route → separate branches | No |
| Content enrichment | BBC articles, EUR-Lex docs | Enrich → follow link → extract details | Sometimes |
| Trend/digest | Reddit, OpenAI jobs | Aggregate → LlmExtract summary | Yes |
| Structured metadata | EUR-Lex (amendments) | ExtractSchema on known DOM | No |

---

## BBC.com — Top Story Tracking
**Runtime LLM: 0**

```
User: "Tell me when BBC changes its top stories"

Input(url="bbc.com")
  → Navigate(waitUntil="domcontentloaded")
  → Wait(selector="[data-testid='westminster-card']")
  → ExtractSchema(scope="[data-testid$='-card']", schema: {headline, description, url, tag, isLive})
  → Filter(where="headline IS NOT NULL", limit=7)
  → ListDiff(key="url", track=["headline","isLive"])
  → Condition(if="added.length > 0 OR removed.length > 0")
  → Transform(map: {leadStory, added, removed})
  → Notify(email)
  → Output
```

**Risks:** BBC React codenames (westminster, dundee) could change. Relative timestamps cause false diffs.

## BBC.com — Topic Monitoring
**Runtime LLM: 1-2**

```
User: "Alert me when BBC publishes anything about AI regulation"

Input(url="bbc.com")
  → Navigate → Wait → ExtractSchema(all cards: {headline, description, url, tag})
  → Filter(headline IS NOT NULL)
  → HashCompare(key="url", emit="new_items_only")
  → Condition(if="items.length == 0" → skip)
  → LlmEvaluate("Which headlines relate to AI regulation/policy?")
  → Filter(relevance >= 0.7)
  → Enrich(forEach: match → Navigate(article url) → ExtractSchema(body text))
  → LlmExtract("Summarize focus on AI regulation")
  → Throttle(max=3/hour)
  → Notify(email)
  → Output
```

## blog.google — New AI Articles
**Runtime LLM: 0**

```
User: "Watch Google's blog for new AI-related articles"

Input(url="blog.google")
  → Navigate(waitUntil="networkidle")
  → Wait(selector="uni-article-feed a.feed-article__overlay")
  → Scroll(target="uni-article-feed")
  → ExtractSchema({title, url, category, date})
  → Filter(category CONTAINS "AI" OR "DeepMind" OR "Gemini")
  → ListDiff(key="url", mode="additions_only")
  → Condition(if="added.length > 0")
  → Enrich(forEach → extract abstract, author from JSON-LD)
  → Notify(email)
  → Output
```

**Risks:** `<uni-article-feed>` is a Web Component (shadow DOM). Only ~9 items render on scroll.

## blog.google — CEO Remarks with Route
**Runtime LLM: 0-1**

```
User: "Monitor when Google CEO posts or edits earnings remarks"

Input(url="blog.google")
  → Navigate → Wait → ExtractSchema(scope=".card--no-image", schema: {title, summary, url})
  → Enrich(navigate to url → extract fullText, publishDate)
  → Route(
      newUrl → LlmExtract("Extract revenue, strategic themes, quotes") → Notify("New CEO post")
      sameUrl → TextDiff(fullText) → Condition(changeRatio > 1%) → LlmEvaluate("Classify edits") → Notify("Remarks updated")
    )
  → Output
```

## Amazon — Price Tracking
**Runtime LLM: 0**

```
User: "Watch this laptop, tell me if price drops below $500"

Input(url="amazon.com/dp/B0D1XD1ZV3", checkInterval="6h")
  → Navigate(useJavaScript=true)
  → Wait(selector=".a-price .a-offscreen")
  → Filter(css="#corePrice_feature_div")
  → ExtractSchema({price, stock, title})
  → NumericDelta(field="price")
  → Condition(price < 500 OR droppedByPercent > 10)
  → Notify(email)
  → Output
```

**Risks:** Amazon rotates price selectors. Bot detection returns CAPTCHA page. Multiple price variants.

## Amazon — Multi-Signal with Route
**Runtime LLM: 0**

```
User: "Monitor this product — price drops, out of stock, or bad reviews"

Input(url="amazon.com/dp/B0D1XD1ZV3", checkInterval="4h")
  → Navigate(useJavaScript=true) → Wait
  → ExtractSchema({price, stock, rating, reviewCount})
  → StructDiff(identity="url")
  → Route(
      priceDropped → Condition(droppedByPercent > 5) → Notify("Price dropped")
      stockChanged → Condition(stock changed to "OutOfStock") → Notify("Out of stock!")
      ratingDropped → Condition(rating < 4.0 AND delta < -0.2) → Notify("Rating falling")
    )
  → Output
```

## OpenAI Jobs — Filtered Tracking
**Runtime LLM: 0**

```
User: "Alert me when OpenAI posts new engineering jobs in San Francisco"

Input(url="jobs.lever.co/openai", checkInterval="12h")
  → Navigate → Wait(selector=".posting")
  → ExtractSchema(scope=".posting", schema: {title, location, team, url})
  → Filter(where="location CONTAINS 'San Francisco' AND title CONTAINS 'Engineer'")
  → ListDiff(key="url", mode="additions_only")
  → Condition(added.length > 0)
  → Notify(email)
  → Output
```

## OpenAI Jobs — Hiring Trends
**Runtime LLM: 1**

```
User: "Track all OpenAI job postings and summarize what teams are hiring"

Input(url="jobs.lever.co/openai", checkInterval="24h")
  → Navigate → Wait → ExtractSchema(all postings)
  → ListDiff(key="url", detect="all_changes")
  → Condition(added.length > 0 OR removed.length > 0)
  → Aggregate(groupBy="team")
  → LlmEvaluate("Summarize hiring trends")
  → Notify(email, weekly digest)
  → Output
```

## Slashdot — Multi-Page Topic Search
**Runtime LLM: 0**

```
User: "Find new articles about Microsoft on Slashdot, checking last 5 pages"

Input(url="news.slashdot.org", checkInterval="6h")
  → Navigate → Wait(selector="article.fhitem")
  → ExtractSchema({title, url, dept, date, comments, tags, bodyPreview})
  → Paginate(strategy="url_parameter", param="page", maxPages=5, delay=2s)
  → Filter(where="title CONTAINS 'Microsoft' OR tags CONTAINS 'microsoft'")
  → ListDiff(key="url", mode="additions_only")
  → Condition(added.length > 0)
  → Aggregate(summarize={count, titles, topCommented})
  → Notify(discord)
  → Output
```

**Key insight:** Paginate block collects across 5 pages into single ListDiff. 0 LLM needed for keyword matching.

## Reddit r/technology — SPA with Upvote Threshold
**Runtime LLM: 1**

```
User: "Watch r/technology for cybersecurity posts over 1000 upvotes"

Input(url="reddit.com/r/technology", checkInterval="2h")
  → Navigate(waitUntil="networkidle")
  → Wait(selector="shreddit-post", timeout=15s)
  → Scroll(times=3, delay=2s)
  → ExtractSchema(scope="shreddit-post", schema: {title, url, score, commentCount, flair})
  → Filter(where="score >= 1000")
  → ListDiff(key="url", mode="additions_only")
  → Condition(added.length > 0)
  → LlmEvaluate("Which are about cybersecurity?")
  → Filter(relevant == true)
  → Notify(email)
  → Output
```

**Risks:** Reddit's `shreddit-post` is a Web Component. Score changes rapidly. Infinite scroll = variable post count.

## EUR-Lex — New AI Regulations
**Runtime LLM: 1-2**

```
User: "Watch for new EU regulations about artificial intelligence"

Input(url="eur-lex.europa.eu/search.html?text='artificial intelligence'", checkInterval="24h")
  → Navigate → Click(dismiss cookie overlay, optional)
  → Wait(selector=".SearchResult")
  → ExtractSchema({title, celexId, status, ref})
  → Paginate(param="page", maxPages=3, delay=2s)
  → LlmExtract("Classify AI relevance: direct/adjacent/tangential")
  → Filter(relevance IN ["direct", "adjacent"])
  → ListDiff(key="celexId", mode="additions_only")
  → Condition(added.length > 0)
  → Enrich(forEach → Navigate(document page) → ExtractSchema(metadata))
  → LlmEvaluate("Summarize for compliance officer")
  → Throttle(max=3/day)
  → Notify(email)
  → Output
```

## EUR-Lex — DSA Amendment Tracking
**Runtime LLM: 0**

```
User: "Track changes to the Digital Services Act — alert on amendments"

Input(url="eur-lex.europa.eu/.../CELEX:32022R2065", checkInterval="24h")
  → Navigate → Wait(selector="dl.NMetadata")
  → ExtractSchema(scope="#relatedDocsTb tr", schema: {relationship, docRef, docUrl, type})
  → Filter(where="relationship CONTAINS 'Corrected by' OR 'Amended by'")
  → ListDiff(key="docRef", mode="additions_only")
  → Condition(added.length > 0)
  → Enrich(forEach → Navigate(docUrl) → ExtractSchema(date, author, summary))
  → Notify(email)
  → Output
```

**Key insight:** 100% programmatic. EUR-Lex metadata is machine-readable.
