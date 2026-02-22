using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Search;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Search;

[Category("Unit")]
public class SearchDiscoveryServiceTests : TestBase
{
    [Test]
    public async Task PromoteResult_WithValidSearchWatch_CreatesUrlWatch()
    {
        var parentWatch = CreateSearchWatch();
        var watchService = Substitute.For<IWatchService>();
        watchService.GetByIdAsync(parentWatch.Id, Arg.Any<CancellationToken>())
            .Returns(parentWatch);

        WatchedSite? createdWatch = null;
        watchService.CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var req = callInfo.Arg<CreateWatchRequest>();
                createdWatch = new WatchedSite
                {
                    Id = Guid.NewGuid(),
                    Url = req.Url,
                    Name = req.Name,
                    SourceType = SourceType.Url,
                    Tags = req.Tags ?? [],
                    CheckInterval = req.CheckInterval ?? TimeSpan.FromMinutes(30)
                };
                return createdWatch;
            });

        var logger = CreateLogger<SearchDiscoveryService>();
        var sut = new SearchDiscoveryService(watchService, logger);

        var request = new PromoteSearchResultRequest
        {
            Url = "https://example.com/discovered-page",
            Name = "Interesting Page"
        };

        var result = await sut.PromoteResultAsync(parentWatch.Id, request);

        result.ShouldNotBeNull();
        result.Url.ShouldBe("https://example.com/discovered-page");
        result.Name.ShouldBe("Interesting Page");
        result.SourceType.ShouldBe(SourceType.Url);
    }

    [Test]
    public async Task PromoteResult_TagsLinkToParentSearchWatch()
    {
        var parentWatch = CreateSearchWatch();
        var watchService = Substitute.For<IWatchService>();
        watchService.GetByIdAsync(parentWatch.Id, Arg.Any<CancellationToken>())
            .Returns(parentWatch);

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

        var logger = CreateLogger<SearchDiscoveryService>();
        var sut = new SearchDiscoveryService(watchService, logger);

        await sut.PromoteResultAsync(parentWatch.Id, new PromoteSearchResultRequest
        {
            Url = "https://example.com/page"
        });

        capturedRequest.ShouldNotBeNull();
        capturedRequest.Tags.ShouldNotBeNull();
        capturedRequest.Tags.ShouldContain($"{SearchDiscoveryService.PromotedFromTagPrefix}{parentWatch.Id}");
    }

    [Test]
    public async Task PromoteResult_WhenParentNotFound_ReturnsNull()
    {
        var watchService = Substitute.For<IWatchService>();
        watchService.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((WatchedSite?)null);

        var logger = CreateLogger<SearchDiscoveryService>();
        var sut = new SearchDiscoveryService(watchService, logger);

        var result = await sut.PromoteResultAsync(Guid.NewGuid(), new PromoteSearchResultRequest
        {
            Url = "https://example.com/page"
        });

        result.ShouldBeNull();
    }

    [Test]
    public async Task PromoteResult_WhenParentIsUrlWatch_ReturnsNull()
    {
        var urlWatch = new WatchedSite
        {
            Id = Guid.NewGuid(),
            Url = "https://example.com",
            SourceType = SourceType.Url
        };

        var watchService = Substitute.For<IWatchService>();
        watchService.GetByIdAsync(urlWatch.Id, Arg.Any<CancellationToken>())
            .Returns(urlWatch);

        var logger = CreateLogger<SearchDiscoveryService>();
        var sut = new SearchDiscoveryService(watchService, logger);

        var result = await sut.PromoteResultAsync(urlWatch.Id, new PromoteSearchResultRequest
        {
            Url = "https://example.com/page"
        });

        result.ShouldBeNull();
    }

    [Test]
    public async Task PromoteResult_InheritsCheckIntervalFromParent()
    {
        var parentWatch = CreateSearchWatch();
        parentWatch.CheckInterval = TimeSpan.FromHours(2);

        var watchService = Substitute.For<IWatchService>();
        watchService.GetByIdAsync(parentWatch.Id, Arg.Any<CancellationToken>())
            .Returns(parentWatch);

        CreateWatchRequest? capturedRequest = null;
        watchService.CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedRequest = callInfo.Arg<CreateWatchRequest>();
                return new WatchedSite
                {
                    Id = Guid.NewGuid(),
                    Url = capturedRequest.Url,
                    CheckInterval = capturedRequest.CheckInterval ?? TimeSpan.FromMinutes(30)
                };
            });

        var logger = CreateLogger<SearchDiscoveryService>();
        var sut = new SearchDiscoveryService(watchService, logger);

        await sut.PromoteResultAsync(parentWatch.Id, new PromoteSearchResultRequest
        {
            Url = "https://example.com/page"
        });

        capturedRequest.ShouldNotBeNull();
        capturedRequest.CheckInterval.ShouldBe(TimeSpan.FromHours(2));
    }

    [Test]
    public async Task PromoteResult_CustomCheckIntervalOverridesParent()
    {
        var parentWatch = CreateSearchWatch();
        parentWatch.CheckInterval = TimeSpan.FromHours(2);

        var watchService = Substitute.For<IWatchService>();
        watchService.GetByIdAsync(parentWatch.Id, Arg.Any<CancellationToken>())
            .Returns(parentWatch);

        CreateWatchRequest? capturedRequest = null;
        watchService.CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedRequest = callInfo.Arg<CreateWatchRequest>();
                return new WatchedSite
                {
                    Id = Guid.NewGuid(),
                    Url = capturedRequest.Url,
                    CheckInterval = capturedRequest.CheckInterval ?? TimeSpan.FromMinutes(30)
                };
            });

        var logger = CreateLogger<SearchDiscoveryService>();
        var sut = new SearchDiscoveryService(watchService, logger);

        await sut.PromoteResultAsync(parentWatch.Id, new PromoteSearchResultRequest
        {
            Url = "https://example.com/page",
            CheckInterval = TimeSpan.FromMinutes(15)
        });

        capturedRequest.ShouldNotBeNull();
        capturedRequest.CheckInterval.ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Test]
    public async Task GetPromotedWatches_ReturnsWatchesWithMatchingTag()
    {
        var parentId = Guid.NewGuid();
        var promotionTag = $"{SearchDiscoveryService.PromotedFromTagPrefix}{parentId}";

        var allWatches = new List<WatchedSite>
        {
            new() { Id = Guid.NewGuid(), Url = "https://promoted1.com", Tags = [promotionTag] },
            new() { Id = Guid.NewGuid(), Url = "https://promoted2.com", Tags = [promotionTag, "other"] },
            new() { Id = Guid.NewGuid(), Url = "https://unrelated.com", Tags = ["unrelated"] },
            new() { Id = Guid.NewGuid(), Url = "https://notag.com", Tags = [] }
        };

        var watchService = Substitute.For<IWatchService>();
        watchService.GetAllAsync(Arg.Any<CancellationToken>()).Returns(allWatches);

        var logger = CreateLogger<SearchDiscoveryService>();
        var sut = new SearchDiscoveryService(watchService, logger);

        var promoted = await sut.GetPromotedWatchesAsync(parentId);

        promoted.Count.ShouldBe(2);
        promoted.ShouldAllBe(w => w.Tags.Contains(promotionTag));
    }

    [Test]
    public async Task GetPromotedWatches_ReturnsEmptyWhenNonePromoted()
    {
        var watchService = Substitute.For<IWatchService>();
        watchService.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<WatchedSite>());

        var logger = CreateLogger<SearchDiscoveryService>();
        var sut = new SearchDiscoveryService(watchService, logger);

        var promoted = await sut.GetPromotedWatchesAsync(Guid.NewGuid());

        promoted.ShouldBeEmpty();
    }

    [Test]
    public async Task PromoteResult_InheritsNotificationsFromParent()
    {
        var parentWatch = CreateSearchWatch();
        parentWatch.Notifications = new NotificationSettings
        {
            EmailEnabled = true,
            EmailAddress = "user@example.com"
        };

        var watchService = Substitute.For<IWatchService>();
        watchService.GetByIdAsync(parentWatch.Id, Arg.Any<CancellationToken>())
            .Returns(parentWatch);

        CreateWatchRequest? capturedRequest = null;
        watchService.CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedRequest = callInfo.Arg<CreateWatchRequest>();
                return new WatchedSite
                {
                    Id = Guid.NewGuid(),
                    Url = capturedRequest.Url,
                    Notifications = capturedRequest.Notifications ?? new()
                };
            });

        var logger = CreateLogger<SearchDiscoveryService>();
        var sut = new SearchDiscoveryService(watchService, logger);

        await sut.PromoteResultAsync(parentWatch.Id, new PromoteSearchResultRequest
        {
            Url = "https://example.com/page"
        });

        capturedRequest.ShouldNotBeNull();
        capturedRequest.Notifications.ShouldNotBeNull();
        capturedRequest.Notifications.EmailEnabled.ShouldBeTrue();
    }

    private static WatchedSite CreateSearchWatch() => new()
    {
        Id = Guid.NewGuid(),
        Url = "search: dotnet news",
        Name = "Search: dotnet news",
        SourceType = SourceType.Search,
        SearchConfig = new SearchConfig
        {
            Query = "dotnet news",
            ProviderId = "searxng"
        },
        CheckInterval = TimeSpan.FromMinutes(30),
        Tags = [],
        Notifications = new NotificationSettings()
    };
}
