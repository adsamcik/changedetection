using System.Linq.Expressions;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Security;

namespace ChangeDetection.Services.Persistence;

/// <summary>
/// Keeps LLM provider API keys encrypted in LiteDB while exposing plaintext only
/// to the runtime services that need to call a provider.
/// </summary>
public sealed class EncryptedLlmProviderRepository(
    LiteDbRepository<LlmProviderConfig> inner,
    IApiKeyProtector apiKeyProtector,
    ILogger<EncryptedLlmProviderRepository> logger) : IRepository<LlmProviderConfig>
{
    public async Task<LlmProviderConfig?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var stored = await inner.GetByIdAsync(id, ct);
        return stored is null ? null : await MaterializeAsync(stored, ct);
    }

    public async Task<IEnumerable<LlmProviderConfig>> GetAllAsync(CancellationToken ct = default)
    {
        var stored = await inner.GetAllAsync(ct);
        var providers = new List<LlmProviderConfig>();

        foreach (var provider in stored)
        {
            providers.Add(await MaterializeAsync(provider, ct));
        }

        return providers;
    }

    public async Task<IEnumerable<LlmProviderConfig>> FindAsync(
        Expression<Func<LlmProviderConfig, bool>> predicate,
        CancellationToken ct = default)
    {
        var compiled = predicate.Compile();
        return (await GetAllAsync(ct)).Where(compiled).ToList();
    }

    public async Task<LlmProviderConfig?> FirstOrDefaultAsync(
        Expression<Func<LlmProviderConfig, bool>> predicate,
        CancellationToken ct = default)
    {
        var compiled = predicate.Compile();
        return (await GetAllAsync(ct)).FirstOrDefault(compiled);
    }

    public async Task<LlmProviderConfig?> FirstOrDefaultOrderedDescAsync<TKey>(
        Expression<Func<LlmProviderConfig, bool>> predicate,
        Expression<Func<LlmProviderConfig, TKey>> orderByDesc,
        CancellationToken ct = default)
    {
        var compiledPredicate = predicate.Compile();
        var compiledOrder = orderByDesc.Compile();
        return (await GetAllAsync(ct))
            .Where(compiledPredicate)
            .OrderByDescending(compiledOrder)
            .FirstOrDefault();
    }

    public async Task<bool> ExistsAsync(
        Expression<Func<LlmProviderConfig, bool>> predicate,
        CancellationToken ct = default)
    {
        var compiled = predicate.Compile();
        return (await GetAllAsync(ct)).Any(compiled);
    }

    public async Task<int> CountAsync(
        Expression<Func<LlmProviderConfig, bool>>? predicate = null,
        CancellationToken ct = default)
    {
        var providers = await GetAllAsync(ct);
        return predicate is null ? providers.Count() : providers.Count(predicate.Compile());
    }

    public Task InsertAsync(LlmProviderConfig entity, CancellationToken ct = default) =>
        inner.InsertAsync(PrepareForStorage(entity), ct);

    public Task InsertManyAsync(
        IEnumerable<LlmProviderConfig> entities,
        CancellationToken ct = default) =>
        inner.InsertManyAsync(entities.Select(PrepareForStorage).ToList(), ct);

    public Task UpdateAsync(LlmProviderConfig entity, CancellationToken ct = default) =>
        inner.UpdateAsync(PrepareForStorage(entity), ct);

    public Task DeleteAsync(Guid id, CancellationToken ct = default) =>
        inner.DeleteAsync(id, ct);

    public async Task DeleteManyAsync(
        Expression<Func<LlmProviderConfig, bool>> predicate,
        CancellationToken ct = default)
    {
        var compiled = predicate.Compile();
        var ids = (await GetAllAsync(ct)).Where(compiled).Select(p => p.Id).ToList();

        foreach (var id in ids)
        {
            await inner.DeleteAsync(id, ct);
        }
    }

    private async Task<LlmProviderConfig> MaterializeAsync(
        LlmProviderConfig stored,
        CancellationToken ct)
    {
        var result = Clone(stored);
        if (string.IsNullOrEmpty(stored.ApiKey))
        {
            return result;
        }

        if (apiKeyProtector.IsProtected(stored.ApiKey))
        {
            result.ApiKey = apiKeyProtector.Unprotect(stored.ApiKey);
            return result;
        }

        // Transparently upgrade keys written by older releases. The caller still
        // receives the original plaintext value, but LiteDB is repaired immediately.
        await inner.UpdateAsync(PrepareForStorage(stored), ct);
        logger.LogInformation(
            "Encrypted legacy plaintext API key for LLM provider {ProviderId}",
            stored.Id);
        return result;
    }

    private LlmProviderConfig PrepareForStorage(LlmProviderConfig provider)
    {
        var stored = Clone(provider);
        if (!string.IsNullOrEmpty(stored.ApiKey))
        {
            stored.ApiKey = apiKeyProtector.IsProtected(stored.ApiKey)
                ? stored.ApiKey
                : apiKeyProtector.Protect(stored.ApiKey);
        }

        return stored;
    }

    private static LlmProviderConfig Clone(LlmProviderConfig provider) => new()
    {
        Id = provider.Id,
        Name = provider.Name,
        ProviderType = provider.ProviderType,
        Priority = provider.Priority,
        IsEnabled = provider.IsEnabled,
        CostPer1KInputTokens = provider.CostPer1KInputTokens,
        CostPer1KOutputTokens = provider.CostPer1KOutputTokens,
        MaxRetries = provider.MaxRetries,
        TimeoutSeconds = provider.TimeoutSeconds,
        Endpoint = provider.Endpoint,
        Model = provider.Model,
        ApiKey = provider.ApiKey,
        IsHealthy = provider.IsHealthy,
        LastError = provider.LastError,
        LastErrorAt = provider.LastErrorAt,
        TotalTokensUsed = provider.TotalTokensUsed,
        TotalCost = provider.TotalCost,
        CreatedAt = provider.CreatedAt,
        UpdatedAt = provider.UpdatedAt
    };
}
