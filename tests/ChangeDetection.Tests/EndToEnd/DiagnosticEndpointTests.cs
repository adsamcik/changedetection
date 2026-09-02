using System.Net;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd;

[Category("Integration")]
public class DiagnosticEndpointTests : IAsyncDisposable
{
    private TestWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [Before(Test)]
    public void Setup()
    {
        _factory = new TestWebApplicationFactory();
        _client = _factory.CreateClient();
    }

    [Test]
    public async Task DiTrace_SingleUserMode_NonLoopbackRequestIsHidden()
    {
        var response = await _client.GetAsync("/api/diag/di-trace");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }
}
