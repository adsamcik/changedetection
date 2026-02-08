# Product Vision

## Goal
Product for others to use (open source or commercial).

## Target Users
Both non-technical beginners and developer/power users.

## Deployment Model
Self-hosted (Docker/binary). Users bring their own LLM API keys or run local models (Ollama).

## Core Product Thesis
> Users describe in natural language what they want to watch. The system does the rest.

The **setup pipeline** interprets the user's request and assembles a **runtime watch pipeline** from composable building blocks. The runtime pipeline executes on schedule without user intervention.

### Two Distinct Pipelines

**Setup Pipeline** (runs once, LLM-heavy):
- Interprets natural language → decomposes into focused sub-problems
- Multiple small LLM calls → assembles runtime pipeline from blocks
- Configures metadata: check frequency, URL, notifications, model selection

**Runtime Pipeline** (runs on schedule, programmatic + LLM where needed):
- Executes blocks in sequence
- LLM used only when programmatic extraction can't work (unstructured content, topic relevance, semantic validation)
- Most simple watches (price checks, new items in lists) use 0 runtime LLM calls

### Design Constraints
- **Small model friendly**: Must work on 7B models. Decompose problems into focused LLM calls, not one mega-prompt.
- **Model complexity slider**: Adjust decomposition granularity based on model capability (7B = many small calls, Haiku 4.5 = fewer larger calls).
- **Schema-first output**: LLM always outputs structured schema so downstream steps can process programmatically.
- **Meta-prompting**: Runtime steps can dynamically craft prompts for downstream LLM steps.

### UX Principles
- Users never edit pipeline blocks directly (for now)
- Pipelines should be **visualised** so users understand what's happening
- Range from simple (price check = programmatic only) to complex (article feed = LLM at runtime)
- Every pipeline starts with an **Input** node and ends with an **Output** node

## Decision Log

| # | Idea | Decision | Notes |
|---|------|----------|-------|
| 1 | Dynamic Pipeline Composition | ✅ Want | Fixed pipeline is the #1 architectural bottleneck |
| 2 | Prompt Registry with Versioning | ❌ Skip | Users won't touch prompts. Prompts in code is fine. |
| 3 | Multi-Page Watch Flows | ✅ Want | Single-URL is a real limitation |
| 4 | LLM Cost Budgets & Quotas | ✅ Want | Even self-hosted users with own API keys need guardrails |
| 5 | Visual Diff Engine | 🟡 Later | Cool but not core to the product vision |
| 6 | Structured Content Handlers (API/RSS/PDF) | 🟡 Later | Most users monitor web pages; API/RSS is power-user territory |
| 7 | Exponential Backoff with Jitter | ✅ Want | Easy win, should already be there |
| 8 | Dead Letter Triage & Auto-Recovery | ✅ Want | Failed pipelines shouldn't disappear silently |
| 9 | Community Watch Templates Marketplace | 🟡 Later | Great idea but too early — core product first |
| 10 | Test Coverage Gaps (endpoints + hubs) | ✅ Want | Untested endpoints are a liability for a product |
