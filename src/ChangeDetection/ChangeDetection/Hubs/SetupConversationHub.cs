using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Shared.Dtos;
using Microsoft.AspNetCore.SignalR;

namespace ChangeDetection.Hubs;

/// <summary>
/// SignalR hub for interactive watch setup conversations.
/// Streams pipeline outputs in real-time as they process user input.
/// </summary>
public class SetupConversationHub(
    IConversationSessionManager sessionManager,
    IWatchSetupPipeline pipeline,
    IHostApplicationLifetime applicationLifetime,
    ILogger<SetupConversationHub> logger) : Hub
{
    // In-memory storage for pipeline sessions (keyed by conversation session ID)
    private static readonly ConcurrentDictionary<Guid, PipelineSession> PipelineSessions = new();

    /// <summary>
    /// Starts a new setup conversation session.
    /// </summary>
    public async Task<StartSetupSessionResponse> StartSession()
    {
        var session = sessionManager.CreateSession();
        
        // Add connection to session group for targeted updates
        await Groups.AddToGroupAsync(Context.ConnectionId, $"session-{session.SessionId}");
        
        logger.LogInformation("Started setup session {SessionId} for connection {ConnectionId}", 
            session.SessionId, Context.ConnectionId);

        return new StartSetupSessionResponse
        {
            SessionId = session.SessionId,
            CreatedAt = session.CreatedAt
        };
    }

    /// <summary>
    /// Sends a message in an existing session and streams pipeline state updates.
    /// </summary>
    public async IAsyncEnumerable<FlowStateEntryDto> SendMessage(
        SendSetupMessageRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var conversationSession = sessionManager.GetSession(request.SessionId);
        if (conversationSession == null)
        {
            yield return new FlowStateEntryDto
            {
                Stage = "Error",
                Status = FlowStateStatusDto.Failed,
                Summary = "Session not found or expired. Please start a new session.",
                Timestamp = DateTimeOffset.UtcNow,
                IsCurrentState = true
            };
            yield break;
        }

        logger.LogInformation("Processing message in session {SessionId}: {Message}", 
            request.SessionId, TruncateForLog(request.Message));

        // Get or create pipeline session
        PipelineSessions.TryGetValue(request.SessionId, out var pipelineSession);

        // Stream pipeline updates
        await foreach (var entry in StreamPipelineAsync(request.SessionId, request.Message, pipelineSession, ct))
        {
            yield return entry;
        }
    }

    /// <summary>
    /// Streams pipeline execution as FlowStateEntry updates.
    /// </summary>
    private async IAsyncEnumerable<FlowStateEntryDto> StreamPipelineAsync(
        Guid sessionId,
        string message,
        PipelineSession? existingSession,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Use application shutdown token for pipeline operations instead of connection token.
        // This ensures pipeline operations complete even if the client disconnects.
        // We still check the connection token for yielding results back to the client.
        var pipelineCt = applicationLifetime.ApplicationStopping;
        
        PipelineResult result;
        var options = new PipelineOptions { MaxRecoveryAttempts = 2 };

        if (existingSession != null && existingSession.ExtractedUrls.Count > 0)
        {
            // Clear awaiting input flag since user is responding
            var convSession = sessionManager.GetSession(sessionId);
            if (convSession != null)
            {
                convSession.AwaitingUserInput = false;
                convSession.CurrentPrompt = null;
                sessionManager.UpdateSession(convSession);
            }

            // Continue existing session with feedback
            if (!ct.IsCancellationRequested)
            {
                yield return new FlowStateEntryDto
                {
                    Stage = "Processing",
                    Status = FlowStateStatusDto.InProgress,
                    Summary = "Processing your response...",
                    Timestamp = DateTimeOffset.UtcNow,
                    IsCurrentState = true
                };
            }

            result = await pipeline.ContinueWithFeedbackAsync(existingSession, message, pipelineCt);
        }
        else
        {
            // New pipeline execution
            if (!ct.IsCancellationRequested)
            {
                yield return new FlowStateEntryDto
                {
                    Stage = "UrlExtraction",
                    Status = FlowStateStatusDto.InProgress,
                    Summary = "Extracting URL from input...",
                    Timestamp = DateTimeOffset.UtcNow,
                    IsCurrentState = true
                };
            }

            result = await pipeline.ProcessAsync(message, options, pipelineCt);
        }

        // Store the session for continuation
        PipelineSessions[sessionId] = result.Session;

        // If client disconnected, stop yielding results but pipeline completed successfully
        if (ct.IsCancellationRequested)
            yield break;

        // Emit stage completion states
        // Take a snapshot of the list to avoid concurrent modification during enumeration
        var iterationHistorySnapshot = result.Session.IterationHistory.ToList();
        foreach (var historyEntry in iterationHistorySnapshot)
        {
            yield return new FlowStateEntryDto
            {
                Stage = result.CurrentStage.ToString(),
                Status = FlowStateStatusDto.Completed,
                Summary = TruncateSummary(historyEntry),
                Timestamp = DateTimeOffset.UtcNow,
                IsCurrentState = false,
                Details = historyEntry
            };
        }

        // Handle the result
        if (result.NeedsUserInput)
        {
            // Update conversation session to mark it as awaiting input
            var conversationSession = sessionManager.GetSession(sessionId);
            if (conversationSession != null)
            {
                conversationSession.AwaitingUserInput = true;
                conversationSession.CurrentPrompt = result.UserPrompts.FirstOrDefault();
                conversationSession.DisplayName = result.Session.SelectedUrl?.NormalizedUrl 
                    ?? result.Session.ExtractedUrls.ToList().FirstOrDefault()?.Url 
                    ?? "New Watch";
                sessionManager.UpdateSession(conversationSession);
            }

            // Emit question state
            var inputType = DetermineInputType(result);
            // Take snapshots to avoid concurrent modification during enumeration
            var suggestedOptionsSnapshot = result.SuggestedOptions.ToList();
            yield return new FlowStateEntryDto
            {
                Stage = result.CurrentStage.ToString(),
                Status = FlowStateStatusDto.Question,
                Summary = result.UserPrompts.FirstOrDefault() ?? "Please provide more information",
                Timestamp = DateTimeOffset.UtcNow,
                IsCurrentState = true,
                InputType = inputType,
                Options = suggestedOptionsSnapshot.Select(o => new FlowOptionDto
                {
                    Label = o.Label,
                    Value = o.Value,
                    IsRecommended = o.IsRecommended,
                    Preview = o.Preview
                }).ToList()
            };
        }
        else if (!result.IsSuccess)
        {
            // Attempt recovery
            if (result.Session.RecoveryAttempts < options.MaxRecoveryAttempts)
            {
                if (!ct.IsCancellationRequested)
                {
                    yield return new FlowStateEntryDto
                    {
                        Stage = "Recovery",
                        Status = FlowStateStatusDto.Recovery,
                        Summary = "Attempting to recover...",
                        Timestamp = DateTimeOffset.UtcNow,
                        IsCurrentState = true
                    };
                }

                var recoveryResult = await pipeline.RecoverFromFailureAsync(result.Session, result, options, pipelineCt);
                
                // Update stored session
                PipelineSessions[sessionId] = recoveryResult.Session;

                // If client disconnected, stop yielding results
                if (ct.IsCancellationRequested)
                    yield break;

                // Emit recovery diagnostic
                if (!string.IsNullOrEmpty(recoveryResult.Session.RecoveryDiagnosticContext))
                {
                    yield return new FlowStateEntryDto
                    {
                        Stage = "Recovery",
                        Status = FlowStateStatusDto.Recovery,
                        Summary = recoveryResult.Session.RecoveryDiagnosticContext,
                        Timestamp = DateTimeOffset.UtcNow,
                        IsCurrentState = false
                    };
                }

                if (recoveryResult.IsSuccess)
                {
                    yield return new FlowStateEntryDto
                    {
                        Stage = "Complete",
                        Status = FlowStateStatusDto.Completed,
                        Summary = recoveryResult.Summary ?? "Watch configured successfully",
                        Timestamp = DateTimeOffset.UtcNow,
                        IsCurrentState = true,
                        Details = recoveryResult.FinalConfiguration != null 
                            ? $"URL: {recoveryResult.FinalConfiguration.Url}" 
                            : null
                    };
                }
                else if (recoveryResult.NeedsUserInput)
                {
                    var inputType = DetermineInputType(recoveryResult);
                    yield return new FlowStateEntryDto
                    {
                        Stage = recoveryResult.CurrentStage.ToString(),
                        Status = FlowStateStatusDto.Question,
                        Summary = recoveryResult.UserPrompts.FirstOrDefault() ?? "Please help me understand",
                        Timestamp = DateTimeOffset.UtcNow,
                        IsCurrentState = true,
                        InputType = inputType
                    };
                }
                else
                {
                    yield return new FlowStateEntryDto
                    {
                        Stage = "Failed",
                        Status = FlowStateStatusDto.Failed,
                        Summary = recoveryResult.ErrorMessage ?? "Setup failed after recovery attempts",
                        Timestamp = DateTimeOffset.UtcNow,
                        IsCurrentState = true
                    };
                }
            }
            else
            {
                yield return new FlowStateEntryDto
                {
                    Stage = "Failed",
                    Status = FlowStateStatusDto.Failed,
                    Summary = result.ErrorMessage ?? "Setup failed",
                    Timestamp = DateTimeOffset.UtcNow,
                    IsCurrentState = true
                };
            }
        }
        else if (result.FinalConfiguration != null)
        {
            // Success!
            yield return new FlowStateEntryDto
            {
                Stage = "Complete",
                Status = FlowStateStatusDto.Completed,
                Summary = result.Summary ?? "Watch configured successfully",
                Timestamp = DateTimeOffset.UtcNow,
                IsCurrentState = true,
                Details = $"URL: {result.FinalConfiguration.Url}"
            };
        }
    }

    private static string DetermineInputType(PipelineResult result)
    {
        var prompt = result.UserPrompts.FirstOrDefault() ?? "";
        
        if (prompt.Contains("[YES/NO]", StringComparison.OrdinalIgnoreCase))
            return "yesno";
        if (prompt.Contains("[SELECT:", StringComparison.OrdinalIgnoreCase))
            return "select";
        if (prompt.Contains("[CONFIRM/MODIFY]", StringComparison.OrdinalIgnoreCase))
            return "confirm";
        if (result.SuggestedOptions.Count > 0)
            return "select";
        
        return "text";
    }

    /// <summary>
    /// Gets the current state of a session.
    /// </summary>
    public SetupSessionStateDto? GetSessionState(Guid sessionId)
    {
        var session = sessionManager.GetSession(sessionId);
        if (session == null)
            return null;

        PipelineSessions.TryGetValue(sessionId, out var pipelineSession);

        return new SetupSessionStateDto
        {
            SessionId = session.SessionId,
            Stage = pipelineSession?.BestSelector != null ? "Complete" : "InProgress",
            AwaitingInput = session.AwaitingUserInput,
            CurrentPrompt = session.CurrentPrompt,
            Configuration = pipelineSession?.BestSelector != null 
                ? new PartialWatchConfigurationDto
                {
                    Url = pipelineSession.SelectedUrl?.NormalizedUrl,
                    CssSelector = pipelineSession.BestSelector.Type == SelectorType.CssSelector 
                        ? pipelineSession.BestSelector.Selector : null,
                    XPathSelector = pipelineSession.BestSelector.Type == SelectorType.XPath 
                        ? pipelineSession.BestSelector.Selector : null
                }
                : null,
            PresentedOptions = []
        };
    }

    /// <summary>
    /// Confirms the current configuration and creates the watch.
    /// </summary>
    public Task<SetupCompletedDto> ConfirmSetup(Guid sessionId, CancellationToken ct = default)
    {
        PipelineSessions.TryGetValue(sessionId, out var pipelineSession);

        if (pipelineSession?.SelectedUrl == null)
        {
            return Task.FromResult(new SetupCompletedDto
            {
                SessionId = sessionId,
                Summary = "Configuration is incomplete. Please provide the required information."
            });
        }

        logger.LogInformation("Setup confirmed for session {SessionId}, URL: {Url}", 
            sessionId, pipelineSession.SelectedUrl.NormalizedUrl);

        return Task.FromResult(new SetupCompletedDto
        {
            SessionId = sessionId,
            Configuration = new PartialWatchConfigurationDto
            {
                Url = pipelineSession.SelectedUrl.NormalizedUrl,
                Name = pipelineSession.FetchedContent?.Title,
                Description = pipelineSession.ContentAnalysis?.UserIntent,
                CssSelector = pipelineSession.BestSelector?.Type == SelectorType.CssSelector 
                    ? pipelineSession.BestSelector.Selector : null,
                XPathSelector = pipelineSession.BestSelector?.Type == SelectorType.XPath 
                    ? pipelineSession.BestSelector.Selector : null,
                UseJavaScript = pipelineSession.FetchedContent?.UsedJavaScript ?? false
            },
            Summary = $"Watch configured for {pipelineSession.SelectedUrl.NormalizedUrl}."
        });
    }

    /// <summary>
    /// Ends a session explicitly.
    /// </summary>
    public async Task EndSession(Guid sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"session-{sessionId}");
        sessionManager.RemoveSession(sessionId);
        
        PipelineSessions.TryRemove(sessionId, out _);
        
        logger.LogInformation("Ended session {SessionId}", sessionId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogInformation("Client disconnected from setup hub: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    private static string TruncateForLog(string text, int maxLength = 100)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..maxLength] + "...";
    }

    private static string TruncateSummary(string text, int maxLength = 80)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..maxLength] + "...";
    }
}

// FlowStateEntryDto, FlowStateStatusDto, FlowOptionDto are defined in ChangeDetection.Shared.Dtos.PipelineDtos
