# Implementation Roadmap — Composable Pipeline System

Clean break from current hardcoded pipeline. Existing watches can be recreated.

## Phase 1: Foundation — Block System Core

The type system, executor, and core blocks. After this phase, pipelines can be defined in JSON and executed programmatically.

### 1.1 Block Interface & Type System
- Define `IPipelineBlock` interface with `InputPorts`, `OutputPorts`, `ExecuteAsync`
- Define `PortType` enum: `HtmlContent`, `PlainText`, `ExtractedObjects`, `BooleanSignal`, `NumericValue`, `DiffResult`, `Notification`
- Define `BlockContext` (carries page instance, state access, cancellation)
- Define `BlockResult` (success/failure/skip + output data)
- Define `PortDescriptor` (name, type, required flag)

### 1.2 Pipeline Definition Schema
- Define `PipelineDefinition` record: `SchemaVersion`, `Blocks[]`, `Connections[]`
- Define `BlockDefinition` record: `Id`, `Type` (discriminator), `Config` (JSON)
- JSON serialization with `schemaVersion` field and type discriminators
- Store as JSON string on `WatchedSite.PipelineDefinitionJson`

### 1.3 Pipeline Validator
- Structural rules: must have Input + Output, no orphans, no cycles, port-type compatibility
- Condition field validation: referenced fields must exist in upstream ExtractSchema output
- Semantic warnings: unused extracted fields, LLM blocks without cost estimate
- Return `ValidationResult` with errors (hard fail) and warnings

### 1.4 Pipeline Executor
- Reads `PipelineDefinition`, topologically sorts blocks
- Creates `BlockContext` with shared Playwright page and state access
- Executes blocks in order, passing outputs to connected inputs via port matching
- Handles error propagation via criticality tiers (Infrastructure → abort, Extraction → retry, Analysis → skip, Delivery → outbox)
- Logs per-block execution: input summary, output summary, duration, status
- Optimistic concurrency on content hash to prevent duplicate runs

### 1.5 Core Blocks — Content Acquisition
- `InputBlock` — reads URL and metadata from pipeline config
- `NavigateBlock` — Playwright page navigation with JS rendering option
- `WaitBlock` — wait for selector / time / network idle
- `ClickBlock` — click element on page
- `ScrollBlock` — scroll to load lazy content

### 1.6 Core Blocks — Data Extraction
- `FilterBlock` — CSS/XPath/regex selector to narrow HTML scope
- `ExtractSchemaBlock` — programmatic field extraction from known selectors

### 1.7 Core Blocks — Comparison
- `HashCompareBlock` — SHA256 content hash comparison
- `ListDiffBlock` — identity-key-based new/removed/modified item detection
- `StructDiffBlock` — per-field object diff
- `NumericDeltaBlock` — absolute/percent change + trend

### 1.8 Core Blocks — Decision & Output
- `ConditionBlock` — gate with operators (equals, contains, regex, >, <, between, changedByPercent, isNewMinimum)
- `NotifyBlock` — email/webhook/discord via existing notification infrastructure
- `OutputBlock` — terminal node, aggregates all upstream outputs into display schema

### 1.9 Block I/O Persistence
- Every block's inputs and outputs automatically stored per execution
- Keyed by `(WatchId, BlockInstanceId, RunTimestamp)`
- Compare blocks read their own previous output for diffing
- First run: no previous state → baseline capture, no notifications

### 1.10 Integration with Background Service
- Replace `ChangeCheckBackgroundService` internals to use `PipelineExecutor`
- Keep scheduling logic (adaptive/fixed intervals, semaphore concurrency)
- `WatchedSite` now references a `PipelineDefinition` instead of flat selector fields

---

## Phase 2: LLM Blocks & Advanced Features

Adds LLM-powered blocks and advanced pipeline features.

### 2.1 LLM Blocks
- `LlmExtractBlock` — unstructured content → schema via LLM, always outputs structured JSON
- `LlmEvaluateBlock` — LLM judges diff/content, outputs structured verdict
- `LlmCraftPromptBlock` — meta-prompting, generates prompt for downstream LLM block

### 2.2 Advanced Blocks
- `PaginateBlock` — multi-page navigation (URL param or next-button strategy), aggregates results
- `RouteBlock` — conditional branching, splits pipeline into parallel lanes
- `EnrichBlock` — follow dynamic URL from extracted data, sub-navigate + extract
- `TransformBlock` — reshape data (rename, drop, compute fields)
- `AggregateBlock` — group by field, summarize (count, distinct, max)
- `ThrottleBlock` — rate limit notifications (max per hour/day, cooldown)
- `TextDiffBlock` — line-by-line DiffPlex diff
- `LookupHistoryBlock` — query past block outputs for trend analysis

