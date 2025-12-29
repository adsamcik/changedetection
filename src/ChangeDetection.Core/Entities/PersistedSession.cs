using System.Text.Json;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Core.Entities;

/// <summary>
/// Persisted conversation session for resuming setup wizards after restart.
/// Stores the session state as JSON for flexibility.
/// </summary>
public class PersistedSession : IOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The session ID (matches ConversationSession.SessionId).
    /// </summary>
    public required Guid SessionId { get; set; }

    /// <summary>
    /// The ID of the user who owns this session.
    /// </summary>
    public Guid OwnerId { get; set; } = Guid.Empty;

    /// <summary>
    /// Display name for this session (typically the URL being configured).
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Current stage of the setup flow.
    /// </summary>
    public SetupStage CurrentStage { get; set; } = SetupStage.Initial;

    /// <summary>
    /// The current prompt awaiting user response.
    /// </summary>
    public string? CurrentPrompt { get; set; }

    /// <summary>
    /// Whether the session is waiting for user input.
    /// </summary>
    public bool AwaitingUserInput { get; set; }

    /// <summary>
    /// The current pipeline stage for display.
    /// </summary>
    public string? CurrentPipelineStage { get; set; }

    /// <summary>
    /// Whether the pipeline was actively processing when persisted.
    /// Note: On restoration, this is always set to false since processing cannot resume.
    /// </summary>
    public bool IsActivelyProcessing { get; set; }

    /// <summary>
    /// When the session was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Last activity time.
    /// </summary>
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// JSON-serialized conversation messages.
    /// </summary>
    public string MessagesJson { get; set; } = "[]";

    /// <summary>
    /// JSON-serialized original user inputs.
    /// </summary>
    public string OriginalInputsJson { get; set; } = "[]";

    /// <summary>
    /// JSON-serialized partial watch configuration.
    /// </summary>
    public string ConfigurationJson { get; set; } = "{}";

    /// <summary>
    /// JSON-serialized presented options.
    /// </summary>
    public string PresentedOptionsJson { get; set; } = "[]";

    /// <summary>
    /// Pending input that hasn't been processed yet.
    /// </summary>
    public string? PendingInput { get; set; }

    /// <summary>
    /// JSON-serialized flow state history (List&lt;FlowStateEntryDto&gt;).
    /// Preserves the processing history with timestamps for session restore.
    /// </summary>
    public string StateHistoryJson { get; set; } = "[]";

    /// <summary>
    /// Creates a persisted session from an in-memory session.
    /// </summary>
    public static PersistedSession FromSession(ConversationSession session, Guid ownerId)
    {
        return new PersistedSession
        {
            SessionId = session.SessionId,
            OwnerId = ownerId,
            DisplayName = session.DisplayName,
            CurrentStage = session.CurrentStage,
            CurrentPrompt = session.CurrentPrompt,
            AwaitingUserInput = session.AwaitingUserInput,
            CurrentPipelineStage = session.CurrentPipelineStage,
            IsActivelyProcessing = session.IsActivelyProcessing,
            CreatedAt = session.CreatedAt,
            LastActivityAt = session.LastActivityAt,
            MessagesJson = JsonSerializer.Serialize(session.Messages),
            OriginalInputsJson = JsonSerializer.Serialize(session.OriginalInputs),
            ConfigurationJson = JsonSerializer.Serialize(session.Configuration),
            PresentedOptionsJson = JsonSerializer.Serialize(session.PresentedOptions),
            PendingInput = session.PendingInput
        };
    }

    /// <summary>
    /// Restores an in-memory session from this persisted session.
    /// </summary>
    public ConversationSession ToSession()
    {
        var messages = JsonSerializer.Deserialize<List<ConversationMessage>>(MessagesJson) ?? [];
        var originalInputs = JsonSerializer.Deserialize<List<string>>(OriginalInputsJson) ?? [];
        var configuration = JsonSerializer.Deserialize<PartialWatchConfiguration>(ConfigurationJson) ?? new();
        var presentedOptions = JsonSerializer.Deserialize<List<PresentedOption>>(PresentedOptionsJson) ?? [];

        return new ConversationSession
        {
            SessionId = SessionId,
            CreatedAt = CreatedAt,
            LastActivityAt = LastActivityAt,
            DisplayName = DisplayName,
            CurrentStage = CurrentStage,
            CurrentPrompt = CurrentPrompt,
            AwaitingUserInput = AwaitingUserInput,
            CurrentPipelineStage = CurrentPipelineStage,
            PendingInput = PendingInput,
            Messages = { },
            OriginalInputs = { },
            Configuration = { },
            PresentedOptions = { }
        }.WithRestoredData(messages, originalInputs, configuration, presentedOptions);
    }
}

/// <summary>
/// Extension methods for ConversationSession restoration.
/// </summary>
public static class ConversationSessionExtensions
{
    /// <summary>
    /// Restores data from persisted storage into a session.
    /// </summary>
    public static ConversationSession WithRestoredData(
        this ConversationSession session,
        List<ConversationMessage> messages,
        List<string> originalInputs,
        PartialWatchConfiguration configuration,
        List<PresentedOption> presentedOptions)
    {
        session.Messages.AddRange(messages);
        session.OriginalInputs.AddRange(originalInputs);
        
        // Copy configuration properties
        session.Configuration.Url = configuration.Url;
        session.Configuration.Name = configuration.Name;
        session.Configuration.Description = configuration.Description;
        session.Configuration.CssSelector = configuration.CssSelector;
        session.Configuration.XPathSelector = configuration.XPathSelector;
        session.Configuration.UseJavaScript = configuration.UseJavaScript;
        session.Configuration.CheckInterval = configuration.CheckInterval;
        foreach (var tag in configuration.Tags)
            session.Configuration.Tags.Add(tag);
        foreach (var pattern in configuration.IgnorePatterns)
            session.Configuration.IgnorePatterns.Add(pattern);
        foreach (var kvp in configuration.TagColors)
            session.Configuration.TagColors[kvp.Key] = kvp.Value;
        
        session.PresentedOptions.AddRange(presentedOptions);
        
        return session;
    }
}
