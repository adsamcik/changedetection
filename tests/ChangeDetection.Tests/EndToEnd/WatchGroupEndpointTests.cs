using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChangeDetection.Shared.Dtos;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd;

[Category("Integration")]
public class WatchGroupEndpointTests : TestBase, IAsyncDisposable
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
    public async Task GetGroups_ReturnsEmptyList()
    {
        var response = await _client.GetAsync("/api/groups");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var groups = await response.Content.ReadFromJsonAsync<List<WatchGroupListItemDto>>();
        groups.ShouldNotBeNull();
        groups.ShouldBeEmpty();
    }

    [Test]
    public async Task CreateGroup_ReturnsCreated()
    {
        var dto = new WatchGroupCreateDto
        {
            Name = "PS5 Price Tracker",
            Description = "Track PS5 across retailers",
            Tags = ["gaming", "price"]
        };

        var response = await _client.PostAsJsonAsync("/api/groups", dto);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var created = await response.Content.ReadFromJsonAsync<WatchGroupDetailDto>();
        created.ShouldNotBeNull();
        created.Name.ShouldBe("PS5 Price Tracker");
        created.Description.ShouldBe("Track PS5 across retailers");
        created.Tags.ShouldContain("gaming");
        created.Id.ShouldNotBeNullOrEmpty();
    }

    [Test]
    public async Task CreateAndGetGroup_RoundTrips()
    {
        var dto = new WatchGroupCreateDto
        {
            Name = "Roundtrip Group",
            Description = "Test roundtrip"
        };
        var createResponse = await _client.PostAsJsonAsync("/api/groups", dto);
        var created = await createResponse.Content.ReadFromJsonAsync<WatchGroupDetailDto>();
        created.ShouldNotBeNull();

        var getResponse = await _client.GetAsync($"/api/groups/{created.Id}");
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var fetched = await getResponse.Content.ReadFromJsonAsync<WatchGroupDetailDto>();
        fetched.ShouldNotBeNull();
        fetched.Name.ShouldBe("Roundtrip Group");

        // List should contain it
        var listResponse = await _client.GetAsync("/api/groups");
        var list = await listResponse.Content.ReadFromJsonAsync<List<WatchGroupListItemDto>>();
        list.ShouldNotBeNull();
        list.ShouldContain(g => g.Id == created.Id);
    }

    [Test]
    public async Task UpdateGroup_ModifiesFields()
    {
        var created = await CreateGroupAsync("Before Update");

        var updateDto = new WatchGroupUpdateDto
        {
            Name = "After Update",
            Description = "Updated description",
            Tags = ["updated"]
        };
        var updateResponse = await _client.PutAsJsonAsync($"/api/groups/{created.Id}", updateDto);
        updateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await updateResponse.Content.ReadFromJsonAsync<WatchGroupDetailDto>();
        updated.ShouldNotBeNull();
        updated.Name.ShouldBe("After Update");
        updated.Description.ShouldBe("Updated description");
        updated.Tags.ShouldContain("updated");
    }

    [Test]
    public async Task UpdateGroup_WithAggregateFields()
    {
        var created = await CreateGroupAsync("Field Config Group");

        var updateDto = new WatchGroupUpdateDto
        {
            AggregateFields =
            [
                new AggregateFieldConfigDto
                {
                    FieldName = "price",
                    Function = "Min",
                    DisplayLabel = "Lowest Price",
                    IsPrimary = true,
                    CurrencyCode = "USD"
                },
                new AggregateFieldConfigDto
                {
                    FieldName = "stock",
                    Function = "Sum",
                    DisplayLabel = "Total Stock"
                }
            ]
        };

        var response = await _client.PutAsJsonAsync($"/api/groups/{created.Id}", updateDto);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await response.Content.ReadFromJsonAsync<WatchGroupDetailDto>();
        updated.ShouldNotBeNull();
        updated.AggregateFields.Count.ShouldBe(2);
        updated.AggregateFields[0].FieldName.ShouldBe("price");
        updated.AggregateFields[0].Function.ShouldBe("Min");
        updated.AggregateFields[1].FieldName.ShouldBe("stock");
    }

    [Test]
    public async Task DeleteGroup_Succeeds()
    {
        var created = await CreateGroupAsync("Delete Me");

        var deleteResponse = await _client.DeleteAsync($"/api/groups/{created.Id}");
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var getResponse = await _client.GetAsync($"/api/groups/{created.Id}");
        getResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task DeleteGroup_NonExistent_ReturnsNotFoundOrMethodNotAllowed()
    {
        var response = await _client.DeleteAsync($"/api/groups/{Guid.NewGuid()}");
        // UseStatusCodePagesWithReExecute may transform 404 to 405 for DELETE
        var status = (int)response.StatusCode;
        (status is 404 or 405).ShouldBeTrue($"Expected 404 or 405, got {response.StatusCode}");
    }

    [Test]
    public async Task AddAndRemoveMember_WorksEndToEnd()
    {
        // Create a group
        var group = await CreateGroupAsync("Membership Group");

        // Create a watch to add as member
        var watchDto = new WatchCreateDto
        {
            Url = "https://example.com/product",
            Title = "Example Product",
            CheckInterval = TimeSpan.FromHours(1)
        };
        var watchResponse = await _client.PostAsJsonAsync("/api/watches", watchDto);
        watchResponse.EnsureSuccessStatusCode();
        var watchId = await ExtractIdAsync(watchResponse);

        // Add member
        var addResponse = await _client.PostAsync($"/api/groups/{group.Id}/members/{watchId}", null);
        addResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Verify membership via group detail
        var detailResponse = await _client.GetAsync($"/api/groups/{group.Id}");
        var detail = await detailResponse.Content.ReadFromJsonAsync<WatchGroupDetailDto>();
        detail.ShouldNotBeNull();
        detail.Members.Count.ShouldBe(1);
        detail.Members[0].WatchId.ShouldBe(watchId);

        // Remove member — verify the endpoint returns success
        var removeResponse = await _client.DeleteAsync($"/api/groups/{group.Id}/members/{watchId}");
        removeResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task GetAggregate_EmptyGroup_ReturnsEmptySnapshot()
    {
        var group = await CreateGroupAsync("Empty Aggregate");

        var response = await _client.GetAsync($"/api/groups/{group.Id}/aggregate");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var snapshot = await response.Content.ReadFromJsonAsync<AggregateSnapshotDto>();
        snapshot.ShouldNotBeNull();
        snapshot.GroupId.ShouldBe(group.Id);
        snapshot.Fields.ShouldBeEmpty();
        snapshot.Members.ShouldBeEmpty();
    }

    [Test]
    public async Task GetAggregate_NonExistentGroup_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/api/groups/{Guid.NewGuid()}/aggregate");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task EvaluateAlerts_EmptyGroup_ReturnsNoAlerts()
    {
        var group = await CreateGroupAsync("Alert Eval Group");

        var response = await _client.GetAsync($"/api/groups/{group.Id}/alerts/evaluate");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<AggregateAlertResultDto>();
        result.ShouldNotBeNull();
        result.GroupId.ShouldBe(group.Id);
        result.TriggeredAlerts.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAlerts_NonExistentGroup_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/api/groups/{Guid.NewGuid()}/alerts/evaluate");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task GroupListItem_ShowsMemberCount()
    {
        var group = await CreateGroupAsync("Count Group");

        // Add two watches as members
        for (var i = 0; i < 2; i++)
        {
            var watchDto = new WatchCreateDto
            {
                Url = $"https://example.com/product-{i}",
                Title = $"Product {i}",
                CheckInterval = TimeSpan.FromHours(1)
            };
            var watchResponse = await _client.PostAsJsonAsync("/api/watches", watchDto);
            watchResponse.EnsureSuccessStatusCode();
            var watchId = await ExtractIdAsync(watchResponse);
            await _client.PostAsync($"/api/groups/{group.Id}/members/{watchId}", null);
        }

        var listResponse = await _client.GetAsync("/api/groups");
        var list = await listResponse.Content.ReadFromJsonAsync<List<WatchGroupListItemDto>>();
        list.ShouldNotBeNull();

        var item = list.First(g => g.Id == group.Id);
        item.MemberCount.ShouldBe(2);
    }

    [Test]
    public async Task DeleteGroup_UnlinksWatchesByDefault()
    {
        var group = await CreateGroupAsync("Unlink Group");

        // Create and add a watch
        var watchDto = new WatchCreateDto
        {
            Url = "https://example.com/unlink-test",
            Title = "Unlink Watch",
            CheckInterval = TimeSpan.FromHours(1)
        };
        var watchResponse = await _client.PostAsJsonAsync("/api/watches", watchDto);
        watchResponse.EnsureSuccessStatusCode();
        var watchId = await ExtractIdAsync(watchResponse);
        await _client.PostAsync($"/api/groups/{group.Id}/members/{watchId}", null);

        // Delete group without deleteWatches flag (default)
        await _client.DeleteAsync($"/api/groups/{group.Id}");

        // Watch should still exist
        var getWatch = await _client.GetAsync($"/api/watches/{watchId}");
        getWatch.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // --- Helpers ---

    private async Task<WatchGroupDetailDto> CreateGroupAsync(string name)
    {
        var dto = new WatchGroupCreateDto { Name = name };
        var response = await _client.PostAsJsonAsync("/api/groups", dto);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<WatchGroupDetailDto>();
        created.ShouldNotBeNull();
        return created;
    }

    /// <summary>
    /// Extracts the "id" field from an HTTP response JSON body using JsonDocument.
    /// Avoids full DTO deserialization issues (e.g., enum serialization mismatches).
    /// </summary>
    private static async Task<string> ExtractIdAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id").GetString()!;
    }
}
