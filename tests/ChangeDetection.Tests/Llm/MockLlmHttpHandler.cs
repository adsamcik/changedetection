using System.Net;
using System.Text;
using System.Text.Json;

namespace ChangeDetection.Tests.Llm;

/// <summary>
/// A mock HTTP handler that simulates OpenAI-compatible API responses.
/// Use this to test LLM integration without requiring a real LLM server.
/// 
/// The handler intercepts requests to /v1/chat/completions and returns
/// configured responses, allowing deterministic testing of LLM-dependent code.
/// </summary>
public class MockLlmHttpHandler : HttpMessageHandler
{
    private readonly Queue<MockLlmResponse> _responses = new();
    private readonly List<MockLlmRequest> _capturedRequests = [];
    private MockLlmResponse? _defaultResponse;

    /// <summary>
    /// Gets all captured requests for assertion purposes.
    /// </summary>
    public IReadOnlyList<MockLlmRequest> CapturedRequests => _capturedRequests.AsReadOnly();

    /// <summary>
    /// Sets a default response to use when no queued responses are available.
    /// </summary>
    public MockLlmHttpHandler WithDefaultResponse(string content)
    {
        _defaultResponse = new MockLlmResponse { Content = content };
        return this;
    }

    /// <summary>
    /// Queues a response to be returned for the next request.
    /// Responses are returned in FIFO order.
    /// </summary>
    public MockLlmHttpHandler QueueResponse(string content, int inputTokens = 100, int outputTokens = 50)
    {
        _responses.Enqueue(new MockLlmResponse
        {
            Content = content,
            InputTokens = inputTokens,
            OutputTokens = outputTokens
        });
        return this;
    }

    /// <summary>
    /// Queues an error response.
    /// </summary>
    public MockLlmHttpHandler QueueError(HttpStatusCode statusCode, string errorMessage)
    {
        _responses.Enqueue(new MockLlmResponse
        {
            IsError = true,
            StatusCode = statusCode,
            ErrorMessage = errorMessage
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Capture the request for assertions
        var requestBody = request.Content != null
            ? await request.Content.ReadAsStringAsync(cancellationToken)
            : null;

        _capturedRequests.Add(new MockLlmRequest
        {
            Method = request.Method,
            RequestUri = request.RequestUri,
            Body = requestBody,
            Timestamp = DateTime.UtcNow
        });

        // Handle model listing endpoints (for health checks)
        if (request.RequestUri?.AbsolutePath == "/v1/models")
        {
            return CreateModelsResponse();
        }

        // Handle Ollama-specific endpoints
        if (request.RequestUri?.AbsolutePath == "/api/tags")
        {
            return CreateOllamaTagsResponse();
        }

        if (request.RequestUri?.AbsolutePath == "/api/version")
        {
            return CreateJsonResponse(new { version = "0.1.0" });
        }

        // Handle chat completions
        if (request.RequestUri?.AbsolutePath == "/v1/chat/completions")
        {
            var response = _responses.Count > 0
                ? _responses.Dequeue()
                : _defaultResponse ?? throw new InvalidOperationException(
                    "No mock response configured. Use QueueResponse() or WithDefaultResponse().");

            if (response.IsError)
            {
                return new HttpResponseMessage(response.StatusCode)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { error = new { message = response.ErrorMessage } }),
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return CreateChatCompletionResponse(response);
        }

        // Unknown endpoint
        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"Mock handler: Unknown endpoint {request.RequestUri}")
        };
    }

    private static HttpResponseMessage CreateChatCompletionResponse(MockLlmResponse response)
    {
        var completionResponse = new
        {
            id = $"chatcmpl-{Guid.NewGuid():N}",
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = "mock-model",
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = response.Content
                    },
                    finish_reason = "stop"
                }
            },
            usage = new
            {
                prompt_tokens = response.InputTokens,
                completion_tokens = response.OutputTokens,
                total_tokens = response.InputTokens + response.OutputTokens
            }
        };

        return CreateJsonResponse(completionResponse);
    }

    private static HttpResponseMessage CreateModelsResponse()
    {
        var modelsResponse = new
        {
            @object = "list",
            data = new[]
            {
                new { id = "mock-model", @object = "model", owned_by = "mock" }
            }
        };

        return CreateJsonResponse(modelsResponse);
    }

    private static HttpResponseMessage CreateOllamaTagsResponse()
    {
        var tagsResponse = new
        {
            models = new[]
            {
                new { name = "mock-model", modified_at = DateTime.UtcNow.ToString("O") }
            }
        };

        return CreateJsonResponse(tagsResponse);
    }

    private static HttpResponseMessage CreateJsonResponse(object content)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(content),
                Encoding.UTF8,
                "application/json")
        };
    }
}

/// <summary>
/// Represents a captured request to the mock LLM handler.
/// </summary>
public record MockLlmRequest
{
    public required HttpMethod Method { get; init; }
    public Uri? RequestUri { get; init; }
    public string? Body { get; init; }
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// Extracts the user message from the request body.
    /// </summary>
    public string? GetUserMessage()
    {
        if (string.IsNullOrEmpty(Body)) return null;

        try
        {
            using var doc = JsonDocument.Parse(Body);
            var messages = doc.RootElement.GetProperty("messages");
            foreach (var msg in messages.EnumerateArray())
            {
                if (msg.GetProperty("role").GetString() == "user")
                {
                    return msg.GetProperty("content").GetString();
                }
            }
        }
        catch
        {
            // Ignore parsing errors
        }

        return null;
    }
}

/// <summary>
/// Configuration for a mock LLM response.
/// </summary>
public record MockLlmResponse
{
    public string Content { get; init; } = "";
    public int InputTokens { get; init; } = 100;
    public int OutputTokens { get; init; } = 50;
    public bool IsError { get; init; }
    public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
    public string? ErrorMessage { get; init; }
}
