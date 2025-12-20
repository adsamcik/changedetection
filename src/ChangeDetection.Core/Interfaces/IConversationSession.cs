namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Manages in-memory conversation sessions for the interactive watch setup flow.
/// Sessions have a sliding expiration and are never persisted.
/// </summary>
public interface IConversationSessionManager
{
    /// <summary>
    /// Creates a new conversation session.
    /// </summary>
    ConversationSession CreateSession();

    /// <summary>
    /// Creates or retrieves a conversation session with a specific ID.
    /// If a session with this ID already exists and is not expired, it is returned.
    /// Otherwise, a new session is created with the specified ID.
    /// This allows clients to pre-generate session IDs for page refresh resilience.
    /// </summary>
    ConversationSession GetOrCreateSession(Guid sessionId);

    /// <summary>
    /// Gets an existing session by ID, returning null if not found or expired.
    /// </summary>
    ConversationSession? GetSession(Guid sessionId);

    /// <summary>
    /// Updates a session (resets the sliding expiration).
    /// </summary>
    void UpdateSession(ConversationSession session);

    /// <summary>
    /// Removes a session explicitly.
    /// </summary>
    void RemoveSession(Guid sessionId);

    /// <summary>
    /// Gets count of active sessions.
    /// </summary>
    int ActiveSessionCount { get; }

    /// <summary>
    /// Gets all sessions that are currently awaiting user input.
    /// </summary>
    IReadOnlyList<ConversationSession> GetSessionsAwaitingInput();

    /// <summary>
    /// Gets all active sessions that should be visible to users.
    /// This includes sessions awaiting input AND sessions with pending input (being processed).
    /// </summary>
    IReadOnlyList<ConversationSession> GetAllActiveSessions();

    /// <summary>
    /// Event fired when a session expires due to inactivity.
    /// Subscribers can use this to clean up related resources.
    /// </summary>
    event Action<Guid>? SessionExpired;
}

/// <summary>
/// In-memory conversation session for interactive watch setup.
/// Contains message history, agent outputs, and partial configuration.
/// </summary>
public class ConversationSession
{
    /// <summary>
    /// Unique session identifier.
    /// Can be set to a pre-generated ID for page refresh resilience.
    /// </summary>
    public required Guid SessionId { get; init; }

    /// <summary>
    /// When the session was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Last activity time (for sliding expiration).
    /// </summary>
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Original user inputs preserved for validation.
    /// Used to verify extracted values aren't hallucinated.
    /// </summary>
    public List<string> OriginalInputs { get; init; } = [];

    /// <summary>
    /// Message history in the conversation.
    /// </summary>
    public List<ConversationMessage> Messages { get; init; } = [];

    /// <summary>
    /// Partial watch configuration being built up.
    /// </summary>
    public PartialWatchConfiguration Configuration { get; init; } = new();

    /// <summary>
    /// Options that have been presented to the user.
    /// Used for input-anchored validation of user selections.
    /// </summary>
    public List<PresentedOption> PresentedOptions { get; init; } = [];

    /// <summary>
    /// Current stage of the setup flow.
    /// </summary>
    public SetupStage CurrentStage { get; set; } = SetupStage.Initial;

    /// <summary>
    /// Whether the session is waiting for user input.
    /// </summary>
    public bool AwaitingUserInput { get; set; }

    /// <summary>
    /// The current question/prompt awaiting user response.
    /// </summary>
    public string? CurrentPrompt { get; set; }

    /// <summary>
    /// A display name for this session (typically the URL being configured).
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Input that was provided when the session was created but hasn't been processed yet.
    /// The hub will process this when the client connects.
    /// </summary>
    public string? PendingInput { get; set; }

    /// <summary>
    /// Whether the session has been sent to the background by the user.
    /// Background sessions continue processing but the user has navigated away.
    /// </summary>
    public bool IsBackgrounded { get; set; }

    /// <summary>
    /// The current pipeline stage for display purposes when backgrounded.
    /// </summary>
    public string? CurrentPipelineStage { get; set; }

    /// <summary>
    /// Touch the session to update last activity time.
    /// </summary>
    public void Touch() => LastActivityAt = DateTimeOffset.UtcNow;

    /// <summary>
    /// Add a user message and preserve the original input.
    /// </summary>
    public void AddUserMessage(string content)
    {
        Messages.Add(new ConversationMessage(MessageRole.User, content));
        OriginalInputs.Add(content);
        Touch();
    }

    /// <summary>
    /// Add an assistant message.
    /// </summary>
    public void AddAssistantMessage(string content)
    {
        Messages.Add(new ConversationMessage(MessageRole.Assistant, content));
        Touch();
    }

    /// <summary>
    /// Record an option that was presented to the user.
    /// </summary>
    public void RecordPresentedOption(string optionId, string displayText, object? value = null)
    {
        PresentedOptions.Add(new PresentedOption(optionId, displayText, value));
    }
}

