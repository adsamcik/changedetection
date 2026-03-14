using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.Pipeline;

/// <summary>
/// Orchestrates the multi-stage watch setup pipeline.
/// Manages the flow, iterations, and feedback loops.
/// Records all events to database for history and debugging.
/// </summary>
public class WatchSetupPipeline(
    UrlExtractionStage urlExtraction,
    ContentFetchingStage contentFetching,
    ContentAnalysisStage contentAnalysis,
    SelectorGenerationStage selectorGeneration,
    SelectorValidationStage selectorValidation,
    SchemaDiscoveryStage schemaDiscovery,
    ILlmProviderChain llmProvider,
    IPipelineEventService eventService,
    ILlmLogService llmLogService,
    IUserContext userContext,
    ILogger<WatchSetupPipeline> logger) : IWatchSetupPipeline
{
    private const int DefaultMaxIterations = 3;
    private const float DefaultMinConfidence = 0.6f;
    
    /// <summary>
    /// Tracks the current pipeline run for event recording.
    /// Stored per-session via AsyncLocal to support concurrent pipelines.
    /// </summary>
    private static readonly AsyncLocal<PipelineRun?> CurrentRun = new();

    /// <inheritdoc />
    public async Task<PipelineResult> ProcessAsync(
        string userInput,
        PipelineOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new PipelineOptions();
        var session = new PipelineSession
        {
            OriginalInput = userInput
        };

        // Start tracking the pipeline run
        var run = await eventService.StartRunAsync(
            session.SessionId, 
            userInput, 
            userContext.CurrentUserId, 
            ct);
        CurrentRun.Value = run;

        logger.LogInformation("Starting pipeline for input: {Input}, run: {RunId}", TruncateForLog(userInput), run.Id);

        // Set up LLM call correlation
        var promptsByRequestId = new ConcurrentDictionary<Guid, string>();
        var currentStage = PipelineStageNames.UrlExtraction;

        void OnLlmLogAdded(LlmLogEntry entry)
        {
            if (entry.PipelineRunId != run.Id) return;

            if (entry.Category == LlmLogCategory.Request && entry.RequestId.HasValue && entry.FullPrompt != null)
            {
                promptsByRequestId[entry.RequestId.Value] = entry.FullPrompt;
            }
            else if (entry.Category is LlmLogCategory.Response or LlmLogCategory.Error)
            {
                string? prompt = null;
                if (entry.RequestId.HasValue)
                    promptsByRequestId.TryRemove(entry.RequestId.Value, out prompt);

                _ = eventService.RecordLlmCallAsync(
                    run.Id, currentStage,
                    entry.ProviderName, entry.Model ?? "unknown",
                    entry.InputTokens ?? 0, entry.OutputTokens ?? 0,
                    entry.DurationMs ?? 0,
                    entry.IsSuccess ?? true, entry.ErrorMessage,
                    prompt, entry.FullResponse,
                    CancellationToken.None);
            }
        }

        llmLogService.OnLogAdded += OnLlmLogAdded;
        PipelineExecutionContext.CurrentPipelineRunId = run.Id;

        try
        {
            // Transition to InProgress
            await eventService.UpdateRunStatusAsync(run.Id, PipelineRunStatus.InProgress, 
                PipelineStageNames.UrlExtraction, ct);
            
            // Stage 1: Extract URLs
            await RecordStageStartAsync(run.Id, PipelineStageNames.UrlExtraction, ct);
            var result = await ExecuteUrlExtractionAsync(session, ct);
            if (result != null)
            {
                await HandleResultAsync(run.Id, result, ct);
                return result;
            }
            await RecordStageCompletedAsync(run.Id, PipelineStageNames.UrlExtraction, 
                $"Extracted URL: {session.SelectedUrl?.NormalizedUrl}", ct);
            await eventService.UpdateExtractedUrlAsync(run.Id, session.SelectedUrl?.NormalizedUrl ?? "", ct);

            // Stage 2: Fetch Content
            currentStage = PipelineStageNames.ContentFetching;
            await RecordStageStartAsync(run.Id, PipelineStageNames.ContentFetching, ct);
            result = await ExecuteContentFetchingAsync(session, options, ct);
            if (result != null)
            {
                await HandleResultAsync(run.Id, result, ct);
                return result;
            }
            await RecordStageCompletedAsync(run.Id, PipelineStageNames.ContentFetching, 
                $"Fetched {session.FetchedContent?.TextContent?.Length ?? 0} chars", ct);

            // Stage 3: Analyze Content
            currentStage = PipelineStageNames.ContentAnalysis;
            await RecordStageStartAsync(run.Id, PipelineStageNames.ContentAnalysis, ct);
            result = await ExecuteContentAnalysisAsync(session, ct);
            if (result != null)
            {
                await HandleResultAsync(run.Id, result, ct);
                return result;
            }
            await RecordStageCompletedAsync(run.Id, PipelineStageNames.ContentAnalysis, 
                $"Detected {session.ContentAnalysis?.ContentType}: {session.ContentAnalysis?.UserIntent}",
                SerializeStageData(new
                {
                    session.ContentAnalysis?.ContentType,
                    session.ContentAnalysis?.UserIntent,
                    session.ContentAnalysis?.Confidence,
                    session.ContentAnalysis?.RecommendedApproach,
                    FilterKeywords = session.ContentAnalysis?.FilterKeywords ?? [],
                    Sections = session.ContentAnalysis?.IdentifiedSections.Select(s => new
                    {
                        s.Name, s.SuggestedSelector, s.IsLikelyTarget
                    }).ToList() ?? []
                }),
                ct);

            // Stage 3.5: Schema Discovery (for list-type content)
            if (session.ContentAnalysis != null && 
                session.FetchedContent != null &&
                SchemaDiscoveryStage.ShouldDiscoverSchema(session.ContentAnalysis.ContentType))
            {
                currentStage = PipelineStageNames.SchemaDiscovery;
                await RecordStageStartAsync(run.Id, PipelineStageNames.SchemaDiscovery, ct);
                await ExecuteSchemaDiscoveryAsync(session, ct);
                await RecordStageCompletedAsync(run.Id, PipelineStageNames.SchemaDiscovery, 
                    session.DiscoveredSchema != null 
                        ? $"Discovered schema with {session.DiscoveredSchema.Fields.Count} fields" 
                        : "No schema discovered",
                    session.DiscoveredSchema != null 
                        ? SerializeStageData(new
                        {
                            session.DiscoveredSchema.ItemSelector,
                            Fields = session.DiscoveredSchema.Fields.Select(f => new
                            {
                                f.Name, f.Type, f.Selector, f.IsIdentityField, f.Confidence
                            }).ToList(),
                            session.DiscoveredSchema.InferredIdentityFields
                        })
                        : null,
                    ct);
            }

            // Stage 4-5: Generate and Validate Selectors (with iteration loop)
            currentStage = PipelineStageNames.SelectorGeneration;
            await RecordStageStartAsync(run.Id, PipelineStageNames.SelectorGeneration, ct);
            result = await ExecuteSelectorIterationAsync(session, options, ct);
            if (result != null)
            {
                await HandleResultAsync(run.Id, result, ct);
                return result;
            }
            await RecordStageCompletedAsync(run.Id, PipelineStageNames.SelectorValidation, 
                session.BestSelector != null ? $"Selected: {session.BestSelector.Selector}" : "No selector",
                SerializeStageData(new
                {
                    BestSelector = session.BestSelector != null ? new
                    {
                        session.BestSelector.Selector,
                        Type = session.BestSelector.Type.ToString(),
                        session.BestSelector.Description,
                        session.BestSelector.Confidence
                    } : null,
                    AllSelectors = session.GeneratedSelectors.Select(s => new
                    {
                        s.Selector, Type = s.Type.ToString(), s.Confidence
                    }).ToList(),
                    Validations = session.ValidationResults.Select(v => new
                    {
                        v.Selector, v.IsValid, v.MatchCount, v.ValidationMessage
                    }).ToList()
                }),
                ct);

            // Stage 6: Build final configuration
            currentStage = PipelineStageNames.Configuration;
            var finalResult = BuildFinalResult(session, options);
            await HandleResultAsync(run.Id, finalResult, ct);
            return finalResult;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Pipeline cancelled");
            await eventService.CancelRunAsync(run.Id, ct);
            return CreateFailedResult(session, PipelineStage.Failed, "Operation was cancelled");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pipeline failed with exception");
            await eventService.RecordFailureAsync(run.Id, session.ContentAnalysis != null 
                ? PipelineStageNames.SelectorGeneration : PipelineStageNames.ContentAnalysis, 
                ex.Message, ex.StackTrace, ct);
            await eventService.FailRunAsync(run.Id, ex.Message, ct);
            return CreateFailedResult(session, PipelineStage.Failed, $"Pipeline error: {ex.Message}");
        }
        finally
        {
            llmLogService.OnLogAdded -= OnLlmLogAdded;
            PipelineExecutionContext.CurrentPipelineRunId = null;
            CurrentRun.Value = null;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PipelineProgress> ProcessStreamingAsync(
        string userInput,
        PipelineOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        options ??= new PipelineOptions();
        var session = new PipelineSession
        {
            OriginalInput = userInput
        };

        // Start tracking the pipeline run
        var run = await eventService.StartRunAsync(
            session.SessionId, 
            userInput, 
            userContext.CurrentUserId, 
            ct);
        CurrentRun.Value = run;

        logger.LogInformation("Starting streaming pipeline for input: {Input}, run: {RunId}", 
            TruncateForLog(userInput), run.Id);

        // Set up LLM call correlation
        var promptsByRequestId = new ConcurrentDictionary<Guid, string>();
        var currentStage = PipelineStageNames.UrlExtraction;

        void OnLlmLogAdded(LlmLogEntry entry)
        {
            if (entry.PipelineRunId != run.Id) return;

            if (entry.Category == LlmLogCategory.Request && entry.RequestId.HasValue && entry.FullPrompt != null)
            {
                promptsByRequestId[entry.RequestId.Value] = entry.FullPrompt;
            }
            else if (entry.Category is LlmLogCategory.Response or LlmLogCategory.Error)
            {
                string? prompt = null;
                if (entry.RequestId.HasValue)
                    promptsByRequestId.TryRemove(entry.RequestId.Value, out prompt);

                _ = eventService.RecordLlmCallAsync(
                    run.Id, currentStage,
                    entry.ProviderName, entry.Model ?? "unknown",
                    entry.InputTokens ?? 0, entry.OutputTokens ?? 0,
                    entry.DurationMs ?? 0,
                    entry.IsSuccess ?? true, entry.ErrorMessage,
                    prompt, entry.FullResponse,
                    CancellationToken.None);
            }
        }

        llmLogService.OnLogAdded += OnLlmLogAdded;
        PipelineExecutionContext.CurrentPipelineRunId = run.Id;

        PipelineResult? result = null;
        Exception? error = null;

        // Transition to InProgress
        await eventService.UpdateRunStatusAsync(run.Id, PipelineRunStatus.InProgress, 
            PipelineStageNames.UrlExtraction, ct);
        
        // Stage 1: URL Extraction
        await RecordStageStartAsync(run.Id, PipelineStageNames.UrlExtraction, ct);
        yield return new PipelineProgress
        {
            Stage = PipelineStage.UrlExtraction,
            Type = ProgressType.Starting,
            Summary = "Extracting URL from input...",
            Session = session
        };

        try
        {
            result = await ExecuteUrlExtractionAsync(session, ct);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (error != null)
        {
            await eventService.RecordFailureAsync(run.Id, PipelineStageNames.UrlExtraction, error.Message, ct: ct);
            await eventService.FailRunAsync(run.Id, error.Message, ct);
            yield return CreateFailedProgress(session, PipelineStage.UrlExtraction, error.Message);
            llmLogService.OnLogAdded -= OnLlmLogAdded;
            PipelineExecutionContext.CurrentPipelineRunId = null;
            CurrentRun.Value = null;
            yield break;
        }

        if (result != null)
        {
            await HandleResultAsync(run.Id, result, ct);
            yield return CreateResultProgress(result);
            llmLogService.OnLogAdded -= OnLlmLogAdded;
            PipelineExecutionContext.CurrentPipelineRunId = null;
            CurrentRun.Value = null;
            yield break;
        }

        await RecordStageCompletedAsync(run.Id, PipelineStageNames.UrlExtraction, 
            $"Extracted URL: {session.SelectedUrl?.NormalizedUrl}", ct);
        await eventService.UpdateExtractedUrlAsync(run.Id, session.SelectedUrl?.NormalizedUrl ?? "", ct);

        yield return new PipelineProgress
        {
            Stage = PipelineStage.UrlExtraction,
            Type = ProgressType.StageCompleted,
            Summary = $"Found URL: {session.SelectedUrl?.NormalizedUrl}",
            Details = session.UserIntent,
            Session = session
        };

        // Stage 2: Content Fetching
        error = null;
        currentStage = PipelineStageNames.ContentFetching;
        await RecordStageStartAsync(run.Id, PipelineStageNames.ContentFetching, ct);
        yield return new PipelineProgress
        {
            Stage = PipelineStage.ContentFetching,
            Type = ProgressType.Starting,
            Summary = $"Fetching content from {session.SelectedUrl?.NormalizedUrl}...",
            Session = session
        };

        try
        {
            result = await ExecuteContentFetchingAsync(session, options, ct);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (error != null)
        {
            await eventService.RecordFailureAsync(run.Id, PipelineStageNames.ContentFetching, error.Message, ct: ct);
            await eventService.FailRunAsync(run.Id, error.Message, ct);
            yield return CreateFailedProgress(session, PipelineStage.ContentFetching, error.Message);
            llmLogService.OnLogAdded -= OnLlmLogAdded;
            PipelineExecutionContext.CurrentPipelineRunId = null;
            CurrentRun.Value = null;
            yield break;
        }

        if (result != null)
        {
            await HandleResultAsync(run.Id, result, ct);
            yield return CreateResultProgress(result);
            llmLogService.OnLogAdded -= OnLlmLogAdded;
            PipelineExecutionContext.CurrentPipelineRunId = null;
            CurrentRun.Value = null;
            yield break;
        }

        await RecordStageCompletedAsync(run.Id, PipelineStageNames.ContentFetching, 
            $"Fetched {session.FetchedContent?.TextContent?.Length ?? 0} chars", ct);

        yield return new PipelineProgress
        {
            Stage = PipelineStage.ContentFetching,
            Type = ProgressType.StageCompleted,
            Summary = $"Content fetched ({session.FetchedContent?.TextContent?.Length ?? 0:N0} chars)",
            Details = session.FetchedContent?.UsedJavaScript == true ? "Used JavaScript rendering" : null,
            Session = session
        };

        // Stage 3: Content Analysis (with streaming thinking)
        error = null;
        currentStage = PipelineStageNames.ContentAnalysis;
        await RecordStageStartAsync(run.Id, PipelineStageNames.ContentAnalysis, ct);
        yield return new PipelineProgress
        {
            Stage = PipelineStage.ContentAnalysis,
            Type = ProgressType.Starting,
            Summary = "Analyzing page content with AI...",
            Session = session
        };

        // Collect streaming progress in a channel to avoid yield-in-try-catch
        var analysisChannel = System.Threading.Channels.Channel.CreateUnbounded<PipelineProgress>();
        
        var analysisTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var progress in ExecuteContentAnalysisStreamingAsync(session, ct))
                {
                    analysisChannel.Writer.TryWrite(progress);
                }
            }
            catch (Exception ex)
            {
                analysisChannel.Writer.TryWrite(CreateFailedProgress(session, PipelineStage.ContentAnalysis, ex.Message));
            }
            finally
            {
                analysisChannel.Writer.Complete();
            }
        }, ct);

        await foreach (var progress in analysisChannel.Reader.ReadAllAsync(ct))
        {
            yield return progress;
            if (progress.Type == ProgressType.Failed)
            {
                error = new Exception(progress.Summary);
            }
        }

        await analysisTask;

        if (error != null)
        {
            await eventService.RecordFailureAsync(run.Id, PipelineStageNames.ContentAnalysis, 
                error.Message, ct: ct);
            await eventService.FailRunAsync(run.Id, error.Message, ct);
            llmLogService.OnLogAdded -= OnLlmLogAdded;
            PipelineExecutionContext.CurrentPipelineRunId = null;
            CurrentRun.Value = null;
            yield break;
        }

        if (session.ContentAnalysis == null)
        {
            await eventService.RecordFailureAsync(run.Id, PipelineStageNames.ContentAnalysis, 
                "Content analysis failed", ct: ct);
            await eventService.FailRunAsync(run.Id, "Content analysis failed", ct);
            yield return CreateFailedProgress(session, PipelineStage.ContentAnalysis, "Content analysis failed");
            llmLogService.OnLogAdded -= OnLlmLogAdded;
            PipelineExecutionContext.CurrentPipelineRunId = null;
            CurrentRun.Value = null;
            yield break;
        }

        await RecordStageCompletedAsync(run.Id, PipelineStageNames.ContentAnalysis, 
            $"Detected {session.ContentAnalysis?.ContentType}: {session.ContentAnalysis?.UserIntent}",
            SerializeStageData(new
            {
                session.ContentAnalysis?.ContentType,
                session.ContentAnalysis?.UserIntent,
                session.ContentAnalysis?.Confidence,
                session.ContentAnalysis?.RecommendedApproach,
                FilterKeywords = session.ContentAnalysis?.FilterKeywords ?? [],
                Sections = session.ContentAnalysis?.IdentifiedSections.Select(s => new
                {
                    s.Name, s.SuggestedSelector, s.IsLikelyTarget
                }).ToList() ?? []
            }),
            ct);

        yield return new PipelineProgress
        {
            Stage = PipelineStage.ContentAnalysis,
            Type = ProgressType.StageCompleted,
            Summary = $"Detected {session.ContentAnalysis?.ContentType}: {session.ContentAnalysis?.UserIntent}",
            Details = $"Found {session.ContentAnalysis?.IdentifiedSections.Count ?? 0} sections",
            Session = session
        };

        // Stage 3.5: Schema Discovery (for list-type content)
        if (session.ContentAnalysis != null && 
            session.FetchedContent != null &&
            SchemaDiscoveryStage.ShouldDiscoverSchema(session.ContentAnalysis.ContentType))
        {
            currentStage = PipelineStageNames.SchemaDiscovery;
            await RecordStageStartAsync(run.Id, PipelineStageNames.SchemaDiscovery, ct);
            yield return new PipelineProgress
            {
                Stage = PipelineStage.ContentAnalysis, // Use ContentAnalysis stage for UI grouping
                Type = ProgressType.InProgress,
                Summary = "Discovering extraction schema for structured content...",
                Session = session
            };

            // Run schema discovery with heartbeat pulses to keep SignalR alive
            await foreach (var pulse in WithHeartbeatAsync(
                ExecuteSchemaDiscoveryAsync(session, ct),
                PipelineStage.ContentAnalysis,
                "Analyzing page structure",
                session, ct))
            {
                yield return pulse;
            }

            await RecordStageCompletedAsync(run.Id, PipelineStageNames.SchemaDiscovery,
                session.DiscoveredSchema != null
                    ? $"Discovered schema with {session.DiscoveredSchema.Fields.Count} fields"
                    : "No schema discovered",
                session.DiscoveredSchema != null
                    ? SerializeStageData(new
                    {
                        session.DiscoveredSchema.ItemSelector,
                        Fields = session.DiscoveredSchema.Fields.Select(f => new
                        {
                            f.Name, f.Type, f.Selector, f.IsIdentityField, f.Confidence
                        }).ToList(),
                        session.DiscoveredSchema.InferredIdentityFields
                    })
                    : null,
                ct);

            if (session.DiscoveredSchema != null)
            {
                yield return new PipelineProgress
                {
                    Stage = PipelineStage.ContentAnalysis,
                    Type = ProgressType.StageCompleted,
                    Summary = $"Schema discovered: {session.DiscoveredSchema.Fields.Count} fields for {session.DiscoveredSchema.SampleItemCount} items",
                    Details = session.DiscoveredSchema.Explanation,
                    Session = session
                };
            }
        }

        // Stage 4-5: Selector Generation and Validation
        error = null;
        currentStage = PipelineStageNames.SelectorGeneration;
        await RecordStageStartAsync(run.Id, PipelineStageNames.SelectorGeneration, ct);
        yield return new PipelineProgress
        {
            Stage = PipelineStage.SelectorGeneration,
            Type = ProgressType.Starting,
            Summary = "Generating selectors for monitoring...",
            Session = session
        };

        // Run selector generation with heartbeat pulses using channel pattern (can't yield in try-catch)
        var selectorChannel = System.Threading.Channels.Channel.CreateUnbounded<PipelineProgress>();

        var selectorTask = Task.Run(async () =>
        {
            try
            {
                var r = await ExecuteSelectorIterationAsync(session, options, ct);
                // Store result in a completion marker
                selectorChannel.Writer.TryWrite(new PipelineProgress
                {
                    Stage = PipelineStage.SelectorGeneration,
                    Type = ProgressType.StageCompleted,
                    Summary = "Selector generation complete",
                    Session = session,
                    Result = r
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Let cancellation propagate naturally
            }
            catch (Exception ex)
            {
                selectorChannel.Writer.TryWrite(CreateFailedProgress(session, PipelineStage.SelectorGeneration, ex.Message));
            }
            finally
            {
                selectorChannel.Writer.Complete();
            }
        }, ct);

        // Yield heartbeat pulses while waiting for selector generation
        var selectorElapsed = System.Diagnostics.Stopwatch.StartNew();
        var selectorHeartbeat = TimeSpan.FromSeconds(8);

        while (!selectorTask.IsCompleted)
        {
            try
            {
                await Task.WhenAny(selectorTask, Task.Delay(selectorHeartbeat, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }

            if (!selectorTask.IsCompleted)
            {
                var secs = (int)selectorElapsed.Elapsed.TotalSeconds;
                yield return new PipelineProgress
                {
                    Stage = PipelineStage.SelectorGeneration,
                    Type = ProgressType.InProgress,
                    Summary = $"Generating selectors... ({secs}s)",
                    Session = session
                };
            }
        }

        // Drain channel for results/errors
        await foreach (var item in selectorChannel.Reader.ReadAllAsync(ct))
        {
            if (item.Type == ProgressType.Failed)
                error = new Exception(item.Summary);
            else if (item.Result != null)
                result = item.Result;
        }

        // Propagate cancellation
        if (ct.IsCancellationRequested)
            ct.ThrowIfCancellationRequested();

        if (error != null)
        {
            await eventService.RecordFailureAsync(run.Id, PipelineStageNames.SelectorGeneration, 
                error.Message, ct: ct);
            await eventService.FailRunAsync(run.Id, error.Message, ct);
            yield return CreateFailedProgress(session, PipelineStage.SelectorGeneration, error.Message);
            llmLogService.OnLogAdded -= OnLlmLogAdded;
            PipelineExecutionContext.CurrentPipelineRunId = null;
            CurrentRun.Value = null;
            yield break;
        }

        if (result != null)
        {
            await HandleResultAsync(run.Id, result, ct);
            yield return CreateResultProgress(result);
            if (!result.NeedsUserInput)
            {
                llmLogService.OnLogAdded -= OnLlmLogAdded;
                PipelineExecutionContext.CurrentPipelineRunId = null;
                CurrentRun.Value = null;
            }
            yield break;
        }

        await RecordStageCompletedAsync(run.Id, PipelineStageNames.SelectorValidation, 
            session.BestSelector != null ? $"Selected: {session.BestSelector.Selector}" : "No selector",
            SerializeStageData(new
            {
                BestSelector = session.BestSelector != null ? new
                {
                    session.BestSelector.Selector,
                    Type = session.BestSelector.Type.ToString(),
                    session.BestSelector.Description,
                    session.BestSelector.Confidence
                } : null,
                AllSelectors = session.GeneratedSelectors.Select(s => new
                {
                    s.Selector, Type = s.Type.ToString(), s.Confidence
                }).ToList(),
                Validations = session.ValidationResults.Select(v => new
                {
                    v.Selector, v.IsValid, v.MatchCount, v.ValidationMessage
                }).ToList()
            }),
            ct);

        yield return new PipelineProgress
        {
            Stage = PipelineStage.SelectorValidation,
            Type = ProgressType.StageCompleted,
            Summary = session.BestSelector != null 
                ? $"Found selector: {session.BestSelector.Selector}"
                : "Selector validation complete",
            Session = session
        };

        // Stage 6: Build final configuration
        currentStage = PipelineStageNames.Configuration;
        var finalResult = BuildFinalResult(session, options);
        await HandleResultAsync(run.Id, finalResult, ct);
        yield return CreateResultProgress(finalResult);
        llmLogService.OnLogAdded -= OnLlmLogAdded;
        PipelineExecutionContext.CurrentPipelineRunId = null;
        CurrentRun.Value = null;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PipelineProgress> ContinueWithFeedbackStreamingAsync(
        PipelineSession session,
        string feedback,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        logger.LogInformation("Continuing pipeline with feedback (streaming): {Feedback}", TruncateForLog(feedback));

        // Try to find existing run for this session
        var run = await eventService.GetRunBySessionIdAsync(session.SessionId, ct);
        if (run != null)
        {
            CurrentRun.Value = run;
            await eventService.RecordUserInteractionAsync(
                run.Id,
                PipelineEventTypes.UserInputReceived,
                feedback,
                $"User feedback: {TruncateForLog(feedback)}",
                ct);
            await eventService.UpdateRunStatusAsync(run.Id, PipelineRunStatus.InProgress, 
                PipelineStageNames.SelectorGeneration, ct);
        }

        yield return new PipelineProgress
        {
            Stage = PipelineStage.SelectorGeneration,
            Type = ProgressType.Starting,
            Summary = "Processing your response...",
            Session = session
        };

        PipelineResult? result = null;
        Exception? error = null;
        
        try
        {
            result = await ContinueWithFeedbackAsync(session, feedback, ct);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (error != null)
        {
            if (run != null)
            {
                await eventService.RecordFailureAsync(run.Id, PipelineStageNames.SelectorGeneration, 
                    error.Message, ct: ct);
                await eventService.FailRunAsync(run.Id, error.Message, ct);
            }
            yield return CreateFailedProgress(session, PipelineStage.Failed, error.Message);
            CurrentRun.Value = null;
            yield break;
        }

        if (run != null)
        {
            await HandleResultAsync(run.Id, result!, ct);
        }

        yield return CreateResultProgress(result!);
        CurrentRun.Value = null;
    }

    private static PipelineProgress CreateResultProgress(PipelineResult result)
    {
        var progressType = result.NeedsUserInput ? ProgressType.NeedsInput
            : result.IsSuccess ? ProgressType.Completed
            : ProgressType.Failed;

        return new PipelineProgress
        {
            Stage = result.CurrentStage,
            Type = progressType,
            Summary = result.Summary ?? (result.IsSuccess ? "Pipeline completed" : result.ErrorMessage ?? "Pipeline failed"),
            Details = result.ErrorMessage,
            Session = result.Session,
            Result = result
        };
    }

    private static PipelineProgress CreateFailedProgress(PipelineSession session, PipelineStage stage, string message)
    {
        return new PipelineProgress
        {
            Stage = stage,
            Type = ProgressType.Failed,
            Summary = $"Error: {message}",
            Details = message,
            Session = session,
            Result = new PipelineResult
            {
                IsSuccess = false,
                CurrentStage = stage,
                Session = session,
                ErrorMessage = message
            }
        };
    }

    /// <summary>
    /// Runs a long-running task while yielding periodic heartbeat progress updates
    /// to keep the SignalR connection alive and inform the user of progress.
    /// </summary>
    private async IAsyncEnumerable<PipelineProgress> WithHeartbeatAsync(
        Task longRunningTask,
        PipelineStage stage,
        string activityDescription,
        PipelineSession session,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var heartbeatInterval = TimeSpan.FromSeconds(8);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        while (!longRunningTask.IsCompleted)
        {
            try
            {
                await Task.WhenAny(longRunningTask, Task.Delay(heartbeatInterval, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }

            if (!longRunningTask.IsCompleted)
            {
                var secs = (int)elapsed.Elapsed.TotalSeconds;
                yield return new PipelineProgress
                {
                    Stage = stage,
                    Type = ProgressType.InProgress,
                    Summary = $"{activityDescription}... ({secs}s)",
                    Session = session
                };
            }
        }

        // Propagate any exception from the task
        await longRunningTask;
    }

    /// <inheritdoc />
    public async Task<PipelineResult> ContinueWithFeedbackAsync(
        PipelineSession session,
        string feedback,
        CancellationToken ct = default)
    {
        logger.LogInformation("Continuing pipeline with feedback: {Feedback}", TruncateForLog(feedback));

        session.IterationHistory.Add($"User feedback: {feedback}");

        // Check what stage we're in and what kind of feedback this is
        if (session.ExtractedUrls.Count > 1 && session.SelectedUrl == null)
        {
            // User is selecting from multiple URLs
            return await HandleUrlSelectionFeedbackAsync(session, feedback, ct);
        }

        // Handle special feedback options (these work even if no selectors were generated)
        var feedbackLower = feedback.ToLowerInvariant().Trim();
        
        if (feedbackLower == "fullpage" || feedbackLower == "monitor entire page")
        {
            // User wants to monitor the entire page
            session.BestSelector = null;
            session.IterationHistory.Add("User chose full page monitoring");
            return BuildFinalResult(session, new PipelineOptions());
        }
        
        if (feedbackLower == "help" || feedbackLower == "help me specify what to watch")
        {
            // User wants to specify what to watch - ask for more details
            return new PipelineResult
            {
                IsSuccess = true,
                CurrentStage = PipelineStage.SelectorGeneration,
                Session = session,
                NeedsUserInput = true,
                UserPrompts = ["Please describe what specific content you want to monitor. For example:\n- \"The list of upcoming events\"\n- \"The main article text\"\n- \"The price shown in the header\""],
                SuggestedOptions = [],
                Summary = "Asking user for content description"
            };
        }
        
        if (feedbackLower == "custom" || feedbackLower == "type something else..." || feedbackLower == "type something else")
        {
            // User wants to provide custom input
            return new PipelineResult
            {
                IsSuccess = true,
                CurrentStage = PipelineStage.SelectorGeneration,
                Session = session,
                NeedsUserInput = true,
                UserPrompts = ["Please describe what you want to monitor, or provide a CSS selector if you know one:"],
                SuggestedOptions = [],
                Summary = "Waiting for custom user input"
            };
        }

        // If we have generated selectors, handle selector feedback
        if (session.GeneratedSelectors.Count > 0)
        {
            return await HandleSelectorFeedbackAsync(session, feedback, ct);
        }
        
        // If no selectors but user provided a description, try to generate selectors based on it
        if (!string.IsNullOrWhiteSpace(feedback) && session.FetchedContent != null)
        {
            return await HandleCustomDescriptionAsync(session, feedback, ct);
        }

        return CreateFailedResult(session, PipelineStage.Failed, "Unable to process feedback at current stage");
    }

    /// <summary>
    /// Handles custom user descriptions to generate new selectors.
    /// </summary>
    private async Task<PipelineResult> HandleCustomDescriptionAsync(
        PipelineSession session,
        string userDescription,
        CancellationToken ct)
    {
        logger.LogInformation("User provided custom description: {Description}", TruncateForLog(userDescription));
        
        // Update the session's user intent with the more specific description
        session.UserIntent = userDescription;
        
        // Re-analyze with the new intent
        if (session.FetchedContent != null)
        {
            session.ContentAnalysis = await contentAnalysis.AnalyzeAsync(
                session.FetchedContent,
                userDescription,
                ct);
                
            session.IterationHistory.Add($"Re-analyzed with user description: {TruncateForLog(userDescription, 50)}");
            
            // Try to generate selectors with the new analysis
            var selectors = await selectorGeneration.GenerateSelectorsAsync(
                session.FetchedContent,
                session.ContentAnalysis,
                ct);
                
            if (selectors.Count > 0)
            {
                session.GeneratedSelectors = selectors;
                
                // Validate and find best
                session.ValidationResults = selectorValidation.ValidateSelectors(
                    session.FetchedContent,
                    selectors,
                    session.ContentAnalysis);
                    
                session.BestSelector = selectorValidation.SelectBestSelector(session.ValidationResults, 0.5f);
                
                if (session.BestSelector != null)
                {
                    session.IterationHistory.Add($"Found selector from user description: {session.BestSelector.Selector}");
                    return BuildFinalResult(session, new PipelineOptions());
                }
                
                // Let user choose from options
                var validOptions = session.ValidationResults
                    .Where(v => v.IsValid)
                    .OrderByDescending(v => v.MatchQuality)
                    .Take(5)
                    .Select(v => new SelectorOption
                    {
                        Label = v.Selector.Description ?? v.Selector.Selector,
                        Value = v.Selector.Selector,
                        Preview = v.ExtractedSample,
                        Confidence = v.MatchQuality,
                        IsRecommended = v == session.ValidationResults.First(x => x.IsValid)
                    })
                    .ToList();
                    
                if (validOptions.Count > 0)
                {
                    return new PipelineResult
                    {
                        IsSuccess = true,
                        CurrentStage = PipelineStage.SelectorValidation,
                        Session = session,
                        NeedsUserInput = true,
                        UserPrompts = ["I found some possible sections based on your description. Please select one:"],
                        SuggestedOptions = validOptions,
                        Summary = $"Found {validOptions.Count} possible matches"
                    };
                }
            }
            
            // Still couldn't find selectors - offer full page option
            session.IterationHistory.Add("Still no selectors after re-analysis");
            return new PipelineResult
            {
                IsSuccess = true,
                CurrentStage = PipelineStage.SelectorGeneration,
                Session = session,
                NeedsUserInput = true,
                UserPrompts = ["I still couldn't find the specific content. Would you like to monitor the entire page instead, or try describing it differently?"],
                SuggestedOptions =
                [
                    new SelectorOption { Label = "Monitor entire page", Value = "fullpage", IsRecommended = true },
                    new SelectorOption { Label = "Try describing again", Value = "custom" }
                ],
                Summary = "Unable to find specific content from description"
            };
        }
        
        return CreateFailedResult(session, PipelineStage.SelectorGeneration, "No content available to analyze");
    }

    private async Task<PipelineResult?> ExecuteUrlExtractionAsync(
        PipelineSession session,
        CancellationToken ct)
    {
        logger.LogDebug("Stage 1: URL Extraction");

        session.ExtractedUrls = urlExtraction.Extract(session.OriginalInput);
        
        // Extract the natural language intent (text without URLs)
        session.UserIntent = urlExtraction.ExtractUserIntent(session.OriginalInput);
        logger.LogDebug("Extracted user intent: {Intent}", session.UserIntent);

        // Detect numeric thresholds from user intent (e.g., "below $30")
        session.DetectedThresholds = DetectThresholds(session.OriginalInput);
        if (session.DetectedThresholds.Count > 0)
        {
            logger.LogInformation("Detected {Count} threshold(s): {Thresholds}",
                session.DetectedThresholds.Count,
                string.Join(", ", session.DetectedThresholds.Select(t => t.OriginalText)));
        }

        if (session.ExtractedUrls.Count == 0)
        {
            session.FailedUrlExtractionAttempts++;
            
            var (message, options) = session.FailedUrlExtractionAttempts switch
            {
                1 => ("I couldn't find a URL in your input. Please provide the website URL you want to monitor.",
                      (List<SelectorOption>?)null),
                2 => ("I still couldn't find a valid URL. Try including the full address starting with https:// — for example:\n• https://example.com/products\n• https://news.ycombinator.com",
                      (List<SelectorOption>?)null),
                _ => ("I'm having trouble finding a URL in your messages. You can paste a URL directly, or I can help you set it up manually.",
                      (List<SelectorOption>?)
                      [
                          new SelectorOption { Label = "Enter URL manually", Value = "manual_url", IsRecommended = true },
                          new SelectorOption { Label = "Cancel setup", Value = "cancel" }
                      ])
            };
            
            return new PipelineResult
            {
                IsSuccess = false,
                CurrentStage = PipelineStage.UrlExtraction,
                Session = session,
                NeedsUserInput = true,
                UserPrompts = [message],
                SuggestedOptions = options ?? [],
                ErrorMessage = "No URL found in input"
            };
        }

        // URL found — reset failed extraction counter
        session.FailedUrlExtractionAttempts = 0;

        if (session.ExtractedUrls.Count > 1)
        {
            // Multiple URLs found - ask user to confirm
            session.SelectedUrl = urlExtraction.SelectPrimaryUrl(session.ExtractedUrls, session.OriginalInput);
            
            var options = session.ExtractedUrls.Select((u, i) => new SelectorOption
            {
                Label = u.NormalizedUrl,
                Value = u.NormalizedUrl,
                Preview = u.Context,
                IsRecommended = u == session.SelectedUrl
            }).ToList();

            return new PipelineResult
            {
                IsSuccess = true,
                CurrentStage = PipelineStage.UrlExtraction,
                Session = session,
                NeedsUserInput = true,
                UserPrompts = ["I found multiple URLs. Which one would you like to monitor?"],
                SuggestedOptions = options,
                Summary = $"Found {session.ExtractedUrls.Count} URLs"
            };
        }

        session.SelectedUrl = session.ExtractedUrls[0];
        session.IterationHistory.Add($"Extracted URL: {session.SelectedUrl.NormalizedUrl}");
        session.IterationHistory.Add($"User intent: {session.UserIntent}");
        
        return null; // Continue to next stage
    }

    private async Task<PipelineResult?> ExecuteContentFetchingAsync(
        PipelineSession session,
        PipelineOptions options,
        CancellationToken ct)
    {
        logger.LogDebug("Stage 2: Content Fetching");

        if (session.SelectedUrl == null)
            return CreateFailedResult(session, PipelineStage.ContentFetching, "No URL selected");

        session.FetchedContent = await contentFetching.FetchAsync(
            session.SelectedUrl.NormalizedUrl,
            options.UseJavaScript,
            options.FetchTimeoutSeconds,
            ct);

        if (!session.FetchedContent.IsSuccess)
        {
            // Try with JavaScript if initial fetch failed or was sparse
            if (!options.UseJavaScript)
            {
                logger.LogInformation("Retrying with JavaScript rendering");
                session.FetchedContent = await contentFetching.RetryWithJavaScriptAsync(
                    session.FetchedContent,
                    options.FetchTimeoutSeconds,
                    ct);
            }
        }

        if (!session.FetchedContent.IsSuccess)
        {
            return new PipelineResult
            {
                IsSuccess = false,
                CurrentStage = PipelineStage.ContentFetching,
                Session = session,
                ErrorMessage = session.FetchedContent.ErrorMessage ?? "Failed to fetch content",
                Summary = $"Could not access {session.SelectedUrl.NormalizedUrl}"
            };
        }

        // Check if we should retry with JS
        if (!session.FetchedContent.UsedJavaScript && 
            contentFetching.ShouldUseJavaScript(session.FetchedContent))
        {
            logger.LogInformation("Content appears to need JavaScript, re-fetching");
            session.FetchedContent = await contentFetching.RetryWithJavaScriptAsync(
                session.FetchedContent,
                options.FetchTimeoutSeconds,
                ct);
        }

        session.IterationHistory.Add($"Fetched content: {session.FetchedContent.TextContent?.Length ?? 0} chars, JS={session.FetchedContent.UsedJavaScript}");

        return null; // Continue
    }

    private async Task<PipelineResult?> ExecuteContentAnalysisAsync(
        PipelineSession session,
        CancellationToken ct)
    {
        logger.LogDebug("Stage 3: Content Analysis");

        if (session.FetchedContent == null)
            return CreateFailedResult(session, PipelineStage.ContentAnalysis, "No content fetched");

        // Pass the extracted user intent (without URL) for better LLM understanding
        var intentToAnalyze = !string.IsNullOrWhiteSpace(session.UserIntent) 
            ? session.UserIntent 
            : session.OriginalInput;

        session.ContentAnalysis = await contentAnalysis.AnalyzeAsync(
            session.FetchedContent,
            intentToAnalyze,
            ct);

        // Build a more descriptive history entry
        var sectionsFound = session.ContentAnalysis.IdentifiedSections.Count;
        var targetSections = session.ContentAnalysis.IdentifiedSections.Count(s => s.IsLikelyTarget);
        var sectionsInfo = sectionsFound > 0 
            ? $", found {sectionsFound} sections ({targetSections} targets)" 
            : ", no clear sections identified";
            
        session.IterationHistory.Add(
            $"Analysis: {session.ContentAnalysis.ContentType}, intent=\"{TruncateForLog(session.ContentAnalysis.UserIntent ?? "", 50)}\"{sectionsInfo}");

        return null; // Continue
    }

    private async IAsyncEnumerable<PipelineProgress> ExecuteContentAnalysisStreamingAsync(
        PipelineSession session,
        [EnumeratorCancellation] CancellationToken ct)
    {
        logger.LogDebug("Stage 3: Content Analysis (streaming)");

        if (session.FetchedContent == null)
        {
            yield return CreateFailedProgress(session, PipelineStage.ContentAnalysis, "No content fetched");
            yield break;
        }

        var intentToAnalyze = !string.IsNullOrWhiteSpace(session.UserIntent) 
            ? session.UserIntent 
            : session.OriginalInput;

        string? currentStep = null;
        var thinkingBuffer = new System.Text.StringBuilder();
        
        // Stage-level timeout for content analysis (3 LLM calls)
        using var stageCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stageCts.CancelAfter(TimeSpan.FromMinutes(3));
        
        await foreach (var progress in contentAnalysis.AnalyzeStreamingAsync(
            session.FetchedContent,
            intentToAnalyze,
            stageCts.Token))
        {
            // Track step transitions
            if (progress.Step != currentStep && progress.Status == "Starting")
            {
                // Output buffered thinking from previous step (if any)
                if (thinkingBuffer.Length > 0 && currentStep != null)
                {
                    yield return new PipelineProgress
                    {
                        Stage = PipelineStage.ContentAnalysis,
                        Type = ProgressType.Thinking,
                        Summary = thinkingBuffer.ToString(),
                        Details = currentStep,
                        Session = session
                    };
                    thinkingBuffer.Clear();
                }
                
                var stepName = progress.Step switch
                {
                    "ContentClassification" => "Classifying content type",
                    "IntentExtraction" => "Understanding your intent",
                    "SectionIdentification" => "Identifying page sections",
                    _ => progress.Step
                };
                
                currentStep = progress.Step;
                
                yield return new PipelineProgress
                {
                    Stage = PipelineStage.ContentAnalysis,
                    Type = ProgressType.InProgress,
                    Summary = stepName + "...",
                    Session = session
                };
            }
            
            // Buffer thinking content instead of streaming each token
            if (progress.Status == "Thinking" && !string.IsNullOrEmpty(progress.ThinkingContent))
            {
                thinkingBuffer.Append(progress.ThinkingContent);
            }
            
            // When step completes, output buffered thinking
            if (progress.Status == "Completed" && thinkingBuffer.Length > 0 && currentStep != null)
            {
                yield return new PipelineProgress
                {
                    Stage = PipelineStage.ContentAnalysis,
                    Type = ProgressType.Thinking,
                    Summary = thinkingBuffer.ToString(),
                    Details = currentStep,
                    Session = session
                };
                thinkingBuffer.Clear();
            }
            
            // Capture final result
            if (progress.Result != null)
            {
                session.ContentAnalysis = progress.Result;
                
                var sectionsFound = session.ContentAnalysis.IdentifiedSections.Count;
                var targetSections = session.ContentAnalysis.IdentifiedSections.Count(s => s.IsLikelyTarget);
                var sectionsInfo = sectionsFound > 0 
                    ? $", found {sectionsFound} sections ({targetSections} targets)" 
                    : ", no clear sections identified";
                    
                session.IterationHistory.Add(
                    $"Analysis: {session.ContentAnalysis.ContentType}, intent=\"{TruncateForLog(session.ContentAnalysis.UserIntent ?? "", 50)}\"{sectionsInfo}");
            }
        }
    }

    private async Task ExecuteSchemaDiscoveryAsync(
        PipelineSession session,
        CancellationToken ct)
    {
        logger.LogDebug("Stage 3.5: Schema Discovery");

        if (session.FetchedContent == null || session.ContentAnalysis == null)
            return;

        if (!SchemaDiscoveryStage.ShouldDiscoverSchema(session.ContentAnalysis.ContentType))
            return;

        // Stage-level timeout to prevent hanging on slow LLM calls
        using var stageCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stageCts.CancelAfter(TimeSpan.FromMinutes(3));

        try
        {
            var discoveredSchema = await schemaDiscovery.DiscoverAsync(
                session.FetchedContent,
                session.ContentAnalysis,
                stageCts.Token);

            if (discoveredSchema != null)
            {
                session.DiscoveredSchema = discoveredSchema;
                session.SchemaEnabled = true;
                session.IterationHistory.Add(
                    $"Schema: Discovered {discoveredSchema.Fields.Count} fields for {discoveredSchema.SampleItemCount} items");
            }
            else
            {
                session.SchemaEnabled = false;
                session.IterationHistory.Add("Schema: Could not discover schema for this content");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Schema discovery timed out after 3 minutes, continuing without schema");
            session.SchemaEnabled = false;
            session.IterationHistory.Add("Schema: Discovery timed out, continuing without schema");
        }
    }

    private async Task<PipelineResult?> ExecuteSelectorIterationAsync(
        PipelineSession session,
        PipelineOptions options,
        CancellationToken ct)
    {
        logger.LogDebug("Stage 4-5: Selector Generation and Validation");

        if (session.FetchedContent == null || session.ContentAnalysis == null)
            return CreateFailedResult(session, PipelineStage.SelectorGeneration, "Missing content or analysis");

        // Stage-level timeout for selector generation (may involve multiple LLM iterations).
        // Scale timeout based on content size — complex pages need more time for LLM processing.
        var contentSize = (session.FetchedContent.CleanedHtml ?? session.FetchedContent.Html ?? "").Length;
        var timeoutMinutes = contentSize > 20_000 ? 8 : 5;
        using var stageCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stageCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));

        if (contentSize > 20_000)
        {
            logger.LogInformation(
                "Large page detected ({Size} chars) — using extended {Timeout}min timeout for selector generation",
                contentSize, timeoutMinutes);
        }

        var maxIterations = options.MaxIterations > 0 ? options.MaxIterations : DefaultMaxIterations;
        var minConfidence = options.MinConfidence > 0 ? options.MinConfidence : DefaultMinConfidence;

        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            session.CurrentIteration = iteration + 1;
            logger.LogDebug("Selector iteration {Iteration}/{Max}", session.CurrentIteration, maxIterations);

            // Generate selectors
            List<GeneratedSelector> selectors;
            if (iteration == 0)
            {
                selectors = await selectorGeneration.GenerateSelectorsAsync(
                    session.FetchedContent,
                    session.ContentAnalysis,
                    stageCts.Token);
            }
            else
            {
                // Refine based on previous validation
                selectors = await selectorGeneration.RefineSelectorsAsync(
                    session.FetchedContent,
                    session.ContentAnalysis,
                    session.ValidationResults,
                    stageCts.Token);
            }

            if (selectors.Count == 0)
            {
                if (iteration == 0)
                {
                    // Build a more helpful message about what was analyzed
                    var contentInfo = session.ContentAnalysis != null
                        ? $"I analyzed this {session.ContentAnalysis.ContentType} page"
                        : "I analyzed the page";
                    
                    var htmlInfo = session.FetchedContent != null
                        ? $" ({(session.FetchedContent.Html?.Length ?? 0):N0} chars of HTML)"
                        : "";
                    
                    var intentInfo = !string.IsNullOrEmpty(session.ContentAnalysis?.UserIntent)
                        ? $" looking for: \"{session.ContentAnalysis.UserIntent}\""
                        : "";

                    session.IterationHistory.Add($"No selectors generated - {contentInfo}{htmlInfo}{intentInfo}");
                    
                    // If first iteration failed, return with monitor-all suggestion
                    return new PipelineResult
                    {
                        IsSuccess = true,
                        CurrentStage = PipelineStage.SelectorGeneration,
                        Session = session,
                        NeedsUserInput = true,
                        UserPrompts = [$"I couldn't identify specific content to monitor. Would you like to monitor the entire page for changes?"],
                        SuggestedOptions =
                        [
                            new SelectorOption { Label = "Monitor entire page", Value = "fullpage", IsRecommended = true },
                            new SelectorOption { Label = "Help me specify what to watch", Value = "help" },
                            new SelectorOption { Label = "Type something else...", Value = "custom", Preview = "Describe what you want to watch" }
                        ],
                        Summary = $"{contentInfo}{intentInfo} but couldn't find a specific section to monitor"
                    };
                }
                break; // No more refinements possible
            }

            session.GeneratedSelectors = selectors;
            session.IterationHistory.Add($"Iteration {session.CurrentIteration}: Generated {selectors.Count} selectors");

            // Validate selectors
            session.ValidationResults = selectorValidation.ValidateSelectors(
                session.FetchedContent,
                selectors,
                session.ContentAnalysis);

            // Check if we have a good enough selector
            session.BestSelector = selectorValidation.SelectBestSelector(session.ValidationResults, minConfidence);

            if (session.BestSelector != null)
            {
                var bestValidation = session.ValidationResults.First(v => v.Selector == session.BestSelector);
                session.IterationHistory.Add($"Found good selector: {session.BestSelector.Selector} (quality={bestValidation.MatchQuality:F2})");
                break; // Success!
            }

            // Check if we need refinement
            if (!selectorValidation.NeedsRefinement(session.ValidationResults, minConfidence))
            {
                // Take the best we have
                session.BestSelector = session.ValidationResults
                    .Where(v => v.IsValid)
                    .OrderByDescending(v => v.MatchQuality)
                    .FirstOrDefault()?.Selector;
                break;
            }

            session.IterationHistory.Add($"Iteration {session.CurrentIteration}: Selectors need refinement");
        }

        // If no good selector after all iterations, ask user
        if (session.BestSelector == null && session.ValidationResults.Any(v => v.IsValid))
        {
            var validOptions = session.ValidationResults
                .Where(v => v.IsValid)
                .OrderByDescending(v => v.MatchQuality)
                .Take(5)
                .Select(v => new SelectorOption
                {
                    Label = v.Selector.Description ?? v.Selector.Selector,
                    Value = v.Selector.Selector,
                    Preview = v.ExtractedSample,
                    Confidence = v.MatchQuality,
                    IsRecommended = v == session.ValidationResults.First(x => x.IsValid)
                })
                .ToList();

            return new PipelineResult
            {
                IsSuccess = true,
                CurrentStage = PipelineStage.SelectorValidation,
                Session = session,
                NeedsUserInput = true,
                UserPrompts = ["I found some possible sections to monitor. Please select the one you want:"],
                SuggestedOptions = validOptions,
                Summary = $"Found {validOptions.Count} possible monitoring targets"
            };
        }

        return null; // Continue to final stage
    }

    private async Task<PipelineResult> HandleUrlSelectionFeedbackAsync(
        PipelineSession session,
        string feedback,
        CancellationToken ct)
    {
        // Find the URL that matches the feedback
        var selectedUrl = session.ExtractedUrls.FirstOrDefault(u =>
            u.NormalizedUrl.Equals(feedback, StringComparison.OrdinalIgnoreCase) ||
            u.Url.Equals(feedback, StringComparison.OrdinalIgnoreCase));

        if (selectedUrl == null)
        {
            return new PipelineResult
            {
                IsSuccess = false,
                CurrentStage = PipelineStage.UrlExtraction,
                Session = session,
                NeedsUserInput = true,
                UserPrompts = ["I didn't recognize that URL. Please select from the options or provide a valid URL."],
                SuggestedOptions = session.ExtractedUrls.Select(u => new SelectorOption
                {
                    Label = u.NormalizedUrl,
                    Value = u.NormalizedUrl
                }).ToList()
            };
        }

        session.SelectedUrl = selectedUrl;
        session.IterationHistory.Add($"User selected URL: {selectedUrl.NormalizedUrl}");

        // Continue with the rest of the pipeline
        return await ProcessAsync(session.OriginalInput, new PipelineOptions(), ct);
    }

    private async Task<PipelineResult> HandleSelectorFeedbackAsync(
        PipelineSession session,
        string feedback,
        CancellationToken ct)
    {
        // Check if user selected a specific selector
        var selectedSelector = session.GeneratedSelectors.FirstOrDefault(s =>
            s.Selector.Equals(feedback, StringComparison.OrdinalIgnoreCase));

        if (selectedSelector != null)
        {
            session.BestSelector = selectedSelector;
            session.IterationHistory.Add($"User selected selector: {selectedSelector.Selector}");
            return BuildFinalResult(session, new PipelineOptions());
        }

        // Check for special commands
        if (feedback.Equals("fullpage", StringComparison.OrdinalIgnoreCase))
        {
            session.BestSelector = null; // Full page monitoring
            session.IterationHistory.Add("User chose full page monitoring");
            return BuildFinalResult(session, new PipelineOptions());
        }

        // Treat as custom selector or description
        session.GeneratedSelectors.Add(new GeneratedSelector
        {
            Selector = feedback,
            Type = DetermineSelectorType(feedback),
            Description = "User-provided selector",
            Confidence = 0.5f,
            Priority = 0
        });

        // Validate the user's selector
        if (session.FetchedContent != null && session.ContentAnalysis != null)
        {
            var validation = selectorValidation.ValidateSelectors(
                session.FetchedContent,
                [session.GeneratedSelectors.Last()],
                session.ContentAnalysis);

            if (validation.Any(v => v.IsValid))
            {
                session.BestSelector = session.GeneratedSelectors.Last();
                session.IterationHistory.Add($"User selector validated: {feedback}");
                return BuildFinalResult(session, new PipelineOptions());
            }

            return new PipelineResult
            {
                IsSuccess = false,
                CurrentStage = PipelineStage.SelectorValidation,
                Session = session,
                NeedsUserInput = true,
                UserPrompts = [$"The selector '{feedback}' didn't match any content. Please try a different one or describe what you want to monitor."],
                ErrorMessage = validation.FirstOrDefault()?.ValidationMessage
            };
        }

        return CreateFailedResult(session, PipelineStage.SelectorValidation, "Unable to validate selector");
    }

    private PipelineResult BuildFinalResult(PipelineSession session, PipelineOptions options)
    {
        if (session.SelectedUrl == null)
            return CreateFailedResult(session, PipelineStage.Complete, "No URL selected");

        var config = new WatchConfiguration
        {
            Url = session.SelectedUrl.NormalizedUrl,
            Name = session.FetchedContent?.Title ?? session.SelectedUrl.NormalizedUrl,
            Description = session.ContentAnalysis?.UserIntent,
            UseJavaScript = session.FetchedContent?.UsedJavaScript ?? options.UseJavaScript,
            Confidence = session.ContentAnalysis?.Confidence ?? 0.5f
        };

        if (session.BestSelector != null)
        {
            switch (session.BestSelector.Type)
            {
                case SelectorType.CssSelector:
                    config.CssSelector = session.BestSelector.Selector;
                    break;
                case SelectorType.XPath:
                    config.XPathSelector = session.BestSelector.Selector;
                    break;
                case SelectorType.TextPattern:
                    config.TextPattern = session.BestSelector.Selector;
                    break;
            }
        }

        // Suggest check interval based on content type
        config.CheckInterval = session.ContentAnalysis?.ContentType switch
        {
            ContentType.StatusPage => TimeSpan.FromMinutes(5),
            ContentType.PriceInfo => TimeSpan.FromMinutes(15),
            ContentType.NewsList or ContentType.EventList => TimeSpan.FromMinutes(30),
            _ => TimeSpan.FromHours(1)
        };

        // Add tags based on content type
        if (session.ContentAnalysis != null)
        {
            config.Tags.Add(session.ContentAnalysis.ContentType.ToString().ToLowerInvariant());
        }

        // Map discovered schema to extraction schema
        if (session.DiscoveredSchema != null && session.SchemaEnabled == true)
        {
            config.SchemaEnabled = true;
            config.Schema = ConvertToExtractionSchema(session.DiscoveredSchema);
        }

        // Create filter rules from user intent
        config.FilterRules = BuildFilterRulesFromIntent(session);
        
        // Check if any filter keywords already match the fetched content
        config.ImmediateMatches = FindImmediateMatches(session);

        var summary = BuildSummary(session, config);

        return new PipelineResult
        {
            IsSuccess = true,
            CurrentStage = PipelineStage.Complete,
            Session = session,
            FinalConfiguration = config,
            Summary = summary
        };
    }

    /// <summary>
    /// Checks if any filter keywords from user intent already appear in the fetched content.
    /// Returns keywords that match, enabling the UI to tell the user immediately.
    /// </summary>
    private static List<string> FindImmediateMatches(PipelineSession session)
    {
        var keywords = session.ContentAnalysis?.FilterKeywords ?? [];
        if (keywords.Count == 0)
            return [];

        var textContent = session.FetchedContent?.TextContent ?? session.FetchedContent?.Html ?? "";
        if (string.IsNullOrWhiteSpace(textContent))
            return [];

        return keywords
            .Where(keyword => textContent.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string BuildSummary(PipelineSession session, WatchConfiguration config)
    {
        var parts = new List<string>
        {
            $"Watch configured for {config.Url}"
        };

        if (!string.IsNullOrEmpty(config.CssSelector))
            parts.Add($"using CSS selector '{config.CssSelector}'");
        else if (!string.IsNullOrEmpty(config.XPathSelector))
            parts.Add($"using XPath '{config.XPathSelector}'");
        else
            parts.Add("monitoring full page");

        if (config.UseJavaScript)
            parts.Add("with JavaScript rendering");

        parts.Add($"checking every {config.CheckInterval?.TotalMinutes ?? 60} minutes");

        var summary = string.Join(", ", parts) + ".";
        
        // Append immediate match notice if keywords already found in content
        if (config.ImmediateMatches.Count > 0)
        {
            var matchList = string.Join(", ", config.ImmediateMatches.Select(m => $"'{m}'"));
            summary += $" ⚡ Note: {matchList} already appears in the current page content. You'll be notified of any future changes.";
        }

        return summary;
    }

    /// <summary>
    /// Creates filter rules from the LLM-extracted filter keywords and discovered schema.
    /// For schema-enabled watches: creates field-level conditions against schema fields.
    /// For text-only watches: creates a content-contains condition.
    /// </summary>
    private static List<FilterRule> BuildFilterRulesFromIntent(PipelineSession session)
    {
        var keywords = session.ContentAnalysis?.FilterKeywords ?? [];
        var thresholds = session.DetectedThresholds;
        
        if (keywords.Count == 0 && thresholds.Count == 0)
            return [];

        var rules = new List<FilterRule>();

        // First, create threshold-based rules (numeric conditions like "below $30")
        if (thresholds.Count > 0 && session.SchemaEnabled == true && session.DiscoveredSchema?.Fields.Count > 0)
        {
            foreach (var threshold in thresholds)
            {
                // Find matching numeric field in schema
                var numericFields = session.DiscoveredSchema.Fields
                    .Where(f => f.Type is "number" or "price" or "decimal" or "integer" or "currency")
                    .ToList();

                // If we have a field hint, try to match it
                var targetField = threshold.FieldHint != null
                    ? numericFields.FirstOrDefault(f => f.Name.Contains(threshold.FieldHint, StringComparison.OrdinalIgnoreCase))
                      ?? numericFields.FirstOrDefault()
                    : numericFields.FirstOrDefault();

                if (targetField != null)
                {
                    var opLabel = threshold.Operator == FilterOperator.LessThan ? "drops below" : "rises above";
                    rules.Add(new FilterRule
                    {
                        Name = $"Threshold: {targetField.Name} {opLabel} {threshold.Value}",
                        Description = $"Notify when {targetField.Name} {opLabel} {threshold.Value} (from: \"{threshold.OriginalText}\")",
                        Conditions =
                        [
                            new FilterCondition
                            {
                                FieldName = targetField.Name,
                                Operator = threshold.Operator,
                                Value = threshold.Value.ToString(CultureInfo.InvariantCulture)
                            }
                        ],
                        Actions =
                        [
                            new FilterAction
                            {
                                Type = FilterActionType.ImmediateNotify,
                                Parameters = new Dictionary<string, string>
                                {
                                    ["reason"] = $"{targetField.Name} {opLabel} {threshold.Value}"
                                }
                            }
                        ],
                        Priority = 200, // Higher priority than keyword rules
                        IsEnabled = true
                    });

                    // Remove threshold value from keywords to avoid duplicate rules
                    keywords = keywords.Where(k => k != threshold.Value.ToString(CultureInfo.InvariantCulture)
                        && k != $"${threshold.Value}").ToList();
                }
            }
        }

        if (keywords.Count == 0)
            return rules;

        if (session.SchemaEnabled == true && session.DiscoveredSchema?.Fields.Count > 0)
        {
            // Schema-enabled: create a rule that checks extracted object fields for keywords
            var textFields = session.DiscoveredSchema.Fields
                .Where(f => f.Type is "string" or "text")
                .Select(f => f.Name)
                .ToList();

            if (textFields.Count == 0)
            {
                // No text fields in schema — fall back to checking all fields
                textFields = session.DiscoveredSchema.Fields.Select(f => f.Name).ToList();
            }

            foreach (var keyword in keywords)
            {
                var conditions = textFields.Select(field => new FilterCondition
                {
                    FieldName = field,
                    Operator = FilterOperator.Contains,
                    Value = keyword
                }).ToList();

                rules.Add(new FilterRule
                {
                    Name = $"Match: {keyword}",
                    Description = $"Notify when any field contains '{keyword}' (from user intent: {session.ContentAnalysis?.UserIntent})",
                    Logic = FilterLogic.Or,
                    Conditions = conditions,
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
        }
        else
        {
            // Text-only watch: create a rule that checks raw content changes
            // Use $content special field which matches against the full text diff
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
        }

        return rules;
    }

    /// <summary>
    /// Detects numeric threshold conditions from the user's original input.
    /// Matches patterns like "below $30", "under 50", "above €100", "less than 25.99".
    /// </summary>
    private static List<DetectedThreshold> DetectThresholds(string userIntent)
    {
        if (string.IsNullOrWhiteSpace(userIntent))
            return [];

        var thresholds = new List<DetectedThreshold>();

        // Pattern: (below|under|less than|cheaper than) [$€£]?NUMBER
        var belowPattern = new Regex(
            @"(?:below|under|less\s+than|cheaper\s+than|drops?\s+(?:below|under|to))\s*[\$€£]?\s*(\d+(?:[.,]\d+)?)",
            RegexOptions.IgnoreCase);

        foreach (Match match in belowPattern.Matches(userIntent))
        {
            if (decimal.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            {
                thresholds.Add(new DetectedThreshold(
                    FieldHint: DetectFieldHint(userIntent, match),
                    Operator: FilterOperator.LessThan,
                    Value: value,
                    OriginalText: match.Value.Trim()));
            }
        }

        // Pattern: (above|over|more than|exceeds) [$€£]?NUMBER
        var abovePattern = new Regex(
            @"(?:above|over|more\s+than|exceeds?|rises?\s+(?:above|over|to))\s*[\$€£]?\s*(\d+(?:[.,]\d+)?)",
            RegexOptions.IgnoreCase);

        foreach (Match match in abovePattern.Matches(userIntent))
        {
            if (decimal.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            {
                thresholds.Add(new DetectedThreshold(
                    FieldHint: DetectFieldHint(userIntent, match),
                    Operator: FilterOperator.GreaterThan,
                    Value: value,
                    OriginalText: match.Value.Trim()));
            }
        }

        return thresholds;
    }

    /// <summary>
    /// Tries to infer which field a threshold applies to based on nearby words.
    /// </summary>
    private static string? DetectFieldHint(string input, Match thresholdMatch)
    {
        // Look at words near the threshold for field hints
        var start = Math.Max(0, thresholdMatch.Index - 40);
        var context = input[start..thresholdMatch.Index].ToLowerInvariant();

        if (context.Contains("price") || context.Contains("cost") || context.Contains("$") || context.Contains("€") || context.Contains("£"))
            return "price";
        if (context.Contains("stock") || context.Contains("inventory") || context.Contains("quantity"))
            return "stock";
        if (context.Contains("rating") || context.Contains("score") || context.Contains("review"))
            return "rating";
        if (context.Contains("discount") || context.Contains("sale") || context.Contains("off"))
            return "price";

        return null;
    }

    private static PipelineResult CreateFailedResult(PipelineSession session, PipelineStage stage, string error)
    {
        return new PipelineResult
        {
            IsSuccess = false,
            CurrentStage = stage,
            Session = session,
            ErrorMessage = error
        };
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
    /// Attempts LLM-powered recovery from a failure with 3 phases:
    /// 1. Diagnose: LLM explains failure in ≤50 chars
    /// 2. Retry: Re-execute with diagnostic context
    /// 3. Ask User: Generate clarifying question if retry fails
    /// </summary>
    public async Task<PipelineResult> RecoverFromFailureAsync(
        PipelineSession session,
        PipelineResult failedResult,
        PipelineOptions options,
        CancellationToken ct = default)
    {
        var maxAttempts = options.MaxRecoveryAttempts;
        if (session.RecoveryAttempts >= maxAttempts)
        {
            logger.LogWarning("Max recovery attempts ({Max}) reached", maxAttempts);
            return await AskUserForHelpAsync(session, failedResult, ct);
        }

        session.RecoveryAttempts++;
        session.LastRecoveryError = failedResult.ErrorMessage;
        logger.LogInformation("Recovery attempt {Attempt}/{Max} for stage {Stage}", 
            session.RecoveryAttempts, maxAttempts, failedResult.CurrentStage);

        // Phase 1: Diagnose the failure
        var diagnosis = await DiagnoseFailureAsync(session, failedResult, ct);
        session.RecoveryDiagnosticContext = diagnosis;
        session.IterationHistory.Add($"Recovery: {diagnosis}");

        // Phase 2: Retry with diagnostic context
        var retryResult = await RetryWithDiagnosticAsync(session, failedResult.CurrentStage, options, ct);
        
        if (retryResult.IsSuccess || !retryResult.ErrorMessage?.Contains("Recovery") == true)
        {
            logger.LogInformation("Recovery successful at attempt {Attempt}", session.RecoveryAttempts);
            return retryResult;
        }

        // Phase 3: Ask user for help if retry failed
        if (session.RecoveryAttempts >= maxAttempts)
        {
            return await AskUserForHelpAsync(session, retryResult, ct);
        }

        // More attempts available, return partial failure
        return retryResult;
    }

    private async Task<string> DiagnoseFailureAsync(
        PipelineSession session,
        PipelineResult failedResult,
        CancellationToken ct)
    {
        var prompt = $"""
            Diagnose briefly (max 50 chars) why this failed.
            Stage: {failedResult.CurrentStage}
            Error: {failedResult.ErrorMessage}
            Input: {TruncateForLog(session.OriginalInput, 200)}
            History: {string.Join("; ", session.IterationHistory.TakeLast(3))}
            Reply with ONLY the diagnosis, no explanation.
            """;

        var response = await llmProvider.ExecuteAsync(prompt, new LlmRequestOptions
        {
            MaxTokens = 30,
            Temperature = 0.1f,
            CompactMode = true
        }, ct);

        // LLM-only: If LLM fails, propagate the error instead of using heuristic fallback
        if (!response.IsSuccess)
        {
            logger.LogWarning("LLM diagnosis failed: {Error}", response.ErrorMessage);
            throw new InvalidOperationException($"LLM diagnosis unavailable: {response.ErrorMessage}");
        }

        var diagnosis = response.Content?.Trim() ?? throw new InvalidOperationException("LLM returned empty diagnosis");
        // Enforce 50 char limit
        return diagnosis.Length > 50 ? diagnosis[..50] : diagnosis;
    }

    private async Task<PipelineResult> RetryWithDiagnosticAsync(
        PipelineSession session,
        PipelineStage failedStage,
        PipelineOptions options,
        CancellationToken ct)
    {
        logger.LogDebug("Retrying stage {Stage} with diagnostic context: {Context}", 
            failedStage, session.RecoveryDiagnosticContext);

        try
        {
            return failedStage switch
            {
                PipelineStage.UrlExtraction => await RetryUrlExtractionAsync(session, ct),
                PipelineStage.ContentFetching => await RetryContentFetchingAsync(session, options, ct),
                PipelineStage.ContentAnalysis => await RetryContentAnalysisAsync(session, ct),
                PipelineStage.SelectorGeneration or PipelineStage.SelectorValidation 
                    => await RetrySelectorGenerationAsync(session, options, ct),
                _ => CreateFailedResult(session, failedStage, "Recovery not supported for this stage")
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Retry failed for stage {Stage}", failedStage);
            return CreateFailedResult(session, failedStage, $"Recovery retry failed: {ex.Message}");
        }
    }

    private async Task<PipelineResult> RetryUrlExtractionAsync(PipelineSession session, CancellationToken ct)
    {
        // Ask LLM to help extract URL with more context
        var prompt = $"""
            Extract a valid URL from this input. Previous attempt failed.
            Input: {session.OriginalInput}
            Diagnostic: {session.RecoveryDiagnosticContext}
            Reply with ONLY the URL, nothing else.
            """;

        var response = await llmProvider.ExecuteAsync(prompt, new LlmRequestOptions
        {
            MaxTokens = 100,
            Temperature = 0.1f,
            CompactMode = true
        }, ct);

        var extractedUrl = response.Content?.Trim() ?? "";
        if (Uri.TryCreate(extractedUrl, UriKind.Absolute, out var uri) && 
            (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            session.ExtractedUrls =
            [
                new ExtractedUrl { Url = extractedUrl, NormalizedUrl = extractedUrl, IsValid = true }
            ];
            session.SelectedUrl = session.ExtractedUrls[0];
            session.IterationHistory.Add($"Recovery extracted URL: {extractedUrl}");
            
            // Continue with rest of pipeline
            var result = await ExecuteContentFetchingAsync(session, new PipelineOptions(), ct);
            if (result != null) return result;
            
            result = await ExecuteContentAnalysisAsync(session, ct);
            if (result != null) return result;
            
            result = await ExecuteSelectorIterationAsync(session, new PipelineOptions(), ct);
            if (result != null) return result;
            
            return BuildFinalResult(session, new PipelineOptions());
        }

        return CreateFailedResult(session, PipelineStage.UrlExtraction, "Recovery: Could not extract valid URL");
    }

    private async Task<PipelineResult> RetryContentFetchingAsync(
        PipelineSession session,
        PipelineOptions options,
        CancellationToken ct)
    {
        // Try with different options based on diagnostic
        var useJs = session.RecoveryDiagnosticContext?.Contains("JavaScript", StringComparison.OrdinalIgnoreCase) == true ||
                    session.RecoveryDiagnosticContext?.Contains("dynamic", StringComparison.OrdinalIgnoreCase) == true ||
                    !options.UseJavaScript;

        session.FetchedContent = await contentFetching.FetchAsync(
            session.SelectedUrl!.NormalizedUrl,
            useJs,
            options.FetchTimeoutSeconds + 10, // Give more time on retry
            ct);

        if (!session.FetchedContent.IsSuccess)
        {
            return CreateFailedResult(session, PipelineStage.ContentFetching, 
                $"Recovery: {session.FetchedContent.ErrorMessage}");
        }

        session.IterationHistory.Add($"Recovery fetched content: {session.FetchedContent.TextContent?.Length ?? 0} chars");

        // Continue with rest of pipeline
        var result = await ExecuteContentAnalysisAsync(session, ct);
        if (result != null) return result;

        result = await ExecuteSelectorIterationAsync(session, options, ct);
        if (result != null) return result;

        return BuildFinalResult(session, options);
    }

    private async Task<PipelineResult> RetryContentAnalysisAsync(PipelineSession session, CancellationToken ct)
    {
        if (session.FetchedContent == null)
            return CreateFailedResult(session, PipelineStage.ContentAnalysis, "No content to analyze");

        // Re-run analysis with recovery context hint
        var intentWithContext = $"{session.UserIntent} (Note: {session.RecoveryDiagnosticContext})";
        
        session.ContentAnalysis = await contentAnalysis.AnalyzeAsync(
            session.FetchedContent,
            intentWithContext,
            ct);

        session.IterationHistory.Add($"Recovery analysis: {session.ContentAnalysis.ContentType}");

        // Continue with selector generation
        var result = await ExecuteSelectorIterationAsync(session, new PipelineOptions(), ct);
        if (result != null) return result;

        return BuildFinalResult(session, new PipelineOptions());
    }

    private async Task<PipelineResult> RetrySelectorGenerationAsync(
        PipelineSession session,
        PipelineOptions options,
        CancellationToken ct)
    {
        if (session.FetchedContent == null || session.ContentAnalysis == null)
            return CreateFailedResult(session, PipelineStage.SelectorGeneration, "Missing content or analysis");

        // Add diagnostic context to analysis for better selector generation
        var originalIntent = session.ContentAnalysis.UserIntent;
        session.ContentAnalysis.UserIntent = $"{originalIntent} ({session.RecoveryDiagnosticContext})";

        var selectors = await selectorGeneration.GenerateSelectorsAsync(
            session.FetchedContent,
            session.ContentAnalysis,
            ct);

        // Restore original intent
        session.ContentAnalysis.UserIntent = originalIntent;

        if (selectors.Count == 0)
        {
            return CreateFailedResult(session, PipelineStage.SelectorGeneration, 
                "Recovery: No selectors generated");
        }

        session.GeneratedSelectors = selectors;
        session.ValidationResults = selectorValidation.ValidateSelectors(
            session.FetchedContent,
            selectors,
            session.ContentAnalysis);

        session.BestSelector = selectorValidation.SelectBestSelector(
            session.ValidationResults, 
            options.MinConfidence);

        if (session.BestSelector != null)
        {
            session.IterationHistory.Add($"Recovery found selector: {session.BestSelector.Selector}");
            return BuildFinalResult(session, options);
        }

        return CreateFailedResult(session, PipelineStage.SelectorValidation, 
            "Recovery: No valid selector found");
    }

    private async Task<PipelineResult> AskUserForHelpAsync(
        PipelineSession session,
        PipelineResult failedResult,
        CancellationToken ct)
    {
        // Generate a clarifying question using LLM
        var prompt = $"""
            Generate a clarifying question to help user fix this issue.
            Stage: {failedResult.CurrentStage}
            Error: {failedResult.ErrorMessage}
            User input: {TruncateForLog(session.OriginalInput, 200)}
            History: {string.Join("; ", session.IterationHistory.TakeLast(5))}
            
            Include exactly one marker for input type:
            - [YES/NO] for confirmation
            - [SELECT: option1, option2, option3] for choices
            - [TEXT] for freeform input
            
            Keep question under 100 chars. Reply with ONLY the question.
            """;

        try
        {
            var response = await llmProvider.ExecuteAsync(prompt, new LlmRequestOptions
            {
                MaxTokens = 80,
                Temperature = 0.3f,
                CompactMode = true
            }, ct);

            var question = response.Content?.Trim() ?? "Something went wrong. Can you provide more details? [TEXT]";
            session.IterationHistory.Add($"Asking user: {question}");

            return new PipelineResult
            {
                IsSuccess = false,
                CurrentStage = failedResult.CurrentStage,
                Session = session,
                NeedsUserInput = true,
                UserPrompts = [question],
                ErrorMessage = failedResult.ErrorMessage,
                Summary = session.RecoveryDiagnosticContext
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to generate clarifying question");
            // Fallback to generic question
            return new PipelineResult
            {
                IsSuccess = false,
                CurrentStage = failedResult.CurrentStage,
                Session = session,
                NeedsUserInput = true,
                UserPrompts = ["Something went wrong. Can you provide more details? [TEXT]"],
                ErrorMessage = failedResult.ErrorMessage
            };
        }
    }

    private static SelectorType DetermineSelectorType(string selector)
    {
        if (selector.StartsWith("//") || selector.Contains("::"))
            return SelectorType.XPath;
        if (selector.StartsWith("/") || selector.StartsWith("^"))
            return SelectorType.TextPattern;
        return SelectorType.CssSelector;
    }

    private static string TruncateForLog(string text, int maxLength = 100)
    {
        if (text.Length <= maxLength)
            return text;
        return text[..maxLength] + "...";
    }
    
    #region Event Tracking Helpers
    
    private Task RecordStageStartAsync(Guid runId, string stage, CancellationToken ct)
    {
        return eventService.RecordEventAsync(
            runId, 
            stage, 
            PipelineEventTypes.StageStarted, 
            $"Starting {stage}", 
            ct: ct);
    }
    
    private Task RecordStageCompletedAsync(Guid runId, string stage, string? summary, CancellationToken ct)
    {
        return eventService.RecordEventAsync(
            runId, 
            stage, 
            PipelineEventTypes.StageCompleted, 
            summary ?? $"Completed {stage}", 
            ct: ct);
    }
    
    private Task RecordStageCompletedAsync(Guid runId, string stage, string? summary, string? dataJson, CancellationToken ct)
    {
        return eventService.RecordEventAsync(
            runId, 
            stage, 
            PipelineEventTypes.StageCompleted, 
            summary ?? $"Completed {stage}",
            dataJson: dataJson,
            ct: ct);
    }
    
    private async Task HandleResultAsync(Guid runId, PipelineResult result, CancellationToken ct)
    {
        if (result.NeedsUserInput)
        {
            await eventService.UpdateRunStatusAsync(runId, PipelineRunStatus.AwaitingUserInput, 
                result.CurrentStage.ToString(), ct);
            await eventService.RecordEventAsync(
                runId,
                result.CurrentStage.ToString(),
                PipelineEventTypes.UserInputRequested,
                string.Join("; ", result.UserPrompts),
                ct: ct);
        }
        else if (result.IsSuccess && result.FinalConfiguration != null)
        {
            // Pipeline completed successfully - record config and mark as completed
            var configJson = JsonSerializer.Serialize(result.FinalConfiguration);
            await eventService.RecordEventAsync(
                runId,
                PipelineStageNames.Configuration,
                PipelineEventTypes.ConfigurationBuilt,
                $"Configuration built for {result.FinalConfiguration.Url}",
                dataJson: configJson,
                ct: ct);
            // Mark completed — watchId will be set later when the watch is actually created
            await eventService.UpdateRunStatusAsync(runId, PipelineRunStatus.Completed, 
                PipelineStageNames.Configuration, ct);
        }
        else if (!result.IsSuccess)
        {
            await eventService.RecordFailureAsync(
                runId,
                result.CurrentStage.ToString(),
                result.ErrorMessage ?? "Unknown error",
                ct: ct);
            await eventService.FailRunAsync(runId, result.ErrorMessage ?? "Pipeline failed", ct);
        }
    }
    
    /// <summary>
    /// Gets the current pipeline run ID for external callers to record watch creation.
    /// </summary>
    public static Guid? GetCurrentRunId() => CurrentRun.Value?.Id;
    
    /// <summary>
    /// Completes the current pipeline run when a watch is created.
    /// Should be called by the hub/service that creates the watch.
    /// </summary>
    public async Task CompleteCurrentRunAsync(Guid watchId, string? configJson = null, CancellationToken ct = default)
    {
        var run = CurrentRun.Value;
        if (run != null)
        {
            await eventService.CompleteRunAsync(run.Id, watchId, configJson, ct);
        }
    }
    
    /// <summary>
    /// Records a user interaction event for the current run.
    /// </summary>
    public async Task RecordUserFeedbackAsync(string feedback, CancellationToken ct = default)
    {
        var run = CurrentRun.Value;
        if (run != null)
        {
            await eventService.RecordUserInteractionAsync(
                run.Id,
                PipelineEventTypes.UserInputReceived,
                feedback,
                $"User provided feedback: {TruncateForLog(feedback)}",
                ct);
        }
    }
    
    private static string? SerializeStageData(object data)
    {
        try
        {
            return JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch
        {
            return null;
        }
    }
    
    #endregion
}
