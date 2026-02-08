# Session Learnings — Composable Pipeline Architecture Design

*Session: 2026-02-07*

---

## Product & Architecture Learnings

### 1. Two-Pipeline Mental Model Is Essential

The single most important insight from this session: there are **two fundamentally different pipelines** with different characteristics:

| | Setup Pipeline | Runtime Pipeline |
|---|---|---|
| Runs | Once (during watch creation) | On schedule (every N minutes) |
| LLM usage | Heavy (6-10 calls for 7B models) | Sparse (0 in 7/12 tested scenarios) |
| Goal | Understand intent → assemble block graph | Execute block graph → detect changes |
| Error handling | Iterate with user | Self-heal or escalate |
| Latency tolerance | Seconds (interactive) | Minutes (background) |

Conflating these two leads to architectural confusion. Every design decision should ask: "Is this for setup or runtime?"

### 2. LLM Calls Must Be Decomposed for Small Models

7B models can't handle complex multi-step reasoning in a single prompt. The setup pipeline must decompose into focused calls:
- Intent parsing (what does the user want?)
- Content analysis (what's on this page?)
- Block selection (which blocks solve this?)
- Per-block configuration (CSS selectors, thresholds, etc.)
- QC validation (does the output match the intent?)

Each call has a narrow, well-defined task. This is more reliable on small models AND more debuggable on large models.

### 3. Most Runtime Pipelines Don't Need LLM

Testing against 7 real websites with 12 pipeline scenarios showed that **7 out of 12 need zero runtime LLM calls**. LLM is only needed for:
- Topic relevance filtering ("is this article about Kubernetes?")
- Unstructured content extraction where no CSS selector exists
- Semantic change evaluation ("is this price drop significant?")
- Trend analysis across historical data

This means runtime costs are lower than feared. The architecture should optimize for the common case (pure programmatic pipelines) while supporting LLM blocks when needed.

### 4. First-Run and Sliding-Window Are Non-Obvious Critical Problems

Two issues that didn't surface until adversarial stress-testing:

**First-run problem:** When a ListDiff block runs for the first time, every item appears as "new." If it notifies on all of them, the user gets a flood of false positives. Solution: first run is always a silent baseline capture.

**Sliding-window problem:** Sites like HN and Reddit rotate items off the front page. Without identity-based tracking (not position-based), items that scroll off appear as "removed" and then "re-added" when they come back. Solution: identity-based tracking with TTL — items aren't marked "removed" until they've been absent for N consecutive checks.

### 5. Extraction Failure ≠ Content Change

If a CSS selector breaks (site redesigned), the extraction returns empty/null. If this flows into a diff block, it looks like "everything was removed" — a false positive change. The architecture must distinguish between:
- **Extraction error** → block reports error, pipeline enters healing flow
- **Genuine empty content** → content changed, diff is legitimate

This requires error typing at the block output level, not just success/failure booleans.

### 6. Validator Is the Moat

The pipeline validator (which checks structural correctness before a pipeline runs) prevents an entire class of runtime errors:
- Type mismatches between block ports
- Missing required blocks (no Input, no Output)
- Cycles in the block graph
- Unknown block types
- Missing required configuration fields

Build the validator before building complex blocks. Every block you add without validation is a bug waiting to happen.

### 7. Pre-Made Architectural Decisions Prevent Agent Thrashing

During the orchestrator prompt design, the adversarial review identified 6 decision points where an AI agent would waste significant time deliberating. Pre-deciding these and documenting the rationale eliminates that thrashing:
- Block output representation → `JsonElement` in typed `BlockResult`
- Pipeline storage → JSON string on `WatchedSite`, not nested BSON
- Block registration → static dictionary, no reflection
- Playwright lifecycle → one `IPage` per pipeline run
- Block I/O storage → LiteDB collection with compound key
- Execution model → sequential in v1

---

## Process & Methodology Learnings

### 8. Adversarial Review Catches Systemic Issues Brainstorming Misses

Brainstorming generated 10 ideas. Adversarial review found 3 **critical** issues (serialization brittleness, type safety gaps, setup validation) and 6 **high** severity issues — none of which were in the original 10 ideas. The brainstorm found *features to build*; the adversary found *ways the features would break*.

Best used together: brainstorm first (generate), then adversary (validate).

### 9. Real-World Testing Against Live Websites Is Irreplaceable

Testing the block model against 7 real websites revealed:
- Which blocks are actually essential vs. theoretically nice
- That most pipelines are simpler than expected (3-6 blocks)
- That SPA rendering (Wait, Click, Scroll) blocks are critical — many modern sites don't work without them
- Specific configuration patterns that recur across sites

Paper design alone would have over-engineered some blocks and under-specified others.

### 10. Interactive Q&A Surfaces the Real Product Vision

The user's original request was "brainstorm pipeline improvements." Through interactive Q&A, the actual goal emerged: a complete architectural redesign around composable blocks with two distinct pipelines. This was much bigger than "improvements" — it was a new product vision.

Key technique: Go through each idea one by one and ask "Do you want this? Why/why not?" The pattern of yes/no answers reveals the underlying vision better than asking "What do you want?" directly.

### 11. Interface-Lock Protocol Prevents Cascading Mismatches

When multiple subagents independently implement pieces of a system, the #1 failure mode is interface drift — each agent interprets the interface slightly differently. Solution:
1. First work item defines and locks the core interfaces
2. Exact interface code is pasted into every subsequent subagent prompt
3. Any interface change requires stop → review → update → re-test all downstream

This is more verbose but prevents a cascade of incompatible implementations that only surface at integration time.

### 12. Explore Gates Before Shared Files Prevent Collisions

Before modifying files that other code depends on (e.g., `WatchedSite.cs`, `Program.cs`, `ChangeCheckBackgroundService.cs`), the orchestrator must run an explore agent to read the current state. Without this:
- New properties might collide with existing ones
- DI registrations might duplicate or conflict
- The modification might break assumptions made by other code

Six specific explore gates were identified for this project. Each is a mandatory checkpoint.

### 13. The Prompt Is a Contract, Not Just Instructions

The orchestrator prompt evolved from "instructions" to a "contract" — it doesn't just say what to do, it constrains what NOT to do, pre-makes decisions to eliminate ambiguity, and defines verification checkpoints. Key contract elements:
- Target file layout (exactly where new code goes)
- Files that must NOT be modified (old pipeline)
- Names that must NOT collide (existing entities)
- Decisions that must NOT be reconsidered
- Quality gates that must NOT be skipped

### 14. Save Design Artifacts Incrementally

The session produced 8 design documents (00-07 + orchestrator prompt). Saving them incrementally (after each major design milestone) was important because:
- Context compaction can lose details; files persist
- The user can review and edit between sessions
- Design docs serve as the source of truth for implementation
- Separate files per concern enable focused reading (not one mega-doc)

---

## What Would I Do Differently

1. **Start with adversarial review earlier** — we did 5+ rounds of brainstorming before the first adversary pass. Running a quick adversarial check after the initial block type design would have caught the serialization and type safety issues sooner.

2. **Test against real websites during block design, not after** — we designed all 22 block types theoretically, then tested against sites. Some blocks could have been validated or eliminated earlier.

3. **Pre-decide more architectural choices in the design docs** — several decisions (output representation, registration strategy, Playwright lifecycle) were only resolved during orchestrator prompt writing. They should have been in `05-architectural-considerations.md`.

4. **Write the orchestrator prompt draft before the adversarial review** — instead of reviewing the prompt after writing it, writing a first draft and having the adversary review before refining would save an editing pass.
