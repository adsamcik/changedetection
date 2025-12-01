using ChangeDetection.Client.Pages;
using ChangeDetection.Components;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Endpoints;
using ChangeDetection.Hubs;
using ChangeDetection.Services;
using ChangeDetection.Services.Background;
using ChangeDetection.Services.Content;
using ChangeDetection.Services.LLM;
using ChangeDetection.Services.Notifications;
using ChangeDetection.Services.Persistence;
using ChangeDetection.Services.Scraping;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// Add SignalR
builder.Services.AddSignalR();

// Add HttpClient factory
builder.Services.AddHttpClient();

// Configure LiteDB
var dbPath = builder.Configuration.GetValue<string>("LiteDb:Path") ?? "changedetection.db";
builder.Services.AddSingleton(new LiteDbContext($"Filename={dbPath};Connection=shared"));

// Register repositories
builder.Services.AddScoped<IRepository<WatchedSite>>(sp => 
    new LiteDbRepository<WatchedSite>(sp.GetRequiredService<LiteDbContext>(), "watches"));
builder.Services.AddScoped<IRepository<ChangeSnapshot>>(sp => 
    new LiteDbRepository<ChangeSnapshot>(sp.GetRequiredService<LiteDbContext>(), "snapshots"));
builder.Services.AddScoped<IRepository<ChangeEvent>>(sp => 
    new LiteDbRepository<ChangeEvent>(sp.GetRequiredService<LiteDbContext>(), "events"));
builder.Services.AddScoped<IRepository<LlmProviderConfig>>(sp => 
    new LiteDbRepository<LlmProviderConfig>(sp.GetRequiredService<LiteDbContext>(), "llm_providers"));
builder.Services.AddScoped<IRepository<LlmUsageRecord>>(sp => 
    new LiteDbRepository<LlmUsageRecord>(sp.GetRequiredService<LiteDbContext>(), "llm_usage"));
builder.Services.AddScoped<IRepository<AppSettings>>(sp => 
    new LiteDbRepository<AppSettings>(sp.GetRequiredService<LiteDbContext>(), "settings"));

// Register services
builder.Services.AddSingleton<PlaywrightFetcher>();
builder.Services.AddSingleton<IContentFetcher>(sp => sp.GetRequiredService<PlaywrightFetcher>());
builder.Services.AddScoped<IContentExtractor, ContentExtractor>();
builder.Services.AddScoped<IDiffService, DiffService>();
builder.Services.AddScoped<IWatchService, ServerWatchService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<ILlmProviderChain, LlmProviderChain>();
builder.Services.AddScoped<IInputProcessor, InputProcessor>();

// Background services
builder.Services.AddHostedService<ChangeCheckBackgroundService>();

// Graceful shutdown for Playwright
builder.Services.AddHostedService<PlaywrightShutdownService>();

var app = builder.Build();

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

app.UseAntiforgery();

// Map API endpoints
app.MapGroup("/api/watches").MapWatchEndpoints();
app.MapGroup("/api/changes").MapChangeEndpoints();
app.MapGroup("/api/llm").MapLlmEndpoints();

// Map SignalR hub
app.MapHub<ChangeDetectionHub>("/hubs/changes");

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

