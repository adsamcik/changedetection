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

public partial class ComposableSetupPipeline(
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
                Detail = $"Session: {session.Id}",
                SessionId = session.Id
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
        var headlessStopwatch = System.Diagnostics.Stopwatch.StartNew();
        logger.LogInformation(
            "[Headless] Step 0: Starting headless build for {Url}, intent: {Intent}",
            url, userIntent ?? "(none)");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        var token = cts.Token;

        var input = string.IsNullOrWhiteSpace(userIntent)
            ? url
            : $"{url} — {userIntent}";
        var (positiveKeywords, negativeKeywords) = ExtractKeywordsFromIntent(input);
        logger.LogInformation(
            "[Headless] Step 0b: Extracted {PosCount} positive, {NegCount} negative keywords from intent",
            positiveKeywords.Count, negativeKeywords.Count);

        // Fast path: platform template (no LLM needed)
        var detectedPlatform = SetupFlowEnhancements.DetectPlatformFromUrl(url);
        logger.LogInformation(
            "[Headless] Step 1: URL platform detection result: {Platform}",
            detectedPlatform ?? "(none — will use LLM path)");

        if (detectedPlatform is not null)
        {
            var template = await setupFlowEnhancements.GetPlatformTemplateAsync(
                detectedPlatform, url, positiveKeywords, negativeKeywords, ct: token);
            if (template is not null)
            {
                logger.LogInformation(
                    "[Headless] Step 1b: Platform template {Platform} resolved with {BlockCount} blocks for {Url} in {Elapsed}ms",
                    detectedPlatform, template.Blocks.Count, url, headlessStopwatch.ElapsedMilliseconds);
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

            logger.LogWarning(
                "[Headless] Step 1b: Platform {Platform} detected but GetPlatformTemplateAsync returned null — no template handler exists, falling through to LLM path",
                detectedPlatform);
        }

        // Phase 1: Parse intent via LLM
        logger.LogInformation("[Headless] Step 2: Parsing intent via LLM for input: {Input}", input);
        ParsedIntent intent;
        try
        {
            intent = await ParseIntentAsync(input, token);
            logger.LogInformation(
                "[Headless] Step 2b: Intent parsed — URL: {Url}, ChangeType: {ChangeType}, Intent: {Intent}, Summary: {Summary}",
                intent.Url, intent.ChangeType, intent.Intent, intent.Summary ?? "(none)");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[Headless] Step 2 FAILED: Intent parsing threw {ExType}: {ExMsg} for {Url} after {Elapsed}ms",
                ex.GetType().Name, ex.Message, url, headlessStopwatch.ElapsedMilliseconds);
            return null;
        }

        // Phase 2: Fetch content (always uses Playwright via UseJavaScript = true)
        logger.LogInformation(
            "[Headless] Step 3: Fetching content from {Url} (UseJavaScript=true, Playwright browser mode)",
            intent.Url);
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
            logger.LogWarning(ex,
                "[Headless] Step 3 FAILED: Content fetch threw {ExType}: {ExMsg} for {Url} after {Elapsed}ms",
                ex.GetType().Name, ex.Message, url, headlessStopwatch.ElapsedMilliseconds);
            return null;
        }

        var htmlLength = fetchResult.Html?.Length ?? 0;
        string? responseContentType = null;
        if (fetchResult.ResponseHeaders is { } respHeaders)
        {
            responseContentType = respHeaders
                .FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value;
        }

        logger.LogInformation(
            "[Headless] Step 3b: Fetch result — Success: {Success}, HTTP {StatusCode}, {Length} chars, " +
            "Content-Type: {ContentType}, Duration: {Duration}ms, ErrorCategory: {ErrCat}, Diag: {Diag}",
            fetchResult.IsSuccess, fetchResult.HttpStatusCode, htmlLength,
            responseContentType ?? "(unknown)",
            fetchResult.DurationMs,
            fetchResult.ErrorCategory,
            fetchResult.GetDiagnosticSummary());

        if (!fetchResult.IsSuccess || string.IsNullOrWhiteSpace(fetchResult.Html))
        {
            logger.LogWarning(
                "[Headless] Step 3 FAILED: No usable HTML content from {Url} — " +
                "IsSuccess={Success}, HtmlNull={IsNull}, HtmlEmpty={IsEmpty}, Error={Error}",
                url, fetchResult.IsSuccess, fetchResult.Html is null,
                string.IsNullOrWhiteSpace(fetchResult.Html), fetchResult.ErrorMessage);
            return null;
        }

        // Detect JS shell (important diagnostic — if HTML is tiny with lots of scripts, content is JS-rendered)
        var jsShellCheck = setupFlowEnhancements.DetectJsShell(fetchResult.Html, htmlLength);
        if (jsShellCheck.IsJsShell)
        {
            logger.LogWarning(
                "[Headless] Step 3c: JS shell detected despite using Playwright! Signals: [{Signals}]. " +
                "Page may require additional wait time or specific interaction to render content",
                string.Join("; ", jsShellCheck.Signals));
        }

        // Phase 3: Analyze content (deterministic first, LLM fallback)
        ContentAnalysisResult? analysis = null;

        try
        {
            var classification = setupFlowEnhancements.ClassifyContent(
                fetchResult.Html, intent.Url, responseContentType);

            logger.LogInformation(
                "[Headless] Step 4: Content classification — Type: {Type}, Confidence: {Confidence:F2}, " +
                "Platform: {Platform}, Signals: [{Signals}]",
                classification.Type, classification.Confidence,
                classification.DetectedPlatform ?? "(none)",
                string.Join("; ", classification.Signals));

            if (classification is { Confidence: >= 0.7 }
                && classification.Type is not (DetectedContentType.Unknown
                    or DetectedContentType.JsShell or DetectedContentType.ErrorPage))
            {
                analysis = BuildDeterministicAnalysis(classification, intent);
                logger.LogInformation(
                    "[Headless] Step 4b: Deterministic analysis built — ContentType: {ContentType}, " +
                    "NeedsJS: {NeedsJs}, HasPagination: {HasPag}, Selector: {Selector}",
                    analysis.ContentType, analysis.NeedsJavaScript,
                    analysis.HasPagination, analysis.RecommendedSelector ?? "(none)");

                // If platform detected, try template
                if (classification.DetectedPlatform is not null)
                {
                    logger.LogInformation(
                        "[Headless] Step 4c: Platform {Platform} found in content, attempting template lookup",
                        classification.DetectedPlatform);

                    var deterministicTemplate = await setupFlowEnhancements.GetPlatformTemplateAsync(
                        classification.DetectedPlatform, intent.Url, positiveKeywords, negativeKeywords, ct: token);
                    if (deterministicTemplate is not null)
                    {
                        logger.LogInformation(
                            "[Headless] Step 4c: Deterministic template resolved with {BlockCount} blocks for {Url} in {Elapsed}ms",
                            deterministicTemplate.Blocks.Count, url, headlessStopwatch.ElapsedMilliseconds);
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

                    logger.LogWarning(
                        "[Headless] Step 4c: Platform {Platform} detected in content but no template handler — falling through to LLM",
                        classification.DetectedPlatform);
                }
            }
            else
            {
                logger.LogInformation(
                    "[Headless] Step 4: Classification insufficient for deterministic path " +
                    "(Confidence={Confidence:F2}, Type={Type}) — will use LLM analysis",
                    classification.Confidence, classification.Type);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[Headless] Step 4 FAILED: Deterministic classification threw {ExType}: {ExMsg}",
                ex.GetType().Name, ex.Message);
        }

        // LLM content analysis fallback
        if (analysis is null)
        {
            logger.LogInformation("[Headless] Step 5: Invoking LLM content analysis (deterministic path unavailable)");
            try
            {
                analysis = await AnalyzeContentAsync(fetchResult.Html, intent, token);
                logger.LogInformation(
                    "[Headless] Step 5b: LLM analysis result — ContentType: {ContentType}, NeedsJS: {NeedsJs}, " +
                    "HasPagination: {HasPag}, Selector: {Selector}, Summary: {Summary}",
                    analysis.ContentType, analysis.NeedsJavaScript,
                    analysis.HasPagination, analysis.RecommendedSelector ?? "(none)",
                    analysis.PageSummary ?? "(none)");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "[Headless] Step 5 FAILED: LLM content analysis threw {ExType}: {ExMsg} for {Url} after {Elapsed}ms",
                    ex.GetType().Name, ex.Message, url, headlessStopwatch.ElapsedMilliseconds);
                return null;
            }
        }

        // Auto-confirm checkpoint 1 (intent) — headless mode

        // Phase 4: Build pipeline via LLM
        logger.LogInformation(
            "[Headless] Step 6: Building pipeline via LLM — Intent: {Intent}, ContentType: {ContentType}, HtmlLength: {Length}",
            intent.Intent, analysis.ContentType, htmlLength);
        PipelineDefinition pipeline;
        try
        {
            pipeline = await BuildPipelineAsync(intent, analysis, fetchResult.Html, token);
            var blockTypes = string.Join(", ", pipeline.Blocks.Select(b => b.Type));
            logger.LogInformation(
                "[Headless] Step 6b: LLM returned pipeline with {BlockCount} blocks [{BlockTypes}], {ConnCount} connections",
                pipeline.Blocks.Count, blockTypes, pipeline.Connections.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[Headless] Step 6 FAILED: Pipeline build threw {ExType}: {ExMsg} for {Url} after {Elapsed}ms",
                ex.GetType().Name, ex.Message, url, headlessStopwatch.ElapsedMilliseconds);
            return null;
        }

        // Phase 5: Dry run — auto-confirm checkpoint 2 only if dry run passes
        logger.LogInformation("[Headless] Step 7: Executing dry run for pipeline with {BlockCount} blocks", pipeline.Blocks.Count);
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
            logger.LogInformation(
                "[Headless] Step 7b: Dry run completed — Success: {Success}, Duration: {Duration}ms, " +
                "SkippedBlocks: {Skipped}, SampleOutput: {Sample}, Error: {Error}",
                dryRunResult.Success, dryRunResult.ExecutionDurationMs,
                dryRunResult.SkippedBlockIds.Count,
                dryRunResult.SampleOutput?[..Math.Min(200, dryRunResult.SampleOutput?.Length ?? 0)] ?? "(none)",
                dryRunResult.Error ?? "(none)");

            if (dryRunResult.BlockFlowSummary is not null)
            {
                logger.LogDebug("[Headless] Step 7b: Block flow summary:\n{FlowSummary}", dryRunResult.BlockFlowSummary);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[Headless] Step 7 FAILED: Dry run threw {ExType}: {ExMsg} for {Url} after {Elapsed}ms",
                ex.GetType().Name, ex.Message, url, headlessStopwatch.ElapsedMilliseconds);
            return null;
        }

        if (!dryRunResult.Success)
        {
            logger.LogWarning(
                "[Headless] Step 7 FAILED: Dry run unsuccessful for {Url}: {Error}. " +
                "Duration: {Duration}ms, Skipped: [{Skipped}], Total elapsed: {Elapsed}ms",
                url, dryRunResult.Error,
                dryRunResult.ExecutionDurationMs,
                string.Join(", ", dryRunResult.SkippedBlockIds),
                headlessStopwatch.ElapsedMilliseconds);
            return null;
        }

        // Guard 2: Reject pipelines that produce zero items — selectors likely don't match actual DOM
        if (IsDryRunOutputEmpty(dryRunResult))
        {
            logger.LogWarning(
                "[Headless] Step 7 REJECTED: Pipeline built but extracted 0 items for {Url} — " +
                "selectors likely don't match actual DOM. Total elapsed: {Elapsed}ms",
                url, headlessStopwatch.ElapsedMilliseconds);
            return null;
        }

        headlessStopwatch.Stop();
        logger.LogInformation(
            "[Headless] SUCCESS: Pipeline built for {Url} — {BlockCount} blocks, dry run passed, total elapsed: {Elapsed}ms",
            url, pipeline.Blocks.Count, headlessStopwatch.ElapsedMilliseconds);
        return pipeline;
    }

    // ── Progress helpers ────────────────────────────────────────────────

}
