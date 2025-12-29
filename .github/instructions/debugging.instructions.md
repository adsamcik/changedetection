---
applyTo: "**/*"
excludeAgent: ["code-review"]
---
# Investigation & Debugging Framework

## Core Philosophy

**Finding A problem is not the same as finding THE problem.**

Every investigation MUST answer: "Am I fixing the root cause, or a symptom?"

---

## Phase 1: Problem Definition (Before ANY Investigation)

### 1.1 Capture the Exact Failure

Before looking at code, document precisely:

```
FAILURE REPORT
==============
What was expected: [exact expected behavior]
What actually happened: [exact observed behavior]  
Reproduction steps: [numbered steps to reproduce]
Environment: [OS, .NET version, relevant config]
Frequency: [always / intermittent / specific conditions]
Recent changes: [what changed before this started]
```

### 1.2 Define Success Criteria

Write down what "fixed" looks like BEFORE investigating:
- What specific behavior must work?
- What tests must pass?
- What edge cases must be verified?

> **Why?** Without clear criteria, you'll "fix" something and not know if it's the right thing.

---

## Phase 2: Structured Investigation

### 2.1 Create Investigation Plan (MANDATORY)

**ALWAYS use the todo list tool** to create investigation items BEFORE starting.

```
Investigation: [Brief description of the problem]
─────────────────────────────────────────────────
□ Reproduce the issue locally
□ Identify the code path (entry point → failure point)
□ Check error messages and stack traces
□ Examine related configuration
□ Review recent changes to affected files
□ Search for similar patterns elsewhere in codebase
□ Check test coverage for the affected code
□ [Domain-specific items based on problem type]
```

### 2.2 Trace the Complete Execution Path

Map the full journey from input to failure:

```
Entry Point → [Component A] → [Component B] → ... → Failure Point
     ↓              ↓              ↓                    ↓
   Verify        Verify        Verify              Document
   inputs      transforms     handoffs              failure
```

**At each step, ask:**
1. What goes IN to this component?
2. What comes OUT?
3. What SHOULD come out?
4. Is the discrepancy HERE or earlier?

### 2.3 Evidence Collection Protocol

Record observations in **neutral language** without conclusions:

```
OBSERVATIONS (not conclusions)
==============================
[1] Method ContentFetcher.FetchAsync returns null when URL ends with '/'
[2] Configuration value HttpTimeout is set to 5000ms
[3] Network trace shows response received at 4950ms average
[4] Exception type is TaskCanceledException, not HttpRequestException
[5] Same URL works in browser and curl
```

**Language Rules:**
| ✅ Neutral (Use This) | ❌ Conclusory (Avoid) |
|----------------------|----------------------|
| "Returns null when..." | "Fails to return..." |
| "Takes 4.8s to complete" | "Is too slow" |
| "Throws exception X" | "Crashes" |
| "Value is Y" | "Value is wrong" |

---

## Phase 3: Hypothesis Development

### 3.1 Generate Multiple Hypotheses

After collecting evidence, generate **at least 3 hypotheses**:

```
HYPOTHESES
==========
H1: [Hypothesis 1] - Evidence: [1, 3] - Contradicted by: [5]
H2: [Hypothesis 2] - Evidence: [2, 4] - Contradicted by: [none yet]
H3: [Hypothesis 3] - Evidence: [1, 4] - Contradicted by: [none yet]
```

### 3.2 Actively Try to Disprove Each Hypothesis

For each hypothesis, ask: **"What evidence would DISPROVE this?"**

Then specifically look for that evidence.

```
Testing H2: "Timeout is too short for slow responses"
─────────────────────────────────────────────────────
To disprove: Find a case where timeout is sufficient but still fails
Test: Set timeout to 30000ms, reproduce issue
Result: Still fails → H2 is NOT the root cause (or not the only cause)
```

### 3.3 Multi-Factor Analysis

Many bugs are "perfect storms." Ask:
- Could **multiple factors** combine to cause this?
- Is there a **race condition**?
- Is there **environmental dependency**?
- Does it require a **specific sequence of events**?

---

## Phase 4: Root Cause Verification

### 4.1 The "Five Whys" Technique

Don't stop at the first "why":

```
Problem: Request times out
Why? → Timeout is 5s, request takes 5.1s
Why? → Request includes expensive DB query
Why? → Query scans entire table without index
Why? → Index was removed in migration 3 weeks ago
Why? → Migration script had typo, dropped wrong index
ROOT CAUSE: Migration script error
```

### 4.2 Verify You Found the Root Cause

