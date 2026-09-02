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

public partial class ComposableSetupPipeline
{
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

        // Sanitize + extract a focused HTML excerpt so the LLM can see actual CSS classes,
        // DOM structure, and data patterns — without this, it's building selectors blind.
        var htmlExcerpt = PrepareHtmlExcerptForPipelineBuilder(html, analysis.RecommendedSelector);

        var prompt = $$"""
            You are a pipeline builder for a website monitoring system.

            Available block types: {{availableBlocks}}

            User intent: {{intent.Intent}}
            Change type: {{intent.ChangeType}}
            Page type: {{analysis.ContentType}}
            Page regions: {{string.Join(", ", analysis.Regions)}}
            Recommended selector: {{analysis.RecommendedSelector ?? "none"}}
            Needs JavaScript: {{analysis.NeedsJavaScript}}

            HTML EXCERPT from the target page (use this to pick accurate CSS selectors):
            ```html
            {{htmlExcerpt}}
            ```

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
            - STUDY THE HTML EXCERPT ABOVE. Use the actual CSS class names, element IDs, and DOM structure you see — do NOT guess generic selectors.
            - For ExtractSchema: set "scope" to the actual repeating element selector visible in the HTML (e.g. "tr.vacancy-specs", "li.job-item", "div.search-result"). Set "schema" field selectors to actual child elements visible in the HTML.
            - If the HTML shows a <table>, use table row selectors. If it shows <div> cards, use div selectors. Match what you SEE, not what you assume.
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

            SELF-ASSESSMENT (critical):
            After analyzing the HTML excerpt, if you cannot identify a clear repeating list of items (jobs, products, articles, etc.)
            with specific CSS classes or HTML patterns visible in the excerpt, respond with ONLY:
            {"cannotExtract": true, "reason": "explanation of why extraction is not possible"}
            Do NOT guess generic selectors like ".job-card", ".listing", ".result-item". Only build a pipeline if you can identify
            specific CSS classes or HTML patterns actually present in the excerpt above.

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

        // Guard 1: LLM self-assessment — if it signals it cannot extract, fail honestly
        if (llmOutput.RootElement.TryGetProperty("cannotExtract", out var cannotExtractEl)
            && cannotExtractEl.ValueKind == JsonValueKind.True)
        {
            var reason = llmOutput.RootElement.TryGetProperty("reason", out var reasonEl)
                ? reasonEl.GetString() ?? "no reason provided"
                : "no reason provided";
            logger.LogWarning(
                "LLM self-assessment: cannot extract from {Url} — {Reason}",
                intent.Url, reason);
            throw new InvalidOperationException($"LLM cannot extract: {reason}");
        }

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

        // Guard 3: Reject pipelines that use only generic/placeholder selectors (likely hallucinated)
        if (HasOnlyPlaceholderSelectors(pipeline))
        {
            logger.LogWarning(
                "Pipeline for {Url} uses only generic placeholder selectors — likely hallucinated, rejecting",
                intent.Url);
            throw new InvalidOperationException(
                "Pipeline uses only generic selectors — likely hallucinated");
        }

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

}
