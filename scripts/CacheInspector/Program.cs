using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var cachePath = @"c:\Users\adam-\GitHub\changedetection\tests\ChangeDetection.Tests\Llm\Cache\llm-responses.db";

// Compute hash the same way as the cache does
string ComputeRequestHash(string requestBody)
{
    string normalizedRequest;
    try
    {
        using var doc = JsonDocument.Parse(requestBody);
        var root = doc.RootElement;
        var model = root.TryGetProperty("model", out var m) ? m.GetString() : "";
        var messages = root.TryGetProperty("messages", out var msgs) ? msgs.ToString() : "";
        var temperature = root.TryGetProperty("temperature", out var t) ? t.GetDouble() : 0.7;
        normalizedRequest = $"{model}|{temperature:F2}|{messages}";
    }
    catch
    {
        normalizedRequest = requestBody;
    }
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRequest));
    return Convert.ToHexString(bytes).ToLowerInvariant();
}

using var connection = new SqliteConnection($"Data Source={cachePath};Mode=ReadOnly");
connection.Open();

// First, list ALL entries
Console.WriteLine("=== ALL Cache Entries ===\n");
using var listCmd = connection.CreateCommand();
listCmd.CommandText = "SELECT request_hash, model, hit_count, created_at FROM llm_cache ORDER BY created_at DESC";
using var listReader = listCmd.ExecuteReader();
int count = 0;
while (listReader.Read())
{
    count++;
    var hash = listReader.GetString(0);
    var model = listReader.IsDBNull(1) ? "?" : listReader.GetString(1);
    var hits = listReader.GetInt64(2);
    var created = listReader.GetString(3);
    Console.WriteLine($"{count}. {hash[..12]}... | {model} | hits={hits} | {created}");
}
listReader.Close();
Console.WriteLine($"\nTotal: {count} entries\n");

// Get the entry with hash f1430d753dc3 and examine what's in response_body
Console.WriteLine("=== Analyzing cached response for hash f1430d753dc3... ===\n");
using var findCmd = connection.CreateCommand();
findCmd.CommandText = "SELECT request_hash, request_body, response_body FROM llm_cache WHERE request_hash LIKE 'f1430d753dc3%'";
using var findReader = findCmd.ExecuteReader();
while (findReader.Read())
{
    var hash = findReader.GetString(0);
    var requestBody = findReader.GetString(1);
    var responseBody = findReader.GetString(2);
    
    Console.WriteLine($"Hash in DB: {hash}");
    Console.WriteLine($"Computed hash: {ComputeRequestHash(requestBody)}");
    
    // Parse the request to see model/temp/messages
    using var reqDoc = JsonDocument.Parse(requestBody);
    var root = reqDoc.RootElement;
    var model = root.TryGetProperty("model", out var m) ? m.GetString() : "";
    var temp = root.TryGetProperty("temperature", out var t) ? t.GetDouble() : 0.7;
    var messages = root.TryGetProperty("messages", out var msgs) ? msgs.ToString() : "";
    
    Console.WriteLine($"\nModel: {model}");
    Console.WriteLine($"Temperature: {temp}");
    Console.WriteLine($"Messages length: {messages.Length} chars");
    Console.WriteLine($"Messages hash component: {messages[..Math.Min(200, messages.Length)]}...");
    
    Console.WriteLine($"\n=== RAW RESPONSE BODY (streaming SSE) ===");
    Console.WriteLine(responseBody[..Math.Min(2000, responseBody.Length)]);
    Console.WriteLine("...");
}
findReader.Close();

// Also compute what hash a different prompt would produce
Console.WriteLine("\n\n=== Testing hash differentiation ===");
var prompt1 = """{"temperature":0.1,"messages":[{"role":"user","content":"Webpage category? Reply ONE word only."}],"model":"ministral-3:14b"}""";
var prompt2 = """{"temperature":0.1,"messages":[{"role":"user","content":"User monitors: test\nPage: test\nSummarize goal in <15 words."}],"model":"ministral-3:14b"}""";
var prompt3 = """{"temperature":0.1,"messages":[{"role":"user","content":"Analyze this HTML and find 1-3 content sections."}],"model":"ministral-3:14b"}""";

Console.WriteLine($"Prompt1 hash: {ComputeRequestHash(prompt1)[..12]}...");
Console.WriteLine($"Prompt2 hash: {ComputeRequestHash(prompt2)[..12]}...");
Console.WriteLine($"Prompt3 hash: {ComputeRequestHash(prompt3)[..12]}...");
Console.WriteLine("(All should be different)");
