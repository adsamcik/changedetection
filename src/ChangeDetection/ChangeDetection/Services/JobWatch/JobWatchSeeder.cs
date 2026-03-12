using System.Text.Json;
using System.Text.Json.Serialization;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.JobWatch;

/// <summary>
/// Seeds the biotech job search watch group with all portal configurations.
/// Creates a WatchGroup with candidate profile, then individual watches per portal
/// with appropriate ExtractionSchemas, FetchSettings, and FilterRules.
/// </summary>
public class JobWatchSeeder(
    IWatchGroupService groupService,
    IWatchService watchService,
    IProfileFilterRuleGenerator filterRuleGenerator,
    ILogger<JobWatchSeeder> logger)
{
    /// <summary>
    /// Create the full job watch project: group + profile + watches + filter rules.
    /// Returns the group and the actual number of watches created.
    /// </summary>
    public async Task<(WatchGroup Group, int CreatedCount)> SeedAsync(
        string profileJson,
        string userIntent,
        CancellationToken ct = default)
    {
        logger.LogInformation("Seeding biotech job search project");

        var group = await groupService.CreateGroupAsync(new WatchGroupCreateRequest
        {
            Name = "Biotech Job Search — Prague + Copenhagen",
            Description = "Automated monitoring of biotech/life-science job portals across Prague and the Copenhagen/Medicon Valley area",
            Icon = "🔬",
            UserIntent = userIntent,
            AnalysisProfileJson = profileJson,
            TemplateId = "job-watch-biotech",
            TemplateVersion = 1,
            Tags = ["job-search", "biotech", "life-science", "prague", "copenhagen"]
        }, ct);

        var profileRules = filterRuleGenerator.GenerateRules(profileJson);

        var portals = GetAllPortalDefinitions();
        var createdCount = 0;
        foreach (var portal in portals)
        {
            try
            {
                await CreatePortalWatchAsync(group.Id, portal, userIntent, profileRules, ct);
                createdCount++;
                logger.LogInformation("Created portal watch: {Name} ({Url})", portal.Name, portal.Url);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to create portal watch: {Name}", portal.Name);
            }
        }

        // Atomicity: if no watches were created, roll back the group
        if (createdCount == 0)
        {
            logger.LogWarning("No portal watches were created — rolling back group {GroupId}", group.Id);
            try { await groupService.DeleteGroupAsync(group.Id, false, ct); }
            catch (Exception ex) { logger.LogError(ex, "Failed to roll back empty group {GroupId}", group.Id); }
            throw new InvalidOperationException("Failed to create any portal watches. Group has been rolled back.");
        }

        logger.LogInformation("Job search project seeded: {Created}/{Total} portal watches", createdCount, portals.Count);
        return (group, createdCount);
    }

    private async Task CreatePortalWatchAsync(
        Guid groupId,
        PortalDefinition portal,
        string userIntent,
        List<FilterRule> profileRules,
        CancellationToken ct)
    {
        await watchService.CreateWatchAsync(new CreateWatchRequest
        {
            Url = portal.Url,
            Name = portal.Name,
            Description = portal.Description,
            UserIntent = userIntent,
            GroupId = groupId,
            Tags = ["job-search", "biotech", portal.Tier, ..portal.ExtraTags],
            CheckInterval = portal.CheckInterval,
            ScheduleSettings = new CheckScheduleSettings
            {
                Mode = CheckScheduleMode.Fixed,
                BaseInterval = portal.CheckInterval
            },
            FetchSettings = portal.FetchSettings,
            SchemaEnabled = true,
            Schema = portal.Schema,
            FilterRules = profileRules.Select(CloneRule).ToList(),
            SkipInitialCheck = true // Avoid blocking on 17 fetches during seed
        }, ct);
    }

    /// <summary>
    /// Returns all portal definitions loaded from the JSON template file.
    /// </summary>
    public static List<PortalDefinition> GetAllPortalDefinitions()
    {
        try
        {
            var templatePath = Path.Combine(AppContext.BaseDirectory, "Templates", "job-watch-portals.json");
            if (!File.Exists(templatePath))
                return [];

            var json = File.ReadAllText(templatePath);
            var template = JsonSerializer.Deserialize<PortalTemplate>(json, PortalTemplateJsonContext.Default.PortalTemplate);
            if (template?.Portals is null)
                return [];

            return template.Portals.Select(p => new PortalDefinition
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                Url = p.Url,
                Tier = p.Tier,
                CheckInterval = TimeSpan.FromHours(p.CheckIntervalHours),
                ExtraTags = p.ExtraTags ?? [],
                FetchSettings = new FetchSettings
                {
                    UseJavaScript = p.FetchSettings?.UseJavaScript ?? false,
                    TimeoutSeconds = p.FetchSettings?.TimeoutSeconds ?? 30,
                    WaitAfterLoadMs = p.FetchSettings?.WaitAfterLoadMs ?? 0
                },
                Schema = BuildSchemaFromTemplate(p.Schema)
            }).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static ExtractionSchema BuildSchemaFromTemplate(PortalSchemaDto? schema)
    {
        if (schema is null)
            return new ExtractionSchema { ItemSelector = "" };

        var fields = (schema.Fields ?? []).Select(f => new SchemaField
        {
            Name = f.Name,
            Selector = f.Selector,
            Type = Enum.TryParse<FieldType>(f.Type, ignoreCase: true, out var ft) ? ft : FieldType.String,
            IsRequired = f.IsRequired ?? false,
            IsIdentityField = f.IsIdentityField ?? false,
            TrackHistory = f.TrackHistory ?? true
        }).ToList();

        return new ExtractionSchema
        {
            ItemSelector = schema.ItemSelector ?? "",
            Fields = fields,
            IdentityFieldNames = schema.IdentityFields ?? [],
            DiffSettings = new ObjectDiffSettings
            {
                Granularity = DiffGranularity.Both,
                EnableImportanceScoring = true
            }
        };
    }

    private static FilterRule CloneRule(FilterRule source) => new()
    {
        Name = source.Name,
        Description = source.Description,
        Priority = source.Priority,
        StopProcessing = source.StopProcessing,
        Logic = source.Logic,
        IsEnabled = source.IsEnabled,
        Conditions = source.Conditions.Select(c => new FilterCondition
        {
            FieldName = c.FieldName, Operator = c.Operator, Value = c.Value, Negate = c.Negate
        }).ToList(),
        Actions = source.Actions.Select(a => new FilterAction
        {
            Type = a.Type, Parameters = new Dictionary<string, string>(a.Parameters)
        }).ToList()
    };
}

/// <summary>
/// Definition for a single job portal watch configuration.
/// </summary>
public class PortalDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Url { get; init; }
    public required string Tier { get; init; }
    public required TimeSpan CheckInterval { get; init; }
    public required FetchSettings FetchSettings { get; init; }
    public required ExtractionSchema Schema { get; init; }
    public List<string> ExtraTags { get; init; } = [];
}

