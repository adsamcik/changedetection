using ChangeDetection.Core.Entities;
using Microsoft.SemanticKernel;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Factory for creating Semantic Kernel instances for specific LLM provider types.
/// Implementations handle provider-specific configuration and kernel building.
/// </summary>
public interface ILlmKernelFactory
{
    /// <summary>
    /// Gets the provider type this factory handles.
    /// </summary>
    LlmProviderType ProviderType { get; }

    /// <summary>
    /// Creates a Semantic Kernel configured for the given provider.
    /// </summary>
    /// <param name="config">The provider configuration.</param>
    /// <param name="httpClient">Pre-configured HttpClient with appropriate timeout.</param>
    /// <returns>A configured Kernel instance.</returns>
    Kernel CreateKernel(LlmProviderConfig config, HttpClient httpClient);
}
