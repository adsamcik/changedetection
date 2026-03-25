using TUnit.Core;

// Default timeout for all tests: 2 minutes.
// Prevents any individual test (including setup/teardown) from hanging
// the entire test suite indefinitely. Tests needing more time can
// override with [Timeout] on the method or class.
[assembly: Timeout(120_000)]
