using ChangeDetection.Client.Pages;
using ChangeDetection.Client;
using ChangeDetection.Components;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Endpoints;
using ChangeDetection.Hubs;
using ChangeDetection.Services;
using ChangeDetection.Services.Authentication;
using ChangeDetection.Services.Background;
using ChangeDetection.Services.BlockExecution;
using ChangeDetection.Services.Content;
using ChangeDetection.Services.GroupWatch;
using ChangeDetection.Services.LLM;
using ChangeDetection.Services.LLM.Factories;
using ChangeDetection.Services.Logging;
using ChangeDetection.Services.Notifications;
using ChangeDetection.Services.Persistence;
using ChangeDetection.Services.Pipeline;
using ChangeDetection.Services.Scraping;
using ChangeDetection.Services.SetupPipeline;
using ChangeDetection.Services.JobWatch;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Core.Pipeline.Validation;
using ChangeDetection.Services.AutoHealing;
using ChangeDetection.Services.Search;
using ChangeDetection.Services.Startup;
using Microsoft.Extensions.Options;
using ChangeDetection.Core.Pipeline.AutoHealing;
using ChangeDetection.Services.Persistence.Migrations;
using Microsoft.AspNetCore.ResponseCompression;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);

// Configure OpenTelemetry logging with file persistence in Development mode
if (builder.Environment.IsDevelopment())
{
    var logsPath = Path.Combine(builder.Environment.ContentRootPath, "logs");
    Directory.CreateDirectory(logsPath);
    
    // Create a file logging provider for OpenTelemetry
    var logFilePath = Path.Combine(logsPath, $"log-{DateTime.Now:yyyy-MM-dd}.txt");
    var fileLogProcessor = new FileLogProcessor(logFilePath);
    
    builder.Logging.ClearProviders();
    builder.Logging.AddOpenTelemetry(options =>
    {
        options.SetResourceBuilder(
            ResourceBuilder.CreateDefault()
                .AddService("ChangeDetection"));
        options.IncludeFormattedMessage = true;
        options.IncludeScopes = true;
        options.AddConsoleExporter();
        options.AddProcessor(fileLogProcessor);
    });
    
    builder.Logging.SetMinimumLevel(LogLevel.Debug);
    builder.Logging.AddFilter("Microsoft", LogLevel.Information);
    builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.AspNetCore.SignalR", LogLevel.Debug);
}

// Add response compression for improved performance
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
    [
        "application/json",
        "application/javascript",
        "text/css",
        "text/html",
        "text/plain",
        "application/wasm"
    ]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
{
    options.Level = System.IO.Compression.CompressionLevel.Fastest;
});
builder.Services.Configure<GzipCompressionProviderOptions>(options =>
{
    options.Level = System.IO.Compression.CompressionLevel.Fastest;
});

// Add output caching for API responses
builder.Services.AddOutputCache(options =>
{
    // Default policy with short duration for API responses
    options.AddBasePolicy(builder => builder.Expire(TimeSpan.FromSeconds(10)));
    
    // Longer cache for static content-like API responses
    options.AddPolicy("LongCache", builder => builder.Expire(TimeSpan.FromMinutes(5)));
    
    // No cache policy for mutation endpoints
    options.AddPolicy("NoCache", builder => builder.NoCache());
});

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// Add cascading authentication state for Blazor
builder.Services.AddCascadingAuthenticationState();

// Add SignalR with optimized settings and JSON protocol configuration
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.MaximumReceiveMessageSize = 64 * 1024; // 64KB - sufficient for most updates
    options.StreamBufferCapacity = 20;
    // LLM pipeline stages can take up to 3-5 minutes per stage.
    // Keep-alive must be frequent enough, and client timeout generous enough,
    // to survive long-running operations without disconnecting.
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromMinutes(5);
})
.AddJsonProtocol(options =>
{
    // Use string enum converter to avoid KeyNotFoundException when deserializing enums
    // This ensures consistent serialization between server and client
    options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

// Configure HTTP JSON options to match SignalR settings
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

// Add HttpClient factory
builder.Services.AddHttpClient();

// Named HttpClient for SearXNG search provider
builder.Services.AddHttpClient("SearXNG");

// Named HttpClient for Google Custom Search API
builder.Services.AddHttpClient("GoogleCSE");

// Named HttpClient for Brave Search API
builder.Services.AddHttpClient("BraveSearch");
builder.Services.AddHttpClient("NewsData");
builder.Services.AddHttpClient("LlmSearchValidation")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 3
    });