### 2.3 Runtime LLM Cost Controls
- Per-watch monthly LLM budget (configurable)
- Cost estimate computed at setup time (LLM calls × model cost)
- Runtime check: if budget exceeded → skip LLM blocks → hash-only comparison → flag as degraded
- Cost dashboard in UI showing per-watch and total usage

---

## Phase 3: Setup Pipeline

The LLM-powered assembly process that converts natural language into block graphs.

### 3.1 Phase 1 — Intent Understanding
- LLM call: parse user's natural language → `{ url, intent, thresholds, frequency }`
- Playwright: fetch page, get HTML
- LLM call: analyze structure → `{ contentType, regions, hasPagination, needsJS }`
- Stream progress to user via SignalR

### 3.2 Checkpoint 1 — Confirm Understanding
- Present summary: "I'll watch X for Y, checking every Z hours"
- User confirms or refines

### 3.3 Phase 2 — Build Pipeline (Iterative)
- LLM call: select blocks → ordered list with reasons
- For each block: specialist LLM call to configure it
- Validate each block's config against real HTML (test selector, verify match count)
- On validation failure: retry with alternative config, ask user if stuck
- Wire blocks into `PipelineDefinition` JSON
- Stream progress per block

### 3.4 Phase 3 — Dry Run
- Execute assembled pipeline once against real page
- Stream each block's result to user in real-time
- Show sample extracted data

### 3.5 Phase 4 — QC Validation
- LLM call: "Given original intent, does pipeline output match?"
- Checks: fields relevant to intent, conditions match thresholds, nothing missing, would produce useful notifications
- If issues → loop back to Phase 2 to fix
- If still failing → present to user with known issues

### 3.6 Checkpoint 2 — Confirm Pipeline
- Show: human-readable pipeline summary
- Show: visual pipeline diagram (blocks + arrows)
- Show: dry run results + QC verdict
- User: confirm / feedback / redo

### 3.7 Save & Schedule
- Persist `PipelineDefinition` + metadata on `WatchedSite`
- Schedule first real check
- Store setup-time HTML snapshot for future auto-healing comparison

---

## Phase 4: Visualization & UX

### 4.1 Pipeline Flow Diagram
- Vertical flow: blocks as rounded rectangles with arrows
- Icons per block type, status indicators (✅/⚠️/❌/⏸️)
- Route blocks branch into parallel lanes, rejoin at Output
- Click to expand: input/output data, duration, error details

### 4.2 Watch Dashboard Cards
- Card type derived from pipeline: `price` / `list` / `content` / `multiSignal`
- Primary value, trend, status sections — all configured by Output node
- Quick-glance status: last checked, next check, health indicator

### 4.3 Per-Block Execution History
- Traffic-light timeline of last run
- Click any block: input summary, output summary, previous runs
- Failed block: human-friendly error explanation

### 4.4 Watch Creation UX
- Natural language input field
- Streaming progress (Phases 1-4 visible in real-time)
- Two-checkpoint confirmation flow
- Pipeline diagram shown at confirmation step

---

## Phase 5: Auto-Healing & Resilience

### 5.1 Layer 1 — Block Self-Heal
- Detect consecutive failures (configurable threshold, default 3)
- LLM: "Selector returned results before, now empty. Here's current HTML. Suggest new selector."
- Validate new selector against live page
- If works → update block config, reset failure count, log change

### 5.2 Layer 2 — Pipeline Diagnosis
- Compare current HTML vs setup-time HTML snapshot
- LLM: "Diagnose: redesign? bot detection? content removed?"
- If fixable → reconfigure affected blocks
- If not → escalate to user

### 5.3 Layer 3 — User Notification
- Pause watch
- Notify user: what broke, what was tried, options (Retry / Re-run setup / Delete)
- All thresholds configurable per watch

### 5.4 Resilience Improvements
- Exponential backoff with jitter (replace fixed retry delays)
- Dead letter triage: periodic re-evaluation of failed items
- Retry-After header parsing for 429 responses

---

## Phase 6: Testing & Quality

### 6.1 Block Unit Tests
- Each `IPipelineBlock` tested with fixture inputs/outputs
- Use existing TestBase + Shouldly infrastructure

### 6.2 Golden Pipeline Tests
- 10-15 canonical pipeline compositions as JSON fixtures
- Test executor against these known-good pipelines
- Price tracking, list monitoring, topic filtering, multi-page, multi-signal

### 6.3 Setup Quality Regression Tests
- Cache entire setup sessions (input → pipeline definition) in SQLite
- Assert generated pipelines match reference structures
- Snapshot tests for LLM judgment quality

### 6.4 Endpoint & Hub Tests
- Cover the 8 untested API endpoint files
- Cover the 2 untested SignalR hub files
- Integration tests for new pipeline-related endpoints
