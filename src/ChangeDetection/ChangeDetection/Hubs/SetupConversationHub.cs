using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Authentication;
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
    IWatchService watchService,
    IHubContext<ChangeDetectionHub> changeHubContext,
    IUserContext userContext,
    IHostApplicationLifetime applicationLifetime,
    ILogger<SetupConversationHub> logger) : Hub
{
    // In-memory storage for pipeline sessions (keyed by conversation session ID)
    private static readonly ConcurrentDictionary<Guid, PipelineSession> PipelineSessions = new();
    
    // In-memory storage for flow state history (for session resume on page reload)
    private static readonly ConcurrentDictionary<Guid, List<FlowStateEntryDto>> SessionStateHistory = new();

    /// <summary>
    /// Gets the dashboard group name for the current user.
    /// Matches the strategy used by <see cref="ChangeDetectionHub"/>.
    /// </summary>
    private string GetUserDashboardGroup()
        => userContext.CurrentUserId == Guid.Empty
            ? "dashboard"
            : $"dashboard-{userContext.CurrentUserId}";

    /// <summary>
    /// Gets the current count of tracked pipeline sessions for diagnostics.
    /// </summary>
    internal static int PipelineSessionCount => PipelineSessions.Count;

    /// <summary>
    /// Gets the current count of tracked state histories for diagnostics.
    /// </summary>
    internal static int StateHistoryCount => SessionStateHistory.Count;

    /// <summary>
    /// Cleans up static dictionary entries for an expired session.
    /// Called by SetupSessionCleanupService when sessions expire in ConversationSessionManager.
    /// </summary>
    /// <returns>True if any entries were removed, false otherwise.</returns>
    internal static bool CleanupExpiredSession(Guid sessionId)
    {
        var pipelineRemoved = PipelineSessions.TryRemove(sessionId, out _);
        var historyRemoved = SessionStateHistory.TryRemove(sessionId, out _);
        return pipelineRemoved || historyRemoved;
    }

    /// <summary>
    /// Performs defensive cleanup by removing entries for sessions that no longer exist in the session manager.
    /// This catches any orphaned entries that might have been missed by the normal expiration event.
    /// </summary>
    /// <param name="sessionManager">The session manager to check against.</param>
    /// <returns>The number of orphaned sessions cleaned up.</returns>
    internal static int DefensiveCleanup(IConversationSessionManager sessionManager)
    {
        var cleanedUp = 0;
        
        // Check pipeline sessions
        foreach (var sessionId in PipelineSessions.Keys.ToList())
        {
            if (sessionManager.GetSession(sessionId) == null)
            {
                if (PipelineSessions.TryRemove(sessionId, out _))
                {
                    cleanedUp++;
                }
            }
        }
        
        // Check state history
        foreach (var sessionId in SessionStateHistory.Keys.ToList())
        {
            if (sessionManager.GetSession(sessionId) == null)
            {
                if (SessionStateHistory.TryRemove(sessionId, out _))
                {
                    cleanedUp++;
                }
            }
        }
        
        return cleanedUp;
    }

    /// <summary>
    /// Records a state entry in history and returns it for yielding.
    /// </summary>
    private static FlowStateEntryDto RecordStateEntry(Guid sessionId, FlowStateEntryDto entry)
    {
        var history = SessionStateHistory.GetOrAdd(sessionId, _ => []);
        
        lock (history)
        {
            // Mark all previous entries as not current
            foreach (var existing in history)
            {
                existing.IsCurrentState = false;
            }
            
            history.Add(entry);
        }
        
        return entry;
    }

    /// <summary>
    /// Gets the state history for a session.
    /// </summary>
    public List<FlowStateEntryDto> GetSessionHistory(Guid sessionId)
    {
        if (SessionStateHistory.TryGetValue(sessionId, out var history))
        {
            lock (history)
            {
                return [.. history]; // Return a copy
            }
        }
        return [];
    }

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
    /// Starts or resumes a session with a specific ID.
    /// This enables page refresh resilience by allowing the client to pre-generate the session ID.
    /// If the session has pending input (from the start-setup endpoint), it will be returned for processing.
    /// </summary>
    /// <param name="sessionId">The session ID to use (typically pre-generated by the client).</param>
    /// <returns>Session response with status indicating whether this is a new or resumed session.</returns>
    public async Task<StartSetupSessionResponse> StartOrResumeSession(Guid sessionId)
    {
        var existingSession = sessionManager.GetSession(sessionId);
        var isResuming = existingSession != null;
        
        // Get or create the session with the specified ID
        var session = sessionManager.GetOrCreateSession(sessionId);
        
        // Add connection to session group for targeted updates
        await Groups.AddToGroupAsync(Context.ConnectionId, $"session-{session.SessionId}");
        
        // Check for pending input that was set by the start-setup endpoint
        var pendingInput = session.PendingInput;
        if (!string.IsNullOrEmpty(pendingInput))
        {
            // Clear the pending input so it's only processed once
            session.PendingInput = null;
            sessionManager.UpdateSession(session);
        }
        
        logger.LogInformation("{Action} setup session {SessionId} for connection {ConnectionId}, PendingInput: {HasPending}", 
            isResuming ? "Resumed" : "Started new", session.SessionId, Context.ConnectionId, !string.IsNullOrEmpty(pendingInput));

        return new StartSetupSessionResponse
        {
            SessionId = session.SessionId,
            CreatedAt = session.CreatedAt,
            IsResumed = isResuming,
            HasPipelineState = isResuming && (PipelineSessions.ContainsKey(sessionId) || SessionStateHistory.ContainsKey(sessionId)),
            PendingInput = pendingInput  // Return pending input for client to process
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
            yield return RecordStateEntry(request.SessionId, new FlowStateEntryDto
            {
                Stage = "Error",
                Status = FlowStateStatusDto.Failed,
                Summary = "Session not found or expired. Please start a new session.",
                Timestamp = DateTimeOffset.UtcNow,
                IsCurrentState = true
            });
            yield break;
        }

        logger.LogInformation("Processing message in session {SessionId}: {Message}", 
            request.SessionId, TruncateForLog(request.Message));

        // Get or create pipeline session
        PipelineSessions.TryGetValue(request.SessionId, out var pipelineSession);

        // Stream pipeline updates with exception handling
        // Note: We can't use try-catch around yield return, so we use a channel-based approach
        var channel = System.Threading.Channels.Channel.CreateUnbounded<FlowStateEntryDto>();
        
        var streamTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var entry in StreamPipelineAsync(request.SessionId, request.Message, pipelineSession, ct))
                {
                    await channel.Writer.WriteAsync(entry, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when client disconnects
                logger.LogDebug("Pipeline streaming cancelled for session {SessionId}", request.SessionId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Pipeline streaming failed for session {SessionId}", request.SessionId);
                
                // Write error entry before completing
                await channel.Writer.WriteAsync(new FlowStateEntryDto
                {
                    Stage = "Failed",
                    Status = FlowStateStatusDto.Failed,
                    Summary = $"An error occurred: {ex.Message}",
                    Details = ex.GetType().Name,
                    Timestamp = DateTimeOffset.UtcNow,
                    IsCurrentState = true
                }, CancellationToken.None);
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, ct);

        await foreach (var entry in channel.Reader.ReadAllAsync(ct))
        {
            yield return RecordStateEntry(request.SessionId, entry);
        }

        await streamTask;
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
        
        var options = new PipelineOptions { MaxRecoveryAttempts = 2 };
        PipelineResult? finalResult = null;

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

            // Continue existing session with feedback - stream progress
            await foreach (var progress in pipeline.ContinueWithFeedbackStreamingAsync(existingSession, message, pipelineCt))
            {
                if (ct.IsCancellationRequested)
                    yield break;

                yield return MapProgressToFlowState(progress);
                
                // Capture final result
                if (progress.Result != null)
                {
                    finalResult = progress.Result;
                    PipelineSessions[sessionId] = progress.Result.Session;
                }
            }
        }
        else
        {
            // New pipeline execution - stream progress
            await foreach (var progress in pipeline.ProcessStreamingAsync(message, options, pipelineCt))
            {
                if (ct.IsCancellationRequested)
                    yield break;

                yield return MapProgressToFlowState(progress);
                
                // Capture final result and update session
                if (progress.Result != null)
                {
                    finalResult = progress.Result;
                    PipelineSessions[sessionId] = progress.Result.Session;
                }
                else if (progress.Session != null)
                {
                    PipelineSessions[sessionId] = progress.Session;
                }
            }
        }

        // If client disconnected, stop yielding results but pipeline completed successfully
        if (ct.IsCancellationRequested)
            yield break;

        // Handle the final result
        if (finalResult == null)
        {
            yield return new FlowStateEntryDto
            {
                Stage = "Failed",
                Status = FlowStateStatusDto.Failed,
                Summary = "Pipeline completed without result",
                Timestamp = DateTimeOffset.UtcNow,
                IsCurrentState = true
            };
            yield break;
        }

        if (finalResult.NeedsUserInput)
        {
            // Update conversation session to mark it as awaiting input
            var conversationSession = sessionManager.GetSession(sessionId);
            if (conversationSession != null)
            {
                conversationSession.AwaitingUserInput = true;
                conversationSession.CurrentPrompt = finalResult.UserPrompts.FirstOrDefault();
                conversationSession.DisplayName = finalResult.Session.SelectedUrl?.NormalizedUrl 
                    ?? finalResult.Session.ExtractedUrls.ToList().FirstOrDefault()?.Url 
                    ?? "New Watch";
                sessionManager.UpdateSession(conversationSession);
                
                // Notify dashboard clients that this session is now awaiting input
                await changeHubContext.Clients.Group(GetUserDashboardGroup()).SendAsync("SetupSessionUpdated", new SetupSessionUpdatedEvent(
                    sessionId,
                    conversationSession.DisplayName,
                    conversationSession.CurrentPrompt,
                    IsProcessing: false,
                    IsCompleted: false,
                    IsCancelled: false), pipelineCt);
            }

            // Emit question state
            var inputType = DetermineInputType(finalResult);
            var suggestedOptionsSnapshot = finalResult.SuggestedOptions.ToList();
            yield return new FlowStateEntryDto
            {
                Stage = finalResult.CurrentStage.ToString(),
                Status = FlowStateStatusDto.Question,
                Summary = finalResult.UserPrompts.FirstOrDefault() ?? "Please provide more information",
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
        else if (!finalResult.IsSuccess)
        {
            // Attempt recovery
            if (finalResult.Session.RecoveryAttempts < options.MaxRecoveryAttempts)
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

                var recoveryResult = await pipeline.RecoverFromFailureAsync(finalResult.Session, finalResult, options, pipelineCt);
                
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

                if (recoveryResult.IsSuccess && recoveryResult.FinalConfiguration != null)
                {
                    // Success after recovery! Create the watch in the database
                    WatchedSite? createdWatch = null;
                    string? persistError = null;
                    try
                    {
                        var createRequest = new CreateWatchRequest
                        {
                            Url = recoveryResult.FinalConfiguration.Url,
                            Name = recoveryResult.Session.FetchedContent?.Title ?? recoveryResult.FinalConfiguration.Url,
                            Description = recoveryResult.Session.ContentAnalysis?.UserIntent,
                            CssSelector = recoveryResult.Session.BestSelector?.Type == SelectorType.CssSelector 
                                ? recoveryResult.Session.BestSelector.Selector : null,
                            XPathSelector = recoveryResult.Session.BestSelector?.Type == SelectorType.XPath 
                                ? recoveryResult.Session.BestSelector.Selector : null,
                            FetchSettings = new FetchSettings
                            {
                                UseJavaScript = recoveryResult.Session.FetchedContent?.UsedJavaScript ?? false
                            }
                        };
                        
                        createdWatch = await watchService.CreateWatchAsync(createRequest, pipelineCt);
                        logger.LogInformation("Created watch {WatchId} after recovery for URL {Url}", createdWatch.Id, createRequest.Url);
                        
                        // Broadcast to dashboard group so Home page updates
                        await changeHubContext.Clients.Group(GetUserDashboardGroup()).SendAsync("WatchCreated", new WatchCreatedEvent(
                            createdWatch.Id,
                            createdWatch.Url,
                            createdWatch.Name ?? createdWatch.Url), pipelineCt);
                        
                        // Notify that this setup session is now complete
                        await changeHubContext.Clients.Group(GetUserDashboardGroup()).SendAsync("SetupSessionUpdated", new SetupSessionUpdatedEvent(
                            sessionId,
                            createdWatch.Name ?? createdWatch.Url,
                            CurrentPrompt: null,
                            IsProcessing: false,
                            IsCompleted: true,
                            IsCancelled: false), pipelineCt);
                        
                        // Send the SetupCompleted event with the watch ID
                        await Clients.Caller.SendAsync("SetupCompleted", createdWatch.Id, 
                            $"URL: {recoveryResult.FinalConfiguration.Url}", pipelineCt);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to create watch after recovery for URL {Url}", recoveryResult.FinalConfiguration.Url);
                        persistError = ex.Message;
                    }
                    
                    if (persistError != null)
                    {
                        yield return new FlowStateEntryDto
                        {
                            Stage = "Failed",
                            Status = FlowStateStatusDto.Failed,
                            Summary = $"Failed to save watch: {persistError}",
                            Timestamp = DateTimeOffset.UtcNow,
                            IsCurrentState = true,
                            Details = $"URL: {recoveryResult.FinalConfiguration.Url}"
                        };
                        yield break;
                    }
                    
                    yield return new FlowStateEntryDto
                    {
                        Stage = "Complete",
                        Status = FlowStateStatusDto.Completed,
                        Summary = recoveryResult.Summary ?? "Watch configured successfully",
                        Timestamp = DateTimeOffset.UtcNow,
                        IsCurrentState = true,
                        Details = $"URL: {recoveryResult.FinalConfiguration.Url}",
                        WatchId = createdWatch!.Id
                    };
                }
                else if (recoveryResult.IsSuccess)
                {
                    // Success but no configuration - shouldn't normally happen
                    yield return new FlowStateEntryDto
                    {
                        Stage = "Complete",
                        Status = FlowStateStatusDto.Completed,
                        Summary = recoveryResult.Summary ?? "Watch configured successfully",
                        Timestamp = DateTimeOffset.UtcNow,
                        IsCurrentState = true
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
                    Summary = finalResult.ErrorMessage ?? "Setup failed",
                    Timestamp = DateTimeOffset.UtcNow,
                    IsCurrentState = true
                };
            }
        }
        else if (finalResult.FinalConfiguration != null)
        {
            // Success! Create the watch in the database
            WatchedSite? createdWatch = null;
            string? persistError = null;
            try
            {
                var createRequest = new CreateWatchRequest
                {
                    Url = finalResult.FinalConfiguration.Url,
                    Name = finalResult.Session.FetchedContent?.Title ?? finalResult.FinalConfiguration.Url,
                    Description = finalResult.Session.ContentAnalysis?.UserIntent,
                    CssSelector = finalResult.Session.BestSelector?.Type == SelectorType.CssSelector 
                        ? finalResult.Session.BestSelector.Selector : null,
                    XPathSelector = finalResult.Session.BestSelector?.Type == SelectorType.XPath 
                        ? finalResult.Session.BestSelector.Selector : null,
                    FetchSettings = new FetchSettings
                    {
                        UseJavaScript = finalResult.Session.FetchedContent?.UsedJavaScript ?? false
                    }
                };
                
                createdWatch = await watchService.CreateWatchAsync(createRequest, pipelineCt);
                logger.LogInformation("Created watch {WatchId} for URL {Url}", createdWatch.Id, createRequest.Url);
                
                // Broadcast to dashboard group so Home page updates
                await changeHubContext.Clients.Group(GetUserDashboardGroup()).SendAsync("WatchCreated", new WatchCreatedEvent(
                    createdWatch.Id,
                    createdWatch.Url,
                    createdWatch.Name ?? createdWatch.Url), pipelineCt);
                
                // Notify that this setup session is now complete (should be removed from pending list)
                await changeHubContext.Clients.Group(GetUserDashboardGroup()).SendAsync("SetupSessionUpdated", new SetupSessionUpdatedEvent(
                    sessionId,
                    createdWatch.Name ?? createdWatch.Url,
                    CurrentPrompt: null,
                    IsProcessing: false,
                    IsCompleted: true,
                    IsCancelled: false), pipelineCt);
                
                // Don't remove session or history yet - keep for page reload resilience
                // They will be cleaned up when the session expires or is explicitly ended
                
                // Send the SetupCompleted event with the watch ID
                await Clients.Caller.SendAsync("SetupCompleted", createdWatch.Id, 
                    $"URL: {finalResult.FinalConfiguration.Url}", pipelineCt);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create watch for URL {Url}", finalResult.FinalConfiguration.Url);
                persistError = ex.Message;
            }
            
            if (persistError != null)
            {
                yield return new FlowStateEntryDto
                {
                    Stage = "Failed",
                    Status = FlowStateStatusDto.Failed,
                    Summary = $"Failed to save watch: {persistError}",
                    Timestamp = DateTimeOffset.UtcNow,
                    IsCurrentState = true,
                    Details = $"URL: {finalResult.FinalConfiguration.Url}"
                };
                yield break;
            }
            
            yield return new FlowStateEntryDto
            {
                Stage = "Complete",
                Status = FlowStateStatusDto.Completed,
                Summary = finalResult.Summary ?? "Watch configured successfully",
                Timestamp = DateTimeOffset.UtcNow,
                IsCurrentState = true,
                Details = $"URL: {finalResult.FinalConfiguration.Url}",
                WatchId = createdWatch!.Id
            };
        }
    }

    /// <summary>
    /// Maps a PipelineProgress to a FlowStateEntryDto for client streaming.
    /// </summary>
    private static FlowStateEntryDto MapProgressToFlowState(PipelineProgress progress)
    {
        var status = progress.Type switch
        {
            ProgressType.Starting => FlowStateStatusDto.InProgress,
            ProgressType.Thinking => FlowStateStatusDto.Thinking,
            ProgressType.StageCompleted => FlowStateStatusDto.Completed,
            ProgressType.Failed => FlowStateStatusDto.Failed,
            ProgressType.Completed => FlowStateStatusDto.Completed,
            _ => FlowStateStatusDto.InProgress
        };

        return new FlowStateEntryDto
        {
            Stage = progress.Stage.ToString(),
            Status = status,
            Summary = progress.Summary ?? "Processing...",
            Details = progress.Details,
            Timestamp = progress.Timestamp,
            IsCurrentState = progress.Type == ProgressType.Starting || progress.Type == ProgressType.Failed
        };
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
            Stage = pipelineSession?.BestSelector != null ? "Complete" : (session.CurrentPipelineStage ?? "InProgress"),
            AwaitingInput = session.AwaitingUserInput,
            IsBackgrounded = session.IsBackgrounded,
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
    public async Task<SetupCompletedDto> ConfirmSetup(Guid sessionId, CancellationToken ct = default)
    {
        PipelineSessions.TryGetValue(sessionId, out var pipelineSession);

        if (pipelineSession?.SelectedUrl == null)
        {
            return new SetupCompletedDto
            {
                SessionId = sessionId,
                Summary = "Configuration is incomplete. Please provide the required information."
            };
        }

        logger.LogInformation("Setup confirmed for session {SessionId}, URL: {Url}", 
            sessionId, pipelineSession.SelectedUrl.NormalizedUrl);

        // Create the watch in the database
        try
        {
            var createRequest = new CreateWatchRequest
            {
                Url = pipelineSession.SelectedUrl.NormalizedUrl,
                Name = pipelineSession.FetchedContent?.Title ?? pipelineSession.SelectedUrl.NormalizedUrl,
                Description = pipelineSession.ContentAnalysis?.UserIntent,
                CssSelector = pipelineSession.BestSelector?.Type == SelectorType.CssSelector 
                    ? pipelineSession.BestSelector.Selector : null,
                XPathSelector = pipelineSession.BestSelector?.Type == SelectorType.XPath 
                    ? pipelineSession.BestSelector.Selector : null,
                FetchSettings = new FetchSettings
                {
                    UseJavaScript = pipelineSession.FetchedContent?.UsedJavaScript ?? false
                }
            };
            
            var createdWatch = await watchService.CreateWatchAsync(createRequest, ct);
            logger.LogInformation("Created watch {WatchId} for URL {Url}", createdWatch.Id, createRequest.Url);
            
            // Broadcast to dashboard group so Home page updates
            await changeHubContext.Clients.Group(GetUserDashboardGroup()).SendAsync("WatchCreated", new WatchCreatedEvent(
                createdWatch.Id,
                createdWatch.Url,
                createdWatch.Name ?? createdWatch.Url), ct);
            
            // Notify that this setup session is now complete
            await changeHubContext.Clients.Group(GetUserDashboardGroup()).SendAsync("SetupSessionUpdated", new SetupSessionUpdatedEvent(
                sessionId,
                createdWatch.Name ?? createdWatch.Url,
                CurrentPrompt: null,
                IsProcessing: false,
                IsCompleted: true,
                IsCancelled: false), ct);

            return new SetupCompletedDto
            {
                SessionId = sessionId,
                WatchId = createdWatch.Id,
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
                Summary = $"Watch created for {pipelineSession.SelectedUrl.NormalizedUrl}."
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create watch for URL {Url}", pipelineSession.SelectedUrl.NormalizedUrl);
            return new SetupCompletedDto
            {
                SessionId = sessionId,
                Summary = $"Failed to create watch: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Ends a session explicitly.
    /// </summary>
    public async Task EndSession(Guid sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"session-{sessionId}");
        sessionManager.RemoveSession(sessionId);
        
        PipelineSessions.TryRemove(sessionId, out _);
        SessionStateHistory.TryRemove(sessionId, out _);
        
        logger.LogInformation("Ended session {SessionId}", sessionId);
    }

    /// <summary>
    /// Sends a session to the background. The session continues processing,
    /// but the client is disconnecting and navigating away.
    /// Unlike EndSession, this preserves the session state for later resumption
    /// or completion notification.
    /// </summary>
    public async Task SendToBackground(Guid sessionId)
    {
        var session = sessionManager.GetSession(sessionId);
        if (session == null)
        {
            logger.LogWarning("Attempted to background non-existent session {SessionId}", sessionId);
            return;
        }

        // Mark session as backgrounded
        session.IsBackgrounded = true;
        sessionManager.UpdateSession(session);

        // Remove connection from session group (client is leaving)
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"session-{sessionId}");

        // Get current pipeline stage for display
        PipelineSessions.TryGetValue(sessionId, out var pipelineSession);
        var currentStage = pipelineSession switch
        {
            { BestSelector: not null } => "Validating",
            { ContentAnalysis: not null } => "Generating selectors",
            { FetchedContent: not null } => "Analyzing content",
            { SelectedUrl: not null } => "Fetching page",
            { ExtractedUrls.Count: > 0 } => "URL extracted",
            _ => "Processing"
        };

        session.CurrentPipelineStage = currentStage;
        sessionManager.UpdateSession(session);

        // Broadcast to dashboard that session is now backgrounded
        await changeHubContext.Clients.Group(GetUserDashboardGroup()).SendAsync("SetupSessionUpdated", new SetupSessionUpdatedEvent(
            sessionId,
            session.DisplayName ?? "New Watch",
            CurrentPrompt: session.CurrentPrompt,
            IsProcessing: !session.AwaitingUserInput,
            IsCompleted: false,
            IsCancelled: false,
            IsBackgrounded: true,
            CurrentStage: currentStage));

        logger.LogInformation("Session {SessionId} sent to background, stage: {Stage}", sessionId, currentStage);
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
