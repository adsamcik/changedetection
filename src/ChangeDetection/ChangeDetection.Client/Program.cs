using ChangeDetection.Client;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Configure shared JSON serializer options with string enum support
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNameCaseInsensitive = true,
};
jsonOptions.Converters.Add(new JsonStringEnumConverter());
builder.Services.AddSingleton(jsonOptions);

// Configure HttpClient to use the server base address
builder.Services.AddScoped(sp => new HttpClient 
{ 
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) 
});

// Register ToastService for global toast notifications
builder.Services.AddScoped<ToastService>();

// Register LocalStorageService for persisting UI state
builder.Services.AddScoped<LocalStorageService>();

// Register KeyboardShortcutService for global keyboard navigation
builder.Services.AddScoped<KeyboardShortcutService>();

// Register ThemeService for dark/light mode management
builder.Services.AddScoped<ThemeService>();

await builder.Build().RunAsync();
