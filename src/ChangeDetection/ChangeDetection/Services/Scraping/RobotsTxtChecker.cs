using System.Collections.Concurrent;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Scraping;

/// <summary>
/// Checks robots.txt compliance by fetching and parsing the robots.txt file for a domain.
/// Uses an in-memory cache with 24-hour TTL per domain.
/// </summary>
public class RobotsTxtChecker(
    IHttpClientFactory httpClientFactory,
    ILogger<RobotsTxtChecker> logger) : IRobotsTxtChecker
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<string, (DateTime FetchedAt, List<RobotsTxtRule> Rules)> _cache = new();

    public async Task<RobotsTxtResult> CheckAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new RobotsTxtResult(RobotsTxtStatus.Unclear, "Invalid URL");

        var domain = uri.GetLeftPart(UriPartial.Authority);
        var rules = await GetRulesAsync(domain, ct);

        if (rules is null)
            return new RobotsTxtResult(RobotsTxtStatus.Unclear, "Could not fetch robots.txt");

        if (rules.Count == 0)
            return new RobotsTxtResult(RobotsTxtStatus.Allowed, "No robots.txt or empty");

        return EvaluateRules(rules, uri.PathAndQuery);
    }

    private async Task<List<RobotsTxtRule>?> GetRulesAsync(string domain, CancellationToken ct)
    {
        if (_cache.TryGetValue(domain, out var cached) && DateTime.UtcNow - cached.FetchedAt < CacheTtl)
            return cached.Rules;

        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var response = await client.GetAsync($"{domain}/robots.txt", ct);

            if (!response.IsSuccessStatusCode)
            {
                var emptyRules = new List<RobotsTxtRule>();
                _cache[domain] = (DateTime.UtcNow, emptyRules);
                return emptyRules;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            var rules = ParseRobotsTxt(content);
            _cache[domain] = (DateTime.UtcNow, rules);
            return rules;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch robots.txt for {Domain}", domain);
            return null;
        }
    }

    internal static List<RobotsTxtRule> ParseRobotsTxt(string content)
    {
        var rules = new List<RobotsTxtRule>();
        string? currentAgent = null;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Split('#')[0].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var colonIdx = line.IndexOf(':');
            if (colonIdx < 0) continue;

            var directive = line[..colonIdx].Trim().ToLowerInvariant();
            var value = line[(colonIdx + 1)..].Trim();

            switch (directive)
            {
                case "user-agent":
                    currentAgent = value.ToLowerInvariant();
                    break;
                case "disallow" when currentAgent is not null && value.Length > 0:
                    rules.Add(new RobotsTxtRule(currentAgent, value, IsAllow: false));
                    break;
                case "allow" when currentAgent is not null && value.Length > 0:
                    rules.Add(new RobotsTxtRule(currentAgent, value, IsAllow: true));
                    break;
            }
        }

        return rules;
    }

    internal static RobotsTxtResult EvaluateRules(List<RobotsTxtRule> rules, string path)
    {
        // Prioritize specific agent rules, then wildcard
        var applicableRules = rules
            .Where(r => r.Agent == "*")
            .OrderByDescending(r => r.Path.Length)
            .ToList();

        foreach (var rule in applicableRules)
        {
            if (!path.StartsWith(rule.Path, StringComparison.OrdinalIgnoreCase)) continue;

            return rule.IsAllow
                ? new RobotsTxtResult(RobotsTxtStatus.Allowed, $"Allowed by rule: {rule.Path}")
                : new RobotsTxtResult(RobotsTxtStatus.Disallowed, $"Disallowed by rule: {rule.Path}");
        }

        return new RobotsTxtResult(RobotsTxtStatus.Allowed, "No matching rules");
    }
}

internal record RobotsTxtRule(string Agent, string Path, bool IsAllow);