builder.Services.AddHttpClient("LightweightFetch")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.GZip
            | System.Net.DecompressionMethods.Deflate
            | System.Net.DecompressionMethods.Brotli
    });
builder.Services.AddHttpClient("RuntimeSandbox")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None
    });
builder.Services.AddHttpClient("HttpRequestBlock")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None
    });

// Add named HttpClient for Blazor prerendering with dynamic base address
builder.Services.AddHttpClient("BlazorPrerender");
builder.Services.AddHttpClient("LinkValidate-NoRedirect")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false
    });

// Add HttpContextAccessor for getting the current request's base address
builder.Services.AddHttpContextAccessor();

// Add HttpClient with base address for Blazor WASM prerendering (uses factory)
builder.Services.AddScoped(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();
    var httpContext = httpContextAccessor.HttpContext;
    
    var httpClient = httpClientFactory.CreateClient("BlazorPrerender");
    if (httpContext?.Request != null)
    {
        var request = httpContext.Request;
        httpClient.BaseAddress = new Uri($"{request.Scheme}://{request.Host}");
    }
    return httpClient;
});

// Configure LiteDB
var dbPath = builder.Configuration.GetValue<string>("LiteDb:Path") ?? "changedetection.db";
builder.Services.AddSingleton(new LiteDbContext($"Filename={dbPath};Connection=shared"));

// Configure authentication and authorization
builder.Services.AddChangeDetectionAuthentication(builder.Configuration);

// Get auth mode for repository registration
var authSettings = builder.Configuration
    .GetSection(AuthenticationSettings.SectionName)
    .Get<AuthenticationSettings>() ?? new AuthenticationSettings();

// Register base repositories (used directly for global entities and as inner repos for tenant-scoped)
builder.Services.AddScoped(sp => 
    new LiteDbRepository<WatchedSite>(sp.GetRequiredService<LiteDbContext>(), "watches"));
builder.Services.AddScoped(sp => 
    new LiteDbRepository<ChangeSnapshot>(sp.GetRequiredService<LiteDbContext>(), "snapshots"));
builder.Services.AddScoped(sp => 
    new LiteDbRepository<ChangeEvent>(sp.GetRequiredService<LiteDbContext>(), "events"));
builder.Services.AddScoped(sp => 
    new LiteDbRepository<Category>(sp.GetRequiredService<LiteDbContext>(), "categories"));
builder.Services.AddScoped(sp =>
    new LiteDbRepository<WatchGroup>(sp.GetRequiredService<LiteDbContext>(), "watch_groups"));
builder.Services.AddScoped(sp => 
    new LiteDbRepository<View>(sp.GetRequiredService<LiteDbContext>(), "views"));
builder.Services.AddScoped(sp => 
    new LiteDbRepository<NotificationOutboxEntry>(sp.GetRequiredService<LiteDbContext>(), "notification_outbox"));
builder.Services.AddScoped(sp =>
    new LiteDbRepository<TrackedItem>(sp.GetRequiredService<LiteDbContext>(), "tracked_listings"));
builder.Services.AddScoped(sp =>
    new LiteDbRepository<PortalSuggestionEntity>(sp.GetRequiredService<LiteDbContext>(), "portal_suggestions"));

// Register tenant-scoped repository wrappers for owned entities
builder.Services.AddScoped<IRepository<WatchedSite>>(sp => 
    new TenantRepository<WatchedSite>(
        sp.GetRequiredService<LiteDbRepository<WatchedSite>>(),
        sp.GetRequiredService<IUserContext>()));
