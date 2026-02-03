using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.SemanticKernel;

namespace ChangeDetection.Services.LLM.Factories;

/// <summary>
/// Factory for creating OpenAI-configured kernels.
/// </summary>
public class OpenAIKernelFactory : ILlmKernelFactory
{
    public LlmProviderType ProviderType => LlmProviderType.OpenAI;

    public Kernel CreateKernel(LlmProviderConfig config, HttpClient httpClient)
    {
        var builder = Kernel.CreateBuilder();

        builder.AddOpenAIChatCompletion(
            modelId: config.Model,
            apiKey: config.ApiKey ?? throw new InvalidOperationException("OpenAI API key is required"),
            httpClient: httpClient);

        return builder.Build();
    }
}
