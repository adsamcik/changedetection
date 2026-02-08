# Output Node & Pipeline Visualization

## Output Node

The Output node is the terminal node of every pipeline. It has access to ALL upstream block outputs and serves two purposes:

### 1. Data Aggregation

Combines results from the pipeline into a final schema that the UI displays as the watch's "current state."

### 2. Display Configuration

The setup LLM generates the display config during pipeline assembly. It tells the UI how to render this watch on the dashboard.

### Schema

```json
{
  "display": {
    "title": "MacBook Pro 14\" — Amazon",
    "primaryValue": "$1,249.00",
    "primaryValueSource": "blocks.extract.price",
    "trend": { "field": "blocks.numericDelta.deltaPercent", "format": "percent" },
    "status": "watching",
    "cardType": "price",
    "sections": [
      { "label": "Stock", "source": "blocks.extract.stock", "type": "badge" },
      { "label": "Rating", "source": "blocks.extract.rating", "type": "stars" }
    ]
  },
  "data": {
    "price": 1249.00,
    "currency": "USD",
    "stock": "In Stock",
    "previousPrice": 1299.00,
    "deltaPercent": -3.8
  }
}
```

### Card Types

Different watch types render as different dashboard cards:

| Card Type | When | Shows |
|-----------|------|-------|
| `price` | NumericDelta block present | Big number + trend chart + threshold indicator |
| `list` | ListDiff block present | Item count + recent additions/removals |
| `content` | TextDiff/HashCompare present | Last change summary + diff link |
| `multiSignal` | Route block present | Status indicator per branch |

## Pipeline Visualization

### Vertical Flow Diagram

Blocks rendered as rounded rectangles with arrows between them:

- Each block shows: **icon** (per block type) + **human-readable name** + **status indicator**
- Status: ✅ success / ⚠️ degraded / ❌ failed / ⏸️ skipped
- Click to expand: input/output data, duration, error details
- For **Route blocks**: diagram branches into parallel vertical lanes that rejoin at Output

### Per-Block Execution History

Users see a traffic-light timeline of the last run:

```
✅ Navigate (0.8s) → ✅ Wait (0.3s) → ✅ Extract (0.2s) → ✅ ListDiff (0.01s) → ⏸️ Condition (no changes) → ⏸️ Notify (skipped)
```

Click any block to see:
- Input summary (truncated)
- Output summary (truncated)
- Duration
- Error details (if failed)
- Previous runs history for this block
