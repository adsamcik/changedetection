using System.Text;
using System.Text.Json;

var sseBody = @"data: {""id"":""chatcmpl-142"",""object"":""chat.completion.chunk"",""created"":1766730319,""model"":""ministral-3:14b"",""system_fingerprint"":""fp_ollama"",""choices"":[{""index"":0,""delta"":{""role"":""assistant"",""content"":""**""},""finish_reason"":null}]}
data: {""id"":""chatcmpl-142"",""object"":""chat.completion.chunk"",""created"":1766730319,""model"":""ministral-3:14b"",""choices"":[{""index"":0,""delta"":{""content"":""Event""},""finish_reason"":null}]}
data: {""id"":""chatcmpl-142"",""object"":""chat.completion.chunk"",""created"":1766730319,""model"":""ministral-3:14b"",""choices"":[{""index"":0,""delta"":{""content"":""List""},""finish_reason"":null}]}
data: {""id"":""chatcmpl-142"",""object"":""chat.completion.chunk"",""created"":1766730319,""model"":""ministral-3:14b"",""choices"":[{""index"":0,""delta"":{""content"":""**""},""finish_reason"":null}]}
data: {""id"":""chatcmpl-142"",""object"":""chat.completion.chunk"",""created"":1766730319,""model"":""ministral-3:14b"",""choices"":[{""index"":0,""delta"":{""content"":""""},""finish_reason"":""stop""}]}
data: {""id"":""chatcmpl-142"",""object"":""chat.completion.chunk"",""created"":1766730319,""model"":""ministral-3:14b"",""choices"":[],""usage"":{""prompt_tokens"":772,""completion_tokens"":5,""total_tokens"":777}}
data: [DONE]";

var contentBuilder = new StringBuilder();
string? model = null;
string? finishReason = null;

var lines = sseBody.Split('\n', StringSplitOptions.RemoveEmptyEntries);
foreach (var line in lines)
{
    var trimmedLine = line.Trim();
    if (!trimmedLine.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        continue;
    
    var jsonPart = trimmedLine[5..].Trim();
    if (jsonPart.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
        continue;
    
    try 
    {
        using var doc = JsonDocument.Parse(jsonPart);
        var root = doc.RootElement;
        
        if (model == null && root.TryGetProperty("model", out var modelProp))
            model = modelProp.GetString();
        
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("delta", out var delta))
            {
                if (delta.TryGetProperty("content", out var content))
                {
                    contentBuilder.Append(content.GetString());
                }
            }
            if (firstChoice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind != JsonValueKind.Null)
                finishReason = fr.GetString();
        }
    }
    catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); }
}

Console.WriteLine($"Model: {model}");
Console.WriteLine($"Content: {contentBuilder}");
Console.WriteLine($"Finish Reason: {finishReason}");
