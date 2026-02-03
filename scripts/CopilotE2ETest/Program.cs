// E2E test for Copilot SDK integration
// Run with: dotnet run --project scripts/CopilotE2ETest/CopilotE2ETest.csproj

using GitHub.Copilot.SDK;

Console.WriteLine("=== GitHub Copilot SDK E2E Test ===\n");

// Create client
Console.WriteLine("1. Creating CopilotClient...");
await using var client = new CopilotClient(new CopilotClientOptions
{
    AutoStart = true,
    UseLoggedInUser = true
});

// Start client
Console.WriteLine("2. Starting client...");
await client.StartAsync();
Console.WriteLine($"   Client state: {client.State}");

// List available models
Console.WriteLine("\n3. Listing available models...");
var models = await client.ListModelsAsync();
Console.WriteLine($"   Found {models.Count} models:\n");

foreach (var model in models.OrderBy(m => m.Name))
{
    Console.WriteLine($"   - Name: {model.Name}, Id: {model.Id}");
}

// Test with a simple prompt - use gpt-5.2 specifically
var selectedModel = "gpt-5.2";
Console.WriteLine($"\n4. Testing with model: {selectedModel}");
var session = await client.CreateSessionAsync(new SessionConfig
{
    Model = selectedModel,
    Streaming = false,
    InfiniteSessions = new InfiniteSessionConfig { Enabled = false }
});

string response = "";
var done = new TaskCompletionSource();

session.On(evt =>
{
    switch (evt)
    {
        case AssistantMessageEvent msg:
            response = msg.Data.Content ?? "";
            Console.WriteLine($"\n   Response received ({response.Length} chars)");
            break;
        case SessionIdleEvent:
            done.TrySetResult();
            break;
        case SessionErrorEvent err:
            Console.WriteLine($"\n   ERROR: {err.Data.Message}");
            done.TrySetException(new Exception(err.Data.Message));
            break;
    }
});

await session.SendAsync(new MessageOptions 
{ 
    Prompt = "Reply with exactly: 'E2E test successful'" 
});

await done.Task;

Console.WriteLine($"\n   Full response: {response.Substring(0, Math.Min(200, response.Length))}...");

// Cleanup - dispose session first, then stop client
Console.WriteLine("\n5. Cleaning up...");
await session.DisposeAsync();
await client.StopAsync();

Console.WriteLine("\n=== E2E Test Complete ===");
