using ChangeDetection.Services.Scraping;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Scraping;

[Category("Unit")]
public class RobotsTxtCheckerTests
{
    [Test]
    public async Task ParseRobotsTxt_EmptyContent_ReturnsEmptyRules()
    {
        var rules = RobotsTxtChecker.ParseRobotsTxt("");
        rules.Count.ShouldBe(0);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ParseRobotsTxt_DisallowAll_ReturnsSingleRule()
    {
        var rules = RobotsTxtChecker.ParseRobotsTxt("User-agent: *\nDisallow: /");
        rules.Count.ShouldBe(1);
        rules[0].Agent.ShouldBe("*");
        rules[0].Path.ShouldBe("/");
        rules[0].IsAllow.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ParseRobotsTxt_AllowAndDisallow_ReturnsBothRules()
    {
        var content = "User-agent: *\nAllow: /public\nDisallow: /private";
        var rules = RobotsTxtChecker.ParseRobotsTxt(content);
        rules.Count.ShouldBe(2);
        rules.ShouldContain(r => r.IsAllow && r.Path == "/public");
        rules.ShouldContain(r => !r.IsAllow && r.Path == "/private");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ParseRobotsTxt_CommentsIgnored()
    {
        var content = "# This is a comment\nUser-agent: *\nDisallow: /secret # hidden";
        var rules = RobotsTxtChecker.ParseRobotsTxt(content);
        rules.Count.ShouldBe(1);
        rules[0].Path.ShouldBe("/secret");
        await Task.CompletedTask;
    }

    [Test]
    public async Task EvaluateRules_DisallowedPath_ReturnsDisallowed()
    {
        var rules = RobotsTxtChecker.ParseRobotsTxt("User-agent: *\nDisallow: /admin");
        var result = RobotsTxtChecker.EvaluateRules(rules, "/admin/settings");
        result.Status.ShouldBe(Core.Interfaces.RobotsTxtStatus.Disallowed);
        await Task.CompletedTask;
    }

    [Test]
    public async Task EvaluateRules_AllowedPath_ReturnsAllowed()
    {
        var rules = RobotsTxtChecker.ParseRobotsTxt("User-agent: *\nDisallow: /admin");
        var result = RobotsTxtChecker.EvaluateRules(rules, "/public/page");
        result.Status.ShouldBe(Core.Interfaces.RobotsTxtStatus.Allowed);
        await Task.CompletedTask;
    }

    [Test]
    public async Task EvaluateRules_LongerPathWins()
    {
        var content = "User-agent: *\nDisallow: /api\nAllow: /api/public";
        var rules = RobotsTxtChecker.ParseRobotsTxt(content);
        var result = RobotsTxtChecker.EvaluateRules(rules, "/api/public/docs");
        result.Status.ShouldBe(Core.Interfaces.RobotsTxtStatus.Allowed);
        await Task.CompletedTask;
    }

    [Test]
    public async Task EvaluateRules_NoMatchingRules_ReturnsAllowed()
    {
        var rules = RobotsTxtChecker.ParseRobotsTxt("User-agent: Googlebot\nDisallow: /");
        var result = RobotsTxtChecker.EvaluateRules(rules, "/anything");
        result.Status.ShouldBe(Core.Interfaces.RobotsTxtStatus.Allowed);
        await Task.CompletedTask;
    }
}
