---
applyTo: "**/*"
excludeAgent: ["code-review"]
---
# Debugging Methodology

## Complete Investigation Before Conclusions

**CRITICAL**: Never stop investigating when something "looks like the problem." Real bugs often involve:
- **Multiple contributing factors** - A "perfect storm" of conditions
- **Cascading failures** - One issue triggering another
- **Coincidental correlations** - Something suspicious that isn't the cause
- **Hidden dependencies** - Implicit coupling between components

## Investigation Workflow

1. **Create investigation todos FIRST** - List ALL areas to investigate
2. **Complete ALL todos** - Even if early finding looks like the answer
3. **Use neutral observation language**:
   - ✅ "Component X shows behavior Y"
   - ❌ "Component X is broken"
4. **NEVER short-circuit** - Synthesize findings AFTER all investigation
5. **Multi-factor analysis** - Ask: "What else could contribute?"

## Evidence Collection

Record observations without conclusions:
```
Observation 1: ContentFetcher returns null when URL has trailing slash
Observation 2: Repository query timeout set to 5s, average query takes 4.8s
Observation 3: SignalR hub disconnects after 30s idle
```

## Synthesis Phase

After ALL observations:
1. Which observations are **causally related**?
2. Which are **coincidental**?
3. Is there a **common root cause**?
4. Could multiple factors create a **"perfect storm"**?

## Anti-Patterns to Avoid

| Anti-Pattern | Description |
|--------------|-------------|
| Premature conclusion | Stopping when first issue found |
| Confirmation bias | Only looking for evidence supporting initial theory |
| Single-cause assumption | Believing bugs have exactly one cause |
| Satisfaction trap | Feeling "done" before complete analysis |
