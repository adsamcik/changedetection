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
    private readonly int _timeoutSeconds;
    private readonly ILogger<CopilotChatCompletionService>? _logger;
    
    /// <summary>
    /// Model attributes exposed to Semantic Kernel.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Attributes { get; }

    public CopilotChatCompletionService(
        CopilotClient client,
        string model,
        ILogger<CopilotChatCompletionService>? logger = null,
        int timeoutSeconds = 60)
    {
        _client = client;
        _model = model;
        _logger = logger;
        _timeoutSeconds = timeoutSeconds;
        Attributes = new Dictionary<string, object?>
        {
            ["ModelId"] = model,
            ["ProviderName"] = "GitHubCopilot"
        };
    }

    /// <summary>
    /// Ensures the client is started and responsive before making requests.
    /// Uses CopilotClient.State (live connection state) instead of a per-instance flag,
    /// since a new CopilotChatCompletionService is created for each request
    /// while the CopilotClient is a singleton.
    /// </summary>
    private async Task EnsureClientStartedAsync(CancellationToken ct)
    {
        // Check the client's live connection state (not a per-instance flag)
        if (_client.State == ConnectionState.Connected)
        {
            _logger?.LogDebug("Copilot client already connected (state: {State})", _client.State);
            return;
        }
        
        _logger?.LogInformation("Copilot client not connected (state: {State}), starting...", _client.State);
        
        using var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await _client.StartAsync(startCts.Token);
        }
        catch (OperationCanceledException) when (startCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Copilot SDK client did not start within 15 seconds (state: {_client.State})");
        }
        
        _logger?.LogInformation("Copilot client started (state: {State})", _client.State);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureClientStartedAsync(cancellationToken);

        // Create a session for this request (disable infinite sessions for simpler lifecycle).
        // AvailableTools = [] disables all tool invocation — this is a pure text-completion
        // service and the model must not invoke web_search, file tools, etc.  Without this
        // restriction gpt-5 attempts tool calls on complex prompts, causing the session to
        // hang (AssistantMessageEvent / SessionIdleEvent never fire).
        _logger?.LogDebug("Creating non-streaming session with model {Model}", _model);
        var session = await _client.CreateSessionAsync(new SessionConfig
        {
            Model = _model,
            Streaming = false,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            OnPermissionRequest = PermissionHandler.ApproveAll,
            AvailableTools = []
        });

        var sessionId = session.SessionId;
        _logger?.LogInformation("Created non-streaming session {SessionId} (model: {Model})", sessionId, _model);
        try
        {
            // Build prompt from chat history
            var prompt = BuildPromptFromHistory(chatHistory);

            // Track response
            string responseContent = "";
            var completionSource = new TaskCompletionSource<string>();

            // Subscribe to events — log ALL events for diagnostics
            var eventCount = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var subscription = session.On(evt =>
            {
                eventCount++;
                _logger?.LogDebug("Session {SessionId} event #{Count} [{Elapsed}ms]: {EventType}",
                    sessionId, eventCount, sw.ElapsedMilliseconds, evt.GetType().Name);

                switch (evt)
                {
                    case AssistantMessageEvent msg:
                        _logger?.LogInformation(
                            "Session {SessionId}: AssistantMessageEvent received ({Length} chars)",
                            sessionId, msg.Data.Content?.Length ?? 0);
                        responseContent = msg.Data.Content ?? "";
                        break;
                    case AssistantReasoningEvent reasoning:
                        _logger?.LogDebug(
                            "Session {SessionId}: AssistantReasoningEvent ({Length} chars)",
                            sessionId, reasoning.Data.Content?.Length ?? 0);
                        break;
                    case SessionIdleEvent:
                        _logger?.LogInformation(
                            "Session {SessionId}: SessionIdleEvent — completing with {Length} chars",
                            sessionId, responseContent.Length);
                        completionSource.TrySetResult(responseContent);
                        break;
                    case SessionErrorEvent err:
                        _logger?.LogError(
                            "Session {SessionId}: SessionErrorEvent — {Message}",
                            sessionId, err.Data.Message);
                        completionSource.TrySetException(
                            new InvalidOperationException($"Copilot session error: {err.Data.Message}"));
                        break;
                    case ToolExecutionStartEvent:
                        _logger?.LogDebug(
                            "Session {SessionId}: Tool execution started",
                            sessionId);
                        break;
                    case ToolExecutionCompleteEvent:
                        _logger?.LogDebug(
                            "Session {SessionId}: Tool execution completed",
                            sessionId);
                        break;
                    default:
                        // Log but don't treat as error — SDK adds new event types regularly
                        _logger?.LogTrace(
                            "Session {SessionId}: Event type {EventType} (not handled explicitly)",
                            sessionId, evt.GetType().Name);
                        break;
                }
            });

            // Handle cancellation
            cancellationToken.Register(() =>
            {
                _logger?.LogWarning(
                    "Session {SessionId}: Cancellation requested after {Elapsed}ms, {Events} events received",
                    sessionId, sw.ElapsedMilliseconds, eventCount);
                completionSource.TrySetCanceled(cancellationToken);
            });

            // Send the message
            _logger?.LogDebug("Session {SessionId}: Sending prompt ({Length} chars, model: {Model})",
                sessionId, prompt.Length, _model);
            await session.SendAsync(new MessageOptions { Prompt = prompt });
            _logger?.LogDebug("Session {SessionId}: SendAsync completed, awaiting events", sessionId);

            // Wait for completion with configurable timeout
            var responseTask = completionSource.Task;
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(_timeoutSeconds), cancellationToken);
            var completed = await Task.WhenAny(responseTask, timeoutTask);
            if (completed == timeoutTask)
            {
                _logger?.LogWarning(
                    "Session {SessionId}: TIMEOUT after {Timeout}s — {Events} events received, responseContent={HasContent}",
                    sessionId, _timeoutSeconds, eventCount, responseContent.Length > 0);
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException(
                    $"Copilot SDK did not respond within {_timeoutSeconds} seconds. " +
                    $"Events received: {eventCount}, model: {_model}, " +
                    $"client state: {_client.State}");
            }

            var result = await responseTask;

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
                _logger?.LogWarning(ex, "Failed to delete Copilot session {SessionId}", sessionId);
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

        // Create a streaming session (disable infinite sessions for simpler lifecycle).
        // AvailableTools = [] — same rationale as the non-streaming path above.
        _logger?.LogDebug("Creating streaming session with model {Model}", _model);
        var session = await _client.CreateSessionAsync(new SessionConfig
        {
            Model = _model,
            Streaming = true,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            OnPermissionRequest = PermissionHandler.ApproveAll,
            AvailableTools = []
        });

        var sessionId = session.SessionId;
        _logger?.LogInformation("Created streaming session {SessionId} (model: {Model})", sessionId, _model);
        try
        {
            // Build prompt from chat history
            var prompt = BuildPromptFromHistory(chatHistory);

            // Use a channel for streaming
            var channel = System.Threading.Channels.Channel.CreateUnbounded<StreamingChatMessageContent>();
            var writer = channel.Writer;

            // Subscribe to streaming events — log ALL events for diagnostics
            var streamEventCount = 0;
            var streamSw = System.Diagnostics.Stopwatch.StartNew();
            using var subscription = session.On(evt =>
            {
                streamEventCount++;
                _logger?.LogDebug("Streaming session {SessionId} event #{Count} [{Elapsed}ms]: {EventType}",
                    sessionId, streamEventCount, streamSw.ElapsedMilliseconds, evt.GetType().Name);

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
                        _logger?.LogInformation(
                            "Streaming session {SessionId}: SessionIdleEvent after {Events} events",
                            sessionId, streamEventCount);
                        writer.TryComplete();
                        break;
                    case SessionErrorEvent err:
                        _logger?.LogError(
                            "Streaming session {SessionId}: SessionErrorEvent — {Message}",
                            sessionId, err.Data.Message);
                        writer.TryComplete(
                            new InvalidOperationException($"Copilot session error: {err.Data.Message}"));
                        break;
                    default:
                        _logger?.LogDebug(
                            "Streaming session {SessionId}: Unhandled event {EventType}",
                            sessionId, evt.GetType().Name);
                        break;
                }
            });

            // Send the message
            _logger?.LogDebug("Streaming session {SessionId}: Sending prompt ({Length} chars)",
                sessionId, prompt.Length);
            await session.SendAsync(new MessageOptions { Prompt = prompt });
            _logger?.LogDebug("Streaming session {SessionId}: SendAsync completed, awaiting stream", sessionId);

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
                _logger?.LogWarning(ex, "Failed to delete Copilot session {SessionId}", sessionId);
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
