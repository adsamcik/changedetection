using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Core.Pipeline.Validation;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.SetupPipeline;

public class ComposableSetupPipeline(
    ILlmProviderChain llmChain,
    IContentFetcher contentFetcher,
    IPipelineExecutor pipelineExecutor,
    IPipelineValidator pipelineValidator,
    IBlockRegistry blockRegistry,
    IRepository<WatchedSite> watchRepo,
    ILogger<ComposableSetupPipeline> logger) : IComposableSetupPipeline
{
    private static readonly ConcurrentDictionary<string, SetupSession> Sessions = new();
    private static readonly TimeSpan SessionExpiration = TimeSpan.FromMinutes(30);

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
        yield return Progress(SetupPhase.IntentParsing, SetupProgressType.Started, "Analyzing your request...");
        yield return Progress(SetupPhase.IntentParsing, SetupProgressType.Thinking, "Understanding what you want to monitor...");

        ParsedIntent? intent = null;
        string? parseError = null;

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

        // Phase 3: Analyze content
        yield return Progress(SetupPhase.ContentAnalysis, SetupProgressType.Thinking, "Analyzing page structure...");

        ContentAnalysisResult? analysis = null;
        string? analysisError = null;

        try
        {
            analysis = await AnalyzeContentAsync(session.FetchedHtml, intent, ct);
            session.ContentAnalysis = analysis;
            session.LlmCallCount++;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to analyze content for session {SessionId}", session.Id);
            analysisError = ex.Message;
        }

        if (analysisError != null)
        {
            yield return FailedProgress(SetupPhase.ContentAnalysis, "Failed to analyze the page content.", analysisError);
            yield break;
        }

        yield return Progress(SetupPhase.ContentAnalysis, SetupProgressType.Progress,
            "Page analysis complete.", analysis!.PageSummary);

        // Checkpoint 1: Show understanding to user
        session.CurrentPhase = SetupPhase.Checkpoint1;
        session.LastActivityAt = DateTime.UtcNow;

        var summary = intent.Summary
            ?? $"I'll watch {intent.Url} for {intent.ChangeType} changes: {intent.Intent}";

        yield return new SetupProgress
        {
            Phase = SetupPhase.Checkpoint1,
            Type = SetupProgressType.CheckpointReached,
            Message = summary,
            Intent = intent,
            Detail = $"Session: {session.Id}"
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

        try
        {
            pipeline = await BuildPipelineAsync(session.Intent!, session.ContentAnalysis!, session.FetchedHtml!, ct);
            session.AssembledPipeline = pipeline;
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

        yield return Progress(SetupPhase.PipelineBuilding, SetupProgressType.Progress,
            $"Pipeline assembled with {pipeline!.Blocks.Count} blocks.",
            string.Join(" → ", pipeline.Blocks.Select(b => b.Type)));

        // Phase 5: Dry run
        session.CurrentPhase = SetupPhase.DryRun;
        yield return Progress(SetupPhase.DryRun, SetupProgressType.Started, "Running pipeline to verify it works...");

        DryRunResult dryRunResult;
        try
        {
            dryRunResult = await ExecuteDryRunAsync(pipeline, ct);
            session.DryRunResult = dryRunResult;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Dry run failed for session {SessionId}", sessionId);
            dryRunResult = new DryRunResult { Success = false, Error = ex.Message };
            session.DryRunResult = dryRunResult;
        }

        yield return Progress(SetupPhase.DryRun, SetupProgressType.Progress,
            dryRunResult.Success
                ? "Dry run succeeded — pipeline produces valid output."
                : "Dry run completed with issues.",
            dryRunResult.SampleOutput ?? dryRunResult.Error);

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

        // Checkpoint 2: Show pipeline + results for approval
        session.CurrentPhase = SetupPhase.Checkpoint2;
        session.LastActivityAt = DateTime.UtcNow;

        var humanSummary = BuildHumanSummary(session.Intent!, pipeline, dryRunResult, qcResult);

        yield return new SetupProgress
        {
            Phase = SetupPhase.Checkpoint2,
            Type = SetupProgressType.CheckpointReached,
            Message = "Pipeline ready for your approval.",
            Proposal = new PipelineProposal
            {
                Pipeline = pipeline,
                HumanSummary = humanSummary,
                DryRun = dryRunResult,
                QcValidation = qcResult
            },
            Detail = $"Session: {session.Id}"
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

                var dryRunResult = await ExecuteDryRunAsync(revisedPipeline, ct);
                session.DryRunResult = dryRunResult;

                var qcResult = await ValidateWithQcAsync(session.Intent!, revisedPipeline, dryRunResult, ct);
                session.QcResult = qcResult;
                session.LlmCallCount++;

                var humanSummary = BuildHumanSummary(session.Intent!, revisedPipeline, dryRunResult, qcResult);

                proposal = new PipelineProposal
                {
                    Pipeline = revisedPipeline,
                    HumanSummary = humanSummary,
                    DryRun = dryRunResult,
                    QcValidation = qcResult
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

            yield return new SetupProgress
            {
                Phase = SetupPhase.Checkpoint2,
                Type = SetupProgressType.CheckpointReached,
                Message = "Revised pipeline ready for approval.",
                Proposal = proposal,
                Detail = $"Session: {session.Id}"
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

    // ── Progress helpers ────────────────────────────────────────────────

    private static SetupProgress Progress(SetupPhase phase, SetupProgressType type, string message, string? detail = null) =>
        new() { Phase = phase, Type = type, Message = message, Detail = detail };

    private static SetupProgress FailedProgress(SetupPhase phase, string message, string? error = null) =>
        new() { Phase = phase, Type = SetupProgressType.Failed, Message = message, Error = error };

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
        // Truncate HTML to avoid exceeding token limits
        var truncatedHtml = html.Length > 15_000
            ? html[..15_000] + "\n<!-- truncated -->"
            : html;

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
            Include a "Navigate" block to fetch the page, then appropriate extraction and comparison blocks.
            
            Common patterns:
            - Price tracking: Input → Navigate → Filter (CSS selector) → ExtractSchema → NumericDelta → Condition → Notify → Output
            - Content changes: Input → Navigate → Filter → HashCompare → Output
            - List monitoring: Input → Navigate → Filter → ExtractSchema → ListDiff → Output
            - Availability: Input → Navigate → Filter → ExtractSchema → Condition → Notify → Output
            
            Respond with a JSON object:
            {
              "blocks": [
                {
                  "id": "unique-id (e.g. 'navigate-1')",
                  "type": "block type from the available list",
                  "config": { block-specific config or null },
                  "position": 0
                }
              ],
              "estimatedLlmCallsPerRun": 0
            }
            
            Config examples:
            - Navigate: { "timeoutSeconds": 30 }
            - Filter: { "cssSelector": "#price", "mode": "css" }
            - ExtractSchema: { "fields": [{"name": "price", "selector": ".price", "type": "number"}] }
            - Condition: { "operator": "lessThan", "field": "price", "value": 50 }
            - NumericDelta: { "field": "price", "threshold": 0.01 }
            - Notify: { "template": "Price changed from {previous} to {current}" }
            
            Rules:
            - Use block types ONLY from the available list
            - Every block needs a unique id in format "type-N" (e.g. "navigate-1")
            - Include Input at position 0 and Output at the last position
            - Match the pipeline pattern to the change type and page analysis
            
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
                $"LLM failed to build pipeline: {response.ErrorMessage ?? "empty response"}");
        }

        var llmOutput = JsonDocument.Parse(response.Content);
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

        // Auto-wire connections based on port compatibility
        var connections = AutoWireConnections(blocks);

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
              "suggestions": ["list of improvement suggestions, empty if none"]
            }
            
            Check for:
            1. Does the pipeline monitor what the user asked for?
            2. Are the right comparison/detection blocks used for the change type?
            3. Is notification configured if the user requested it?
            4. Are there missing blocks that would improve accuracy?
            
            Respond ONLY with the JSON object.
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            ExpectJson = true,
            Temperature = 0.2f,
            MaxTokens = 512,
            UsageType = LlmUsageType.Validation
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

        var llmOutput = JsonDocument.Parse(response.Content);
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

    private async Task<DryRunResult> ExecuteDryRunAsync(PipelineDefinition pipeline, CancellationToken ct)
    {
        try
        {
            var tempWatchId = Guid.NewGuid();
            var stateStore = new InMemoryBlockStateStore();

            var result = await pipelineExecutor.ExecuteAsync(pipeline, tempWatchId, stateStore, null, ct);

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
                SampleOutput = sampleOutput,
                Error = result.Error
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

    private static string BuildHumanSummary(
        ParsedIntent intent, PipelineDefinition pipeline, DryRunResult dryRun, QcResult qc)
    {
        var parts = new List<string>
        {
            $"**Monitoring:** {intent.Url}",
            $"**Intent:** {intent.Intent}",
            $"**Pipeline:** {string.Join(" → ", pipeline.Blocks.Select(b => b.Type))} ({pipeline.Blocks.Count} blocks)"
        };

        if (dryRun.Success)
            parts.Add("**Dry run:** ✓ Pipeline executed successfully");
        else
            parts.Add($"**Dry run:** ✗ {dryRun.Error ?? "Failed"}");

        if (qc.Valid)
            parts.Add("**Quality check:** ✓ Passed");
        else
            parts.Add($"**Quality check:** ✗ {string.Join(", ", qc.Issues)}");

        if (qc.Suggestions.Count > 0)
            parts.Add($"**Suggestions:** {string.Join(", ", qc.Suggestions)}");

        return string.Join("\n", parts);
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

        public Task SaveOutputAsync(string watchId, string blockInstanceId, JsonElement output, CancellationToken ct = default)
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
