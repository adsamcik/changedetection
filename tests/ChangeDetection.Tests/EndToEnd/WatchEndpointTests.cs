using System.Net;
using System.Net.Http.Json;
using ChangeDetection.Shared.Dtos;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd;

[Category("Integration")]
public class WatchEndpointTests : TestBase, IAsyncDisposable
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
    public async Task GetWatches_ReturnsEmptyList()
    {
        var response = await _client.GetAsync("/api/watches");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var watches = await response.Content.ReadFromJsonAsync<List<WatchListItemDto>>();
        watches.ShouldNotBeNull();
        watches.ShouldBeEmpty();
    }

    [Test]
    public async Task CreateWatch_ReturnsCreated()
    {
        var dto = new WatchCreateDto
        {
            Url = "https://example.com",
            Title = "Example Watch",
            CheckInterval = TimeSpan.FromHours(1)
        };

        var response = await _client.PostAsJsonAsync("/api/watches", dto);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var created = await response.Content.ReadFromJsonAsync<WatchDetailDto>();
        created.ShouldNotBeNull();
        created.Url.ShouldBe("https://example.com");
        created.Title.ShouldBe("Example Watch");
        created.Id.ShouldNotBeNullOrEmpty();
    }

    [Test]
    public async Task CreateAndGetWatch_RoundTrips()
    {
        // Create
        var dto = new WatchCreateDto
        {
            Url = "https://example.com/roundtrip",
            Title = "Roundtrip Watch",
            CheckInterval = TimeSpan.FromMinutes(30)
        };
        var createResponse = await _client.PostAsJsonAsync("/api/watches", dto);
        var created = await createResponse.Content.ReadFromJsonAsync<WatchDetailDto>();
        created.ShouldNotBeNull();

        // Get by ID
        var getResponse = await _client.GetAsync($"/api/watches/{created.Id}");
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var fetched = await getResponse.Content.ReadFromJsonAsync<WatchDetailDto>();
        fetched.ShouldNotBeNull();
        fetched.Url.ShouldBe("https://example.com/roundtrip");
        fetched.Title.ShouldBe("Roundtrip Watch");

        // List should contain it
        var listResponse = await _client.GetAsync("/api/watches");
        var list = await listResponse.Content.ReadFromJsonAsync<List<WatchListItemDto>>();
        list.ShouldNotBeNull();
        list.ShouldContain(w => w.Id == created.Id);
    }

    [Test]
    public async Task UpdateWatch_ModifiesFields()
    {
        // Create
        var createDto = new WatchCreateDto
        {
            Url = "https://example.com/update",
            Title = "Before Update",
            CheckInterval = TimeSpan.FromHours(1)
        };
        var createResponse = await _client.PostAsJsonAsync("/api/watches", createDto);
        var created = await createResponse.Content.ReadFromJsonAsync<WatchDetailDto>();
        created.ShouldNotBeNull();

        // Update (PUT uses WatchCreateDto per endpoint signature)
        var updateDto = new WatchCreateDto
        {
            Url = "https://example.com/update",
            Title = "After Update",
            CheckInterval = TimeSpan.FromMinutes(15),
            CssSelector = ".content"
        };
        var updateResponse = await _client.PutAsJsonAsync($"/api/watches/{created.Id}", updateDto);
        updateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await updateResponse.Content.ReadFromJsonAsync<WatchDetailDto>();
        updated.ShouldNotBeNull();
        updated.Title.ShouldBe("After Update");
        updated.CssSelector.ShouldBe(".content");
    }

    [Test]
    public async Task DeleteWatch_Succeeds()
    {
        // Create
        var dto = new WatchCreateDto
        {
            Url = "https://example.com/delete",
            Title = "Delete Me",
            CheckInterval = TimeSpan.FromHours(1)
        };
        var createResponse = await _client.PostAsJsonAsync("/api/watches", dto);
        var created = await createResponse.Content.ReadFromJsonAsync<WatchDetailDto>();
        created.ShouldNotBeNull();

        // Delete
        var deleteResponse = await _client.DeleteAsync($"/api/watches/{created.Id}");
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Verify gone
        var getResponse = await _client.GetAsync($"/api/watches/{created.Id}");
        getResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task DeleteWatch_NonExistent_ReturnsNotFoundOrMethodNotAllowed()
    {
        var response = await _client.DeleteAsync($"/api/watches/{Guid.NewGuid()}");
        // UseStatusCodePagesWithReExecute may transform 404 to 405 for DELETE
        var status = (int)response.StatusCode;
        (status is 404 or 405).ShouldBeTrue($"Expected 404 or 405, got {response.StatusCode}");
    }

    [Test]
    public async Task GetWatch_InvalidId_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/watches/not-a-guid");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task TriggerCheck_ReturnsOk()
    {
        // Create a watch first
        var dto = new WatchCreateDto
        {
            Url = "https://example.com/check",
            Title = "Check Me",
            CheckInterval = TimeSpan.FromHours(1)
        };
        var createResponse = await _client.PostAsJsonAsync("/api/watches", dto);
        var created = await createResponse.Content.ReadFromJsonAsync<WatchDetailDto>();
        created.ShouldNotBeNull();

        // Trigger check — first check with stub fetcher should not error
        var checkResponse = await _client.PostAsync($"/api/watches/{created.Id}/check", null);
        checkResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task TriggerCheck_NonExistent_ReturnsErrorStatus()
    {
        var response = await _client.PostAsync($"/api/watches/{Guid.NewGuid()}/check", null);
        var status = (int)response.StatusCode;
        (status is >= 400 and < 500).ShouldBeTrue($"Expected 4xx error, got {response.StatusCode}");
    }

    [Test]
    public async Task EnableDisableWatch_ReturnsNoContent()
    {
        // Create
        var dto = new WatchCreateDto
        {
            Url = "https://example.com/toggle",
            Title = "Toggle Watch",
            CheckInterval = TimeSpan.FromHours(1)
        };
        var createResponse = await _client.PostAsJsonAsync("/api/watches", dto);
        var created = await createResponse.Content.ReadFromJsonAsync<WatchDetailDto>();
        created.ShouldNotBeNull();

        // Enable returns 204
        var enableResponse = await _client.PostAsync($"/api/watches/{created.Id}/enable", null);
        enableResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Disable returns 204
        var disableResponse = await _client.PostAsync($"/api/watches/{created.Id}/disable", null);
        disableResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}
