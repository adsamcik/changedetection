using System.Text.Json;
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
    /// </summary>
    public async Task<WatchGroup> SeedAsync(
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
            Tags = ["job-search", "biotech", "life-science", "prague", "copenhagen"]
        }, ct);

        var profileRules = filterRuleGenerator.GenerateRules(profileJson);

        var portals = GetAllPortalDefinitions();
        foreach (var portal in portals)
        {
            try
            {
                await CreatePortalWatchAsync(group.Id, portal, userIntent, profileRules, ct);
                logger.LogInformation("Created portal watch: {Name} ({Url})", portal.Name, portal.Url);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to create portal watch: {Name}", portal.Name);
            }
        }

        logger.LogInformation("Job search project seeded with {Count} portal watches", portals.Count);
        return group;
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
            FilterRules = profileRules.Select(CloneRule).ToList()
        }, ct);
    }

    /// <summary>
    /// Returns all portal definitions with their extraction schemas.
    /// </summary>
    public static List<PortalDefinition> GetAllPortalDefinitions()
    {
        var daily = TimeSpan.FromHours(24);
        var twoDays = TimeSpan.FromHours(48);
        var weekly = TimeSpan.FromDays(7);

        var standardFetch = new FetchSettings { TimeoutSeconds = 30 };
        var jsFetch = new FetchSettings
        {
            UseJavaScript = true,
            TimeoutSeconds = 45,
            WaitAfterLoadMs = 3000
        };
        var heavyJsFetch = new FetchSettings
        {
            UseJavaScript = true,
            TimeoutSeconds = 60,
            WaitAfterLoadMs = 5000
        };

        return
        [
            // ===== TIER 1: Primary portals (daily / every-2-days) =====

            new()
            {
                Id = "watch-ucph",
                Name = "University of Copenhagen Vacancies",
                Description = "Lab/research roles in Faculty of Health, BRIC, NNF Center at UCPH",
                Url = "https://employment.ku.dk/all-vacancies/",
                Tier = "tier-1",
                CheckInterval = daily,
                FetchSettings = standardFetch,
                Schema = BuildJobSchema(
                    "table.table tbody tr, .vacancy-list .vacancy-item, article.vacancy",
                    "td:first-child a, .vacancy-title a, h2 a",
                    company: "td:nth-child(2), .department, .faculty",
                    location: "td:nth-child(3), .location",
                    url: "td:first-child a, .vacancy-title a",
                    posted: "td:last-child, .date, time",
                    deadline: ".deadline, .application-deadline"),
                ExtraTags = ["denmark", "academia"]
            },
            new()
            {
                Id = "watch-novo",
                Name = "Novo Nordisk Denmark",
                Description = "Scientist/lab tech roles at Novo Nordisk Denmark sites",
                Url = "https://careers.novonordisk.com/global/en/search-results?keywords=laboratory%20scientist&location=Denmark",
                Tier = "tier-1",
                CheckInterval = daily,
                FetchSettings = heavyJsFetch,
                Schema = BuildJobSchema(
                    ".job-card, .search-result-item, [data-ph-at-id='job-card']",
                    ".job-title a, .job-card-title, [data-ph-at-id='job-title']",
                    location: ".job-location, [data-ph-at-id='job-location']",
                    url: ".job-title a, [data-ph-at-id='job-title'] a",
                    posted: ".job-date, .posted-date"),
                ExtraTags = ["denmark", "pharma", "novo-nordisk"]
            },
            new()
            {
                Id = "watch-novonesis",
                Name = "Novonesis Careers Denmark",
                Description = "R&D/lab roles at Novonesis Denmark",
                Url = "https://www.novonesis.com/en/careers/jobs?location=Denmark",
                Tier = "tier-1",
                CheckInterval = daily,
                FetchSettings = jsFetch,
                Schema = BuildJobSchema(
                    ".job-card, .job-list-item, article.job",
                    ".job-title, h3, h2 a",
                    location: ".job-location, .location",
                    url: "a.job-title, a.job-link, h3 a"),
                ExtraTags = ["denmark", "biotech"]
            },
            new()
            {
                Id = "watch-szu",
                Name = "SZÚ Kariéra (Czech State Health Institute)",
                Description = "Diagnostics/lab roles at Czech State Health Institute",
                Url = "https://szu.gov.cz/statni-zdravotni-ustav-starame-se-o-zdrave-cesko/kariera/",
                Tier = "tier-1",
                CheckInterval = twoDays,
                FetchSettings = standardFetch,
                Schema = BuildJobSchema(
                    "article, .career-item, .job-listing, li.vacancy, .wp-block-list li",
                    "h2 a, h3 a, a.career-link, a",
                    location: ".location, .mesto",
                    url: "h2 a, h3 a, a",
                    posted: ".date, time, .datum",
                    deadline: ".deadline, .uzaverka"),
                ExtraTags = ["czech", "public-sector", "diagnostics"]
            },
            new()
            {
                Id = "watch-img",
                Name = "IMG / CCP Careers (Academy of Sciences)",
                Description = "Positions at Academy of Sciences / Czech Centre for Phenogenomics",
                Url = "https://www.img.cas.cz/kariera/volna-mista/",
                Tier = "tier-1",
                CheckInterval = daily,
                FetchSettings = standardFetch,
                Schema = BuildJobSchema(
                    "article, .position-item, li.vacancy, .career-list li",
                    "h2 a, h3 a, a, .position-title",
                    company: ".department, .oddeleni",
                    url: "h2 a, h3 a, a",
                    posted: ".date, time"),
                ExtraTags = ["czech", "academia", "phenogenomics"]
            },
            new()
            {
                Id = "watch-jobscz",
                Name = "Jobs.cz Lab/Chemistry Prague",
                Description = "Lab/chemist/analyst roles in Prague from Jobs.cz",
                Url = "https://www.jobs.cz/prace/?profession%5B0%5D=201100032&profession%5B1%5D=201100139&locality%5Bcode%5D=3468",
                Tier = "tier-1",
                CheckInterval = daily,
                FetchSettings = jsFetch,
                Schema = BuildJobSchema(
                    ".search-list__item, article.job, .standalone-search-item",
                    "h2 a, .search-list__main-info__title a, .job-title a",
                    company: ".search-list__main-info__company, .company-name",
                    location: ".search-list__main-info__address, .location",
                    url: "h2 a, .search-list__main-info__title a",
                    posted: ".search-list__advert-age, .date"),
                ExtraTags = ["czech", "prague", "job-board"]
            },
            new()
            {
                Id = "watch-lund",
                Name = "Lund University Vacancies",
                Description = "Research/lab positions at Lund University (Medicon Valley)",
                Url = "https://www.lunduniversity.lu.se/vacancies",
                Tier = "tier-1",
                CheckInterval = daily,
                FetchSettings = jsFetch,
                Schema = BuildJobSchema(
                    "table tbody tr, .vacancy-row, .job-list-item",
                    "td a, .title a, h3 a",
                    company: ".department, .faculty, td:nth-child(2)",
                    location: "td:nth-child(3), .location",
                    url: "td a, .title a",
                    posted: "td:nth-child(4), .date",
                    deadline: "td:last-child, .deadline"),
                ExtraTags = ["sweden", "academia", "medicon-valley"]
            },
            new()
            {
                Id = "watch-gcr",
                Name = "Greater Copenhagen Region Careers",
                Description = "Cross-border DK+SE life-science roles for internationals",
                Url = "https://careerportal.greatercphregion.com/",
                Tier = "tier-1",
                CheckInterval = daily,
                FetchSettings = jsFetch,
                Schema = BuildJobSchema(
                    ".job-card, article.job, .listing-item",
                    "h2, h3, .job-title, a.title",
                    company: ".company, .employer",
                    location: ".location, .city",
                    url: "a.job-link, h2 a, h3 a"),
                ExtraTags = ["denmark", "sweden", "cross-border"]
            },
            new()
            {
                Id = "watch-medicon-village",
                Name = "Medicon Village Open Positions",
                Description = "Lab/science positions in Medicon Village cluster (Lund, Sweden)",
                Url = "https://www.mediconvillage.se/open-positions/",
                Tier = "tier-1",
                CheckInterval = daily,
                FetchSettings = standardFetch,
                Schema = BuildJobSchema(
                    "article, .position-item, .job-listing, li.job",
                    "h2 a, h3 a, .title a",
                    company: ".company, .organization",
                    url: "h2 a, h3 a, a"),
                ExtraTags = ["sweden", "medicon-valley", "biotech-cluster"]
            },
            new()
            {
                Id = "watch-lundbeck",
                Name = "Lundbeck Careers Copenhagen",
                Description = "Scientist/lab roles at Lundbeck Copenhagen",
                Url = "https://jobs.lundbeck.com/search/?q=scientist+laboratory&locationsearch=copenhagen",
                Tier = "tier-1",
                CheckInterval = daily,
                FetchSettings = jsFetch,
                Schema = BuildJobSchema(
                    ".job-card, .search-result, tr.job-row, .job-listing",
                    ".job-title a, td.title a, h3 a",
                    location: ".job-location, td.location, .location",
                    url: ".job-title a, td.title a, h3 a",
                    posted: ".date, td.date"),
                ExtraTags = ["denmark", "pharma", "neuroscience"]
            },

            // ===== TIER 2: Supplementary portals (weekly) =====

            new()
            {
                Id = "watch-bavarian",
                Name = "Bavarian Nordic Careers (Workday)",
                Description = "Lab/QC roles at vaccine company near Copenhagen",
                Url = "https://bavariannordic.wd103.myworkdayjobs.com/en-US/BavarianNordic",
                Tier = "tier-2",
                CheckInterval = weekly,
                FetchSettings = heavyJsFetch,
                Schema = BuildJobSchema(
                    "[data-automation-id='jobItem'], .job-card, li.css-1q2dra3",
                    "a[data-automation-id='jobTitle'], .job-title a",
                    location: "[data-automation-id='locations'], .location",
                    url: "a[data-automation-id='jobTitle']",
                    posted: "[data-automation-id='postedOn'], .date"),
                ExtraTags = ["denmark", "vaccines", "workday"]
            },
            new()
            {
                Id = "watch-genmab",
                Name = "Genmab Careers Copenhagen",
                Description = "Lab roles at antibody biotech (watch for non-senior roles)",
                Url = "https://careers.genmab.com/search/?searchby=location&d=10&lat=55.6761&lon=12.5683",
                Tier = "tier-2",
                CheckInterval = weekly,
                FetchSettings = jsFetch,
                Schema = BuildJobSchema(
                    ".job-card, .search-result-item, tr.job-row",
                    ".job-title a, td a, h3 a",
                    location: ".job-location, .location",
                    url: ".job-title a, td a, h3 a"),
                ExtraTags = ["denmark", "antibody", "biotech"]
            },
            new()
            {
                Id = "watch-ferring",
                Name = "Ferring Pharmaceuticals Careers",
                Description = "Lab roles at Ferring Copenhagen",
                Url = "https://careers.ferring.com/search/?q=scientist&locationsearch=copenhagen",
                Tier = "tier-2",
                CheckInterval = weekly,
                FetchSettings = jsFetch,
                Schema = BuildJobSchema(
                    ".job-card, .search-result, tr.job-row",
                    ".job-title a, td a, h3 a",
                    location: ".job-location, .location",
                    url: ".job-title a, td a, h3 a"),
                ExtraTags = ["denmark", "pharma"]
            },
            new()
            {
                Id = "watch-alk",
                Name = "ALK-Abelló Careers (Hørsholm)",
                Description = "Lab/immunology roles at ALK",
                Url = "https://alkabello.easycruit.com/index.html",
                Tier = "tier-2",
                CheckInterval = weekly,
                FetchSettings = jsFetch,
                Schema = BuildJobSchema(
                    ".vacancy-item, .job-listing, tr.vacancy, li.position",
                    "a.vacancy-title, .title a, td a",
                    location: ".location, .region",
                    url: "a.vacancy-title, .title a",
                    deadline: ".deadline"),
                ExtraTags = ["denmark", "allergy", "immunotherapy"]
            },
            new()
            {
                Id = "watch-ssi",
                Name = "Statens Serum Institut Jobs",
                Description = "Virology/diagnostics lab roles at Danish SSI",
                Url = "https://www.ssi.dk/om-ssi/job-i-ssi",
                Tier = "tier-2",
                CheckInterval = weekly,
                FetchSettings = standardFetch,
                Schema = BuildJobSchema(
                    "article, .job-item, li.vacancy, .content-list li",
                    "h2 a, h3 a, a.job-link",
                    company: ".department",
                    url: "h2 a, h3 a, a",
                    posted: ".date, time",
                    deadline: ".deadline, .ansogningsfrist"),
                ExtraTags = ["denmark", "public-sector", "virology"]
            },
            new()
            {
                Id = "watch-eures",
                Name = "EURES Life Science Jobs",
                Description = "EU cross-border life-science positions via EURES",
                Url = "https://eures.europa.eu/eures-services/eures-targeted-mobility-scheme_en",
                Tier = "tier-2",
                CheckInterval = weekly,
                FetchSettings = jsFetch,
                Schema = BuildJobSchema(
                    ".job-card, article.job, .listing-item, .search-result",
                    "h2 a, h3 a, .job-title",
                    company: ".company, .employer",
                    location: ".location, .country",
                    url: "a.job-link, h2 a"),
                ExtraTags = ["eu", "cross-border", "mobility"]
            },
            new()
            {
                Id = "watch-jobindex",
                Name = "Jobindex Life Science Denmark",
                Description = "Broad Danish lab/science job board",
                Url = "https://www.jobindex.dk/jobsoegning?q=laboratory+scientist&area=2",
                Tier = "tier-2",
                CheckInterval = weekly,
                FetchSettings = jsFetch,
                Schema = BuildJobSchema(
                    ".jobsearch-result, .PaidJob, article.job, div.jix_robotjob",
                    "h4 a, .jix_robotjob--link, .job-title a",
                    company: ".jix_robotjob--company, .company a",
                    location: ".jix_robotjob--area, .location",
                    url: "h4 a, .jix_robotjob--link a",
                    posted: ".jix_robotjob--published-date, .date",
                    deadline: ".deadline, .ansogningsfrist"),
                ExtraTags = ["denmark", "job-board"]
            }
        ];
    }

    /// <summary>
    /// Build a job listing ExtractionSchema with fallback CSS selectors.
    /// </summary>
    private static ExtractionSchema BuildJobSchema(
        string itemSelector,
        string titleSelector,
        string? company = null,
        string? location = null,
        string? url = null,
        string? posted = null,
        string? deadline = null)
    {
        var fields = new List<SchemaField>
        {
            new() { Name = "title", Type = FieldType.String, Selector = titleSelector, IsRequired = true, IsIdentityField = true }
        };

        if (url is not null)
            fields.Add(new() { Name = "url", Type = FieldType.Url, Selector = url, IsRequired = true });

        if (company is not null)
            fields.Add(new() { Name = "company", Type = FieldType.String, Selector = company, IsIdentityField = true });

        if (location is not null)
            fields.Add(new() { Name = "location", Type = FieldType.String, Selector = location });

        if (posted is not null)
            fields.Add(new() { Name = "posted_date", Type = FieldType.Date, Selector = posted });

        if (deadline is not null)
            fields.Add(new() { Name = "deadline", Type = FieldType.Date, Selector = deadline, TrackHistory = false });

        var identityFields = new List<string> { "title" };
        if (company is not null) identityFields.Add("company");

        return new ExtractionSchema
        {
            ItemSelector = itemSelector,
            Fields = fields,
            IdentityFieldNames = identityFields,
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
