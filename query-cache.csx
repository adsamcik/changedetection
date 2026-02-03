using Microsoft.Data.Sqlite;
var dbPath = @"c:\Users\adam-\GitHub\changedetection\tests\ChangeDetection.Tests\Llm\Cache\llm-responses.db";
using var conn = new SqliteConnection($"Data Source={dbPath}");
conn.Open();
using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT request_hash, model, response_content, duration_ms, hit_count FROM llm_cache LIMIT 20";
using var reader = cmd.ExecuteReader();
while (reader.Read()) {
    var hash = reader.GetString(0);
    var model = reader.IsDBNull(1) ? "null" : reader.GetString(1);
    var content = reader.IsDBNull(2) ? "null" : reader.GetString(2);
    var contentPreview = content.Length > 80 ? content.Substring(0, 80) + "..." : content;
    var duration = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
    var hits = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);
    Console.WriteLine($"Hash: {hash.Substring(0,16)} | Model: {model} | Duration: {duration}ms | Hits: {hits}");
    Console.WriteLine($"  Content: {contentPreview}");
    Console.WriteLine();
}
