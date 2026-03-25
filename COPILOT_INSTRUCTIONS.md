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

5. **Search infrastructure is always available.** `CopilotSearchProvider` can use CopilotSDK's
   native grounded `web_search` tool when Copilot is configured. Dedicated search engines
   (SearXNG, Brave, Google) still provide valuable breadth and redundancy, but the system
   no longer depends on LLM-invented URL suggestions as its always-on fallback.

### Search Provider Hierarchy:
```
MultiProviderSearchService runs ALL available providers in parallel:
  ├── CopilotSearchProvider ← grounded CopilotSDK native web_search
  ├── SearXNG              (if configured — best: free, self-hosted, no API key)
  ├── BraveSearchProvider  (if API key configured)
  ├── GoogleCseProvider    (if API key configured)
  └── NewsDataProvider    (if API key configured — news only)

Results merged + deduped across all providers.
Fresh install with Copilot configured → CopilotSearchProvider provides grounded discovery.
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
2. Web search results      — Copilot native `web_search` / SearXNG / Brave / Google
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
- ✅ Copilot native `web_search` returns grounded results that the app can further validate/filter
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
- **Unknown sites**: Fallback chain: template → LLM composable builder → generic scraper.
  Pipeline is NEVER null — every portal gets at least a basic link-extraction pipeline.
- **Pipeline execution**: 8/8 real API sources validated (Workday POST, Platsbanken GET,
  HTML sites, Playwright JS sites)
- **All block types verified**: HttpRequest, JsonExtract, DataFilter, RelevanceScore,
  ForEachRequest, Iterate, ListDiff, ExtractSchema, Navigate+Playwright
- **CopilotSDK native search**: Discovery uses Copilot's built-in `web_search` tool for
  grounded results — works for any domain/country without external search engines
- **Live Copenhagen retest**: "biology jobs in Copenhagen" created **14 watches** end-to-end
  after catalog expansion and CopilotSDK search/tool fixes

### What Needs LLM (CopilotSDK):
- **Unknown sites**: Pages without a pre-built template need LLM to analyze structure
  and generate extraction pipeline
- **Adversarial testing**: QC mutation tests during setup
- **LlmExtract block**: Fallback extraction when CSS selectors fail

### Known Limitations:
- CopilotSDK search requires GitHub Copilot subscription (or BYOK API key)
- LLM provider must be healthy for non-template sites
- Profile scoring defaults are generic — user should customize keywords after creation
- Detail page fetching (ForEachRequest) not included in default templates — available as
  a block the LLM can add
- Single-instance only (WatchExecutionLock is in-memory ConcurrentDictionary)
- **Berlin step-1 SignalR hang is resolved** — the `Channel<T>` producer/consumer split
  removed the discovery-stream deadlock, so Berlin now advances into the search/analysis
  phase instead of stalling before search starts.
- **Remaining live issue**: non-catalog Berlin queries can still feel slow during
  grounded search + analysis; this is now a latency problem in the search phase, not a
  SignalR streaming deadlock.

### Infrastructure Requirements:
```
Required:
  - CopilotSDK (LLM)     — pipeline building, content classification, intent parsing
  - Playwright/Chromium   — JS-rendered sites (Teamtailor, Workable, SPAs)

Search (at least one):
  - CopilotSDK native     — now first-class and grounded (uses built-in web_search)
  - SearXNG (recommended) — still excellent for self-hosted breadth: docker run -p 8080:8080 searxng/searxng
  - OR Brave Search API   — requires BraveApiKey in SearchSettings
  - OR Google CSE          — requires GoogleCseApiKey + GoogleCseEngineId

Optional:
  - NewsData API          — for news/RSS monitoring (separate use case)
