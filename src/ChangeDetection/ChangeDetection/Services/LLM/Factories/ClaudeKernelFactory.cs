using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Anthropic;

namespace ChangeDetection.Services.LLM.Factories;

/// <summary>
/// Factory for creating Anthropic Claude-configured kernels.
/// </summary>
public class ClaudeKernelFactory : ILlmKernelFactory
{
    public LlmProviderType ProviderType => LlmProviderType.Claude;

    public Kernel CreateKernel(LlmProviderConfig config, HttpClient httpClient)
    {
        var builder = Kernel.CreateBuilder();

#pragma warning disable SKEXP0070
        builder.Services.AddKeyedSingleton<IChatCompletionService>(
            null,
            new AnthropicChatCompletionService(
                modelId: config.Model,
                apiKey: config.ApiKey ?? throw new InvalidOperationException("Claude API key is required"),
                httpClient: httpClient));
#pragma warning restore SKEXP0070

        return builder.Build();
    }
}
