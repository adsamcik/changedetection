# Setup Pipeline Assembly

## Approach: Sequential Specialist Chain with Iteration & Streaming

The setup pipeline converts natural language into a composable block graph through focused LLM calls, with user checkpoints and streaming progress.

## Flow

```
USER INPUT: Natural language description
       │
       ▼
┌─ PHASE 1: Understand ──────────────────────────────┐
│  LLM: Parse intent → {url, intent, thresholds}     │
│  Stream: "I understand you want to watch..."        │
│  Playwright: Fetch page, get HTML                    │
│  LLM: Analyze structure → {contentType, regions}    │
│  Stream: "I found a news site with articles..."     │
└─────────────────────────────────────────────────────┘
       │
       ▼
┌─ CHECKPOINT 1: Confirm Understanding ──────────────┐
│  Show: summary of what we'll monitor                │
│  User: ✅ Correct / ✏️ refine                       │
└─────────────────────────────────────────────────────┘
       │
       ▼
┌─ PHASE 2: Build Pipeline (iterative) ──────────────┐
│  LLM: Select blocks → ordered list                  │
│  FOR EACH block:                                    │
│    LLM specialist: Configure block                  │
│    Validate against real HTML                        │
│    IF fails → retry alt config → ask user if stuck  │
│  Wire blocks → pipeline definition                  │
│  Stream progress throughout                         │
└─────────────────────────────────────────────────────┘
       │
       ▼
┌─ PHASE 3: Dry Run ─────────────────────────────────┐
│  Execute pipeline once against real page             │
│  Stream each block's result live                    │
│  Show sample extracted data                          │
└─────────────────────────────────────────────────────┘
       │
       ▼
┌─ PHASE 4: QC Validation (LLM) ────────────────────┐
│  LLM: "Given the user's original intent, does the  │
│         pipeline output match what they asked for?" │
│  Checks:                                            │
│    - Are the extracted fields relevant to intent?   │
│    - Do conditions match stated thresholds?         │
│    - Is anything missing from what user wanted?     │
│    - Would this produce useful notifications?       │
│  Output: { valid: bool, issues: [], suggestions: [] }│
│  IF issues → loop back to Phase 2 to fix           │
│  IF still fails after iteration → present to user   │
│    with known issues for manual decision            │
└─────────────────────────────────────────────────────┘
       │
       ▼
┌─ CHECKPOINT 2: Confirm Pipeline ───────────────────┐
│  Show: Human-readable pipeline summary              │
│  Show: Visual pipeline diagram (blocks + arrows)    │
│  Show: Dry run results + QC verdict                 │
│  User: ✅ Looks good / ✏️ Feedback / 🔄 Redo       │
└─────────────────────────────────────────────────────┘
       │
       ▼
┌─ SAVE & SCHEDULE ──────────────────────────────────┐
│  Persist pipeline definition + metadata             │
│  Schedule first real check                          │
│  Stream: "✅ Watch created!"                        │
└─────────────────────────────────────────────────────┘
```

## Design Principles

- **2 user checkpoints**: after understanding + after build/QC. Not more — we don't want 5 rounds of questions.
- **Streaming throughout**: user sees progress live, not a loading spinner.
- **Iterative block config**: each specialist validates against real HTML. Retries before asking user.
- **Dry run shows real data**: user sees actual extracted content, not just "it works."
- **LLM QC gate**: validates output matches intent before user sees it. Auto-iterates to fix issues. Presents to user with known issues if it can't resolve them.
- **Small model friendly**: decomposed into focused calls. Model complexity slider adjusts granularity.

## Setup LLM Call Budget

| Step | Call | Small Model (7B) | Large Model (Haiku 4.5) |
|------|------|-------------------|---------------------|
| Intent parsing | 1 | 1 focused call | 1 call |
| Content analysis | 1-2 | 2 calls (structure + pagination) | 1 combined call |
| Block selection | 1 | 1 call | Merged with analysis |
| Per-block config | 2-5 | 1 per block | Batch 2-3 blocks per call |
| QC validation | 1 | 1 call | 1 call |
| **Total** | **6-10** | **6-10** | **4-6** |

## Specialist Prompts (Per Block Type)

Each block type has its own focused specialist prompt during Phase 2:

| Block | Specialist Prompt Focus |
|-------|------------------------|
| **Filter** | "Given this HTML region and user intent, what CSS/XPath selector isolates the relevant section?" Validates match count. |
| **ExtractSchema** | "What fields should we extract? What are their types and selectors?" Validates each selector returns data. |
| **Condition** | "User wants 'price below $50' — what condition config?" Maps natural language to operator + value. |
| **LlmEvaluate** | "User wants 'articles about cybersecurity' — what evaluation prompt?" Crafts the runtime prompt. |
| **Paginate** | "Does this page have pagination? What strategy?" Detects URL params vs. next buttons vs. infinite scroll. |
| **Notify** | "What notification template fits this watch?" Selects channel + formats output fields. |
