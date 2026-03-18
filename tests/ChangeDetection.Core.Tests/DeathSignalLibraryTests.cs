using ChangeDetection.Core.Pipeline;
using Shouldly;

namespace ChangeDetection.Core.Tests;

[Category("Unit")]
public class DeathSignalLibraryTests
{
    [Test]
    public void ContainsDeathSignal_FindsConfiguredLanguageSignal()
    {
        DeathSignalLibrary.ContainsDeathSignal("Denne side siger: stillingen er besat", "da").ShouldBeTrue();
    }

    [Test]
    public void ContainsDeathSignal_FallsBackToAllLanguages()
    {
        DeathSignalLibrary.ContainsDeathSignal("Oops, stránka nenalezena.").ShouldBeTrue();
    }

    [Test]
    public void ContainsDeathSignal_ReturnsFalse_WhenNoSignalExists()
    {
        DeathSignalLibrary.ContainsDeathSignal("Application page is live and accepting applicants.", "en").ShouldBeFalse();
    }

    [Test]
    public void ContainsDeathSignal_DoesNotMatch_Generic404Text()
    {
        DeathSignalLibrary.ContainsDeathSignal("We have 404 applicants in the funnel.", "en").ShouldBeFalse();
    }
}