```

### Stress Test Results / Current Score Baseline:
- **34 issues fixed** across 7 phases (security, data integrity, core logic, reliability, UI, tooling, Phase 7)
- **Opus 4.6 re-score**: 6/10 → 7.5/10
- **GPT 5.4 re-score**: ~3.5/10 → **7.8/10**
- **Remaining blocker**: non-catalog discovery is still slower than desired in the search
  phase, even though the step-1 streaming hang is fixed
- Key fixes: ThreadSafeLiteDbContext (DB corruption prevention), pipeline fallback chain
  (unknown sites now get pipelines), CopilotSDK native search (real web search, not hallucination),
  SkipInitialCheck inversion, wildcard domain bypass, prompt injection escape
- **Current test-suite snapshot**: **2175 / 2270 passing**
- **Current live-test snapshot**:
  - Copenhagen: **14 watches created**
  - Berlin: discovery now reaches **search/analysis** reliably; remaining issue is slow
    non-catalog queries during the search phase
- **Final GPT score**: **7.8/10**

### Lessons Learned (Stress Test):
1. **Don't shadow SDK built-in tools** — registering a custom `web_search` tool blocked Copilot's native search and routed to LLM hallucination instead
2. **Measure product output, not plumbing** — "14 portals discovered" means nothing if 0 jobs are extracted. Metric should be "clickable job listings visible to user"
3. **Fix product gaps before security** — security fixes don't help if the product doesn't work. Prioritize: search quality → pipeline quality → extraction quality → then harden
4. **LiteDB Shared mode is NOT thread-safe for concurrent writers** — always wrap with SemaphoreSlim or use Exclusive mode
5. **SemaphoreSlim.Wait() deadlocks in async ASP.NET** — always use WaitAsync() in async contexts
6. **BoundedChannel with DropOldest silently loses data** — use Wait mode for critical persistence
7. **Both stress-test models find different things** — GPT found SSRF/recursion/fail-open; Opus found DB corruption/wildcard bypass/double-init. Always run both.

### Lessons Learned (Mar 23, 2026):
1. **CopilotSDK tool restriction must be explicit** — `CopilotChatCompletionService` is a pure text-completion path, so it should prefer `AvailableTools = []` when the SDK honors an empty allow-list. If a future SDK ignores that, fall back to an explicit `ExcludedTools` blacklist and document that new SDK tools could still leak through. `CopilotSearchProvider` is the exception: it uses `AvailableTools = ["web_search"]` to allow search and nothing else.
2. **CopilotSDK event handling must include reasoning + tool events** — handle `AssistantReasoningEvent`, `ToolExecutionStartEvent`, and `ToolExecutionCompleteEvent` alongside `AssistantMessageEvent` and `SessionIdleEvent`. Unknown future SDK events should be logged at **Trace** level, not Debug.
3. **SignalR async iterator + singleton semaphore can deadlock** — streaming `IAsyncEnumerable` directly from a hub means the iterator runs on the SignalR connection pipeline. If that iterator awaits the singleton `ThreadSafeLiteDbContext` semaphore while a background service holds it, the stream can stall indefinitely. The correct fix is to decouple producer and transport with `Channel<T>`.
4. **`#blazor-error-ui` is a red herring** — the DOM always contains the "An unhandled error has occurred" element. It only matters if it becomes visible. Check visibility / `style` before treating it as a real crash during Playwright debugging.
5. **Blazor prerender + SignalR causes double initialization** — `InteractiveAuto` with prerender enabled triggers `OnAfterRenderAsync(firstRender: true)` during prerender and again after WASM hydration. Pages that open SignalR hub streams should use `@rendermode @(new InteractiveAutoRenderMode(prerender: false))`.
6. **ThreadSafeLiteDbContext is a global bottleneck** — `SemaphoreSlim(1,1)` serializes all LiteDB operations across discovery, background services, hubs, and API endpoints. It prevents corruption but hurts read-heavy concurrency. Future options include `ReaderWriterLockSlim`, narrower critical sections, or timeout-based acquisition.
7. **Tool-allowlist behavior needs re-validation on SDK upgrades** — if `AvailableTools = []` stops behaving like "disable everything," the fallback is an explicit `ExcludedTools` blacklist. Re-check this anytime the CopilotSDK version changes because new built-in tools could otherwise slip through.
8. **SafeStream error propagation in C# async iterators is awkward but important** — `yield return` cannot appear inside a `catch`. Capture the error payload in a variable, exit the `catch`, yield once, then stop via a sentinel check.
9. **Hosted services make tests hang** — `WebApplicationFactory<Program>` boots all `IHostedService` registrations unless explicitly removed. Tests should call `services.RemoveAll<IHostedService>()` to avoid Playwright download/setup work, background checkers, provider sync, and Ollama seeding from blocking completion.
10. **TUnit filter syntax is easy to get wrong** — the test tree is four levels deep, so the reliable form is `--treenode-filter "/*/*/*/*TestMethodName*"`. Use `--list-tests` first when the filter does not match.
11. **Catalog expansion matters as much as LLM quality** — the static portal catalog remains the main discovery source for known regions. Missing German / Dutch / Austrian / Swiss portals produced zero results regardless of prompt quality. Expand the catalog proactively; don't expect LLM search alone to fill regional gaps.
12. **Pipeline port typing is now stricter** — after tightening compatibility rules, previously tolerated template pipelines can fail validation. Examples: `DiffResult → ExtractedObjects` and `PlainText → ExtractedObjects` are no longer accepted. Template graphs must use the correct port types explicitly.
