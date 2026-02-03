using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.SemanticKernel;

namespace ChangeDetection.Services.LLM.Factories;

/// <summary>
/// Factory for creating Azure OpenAI-configured kernels.
/// </summary>
public class AzureOpenAIKernelFactory : ILlmKernelFactory
{
    public LlmProviderType ProviderType => LlmProviderType.AzureOpenAI;

    public Kernel CreateKernel(LlmProviderConfig config, HttpClient httpClient)
    {
        var builder = Kernel.CreateBuilder();

        builder.AddAzureOpenAIChatCompletion(
            deploymentName: config.Model,
            endpoint: config.Endpoint ?? throw new InvalidOperationException("Azure OpenAI endpoint is required"),
            apiKey: config.ApiKey ?? throw new InvalidOperationException("Azure OpenAI API key is required"),
            httpClient: httpClient);

        return builder.Build();
    }
}
