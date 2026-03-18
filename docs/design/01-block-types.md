# Pipeline Block Types

Every pipeline starts with an **Input** node and ends with an **Output** node.

## Category 0: Pipeline Boundary

| Block | Config | Notes |
|-------|--------|-------|
| **Input** | `{ url, checkInterval, metadata }` | **Start node.** Every pipeline begins here. Carries the target URL, scheduling config, and any user-provided context. Single entry point. |
| **Output** | `{ schema, displayConfig }` | **End node.** Every pipeline ends here. Has access to ALL upstream node outputs. Combines into final result schema with display hints for UI. |

## Category 1: Content Acquisition

| Block | Config | Notes |
|-------|--------|-------|
| **Navigate** | `{ url, useJavaScript, timeout }` | Goes to URL, optional JS rendering |
| **Click** | `{ selector, waitAfter }` | Click element on current page |
| **Wait** | `{ forSelector \| forTime \| forNetworkIdle }` | Wait for condition |
| **Scroll** | `{ direction, times }` | Load lazy content via scrolling |
| **Paginate** | `{ nextSelector \| urlParam, maxPages, delay, aggregateResults }` | Navigate through multiple pages, combine results |

## Category 2: Data Extraction

| Block | Config | Notes |
|-------|--------|-------|
| **Filter** | `{ css \| xpath \| regex }` | Narrow HTML scope. Reduces what downstream blocks see. |
| **ExtractSchema** | `{ schema: [{field, selector, type}] }` | Programmatic extraction of known structure. Fast, no LLM. |
| **LlmExtract** | `{ prompt, outputSchema }` | LLM parses unstructured content into schema. For messy/unstructured HTML. |

> Design note: LlmExtract should aim for smallest scope that has everything. Typical chain: Filter → ExtractSchema (structured fields) → pass to LlmExtract if needed. Schemas must be serializable.

## Category 3: Comparison & Analysis

| Block | Config | Notes |
|-------|--------|-------|
| **HashCompare** | `{ scope: full \| filtered }` | Cheapest. SHA256 hash equality. Just "changed or not?" |
| **TextDiff** | `{}` | Line-by-line diff (DiffPlex). Shows added/removed/modified lines. |
| **StructDiff** | `{ identityFields }` | Object-level diff on extracted schemas. Per-field granularity. |
| **ListDiff** | `{ identityKey, mode }` | Detects new/removed/reordered items in collections. |
| **RelevanceScore** | `{ targetFields, positiveKeywords, negativeKeywords, minScore }` | Zero-LLM weighted keyword scoring. Filters noisy feeds instantly. Use before LlmEvaluate to reduce token cost. |
| **NumericDelta** | `{ field }` | Absolute change, % change, trend direction, new min/max. |
| **LlmEvaluate** | `{ prompt, outputSchema }` | LLM judges diff or content. Always outputs structured schema. |
| **LlmCraftPrompt** | `{ instructions }` | Meta-prompting: generates prompt for downstream LLM block. |

## Category 4: Decision & Output

| Block | Config | Notes |
|-------|--------|-------|
| **Condition** | `{ field, operator, value }` | Gate. Downstream blocks only run if condition passes. Operators: equals, contains, regex, >, <, between, changedByPercent, isNewMinimum, etc. |
| **Transform** | `{ rename, drop, compute }` | Reshape data. Extensible later to accept multiple inputs (e.g., currency rate provider). |
| **Notify** | `{ channel, template, includeFields }` | Send alert. Email, webhook, discord. |

## Category 5: Advanced

| Block | Config | Notes |
|-------|--------|-------|
| **Route** | `{ conditions: [{if, then_branch}] }` | Conditional branching. Different execution paths based on data. |
| **Enrich** | `{ urlField, extractSchema }` | Follow dynamic URL from extracted data to get more context. Sub-navigate + extract. |
| **LinkValidate** | `{ urlFields, language?, followRedirects? }` | Follows URLs from extracted data, checks liveness. Detects dead links, 404s, death signals in 6+ languages. |
| **LookupHistory** | `{ field, period }` | Query past values for trend analysis. |
| **Aggregate** | `{ groupBy, summarize }` | Combine batch results into summary. For digest-style notifications. |
| **Throttle** | `{ maxFrequency, cooldown }` | Rate limit notifications. |

## Deferred (Not in v1)

| Block | Reason |
|-------|--------|
| ~~ForEach / Merge~~ | Fan-out/fan-in adds DAG complexity — deferred |
| ~~Log~~ | Block I/O is automatically persisted anyway |
| ~~Retry~~ | Built into the executor, not a user-facing block |
| ~~Authenticate~~ | Credential security too complex for v1 |

## Key Design Decisions

1. **Block I/O persistence**: Every block's inputs and outputs are automatically stored. Any block can access its own history (enables Compare blocks without explicit StoreSnapshot).
2. **Setup specialist prompts**: Each block type has its own specialist LLM prompt during setup. Not one monolithic "assemble everything" call — chain of focused calls per block.
3. **Model complexity slider**: More focused calls for small models (7B), fewer larger calls for capable models (Haiku 4.5).
4. **Output node**: Final terminal node that can query any upstream node's output and combine into displayable result. LLM generates the display config during setup.
5. **Transform extensibility**: Future: plug multiple block inputs into Transform (e.g., currency rate provider as a second input).
