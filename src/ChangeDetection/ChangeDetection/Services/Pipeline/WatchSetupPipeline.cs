using System.Runtime.CompilerServices;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Pipeline;

/// <summary>
/// Orchestrates the multi-stage watch setup pipeline.
/// Manages the flow, iterations, and feedback loops.
/// </summary>
public class WatchSetupPipeline(
    UrlExtractionStage urlExtraction,
    ContentFetchingStage contentFetching,
    ContentAnalysisStage contentAnalysis,
    SelectorGenerationStage selectorGeneration,
    SelectorValidationStage selectorValidation,
    ILlmProviderChain llmProvider,
    ILogger<WatchSetupPipeline> logger) : IWatchSetupPipeline
{
    private const int DefaultMaxIterations = 3;
    private const float DefaultMinConfidence = 0.6f;

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

        logger.LogInformation("Starting pipeline for input: {Input}", TruncateForLog(userInput));

        try
        {
            // Stage 1: Extract URLs
            var result = await ExecuteUrlExtractionAsync(session, ct);
            if (result != null) return result;

            // Stage 2: Fetch Content
            result = await ExecuteContentFetchingAsync(session, options, ct);
            if (result != null) return result;

            // Stage 3: Analyze Content
            result = await ExecuteContentAnalysisAsync(session, ct);
            if (result != null) return result;

            // Stage 4-5: Generate and Validate Selectors (with iteration loop)
            result = await ExecuteSelectorIterationAsync(session, options, ct);
            if (result != null) return result;

            // Stage 6: Build final configuration
            return BuildFinalResult(session, options);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Pipeline cancelled");
            return CreateFailedResult(session, PipelineStage.Failed, "Operation was cancelled");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pipeline failed with exception");
            return CreateFailedResult(session, PipelineStage.Failed, $"Pipeline error: {ex.Message}");
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

        logger.LogInformation("Starting streaming pipeline for input: {Input}", TruncateForLog(userInput));

        PipelineResult? result = null;
        Exception? error = null;

        // Stage 1: URL Extraction
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
            yield return CreateFailedProgress(session, PipelineStage.UrlExtraction, error.Message);
            yield break;
        }

        if (result != null)
        {
            yield return CreateResultProgress(result);
            yield break;
        }

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
            yield return CreateFailedProgress(session, PipelineStage.ContentFetching, error.Message);
            yield break;
        }

        if (result != null)
        {
            yield return CreateResultProgress(result);
            yield break;
        }

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
            yield break;
        }

        if (session.ContentAnalysis == null)
        {
            yield return CreateFailedProgress(session, PipelineStage.ContentAnalysis, "Content analysis failed");
            yield break;
        }

        yield return new PipelineProgress
        {
            Stage = PipelineStage.ContentAnalysis,
            Type = ProgressType.StageCompleted,
            Summary = $"Detected {session.ContentAnalysis?.ContentType}: {session.ContentAnalysis?.UserIntent}",
            Details = $"Found {session.ContentAnalysis?.IdentifiedSections.Count ?? 0} sections",
            Session = session
        };

        // Stage 4-5: Selector Generation and Validation
        error = null;
        yield return new PipelineProgress
        {
            Stage = PipelineStage.SelectorGeneration,
            Type = ProgressType.Starting,
            Summary = "Generating selectors for monitoring...",
            Session = session
        };

        try
        {
            result = await ExecuteSelectorIterationAsync(session, options, ct);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (error != null)
        {
            yield return CreateFailedProgress(session, PipelineStage.SelectorGeneration, error.Message);
            yield break;
        }

        if (result != null)
        {
            yield return CreateResultProgress(result);
            yield break;
        }

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
        var finalResult = BuildFinalResult(session, options);
        yield return CreateResultProgress(finalResult);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PipelineProgress> ContinueWithFeedbackStreamingAsync(
        PipelineSession session,
        string feedback,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        logger.LogInformation("Continuing pipeline with feedback (streaming): {Feedback}", TruncateForLog(feedback));

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
            yield return CreateFailedProgress(session, PipelineStage.Failed, error.Message);
            yield break;
        }

        yield return CreateResultProgress(result!);
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

        if (session.ExtractedUrls.Count == 0)
        {
            return new PipelineResult
            {
                IsSuccess = false,
                CurrentStage = PipelineStage.UrlExtraction,
                Session = session,
                NeedsUserInput = true,
                UserPrompts = ["I couldn't find a URL in your input. Please provide the website URL you want to monitor."],
                ErrorMessage = "No URL found in input"
            };
        }

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
        
        await foreach (var progress in contentAnalysis.AnalyzeStreamingAsync(
            session.FetchedContent,
            intentToAnalyze,
            ct))
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

    private async Task<PipelineResult?> ExecuteSelectorIterationAsync(
        PipelineSession session,
        PipelineOptions options,
        CancellationToken ct)
    {
        logger.LogDebug("Stage 4-5: Selector Generation and Validation");

        if (session.FetchedContent == null || session.ContentAnalysis == null)
            return CreateFailedResult(session, PipelineStage.SelectorGeneration, "Missing content or analysis");

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
                    ct);
            }
            else
            {
                // Refine based on previous validation
                selectors = await selectorGeneration.RefineSelectorsAsync(
                    session.FetchedContent,
                    session.ContentAnalysis,
                    session.ValidationResults,
                    ct);
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

        return string.Join(", ", parts) + ".";
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
}
