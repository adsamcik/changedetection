using ChangeDetection.Core.Interfaces;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Scraping;

[Category("Unit")]
public class DeviceProfileTests
{
    [Test]
    public async Task Desktop_HasExpectedDimensions()
    {
        var settings = DeviceProfileSettings.FromProfile(DeviceProfile.Desktop);
        settings.ViewportWidth.ShouldBe(1920);
        settings.ViewportHeight.ShouldBe(1080);
        settings.IsMobile.ShouldBeFalse();
        settings.HasTouch.ShouldBeFalse();
        settings.DeviceScaleFactor.ShouldBe(1.0f);
        await Task.CompletedTask;
    }

    [Test]
    public async Task MobilePortrait_HasMobileSettings()
    {
        var settings = DeviceProfileSettings.FromProfile(DeviceProfile.MobilePortrait);
        settings.ViewportWidth.ShouldBe(390);
        settings.ViewportHeight.ShouldBe(844);
        settings.IsMobile.ShouldBeTrue();
        settings.HasTouch.ShouldBeTrue();
        settings.DeviceScaleFactor.ShouldBeGreaterThan(1.0f);
        settings.UserAgent.ShouldContain("iPhone");
        await Task.CompletedTask;
    }

    [Test]
    public async Task MobileLandscape_SwapsDimensions()
    {
        var settings = DeviceProfileSettings.FromProfile(DeviceProfile.MobileLandscape);
        settings.ViewportWidth.ShouldBeGreaterThan(settings.ViewportHeight);
        settings.IsMobile.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Tablet_HasTabletSettings()
    {
        var settings = DeviceProfileSettings.FromProfile(DeviceProfile.Tablet);
        settings.ViewportWidth.ShouldBe(820);
        settings.ViewportHeight.ShouldBe(1180);
        settings.IsMobile.ShouldBeTrue();
        settings.HasTouch.ShouldBeTrue();
        settings.UserAgent.ShouldContain("iPad");
        await Task.CompletedTask;
    }

    [Test]
    public async Task AllProfiles_HaveDistinctUserAgents()
    {
        var profiles = Enum.GetValues<DeviceProfile>();
        var agents = profiles.Select(p => DeviceProfileSettings.FromProfile(p).UserAgent).ToList();
        agents.Distinct().Count().ShouldBe(3); // Desktop, Mobile (shared), Tablet
        await Task.CompletedTask;
    }
}
