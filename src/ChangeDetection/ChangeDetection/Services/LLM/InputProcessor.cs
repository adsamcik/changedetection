using System.Text.Json;
using System.Text.RegularExpressions;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.LLM;

/// <summary>
/// Processes user input through a multi-stage LLM pipeline.
/// </summary>
public partial class InputProcessor : IInputProcessor
{
    private readonly ILlmProviderChain _llmChain;
    private readonly IWatchService _watchService;
    private readonly ILogger<InputProcessor> _logger;

    public InputProcessor(
        ILlmProviderChain llmChain,
        IWatchService watchService,
        ILogger<InputProcessor> logger)
    {
        _llmChain = llmChain;
        _watchService = watchService;
        _logger = logger;
    }

    public InputAnalysis Analyze(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new InputAnalysis
            {
                Type = InputType.Unknown,
                IsValid = false,
                ValidationMessage = "Input is empty"
            };
        }

        var trimmed = input.Trim();

        // Check for URL patterns
        if (UrlRegex().IsMatch(trimmed))
        {
            var normalizedUrl = NormalizeUrl(trimmed);
            var isValid = Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri);

            return new InputAnalysis
            {
                Type = InputType.Url,
                DetectedUrl = trimmed,
                NormalizedUrl = normalizedUrl,
                IsValid = isValid,
                ValidationMessage = isValid ? null : "Invalid URL format"
            };
        }

        // Check for URL-like patterns (www., domain.tld)
        if (DomainRegex().IsMatch(trimmed) && !trimmed.Contains(' '))
        {
            var normalizedUrl = $"https://{trimmed}";
            var isValid = Uri.TryCreate(normalizedUrl, UriKind.Absolute, out _);

            return new InputAnalysis
            {
                Type = InputType.Url,
                DetectedUrl = trimmed,
                NormalizedUrl = normalizedUrl,
                IsValid = isValid,
                ValidationMessage = isValid ? null : "Invalid URL format"
            };
        }

        // Otherwise, treat as natural language
        return new InputAnalysis
        {
            Type = InputType.NaturalLanguage,
            IsValid = true
        };
    }

    public async Task<LlmProcessResult> ProcessWithLlmAsync(string input, CancellationToken ct = default)
    {
        try
        {
            // Stage 1: Intent Classification
            var intent = await ClassifyIntentAsync(input, ct);
            _logger.LogInformation("Classified intent: {Intent}", intent);

            if (intent == IntentType.Help)
            {
                return CreateHelpResponse();
            }

            // Stage 2: Entity Extraction
            var parsedRequest = await ExtractEntitiesAsync(input, intent, ct);
            _logger.LogInformation("Extracted entities: {Request}", JsonSerializer.Serialize(parsedRequest));

            // Stage 3: Validation
            var validation = ValidateRequest(parsedRequest, intent);
            
            if (validation.NeedsClarification)
            {
                return new LlmProcessResult
                {
                    IsSuccess = true,
                    Intent = intent,
                    ParsedRequest = parsedRequest,
                    NeedsClarification = true,
                    ClarificationQuestions = validation.Questions,
                    Suggestions = validation.Suggestions
                };
            }

            // Stage 4: Execution
            return await ExecuteIntentAsync(intent, parsedRequest, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing input with LLM");
            return new LlmProcessResult
            {
                IsSuccess = false,
                ErrorMessage = "Failed to process your request. Please try again."
            };
        }
    }

    private async Task<IntentType> ClassifyIntentAsync(string input, CancellationToken ct)
    {
        var prompt = $"""
            Classify the user's intent from the following input. Respond with ONLY one of these exact words:
            - CreateWatch: User wants to monitor/watch a website for changes
            - ModifyWatch: User wants to update/change an existing watch
            - DeleteWatch: User wants to remove/delete a watch
            - QueryStatus: User wants to know the status of watches or check for changes
            - ListWatches: User wants to see their list of watches
            - Help: User is asking for help or information about how to use the system

            User input: "{input}"

            Intent:
            """;

        var response = await _llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            UsageType = LlmUsageType.IntentClassification,
            Temperature = 0.1f,
            MaxTokens = 50
        }, ct);

        if (!response.IsSuccess || string.IsNullOrEmpty(response.Content))
        {
            return IntentType.Unknown;
        }

        var content = response.Content.Trim();
        
        return content.ToLowerInvariant() switch
        {
            var c when c.Contains("createwatch") => IntentType.CreateWatch,
            var c when c.Contains("modifywatch") => IntentType.ModifyWatch,
            var c when c.Contains("deletewatch") => IntentType.DeleteWatch,
            var c when c.Contains("querystatus") => IntentType.QueryStatus,
            var c when c.Contains("listwatches") => IntentType.ListWatches,
            var c when c.Contains("help") => IntentType.Help,
            _ => IntentType.Unknown
        };
    }

    private async Task<ParsedWatchRequest> ExtractEntitiesAsync(string input, IntentType intent, CancellationToken ct)
    {
        var prompt = $$"""
            Extract structured information from the user's request to set up website monitoring.
            
            User input: "{{input}}"
            
            Extract the following fields and respond in JSON format:
            {
                "url": "the URL to monitor (if mentioned)",
                "name": "a friendly name for the watch (if mentioned, or generate a short one based on the URL)",
                "cssSelector": "CSS selector to target specific content (if mentioned)",
                "checkIntervalMinutes": number representing how often to check (convert phrases like 'every hour' to 60, 'every 5 minutes' to 5, default null),
                "useJavaScript": true if user mentions JavaScript, dynamic content, or SPA (default false),
                "tags": ["array", "of", "tags"] if mentioned,
                "notificationEmail": "email address if mentioned",
                "discordWebhook": "discord webhook URL if mentioned",
                "description": "brief description of what to watch for"
            }
            
            Only include fields that can be extracted. Use null for missing fields.
            Respond with ONLY the JSON object, no other text.
            """;

        var response = await _llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            UsageType = LlmUsageType.EntityExtraction,
            Temperature = 0.1f,
            MaxTokens = 500,
            ExpectJson = true
        }, ct);

        if (!response.IsSuccess || string.IsNullOrEmpty(response.Content))
        {
            return new ParsedWatchRequest();
        }

        try
        {
            // Clean up the response - remove markdown code blocks if present
            var json = response.Content.Trim();
            if (json.StartsWith("```"))
            {
                json = json.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")).Aggregate((a, b) => a + "\n" + b);
            }

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<ParsedWatchRequest>(json, options) ?? new ParsedWatchRequest();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse LLM response as JSON: {Content}", response.Content);
            return new ParsedWatchRequest();
        }
    }

    private ValidationResult ValidateRequest(ParsedWatchRequest request, IntentType intent)
    {
        var questions = new List<string>();
        var suggestions = new List<SuggestionChip>();

        if (intent == IntentType.CreateWatch)
        {
            if (string.IsNullOrEmpty(request.Url))
            {
                questions.Add("What website URL would you like to monitor?");
                suggestions.Add(new SuggestionChip { Label = "Example: https://example.com", Value = "https://", Type = SuggestionType.SetValue });
            }
            else if (!Uri.TryCreate(request.Url, UriKind.Absolute, out _))
            {
                questions.Add($"The URL '{request.Url}' doesn't look valid. Can you provide a complete URL?");
            }

            if (request.CheckInterval == null)
            {
                suggestions.Add(new SuggestionChip { Label = "Check every 5 minutes", Value = " every 5 minutes", Type = SuggestionType.AppendText });
                suggestions.Add(new SuggestionChip { Label = "Check every hour", Value = " every hour", Type = SuggestionType.AppendText });
                suggestions.Add(new SuggestionChip { Label = "Check daily", Value = " once a day", Type = SuggestionType.AppendText });
            }
        }

        return new ValidationResult
        {
            NeedsClarification = questions.Count > 0,
            Questions = questions,
            Suggestions = suggestions
        };
    }

    private async Task<LlmProcessResult> ExecuteIntentAsync(IntentType intent, ParsedWatchRequest request, CancellationToken ct)
    {
        switch (intent)
        {
            case IntentType.CreateWatch:
                if (string.IsNullOrEmpty(request.Url) || !Uri.TryCreate(request.Url, UriKind.Absolute, out _))
                {
                    return new LlmProcessResult
                    {
                        IsSuccess = false,
                        Intent = intent,
                        ErrorMessage = "A valid URL is required to create a watch."
                    };
                }

                var createRequest = new CreateWatchRequest
                {
                    Url = request.Url,
                    Name = request.Name,
                    CssSelector = request.CssSelector,
                    CheckInterval = request.CheckInterval,
                    UseJavaScript = request.UseJavaScript ?? false,
                    Tags = request.Tags,
                    Description = request.Description
                };

                if (!string.IsNullOrEmpty(request.NotificationEmail))
                {
                    createRequest.Notifications = new NotificationSettings
                    {
                        EmailEnabled = true,
                        EmailAddress = request.NotificationEmail
                    };
                }

                var watch = await _watchService.CreateWatchAsync(createRequest, ct);

                return new LlmProcessResult
                {
                    IsSuccess = true,
                    Intent = intent,
                    ParsedRequest = request,
                    CreatedWatchId = watch.Id,
                    Summary = $"Created watch '{watch.Name ?? watch.Url}' that will check every {watch.CheckInterval.TotalMinutes} minutes."
                };

            case IntentType.ListWatches:
                var watches = await _watchService.GetAllAsync(ct);
                var count = watches.Count();
                return new LlmProcessResult
                {
                    IsSuccess = true,
                    Intent = intent,
                    Summary = count == 0 
                        ? "You don't have any watches yet. Try saying 'Watch example.com for changes'."
                        : $"You have {count} watch{(count == 1 ? "" : "es")}."
                };

            case IntentType.QueryStatus:
                return new LlmProcessResult
                {
                    IsSuccess = true,
                    Intent = intent,
                    Summary = "Checking status of your watches..."
                };

            default:
                return new LlmProcessResult
                {
                    IsSuccess = false,
                    Intent = intent,
                    ErrorMessage = "I'm not sure how to handle that request. Try asking me to watch a website for changes."
                };
        }
    }

    private static LlmProcessResult CreateHelpResponse()
    {
        return new LlmProcessResult
        {
            IsSuccess = true,
            Intent = IntentType.Help,
            Summary = """
                I can help you monitor websites for changes. Here are some things you can try:
                
                • "Watch https://example.com for changes every hour"
                • "Monitor the price on https://shop.com/product using .price selector"
                • "Check https://news.com every 5 minutes and email me at user@example.com"
                • "List my watches"
                • "What's the status of my watches?"
                """,
            Suggestions =
            [
                new SuggestionChip { Label = "Watch a website", Value = "Watch ", Type = SuggestionType.SetValue },
                new SuggestionChip { Label = "List my watches", Value = "List my watches", Type = SuggestionType.SetValue },
                new SuggestionChip { Label = "Check status", Value = "What's the status?", Type = SuggestionType.SetValue }
            ]
        };
    }

    private static string NormalizeUrl(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://{url}";
        }
        return url;
    }

    [GeneratedRegex(@"^https?://", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^(www\.)?[\w-]+\.(com|org|net|io|co|dev|app|edu|gov|mil|int|eu|uk|de|fr|jp|cn|au|ca|br|in|ru|nl|se|no|fi|dk|pl|es|it|ch|at|be|pt|gr|cz|hu|ro|bg|hr|sk|si|lt|lv|ee)(/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DomainRegex();

    private class ValidationResult
    {
        public bool NeedsClarification { get; set; }
        public List<string> Questions { get; set; } = [];
        public List<SuggestionChip> Suggestions { get; set; } = [];
    }
}
