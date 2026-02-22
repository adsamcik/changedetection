using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
    IServiceScopeFactory scopeFactory,
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
    /// Also persists the history to durable storage asynchronously.
    /// </summary>
    private FlowStateEntryDto RecordStateEntry(Guid sessionId, FlowStateEntryDto entry)
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
        
        // Persist asynchronously (fire and forget, but log errors)
        _ = PersistStateHistoryAsync(sessionId, history);
        
        return entry;
    }

    /// <summary>
    /// Persists the state history to durable storage.
    /// </summary>
    private async Task PersistStateHistoryAsync(Guid sessionId, List<FlowStateEntryDto> history)
    {
        try
        {
            string json;
            lock (history)
            {
                json = JsonSerializer.Serialize(history);
            }
            
            using var scope = scopeFactory.CreateScope();
            var persistence = scope.ServiceProvider.GetService<ISessionPersistenceService>();
            if (persistence != null)
            {
                await persistence.SaveStateHistoryAsync(sessionId, json);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist state history for session {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Gets the state history for a session.
    /// Loads from persistent storage if not in memory (e.g., after app restart).
    /// </summary>
    public async Task<List<FlowStateEntryDto>> GetSessionHistoryAsync(Guid sessionId)
    {
        if (SessionStateHistory.TryGetValue(sessionId, out var history))
        {
            lock (history)
            {
                return [.. history]; // Return a copy
            }
        }
        
        // Try to load from persistence
        try
        {
            using var scope = scopeFactory.CreateScope();
            var persistence = scope.ServiceProvider.GetService<ISessionPersistenceService>();
            if (persistence != null)
            {
                var json = await persistence.LoadStateHistoryAsync(sessionId);
                var loadedHistory = JsonSerializer.Deserialize<List<FlowStateEntryDto>>(json) ?? [];
                
                if (loadedHistory.Count > 0)
                {
                    // Store in memory for future access
                    SessionStateHistory[sessionId] = loadedHistory;
                    logger.LogDebug("Loaded {Count} state history entries from persistence for session {SessionId}", 
                        loadedHistory.Count, sessionId);
                    return [.. loadedHistory];
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load state history from persistence for session {SessionId}", sessionId);
        }
        
        return [];
    }

    /// <summary>
    /// Gets the state history for a session (synchronous version for backward compatibility).
    /// </summary>
    [Obsolete("Use GetSessionHistoryAsync instead")]
    public List<FlowStateEntryDto> GetSessionHistory(Guid sessionId)
    {
        return GetSessionHistoryAsync(sessionId).GetAwaiter().GetResult();
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
    /// The pipeline runs to completion even if the client disconnects.
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

        // Mark session as actively processing
        conversationSession.IsActivelyProcessing = true;
        sessionManager.UpdateSession(conversationSession);
        
        // Notify dashboard clients that this session is now processing
        await changeHubContext.Clients.Group(GetUserDashboardGroup()).SendAsync("SetupSessionUpdated", new SetupSessionUpdatedEvent(
            request.SessionId,
            conversationSession.DisplayName ?? "New Watch",
            CurrentPrompt: null,
            IsProcessing: true,
            IsCompleted: false,
            IsCancelled: false,
            IsBackgrounded: false,
            CurrentStage: conversationSession.CurrentPipelineStage));

        // Get or create pipeline session
        PipelineSessions.TryGetValue(request.SessionId, out var pipelineSession);

        // Stream pipeline updates with exception handling
        // CRITICAL: The pipeline must run to completion even if the client disconnects.
        // We use CancellationToken.None for the task and channel operations so the pipeline
        // continues. The client's ct is only used to stop yielding results.
        var channel = System.Threading.Channels.Channel.CreateUnbounded<FlowStateEntryDto>();
        
        // Start the pipeline task with no cancellation - it runs to completion
        // regardless of client connection state
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var entry in StreamPipelineAsync(request.SessionId, request.Message, pipelineSession))
                {
                    // Always record state entries, even if client disconnected
                    RecordStateEntry(request.SessionId, entry);
                    
                    // Try to write to channel (may fail if reader stopped, that's OK)
                    await channel.Writer.WriteAsync(entry, CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Pipeline streaming failed for session {SessionId}", request.SessionId);
                
                // Write error entry
                var errorEntry = new FlowStateEntryDto
                {
                    Stage = "Failed",
                    Status = FlowStateStatusDto.Failed,
                    Summary = $"An error occurred: {ex.Message}",
                    Details = ex.GetType().Name,
                    Timestamp = DateTimeOffset.UtcNow,
                    IsCurrentState = true
                };
                RecordStateEntry(request.SessionId, errorEntry);
                await channel.Writer.WriteAsync(errorEntry, CancellationToken.None);
                
                // Notify dashboard that this session failed (so it's removed from pending list)
                try
                {
                    var failedSession = sessionManager.GetSession(request.SessionId);
                    await changeHubContext.Clients.Group(GetUserDashboardGroup()).SendAsync("SetupSessionUpdated", new SetupSessionUpdatedEvent(
                        request.SessionId,
                        failedSession?.DisplayName ?? "Failed Setup",
                        CurrentPrompt: null,
                        IsProcessing: false,
                        IsCompleted: true,
                        IsCancelled: false));
                }
                catch { /* Best-effort notification */ }
            }
            finally
            {
                // Clear processing flags when pipeline completes (success or failure)
                var session = sessionManager.GetSession(request.SessionId);
                if (session != null)
                {
                    session.IsActivelyProcessing = false;
                    session.PendingInput = null;
                    sessionManager.UpdateSession(session);
                }
                
                channel.Writer.Complete();
            }
        }, CancellationToken.None);

        // Read from channel and yield to client while connected
        // When client disconnects (ct cancelled), we stop yielding but the task continues
        await foreach (var entry in channel.Reader.ReadAllAsync(ct))
        {
            yield return entry;
        }
        
        // Log if client disconnected mid-processing
        if (ct.IsCancellationRequested)
        {
            logger.LogInformation("Client disconnected from session {SessionId}, pipeline will complete", request.SessionId);
        }
    }

    /// <summary>
    /// Streams pipeline execution as FlowStateEntry updates.
    /// Runs to completion regardless of client connection - uses application lifetime for cancellation.
    /// </summary>
    private async IAsyncEnumerable<FlowStateEntryDto> StreamPipelineAsync(
        Guid sessionId,
        string message,
        PipelineSession? existingSession)
    {
        // Use application shutdown token for pipeline operations.
        // This ensures pipeline operations complete even if the client disconnects.
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
                yield return new FlowStateEntryDto
                {
                    Stage = "Recovery",
                    Status = FlowStateStatusDto.Recovery,
                    Summary = "Attempting to recover...",
                    Timestamp = DateTimeOffset.UtcNow,
                    IsCurrentState = true
                };

                var recoveryResult = await pipeline.RecoverFromFailureAsync(finalResult.Session, finalResult, options, pipelineCt);
                
                // Update stored session
                PipelineSessions[sessionId] = recoveryResult.Session;

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
                            },
                            SchemaEnabled = recoveryResult.FinalConfiguration.SchemaEnabled,
                            Schema = recoveryResult.FinalConfiguration.Schema,
                            FilterRules = recoveryResult.FinalConfiguration.FilterRules.Count > 0
                                ? recoveryResult.FinalConfiguration.FilterRules : null
                        };
                        
                        createdWatch = await watchService.CreateWatchAsync(createRequest, pipelineCt);
                        logger.LogInformation("Created watch {WatchId} after recovery for URL {Url}", createdWatch.Id, createRequest.Url);
                        
                        // Mark session as completed so it's excluded from pending list
                        var convSessionAfterRecovery = sessionManager.GetSession(sessionId);
                        if (convSessionAfterRecovery != null)
                        {
                            convSessionAfterRecovery.IsCompleted = true;
                            convSessionAfterRecovery.PendingInput = null;
                            convSessionAfterRecovery.IsActivelyProcessing = false;
                            sessionManager.UpdateSession(convSessionAfterRecovery);
                        }
                        
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
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to create watch after recovery for URL {Url}", recoveryResult.FinalConfiguration.Url);
                        persistError = ex.Message;
                    }
                    
                    // Try to notify caller - this may fail if client disconnected, which is OK
                    if (createdWatch != null)
                    {
                        try
                        {
                            await Clients.Caller.SendAsync("SetupCompleted", createdWatch.Id, 
                                $"URL: {recoveryResult.FinalConfiguration.Url}", pipelineCt);
                        }
                        catch (ObjectDisposedException)
                        {
                            // Client disconnected before we could notify them - this is fine,
                            // the watch was created and dashboard was notified via changeHubContext
                            logger.LogDebug("Client disconnected before SetupCompleted notification for watch {WatchId}", createdWatch.Id);
                        }
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
                    },
                    SchemaEnabled = finalResult.FinalConfiguration.SchemaEnabled,
                    Schema = finalResult.FinalConfiguration.Schema,
                    FilterRules = finalResult.FinalConfiguration.FilterRules.Count > 0
                        ? finalResult.FinalConfiguration.FilterRules : null
                };
                
                createdWatch = await watchService.CreateWatchAsync(createRequest, pipelineCt);
                logger.LogInformation("Created watch {WatchId} for URL {Url}", createdWatch.Id, createRequest.Url);
                
                // Mark session as completed so it's excluded from pending list
                var convSessionAfterCreate = sessionManager.GetSession(sessionId);
                if (convSessionAfterCreate != null)
                {
                    convSessionAfterCreate.IsCompleted = true;
                    convSessionAfterCreate.PendingInput = null;
                    convSessionAfterCreate.IsActivelyProcessing = false;
                    sessionManager.UpdateSession(convSessionAfterCreate);
                }
                
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
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create watch for URL {Url}", finalResult.FinalConfiguration.Url);
                persistError = ex.Message;
            }
            
            // Try to notify caller - this may fail if client disconnected, which is OK
            // Don't remove session or history yet - keep for page reload resilience
            // They will be cleaned up when the session expires or is explicitly ended
            if (createdWatch != null)
            {
                try
                {
                    await Clients.Caller.SendAsync("SetupCompleted", createdWatch.Id, 
                        $"URL: {finalResult.FinalConfiguration.Url}", pipelineCt);
                }
                catch (ObjectDisposedException)
                {
                    // Client disconnected before we could notify them - this is fine,
                    // the watch was created and dashboard was notified via changeHubContext
                    logger.LogDebug("Client disconnected before SetupCompleted notification for watch {WatchId}", createdWatch.Id);
                }
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
            IsBackgrounded = false, // Background mode removed
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
                },
                SchemaEnabled = pipelineSession.SchemaEnabled ?? false,
                Schema = pipelineSession.DiscoveredSchema != null
                    ? ConvertToExtractionSchema(pipelineSession.DiscoveredSchema)
                    : null,
                FilterRules = BuildFilterRulesFromSession(pipelineSession)
            };
            
            var createdWatch = await watchService.CreateWatchAsync(createRequest, ct);
            logger.LogInformation("Created watch {WatchId} for URL {Url}", createdWatch.Id, createRequest.Url);
            
            // Mark session as completed so it's excluded from pending list
            var convSessionAfterFinalize = sessionManager.GetSession(sessionId);
            if (convSessionAfterFinalize != null)
            {
                convSessionAfterFinalize.IsCompleted = true;
                sessionManager.UpdateSession(convSessionAfterFinalize);
            }
            
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

    private static ExtractionSchema ConvertToExtractionSchema(DiscoveredSchema discovered)
    {
        return new ExtractionSchema
        {
            ItemSelector = discovered.ItemSelector,
            Fields = discovered.Fields.Select(f => new SchemaField
            {
                Name = f.Name,
                Type = ParseFieldType(f.Type),
                Selector = f.Selector,
                IsRequired = f.IsRequired,
                IsIdentityField = f.IsIdentityField,
                SampleValue = f.SampleValues.FirstOrDefault(),
                Confidence = f.Confidence
            }).ToList(),
            IdentityFieldNames = discovered.InferredIdentityFields.ToList(),
            Version = 1,
            DiscoveredAt = DateTime.UtcNow
        };
    }

    private static FieldType ParseFieldType(string typeString)
    {
        return typeString?.ToLowerInvariant() switch
        {
            "date" => FieldType.Date,
            "url" => FieldType.Url,
            "number" => FieldType.Number,
            "currency" => FieldType.Currency,
            "image" => FieldType.Image,
            "html" => FieldType.Html,
            "percentage" => FieldType.Percentage,
            "duration" => FieldType.Duration,
            "boolean" => FieldType.Boolean,
            "status" => FieldType.Status,
            _ => FieldType.String
        };
    }

    /// <summary>
    /// Builds filter rules from a pipeline session's content analysis keywords.
    /// Used in the ConfirmSetup path where we don't have a FinalConfiguration.
    /// </summary>
    private static List<FilterRule>? BuildFilterRulesFromSession(PipelineSession session)
    {
        var keywords = session.ContentAnalysis?.FilterKeywords ?? [];
        if (keywords.Count == 0)
            return null;

        var rules = new List<FilterRule>();
        foreach (var keyword in keywords)
        {
            rules.Add(new FilterRule
            {
                Name = $"Match: {keyword}",
                Description = $"Notify when content contains '{keyword}' (from user intent: {session.ContentAnalysis?.UserIntent})",
                Conditions =
                [
                    new FilterCondition
                    {
                        FieldName = "$content",
                        Operator = FilterOperator.Contains,
                        Value = keyword
                    }
                ],
                Actions =
                [
                    new FilterAction
                    {
                        Type = FilterActionType.ImmediateNotify,
                        Parameters = new Dictionary<string, string>
                        {
                            ["reason"] = $"Content matches filter keyword: {keyword}"
                        }
                    }
                ],
                Priority = 100,
                IsEnabled = true
            });
        }

        return rules;
    }
}

// FlowStateEntryDto, FlowStateStatusDto, FlowOptionDto are defined in ChangeDetection.Shared.Dtos.PipelineDtos
