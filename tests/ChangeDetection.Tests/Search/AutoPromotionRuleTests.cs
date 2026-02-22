using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Search;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Search;

[Category("Unit")]
public class AutoPromotionRuleTests : TestBase
{
    [Test]
    public async Task MatchesRule_UrlPatternGlob_MatchesWildcard()
    {
        var rule = new AutoPromotionRule { UrlPattern = "*github.com/*/releases*" };
        var result = new SearchResult
        {
            Url = "https://github.com/dotnet/runtime/releases/tag/v10.0.0",
            Title = "Release v10.0.0",
            Position = 1
        };

        SearchDiscoveryService.MatchesRule(rule, result).ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task MatchesRule_UrlPatternGlob_DoesNotMatchNonMatching()
    {
        var rule = new AutoPromotionRule { UrlPattern = "*github.com/*/releases*" };
        var result = new SearchResult
        {
            Url = "https://stackoverflow.com/questions/12345",
            Title = "Some question",
            Position = 1
        };

        SearchDiscoveryService.MatchesRule(rule, result).ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task MatchesRule_TitleContains_CaseInsensitive()
    {
        var rule = new AutoPromotionRule { TitleContains = "release" };
        var result = new SearchResult
        {
            Url = "https://example.com/news",
            Title = "New RELEASE Available Now",
            Position = 1
        };

        SearchDiscoveryService.MatchesRule(rule, result).ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task MatchesRule_TitleContains_NoMatch()
    {
        var rule = new AutoPromotionRule { TitleContains = "release" };
        var result = new SearchResult
        {
            Url = "https://example.com/blog",
            Title = "Blog Post About Cats",
            Position = 1
        };

        SearchDiscoveryService.MatchesRule(rule, result).ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task MatchesRule_BothCriteria_RequiresBothToMatch()
    {
        var rule = new AutoPromotionRule
        {
            UrlPattern = "*github.com*",
            TitleContains = "release"
        };

        var matchesBoth = new SearchResult
        {
            Url = "https://github.com/dotnet/releases",
            Title = "Release Notes",
            Position = 1
        };

        var matchesUrlOnly = new SearchResult
        {
            Url = "https://github.com/dotnet/docs",
            Title = "Documentation Update",
            Position = 2
        };

        var matchesTitleOnly = new SearchResult
        {
            Url = "https://stackoverflow.com/q/12345",
            Title = "Latest Release Info",
            Position = 3
        };

        SearchDiscoveryService.MatchesRule(rule, matchesBoth).ShouldBeTrue();
        SearchDiscoveryService.MatchesRule(rule, matchesUrlOnly).ShouldBeFalse();
        SearchDiscoveryService.MatchesRule(rule, matchesTitleOnly).ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task MatchesRule_NoCriteria_ReturnsFalse()
    {
        var rule = new AutoPromotionRule(); // no patterns set
        var result = new SearchResult
        {
            Url = "https://example.com",
            Title = "Anything",
            Position = 1
        };

        SearchDiscoveryService.MatchesRule(rule, result).ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task MatchGlob_SimplePatterns()
    {
        SearchDiscoveryService.MatchGlob("https://example.com/page", "*example.com*").ShouldBeTrue();
        SearchDiscoveryService.MatchGlob("https://other.com/page", "*example.com*").ShouldBeFalse();
        SearchDiscoveryService.MatchGlob("test.txt", "*.txt").ShouldBeTrue();
        SearchDiscoveryService.MatchGlob("test.md", "*.txt").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task MatchGlob_QuestionMarkSingleChar()
    {
        SearchDiscoveryService.MatchGlob("file1.txt", "file?.txt").ShouldBeTrue();
        SearchDiscoveryService.MatchGlob("file12.txt", "file?.txt").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task MatchGlob_CaseInsensitive()
    {
        SearchDiscoveryService.MatchGlob("HTTPS://EXAMPLE.COM", "*example.com").ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task AutoPromoteAsync_NoRules_ReturnsEmpty()
    {
        var parentWatch = CreateSearchWatch(autoPromotionRules: []);
        var watchService = Substitute.For<IWatchService>();
        watchService.GetByIdAsync(parentWatch.Id, Arg.Any<CancellationToken>()).Returns(parentWatch);

        var sut = new SearchDiscoveryService(watchService, CreateLogger<SearchDiscoveryService>());

        var results = new List<SearchResult>
        {
            new() { Url = "https://example.com", Title = "Test", Position = 1 }
        };

        var promoted = await sut.AutoPromoteAsync(parentWatch.Id, results);
        promoted.ShouldBeEmpty();
    }

    [Test]
    public async Task AutoPromoteAsync_MatchingRule_PromotesResult()
    {
        var rules = new List<AutoPromotionRule>
        {
            new() { UrlPattern = "*github.com*", IsEnabled = true }
        };

        var parentWatch = CreateSearchWatch(autoPromotionRules: rules);
        var watchService = Substitute.For<IWatchService>();
        watchService.GetByIdAsync(parentWatch.Id, Arg.Any<CancellationToken>()).Returns(parentWatch);
        watchService.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<WatchedSite>());
        watchService.CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new WatchedSite
            {
                Id = Guid.NewGuid(),
                Url = callInfo.Arg<CreateWatchRequest>().Url,
                Tags = callInfo.Arg<CreateWatchRequest>().Tags ?? []
            });

        var sut = new SearchDiscoveryService(watchService, CreateLogger<SearchDiscoveryService>());

        var results = new List<SearchResult>
        {
            new() { Url = "https://github.com/dotnet/runtime", Title = "Runtime", Position = 1 },
            new() { Url = "https://stackoverflow.com/q/123", Title = "SO Question", Position = 2 }
        };

        var promoted = await sut.AutoPromoteAsync(parentWatch.Id, results);

        promoted.Count.ShouldBe(1);
        promoted[0].Url.ShouldBe("https://github.com/dotnet/runtime");
    }

    [Test]
    public async Task AutoPromoteAsync_DisabledRule_SkipsMatches()
    {
        var rules = new List<AutoPromotionRule>
        {
            new() { UrlPattern = "*github.com*", IsEnabled = false }
        };

        var parentWatch = CreateSearchWatch(autoPromotionRules: rules);
        var watchService = Substitute.For<IWatchService>();
        watchService.GetByIdAsync(parentWatch.Id, Arg.Any<CancellationToken>()).Returns(parentWatch);

        var sut = new SearchDiscoveryService(watchService, CreateLogger<SearchDiscoveryService>());

        var results = new List<SearchResult>
        {
            new() { Url = "https://github.com/dotnet/runtime", Title = "Runtime", Position = 1 }
        };

        var promoted = await sut.AutoPromoteAsync(parentWatch.Id, results);
        promoted.ShouldBeEmpty();
    }

    [Test]
    public async Task AutoPromoteAsync_AlreadyPromotedUrl_Skipped()
    {
        var rules = new List<AutoPromotionRule>
        {
            new() { UrlPattern = "*github.com*", IsEnabled = true }
        };

        var parentWatch = CreateSearchWatch(autoPromotionRules: rules);
        var promotionTag = $"{SearchDiscoveryService.PromotedFromTagPrefix}{parentWatch.Id}";

        var existingWatch = new WatchedSite
        {
            Id = Guid.NewGuid(),
            Url = "https://github.com/dotnet/runtime",
            Tags = [promotionTag]
        };

        var watchService = Substitute.For<IWatchService>();
        watchService.GetByIdAsync(parentWatch.Id, Arg.Any<CancellationToken>()).Returns(parentWatch);
        watchService.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<WatchedSite> { existingWatch });

        var sut = new SearchDiscoveryService(watchService, CreateLogger<SearchDiscoveryService>());

        var results = new List<SearchResult>
        {
            new() { Url = "https://github.com/dotnet/runtime", Title = "Runtime", Position = 1 }
        };

        var promoted = await sut.AutoPromoteAsync(parentWatch.Id, results);
        promoted.ShouldBeEmpty();
    }

    [Test]
    public async Task AutoPromoteAsync_RuleWithCssSelector_AppliedToWatch()
    {
        var rules = new List<AutoPromotionRule>
        {
            new() { UrlPattern = "*github.com*", IsEnabled = true, CssSelector = "article.main" }
        };

        var parentWatch = CreateSearchWatch(autoPromotionRules: rules);
        var watchService = Substitute.For<IWatchService>();
        watchService.GetByIdAsync(parentWatch.Id, Arg.Any<CancellationToken>()).Returns(parentWatch);
        watchService.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<WatchedSite>());

        CreateWatchRequest? capturedRequest = null;
        watchService.CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedRequest = callInfo.Arg<CreateWatchRequest>();
                return new WatchedSite
                {
                    Id = Guid.NewGuid(),
                    Url = capturedRequest.Url,
                    Tags = capturedRequest.Tags ?? []
                };
            });

        var sut = new SearchDiscoveryService(watchService, CreateLogger<SearchDiscoveryService>());

        var results = new List<SearchResult>
        {
            new() { Url = "https://github.com/repo", Title = "Repo", Position = 1 }
        };

        await sut.AutoPromoteAsync(parentWatch.Id, results);

        capturedRequest.ShouldNotBeNull();
        capturedRequest.CssSelector.ShouldBe("article.main");
    }

    private static WatchedSite CreateSearchWatch(
        List<AutoPromotionRule>? autoPromotionRules = null) => new()
    {
        Id = Guid.NewGuid(),
        Url = "search: test query",
        Name = "Test Search",
        SourceType = SourceType.Search,
        SearchConfig = new SearchConfig
        {
            Query = "test query",
            ProviderId = "searxng",
            AutoPromotionRules = autoPromotionRules ?? []
        },
        CheckInterval = TimeSpan.FromMinutes(30),
        Tags = [],
        Notifications = new NotificationSettings()
    };
}