builder.Services.AddScoped<IRepository<ChangeSnapshot>>(sp => 
    new TenantRepository<ChangeSnapshot>(
        sp.GetRequiredService<LiteDbRepository<ChangeSnapshot>>(),
        sp.GetRequiredService<IUserContext>()));
builder.Services.AddScoped<IRepository<ChangeEvent>>(sp => 
    new TenantRepository<ChangeEvent>(
        sp.GetRequiredService<LiteDbRepository<ChangeEvent>>(),
        sp.GetRequiredService<IUserContext>()));
builder.Services.AddScoped<IRepository<Category>>(sp => 
    new TenantRepository<Category>(
        sp.GetRequiredService<LiteDbRepository<Category>>(),
        sp.GetRequiredService<IUserContext>()));
builder.Services.AddScoped<IRepository<View>>(sp => 
    new TenantRepository<View>(
        sp.GetRequiredService<LiteDbRepository<View>>(),
        sp.GetRequiredService<IUserContext>()));
builder.Services.AddScoped<IRepository<WatchGroup>>(sp =>
    new TenantRepository<WatchGroup>(
        sp.GetRequiredService<LiteDbRepository<WatchGroup>>(),
        sp.GetRequiredService<IUserContext>()));
builder.Services.AddScoped<IRepository<TrackedItem>>(sp =>
    new TenantRepository<TrackedItem>(
        sp.GetRequiredService<LiteDbRepository<TrackedItem>>(),
        sp.GetRequiredService<IUserContext>()));
builder.Services.AddScoped<IRepository<PortalSuggestionEntity>>(sp =>
    new TenantRepository<PortalSuggestionEntity>(
        sp.GetRequiredService<LiteDbRepository<PortalSuggestionEntity>>(),
        sp.GetRequiredService<IUserContext>()));
builder.Services.AddScoped<IRepository<NotificationOutboxEntry>>(sp => 
    new TenantRepository<NotificationOutboxEntry>(
        sp.GetRequiredService<LiteDbRepository<NotificationOutboxEntry>>(),
        sp.GetRequiredService<IUserContext>()));

// Register global repositories (not tenant-scoped)
builder.Services.AddScoped<IRepository<LlmProviderConfig>>(sp => 
    new LiteDbRepository<LlmProviderConfig>(sp.GetRequiredService<LiteDbContext>(), "llm_providers"));
builder.Services.AddScoped<IRepository<LlmUsageRecord>>(sp => 
    new LiteDbRepository<LlmUsageRecord>(sp.GetRequiredService<LiteDbContext>(), "llm_usage"));
builder.Services.AddScoped<IRepository<AppSettings>>(sp => 
    new LiteDbRepository<AppSettings>(sp.GetRequiredService<LiteDbContext>(), "settings"));
builder.Services.AddScoped<IRepository<NotificationTemplate>>(sp => 
    new LiteDbRepository<NotificationTemplate>(sp.GetRequiredService<LiteDbContext>(), "notification_templates"));
builder.Services.AddScoped<IRepository<PriceHistoryEntry>>(sp => 
    new LiteDbRepository<PriceHistoryEntry>(sp.GetRequiredService<LiteDbContext>(), "price_history"));
builder.Services.AddScoped<IRepository<FieldValueHistory>>(sp => 
    new LiteDbRepository<FieldValueHistory>(sp.GetRequiredService<LiteDbContext>(), "field_history"));
builder.Services.AddScoped<IRepository<BlockExecutionSnapshotEntity>>(sp => 
    new LiteDbRepository<BlockExecutionSnapshotEntity>(sp.GetRequiredService<LiteDbContext>(), "blockexecutionsnapshots"));

// Register services
builder.Services.AddSingleton<PlaywrightFetcher>();
builder.Services.AddSingleton<IContentFetcher>(sp => sp.GetRequiredService<PlaywrightFetcher>());
builder.Services.AddSingleton<ILlmLogService, LlmLogService>();
builder.Services.AddSingleton<IRobotsTxtChecker, RobotsTxtChecker>();
builder.Services.AddSingleton<ContentSanitizer>();
builder.Services.AddScoped<PinnedHttpClient>();

