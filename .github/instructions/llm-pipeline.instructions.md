---
applyTo: "src/**/Pipeline/**/*.cs,src/**/Agents/**/*.cs,src/**/Services/*Agent*.cs,src/**/Services/*Llm*.cs"
---
# LLM Pipeline Architecture

## Core Principle: LLM-Only

The watch setup pipeline is **LLM-only** with **no heuristics or regex fallbacks**.

### Non-Negotiable Rules

1. **No Heuristics** - Never use regex, pattern matching, or rule-based extraction
2. **No Fallbacks** - If LLM unavailable, fail gracefully with clear error
3. **Multi-Agent Design** - Specialized agents for focused tasks
4. **Parallel Execution** - Independent agents via `IAsyncEnumerable.Merge()`
5. **Streaming Responses** - Real-time output including reasoning
6. **In-Memory Sessions** - 30-minute sliding expiration, no persistence
7. **Input-Anchored Validation** - Only verify extracted values exist in original input

## Agent Types

| Agent | Responsibility |
|-------|----------------|
| `UrlExtractionAgent` | Extract URLs from natural language |
| `ContentAnalysisAgent` | Analyze page structure and intent |
| `SelectorGenerationAgent` | Generate CSS/XPath selectors |
| `ValidationAgent` | Verify config against original input |
| `SynthesisAgent` | Combine outputs into final config |
| `ResolutionAgent` | Reconcile conflicting outputs |

## Streaming Chunks

```csharp
public record AgentStreamChunk(
    string AgentName,
    ChunkType Type,      // Thinking, Intermediate, Question, Result, Validation
    string Content,
    float? Confidence,
    bool IsCollapsible); // Reasoning collapsible, results not
```

## UI Guidelines

- Reasoning **collapsed by default** with "Show thinking" toggle
- Per-agent activity indicators for parallel work
- Final synthesized response displayed cleanly
