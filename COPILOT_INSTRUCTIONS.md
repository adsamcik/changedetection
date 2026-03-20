# ChangeDetection — Development Philosophy

## Product Vision

This is a **fully autonomous web monitoring agent**. The user describes what they want in 
natural language ("watch for biology research jobs in Copenhagen") and the system does 
everything: discovers relevant portals, builds extraction pipelines, monitors for changes, 
filters by relevance, and notifies. The user NEVER manually configures pipelines, selectors, 
or technical details.

### UX Principles:

1. **Single entry point.** The SmartInput on the dashboard is the ONLY way to create 
   watches. User types either a URL or natural language description — the system detects 
   which and routes accordingly. No separate "Add Watch" vs "Group Watch" flows.
   - URL input (e.g., `https://company.com/jobs`) → single watch setup via ComposableSetupPipeline
   - Natural language (e.g., "biology jobs in Copenhagen") → group watch discovery flow

2. **The LLM is ALWAYS the pipeline builder.** There is no manual pipeline editor as a 
   primary workflow. Users describe intent → the system builds, validates, and runs 
   everything. The LLM decides which blocks to use, what selectors to apply, and how 
   to paginate.

3. **Users never see implementation details.** They don't know about blocks, ports, 
   JSONPath, CSS selectors, or pipeline definitions. They see: "Monitoring 12 career 
   portals for research assistant jobs" — not "HttpRequest → Paginate → JsonExtract."

3. **Discovery must be grounded, not hallucinated.** When the user asks to watch a 
   topic/location, the system needs REAL URLs. These come from web search (SearXNG or 
   configured providers), existing watches (proven catalog), or the user pasting a URL. 
   The LLM never invents URLs from its training data.

4. **The catalog grows from usage.** Every successful watch becomes a verified catalog 
   entry. Over time, the system knows more portals and can suggest better matches. A 
   fresh install starts empty — the catalog bootstraps from web search + user input.

5. **Search infrastructure is always available.** The `LlmSearchProvider` implements 
   `ISearchProvider` using the LLM to suggest URLs, then validates each via HTTP HEAD — 
   hallucinated URLs silently 404 and get dropped. This is always available (only needs 
   CopilotSDK). Dedicated search engines (SearXNG, Brave, Google) provide better coverage 
   and are recommended for production, but the system works without them.

### Search Provider Hierarchy:
```
MultiProviderSearchService runs ALL available providers in parallel:
  ├── SearXNG              (if configured — best: free, self-hosted, no API key)
  ├── BraveSearchProvider  (if API key configured)
  ├── GoogleCseProvider    (if API key configured)
  ├── LlmSearchProvider   ← ALWAYS available (LLM suggests + HTTP validates)
  └── NewsDataProvider    (if API key configured — news only)

Results merged + deduped across all providers.
Fresh install with zero config → LlmSearchProvider still provides discovery.
```

## Core Principle: LLM-First, No Silent Fallbacks

The CopilotSDK (LLM) is the brain of this application. It analyzes pages, generates 
extraction pipelines, classifies content, and crafts selectors. 

### Rules:

1. **If the LLM is unavailable, the system MUST fail clearly** — not silently degrade 
   to basic "full page text monitoring." A watch without proper extraction is useless 
   for structured monitoring.

2. **The deterministic classifier is a FAST PATH, not a fallback.** When we recognize 
   a known platform (Workday URL, Teamtailor, etc.), we skip the LLM and use a pre-built 
   template. This is an optimization for speed, not a replacement for the LLM.

3. **For unknown sites, the LLM MUST be involved.** It analyzes page structure, discovers 
   repeating items, generates CSS selectors or JSONPath expressions, and builds the 
   extraction pipeline. Without the LLM, we can't do this.

4. **"Monitor entire page" is a USER CHOICE, not a system default.** If the user explicitly 
   wants basic text monitoring, they can choose it. But it should never be the automatic 
   recommendation when the LLM fails.

5. **Errors should be loud and actionable.** When something fails, tell the user exactly 
   what went wrong and how to fix it. "Check your Copilot settings" is better than 
   silently creating a useless watch.

## Core Principle: Grounded Data — LLM Never Invents URLs or Facts

The LLM reasons, classifies, selects, and composes. It NEVER generates factual data 
(URLs, company names, API endpoints) from its training knowledge — that data hallucinates.

### Rules:

1. **Every URL must come from a grounded source.** Verified catalogs (`job-watch-portals.json`, 
   `sites.json`), web search results, or user input. The LLM may ONLY select from these 
   sources — never generate URLs from imagination.

2. **The LLM selects, it does not invent.** When discovering career portals, the LLM receives 
   a numbered catalog of verified URLs and returns index numbers. It explains WHY each selection 
   matches the user's intent, but never produces a URL string itself.

3. **Web search results are grounded data.** If search provider API keys are configured, search 
   results provide additional verified URLs. The LLM may classify/filter these results, but the 
   URLs themselves come from the search engine, not the LLM.

4. **Pipeline templates use verified URL patterns.** Workday API paths, Teamtailor job endpoints, 
   and Platsbanken API URLs are derived from verified catalog entries (with exact `site_id`, 
   `instance`, `subdomain`), not from LLM guesswork.

5. **When the catalog is insufficient, ask the user.** If no catalog entries match the user's 
   intent and no search providers are configured, tell the user clearly: "I don't have verified 
   sources for [location/field]. You can paste a specific URL, or configure a search provider 
   for broader discovery."