// LLM Kernel Factories for provider-specific kernel creation
builder.Services.AddSingleton<ILlmKernelFactory, OllamaKernelFactory>();
builder.Services.AddSingleton<ILlmKernelFactory, OpenAIKernelFactory>();
builder.Services.AddSingleton<ILlmKernelFactory, AzureOpenAIKernelFactory>();
builder.Services.AddSingleton<ILlmKernelFactory, GeminiKernelFactory>();
builder.Services.AddSingleton<ILlmKernelFactory, ClaudeKernelFactory>();
builder.Services.AddSingleton<ILlmKernelFactory, CopilotKernelFactory>();

builder.Services.AddScoped<IContentExtractor, ContentExtractor>();
builder.Services.AddScoped<IStructuredDataExtractor, StructuredDataExtractor>();
builder.Services.AddSingleton<IPiiRedactor, PiiRedactor>();
builder.Services.AddSingleton<ITrustAutopilot, TrustAutopilot>();
builder.Services.AddScoped<IDomCompactor, DomCompactor>();
builder.Services.AddScoped<IDiffService, DiffService>();
builder.Services.AddScoped<IWatchService, ServerWatchService>();
builder.Services.AddScoped<ICategoryService, ServerCategoryService>();
builder.Services.AddScoped<IWatchGroupService, ServerWatchGroupService>();
builder.Services.AddScoped<IPortalDiscoveryAnalyzer, PortalDiscoveryAnalyzer>();
builder.Services.AddScoped<IPortalSuggestionService, PortalSuggestionService>();
builder.Services.AddScoped<IAggregateSetupPipeline, AggregateSetupPipeline>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<ILlmProviderChain, LlmProviderChain>();
builder.Services.AddScoped<IInputProcessor, InputProcessor>();

// Notification outbox for reliable delivery
builder.Services.AddScoped<INotificationOutboxRepository, NotificationOutboxRepository>();
builder.Services.AddScoped<INotificationOutboxService, NotificationOutboxService>();

// Session persistence for resumable setup wizards
builder.Services.AddScoped<ISessionPersistenceService, SessionPersistenceService>();

// LLM-powered content analysis services
builder.Services.AddScoped<IChangeAnalyzer, ChangeAnalyzer>();
builder.Services.AddScoped<IProfileRelevanceScorer, JobMatchRelevanceScorer>();
builder.Services.AddScoped<IContentEnricher, ContentEnricher>();
builder.Services.AddScoped<IDeduplicationService, DeduplicationService>();
builder.Services.AddScoped<IJobDeduplicationService, JobDeduplicationService>();

builder.Services.AddSingleton<SetupFlowEnhancements>();

// Pipeline queue for persistent, concurrent pipeline execution
builder.Services.AddSingleton<IPipelineQueueRepository, PipelineQueueRepository>();
builder.Services.AddSingleton<PipelineQueueService>();
builder.Services.AddSingleton<IPipelineQueueService>(sp => sp.GetRequiredService<PipelineQueueService>());
builder.Services.AddHostedService<PipelineWorkerService>();

// Pipeline event tracking for history and debugging
builder.Services.AddScoped<IPipelineEventService, PipelineEventService>();

// Pipeline support services
builder.Services.AddSingleton<IConversationSessionManager, ConversationSessionManager>();
builder.Services.AddScoped<IInputAnchorValidator, InputAnchorValidator>();

// Session cleanup service - cleans up hub dictionaries when sessions expire
builder.Services.AddHostedService<SetupSessionCleanupService>();

// URL validation for SSRF protection
builder.Services.AddSingleton<IUrlValidator, SafeUrlValidator>();
builder.Services.AddSingleton<DomainPinValidator>();
builder.Services.AddSingleton<PipelineSecurityValidator>();
builder.Services.AddSingleton<PipelineAuditService>();

