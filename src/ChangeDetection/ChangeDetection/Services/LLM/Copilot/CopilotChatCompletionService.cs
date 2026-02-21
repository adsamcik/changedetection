using System.Runtime.CompilerServices;
using GitHub.Copilot.SDK;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChangeDetection.Services.LLM.Copilot;

/// <summary>
/// Adapter that implements Semantic Kernel's IChatCompletionService using the GitHub Copilot SDK.
/// Converts chat history to Copilot session messages and events back to chat responses.
/// </summary>
public class CopilotChatCompletionService : IChatCompletionService
{
    private readonly CopilotClient _client;
    private readonly string _model;
    private readonly ILogger<CopilotChatCompletionService>? _logger;
    private bool _clientStarted;
    
    /// <summary>
    /// Model attributes exposed to Semantic Kernel.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Attributes { get; }

    public CopilotChatCompletionService(
        CopilotClient client,
        string model,
        ILogger<CopilotChatCompletionService>? logger = null)
    {
        _client = client;
        _model = model;
        _logger = logger;
        Attributes = new Dictionary<string, object?>
        {
            ["ModelId"] = model,
            ["ProviderName"] = "GitHubCopilot"
        };
    }

    /// <summary>
    /// Ensures the client is started before making requests.
    /// </summary>
    private async Task EnsureClientStartedAsync(CancellationToken ct)
    {
        if (_clientStarted) return;
        
        await _client.StartAsync();
        _clientStarted = true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureClientStartedAsync(cancellationToken);

        // Create a session for this request (disable infinite sessions for simpler lifecycle)
        var session = await _client.CreateSessionAsync(new SessionConfig
        {
            Model = _model,
            Streaming = false,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false }
        });

        var sessionId = session.SessionId;
        try
        {
            // Build prompt from chat history
            var prompt = BuildPromptFromHistory(chatHistory);

            // Track response
            string responseContent = "";
            var completionSource = new TaskCompletionSource<string>();

            // Subscribe to events
            using var subscription = session.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageEvent msg:
                        responseContent = msg.Data.Content ?? "";
                        break;
                    case SessionIdleEvent:
                        completionSource.TrySetResult(responseContent);
                        break;
                    case SessionErrorEvent err:
                        completionSource.TrySetException(
                            new InvalidOperationException($"Copilot session error: {err.Data.Message}"));
                        break;
                }
            });

            // Handle cancellation
            cancellationToken.Register(() => completionSource.TrySetCanceled(cancellationToken));

            // Send the message
            await session.SendAsync(new MessageOptions { Prompt = prompt });

            // Wait for completion
            var result = await completionSource.Task;

            return
            [
                new ChatMessageContent(AuthorRole.Assistant, result)
                {
                    ModelId = _model
                }
            ];
        }
        finally
        {
            // Dispose session, then delete from disk to prevent orphaned session directories
            await session.DisposeAsync();
            try
            {
                await _client.DeleteSessionAsync(sessionId);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to delete Copilot session {SessionId}", sessionId);
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureClientStartedAsync(cancellationToken);

        // Create a streaming session (disable infinite sessions for simpler lifecycle)
        var session = await _client.CreateSessionAsync(new SessionConfig
        {
            Model = _model,
            Streaming = true,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false }
        });

        var sessionId = session.SessionId;
        try
        {
            // Build prompt from chat history
            var prompt = BuildPromptFromHistory(chatHistory);

            // Use a channel for streaming
            var channel = System.Threading.Channels.Channel.CreateUnbounded<StreamingChatMessageContent>();
            var writer = channel.Writer;

            // Subscribe to streaming events
            using var subscription = session.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta:
                        var content = new StreamingChatMessageContent(
                            AuthorRole.Assistant,
                            delta.Data.DeltaContent)
                        {
                            ModelId = _model
                        };
                        writer.TryWrite(content);
                        break;
                    case SessionIdleEvent:
                        writer.TryComplete();
                        break;
                    case SessionErrorEvent err:
                        writer.TryComplete(
                            new InvalidOperationException($"Copilot session error: {err.Data.Message}"));
                        break;
                }
            });

            // Send the message
            await session.SendAsync(new MessageOptions { Prompt = prompt });

            // Yield streaming content
            await foreach (var content in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return content;
            }
        }
        finally
        {
            // Dispose session, then delete from disk to prevent orphaned session directories
            await session.DisposeAsync();
            try
            {
                await _client.DeleteSessionAsync(sessionId);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to delete Copilot session {SessionId}", sessionId);
            }
        }
    }

    /// <summary>
    /// Converts Semantic Kernel ChatHistory to a single prompt string for Copilot.
    /// </summary>
    private static string BuildPromptFromHistory(ChatHistory chatHistory)
    {
        if (chatHistory.Count == 0)
            return "";

        // If there's only one message, return it directly
        if (chatHistory.Count == 1)
            return chatHistory[0].Content ?? "";

        // Build a structured prompt from the history
        var parts = new List<string>();

        foreach (var message in chatHistory)
        {
            var role = message.Role.ToString();
            var content = message.Content ?? "";

            // Format based on role
            if (message.Role == AuthorRole.System)
            {
                parts.Add($"[System Instructions]\n{content}");
            }
            else if (message.Role == AuthorRole.User)
            {
                parts.Add($"[User]\n{content}");
            }
            else if (message.Role == AuthorRole.Assistant)
            {
                parts.Add($"[Assistant]\n{content}");
            }
        }

        return string.Join("\n\n", parts);
    }
}
