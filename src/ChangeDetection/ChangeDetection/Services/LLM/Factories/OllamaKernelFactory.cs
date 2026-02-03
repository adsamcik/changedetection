using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.SemanticKernel;

namespace ChangeDetection.Services.LLM.Factories;

/// <summary>
/// Factory for creating Ollama-configured kernels.
/// Uses OpenAI connector with custom endpoint for Ollama compatibility.
/// </summary>
public class OllamaKernelFactory : ILlmKernelFactory
{
    public LlmProviderType ProviderType => LlmProviderType.Ollama;

    public Kernel CreateKernel(LlmProviderConfig config, HttpClient httpClient)
    {
        var endpoint = config.Endpoint ?? "http://localhost:11434";
        var builder = Kernel.CreateBuilder();

#pragma warning disable SKEXP0010
        builder.AddOpenAIChatCompletion(
            modelId: config.Model,
            apiKey: "ollama", // Ollama doesn't require API key but SK requires non-null
            endpoint: new Uri($"{endpoint}/v1"),
            httpClient: httpClient);
#pragma warning restore SKEXP0010

        return builder.Build();
    }
}
