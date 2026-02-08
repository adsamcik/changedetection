# Architectural Considerations

Issues identified through adversarial review. These need to be addressed during implementation.

## Critical (Resolve Before Building)

### 1. Serialization & Versioning

**Problem:** Polymorphic blocks stored in LiteDB's BSON will silently lose data on schema changes (property renames, new block types).

**Solution:** Store pipeline definitions as versioned JSON strings with explicit `schemaVersion` field and `type` discriminators per block. Write a `PipelineDefinitionMigrator` that chains version-to-version transforms. Store as a JSON string on `WatchedSite`, not nested BSON.

### 2. Type Safety Between Blocks

**Problem:** Dynamic pipeline in a static language. `LlmExtract` returns string `"29.99"`, `Condition` expects decimal `29.99` — silent mismatch.

**Solution:** Define a `PortType` enum (HtmlContent, PlainText, ExtractedObjects, BooleanSignal, NumericValue, DiffResult, Notification). Validate all connections are port-type compatible at setup time. For `ExtractedObjects`, embed the schema shape in the port so `Condition` can validate it references valid fields.

### 3. Setup Validation ("Pipeline Compiler")

**Problem:** LLM generates bad pipelines — missing Navigate, wrong field names, orphan blocks. No one checks before the pipeline runs unattended.

**Solution:** `PipelineValidator` with structural rules (hard fail) and semantic rules (warnings). On failure, feed errors back to setup LLM and let it regenerate. User sees human-readable summary for final confirmation.

**Structural rules (hard fail):**
- Must have at least one Navigate block
- All connections are port-type compatible
- No orphan blocks (every block reachable)
- No cycles (DAG only)
- Condition field names exist in upstream ExtractSchema output

**Semantic rules (warnings):**
- LLM blocks present → show cost estimate
- Check interval < 5 min with LLM blocks → warn about cost
- Schema field extracted but never referenced downstream → warn about wasted tokens

## High Severity

### 4. Error Propagation

**Problem:** Block 3 of 6 fails — retry? skip? abort? Depends on block type.

**Solution:** Criticality tiers per block TYPE (not per instance):

| Tier | Blocks | On Failure |
|------|--------|------------|
| Infrastructure | Navigate, Wait, Click | Abort run, schedule retry |
| Extraction | Filter, ExtractSchema, LlmExtract | Retry 2x with backoff, then abort |
| Analysis | LlmEvaluate, Compare, Condition | Skip with degraded result, flag in output |
| Delivery | Notify | Outbox/retry (already solved in current codebase) |

### 5. State Management

**Problem:** Compare needs "previous" state per block instance, not per watch. A pipeline could have multiple Compare blocks.

**Solution:** Each block instance gets a stable `BlockInstanceId` (GUID, assigned at setup). Block state stored in a separate collection keyed by `(WatchId, BlockInstanceId)`. First run: no state exists → block knows it's first run (baseline capture).

### 6. LLM Cost Predictability

**Problem:** Users don't know what runtime LLM blocks cost. Self-hosted with Ollama = GPU time; API keys = real money.

**Solution:** At setup time, compute estimated cost per run based on block types and model selection. Show in confirmation step. Add per-watch monthly LLM budget with graceful degradation (skip LLM blocks, fall back to hash-only, flag as degraded).

### 7. Migration from Current Architecture

**Problem:** 6 hardcoded stages in WatchSetupPipeline → composable blocks, without breaking working watches.

**Solution:** Lazy `LegacyPipelineMigrator` — deterministic mapping from flat WatchedSite properties to block graph. Run on first execution if `PipelineDefinitionJson == null`. Feature flag to keep old code path alive for 2 release cycles.

### 8. Debugging

**Problem:** Non-technical user sees "block 4 failed" — meaningless.

**Solution:** Traffic-light timeline in UI with per-block status. On failure, generate a `UserFriendlyError` via LLM: "We tried to find the price on the page but the section we were looking for no longer exists. The website may have changed its layout."

### 9. Concurrency (Pre-Existing Bug)

**Problem:** Two check runs for the same watch overlap → duplicate ChangeEvents.

**Solution:** Optimistic concurrency — compare-and-swap on content hash when storing snapshot. If swap fails, discard stale run results.

## Known Systemic Issues

### Extraction Failure Masquerades as Content Change
If LlmExtract returns garbage or ExtractSchema returns empty (site changed), Compare sees "everything was removed" and triggers a false notification. Need a distinction between "content changed" and "extraction failed" — extraction errors should NOT flow into the diff pipeline.

### First-Run Problem
HashCompare/ListDiff on the first check returns everything as "new" since there's no previous state. The first run should always be a silent baseline capture — no notifications fired.

### Sliding Window Lists
Sites like HN show 30 items. Items scroll off page 1 between checks. ListDiff sees items as "removed" when they just moved to page 2. Need: identity-based tracking with TTL, not just current-vs-previous comparison.
