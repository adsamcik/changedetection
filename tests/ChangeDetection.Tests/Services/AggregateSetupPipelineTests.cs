using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services;

[Category("Unit")]
public class AggregateSetupPipelineTests : TestBase
{
    private readonly IWatchSetupPipeline _watchPipeline = Substitute.For<IWatchSetupPipeline>();
    private readonly IWatchGroupService _groupService = Substitute.For<IWatchGroupService>();
    private readonly IWatchService _watchService = Substitute.For<IWatchService>();
    private readonly AggregateSetupPipeline _sut;

    public AggregateSetupPipelineTests()
    {
        _sut = new AggregateSetupPipeline(
            _watchPipeline,
            _groupService,
            _watchService,
            CreateLogger<AggregateSetupPipeline>());
    }

    [Test]
    public async Task SetupGroupAsync_CreatesGroupAndWatches()
    {
        var group = new WatchGroup { Name = "Test Group" };
        _groupService.CreateGroupAsync(Arg.Any<WatchGroupCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(group);

        var watch = new WatchedSite { Url = "https://amazon.com/ps5" };
        _watchService.CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(watch);

        _watchPipeline.ProcessAsync(Arg.Any<string>(), Arg.Any<PipelineOptions>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineResult
            {
                IsSuccess = true,
                FinalConfiguration = new WatchConfiguration
                {
                    Url = "https://amazon.com/ps5",
                    Name = "PS5 on Amazon",
                    Confidence = 0.9f
                }
            });

        var request = new AggregateSetupRequest
        {
            UserIntent = "Track PS5 price",
            Urls = ["https://amazon.com/ps5", "https://bestbuy.com/ps5"]
        };

        var result = await _sut.SetupGroupAsync(request);

        await _groupService.Received(1)
            .CreateGroupAsync(Arg.Any<WatchGroupCreateRequest>(), Arg.Any<CancellationToken>());
        await _watchPipeline.Received(2)
            .ProcessAsync(Arg.Any<string>(), Arg.Any<PipelineOptions>(), Arg.Any<CancellationToken>());
        await _watchService.Received(2)
            .CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetupGroupStreamingAsync_YieldsProgressForEachUrl()
    {
        var group = new WatchGroup { Name = "Streaming Test" };
        _groupService.CreateGroupAsync(Arg.Any<WatchGroupCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(group);
        _groupService.GetByIdAsync(group.Id, Arg.Any<CancellationToken>())
            .Returns(group);

        var watch = new WatchedSite { Url = "https://test.com" };
        _watchService.CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(watch);

        _watchPipeline.ProcessAsync(Arg.Any<string>(), Arg.Any<PipelineOptions>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineResult
            {
                IsSuccess = true,
                FinalConfiguration = new WatchConfiguration { Url = "https://test.com", Confidence = 0.8f }
            });

        var request = new AggregateSetupRequest
        {
            UserIntent = "Test",
            Urls = ["https://a.com", "https://b.com", "https://c.com"]
        };

        var stages = new List<AggregateSetupStage>();
        await foreach (var progress in _sut.SetupGroupStreamingAsync(request))
        {
            stages.Add(progress.Stage);
        }

        stages.ShouldContain(AggregateSetupStage.Started);
        stages.Count(s => s == AggregateSetupStage.SettingUpWatch).ShouldBe(3);
        stages.Count(s => s == AggregateSetupStage.WatchSetupComplete).ShouldBe(3);
        stages.ShouldContain(AggregateSetupStage.AligningSchemas);
        stages.ShouldContain(AggregateSetupStage.Complete);
    }

    [Test]
    public async Task SetupGroupStreamingAsync_HandlesPartialFailure()
    {
        var group = new WatchGroup { Name = "Partial Failure" };
        _groupService.CreateGroupAsync(Arg.Any<WatchGroupCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(group);
        _groupService.GetByIdAsync(group.Id, Arg.Any<CancellationToken>())
            .Returns(group);

        var watch = new WatchedSite { Url = "https://ok.com" };
        _watchService.CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(watch);

        // First URL succeeds, second fails
        _watchPipeline.ProcessAsync(Arg.Is<string>(s => s.Contains("ok.com")), Arg.Any<PipelineOptions>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineResult
            {
                IsSuccess = true,
                FinalConfiguration = new WatchConfiguration { Url = "https://ok.com", Confidence = 0.9f }
            });
        _watchPipeline.ProcessAsync(Arg.Is<string>(s => s.Contains("fail.com")), Arg.Any<PipelineOptions>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineResult { IsSuccess = false, ErrorMessage = "Content not found" });

        var request = new AggregateSetupRequest
        {
            UserIntent = "Test",
            Urls = ["https://ok.com", "https://fail.com"]
        };

        var stages = new List<AggregateSetupStage>();
        await foreach (var progress in _sut.SetupGroupStreamingAsync(request))
        {
            stages.Add(progress.Stage);
        }

        stages.ShouldContain(AggregateSetupStage.WatchSetupComplete);
        stages.ShouldContain(AggregateSetupStage.WatchSetupFailed);
        stages.ShouldContain(AggregateSetupStage.Complete); // Still completes with partial results
    }

    [Test]
    public async Task SetupGroupStreamingAsync_AllFail_YieldsFailedStage()
    {
        var group = new WatchGroup { Name = "All Fail" };
        _groupService.CreateGroupAsync(Arg.Any<WatchGroupCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(group);

        _watchPipeline.ProcessAsync(Arg.Any<string>(), Arg.Any<PipelineOptions>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineResult { IsSuccess = false, ErrorMessage = "Unreachable" });

        var request = new AggregateSetupRequest
        {
            UserIntent = "Test",
            Urls = ["https://a.com"]
        };

        var stages = new List<AggregateSetupStage>();
        await foreach (var progress in _sut.SetupGroupStreamingAsync(request))
        {
            stages.Add(progress.Stage);
        }

        stages.ShouldContain(AggregateSetupStage.WatchSetupFailed);
        stages.ShouldContain(AggregateSetupStage.Failed);
        stages.ShouldNotContain(AggregateSetupStage.Complete);
    }

    [Test]
    public async Task SetupGroupStreamingAsync_WithSchemas_SuggestsAggregation()
    {
        var group = new WatchGroup { Name = "Schema Test" };
        _groupService.CreateGroupAsync(Arg.Any<WatchGroupCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(group);
        _groupService.GetByIdAsync(group.Id, Arg.Any<CancellationToken>())
            .Returns(group);

        var watch = new WatchedSite { Url = "https://test.com" };
        _watchService.CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(watch);

        var schema = new ExtractionSchema
        {
            ItemSelector = ".product",
            Fields =
            [
                new SchemaField { Name = "price", Selector = ".price", Type = FieldType.Number },
                new SchemaField { Name = "title", Selector = ".title", Type = FieldType.String }
            ]
        };

        _watchPipeline.ProcessAsync(Arg.Any<string>(), Arg.Any<PipelineOptions>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineResult
            {
                IsSuccess = true,
                FinalConfiguration = new WatchConfiguration
                {
                    Url = "https://test.com",
                    Schema = schema,
                    SchemaEnabled = true,
                    Confidence = 0.9f
                }
            });

        var request = new AggregateSetupRequest
        {
            UserIntent = "Track prices",
            Urls = ["https://a.com", "https://b.com"],
            FieldHint = "price"
        };

        var stages = new List<AggregateSetupStage>();
        await foreach (var progress in _sut.SetupGroupStreamingAsync(request))
        {
            stages.Add(progress.Stage);
        }

        stages.ShouldContain(AggregateSetupStage.SuggestingAggregation);

        // Verify group was updated with aggregate field config
        await _groupService.Received(1)
            .UpdateGroupAsync(Arg.Is<WatchGroup>(g => g.AggregateFields.Count > 0), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetupGroupAsync_SetsGroupNameFromIntent()
    {
        var group = new WatchGroup { Name = "Track PS5 prices" };
        _groupService.CreateGroupAsync(
            Arg.Is<WatchGroupCreateRequest>(r => r.Name == "Track PS5 prices"),
            Arg.Any<CancellationToken>())
            .Returns(group);

        _watchPipeline.ProcessAsync(Arg.Any<string>(), Arg.Any<PipelineOptions>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineResult { IsSuccess = false, ErrorMessage = "fail" });

        var request = new AggregateSetupRequest
        {
            UserIntent = "Track PS5 prices",
            Urls = ["https://test.com"]
        };

        await _sut.SetupGroupAsync(request);

        await _groupService.Received()
            .CreateGroupAsync(
                Arg.Is<WatchGroupCreateRequest>(r => r.Name == "Track PS5 prices"),
                Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetupGroupAsync_UsesExplicitGroupName()
    {
        var group = new WatchGroup { Name = "My Custom Group" };
        _groupService.CreateGroupAsync(
            Arg.Is<WatchGroupCreateRequest>(r => r.Name == "My Custom Group"),
            Arg.Any<CancellationToken>())
            .Returns(group);

        _watchPipeline.ProcessAsync(Arg.Any<string>(), Arg.Any<PipelineOptions>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineResult { IsSuccess = false, ErrorMessage = "fail" });

        var request = new AggregateSetupRequest
        {
            UserIntent = "Track PS5 prices",
            Urls = ["https://test.com"],
            GroupName = "My Custom Group"
        };

        await _sut.SetupGroupAsync(request);

        await _groupService.Received()
            .CreateGroupAsync(
                Arg.Is<WatchGroupCreateRequest>(r => r.Name == "My Custom Group"),
                Arg.Any<CancellationToken>());
    }
}
