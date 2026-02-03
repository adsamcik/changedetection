using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.SemanticKernel;

namespace ChangeDetection.Services.LLM.Factories;

/// <summary>
/// Factory for creating Google Gemini-configured kernels.
/// </summary>
public class GeminiKernelFactory : ILlmKernelFactory
{
    public LlmProviderType ProviderType => LlmProviderType.Gemini;

    public Kernel CreateKernel(LlmProviderConfig config, HttpClient httpClient)
    {
        var builder = Kernel.CreateBuilder();

#pragma warning disable SKEXP0070
        builder.AddGoogleAIGeminiChatCompletion(
            modelId: config.Model,
            apiKey: config.ApiKey ?? throw new InvalidOperationException("Gemini API key is required"),
            httpClient: httpClient);
#pragma warning restore SKEXP0070

        return builder.Build();
    }
}
