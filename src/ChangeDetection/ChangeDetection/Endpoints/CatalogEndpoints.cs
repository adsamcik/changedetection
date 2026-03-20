using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Pipeline;

namespace ChangeDetection.Endpoints;

/// <summary>
/// API endpoints for exporting and importing verified portal catalogs.
/// Enables community sharing of known-good career portal configurations.
/// </summary>
public static class CatalogEndpoints
{
    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions CamelCaseJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/export", ExportCatalog)
            .WithName("ExportCatalog")
            .WithDescription("Export all verified watches as a portable JSON catalog for sharing")
            .Produces<CatalogExport>(contentType: "application/json");

        group.MapPost("/import", ImportCatalog)
            .WithName("ImportCatalog")
            .WithDescription("Import a catalog JSON file and merge entries into the local catalog")
            .Produces<CatalogImportResult>();

        return group;
    }

    private static async Task<IResult> ExportCatalog(
        IWatchService watchService,
        CancellationToken ct)
    {
        var allWatches = await watchService.GetAllAsync(ct);

        var verifiedWatches = allWatches
            .Where(w => w.CatalogStatus == CatalogVerificationStatus.Verified)
            .ToList();

        var export = new CatalogExport
        {
            Version = 1,
            ExportedAt = DateTime.UtcNow,
            Portals = verifiedWatches.Select(w => new CatalogPortalEntry
            {
                Url = w.Url,
                Name = w.Name,
                Platform = SetupFlowEnhancements.DetectPlatformFromUrl(w.Url),
                LocationKeywords = ExtractLocationKeywords(w),
                Tags = w.Tags ?? [],
                TotalSuccessfulChecks = w.TotalSuccessfulChecks,
                AverageItemCount = w.TotalSuccessfulChecks > 0
                    ? w.TotalItemsExtracted / w.TotalSuccessfulChecks
                    : 0,
                LastVerifiedAt = w.LastSuccessfulCheckAt
            }).ToList()
        };

        var json = JsonSerializer.Serialize(export, IndentedJson);

        return Results.File(
            System.Text.Encoding.UTF8.GetBytes(json),
            contentType: "application/json",
            fileDownloadName: "changedetection-catalog.json");
    }

    private static async Task<IResult> ImportCatalog(
        HttpRequest request,
        IWatchService watchService,
        CancellationToken ct)
    {
        CatalogExport? catalog;
        try
        {
            catalog = await JsonSerializer.DeserializeAsync<CatalogExport>(
                request.Body, CamelCaseJson, ct);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new CatalogImportResult
            {
                Errors = 1,
                ErrorDetails = ["Invalid JSON format"]
            });
        }

        if (catalog is null || catalog.Portals is null || catalog.Portals.Count == 0)
        {
            return Results.BadRequest(new CatalogImportResult
            {
                ErrorDetails = ["Catalog is empty or missing 'portals' array"]
            });
        }

        var allWatches = await watchService.GetAllAsync(ct);
        var existingDomains = allWatches
            .Select(w => NormalizeDomain(w.Url))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int imported = 0, skipped = 0, errors = 0;
        var errorDetails = new List<string>();

        foreach (var portal in catalog.Portals)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(portal.Url))
                {
                    errors++;
                    errorDetails.Add("Skipped entry with empty URL");
                    continue;
                }

                var domain = NormalizeDomain(portal.Url);
                if (existingDomains.Contains(domain))
                {
                    skipped++;
                    continue;
                }

                var createRequest = new CreateWatchRequest
                {
                    Url = portal.Url,
                    Name = portal.Name,
                    Tags = portal.Tags?.Count > 0 ? portal.Tags : null,
                    SkipInitialCheck = true
                };

                await watchService.CreateWatchAsync(createRequest, ct);

                // Track the domain so subsequent entries with the same domain are deduped
                existingDomains.Add(domain);
                imported++;
            }
            catch (Exception ex)
            {
                errors++;
                errorDetails.Add($"Failed to import '{portal.Url}': {ex.Message}");
            }
        }

        return Results.Ok(new CatalogImportResult
        {
            Imported = imported,
            Skipped = skipped,
            Errors = errors,
            ErrorDetails = errorDetails.Count > 0 ? errorDetails : null
        });
    }

    /// <summary>
    /// Extracts location-related keywords from a watch's tags.
    /// </summary>
    private static List<string> ExtractLocationKeywords(WatchedSite watch)
    {
        if (watch.Tags is null || watch.Tags.Count == 0)
            return [];

        // Common location-related tag patterns
        var locationIndicators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "copenhagen", "denmark", "stockholm", "sweden", "oslo", "norway",
            "helsinki", "finland", "amsterdam", "netherlands", "brussels", "belgium",
            "berlin", "munich", "germany", "vienna", "austria", "zurich", "switzerland",
            "prague", "czech", "paris", "france", "london", "uk",
            "europe", "nordic", "scandinavia", "dach"
        };

        return watch.Tags
            .Where(t => locationIndicators.Contains(t))
            .ToList();
    }

    /// <summary>
    /// Normalizes a URL to its domain for deduplication.
    /// Mirrors the logic in GroupWatchDiscoveryService.NormalizeDomain.
    /// </summary>
    private static string NormalizeDomain(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url.Trim().ToLowerInvariant();

        var host = uri.Host.Trim().ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }
}

public class CatalogImportResult
{
    public int Imported { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public List<string>? ErrorDetails { get; set; }
}
