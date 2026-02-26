using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChangeDetection.Shared.Dtos;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd;

[Category("Integration")]
public class SearchEndpointTests : TestBase, IAsyncDisposable
{
    private TestWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

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

    // --- GET /api/settings/search ---

    [Test]
    public async Task GetSearchSettings_ReturnsOkWithDefaults()
    {
        var response = await _client.GetAsync("/api/settings/search");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var settings = await response.Content.ReadFromJsonAsync<SearchSettingsDto>(JsonOptions);
        settings.ShouldNotBeNull();
        settings.DefaultProvider.ShouldNotBeNullOrEmpty();
        settings.DefaultMaxResults.ShouldBeGreaterThan(0);
        settings.TimeoutSeconds.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task GetSearchSettings_MasksApiKeys()
    {
        var response = await _client.GetAsync("/api/settings/search");
        var settings = await response.Content.ReadFromJsonAsync<SearchSettingsDto>(JsonOptions);
        settings.ShouldNotBeNull();

        // API keys should be null (not configured in test) or masked
        if (settings.GoogleCseApiKey is not null)
            settings.GoogleCseApiKey.ShouldBe("***configured***");
        if (settings.BraveApiKey is not null)
            settings.BraveApiKey.ShouldBe("***configured***");
    }

    // --- PUT /api/settings/search ---

    [Test]
    public async Task UpdateSearchSettings_ReturnsOk()
    {
        var dto = new SearchSettingsDto
        {
            DefaultProvider = "brave",
            DefaultMaxResults = 10,
            TimeoutSeconds = 15
        };

        var response = await _client.PutAsJsonAsync("/api/settings/search", dto, JsonOptions);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<SearchSettingsDto>(JsonOptions);
        result.ShouldNotBeNull();
        result.DefaultProvider.ShouldBe("brave");
        result.DefaultMaxResults.ShouldBe(10);
        result.TimeoutSeconds.ShouldBe(15);
    }

    // --- POST /api/watches/{id}/promote ---

    [Test]
    public async Task PromoteSearchResult_WithValidWatch_ReturnsCreated()
    {
        // First create a search watch to promote from
        var watchId = await CreateSearchWatchAsync();

        var promoteDto = new PromoteSearchResultDto
        {
            Url = "https://example.com/promoted-page",
            Name = "Promoted Watch"
        };

        var response = await _client.PostAsJsonAsync($"/api/watches/{watchId}/promote", promoteDto);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var created = await response.Content.ReadFromJsonAsync<WatchDetailDto>(JsonOptions);
        created.ShouldNotBeNull();
        created.Url.ShouldBe("https://example.com/promoted-page");
        created.Title.ShouldBe("Promoted Watch");
        created.SourceType.ShouldBe(SourceTypeDto.Url);
    }

    [Test]
    public async Task PromoteSearchResult_WithCssSelector_IncludesSelector()
    {
        var watchId = await CreateSearchWatchAsync();

        var promoteDto = new PromoteSearchResultDto
        {
            Url = "https://example.com/with-selector",
            Name = "Selector Watch",
            CssSelector = "div.main-content"
        };

        var response = await _client.PostAsJsonAsync($"/api/watches/{watchId}/promote", promoteDto);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var created = await response.Content.ReadFromJsonAsync<WatchDetailDto>(JsonOptions);
        created.ShouldNotBeNull();
        created.CssSelector.ShouldBe("div.main-content");
    }

    [Test]
    public async Task PromoteSearchResult_WithCheckInterval_UsesCustomInterval()
    {
        var watchId = await CreateSearchWatchAsync();

        var promoteDto = new PromoteSearchResultDto
        {
            Url = "https://example.com/custom-interval",
            CheckIntervalMinutes = 15
        };

        var response = await _client.PostAsJsonAsync($"/api/watches/{watchId}/promote", promoteDto);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var created = await response.Content.ReadFromJsonAsync<WatchDetailDto>(JsonOptions);
        created.ShouldNotBeNull();
        created.CheckInterval.ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Test]
    public async Task PromoteSearchResult_EmptyUrl_ReturnsBadRequest()
    {
        var watchId = await CreateSearchWatchAsync();

        var promoteDto = new PromoteSearchResultDto
        {
            Url = ""
        };

        var response = await _client.PostAsJsonAsync($"/api/watches/{watchId}/promote", promoteDto);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task PromoteSearchResult_NonExistentWatch_ReturnsErrorStatus()
    {
        var fakeId = Guid.NewGuid();
        var promoteDto = new PromoteSearchResultDto
        {
            Url = "https://example.com/orphan"
        };

        var response = await _client.PostAsJsonAsync($"/api/watches/{fakeId}/promote", promoteDto);
        // Non-existent parent watch returns an error status (400 or 404)
        response.IsSuccessStatusCode.ShouldBeFalse();
    }

    [Test]
    public async Task PromoteSearchResult_TagsLinkToParent()
    {
        var watchId = await CreateSearchWatchAsync();

        var promoteDto = new PromoteSearchResultDto
        {
            Url = "https://example.com/tagged",
            Name = "Tagged Watch"
        };

        var response = await _client.PostAsJsonAsync($"/api/watches/{watchId}/promote", promoteDto);
        var created = await response.Content.ReadFromJsonAsync<WatchDetailDto>(JsonOptions);
        created.ShouldNotBeNull();
        created.Tags.ShouldContain(t => t.Name.Contains($"promoted-from:{watchId}"));
    }

    // --- Create search watch helper ---

    [Test]
    public async Task CreateSearchWatch_WithSearchConfig_ReturnsCreated()
    {
        var dto = new WatchCreateDto
        {
            Url = "https://search.example.com",
            Title = "Search Watch",
            CheckInterval = TimeSpan.FromHours(1),
            SourceType = SourceTypeDto.Search,
            SearchConfig = new SearchConfigDto
            {
                Query = "test query",
                ProviderId = "searxng",
                MaxResults = 10
            }
        };

        var response = await _client.PostAsJsonAsync("/api/watches", dto);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var created = await response.Content.ReadFromJsonAsync<WatchDetailDto>(JsonOptions);
        created.ShouldNotBeNull();
        created.SourceType.ShouldBe(SourceTypeDto.Search);
    }

    private async Task<string> CreateSearchWatchAsync()
    {
        var dto = new WatchCreateDto
        {
            Url = "https://search.example.com",
            Title = "Parent Search Watch",
            CheckInterval = TimeSpan.FromHours(1),
            SourceType = SourceTypeDto.Search,
            SearchConfig = new SearchConfigDto
            {
                Query = "integration test query",
                MaxResults = 10
            }
        };

        var response = await _client.PostAsJsonAsync("/api/watches", dto);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<WatchDetailDto>(JsonOptions);
        return created!.Id;
    }
}