Answer ALL of these before concluding:

| Question | Your Answer |
|----------|-------------|
| Does this explain ALL symptoms observed? | |
| Why did this work before? What changed? | |
| Why doesn't this fail in other similar places? | |
| If I fix only this, will the problem be fully resolved? | |
| Could this be masking a deeper issue? | |

### 4.3 Distinguish Root Cause from Contributing Factors

```
ROOT CAUSE: The single thing that, if different, would prevent the bug
CONTRIBUTING FACTORS: Things that make it worse or easier to trigger
SYMPTOMS: Observable effects of the root cause
```

Example:
- **Root Cause**: Index missing from database table
- **Contributing Factor**: Timeout set too aggressively  
- **Symptom**: Request timeouts for users

Fixing the timeout masks the problem. Fix the root cause.

---

## Phase 5: Solution Validation

### 5.1 Pre-Implementation Checklist

Before writing ANY fix:

- [ ] Root cause is identified (not just symptoms)
- [ ] Fix addresses root cause (not workaround)
- [ ] All investigation todos are complete
- [ ] Success criteria are defined
- [ ] No conflicting hypotheses remain unexplained

### 5.2 Solution Testing Protocol

| Test Type | Requirement |
|-----------|-------------|
| Direct reproduction | Original failure now succeeds |
| Regression | Existing tests still pass |
| Edge cases | Related scenarios work |
| Negative testing | Invalid inputs handled correctly |

### 5.3 Post-Fix Verification

After implementing fix:

1. **Reproduce original issue** - Confirm it's fixed
2. **Run related tests** - No regressions
3. **Check similar code paths** - Same bug pattern elsewhere?
4. **Review own fix critically** - Any new problems introduced?

---

## Investigation Anti-Patterns

### Premature Conclusion Trap

| Anti-Pattern | Problem | Correct Approach |
|--------------|---------|------------------|
| "Found something suspicious, fixing it" | May not be root cause | Complete all investigation todos first |
| "This looks like the problem" | Confirmation bias | Actively try to disprove |
| "First test passed, done" | Incomplete validation | Test all success criteria |
| "Similar to a bug I fixed before" | Pattern matching error | Verify with evidence |

### Confirmation Bias Guards

- **Seek disconfirming evidence** as actively as confirming
- **Have someone else review** your hypothesis
- **Ask: What would convince me I'm wrong?**

### Single-Cause Fallacy

Real bugs often involve:
- **Multiple contributing factors** - A "perfect storm"
- **Cascading failures** - One issue triggering another
- **Coincidental correlations** - Something suspicious but unrelated
- **Hidden dependencies** - Implicit coupling

---

## Investigation Checklist (Copy for Each Investigation)

```markdown
## Investigation: [Problem Title]

### Phase 1: Definition
- [ ] Documented exact expected vs actual behavior
- [ ] Captured reproduction steps
- [ ] Defined success criteria

### Phase 2: Investigation  
- [ ] Created investigation todos
- [ ] Traced full execution path
- [ ] Collected evidence (minimum 5 observations)
- [ ] Completed ALL investigation todos

### Phase 3: Hypotheses
- [ ] Generated ≥3 hypotheses
- [ ] Attempted to disprove each hypothesis
- [ ] Considered multi-factor causes

### Phase 4: Root Cause
- [ ] Applied "Five Whys" 
- [ ] Verified root cause explains ALL symptoms
- [ ] Explained why this didn't fail before
- [ ] Confirmed no deeper issue masked

### Phase 5: Validation
- [ ] Fixed root cause (not symptom)
- [ ] Original issue is resolved
- [ ] Regression tests pass
- [ ] Checked for same pattern elsewhere
```

---

## Domain-Specific Investigation Guides

### HTTP/Network Issues
- Check timeout configurations at ALL layers
- Verify DNS resolution
- Check proxy/firewall settings
- Compare request/response headers
- Trace through reverse proxies

### Database Issues  
- Check query execution plans
- Verify indexes exist and are used
- Check connection pool settings
- Review transaction isolation levels
- Look for N+1 query patterns

### Async/Concurrency Issues
- Look for race conditions
- Check for deadlocks
- Verify cancellation token handling
- Review task scheduling
- Check for missing ConfigureAwait

### Blazor/SignalR Issues
- Check circuit state
- Verify hub connection lifecycle
- Review component render timing
- Check for disposed context access
- Verify JS interop calls

### LLM/Pipeline Issues
- Verify API keys and configuration
- Check rate limiting
- Review prompt construction
- Verify response parsing
- Check timeout settings for long operations
