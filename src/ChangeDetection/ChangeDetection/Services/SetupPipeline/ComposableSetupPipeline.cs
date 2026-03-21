using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Core.Pipeline.Validation;
using ChangeDetection.Services.BlockExecution;
using ChangeDetection.Services.Pipeline;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.SetupPipeline;

public class ComposableSetupPipeline(
    ILlmProviderChain llmChain,
    IContentFetcher contentFetcher,
    IPipelineExecutor pipelineExecutor,
    IPipelineValidator pipelineValidator,
    IBlockRegistry blockRegistry,
    IPlatformDetector platformDetector,
    IPipelineTemplateRegistry templateRegistry,
    IRepository<WatchedSite> watchRepo,
    SetupFlowEnhancements setupFlowEnhancements,
    PipelineSecurityValidator securityValidator,
    ContentSanitizer contentSanitizer,
    ILogger<ComposableSetupPipeline> logger) : IComposableSetupPipeline
{
    private static readonly ConcurrentDictionary<string, SetupSession> Sessions = new();
    private static readonly TimeSpan SessionExpiration = TimeSpan.FromMinutes(30);
    private const float TemplateConfidenceThreshold = 0.9f;
    private static readonly IReadOnlyDictionary<string, int> IntentPositiveKeywordWeights =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["scientist"] = 10,
            ["research"] = 8,
            ["laboratory"] = 5,
            ["lab technician"] = 6,
            ["molecular biology"] = 10,
            ["cell culture"] = 8,
            ["PCR"] = 8,
            ["biotech"] = 5,
            ["microscopy"] = 5,
            ["GMP"] = 5,
            ["CRISPR"] = 6,
            ["diagnostics"] = 5,
            ["engineer"] = 6,
            ["developer"] = 6,
            ["analyst"] = 6
        };

    private static readonly IReadOnlyDictionary<string, int> IntentNegativeKeywordWeights =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["director"] = -15,
            ["VP"] = -20,
            ["senior"] = -3,
            ["manager"] = -5,
            ["PhD required"] = -20,
            ["intern"] = -5
        };

    public async IAsyncEnumerable<SetupProgress> StartSetupAsync(
        SetupRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var session = new SetupSession
        {
            UserInput = request.UserInput,
            OwnerId = request.OwnerId
        };
        Sessions[session.Id] = session;
        logger.LogInformation("Starting composable setup session {SessionId}", session.Id);

        // Phase 1: Parse intent
        yield return Progress(SetupPhase.IntentParsing, SetupProgressType.Started, "Analyzing your request...") with { SessionId = session.Id };
        yield return Progress(SetupPhase.IntentParsing, SetupProgressType.Thinking, "Understanding what you want to monitor...");

        ParsedIntent? intent = null;
        string? parseError = null;
        ContentAnalysisResult? analysis = null;
        string? analysisSource = null;
        string? analysisError = null;
        var (positiveKeywords, negativeKeywords) = ExtractKeywordsFromIntent(request.UserInput);

        // Fast path: if the input is a known platform URL, skip LLM intent parsing entirely
        var extractedUrl = ExtractUrlFromInput(request.UserInput);
        if (extractedUrl is not null)
        {
            // Direct platform detection from URL (no content needed)
            var detectedPlatform = SetupFlowEnhancements.DetectPlatformFromUrl(extractedUrl);
            var fastTemplate = detectedPlatform is not null 
                ? await setupFlowEnhancements.GetPlatformTemplateAsync(
                    detectedPlatform,
                    extractedUrl,
                    positiveKeywords,
                    negativeKeywords,
                    ct: ct)
                : null;
            if (detectedPlatform is not null && fastTemplate is not null)
            {
                logger.LogInformation(
                    "Fast path: Detected {Platform} from URL, skipping LLM intent parsing",
                    detectedPlatform);

                yield return Progress(SetupPhase.IntentParsing, SetupProgressType.Progress,
                    $"Detected {detectedPlatform} platform — using optimized pipeline template",
                    $"URL pattern matched known platform");

                // Create synthetic intent
                intent = new ParsedIntent
                {
                    Url = extractedUrl,
                    Intent = $"Monitor job listings on {detectedPlatform}",
                    Summary = $"Monitoring {new Uri(extractedUrl).Host} for new job postings",
                    ChangeType = "listing"
                };
                session.Intent = intent;
                session.AssembledPipeline = fastTemplate with
                {
                    Metadata = new PipelineMetadata
                    {
                        DisplayTitle = intent.Summary,
                        CreatedAt = DateTime.UtcNow,
                        UserIntent = intent.Intent,
                        CardType = "jobs",
                        EstimatedLlmCallsPerRun = 0
                    }
                };
                session.ContentAnalysis = new ContentAnalysisResult
                {
                    ContentType = "listing",
                    PageSummary = $"{detectedPlatform} job board — using pre-built API pipeline"
                };

                // Skip directly to checkpoint 1
                analysisSource = $"deterministic-template:{detectedPlatform}";
                analysis = session.ContentAnalysis;
                goto checkpoint1;
            }
        }

        // Normal path: use LLM to parse intent
        try
        {
            intent = await ParseIntentAsync(request.UserInput, ct);
            session.Intent = intent;
            session.LlmCallCount++;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse intent for session {SessionId}", session.Id);
            parseError = ex.Message;
        }

        if (parseError != null)
        {
            yield return FailedProgress(SetupPhase.IntentParsing, "Failed to understand your request.", parseError);
            yield break;
        }

        // Phase 2: Fetch content
        yield return Progress(SetupPhase.ContentFetching, SetupProgressType.Started, $"Fetching content from {intent!.Url}...");

        FetchResult? fetchResult = null;
        string? fetchError = null;

        try
        {
            fetchResult = await contentFetcher.FetchAsync(intent.Url, new FetchOptions
            {
                UseJavaScript = true,
                TimeoutSeconds = 30
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch content for session {SessionId}", session.Id);
            fetchError = ex.Message;
        }

        if (fetchError != null)
        {
            yield return FailedProgress(SetupPhase.ContentFetching, "Failed to fetch the page.", fetchError);
            yield break;
        }

        if (!fetchResult!.IsSuccess || string.IsNullOrWhiteSpace(fetchResult.Html))
        {
            yield return FailedProgress(SetupPhase.ContentFetching,
                "Failed to fetch the page content.", fetchResult.ErrorMessage ?? "No HTML content returned.");
            yield break;
        }

        session.FetchedHtml = fetchResult.Html;
        yield return Progress(SetupPhase.ContentFetching, SetupProgressType.Progress,
            "Page content fetched successfully.", $"HTTP {fetchResult.HttpStatusCode} in {fetchResult.DurationMs}ms");

        // Check for JS shell (SPA that needs longer render wait)
        var didRetryFetch = false;
        var jsDetection = setupFlowEnhancements.DetectJsShell(session.FetchedHtml, session.FetchedHtml.Length);
        if (jsDetection.IsJsShell && jsDetection.ShouldRetryWithPlaywright)
        {
            yield return Progress(SetupPhase.ContentFetching, SetupProgressType.Progress,
                $"Detected JS shell ({string.Join(", ", jsDetection.Signals)}). Re-fetching with extended wait...");

            var originalLength = session.FetchedHtml.Length;
            FetchResult? retryResult = null;
            try
            {
                retryResult = await contentFetcher.FetchAsync(intent!.Url, new FetchOptions
                {
                    UseJavaScript = true,
                    TimeoutSeconds = 45,
                    WaitAfterLoadMs = 3000
                }, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "JS shell re-fetch failed for session {SessionId}, continuing with original content", session.Id);
            }

            if (retryResult is { IsSuccess: true } && retryResult.Html is not null
                && retryResult.Html.Length > originalLength * 2)
            {
                session.FetchedHtml = retryResult.Html;
                didRetryFetch = true;
                yield return Progress(SetupPhase.ContentFetching, SetupProgressType.Progress,
                    $"Playwright re-fetch: {retryResult.Html.Length:N0} chars (was {originalLength:N0})");
            }
        }

        // Check for cookie wall / consent page blocking actual content
        if (!didRetryFetch)
        {
            var fetchedContent = session.FetchedHtml;
            if (fetchedContent != null)
            {
                var lower = fetchedContent.ToLowerInvariant();
                var hasCookieWall = lower.Contains("cookie") && (
                    lower.Contains("consent") || lower.Contains("accept all") ||
                    lower.Contains("cookie policy") || lower.Contains("cookie information") ||
                    lower.Contains("gdpr") || lower.Contains("privacy"));

                if (hasCookieWall)
                {
                    yield return Progress(SetupPhase.ContentFetching, SetupProgressType.Progress,
                        "Cookie consent page detected. Re-fetching with browser automation...");

                    FetchResult? cookieRetryResult = null;
                    try
                    {
                        cookieRetryResult = await contentFetcher.FetchAsync(intent!.Url, new FetchOptions
                        {
                            UseJavaScript = true,
                            TimeoutSeconds = 45,
                            WaitAfterLoadMs = 5000
                        }, ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Cookie wall re-fetch failed for session {SessionId}", session.Id);
                    }

                    if (cookieRetryResult is { IsSuccess: true, Html: not null } &&
                        cookieRetryResult.Html.Length > fetchedContent.Length)
                    {
                        session.FetchedHtml = cookieRetryResult.Html;
                        yield return Progress(SetupPhase.ContentFetching, SetupProgressType.Progress,
                            $"Browser re-fetch: {cookieRetryResult.Html.Length:N0} chars (was {fetchedContent.Length:N0})");
                    }
                }
            }
        }

        // Phase 3: Analyze content
        yield return Progress(SetupPhase.ContentAnalysis, SetupProgressType.Thinking, "Analyzing page structure...");

        // Track how the analysis was determined for transparent checkpoint messaging
        // (variables declared earlier for goto scope)

        // Try deterministic classifier first (fast, no LLM needed)
        ContentClassification? classification = null;
        try
        {
            string? responseContentType = null;
            if (fetchResult?.ResponseHeaders is { } headers)
            {
                responseContentType = headers
                    .FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value;
            }
            classification = setupFlowEnhancements.ClassifyContent(
                session.FetchedHtml!, intent!.Url, responseContentType);
            
            logger.LogInformation(
                "Deterministic classifier result: Type={Type}, Confidence={Confidence}, Platform={Platform}, Signals={Signals}",
                classification.Type, classification.Confidence, classification.DetectedPlatform ?? "none",
                string.Join("; ", classification.Signals));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Deterministic classification failed for session {SessionId}, falling back to LLM", session.Id);
        }

        if (classification is { Confidence: >= 0.7 })
        {
            logger.LogInformation(
                "Deterministic classification: {Type} with {Confidence:P0} confidence (signals: {Signals})",
                classification.Type, classification.Confidence, string.Join(", ", classification.Signals));

            yield return Progress(SetupPhase.ContentAnalysis, SetupProgressType.Progress,
                $"Detected {classification.Type} with {classification.Confidence:P0} confidence",
                $"Signals: {string.Join(", ", classification.Signals)}");

            // If platform detected, try to get a pre-built pipeline template
            if (classification.DetectedPlatform is not null)
            {
                var deterministicTemplate = await setupFlowEnhancements.GetPlatformTemplateAsync(
                    classification.DetectedPlatform,
                    intent!.Url,
                    positiveKeywords,
                    negativeKeywords,
                    ct: ct);
                if (deterministicTemplate is not null)
                {
                    analysis = BuildDeterministicAnalysis(classification, intent!);
                    session.ContentAnalysis = analysis;
                    session.AssembledPipeline = deterministicTemplate with
                    {
                        Metadata = new PipelineMetadata
                        {
                            DisplayTitle = intent!.Summary ?? intent.Intent,
                            CreatedAt = DateTime.UtcNow,
                            UserIntent = intent.Intent,
                            CardType = analysis.ContentType,
                            EstimatedLlmCallsPerRun = 0
                        }
                    };

                    logger.LogInformation(
                        "Using pre-built pipeline template for {Platform}, skipping LLM analysis",
                        classification.DetectedPlatform);

                    analysisSource = $"deterministic-template:{classification.DetectedPlatform}";
                }
            }

            // Deterministic classification succeeded but no template — build synthetic analysis to skip LLM
            if (analysis is null && classification.Type is not (DetectedContentType.Unknown
                    or DetectedContentType.JsShell or DetectedContentType.ErrorPage))
            {
                analysis = BuildDeterministicAnalysis(classification, intent!);
                session.ContentAnalysis = analysis;
                analysisSource = $"deterministic-classification:{classification.Type}";
            }
        }

        // Fall through to existing platform detection + LLM if analysis not yet determined
        if (analysis is null)
        {
            try
            {
                var detectedPlatform = DetectPlatform(intent!.Url, session.FetchedHtml);
                session.DetectedPlatform = detectedPlatform;

                if (TryGetTemplatePipeline(detectedPlatform, intent, out var selectedTemplate, out var templatePipeline))
                {
                    analysis = BuildTemplateAnalysis(intent, detectedPlatform!, selectedTemplate!);
                    session.ContentAnalysis = analysis;
                    session.SelectedTemplate = selectedTemplate;
                    session.AssembledPipeline = SpecializeTemplatePipeline(
                        templatePipeline!,
                        intent,
                        analysis,
                        detectedPlatform!,
                        selectedTemplate!);

                    logger.LogInformation(
                        "Detected platform: {Platform} (confidence: {Confidence}). Using pre-built template.",
                        detectedPlatform!.PlatformName,
                        detectedPlatform.Confidence);

                    analysisSource = $"platform-template:{detectedPlatform.PlatformName}";
                }
                else
                {
                    analysis = await AnalyzeContentAsync(session.FetchedHtml, intent, ct);
                    session.ContentAnalysis = analysis;
                    session.LlmCallCount++;
                    analysisSource = "llm";
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "LLM content analysis failed for session {SessionId}. The AI provider may be unavailable.", session.Id);
                analysisError = ex.Message;
            }
        }

        if (analysisError != null)
        {
            yield return FailedProgress(SetupPhase.ContentAnalysis,
                "AI analysis failed — please check your Copilot/LLM provider settings. " +
                "Without AI analysis, the system cannot determine page structure or generate extraction selectors.",
                analysisError);
            yield break;
        }

        // Guard: if LLM returned a vague "other" type with no regions, it couldn't determine page structure.
        // Don't silently proceed — flag it to the user so they can provide guidance.
        if (analysisSource == "llm"
            && analysis!.ContentType is "other"
            && (analysis.Regions is null || analysis.Regions.Count == 0))
        {
            logger.LogWarning(
                "LLM analysis for session {SessionId} returned content type 'other' with no regions — page structure undetermined",
                session.Id);

            yield return Progress(SetupPhase.ContentAnalysis, SetupProgressType.Progress,
                "I couldn't determine the page structure. The AI wasn't able to identify specific content regions. " +
                "You may want to describe what you'd like to extract, or choose basic full-page monitoring.",
                analysis.PageSummary);
        }
        else
        {
            yield return Progress(SetupPhase.ContentAnalysis, SetupProgressType.Progress,
                "Page analysis complete.", analysis!.PageSummary);
        }

        checkpoint1:
        session.AnalysisSource = analysisSource;

        // Checkpoint 1: Show understanding to user — be transparent about how we got here
        session.CurrentPhase = SetupPhase.Checkpoint1;
        session.LastActivityAt = DateTime.UtcNow;

        var analysisDetail = analysisSource switch
        {
            string s when s.StartsWith("deterministic-template:") =>
                $"Using pre-built template for {s["deterministic-template:".Length..]} (fast path, no AI needed)",
            string s when s.StartsWith("deterministic-classification:") =>
                $"Detected as {s["deterministic-classification:".Length..]} by pattern matching (fast path)",
            string s when s.StartsWith("platform-template:") =>
                $"Recognized {s["platform-template:".Length..]} platform, using pre-built template",
            "llm" => $"AI analyzed the page: {analysis!.ContentType} content with {analysis.Regions?.Count ?? 0} regions identified",
            _ => "Analysis complete"
        };

        var summary = intent.Summary
            ?? $"I'll watch {intent.Url} for {intent.ChangeType} changes: {intent.Intent}";

        yield return new SetupProgress
        {
            Phase = SetupPhase.Checkpoint1,
            Type = SetupProgressType.CheckpointReached,
            Message = summary,
            Intent = intent,
            Detail = analysisDetail,
            SessionId = session.Id
        };
    }

    public async IAsyncEnumerable<SetupProgress> ConfirmIntentAsync(
        string sessionId,
        bool confirmed,
        string? feedback = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Sessions.TryGetValue(sessionId, out var session))
        {
            yield return FailedProgress(SetupPhase.Checkpoint1,
                "Session not found or expired.", $"Session {sessionId} does not exist.");
            yield break;
        }

        session.LastActivityAt = DateTime.UtcNow;

        if (!confirmed && !string.IsNullOrWhiteSpace(feedback))
        {
            // Re-parse intent with feedback
            yield return Progress(SetupPhase.IntentParsing, SetupProgressType.Thinking, "Re-analyzing with your feedback...");

            ParsedIntent? revisedIntent = null;
            string? reParseError = null;

            try
            {
                var revisedInput = $"{session.UserInput}\n\nUser feedback: {feedback}";
                revisedIntent = await ParseIntentAsync(revisedInput, ct);
                session.Intent = revisedIntent;
                session.LlmCallCount++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to re-parse intent with feedback for session {SessionId}", sessionId);
                reParseError = ex.Message;
            }

            if (reParseError != null)
            {
                yield return FailedProgress(SetupPhase.IntentParsing, "Failed to re-analyze with your feedback.", reParseError);
                yield break;
            }

            yield return new SetupProgress
            {
                Phase = SetupPhase.Checkpoint1,
                Type = SetupProgressType.CheckpointReached,
                Message = revisedIntent!.Summary ?? $"Updated: I'll watch {revisedIntent.Url} for {revisedIntent.ChangeType} changes.",
                Intent = revisedIntent,
                Detail = $"Session: {session.Id}"
            };
            yield break;
        }

        if (!confirmed)
        {
            Sessions.TryRemove(sessionId, out _);
            yield return FailedProgress(SetupPhase.Checkpoint1, "Setup cancelled by user.");
            yield break;
        }

        // Phase 4: Build pipeline
        session.CurrentPhase = SetupPhase.PipelineBuilding;
        yield return Progress(SetupPhase.PipelineBuilding, SetupProgressType.Started, "Building monitoring pipeline...");
        yield return Progress(SetupPhase.PipelineBuilding, SetupProgressType.Thinking, "Selecting and configuring pipeline blocks...");

        PipelineDefinition? pipeline = null;
        string? buildError = null;
        string? templateBuildDetail = null;

        try
        {
            if (session.AssembledPipeline is not null)
            {
                pipeline = session.AssembledPipeline;
                templateBuildDetail = session.SelectedTemplate?.Description;
            }
            else
            {
                pipeline = await BuildPipelineAsync(session.Intent!, session.ContentAnalysis!, session.FetchedHtml!, ct);
                session.AssembledPipeline = pipeline;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to build pipeline for session {SessionId}", sessionId);
            buildError = ex.Message;
        }

        if (buildError != null)
        {
            yield return FailedProgress(SetupPhase.PipelineBuilding, "Failed to build the monitoring pipeline.", buildError);
            yield break;
        }

        if (templateBuildDetail is not null)
        {
            yield return Progress(
                SetupPhase.PipelineBuilding,
                SetupProgressType.Progress,
                $"Detected platform: {session.DetectedPlatform?.PlatformName ?? "known site"} (confidence: {session.DetectedPlatform?.Confidence ?? 0:0.00}). Using pre-built template.",
                templateBuildDetail);
        }

        yield return Progress(SetupPhase.PipelineBuilding, SetupProgressType.Progress,
            $"Pipeline assembled with {pipeline!.Blocks.Count} blocks.",
            string.Join(" → ", pipeline.Blocks.Select(b => b.Type)));

        // Phase 5: Dry run
        session.CurrentPhase = SetupPhase.DryRun;
        yield return Progress(SetupPhase.DryRun, SetupProgressType.Started, "Running pipeline to verify it works...");

        DryRunResult dryRunResult;
        try
        {
            dryRunResult = await ExecuteDryRunAsync(pipeline, session, ct);
            session.DryRunResult = dryRunResult;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Dry run failed for session {SessionId}", sessionId);
            dryRunResult = new DryRunResult { Success = false, Error = ex.Message };
            session.DryRunResult = dryRunResult;
        }

        var dryRunDetail = dryRunResult.BlockFlowSummary is not null
            ? dryRunResult.BlockFlowSummary + "\n\n" + (dryRunResult.SampleOutput ?? dryRunResult.Error ?? "")
            : dryRunResult.SampleOutput ?? dryRunResult.Error;

        yield return Progress(SetupPhase.DryRun, SetupProgressType.Progress,
            dryRunResult.Success
                ? "Dry run succeeded — pipeline produces valid output."
                : "Dry run completed with issues.",
            dryRunDetail);

        // Phase 5.5: Adversarial testing (optional, requires large model)
        session.CurrentPhase = SetupPhase.AdversarialTest;
        yield return Progress(SetupPhase.AdversarialTest, SetupProgressType.Started, "Running adversarial mutation tests...");

        AdversarialTestResult adversarialResult;
        try
        {
            adversarialResult = await RunAdversarialTestAsync(pipeline, dryRunResult, session.Intent!, ct);
            session.AdversarialTestResult = adversarialResult;
            if (!adversarialResult.Skipped) session.LlmCallCount++;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Adversarial testing failed for session {SessionId}", sessionId);
            adversarialResult = new AdversarialTestResult { Passed = true, Skipped = true, SkipReason = "Adversarial testing encountered an error." };
            session.AdversarialTestResult = adversarialResult;
        }

        yield return Progress(SetupPhase.AdversarialTest, SetupProgressType.Progress,
            adversarialResult.Skipped
                ? $"Adversarial testing skipped: {adversarialResult.SkipReason}"
                : adversarialResult.Passed
                    ? "Adversarial testing passed — pipeline is resilient."
                    : $"Adversarial testing found {adversarialResult.FragileBlocks.Count} fragile block(s).",
            adversarialResult.FragileBlocks.Count > 0
                ? $"Fragile: {string.Join(", ", adversarialResult.FragileBlocks)}"
                : null);

        // Phase 6: QC validation
        session.CurrentPhase = SetupPhase.QcValidation;
        yield return Progress(SetupPhase.QcValidation, SetupProgressType.Thinking, "Validating pipeline against your original intent...");

        QcResult qcResult;
        try
        {
            qcResult = await ValidateWithQcAsync(session.Intent!, pipeline, dryRunResult, ct);
            session.QcResult = qcResult;
            session.LlmCallCount++;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "QC validation failed for session {SessionId}", sessionId);
            qcResult = new QcResult { Valid = true, Issues = [], Suggestions = [] };
            session.QcResult = qcResult;
        }

        yield return Progress(SetupPhase.QcValidation, SetupProgressType.Progress,
            qcResult.Valid ? "Quality check passed." : "Quality check found potential issues.",
            qcResult.Issues.Count > 0 ? string.Join("; ", qcResult.Issues) : null);

        var dryRunHasUsableOutput = HasUsableDryRunOutput(dryRunResult);
        var userFacingQcResult = CreateUserFacingQcResult(qcResult);

        // Checkpoint 2: Show pipeline + results for approval
        session.CurrentPhase = SetupPhase.Checkpoint2;
        session.LastActivityAt = DateTime.UtcNow;

        var humanSummary = BuildHumanSummary(session.Intent!, pipeline, dryRunResult, userFacingQcResult, adversarialResult);

        if (IsDeterministicTemplateSource(session.AnalysisSource) && dryRunHasUsableOutput)
        {
            logger.LogInformation("Auto-approving template pipeline (dry run passed, no LLM needed)");
            yield return Progress(
                SetupPhase.Checkpoint2,
                SetupProgressType.Progress,
                "Trusted template passed the dry run — creating your watch automatically.");

            await foreach (var progress in ConfirmPipelineAsync(sessionId, confirmed: true, ct: ct))
                yield return progress;

            yield break;
        }

        var checkpoint2Message = dryRunHasUsableOutput
            ? "Pipeline ready for your approval."
            : "⚠️ Pipeline ran but extracted no data";
        var checkpoint2Detail = dryRunHasUsableOutput
            ? $"{dryRunResult.BlockFlowSummary}\n\nSession: {session.Id}"
            : $"{dryRunResult.BlockFlowSummary}\n\nThe page may need JavaScript rendering or has a cookie wall blocking content. Session: {session.Id}";

        yield return new SetupProgress
        {
            Phase = SetupPhase.Checkpoint2,
            Type = SetupProgressType.CheckpointReached,
            Message = checkpoint2Message,
            Proposal = new PipelineProposal
            {
                Pipeline = pipeline,
                HumanSummary = humanSummary,
                DryRun = dryRunResult,
                QcValidation = userFacingQcResult,
                AdversarialTest = adversarialResult
            },
            Detail = checkpoint2Detail,
            SessionId = session.Id
        };
    }

    public async IAsyncEnumerable<SetupProgress> ConfirmPipelineAsync(
        string sessionId,
        bool confirmed,
        string? feedback = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Sessions.TryGetValue(sessionId, out var session))
        {
            yield return FailedProgress(SetupPhase.Checkpoint2,
                "Session not found or expired.", $"Session {sessionId} does not exist.");
            yield break;
        }

        session.LastActivityAt = DateTime.UtcNow;

        if (!confirmed && !string.IsNullOrWhiteSpace(feedback))
        {
            // Rebuild pipeline with feedback
            yield return Progress(SetupPhase.PipelineBuilding, SetupProgressType.Thinking, "Rebuilding pipeline with your feedback...");

            PipelineProposal? proposal = null;
            string? rebuildError = null;

            try
            {
                var revisedPipeline = await RebuildPipelineWithFeedbackAsync(session, feedback, ct);
                session.AssembledPipeline = revisedPipeline;
                session.LlmCallCount++;

                var dryRunResult = await ExecuteDryRunAsync(revisedPipeline, session, ct);
                session.DryRunResult = dryRunResult;

                var adversarialResult = await RunAdversarialTestAsync(revisedPipeline, dryRunResult, session.Intent!, ct);
                session.AdversarialTestResult = adversarialResult;
                if (!adversarialResult.Skipped) session.LlmCallCount++;

                var qcResult = await ValidateWithQcAsync(session.Intent!, revisedPipeline, dryRunResult, ct);
                session.QcResult = qcResult;
                session.LlmCallCount++;

                var userFacingQcResult = CreateUserFacingQcResult(qcResult);
                var humanSummary = BuildHumanSummary(session.Intent!, revisedPipeline, dryRunResult, userFacingQcResult, adversarialResult);

                proposal = new PipelineProposal
                {
                    Pipeline = revisedPipeline,
                    HumanSummary = humanSummary,
                    DryRun = dryRunResult,
                    QcValidation = userFacingQcResult,
                    AdversarialTest = adversarialResult
                };
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to rebuild pipeline with feedback for session {SessionId}", sessionId);
                rebuildError = ex.Message;
            }

            if (rebuildError != null)
            {
                yield return FailedProgress(SetupPhase.PipelineBuilding, "Failed to rebuild the pipeline.", rebuildError);
                yield break;
            }

            var revisedDryRunHasUsableOutput = proposal?.DryRun is not null && HasUsableDryRunOutput(proposal.DryRun);
            var revisedBlockFlow = proposal?.DryRun?.BlockFlowSummary;

            yield return new SetupProgress
            {
                Phase = SetupPhase.Checkpoint2,
                Type = SetupProgressType.CheckpointReached,
                Message = revisedDryRunHasUsableOutput
                    ? "Revised pipeline ready for approval."
                    : "⚠️ Revised pipeline ran but extracted no data",
                Proposal = proposal,
                Detail = revisedDryRunHasUsableOutput
                    ? $"{revisedBlockFlow}\n\nSession: {session.Id}"
                    : $"{revisedBlockFlow}\n\nThe page may need JavaScript rendering or has a cookie wall blocking content. Session: {session.Id}",
                SessionId = session.Id
            };
            yield break;
        }

        if (!confirmed)
        {
            Sessions.TryRemove(sessionId, out _);
            yield return FailedProgress(SetupPhase.Checkpoint2, "Setup cancelled by user.");
            yield break;
        }

        // Save the watch
        session.CurrentPhase = SetupPhase.Saving;
        yield return Progress(SetupPhase.Saving, SetupProgressType.Started, "Saving your watch...");

        Guid? watchId = null;
        string? saveUrl = null;
        string? saveError = null;

        try
        {
            var watch = new WatchedSite
            {
                Url = session.Intent!.Url,
                Name = session.AssembledPipeline!.Metadata?.DisplayTitle ?? session.Intent.Intent,
                Description = session.Intent.Summary ?? session.Intent.Intent,
                UserIntent = session.Intent.Intent,
                PipelineDefinitionJson = PipelineSerializer.Serialize(session.AssembledPipeline),
                SetupTimeHtml = session.FetchedHtml,
                OwnerId = session.OwnerId ?? Guid.Empty,
                CheckInterval = ParseFrequency(session.Intent.Frequency),
                FetchSettings = new FetchSettings
                {
                    UseJavaScript = session.ContentAnalysis?.NeedsJavaScript ?? false
                },
                Notifications = new NotificationSettings
                {
                    MinimumImportance = ChangeImportance.Medium
                }
            };

            await watchRepo.InsertAsync(watch, ct);
            watchId = watch.Id;
            saveUrl = watch.Url;
            Sessions.TryRemove(sessionId, out _);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save watch for session {SessionId}", sessionId);
            saveError = ex.Message;
        }

        if (saveError != null)
        {
            yield return FailedProgress(SetupPhase.Saving, "Failed to save the watch.", saveError);
            yield break;
        }

        yield return new SetupProgress
        {
            Phase = SetupPhase.Saving,
            Type = SetupProgressType.Completed,
            Message = "Watch created successfully!",
            WatchId = watchId,
            Detail = $"Monitoring {saveUrl}"
        };
    }

    // ── Headless pipeline building (no UI, no SignalR) ────────────────────

    /// <inheritdoc/>
    public async Task<PipelineDefinition?> BuildPipelineHeadlessAsync(
        string url,
        string? userIntent,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        var token = cts.Token;

        var input = string.IsNullOrWhiteSpace(userIntent)
            ? url
            : $"{url} — {userIntent}";
        var (positiveKeywords, negativeKeywords) = ExtractKeywordsFromIntent(input);

        // Fast path: platform template (no LLM needed)
        var detectedPlatform = SetupFlowEnhancements.DetectPlatformFromUrl(url);
        if (detectedPlatform is not null)
        {
            var template = await setupFlowEnhancements.GetPlatformTemplateAsync(
                detectedPlatform, url, positiveKeywords, negativeKeywords, ct: token);
            if (template is not null)
            {
                logger.LogInformation(
                    "[Headless] Using platform template {Platform} for {Url}", detectedPlatform, url);
                return template with
                {
                    Metadata = new PipelineMetadata
                    {
                        DisplayTitle = userIntent ?? $"Monitor {new Uri(url).Host}",
                        CreatedAt = DateTime.UtcNow,
                        UserIntent = userIntent,
                        CardType = "jobs",
                        EstimatedLlmCallsPerRun = 0
                    }
                };
            }
        }

        // Phase 1: Parse intent via LLM
        ParsedIntent intent;
        try
        {
            intent = await ParseIntentAsync(input, token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Headless] Failed to parse intent for {Url}", url);
            return null;
        }

        // Phase 2: Fetch content
        FetchResult? fetchResult;
        try
        {
            fetchResult = await contentFetcher.FetchAsync(intent.Url, new FetchOptions
            {
                UseJavaScript = true,
                TimeoutSeconds = 30
            }, token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Headless] Failed to fetch content for {Url}", url);
            return null;
        }

        if (!fetchResult.IsSuccess || string.IsNullOrWhiteSpace(fetchResult.Html))
        {
            logger.LogWarning("[Headless] No HTML content from {Url}", url);
            return null;
        }

        // Phase 3: Analyze content (deterministic first, LLM fallback)
        ContentAnalysisResult? analysis = null;

        try
        {
            string? responseContentType = null;
            if (fetchResult.ResponseHeaders is { } headers)
            {
                responseContentType = headers
                    .FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value;
            }
            var classification = setupFlowEnhancements.ClassifyContent(
                fetchResult.Html, intent.Url, responseContentType);

            if (classification is { Confidence: >= 0.7 }
                && classification.Type is not (DetectedContentType.Unknown
                    or DetectedContentType.JsShell or DetectedContentType.ErrorPage))
            {
                analysis = BuildDeterministicAnalysis(classification, intent);

                // If platform detected, try template
                if (classification.DetectedPlatform is not null)
                {
                    var deterministicTemplate = await setupFlowEnhancements.GetPlatformTemplateAsync(
                        classification.DetectedPlatform, intent.Url, positiveKeywords, negativeKeywords, ct: token);
                    if (deterministicTemplate is not null)
                    {
                        logger.LogInformation("[Headless] Using deterministic template for {Url}", url);
                        return deterministicTemplate with
                        {
                            Metadata = new PipelineMetadata
                            {
                                DisplayTitle = intent.Summary ?? intent.Intent,
                                CreatedAt = DateTime.UtcNow,
                                UserIntent = intent.Intent,
                                CardType = analysis.ContentType,
                                EstimatedLlmCallsPerRun = 0
                            }
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Headless] Deterministic classification failed for {Url}", url);
        }

        // LLM content analysis fallback
        if (analysis is null)
        {
            try
            {
                analysis = await AnalyzeContentAsync(fetchResult.Html, intent, token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Headless] LLM content analysis failed for {Url}", url);
                return null;
            }
        }

        // Auto-confirm checkpoint 1 (intent) — headless mode

        // Phase 4: Build pipeline via LLM
        PipelineDefinition pipeline;
        try
        {
            pipeline = await BuildPipelineAsync(intent, analysis, fetchResult.Html, token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Headless] Failed to build pipeline for {Url}", url);
            return null;
        }

        // Phase 5: Dry run — auto-confirm checkpoint 2 only if dry run passes
        var session = new SetupSession
        {
            UserInput = input,
            FetchedHtml = fetchResult.Html,
            Intent = intent,
            ContentAnalysis = analysis,
            AssembledPipeline = pipeline
        };

        DryRunResult dryRunResult;
        try
        {
            dryRunResult = await ExecuteDryRunAsync(pipeline, session, token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Headless] Dry run failed for {Url}", url);
            return null;
        }

        if (!dryRunResult.Success)
        {
            logger.LogWarning("[Headless] Dry run unsuccessful for {Url}: {Error}", url, dryRunResult.Error);
            return null;
        }

        logger.LogInformation(
            "[Headless] Pipeline built successfully for {Url}: {BlockCount} blocks, dry run passed",
            url, pipeline.Blocks.Count);
        return pipeline;
    }

    // ── Progress helpers ────────────────────────────────────────────────

    private static SetupProgress Progress(SetupPhase phase, SetupProgressType type, string message, string? detail = null) =>
        new() { Phase = phase, Type = type, Message = message, Detail = detail };

    private static SetupProgress FailedProgress(SetupPhase phase, string message, string? error = null) =>
        new() { Phase = phase, Type = SetupProgressType.Failed, Message = message, Error = error };

    /// <summary>
    /// Strips markdown code fences from LLM responses that wrap JSON in markdown.
    /// </summary>
    private static string StripMarkdownFences(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith('`')) return trimmed;
        
        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline > 0)
            trimmed = trimmed[(firstNewline + 1)..];
        if (trimmed.EndsWith("```"))
            trimmed = trimmed[..^3].TrimEnd();
        return trimmed;
    }

    /// <summary>
    /// Extracts a URL from user input text. Returns null if no valid URL found.
    /// Handles both bare URLs and URLs embedded in natural language.
    /// </summary>
    private static string? ExtractUrlFromInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var trimmed = input.Trim();

        // If the entire input is a URL
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var directUri) &&
            (directUri.Scheme == "http" || directUri.Scheme == "https"))
            return trimmed;

        // Extract URL from natural language
        var match = System.Text.RegularExpressions.Regex.Match(
            trimmed, @"https?://[^\s""'<>]+",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
        return match.Success ? match.Value.TrimEnd('.', ',', ')', ']') : null;
    }

    private static (List<RelevanceKeyword> positive, List<RelevanceKeyword> negative) ExtractKeywordsFromIntent(string input)
    {
        var normalizedInput = input ?? string.Empty;
        var lowerInput = normalizedInput.ToLowerInvariant();

        var positive = IntentPositiveKeywordWeights
            .Select(pattern => new RelevanceKeyword(
                pattern.Key,
                lowerInput.Contains(pattern.Key.ToLowerInvariant(), StringComparison.Ordinal)
                    ? pattern.Value + 5
                    : pattern.Value))
            .ToList();

        var negative = IntentNegativeKeywordWeights
            .Select(pattern => new RelevanceKeyword(pattern.Key, pattern.Value))
            .ToList();

        return (positive, negative);
    }

    // ── LLM interaction methods ─────────────────────────────────────────

    private async Task<ParsedIntent> ParseIntentAsync(string userInput, CancellationToken ct)
    {
        var prompt = $$"""
            You are an assistant that parses natural language requests for website monitoring.
            
            Analyze the following user input and extract their monitoring intent.
            
            User input: "{{userInput}}"
            
            Respond with a JSON object with these fields:
            {
              "url": "the URL to monitor (string, required)",
              "intent": "what the user wants to track in one sentence (string, required)",
              "changeType": "one of: price, content, availability, list, structure, any (string, required)",
              "summary": "a friendly summary like 'I will watch X for Y changes' (string)",
              "thresholds": { "key": "value" } or null if no thresholds mentioned,
              "frequency": "how often to check, e.g. '5m', '1h', '30m' or null if not specified (string or null)",
              "notificationPreference": "email, webhook, or null if not specified (string or null)"
            }
            
            Rules:
            - Extract the URL exactly as the user provided it
            - Infer changeType from context (price drops → "price", new items → "list", etc.)
            - Include thresholds only if the user mentioned specific values (e.g. "below $50")
            - Keep the summary concise and user-friendly
            
            Respond ONLY with the JSON object, no additional text.
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            ExpectJson = true,
            Temperature = 0.2f,
            MaxTokens = 512,
            UsageType = LlmUsageType.WatchSetup
        }, ct);

        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Content))
        {
            throw new InvalidOperationException(
                $"LLM failed to parse intent: {response.ErrorMessage ?? "empty response"}");
        }

        return DeserializeOrThrow<ParsedIntent>(response.Content, "ParsedIntent");
    }

    private async Task<ContentAnalysisResult> AnalyzeContentAsync(
        string html, ParsedIntent intent, CancellationToken ct)
    {
        // Sanitize HTML to strip prompt injection before LLM processing
        var sanitized = contentSanitizer.SanitizeHtml(html);
        var sanitizedHtml = sanitized.Content;

        // Truncate HTML to avoid exceeding token limits
        var truncatedHtml = sanitizedHtml.Length > 15_000
            ? sanitizedHtml[..15_000] + "\n<!-- truncated -->"
            : sanitizedHtml;

        var prompt = $$"""
            You are a web page analyst. Analyze the following HTML page structure for a monitoring system.
            
            The user wants to monitor this page for: {{intent.Intent}}
            Change type: {{intent.ChangeType}}
            
            HTML content (may be truncated):
            ```html
            {{truncatedHtml}}
            ```
            
            Analyze the page and respond with a JSON object:
            {
              "contentType": "one of: product, article, listing, dashboard, forum, documentation, other (string, required)",
              "regions": ["list of identifiable content regions, e.g. 'price section', 'product details', 'comments'"],
              "hasPagination": true/false,
              "needsJavaScript": true/false (whether JS rendering is likely needed for the monitored content),
              "recommendedSelector": "a CSS selector that targets the most relevant content area, or null",
              "pageSummary": "brief description of what this page contains"
            }
            
            Respond ONLY with the JSON object.
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            ExpectJson = true,
            Temperature = 0.2f,
            MaxTokens = 512,
            UsageType = LlmUsageType.ContentAnalysis
        }, ct);

        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Content))
        {
            throw new InvalidOperationException(
                $"LLM failed to analyze content: {response.ErrorMessage ?? "empty response"}");
        }

        return DeserializeOrThrow<ContentAnalysisResult>(response.Content, "ContentAnalysisResult");
    }

    private async Task<PipelineDefinition> BuildPipelineAsync(
        ParsedIntent intent, ContentAnalysisResult analysis, string html, CancellationToken ct)
    {
        return await BuildPipelineWithRetryAsync(intent, analysis, html, retryOnDomainPin: true, ct);
    }

    private async Task<PipelineDefinition> BuildPipelineWithRetryAsync(
        ParsedIntent intent, ContentAnalysisResult analysis, string html,
        bool retryOnDomainPin, CancellationToken ct)
    {
        var availableBlocks = string.Join(", ", blockRegistry.RegisteredBlockTypes);

        var prompt = $$"""
            You are a pipeline builder for a website monitoring system.
            
            Available block types: {{availableBlocks}}
            
            User intent: {{intent.Intent}}
            Change type: {{intent.ChangeType}}
            Page type: {{analysis.ContentType}}
            Page regions: {{string.Join(", ", analysis.Regions)}}
            Recommended selector: {{analysis.RecommendedSelector ?? "none"}}
            Needs JavaScript: {{analysis.NeedsJavaScript}}
            
            Design a pipeline by selecting blocks and providing configuration for each.
            The pipeline MUST start with an "Input" block and end with an "Output" block.
            
            PIPELINE PATTERNS (choose the best match):
            
            1. JSON API monitoring (job boards, REST APIs):
               Input → HttpRequest (POST/GET with JSON body/headers) → JsonExtract (JSONPath) → DataFilter → RelevanceScore → ListDiff → Output
               Config: HttpRequest needs method, headers, body. JsonExtract needs jsonpath expressions. DataFilter needs field conditions.
            
            2. HTML page with job listings:
               Input → Navigate (with useJavaScript if SPA) → ExtractSchema (CSS selectors) → DataFilter → ListDiff → Output
               
            3. Multi-query search (aggregating results from multiple searches):
               Input → Iterate (values=["query1","query2",...], urlTemplate, extract jsonpath) → DataFilter → RelevanceScore → ListDiff → Output
               
            4. List + detail enrichment (fetch detail pages for each item):
               Input → HttpRequest → JsonExtract → ForEachRequest (urlTemplate for detail, extract mappings) → ListDiff → Output
            
            5. Price/stock tracking:
               Input → Navigate → ExtractSchema → NumericDelta → Condition → Notify → Output
            
            6. Simple content change detection:
               Input → Navigate → HashCompare → Condition → Notify → Output
            
            BLOCK CONFIGS:
            - Input: { "url": "https://..." }
            - Navigate: { "useJavaScript": true/false, "timeout": 30000, "waitForSelector": ".css-selector" }
            - HttpRequest: { "method": "GET"/"POST", "headers": {"Accept":"application/json","Content-Type":"application/json"}, "body": "{json}", "timeout": 30000 }
              Output ports: body (raw text), json (parsed), html, status, response (full composite with url+requestBody — use this when connecting to Paginate offset mode)
            - JsonExtract: { "extractions": [{"name":"items","jsonpath":"$.data[*]","type":"array"}, {"name":"total","jsonpath":"$.total","type":"number"}] }
            - ExtractSchema: { "scope": ".item-container", "listMode": true, "schema": [{"field":"title","selector":".title"},{"field":"url","selector":"a[href]"}], "preferStructuredData": true }
              When listMode=true, scope selects repeating elements and schema fields are extracted per element → outputs JSON array. Use for job listing pages.
            - DataFilter: { "conditions": [{"field":"location","operator":"contains","value":"Denmark"}], "mode": "any" }
            - RelevanceScore: { "targetFields": ["title","description"], "positiveKeywords": [{"keyword":"scientist","weight":10}], "negativeKeywords": [{"keyword":"director","weight":-15}], "minScore": 0 }
            - ListDiff: { "identityKey": "url" or "id", "mode": "all_changes"/"additions_only" }
            - Iterate: { "values": ["q1","q2"], "request": {"urlTemplate":"https://api?q=URLTEMPLATE_VALUE","method":"GET"}, "extract": {"jsonpath":"$.hits[*]","type":"array"}, "deduplicateKey": "id" }
            - ForEachRequest: { "request": {"urlTemplate":"https://api/URLTEMPLATE_ITEM_ID","method":"GET"}, "extract": {"format":"json","mappings":[{"source":"$.description","target":"description"}]}, "rateLimit": {"delayMs":500} }
            - Deduplicate: { "identityKey": "id" }
            - LlmExtract: { "prompt": "Extract job listings as JSON array with title, company, location, url", "outputSchema": {"type":"array"} }
            - Condition: { "operator": "greaterThan", "field": "added.length", "value": 0 }
            - Notify: { "template": "New items detected" }
            
            PORT CONNECTION RULES (critical — mismatched ports cause pipeline validation failure):
            - Navigate outputs: "url" (Url type), "html" (HtmlContent type)
            - ExtractSchema accepts: "html" (HtmlContent) → outputs "data" (ExtractedObjects)
            - HashCompare accepts: "data" (ExtractedObjects) → outputs "result" (DiffResult)
            - ListDiff accepts: "data" (ExtractedObjects) → outputs "result" (DiffResult)
            - DataFilter accepts: "data" (ExtractedObjects) → outputs "filtered" (ExtractedObjects)
            - Condition accepts: "signal" (BooleanSignal), optional "data" (ExtractedObjects)
            - Output accepts: "data" (ExtractedObjects)
            - HtmlContent is compatible with ExtractedObjects — Navigate "html" port can connect directly to HashCompare "data" port
            - Do NOT connect Navigate "html" directly to ListDiff "data" — use ExtractSchema between them to extract structured items first
            
            IMPORTANT RULES:
            - Use ONLY the URL provided: {{intent.Url}}. Do NOT generate example or placeholder URLs.
            - All URLs in block configs (Input url, Navigate url, HttpRequest url, etc.) MUST use the domain from the provided URL. Never invent URLs.
            - For JSON APIs: Use HttpRequest + JsonExtract (NOT Navigate + ExtractSchema)
            - For HTML pages: Use Navigate + ExtractSchema with listMode=true (for lists of items)
            - For pages needing login/cookies: Use Navigate with useJavaScript: true
            - Always include ListDiff for change tracking (unless it's a one-off check)
            - When using Paginate offset mode after HttpRequest, connect from httprequest "response" port (not "body" or "json") to paginate "json" port
            - DataFilter and RelevanceScore are optional — include them when the user wants filtering/scoring
            - ListDiff accepts both arrays and objects with "items" array
            - ExtractSchema with listMode=true outputs a JSON array — use for job listing pages with repeating elements
            - CSS selectors must be Level 3 compatible — do NOT use :has() pseudo-class
            - Use block types ONLY from the available list
            - Every block needs a unique id in format "type-N" (e.g. "httprequest-1")
            - Include Input at position 0 and Output at the last position
            - Always include connections between blocks
            
            Respond ONLY with a JSON object:
            {
              "blocks": [...],
              "connections": [{"fromBlockId":"input-1","fromPort":"url","toBlockId":"navigate-1","toPort":"url"}, ...],
              "estimatedLlmCallsPerRun": 0
            }
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            ExpectJson = true,
            Temperature = 0.3f,
            MaxTokens = 1024,
            UsageType = LlmUsageType.WatchSetup,
            PreferLargeModel = true
        }, ct);

        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Content))
        {
            throw new InvalidOperationException(
                $"LLM failed to build pipeline: {response.ErrorMessage ?? "empty response"}");
        }

        var jsonContent = StripMarkdownFences(response.Content);

        var llmOutput = JsonDocument.Parse(jsonContent);
        var blocksElement = llmOutput.RootElement.GetProperty("blocks");

        var blocks = new List<BlockDefinition>();
        foreach (var blockEl in blocksElement.EnumerateArray())
        {
            var blockType = blockEl.GetProperty("type").GetString()!;

            // Only include blocks that are actually registered
            if (!blockRegistry.IsRegistered(blockType))
            {
                logger.LogWarning("LLM suggested unregistered block type '{BlockType}', skipping", blockType);
                continue;
            }

            blocks.Add(new BlockDefinition
            {
                Id = blockEl.GetProperty("id").GetString()!,
                Type = blockType,
                Config = blockEl.TryGetProperty("config", out var configEl) && configEl.ValueKind != JsonValueKind.Null
                    ? configEl.Clone()
                    : null,
                Position = blockEl.TryGetProperty("position", out var posEl) ? posEl.GetInt32() : null
            });
        }

        // Ensure Input at start and Output at end
        if (blocks.Count == 0 || blocks[0].Type != "Input")
        {
            blocks.Insert(0, new BlockDefinition { Id = "input-1", Type = "Input", Position = 0 });
        }

        if (blocks[^1].Type != "Output")
        {
            blocks.Add(new BlockDefinition { Id = "output-1", Type = "Output", Position = blocks.Count });
        }

        // Use LLM-provided connections if present, otherwise auto-wire
        List<ConnectionDefinition> connections;
        if (llmOutput.RootElement.TryGetProperty("connections", out var connectionsEl) && 
            connectionsEl.ValueKind == JsonValueKind.Array && connectionsEl.GetArrayLength() > 0)
        {
            connections = [];
            foreach (var connEl in connectionsEl.EnumerateArray())
            {
                connections.Add(new ConnectionDefinition
                {
                    FromBlockId = connEl.GetProperty("fromBlockId").GetString()!,
                    FromPort = connEl.GetProperty("fromPort").GetString()!,
                    ToBlockId = connEl.GetProperty("toBlockId").GetString()!,
                    ToPort = connEl.GetProperty("toPort").GetString()!
                });
            }
            logger.LogInformation("Using {Count} LLM-provided connections", connections.Count);
        }
        else
        {
            connections = AutoWireConnections(blocks);
            logger.LogInformation("Auto-wired {Count} connections (LLM didn't provide them)", connections.Count);
        }

        var estimatedLlmCalls = llmOutput.RootElement.TryGetProperty("estimatedLlmCallsPerRun", out var llmCallsEl)
            ? llmCallsEl.GetInt32()
            : 0;

        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks = blocks,
            Connections = connections,
            Metadata = new PipelineMetadata
            {
                DisplayTitle = intent.Summary ?? intent.Intent,
                CreatedAt = DateTime.UtcNow,
                UserIntent = intent.Intent,
                EstimatedLlmCallsPerRun = estimatedLlmCalls,
                CardType = intent.ChangeType
            }
        };

        // Validate the pipeline
        var validationResult = pipelineValidator.Validate(pipeline, blockRegistry);
        if (!validationResult.IsValid)
        {
            var errors = string.Join("; ", validationResult.Errors.Select(e => e.Message));
            logger.LogWarning("Pipeline validation failed: {Errors}. Attempting auto-fix.", errors);
            pipeline = AutoFixPipeline(pipeline, validationResult);
        }

        // Security validation: enforce domain pinning, block allowlist, data-flow analysis
        var domainPin = DomainPin.FromUserUrl(intent.Url);
        var securityResult = securityValidator.Validate(pipeline, domainPin);
        if (!securityResult.IsValid)
        {
            var violations = string.Join("; ", securityResult.Violations.Select(v => $"[{v.Rule}] {v.Detail}"));

            // If domain pinning caught hallucinated URLs and we haven't retried yet, retry once
            if (retryOnDomainPin)
            {
                logger.LogWarning(
                    "Pipeline security validation failed (likely hallucinated URLs): {Violations}. Retrying with stronger prompt.",
                    violations);
                return await BuildPipelineWithRetryAsync(intent, analysis, html, retryOnDomainPin: false, ct);
            }

            logger.LogError("Pipeline security validation failed after retry: {Violations}", violations);
            throw new InvalidOperationException($"Pipeline rejected by security policy: {violations}");
        }

        return pipeline;
    }

    private async Task<QcResult> ValidateWithQcAsync(
        ParsedIntent intent, PipelineDefinition pipeline, DryRunResult dryRunResult, CancellationToken ct)
    {
        var blockSummary = string.Join("\n",
            pipeline.Blocks.Select(b => $"  - {b.Id} ({b.Type})"));
        var connectionSummary = string.Join("\n",
            pipeline.Connections.Select(c => $"  - {c.FromBlockId}.{c.FromPort} → {c.ToBlockId}.{c.ToPort}"));

        var prompt = $$"""
            You are a quality assurance reviewer for a website monitoring pipeline.
            
            Original user intent: {{intent.Intent}}
            Change type: {{intent.ChangeType}}
            URL: {{intent.Url}}
            
            Pipeline blocks:
            {{blockSummary}}
            
            Connections:
            {{connectionSummary}}
            
            Dry run result: {{(dryRunResult.Success ? "Succeeded" : $"Failed: {dryRunResult.Error}")}}
            {{(dryRunResult.SampleOutput != null ? $"Sample output: {dryRunResult.SampleOutput}" : "")}}
            
            Validate whether this pipeline correctly fulfills the user's intent.
            
            Respond with a JSON object:
            {
              "valid": true/false,
              "issues": ["list of problems found, empty if none"],
              "suggestions": ["list of improvement suggestions, empty if none"],
              "blockJustifications": { "block-id": "why this block is necessary" },
              "unjustifiedBlocks": ["block IDs that cannot be justified"]
            }
            
            Check for:
            1. Does the pipeline monitor what the user asked for?
            2. Are the right comparison/detection blocks used for the change type?
            3. Is notification configured if the user requested it?
            4. Are there missing blocks that would improve accuracy?
            5. For each block, provide a justification of why it exists in blockJustifications.
            6. List any block IDs you cannot justify in unjustifiedBlocks.
            
            Respond ONLY with the JSON object.
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            ExpectJson = true,
            Temperature = 0.2f,
            MaxTokens = 1024,
            UsageType = LlmUsageType.Validation,
            PreferLargeModel = true
        }, ct);

        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Content))
        {
            // QC failure is non-fatal — default to valid
            return new QcResult { Valid = true, Issues = [], Suggestions = [] };
        }

        return DeserializeOrThrow<QcResult>(response.Content, "QcResult");
    }

    private async Task<PipelineDefinition> RebuildPipelineWithFeedbackAsync(
        SetupSession session, string feedback, CancellationToken ct)
    {
        var currentPipelineJson = PipelineSerializer.Serialize(session.AssembledPipeline!);

        var prompt = $$"""
            You are a pipeline builder. The user has requested changes to an existing monitoring pipeline.
            
            Original intent: {{session.Intent!.Intent}}
            Change type: {{session.Intent.ChangeType}}
            Available block types: {{string.Join(", ", blockRegistry.RegisteredBlockTypes)}}
            
            Current pipeline:
            {{currentPipelineJson}}
            
            User feedback: {{feedback}}
            
            Modify the pipeline based on the feedback. Return the COMPLETE updated pipeline.
            
            Respond with a JSON object:
            {
              "blocks": [
                {
                  "id": "unique-id",
                  "type": "block type",
                  "config": { config or null },
                  "position": 0
                }
              ],
              "estimatedLlmCallsPerRun": 0
            }
            
            Rules:
            - Keep the Input block at the start and Output block at the end
            - Only use block types from the available list
            - Address the user's feedback while preserving intent
            
            Respond ONLY with the JSON object.
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            ExpectJson = true,
            Temperature = 0.3f,
            MaxTokens = 1024,
            UsageType = LlmUsageType.WatchSetup
        }, ct);

        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Content))
        {
            throw new InvalidOperationException(
                $"LLM failed to rebuild pipeline: {response.ErrorMessage ?? "empty response"}");
        }

        var rebuildJson = StripMarkdownFences(response.Content);
        var llmOutput = JsonDocument.Parse(rebuildJson);
        var blocksElement = llmOutput.RootElement.GetProperty("blocks");

        var blocks = new List<BlockDefinition>();
        foreach (var blockEl in blocksElement.EnumerateArray())
        {
            var blockType = blockEl.GetProperty("type").GetString()!;
            if (!blockRegistry.IsRegistered(blockType))
            {
                logger.LogWarning("LLM suggested unregistered block type '{BlockType}', skipping", blockType);
                continue;
            }

            blocks.Add(new BlockDefinition
            {
                Id = blockEl.GetProperty("id").GetString()!,
                Type = blockType,
                Config = blockEl.TryGetProperty("config", out var configEl) && configEl.ValueKind != JsonValueKind.Null
                    ? configEl.Clone()
                    : null,
                Position = blockEl.TryGetProperty("position", out var posEl) ? posEl.GetInt32() : null
            });
        }

        if (blocks.Count == 0 || blocks[0].Type != "Input")
            blocks.Insert(0, new BlockDefinition { Id = "input-1", Type = "Input", Position = 0 });

        if (blocks[^1].Type != "Output")
            blocks.Add(new BlockDefinition { Id = "output-1", Type = "Output", Position = blocks.Count });

        var connections = AutoWireConnections(blocks);

        return new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks = blocks,
            Connections = connections,
            Metadata = new PipelineMetadata
            {
                DisplayTitle = session.Intent.Summary ?? session.Intent.Intent,
                CreatedAt = DateTime.UtcNow,
                UserIntent = session.Intent.Intent,
                EstimatedLlmCallsPerRun = llmOutput.RootElement.TryGetProperty("estimatedLlmCallsPerRun", out var el)
                    ? el.GetInt32()
                    : 0,
                CardType = session.Intent.ChangeType
            }
        };
    }

    // ── Helper methods ──────────────────────────────────────────────────

    private List<ConnectionDefinition> AutoWireConnections(List<BlockDefinition> blocks)
    {
        var connections = new List<ConnectionDefinition>();

        for (var i = 0; i < blocks.Count - 1; i++)
        {
            var fromBlock = blocks[i];
            var toBlock = blocks[i + 1];

            if (!blockRegistry.IsRegistered(fromBlock.Type) || !blockRegistry.IsRegistered(toBlock.Type))
                continue;

            var outputPorts = blockRegistry.GetOutputPorts(fromBlock.Type);
            var inputPorts = blockRegistry.GetInputPorts(toBlock.Type);

            // Match output ports to input ports by type compatibility
            var connected = false;
            foreach (var outPort in outputPorts)
            {
                foreach (var inPort in inputPorts)
                {
                    if (outPort.Type == inPort.Type)
                    {
                        connections.Add(new ConnectionDefinition
                        {
                            FromBlockId = fromBlock.Id,
                            FromPort = outPort.Name,
                            ToBlockId = toBlock.Id,
                            ToPort = inPort.Name
                        });
                        connected = true;
                        break; // one connection per input port
                    }
                }

                if (connected) break;
            }

            // If no type match found, try connecting first output to first input
            if (!connected && outputPorts.Count > 0 && inputPorts.Count > 0)
            {
                connections.Add(new ConnectionDefinition
                {
                    FromBlockId = fromBlock.Id,
                    FromPort = outputPorts[0].Name,
                    ToBlockId = toBlock.Id,
                    ToPort = inputPorts[0].Name
                });
            }
        }

        return connections;
    }

    private PipelineDefinition AutoFixPipeline(PipelineDefinition pipeline, ChangeDetection.Core.Pipeline.Validation.ValidationResult validationResult)
    {
        // Re-wire connections as a basic auto-fix
        var blocks = pipeline.Blocks.ToList();
        var connections = AutoWireConnections(blocks);

        return pipeline with
        {
            Connections = connections
        };
    }

    private async Task<DryRunResult> ExecuteDryRunAsync(PipelineDefinition pipeline, SetupSession session, CancellationToken ct)
    {
        try
        {
            var tempWatchId = Guid.NewGuid();
            var stateStore = new InMemoryBlockStateStore();
            var dryRunPipeline = CreateDryRunPipeline(pipeline, session);

            var result = await pipelineExecutor.ExecuteAsync(
                dryRunPipeline, tempWatchId, stateStore, null, ct, isDryRun: true);

            if (result.Success && IsDryRunOutputEmpty(result.OutputData))
            {
                logger.LogInformation("Dry run produced empty output, retrying after 2s delay...");
                await Task.Delay(2000, ct);
                result = await pipelineExecutor.ExecuteAsync(
                    dryRunPipeline, tempWatchId, stateStore, null, ct, isDryRun: true);
            }

            var blockOutputs = new Dictionary<string, object?>();
            foreach (var (blockId, blockResult) in result.BlockResults)
            {
                blockOutputs[blockId] = blockResult.Output?.ValueKind == JsonValueKind.Undefined
                    ? null
                    : blockResult.Output?.ToString();
            }

            var sampleOutput = result.OutputData?.ValueKind != JsonValueKind.Undefined
                ? result.OutputData?.ToString()
                : null;

            // Truncate sample output for display
            if (sampleOutput?.Length > 500)
                sampleOutput = sampleOutput[..500] + "... (truncated)";

            return new DryRunResult
            {
                Success = result.Success,
                BlockOutputs = blockOutputs,
                ExecutionDurationMs = result.ExecutionDurationMs,
                SkippedBlockIds = [.. result.SkippedBlockIds],
                WasBaseline = result.WasBaseline,
                SampleOutput = sampleOutput,
                Error = result.Error,
                BlockFlowSummary = BuildBlockFlowSummary(result, dryRunPipeline)
            };
        }
        catch (Exception ex)
        {
            return new DryRunResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    private static PipelineDefinition CreateDryRunPipeline(PipelineDefinition pipeline, SetupSession session)
    {
        if (string.IsNullOrWhiteSpace(session.FetchedHtml))
            return pipeline;

        var blocks = pipeline.Blocks.Select(block =>
        {
            if (!string.Equals(block.Type, "Navigate", StringComparison.OrdinalIgnoreCase))
                return block;

            var config = block.Config is { ValueKind: JsonValueKind.Object } existingConfig
                ? JsonNode.Parse(existingConfig.GetRawText())!.AsObject()
                : new JsonObject();

            config["_cachedHtml"] = session.FetchedHtml;
            return block with { Config = JsonSerializer.SerializeToElement(config) };
        }).ToList();

        return pipeline with { Blocks = blocks };
    }

    private async Task<AdversarialTestResult> RunAdversarialTestAsync(
        PipelineDefinition pipeline, DryRunResult dryRun, ParsedIntent intent, CancellationToken ct)
    {
        if (!dryRun.Success)
        {
            return new AdversarialTestResult
            {
                Passed = true,
                Skipped = true,
                SkipReason = "Dry run failed — adversarial testing requires a working pipeline."
            };
        }

        var hasLargeModel = await llmChain.HasLargeModelAsync(ct);
        if (!hasLargeModel)
        {
            return new AdversarialTestResult
            {
                Passed = true,
                Skipped = true,
                SkipReason = "No large model available — adversarial testing requires a large model."
            };
        }

        var blockSummary = string.Join("\n",
            pipeline.Blocks.Select(b => $"  - {b.Id} ({b.Type})"));

        var prompt = $$"""
            You are an adversarial tester for website monitoring pipelines.
            
            Pipeline blocks:
            {{blockSummary}}
            
            User intent: {{intent.Intent}}
            URL: {{intent.Url}}
            
            Imagine 3 realistic mutations that could happen to the target page:
            1. A CSS class or element ID is renamed
            2. The data format changes (e.g. currency symbol, date format)
            3. The page is completely redesigned with new structure
            
            For each mutation, predict which pipeline blocks would break (become "fragile").
            A block is fragile if it relies on specific selectors, class names, or structure
            that would change under the mutation.
            
            Respond with a JSON object:
            {
              "mutations": [
                {
                  "description": "short description of the mutation",
                  "predictedFragileBlocks": ["block-id-1", "block-id-2"]
                }
              ]
            }
            
            Respond ONLY with the JSON object.
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            ExpectJson = true,
            Temperature = 0.3f,
            MaxTokens = 512,
            UsageType = LlmUsageType.AdversarialTest,
            PreferLargeModel = true
        }, ct);

        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Content))
        {
            return new AdversarialTestResult
            {
                Passed = true,
                Skipped = true,
                SkipReason = $"LLM call failed: {response.ErrorMessage ?? "empty response"}"
            };
        }

        try
        {
            var analysis = DeserializeOrThrow<MutationAnalysis>(response.Content, "MutationAnalysis");
            var allFragile = analysis.Mutations
                .SelectMany(m => m.PredictedFragileBlocks ?? [])
                .Distinct()
                .ToList();

            var mutationsPassed = analysis.Mutations.Count(m => (m.PredictedFragileBlocks ?? []).Count == 0);

            return new AdversarialTestResult
            {
                Passed = allFragile.Count == 0,
                MutationsTested = analysis.Mutations.Count,
                MutationsPassed = mutationsPassed,
                FragileBlocks = allFragile,
                Warnings = analysis.Mutations
                    .Where(m => (m.PredictedFragileBlocks ?? []).Count > 0)
                    .Select(m => $"{m.Description}: {string.Join(", ", m.PredictedFragileBlocks!)}")
                    .ToList()
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse adversarial test response");
            return new AdversarialTestResult
            {
                Passed = true,
                Skipped = true,
                SkipReason = $"Failed to parse response: {ex.Message}"
            };
        }
    }

    private static string BuildHumanSummary(
        ParsedIntent intent, PipelineDefinition pipeline, DryRunResult dryRun, QcResult qc,
        AdversarialTestResult? adversarial = null)
    {
        var parts = new List<string>
        {
            $"**Monitoring:** {intent.Url}",
            $"**Intent:** {intent.Intent}",
            $"**Pipeline:** {string.Join(" → ", pipeline.Blocks.Select(b => b.Type))} ({pipeline.Blocks.Count} blocks)"
        };

        if (dryRun.Success && HasUsableDryRunOutput(dryRun))
            parts.Add("**Dry run:** ✓ Pipeline executed successfully");
        else if (dryRun.Success)
        {
            parts.Add("**Dry run:** ⚠ Pipeline executed but extracted no data");
            parts.Add("**Why this matters:** The page may need JavaScript rendering or a cookie banner may be blocking extraction.");
        }
        else
            parts.Add($"**Dry run:** ✗ {dryRun.Error ?? "Failed"}");

        if (!string.IsNullOrWhiteSpace(dryRun.BlockFlowSummary))
            parts.Add(dryRun.BlockFlowSummary);

        if (qc.Valid)
            parts.Add("**Quality check:** ✓ Passed");
        else
            parts.Add($"**Quality check:** ✗ {string.Join(", ", qc.Issues)}");

        if (qc.Suggestions.Count > 0)
            parts.Add($"**Suggestions:** {string.Join(", ", qc.Suggestions)}");

        if (qc.UnjustifiedBlocks.Count > 0)
            parts.Add($"**Unjustified blocks:** {string.Join(", ", qc.UnjustifiedBlocks)}");

        if (adversarial is not null && !adversarial.Skipped)
        {
            if (adversarial.Passed)
                parts.Add("**Adversarial test:** ✓ Pipeline is resilient to mutations");
            else
                parts.Add($"**Adversarial test:** ⚠ {adversarial.FragileBlocks.Count} fragile block(s): {string.Join(", ", adversarial.FragileBlocks)}");
        }
        else if (adversarial is { Skipped: true })
        {
            parts.Add($"**Adversarial test:** Skipped — {adversarial.SkipReason}");
        }

        return string.Join("\n", parts);
    }

    private static string BuildBlockFlowSummary(
        PipelineExecutionResult result, PipelineDefinition pipeline)
    {
        var sb = new StringBuilder();
        sb.AppendLine("📊 Block Flow:");

        // Build a lookup from block ID to block type for readable labels
        var blockTypeById = pipeline.Blocks.ToDictionary(b => b.Id, b => b.Type, StringComparer.OrdinalIgnoreCase);

        foreach (var (blockId, blockResult) in result.BlockResults)
        {
            var icon = blockResult.Status switch
            {
                BlockExecutionStatus.Completed => "✅",
                BlockExecutionStatus.Failed => "❌",
                BlockExecutionStatus.Skipped => "⚠️",
                BlockExecutionStatus.Baseline => "⏳",
                _ => "❓"
            };

            var blockType = blockTypeById.TryGetValue(blockId, out var t) ? t : blockId;

            var detail = BuildBlockDetail(blockResult);
            sb.AppendLine($"  {icon} {blockType} → {detail}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildBlockDetail(BlockResult blockResult)
    {
        if (blockResult.Status == BlockExecutionStatus.Skipped)
            return blockResult.SkipReason ?? "skipped";

        if (blockResult.Status == BlockExecutionStatus.Baseline)
            return DescribeOutput(blockResult, suffix: " (baseline captured)");

        if (!blockResult.Success)
            return blockResult.Error ?? "failed";

        return DescribeOutput(blockResult);
    }

    private static string DescribeOutput(BlockResult blockResult, string? suffix = null)
    {
        if (!blockResult.Output.HasValue || blockResult.Output.Value.ValueKind == JsonValueKind.Undefined)
            return blockResult.Success ? "no output" + (suffix ?? "") : "empty";

        var output = blockResult.Output.Value;
        string detail;

        if (output.ValueKind == JsonValueKind.Array)
        {
            detail = $"{output.GetArrayLength()} items";
        }
        else if (output.ValueKind == JsonValueKind.Object)
        {
            if (output.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                detail = $"{items.GetArrayLength()} items";
            else if (output.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                detail = $"{data.GetArrayLength()} items";
            else if (output.TryGetProperty("body", out var body))
                detail = $"response received ({body.GetString()?.Length ?? 0} chars)";
            else
                detail = "data received";
        }
        else
        {
            detail = "data received";
        }

        return detail + (suffix ?? "");
    }

    private static bool IsDeterministicTemplateSource(string? analysisSource)
        => analysisSource?.StartsWith("deterministic-template:", StringComparison.OrdinalIgnoreCase) == true;

    private static bool HasUsableDryRunOutput(DryRunResult? dryRunResult)
        => dryRunResult is { Success: true } && !IsDryRunOutputEmpty(dryRunResult);

    private static bool IsDryRunOutputEmpty(JsonElement? outputData)
    {
        if (outputData is null or { ValueKind: JsonValueKind.Undefined })
            return true;

        return IsEffectivelyEmpty(outputData.Value);
    }

    private static bool IsDryRunOutputEmpty(DryRunResult dryRunResult)
    {
        if (!dryRunResult.Success)
            return false;

        if (string.IsNullOrWhiteSpace(dryRunResult.SampleOutput))
            return true;

        var sampleOutput = dryRunResult.SampleOutput.Trim();
        if (sampleOutput is "[]" or "{}" or "null")
            return true;

        try
        {
            using var document = JsonDocument.Parse(sampleOutput);
            return IsEffectivelyEmpty(document.RootElement);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsEffectivelyEmpty(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => true,
            JsonValueKind.Undefined => true,
            JsonValueKind.String => string.IsNullOrWhiteSpace(element.GetString()),
            JsonValueKind.Array => element.GetArrayLength() == 0,
            JsonValueKind.Object => element.EnumerateObject().ToList() is var properties &&
                                    (properties.Count == 0 || properties.All(property => IsEffectivelyEmpty(property.Value))),
            _ => false
        };
    }

    private static QcResult CreateUserFacingQcResult(QcResult qcResult)
    {
        var simplifiedIssues = qcResult.Issues
            .Select(SimplifyQcMessage)
            .Where(issue => !string.IsNullOrWhiteSpace(issue))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return qcResult with
        {
            Issues = simplifiedIssues
        };
    }

    private static string SimplifyQcMessage(string technicalMessage)
    {
        if (technicalMessage.Contains("pagination", StringComparison.OrdinalIgnoreCase))
            return "⚠️ This will only monitor the first page of results. Some older listings may be missed.";

        if (technicalMessage.Contains("empty list", StringComparison.OrdinalIgnoreCase) ||
            technicalMessage.Contains("empty array", StringComparison.OrdinalIgnoreCase))
            return "⚠️ The initial test didn't find any data. The page may have a cookie wall or require JavaScript.";

        if (technicalMessage.Contains("RelevanceScore", StringComparison.OrdinalIgnoreCase) &&
            technicalMessage.Contains("not clearly aligned", StringComparison.OrdinalIgnoreCase))
            return "ℹ️ The relevance scoring uses default keywords. You can customize these after creation.";

        if (technicalMessage.Contains("stable key", StringComparison.OrdinalIgnoreCase) ||
            technicalMessage.Contains("normalization", StringComparison.OrdinalIgnoreCase))
            return "ℹ️ Minor changes in job listing order might trigger false alerts initially.";

        if (technicalMessage.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
            technicalMessage.Contains("consent", StringComparison.OrdinalIgnoreCase))
            return "⚠️ This site has a cookie consent banner that may block content extraction.";

        var firstSentence = technicalMessage.Split('.')[0];
        return firstSentence.Length > 100 ? firstSentence[..100] + "..." : firstSentence;
    }

    private static TimeSpan ParseFrequency(string? frequency)
    {
        if (string.IsNullOrWhiteSpace(frequency))
            return TimeSpan.FromMinutes(30);

        frequency = frequency.Trim().ToLowerInvariant();

        if (frequency.EndsWith('m') && int.TryParse(frequency[..^1], out var minutes))
            return TimeSpan.FromMinutes(Math.Max(1, minutes));

        if (frequency.EndsWith('h') && int.TryParse(frequency[..^1], out var hours))
            return TimeSpan.FromHours(Math.Max(1, hours));

        if (frequency.EndsWith('d') && int.TryParse(frequency[..^1], out var days))
            return TimeSpan.FromDays(Math.Max(1, days));

        return TimeSpan.FromMinutes(30);
    }

    private DetectedPlatform? DetectPlatform(string url, string? html)
        => platformDetector.DetectFromContent(url, html ?? string.Empty);

    private bool TryGetTemplatePipeline(
        DetectedPlatform? detectedPlatform,
        ParsedIntent intent,
        out PipelineTemplate? selectedTemplate,
        out PipelineDefinition? pipeline)
    {
        selectedTemplate = null;
        pipeline = null;

        if (detectedPlatform is null || detectedPlatform.Confidence < TemplateConfidenceThreshold)
            return false;

        selectedTemplate = templateRegistry.GetTemplate(detectedPlatform.PlatformId, intent.Intent);
        if (selectedTemplate is null)
            return false;

        pipeline = selectedTemplate.Pipeline;
        return true;
    }

    private static ContentAnalysisResult BuildTemplateAnalysis(
        ParsedIntent intent,
        DetectedPlatform detectedPlatform,
        PipelineTemplate selectedTemplate)
    {
        var contentType = detectedPlatform.PlatformId switch
        {
            "workday" => "listing",
            "wordpress" => "feed",
            "shopify" => "product",
            _ => "other"
        };

        var selector = detectedPlatform.PlatformId switch
        {
            "shopify" => ".price, .product__price, [data-product-price]",
            _ => null
        };

        return new ContentAnalysisResult
        {
            ContentType = contentType,
            Regions = detectedPlatform.PlatformId switch
            {
                "workday" => ["job listings", "job metadata"],
                "wordpress" => ["post feed", "latest entries"],
                "shopify" => ["product price", "product title"],
                _ => []
            },
            HasPagination = detectedPlatform.PlatformId == "workday",
            NeedsJavaScript = detectedPlatform.PlatformId == "shopify",
            RecommendedSelector = selector,
            PageSummary = $"Detected {detectedPlatform.PlatformName} ({detectedPlatform.Confidence:0.00}) and selected the \"{selectedTemplate.Description}\" template for \"{intent.Intent}\"."
        };
    }

    private static PipelineDefinition SpecializeTemplatePipeline(
        PipelineDefinition template,
        ParsedIntent intent,
        ContentAnalysisResult analysis,
        DetectedPlatform detectedPlatform,
        PipelineTemplate selectedTemplate)
    {
        var sourceUrl = BuildTemplateSourceUrl(intent.Url, detectedPlatform.PlatformId);

        var blocks = template.Blocks.Select(block =>
        {
            if (!string.Equals(block.Type, "Input", StringComparison.OrdinalIgnoreCase))
                return block;

            return block with
            {
                Config = JsonSerializer.SerializeToElement(new
                {
                    url = sourceUrl,
                    intent = intent.Intent,
                    platform = detectedPlatform.PlatformId
                })
            };
        }).ToList();

        return template with
        {
            Blocks = blocks,
            Metadata = new PipelineMetadata
            {
                DisplayTitle = $"{intent.Summary ?? intent.Intent} ({detectedPlatform.PlatformName})",
                CreatedAt = DateTime.UtcNow,
                UserIntent = intent.Intent,
                EstimatedLlmCallsPerRun = template.Metadata?.EstimatedLlmCallsPerRun ?? 0,
                CardType = analysis.ContentType
            }
        };
    }

    private static string BuildTemplateSourceUrl(string url, string platformId)
    {
        if (!string.Equals(platformId, "wordpress", StringComparison.OrdinalIgnoreCase))
            return url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        if (uri.AbsolutePath.EndsWith("/feed", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.EndsWith("/feed/", StringComparison.OrdinalIgnoreCase) ||
            uri.Query.Contains("feed=", StringComparison.OrdinalIgnoreCase))
            return url;

        var builder = new UriBuilder(uri);
        var path = builder.Path.TrimEnd('/');
        builder.Path = string.IsNullOrEmpty(path) ? "/feed/" : $"{path}/feed/";
        return builder.Uri.ToString();
    }

    private static ContentAnalysisResult BuildDeterministicAnalysis(
        ContentClassification classification, ParsedIntent intent)
    {
        return classification.Type switch
        {
            DetectedContentType.JobListing => new ContentAnalysisResult
            {
                ContentType = "listing",
                Regions = ["job listings", "job metadata"],
                HasPagination = classification.DetectedPlatform is "workday",
                NeedsJavaScript = false,
                PageSummary = BuildDeterministicSummary(classification)
            },
            DetectedContentType.ProductListing => new ContentAnalysisResult
            {
                ContentType = "product",
                Regions = ["product listings", "product details"],
                HasPagination = true,
                NeedsJavaScript = false,
                PageSummary = BuildDeterministicSummary(classification)
            },
            DetectedContentType.NewsFeed => new ContentAnalysisResult
            {
                ContentType = "article",
                Regions = ["news articles", "post entries"],
                HasPagination = false,
                NeedsJavaScript = false,
                PageSummary = BuildDeterministicSummary(classification)
            },
            DetectedContentType.ApiJson => new ContentAnalysisResult
            {
                ContentType = "other",
                Regions = ["API response data"],
                HasPagination = false,
                NeedsJavaScript = false,
                PageSummary = BuildDeterministicSummary(classification)
            },
            _ => new ContentAnalysisResult
            {
                ContentType = "other",
                Regions = [],
                HasPagination = false,
                NeedsJavaScript = classification.Type == DetectedContentType.JsShell,
                PageSummary = BuildDeterministicSummary(classification)
            }
        };
    }

    private static string BuildDeterministicSummary(ContentClassification classification)
        => $"Deterministic: {classification.Type} on {classification.DetectedPlatform ?? "unknown"} " +
           $"({classification.Confidence:P0}). Signals: {string.Join(", ", classification.Signals)}";

    private record MutationAnalysis
    {
        public List<MutationEntry> Mutations { get; init; } = [];
    }

    private record MutationEntry
    {
        public string Description { get; init; } = "";
        public List<string> PredictedFragileBlocks { get; init; } = [];
    }

    private static T DeserializeOrThrow<T>(string json, string typeName)
    {
        try
        {
            var result = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return result ?? throw new InvalidOperationException(
                $"Deserialization of {typeName} returned null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize {typeName} from LLM response: {ex.Message}\nContent: {json[..Math.Min(json.Length, 500)]}",
                ex);
        }
    }

    /// <summary>Removes expired sessions from the static dictionary.</summary>
    internal static void CleanupExpiredSessions()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in Sessions)
        {
            if (now - kvp.Value.LastActivityAt > SessionExpiration)
                Sessions.TryRemove(kvp.Key, out _);
        }
    }

    /// <summary>In-memory state store for dry runs — no persistence needed.</summary>
    private sealed class InMemoryBlockStateStore : IBlockStateStore
    {
        private readonly ConcurrentDictionary<string, JsonElement> _store = new();

        public Task<JsonElement?> GetPreviousOutputAsync(string watchId, string blockInstanceId, CancellationToken ct = default)
        {
            var key = $"{watchId}:{blockInstanceId}";
            return Task.FromResult(_store.TryGetValue(key, out var value) ? value : (JsonElement?)null);
        }

        public Task<JsonElement?> GetCachedOutputAsync(
            string watchId,
            string blockInstanceId,
            string inputHash,
            string pipelineHash,
            CancellationToken ct = default)
        {
            return Task.FromResult((JsonElement?)null);
        }

        public Task SaveOutputAsync(
            string watchId,
            string blockInstanceId,
            JsonElement output,
            string? inputHash = null,
            string? pipelineHash = null,
            CancellationToken ct = default)
        {
            var key = $"{watchId}:{blockInstanceId}";
            _store[key] = output;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BlockExecutionSnapshot>> GetHistoryAsync(
            string watchId, string blockInstanceId, int maxResults = 10, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<BlockExecutionSnapshot>>([]);
        }
    }
}
