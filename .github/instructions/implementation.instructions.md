---
applyTo: "src/**/*.cs"
---
# Implementation Standards

## Always Implement Properly

**CRITICAL**: Never implement quick hacks or workarounds.

1. **No Quick Fixes** - If a feature requires architectural changes, make them
2. **Follow Existing Patterns** - Study similar implementations in the codebase
3. **Interface-First Design** - Define in `ChangeDetection.Core` before implementing
4. **Streaming Over Blocking** - Use `IAsyncEnumerable<T>` for long operations
5. **Complete the Circuit** - Wire through: Interface → Implementation → Hub/Endpoint → Client

## Long-Term Health Focus

Every implementation must prioritize long-term health over short-term convenience.

**Guiding Principles:**
- **Sustainability Over Speed** - Maintainable beats quick
- **Future Developer Empathy** - Write code others can understand
- **Incremental Improvement** - Leave codebase better than found
- **Avoid Coupling** - Changes shouldn't cascade through system
- **Test Coverage** - Untested code is a liability
- **Clear Boundaries** - Respect layers: Core → Shared → Server → Client

## When Fixing Bugs

- Fix root cause, not symptom
- Consider if bug reveals design flaw
- Add regression tests
- Check for similar patterns elsewhere

## When Adding Features

- Design for extensibility
- Don't hardcode values that might change
- Consider performance at scale
- Document non-obvious decisions

## Red Flags to Avoid

- `// TODO: fix this later` without tracking issue
- Copy-pasting instead of extracting shared logic
- Suppressing warnings instead of fixing
- Tightly coupling to implementation details