// JSON template DTOs for deserialization from job-watch-portals.json

public class PortalTemplate
{
    [JsonPropertyName("templateId")]
    public string? TemplateId { get; set; }

    [JsonPropertyName("templateVersion")]
    public int TemplateVersion { get; set; }

    [JsonPropertyName("portals")]
    public List<PortalDto>? Portals { get; set; }
}

public class PortalDto
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public required string Description { get; set; }

    [JsonPropertyName("url")]
    public required string Url { get; set; }

    [JsonPropertyName("tier")]
    public required string Tier { get; set; }

    [JsonPropertyName("checkIntervalHours")]
    public double CheckIntervalHours { get; set; }

    [JsonPropertyName("extraTags")]
    public List<string>? ExtraTags { get; set; }

    [JsonPropertyName("fetchSettings")]
    public FetchSettingsDto? FetchSettings { get; set; }

    [JsonPropertyName("schema")]
    public PortalSchemaDto? Schema { get; set; }
}

public class FetchSettingsDto
{
    [JsonPropertyName("useJavaScript")]
    public bool UseJavaScript { get; set; }

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 30;

    [JsonPropertyName("waitAfterLoadMs")]
    public int? WaitAfterLoadMs { get; set; }
}

public class PortalSchemaDto
{
    [JsonPropertyName("itemSelector")]
    public string? ItemSelector { get; set; }

    [JsonPropertyName("fields")]
    public List<SchemaFieldDto>? Fields { get; set; }

    [JsonPropertyName("identityFields")]
    public List<string>? IdentityFields { get; set; }
}

public class SchemaFieldDto
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("selector")]
    public required string Selector { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "String";

    [JsonPropertyName("isRequired")]
    public bool? IsRequired { get; set; }

    [JsonPropertyName("isIdentityField")]
    public bool? IsIdentityField { get; set; }

    [JsonPropertyName("trackHistory")]
    public bool? TrackHistory { get; set; }
}

[JsonSerializable(typeof(PortalTemplate))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class PortalTemplateJsonContext : JsonSerializerContext;
