using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.LLM.Copilot;
using GitHub.Copilot.SDK;
using Microsoft.SemanticKernel;

namespace ChangeDetection.Services.LLM.Factories;

/// <summary>
/// Factory for creating Copilot SDK-configured kernels.
/// Manages CopilotClient lifecycle and creates adapter-based chat completion services.
/// </summary>
public class CopilotKernelFactory : ILlmKernelFactory, IAsyncDisposable
{
    private readonly ILogger<CopilotKernelFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly object _clientGate = new();
    private CopilotClient? _client;
    private bool _disposed;

    public LlmProviderType ProviderType => LlmProviderType.Copilot;

    public CopilotKernelFactory(ILogger<CopilotKernelFactory> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Gets or creates the shared CopilotClient instance.
    /// The client manages the Copilot CLI process lifecycle.
    /// </summary>
    private CopilotClient GetOrCreateClient()
    {
        if (_client is not null)
            return _client;

        lock (_clientGate)
        {
            if (_client is not null)
                return _client;

            var options = new CopilotClientOptions
            {
                AutoStart = true,
                AutoRestart = true,
                UseLoggedInUser = true,
                Logger = _loggerFactory.CreateLogger<CopilotClient>()
            };

            _client = new CopilotClient(options);
            _logger.LogInformation("Created CopilotClient with auto-start enabled");

            return _client;
        }
    }

    public Kernel CreateKernel(LlmProviderConfig config, HttpClient httpClient)
    {
        var client = GetOrCreateClient();

        // Determine model - use config model or default to gpt-5 (available via Copilot CLI)
        var model = !string.IsNullOrEmpty(config.Model) ? config.Model : "gpt-5";

        // Create the chat completion service adapter, passing the provider's timeout
        var chatService = new CopilotChatCompletionService(
            client,
            model,
            _loggerFactory.CreateLogger<CopilotChatCompletionService>(),
            timeoutSeconds: config.TimeoutSeconds);

        // Build kernel with our adapter service
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>(chatService);

        _logger.LogDebug("Created Copilot kernel with model {Model}", model);

        return builder.Build();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_client is not null)
        {
            try
            {
                await _client.StopAsync();
                await _client.DisposeAsync();
                _logger.LogInformation("Disposed CopilotClient");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing CopilotClient");
            }
        }
        GC.SuppressFinalize(this);
    }
}
