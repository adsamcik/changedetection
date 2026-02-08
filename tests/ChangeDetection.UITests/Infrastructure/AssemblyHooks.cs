using TUnit.Core;

namespace ChangeDetection.UITests.Infrastructure;

/// <summary>
/// Assembly-level hooks to ensure the shared server and browser
/// are properly cleaned up when the test run completes.
/// </summary>
public static class AssemblyHooks
{
    [After(Assembly)]
    public static async Task CleanupSharedResources()
    {
        await UITestBase.ShutdownAsync();
    }
}
