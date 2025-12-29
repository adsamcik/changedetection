namespace ChangeDetection.Tests.Llm;

/// <summary>
/// A simple IHttpClientFactory implementation for testing that uses a custom handler.
/// </summary>
public class MockHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler);
}
