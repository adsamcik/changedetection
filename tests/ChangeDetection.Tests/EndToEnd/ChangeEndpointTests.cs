using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Shared.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd;

[Category("Integration")]
public class ChangeEndpointTests : TestBase, IAsyncDisposable
{
    private TestWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [Before(Test)]
    public void Setup()
    {
        _factory = new TestWebApplicationFactory();
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory != null) await _factory.DisposeAsync();
    }

    [Test]
    public async Task GetAll_Empty_ReturnsEmptyList()
    {
        var response = await _client.GetAsync("/api/changes");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var changes = await response.Content.ReadFromJsonAsync<List<ChangeListItemDto>>();
        changes.ShouldNotBeNull();
        changes.ShouldBeEmpty();
    }

    [Test]
    public async Task GetAll_WithChanges_ReturnsList()
    {
        var (watchId, _, _) = await SeedWatchWithSnapshots();
        var eventRepo = _factory.Services.GetRequiredService<IRepository<ChangeEvent>>();

        var ev1 = CreateChangeEvent(watchId);
        var ev2 = CreateChangeEvent(watchId);
        await eventRepo.InsertAsync(ev1);
        await eventRepo.InsertAsync(ev2);

        var response = await _client.GetAsync("/api/changes");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var changes = await response.Content.ReadFromJsonAsync<List<ChangeListItemDto>>();
        changes.ShouldNotBeNull();
        changes.Count.ShouldBe(2);
        changes.ShouldAllBe(c => c.WatchId == watchId.ToString());
    }

    [Test]
    public async Task GetAll_FilterByWatchId_ReturnsFiltered()
    {
        var (watchId1, _, _) = await SeedWatchWithSnapshots();
        var (watchId2, _, _) = await SeedWatchWithSnapshots("https://other.com", "Other Watch");
        var eventRepo = _factory.Services.GetRequiredService<IRepository<ChangeEvent>>();

        await eventRepo.InsertAsync(CreateChangeEvent(watchId1));
        await eventRepo.InsertAsync(CreateChangeEvent(watchId1));
        await eventRepo.InsertAsync(CreateChangeEvent(watchId2));

        var response = await _client.GetAsync($"/api/changes?watchId={watchId1}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var changes = await response.Content.ReadFromJsonAsync<List<ChangeListItemDto>>();
        changes.ShouldNotBeNull();
        changes.Count.ShouldBe(2);
        changes.ShouldAllBe(c => c.WatchId == watchId1.ToString());
    }

    [Test]
    public async Task GetById_ExistingChange_ReturnsDetail()
    {
        var (watchId, prevSnapshotId, currSnapshotId) = await SeedWatchWithSnapshots();
        var eventRepo = _factory.Services.GetRequiredService<IRepository<ChangeEvent>>();

        var ev = CreateChangeEvent(watchId, prevSnapshotId, currSnapshotId);
        ev.DiffSummary = "Price changed";
        ev.DiffHtml = "<ins>$20</ins><del>$10</del>";
        ev.LinesAdded = 1;
        ev.LinesRemoved = 1;
        await eventRepo.InsertAsync(ev);

        var response = await _client.GetAsync($"/api/changes/{ev.Id}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var detail = await response.Content.ReadFromJsonAsync<ChangeDetailDto>();
        detail.ShouldNotBeNull();
        detail.Id.ShouldBe(ev.Id.ToString());
        detail.WatchId.ShouldBe(watchId.ToString());
        detail.Summary.ShouldBe("Price changed");
        detail.DiffHtml.ShouldBe("<ins>$20</ins><del>$10</del>");
        detail.LinesAdded.ShouldBe(1);
        detail.LinesRemoved.ShouldBe(1);
        detail.PreviousSnapshot.ShouldNotBeNull();
        detail.CurrentSnapshot.ShouldNotBeNull();
    }

    [Test]
    public async Task ChangeEndpoints_IncludeExtractedEntitiesJson()
    {
        var (watchId, prevSnapshotId, currSnapshotId) = await SeedWatchWithSnapshots();
        var eventRepo = _factory.Services.GetRequiredService<IRepository<ChangeEvent>>();

        var listingsJson = """
            [{"title":"Postdoc in Biomedicine","company":"Department of Chemistry","deadline":"2026-03-13","education_required":"PhD","match_assessment":"PASS - strong fit"}]
            """;

        var ev = CreateChangeEvent(watchId, prevSnapshotId, currSnapshotId);
        ev.BriefSummary = "1 new position found: Postdoc in Biomedicine";
        ev.DiffSummary = "41 lines added, 43 lines removed";
        ev.ExtractedEntitiesJson = listingsJson;
        await eventRepo.InsertAsync(ev);

        var listResponse = await _client.GetAsync($"/api/changes?watchId={watchId}");
        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var changes = await listResponse.Content.ReadFromJsonAsync<List<ChangeListItemDto>>();

        changes.ShouldNotBeNull();
        changes.Count.ShouldBe(1);
        changes[0].Summary.ShouldBe("1 new position found: Postdoc in Biomedicine");
        changes[0].ExtractedEntitiesJson.ShouldBe(listingsJson);

        var detailResponse = await _client.GetAsync($"/api/changes/{ev.Id}");
        detailResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = await detailResponse.Content.ReadFromJsonAsync<ChangeDetailDto>();

        detail.ShouldNotBeNull();
        detail.Summary.ShouldBe("1 new position found: Postdoc in Biomedicine");
        detail.ExtractedEntitiesJson.ShouldBe(listingsJson);
    }

    [Test]
    public async Task GetById_NonExistent_Returns404()
    {
        var response = await _client.GetAsync($"/api/changes/{Guid.NewGuid()}");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task MarkViewed_ExistingChange_SetsViewedFlag()
    {
        var (watchId, _, _) = await SeedWatchWithSnapshots();
        var eventRepo = _factory.Services.GetRequiredService<IRepository<ChangeEvent>>();

        var ev = CreateChangeEvent(watchId);
        ev.IsViewed = false;
        await eventRepo.InsertAsync(ev);

        var response = await _client.PostAsync($"/api/changes/{ev.Id}/viewed", null);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var updated = await eventRepo.GetByIdAsync(ev.Id);
        updated.ShouldNotBeNull();
        updated.IsViewed.ShouldBeTrue();
    }

    [Test]
    public async Task GetUnviewedCount_ReturnsCorrectCount()
    {
        var (watchId, _, _) = await SeedWatchWithSnapshots();
        var eventRepo = _factory.Services.GetRequiredService<IRepository<ChangeEvent>>();

        var viewed = CreateChangeEvent(watchId);
        viewed.IsViewed = true;
        var unviewed1 = CreateChangeEvent(watchId);
        unviewed1.IsViewed = false;
        var unviewed2 = CreateChangeEvent(watchId);
        unviewed2.IsViewed = false;

        await eventRepo.InsertAsync(viewed);
        await eventRepo.InsertAsync(unviewed1);
        await eventRepo.InsertAsync(unviewed2);

        var response = await _client.GetAsync("/api/changes/unviewed/count");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var count = await response.Content.ReadFromJsonAsync<int>();
        count.ShouldBe(2);
    }

    [Test]
    public async Task GetFieldHistory_ExistingData_ReturnsHistory()
    {
        var watchRepo = _factory.Services.GetRequiredService<IRepository<WatchedSite>>();
        var snapshotRepo = _factory.Services.GetRequiredService<IRepository<ChangeSnapshot>>();

        var watch = new WatchedSite
        {
            Url = "https://example.com/products",
            Name = "Product Watch",
            SchemaEnabled = true,
            Schema = new ExtractionSchema
            {
                ItemSelector = ".product",
                Fields =
                [
                    new SchemaField
                    {
                        Name = "price",
                        Selector = ".price",
                        Type = FieldType.Currency,
                        CurrencyCode = "USD",
                        IsIdentityField = false
                    },
                    new SchemaField
                    {
                        Name = "name",
                        Selector = ".name",
                        Type = FieldType.String,
                        IsIdentityField = true
                    }
                ],
                IdentityFieldNames = ["name"]
            }
        };
        await watchRepo.InsertAsync(watch);

        var objects = new List<ExtractedObject>
        {
            new()
            {
                IdentityKey = "Widget",
                Index = 0,
                Fields = new Dictionary<string, string?> { ["name"] = "Widget", ["price"] = "$10.00" }
            }
        };
        var objectsJson = JsonSerializer.Serialize(objects);

        var snap1 = new ChangeSnapshot
        {
            WatchedSiteId = watch.Id,
            Content = "snapshot1",
            ContentHash = "hash1",
            CapturedAt = DateTime.UtcNow.AddDays(-2),
            ExtractedObjectsJson = objectsJson
        };

        var objects2 = new List<ExtractedObject>
        {
            new()
            {
                IdentityKey = "Widget",
                Index = 0,
                Fields = new Dictionary<string, string?> { ["name"] = "Widget", ["price"] = "$15.00" }
            }
        };

        var snap2 = new ChangeSnapshot
        {
            WatchedSiteId = watch.Id,
            Content = "snapshot2",
            ContentHash = "hash2",
            CapturedAt = DateTime.UtcNow.AddDays(-1),
            ExtractedObjectsJson = JsonSerializer.Serialize(objects2)
        };

        await snapshotRepo.InsertAsync(snap1);
        await snapshotRepo.InsertAsync(snap2);

        var encodedIdentity = Uri.EscapeDataString("Widget");
        var response = await _client.GetAsync(
            $"/api/changes/{watch.Id}/field-history/{encodedIdentity}/price");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var history = await response.Content.ReadFromJsonAsync<FieldHistoryDto>();
        history.ShouldNotBeNull();
        history.FieldName.ShouldBe("price");
        history.ObjectIdentity.ShouldBe("Widget");
        history.DataPoints.ShouldNotBeNull();
        history.DataPoints.Count.ShouldBe(2);
        history.Trend.ShouldBe("up");
    }

    // ========== Helpers ==========

    private async Task<(Guid watchId, Guid prevSnapshotId, Guid currSnapshotId)> SeedWatchWithSnapshots(
        string url = "https://example.com",
        string name = "Test Watch")
    {
        var watchRepo = _factory.Services.GetRequiredService<IRepository<WatchedSite>>();
        var snapshotRepo = _factory.Services.GetRequiredService<IRepository<ChangeSnapshot>>();

        var watch = new WatchedSite { Url = url, Name = name };
        await watchRepo.InsertAsync(watch);

        var prev = new ChangeSnapshot
        {
            WatchedSiteId = watch.Id,
            Content = "previous content",
            ContentHash = "prev-hash",
            CapturedAt = DateTime.UtcNow.AddHours(-1)
        };
        var curr = new ChangeSnapshot
        {
            WatchedSiteId = watch.Id,
            Content = "current content",
            ContentHash = "curr-hash",
            CapturedAt = DateTime.UtcNow
        };

        await snapshotRepo.InsertAsync(prev);
        await snapshotRepo.InsertAsync(curr);

        return (watch.Id, prev.Id, curr.Id);
    }

    private static ChangeEvent CreateChangeEvent(
        Guid watchId,
        Guid? previousSnapshotId = null,
        Guid? currentSnapshotId = null)
    {
        return new ChangeEvent
        {
            WatchedSiteId = watchId,
            PreviousSnapshotId = previousSnapshotId ?? Guid.NewGuid(),
            CurrentSnapshotId = currentSnapshotId ?? Guid.NewGuid(),
            ChangeType = ChangeType.Modified,
            Importance = ChangeImportance.Medium,
            DiffSummary = "Changes detected",
            DetectedAt = DateTime.UtcNow
        };
    }
}
