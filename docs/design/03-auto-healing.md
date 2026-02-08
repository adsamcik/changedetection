# Runtime Auto-Healing

When selectors break at runtime (website redesign, A/B test rotation, bot detection), the system attempts automatic recovery before bothering the user.

## Failure Types

| Type | Cause | Signal |
|------|-------|--------|
| **Transient** | Server hiccup, rate limit, network timeout | Block returns error, next run works fine |
| **Selector drift** | Minor DOM change, class rename | Block returns 0 results when it previously returned N |
| **Full redesign** | Website completely restructured | Multiple blocks fail simultaneously |
| **Bot detection** | CAPTCHA page served instead of real content | Page HTML is completely different, no expected selectors exist |
| **Content removed** | Product discontinued, article archived | Page returns 404 or redirect |

## Recovery Flow

```
Block fails (0 results or extraction error)
       │
       ▼
┌─ Consecutive failures < Layer1Threshold ───────────┐
│  Just log, retry on next check cycle                │
│  (Could be transient: server hiccup, rate limit)    │
└─────────────────────────────────────────────────────┘
       │ Threshold reached
       ▼
┌─ Layer 1: Block Self-Heal (LLM) ──────────────────┐
│  Re-fetch page fresh                                │
│  LLM: "Selector returned results before, now empty. │
│         Here's current HTML. Suggest new selector."  │
│  Validate new selector against live page            │
│  IF works → update block config, reset failures     │
│  IF fails after N attempts → escalate               │
└─────────────────────────────────────────────────────┘
       │ More failures
       ▼
┌─ Layer 2: Pipeline Diagnosis (LLM) ───────────────┐
│  Compare current HTML vs setup-time HTML snapshot   │
│  LLM: "Diagnose: redesign? bot detection? removed?" │
│  IF fixable → reconfigure affected blocks           │
│  IF not → escalate                                  │
└─────────────────────────────────────────────────────┘
       │ More failures
       ▼
┌─ Layer 3: User Notification ───────────────────────┐
│  Pause watch                                        │
│  Notify: what broke, what we tried, user options    │
│  Options: [Retry] [Re-run setup] [Delete watch]     │
└─────────────────────────────────────────────────────┘
```

## Configurable Thresholds (Per Watch, With Defaults)

| Setting | Default | Description |
|---------|---------|-------------|
| `Layer1Threshold` | 3 | Consecutive failures before triggering LLM self-heal |
| `Layer1MaxAttempts` | 2 | LLM self-heal attempts before escalating |
| `Layer2Threshold` | 3 more | Additional failures before triggering pipeline diagnosis |
| `Layer2MaxAttempts` | 1 | Pipeline diagnosis attempts before escalating |
| `Layer3Action` | Pause + Notify | What happens when all healing fails |

All thresholds are configurable per watch — some users want aggressive healing, some want to know immediately.

## Requirements

- **Store setup-time HTML snapshot** (or DOM region) so Layer 2 can compare "what it looked like then vs. now" and distinguish redesign from minor shift from bot detection.
- **Both Layer 1 and Layer 2 use LLM** — programmatic heuristics won't reliably find new selectors in changed DOM.
- **Log all healing attempts** — every selector change, every diagnosis, every rollback. Visible in per-block execution history for debugging and user transparency.
- **Rollback capability** — if a healed selector starts returning wrong data, revert to previous config.