/// <summary>
/// A message in the conversation.
/// </summary>
/// <param name="Role">Who sent the message.</param>
/// <param name="Content">Message content.</param>
public record ConversationMessage(MessageRole Role, string Content)
{
    /// <summary>
    /// When the message was sent.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Role in the conversation.
/// </summary>
public enum MessageRole
{
    User,
    Assistant,
    System
}

/// <summary>
/// An option that was presented to the user.
/// Used for input-anchored validation.
/// </summary>
/// <param name="OptionId">Unique identifier for this option.</param>
/// <param name="DisplayText">Text shown to the user.</param>
/// <param name="Value">Associated value if any.</param>
public record PresentedOption(string OptionId, string DisplayText, object? Value);

/// <summary>
/// Partial watch configuration being built up during setup.
/// </summary>
public class PartialWatchConfiguration
{
    public string? Url { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? CssSelector { get; set; }
    public string? XPathSelector { get; set; }
    public bool? UseJavaScript { get; set; }
    public TimeSpan? CheckInterval { get; set; }
    public List<string> Tags { get; init; } = [];
    public List<string> IgnorePatterns { get; init; } = [];
    public Dictionary<string, string> TagColors { get; init; } = [];
    public Guid? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public float? OverallConfidence { get; set; }

    /// <summary>
    /// Schema discovered by LLM for structured object extraction.
    /// Null if content is not list-type or schema discovery was skipped.
    /// </summary>
    public DiscoveredSchema? DiscoveredSchema { get; set; }

    /// <summary>
    /// Whether schema extraction should be enabled for this watch.
    /// Null means not yet determined (will be set based on content type).
    /// </summary>
    public bool? SchemaEnabled { get; set; }

    /// <summary>
    /// Identity fields inferred by LLM (can be overridden by user).
    /// </summary>
    public List<string> InferredIdentityFields { get; init; } = [];

    /// <summary>
    /// Whether minimum required fields are set.
    /// </summary>
    public bool HasMinimumConfiguration => !string.IsNullOrEmpty(Url);

    /// <summary>
    /// Whether the configuration is complete enough to create a watch.
    /// </summary>
    public bool IsComplete => HasMinimumConfiguration;
}

/// <summary>
/// Stages of the interactive setup flow.
/// </summary>
public enum SetupStage
{
    /// <summary>
    /// Initial state, waiting for user input.
    /// </summary>
    Initial,

    /// <summary>
    /// Processing user input with agents.
    /// </summary>
    Processing,

    /// <summary>
    /// Asking user to select from multiple URLs.
    /// </summary>
    UrlSelection,

    /// <summary>
    /// Fetching content from the URL.
    /// </summary>
    Fetching,

    /// <summary>
    /// Analyzing page content.
    /// </summary>
    Analyzing,

    /// <summary>
    /// Asking user to confirm or refine selectors.
    /// </summary>
    SelectorRefinement,

    /// <summary>
    /// LLM is discovering schema for structured object extraction.
    /// Auto-triggered for list-type content (EventList, ProductListing, etc.).
    /// </summary>
    SchemaDiscovery,

    /// <summary>
    /// User is reviewing and refining the discovered schema.
    /// </summary>
    SchemaRefinement,

    /// <summary>
    /// Final confirmation before creating watch.
    /// </summary>
    Confirmation,

    /// <summary>
    /// Watch created successfully.
    /// </summary>
    Completed,

/// <summary>
/// An error occurred.
/// </summary>
    Error
}

/// <summary>
/// Schema discovered by LLM during setup.
/// Used for structured object extraction from list-type pages.
/// </summary>
public class DiscoveredSchema
{
    /// <summary>
    /// CSS or XPath selector for the repeating item container.
    /// </summary>
    public required string ItemSelector { get; set; }

    /// <summary>
    /// Discovered fields with their selectors and types.
    /// </summary>
    public List<DiscoveredField> Fields { get; init; } = [];

    /// <summary>
    /// Fields inferred to uniquely identify objects (for diff matching).
    /// </summary>
    public List<string> InferredIdentityFields { get; init; } = [];

    /// <summary>
    /// Overall confidence in the schema discovery (0-1).
    /// </summary>
    public float Confidence { get; set; }

    /// <summary>
    /// Number of items found on the page matching the schema.
    /// </summary>
    public int SampleItemCount { get; set; }

    /// <summary>
    /// LLM's explanation of the discovered structure.
    /// </summary>
    public string? Explanation { get; set; }

    /// <summary>
    /// Content type detected (EventList, ProductListing, etc.).
    /// </summary>
    public string? ContentType { get; set; }
}

/// <summary>
/// A field discovered during schema discovery.
/// </summary>
public class DiscoveredField
{
    /// <summary>
    /// Suggested field name.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Inferred field type: String, Date, Url, Number, Image, Html.
    /// </summary>
    public required string Type { get; set; }

    /// <summary>
    /// Selector relative to item container.
    /// </summary>
    public required string Selector { get; set; }

    /// <summary>
    /// Whether the field appears in all items (required).
    /// </summary>
    public bool IsRequired { get; set; }

    /// <summary>
    /// Whether LLM suggests this as an identity field.
    /// </summary>
    public bool IsIdentityField { get; set; }

    /// <summary>
    /// Sample values extracted from the page.
    /// </summary>
    public List<string> SampleValues { get; init; } = [];

    /// <summary>
    /// Confidence in this field's detection (0-1).
    /// </summary>
    public float Confidence { get; set; }

    /// <summary>
    /// LLM's explanation for this field.
    /// </summary>
    public string? Reasoning { get; set; }
}