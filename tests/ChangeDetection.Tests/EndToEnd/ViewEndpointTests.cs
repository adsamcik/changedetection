using System.Net;
using System.Net.Http.Json;
using ChangeDetection.Shared.Dtos;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd;

[Category("Integration")]
public class ViewEndpointTests : TestBase, IAsyncDisposable
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
    public async Task GetViews_ReturnsBuiltInViews()
    {
        var response = await _client.GetAsync("/api/views");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var views = await response.Content.ReadFromJsonAsync<List<ViewDto>>();
        views.ShouldNotBeNull();
        // Built-in views ("Errors" and "Recently Changed") are seeded on first access
        views.ShouldContain(v => v.Name == "Errors" && v.IsBuiltIn);
        views.ShouldContain(v => v.Name == "Recently Changed" && v.IsBuiltIn);
    }

    [Test]
    public async Task CreateAndGetView_RoundTrips()
    {
        // Create
        var createDto = new ViewCreateDto
        {
            Name = "My Custom View",
            Icon = "🔍",
            SortBy = "LastChecked",
            SortDescending = true,
            Filters = new ViewFiltersDto { ChangedRecently = true }
        };

        var createResponse = await _client.PostAsJsonAsync("/api/views", createDto);
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var created = await createResponse.Content.ReadFromJsonAsync<ViewDto>();
        created.ShouldNotBeNull();
        created.Name.ShouldBe("My Custom View");
        created.Icon.ShouldBe("🔍");
        created.IsBuiltIn.ShouldBeFalse();
        created.Id.ShouldNotBeNullOrEmpty();

        // Get by ID
        var getResponse = await _client.GetAsync($"/api/views/{created.Id}");
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var fetched = await getResponse.Content.ReadFromJsonAsync<ViewDto>();
        fetched.ShouldNotBeNull();
        fetched.Name.ShouldBe("My Custom View");
    }

    [Test]
    public async Task UpdateView_ModifiesFields()
    {
        // Create
        var createDto = new ViewCreateDto { Name = "Original View" };
        var createResponse = await _client.PostAsJsonAsync("/api/views", createDto);
        var created = await createResponse.Content.ReadFromJsonAsync<ViewDto>();
        created.ShouldNotBeNull();

        // Update
        var updateDto = new ViewUpdateDto { Name = "Renamed View", Icon = "✅" };
        var updateResponse = await _client.PutAsJsonAsync($"/api/views/{created.Id}", updateDto);
        updateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await updateResponse.Content.ReadFromJsonAsync<ViewDto>();
        updated.ShouldNotBeNull();
        updated.Name.ShouldBe("Renamed View");
        updated.Icon.ShouldBe("✅");
    }

    [Test]
    public async Task UpdateBuiltInView_ReturnsBadRequest()
    {
        // Built-in view "Errors" has a well-known ID
        var builtInId = "00000000-0000-0000-0000-000000000001";

        // Ensure built-in views are seeded
        await _client.GetAsync("/api/views");

        var updateDto = new ViewUpdateDto { Name = "Hacked" };
        var response = await _client.PutAsJsonAsync($"/api/views/{builtInId}", updateDto);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task DeleteView_Succeeds()
    {
        // Create
        var createDto = new ViewCreateDto { Name = "ToDelete View" };
        var createResponse = await _client.PostAsJsonAsync("/api/views", createDto);
        var created = await createResponse.Content.ReadFromJsonAsync<ViewDto>();
        created.ShouldNotBeNull();

        // Delete
        var deleteResponse = await _client.DeleteAsync($"/api/views/{created.Id}");
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Verify gone
        var getResponse = await _client.GetAsync($"/api/views/{created.Id}");
        getResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task DeleteBuiltInView_ReturnsBadRequest()
    {
        var builtInId = "00000000-0000-0000-0000-000000000001";

        // Ensure built-in views are seeded
        await _client.GetAsync("/api/views");

        var response = await _client.DeleteAsync($"/api/views/{builtInId}");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task GetView_InvalidId_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/views/not-a-guid");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