// Search provider configuration
builder.Services.Configure<SearchSettings>(builder.Configuration.GetSection("SearchSettings"));
builder.Services.AddSingleton<ISearchProvider>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpClientFactory.CreateClient("SearXNG");
    var settings = sp.GetRequiredService<IOptions<SearchSettings>>();
    var urlValidator = sp.GetRequiredService<IUrlValidator>();
    var logger = sp.GetRequiredService<ILogger<SearXNGSearchProvider>>();
    return new SearXNGSearchProvider(httpClient, settings, urlValidator, logger);
});
builder.Services.AddSingleton<ISearchProvider>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpClientFactory.CreateClient("GoogleCSE");
    var settings = sp.GetRequiredService<IOptions<SearchSettings>>();
    var logger = sp.GetRequiredService<ILogger<GoogleCseSearchProvider>>();
    return new GoogleCseSearchProvider(httpClient, settings, logger);
});
builder.Services.AddSingleton<ISearchProvider>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpClientFactory.CreateClient("BraveSearch");
    var settings = sp.GetRequiredService<IOptions<SearchSettings>>();
    var logger = sp.GetRequiredService<ILogger<BraveSearchProvider>>();
    return new BraveSearchProvider(httpClient, settings, logger);
});
builder.Services.AddSingleton<ISearchProvider>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpClientFactory.CreateClient("NewsData");
    var settings = sp.GetRequiredService<IOptions<SearchSettings>>();
    var logger = sp.GetRequiredService<ILogger<NewsDataSearchProvider>>();
    return new NewsDataSearchProvider(httpClient, settings, logger);
});
// LLM-backed search — always available, validates every suggestion via HTTP HEAD
builder.Services.AddSingleton<ISearchProvider>(sp =>
{
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var logger = sp.GetRequiredService<ILogger<LlmSearchProvider>>();
    return new LlmSearchProvider(scopeFactory, httpClientFactory, logger);
});
builder.Services.AddScoped<ISearchDiscoveryService, SearchDiscoveryService>();
builder.Services.AddSingleton<MultiProviderSearchService>();
builder.Services.AddScoped<IQueryEvolutionService, QueryEvolutionService>();
builder.Services.AddScoped<IRssDiscoveryService, RssDiscoveryService>();
builder.Services.AddSingleton<AdversarialSearchAnalyzer>();

// Group Watch discovery service
builder.Services.Configure<GroupWatchDiscoveryOptions>(
    builder.Configuration.GetSection("GroupWatchDiscovery"));
builder.Services.AddScoped<IGroupWatchDiscoveryService, GroupWatchDiscoveryService>();

// Composable pipeline block system
builder.Services.AddSingleton<IBlockRegistry>(sp =>
{
    var registry = new BlockRegistry();
    BlockRegistry.RegisterCoreBlocks(registry);
    return registry;
});
builder.Services.AddSingleton<IPlatformDetector, PlatformDetector>();
builder.Services.AddSingleton<IPipelineTemplateRegistry, PipelineTemplateRegistry>();
builder.Services.AddScoped<IPipelineValidator, PipelineValidator>();
builder.Services.AddScoped<IPipelineExecutor, PipelineExecutor>();
builder.Services.AddScoped<IBlockStateStore, LiteDbBlockStateStore>();
builder.Services.AddSingleton<PipelineReliabilityService>();
builder.Services.AddScoped<ILlmCostTracker, LlmCostTracker>();
builder.Services.AddSingleton<PipelineDegradationService>();

// Composable setup pipeline (LLM-driven pipeline assembly from natural language)
builder.Services.AddScoped<IComposableSetupPipeline, ComposableSetupPipeline>();

// Legacy setup pipeline + stages — kept for AggregateSetupPipeline, PipelineWorkerService, LlmEndpoints
// Marked [Obsolete] in WatchSetupPipeline.cs. Will be fully removed when dependents are migrated.
builder.Services.AddScoped<UrlExtractionStage>();
builder.Services.AddScoped<ContentFetchingStage>();
builder.Services.AddScoped<ContentAnalysisStage>();
builder.Services.AddScoped<SelectorGenerationStage>();
builder.Services.AddScoped<SelectorValidationStage>();
builder.Services.AddScoped<SchemaDiscoveryStage>();
builder.Services.AddScoped<IWatchSetupPipeline, WatchSetupPipeline>();

