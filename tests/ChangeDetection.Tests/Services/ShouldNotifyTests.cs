using ChangeDetection.Core.Entities;
using ChangeDetection.Services.Background;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services;

[Category("Unit")]
public class ShouldNotifyTests
{
    private static NotificationSettings EnabledNotifications() => new()
    {
        EmailEnabled = true
    };

    [Test]
    public async Task ShouldNotify_NoRelevanceThreshold_UsesImportanceOnly()
    {
        var settings = EnabledNotifications();
        settings.MinimumImportance = ChangeImportance.Medium;
        var change = new ChangeEvent { Importance = ChangeImportance.High };

        ChangeCheckBackgroundService.ShouldNotify(settings, change).ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ShouldNotify_BelowImportance_ReturnsFalse()
    {
        var settings = EnabledNotifications();
        settings.MinimumImportance = ChangeImportance.High;
        var change = new ChangeEvent { Importance = ChangeImportance.Low };

        ChangeCheckBackgroundService.ShouldNotify(settings, change).ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ShouldNotify_MeetsRelevanceThreshold_ReturnsTrue()
    {
        var settings = EnabledNotifications();
        var change = new ChangeEvent { Importance = ChangeImportance.Medium, RelevanceScore = 0.8f };

        ChangeCheckBackgroundService.ShouldNotify(settings, change, minRelevance: 0.5f).ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ShouldNotify_BelowRelevanceThreshold_ReturnsFalse()
    {
        var settings = EnabledNotifications();
        var change = new ChangeEvent { Importance = ChangeImportance.Medium, RelevanceScore = 0.3f };

        ChangeCheckBackgroundService.ShouldNotify(settings, change, minRelevance: 0.5f).ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ShouldNotify_NullRelevanceScore_IgnoresThreshold()
    {
        var settings = EnabledNotifications();
        var change = new ChangeEvent { Importance = ChangeImportance.Medium, RelevanceScore = null };

        ChangeCheckBackgroundService.ShouldNotify(settings, change, minRelevance: 0.9f).ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ShouldNotify_NoChannels_ReturnsFalse()
    {
        var settings = new NotificationSettings();
        var change = new ChangeEvent { Importance = ChangeImportance.Critical, RelevanceScore = 1.0f };

        ChangeCheckBackgroundService.ShouldNotify(settings, change).ShouldBeFalse();
        await Task.CompletedTask;
    }
}