### Grounded Data Sources (priority order):
```
1. Existing watches        — Every successful watch is a verified portal (self-growing catalog)
2. Web search results      — SearXNG (default, self-hosted) / Brave / Google (if API keys configured)
3. Static seed catalogs    — job-watch-portals.json, sites.json (bootstrap for fresh installs)
4. User-provided URLs      — Directly pasted by the user
```

### How the Catalog Grows:
```
Fresh install → static seed files only (17 + 48 entries)
     ↓
User creates first Group Watch → web search finds 8 new portals → watches created
     ↓
Those 8 watches are now in the catalog for future discoveries
     ↓
Next Group Watch query can draw from 73+ verified sources
     ↓
Failed watches get flagged → removed from catalog suggestions
```

### Anti-Patterns (NEVER do these):
- ❌ Trust LLM-generated URLs without HTTP validation
- ❌ Use LLM knowledge as authoritative (it's a suggestion source, not a fact source)
- ❌ Show unvalidated LLM URLs to the user as confirmed results
- ❌ Skip the HTTP liveness check to save time

### Acceptable Patterns:
- ✅ LLM suggests URLs → HTTP HEAD validates each → only live ones returned (LlmSearchProvider)
- ✅ LLM selects from a verified catalog by index number
- ✅ LLM classifies/filters web search results (real URLs from real search engines)
- ✅ LLM builds pipelines for validated URLs (its core strength)

### Architecture:

```
User Input (URL + intent)
    ↓
Deterministic Fast Path (known platforms → template)
    ↓ (if no match)
CopilotSDK LLM Analysis (classify → discover schema → generate pipeline)
    ↓ (if LLM unavailable)
FAIL with clear error message — DO NOT silently degrade
```

### Block Philosophy:

Blocks are UNIVERSAL primitives. The LLM composes them into site-specific pipelines.
No per-site code. The blocks handle: HTTP (GET/POST), JSON extraction, HTML extraction, 
pagination, iteration, filtering, scoring, diffing, and notification. The LLM decides 
which blocks to use and how to configure them.

## No Legacy Code Policy

1. **Never maintain parallel legacy/modern implementations.** When a modern replacement 
   exists, the legacy version must be removed or migrated — never left running alongside.

2. **All UI flows must use the latest pipeline.** The setup flow uses `ComposableSetupPipeline` 
   via `ComposableSetupHub`. The old `WatchSetupPipeline`/`SetupConversationHub` are deprecated 
   and should not be used for new features.

3. **All watch checks must use the pipeline executor.** Watches without `PipelineDefinitionJson` 
   should get an auto-generated basic pipeline, not fall back to the legacy `ServerWatchService` 
   check path.

4. **When adding features, check that only ONE code path exists.** If you find both a legacy 
   and modern implementation for the same feature, consolidate to the modern one before 
   proceeding.

5. **Dead code must be removed promptly.** Unused services, unused hub endpoints, and 
   unreachable code paths should be deleted, not commented out or left "for reference."

## Legacy Code Status

All legacy code paths have been removed. The `WatchSetupPipeline` and `SetupConversationHub` are
deprecated and marked `[Obsolete]`. ALL watches use the pipeline executor path — watches without
a `PipelineDefinitionJson` get an auto-generated basic pipeline (Input → Navigate → ExtractSchema/
HashCompare → Output) on first check, then proceed through the standard pipeline executor.

The legacy DI registrations (individual pipeline stages, `IWatchSetupPipeline`) and the commented-out
`SetupConversationHub` hub mapping have been removed from `Program.cs`.

## Current Status (Mar 2026)

### What Works End-to-End:
- **Known platforms** (Workday, Teamtailor, Platsbanken, Workable): User pastes URL →
  platform detected from URL pattern → pipeline template generated with zero LLM calls →
  dry-run verified → watch created with full pipeline
- **Pipeline execution**: 8/8 real API sources validated (Workday POST, Platsbanken GET,
  HTML sites, Playwright JS sites)
- **All block types verified**: HttpRequest, JsonExtract, DataFilter, RelevanceScore,
  ForEachRequest, Iterate, ListDiff, ExtractSchema, Navigate+Playwright

### What Needs LLM (CopilotSDK):
- **Unknown sites**: Pages without a pre-built template need LLM to analyze structure
  and generate extraction pipeline
- **Adversarial testing**: QC mutation tests during setup
- **LlmExtract block**: Fallback extraction when CSS selectors fail

### Known Limitations:
- Search provider required for Group Watch discovery (SearXNG recommended — `docker run searxng/searxng`)
- LLM provider must be healthy for non-template sites
- Profile scoring defaults are generic — user should customize keywords after creation
- Detail page fetching (ForEachRequest) not included in default templates — available as
  a block the LLM can add

### Infrastructure Requirements:
```
Required:
  - CopilotSDK (LLM)     — pipeline building, content classification, intent parsing
  - Playwright/Chromium   — JS-rendered sites (Teamtailor, Workable, SPAs)

Required for Group Watch discovery:
  - SearXNG (recommended) — self-hosted, no API key: docker run -p 8080:8080 searxng/searxng
  - OR Brave Search API   — requires BraveApiKey in SearchSettings
  - OR Google CSE          — requires GoogleCseApiKey + GoogleCseEngineId

Optional:
  - NewsData API          — for news/RSS monitoring (separate use case)
```