// Object extraction and filtering services
builder.Services.AddScoped<IObjectExtractionService, ObjectExtractionService>();
builder.Services.AddScoped<IObjectDiffService, ObjectDiffService>();
builder.Services.AddScoped<IFilterEvaluationService, FilterEvaluationService>();
builder.Services.AddScoped<IErrorResolutionService, ErrorResolutionService>();
builder.Services.AddSingleton<IProfileFilterRuleGenerator, ProfileFilterRuleGenerator>();
builder.Services.AddScoped<JobWatchSeeder>();
builder.Services.AddSingleton<IAlertPolicyService, AlertPolicyService>();
builder.Services.AddScoped<IItemTrackingService, ItemTrackingService>();
builder.Services.AddSingleton<IAlertContentGenerator, AlertContentGenerator>();

// Auto-healing services
builder.Services.AddScoped<IAutoHealingService, AutoHealingService>();
builder.Services.AddSingleton<IFailureTracker, FailureTracker>();

// Price tracking services
builder.Services.AddScoped<IPriceHistoryRepository, PriceHistoryRepository>();
builder.Services.AddScoped<IAlertThresholdEvaluator, AlertThresholdEvaluator>();
builder.Services.AddScoped<INotificationTemplateEngine, NotificationTemplateEngine>();
builder.Services.AddScoped<IPriceTrackingService, PriceTrackingService>();
builder.Services.AddScoped<IFieldHistoryService, FieldHistoryService>();

// Client-side services needed for Interactive Server mode
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<LocalStorageService>();
builder.Services.AddScoped<KeyboardShortcutService>();
builder.Services.AddScoped<ThemeService>();

// Database migrations (must run before any other hosted services)
builder.Services.AddSingleton<IDatabaseMigration, V001_InitialSchema>();
builder.Services.AddSingleton<IDatabaseMigration, V002_PortalSuggestions>();
builder.Services.AddHostedService<DatabaseMigrationRunner>();

// Startup/shutdown services (registered first so they stop last during shutdown)
builder.Services.AddHostedService<GracefulShutdownService>();
builder.Services.AddHostedService<WatchStatusRecoveryService>();
builder.Services.AddHostedService<LlmProviderHealthRecoveryService>();
builder.Services.AddHostedService<LlmProviderConfigSyncService>();

// Background services
builder.Services.AddHostedService<ChangeCheckBackgroundService>();
builder.Services.AddHostedService<NotificationOutboxProcessor>();

// Database backup service
builder.Services.AddSingleton<IDatabaseBackupService, DatabaseBackupService>();
builder.Services.AddHostedService<DatabaseBackupBackgroundService>();
builder.Services.AddHostedService<SnapshotCleanupService>();
builder.Services.AddSingleton<DatabaseMaintenanceService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DatabaseMaintenanceService>());

// Graceful shutdown for Playwright
builder.Services.AddHostedService<PlaywrightShutdownService>();

// Default LLM provider seeder - auto-detects Ollama and seeds best available model
builder.Services.AddHostedService<DefaultProviderSeeder>();

// Health checks
builder.Services.AddHealthChecks()
    .AddCheck<LiteDbHealthCheck>("litedb");

var app = builder.Build();

// Enable response compression first in pipeline for maximum benefit
app.UseResponseCompression();

// Forward headers must come FIRST before any scheme-dependent middleware (like HttpsRedirection)
// This ensures proper handling of X-Forwarded-* headers from reverse proxies
app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

// Configure authentication and authorization middleware
app.UseChangeDetectionAuthentication(builder.Configuration);

// Enable output caching for API responses
app.UseOutputCache();

app.UseAntiforgery();

app.MapHealthChecks("/health");

// Map API endpoints with appropriate authorization
// User-owned data endpoints require authentication in SSO mode
app.MapGroup("/api/watches")
    .RequireAuthenticationInSsoMode(builder.Configuration)
    .MapWatchEndpoints();
app.MapGroup("/api/changes")
    .RequireAuthenticationInSsoMode(builder.Configuration)
    .MapChangeEndpoints();
app.MapGroup("/api/categories")
    .RequireAuthenticationInSsoMode(builder.Configuration)
    .MapCategoryEndpoints();
app.MapGroup("/api/views")
    .RequireAuthenticationInSsoMode(builder.Configuration)
    .MapViewEndpoints();
app.MapGroup("/api/groups")
    .RequireAuthenticationInSsoMode(builder.Configuration)
    .MapWatchGroupEndpoints();

// LLM provider management requires admin in SSO mode
app.MapGroup("/api/llm")
    .RequireAdminInSsoMode(builder.Configuration)
    .MapLlmEndpoints();

// Settings management requires admin in SSO mode
app.MapGroup("/api/settings")
    .RequireAdminInSsoMode(builder.Configuration)
    .MapSettingsEndpoints();

// Notification settings and templates requires admin in SSO mode
app.MapGroup("/api/notifications")
    .RequireAdminInSsoMode(builder.Configuration)
    .MapNotificationEndpoints();

// Pipeline debug endpoints for inspecting runs, events, and LLM logs
app.MapGroup("/api/debug/pipeline")
    .RequireAdminInSsoMode(builder.Configuration)
    .MapPipelineDebugEndpoints();

// Change feed endpoints for history, CSV export, and RSS
app.MapGroup("/api/feeds")
    .RequireAuthenticationInSsoMode(builder.Configuration)
    .MapFeedEndpoints();

// Database health monitoring (admin-only in SSO mode)
app.MapGroup("/api/health")
    .RequireAdminInSsoMode(builder.Configuration)
    .MapDatabaseHealthEndpoints();

// Job Watch feature
app.MapGroup("/api/jobwatch")
    .RequireAuthenticationInSsoMode(builder.Configuration)
    .MapJobWatchEndpoints();

// Catalog export/import for sharing verified portal configurations
app.MapGroup("/api/catalog")
    .RequireAuthenticationInSsoMode(builder.Configuration)
    .MapCatalogEndpoints();

// Map SignalR hub
app.MapHub<ChangeDetectionHub>("/hubs/changes")
    .RequireAuthenticationInSsoMode(builder.Configuration);
app.MapHub<ComposableSetupHub>("/hubs/composable-setup")
    .RequireAuthenticationInSsoMode(builder.Configuration);  // MODERN
app.MapHub<AggregateSetupHub>("/hubs/aggregate-setup")
    .RequireAuthenticationInSsoMode(builder.Configuration);  // MODERN
app.MapHub<GroupWatchHub>("/hubs/group-watch")
    .RequireAuthenticationInSsoMode(builder.Configuration);  // Group watch discovery

// Configure static files with aggressive caching (assets are fingerprinted for cache-busting)
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // Cache static files for 1 year (immutable because they're fingerprinted)
        if (ctx.File.Name.Contains('.') && 
            (ctx.File.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
             ctx.File.Name.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
             ctx.File.Name.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase) ||
             ctx.File.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
             ctx.File.Name.EndsWith(".woff", StringComparison.OrdinalIgnoreCase) ||
             ctx.File.Name.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase) ||
             ctx.File.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
             ctx.File.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)))
        {
            ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        }
    }
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(ChangeDetection.Client._Imports).Assembly);

app.Run();

/// <summary>
/// Hosted service for graceful Playwright shutdown.
/// </summary>
public class PlaywrightShutdownService : IHostedService
{
    private readonly PlaywrightFetcher _playwright;

    public PlaywrightShutdownService(PlaywrightFetcher playwright)
    {
        _playwright = playwright;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _playwright.DisposeAsync();
    }
}

/// <summary>
/// Entry point for the application. Exposed for WebApplicationFactory in tests.
/// </summary>
public partial class Program { }
