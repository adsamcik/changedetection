using System.Text.Json;
using HtmlAgilityPack;

namespace ChangeDetection.Services;

public interface IStructuredDataExtractor
{
    /// <summary>Extract JSON-LD objects from HTML.</summary>
    List<JsonElement> ExtractJsonLd(string html);

    /// <summary>Extract OpenGraph meta properties.</summary>
    Dictionary<string, string> ExtractOpenGraph(string html);

    /// <summary>Extract Schema.org microdata.</summary>
    List<JsonElement> ExtractSchemaOrg(string html);

    /// <summary>Detect RSS/Atom feed URLs.</summary>
    List<string> DetectFeeds(string html);

    /// <summary>Try to extract a field value from structured data sources (JSON-LD first, then OG, then microdata).</summary>
    string? TryExtractField(string html, string fieldName);

    /// <summary>Try to extract a field value and report which structured data source provided it.</summary>
    (string? Value, string? Source) TryExtractFieldWithSource(string html, string fieldName);
}

public sealed class StructuredDataExtractor : IStructuredDataExtractor
{
    private static readonly Dictionary<string, string[]> FieldAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = ["title", "name", "headline"],
        ["name"] = ["name", "headline", "title"],
        ["description"] = ["description", "summary"],
        ["url"] = ["url", "mainEntityOfPage", "sameAs"],
        ["image"] = ["image", "thumbnailUrl", "logo.url", "logo"],
        ["price"] = ["offers.price", "price", "lowPrice", "highPrice"],
        ["pricecurrency"] = ["offers.priceCurrency", "priceCurrency", "currency"],
        ["currency"] = ["offers.priceCurrency", "priceCurrency", "currency"],
        ["availability"] = ["offers.availability", "availability"],
        ["company"] = ["hiringOrganization.name", "organization.name", "brand.name", "seller.name", "company"],
        ["organization"] = ["organization.name", "hiringOrganization.name", "brand.name", "seller.name"],
        ["location"] = ["jobLocation.address.addressLocality", "jobLocation.name", "location", "address.addressLocality"],
        ["deadline"] = ["validThrough", "expires", "endDate"],
        ["date"] = ["datePosted", "datePublished", "startDate", "dateModified", "dateCreated"],
        ["posteddate"] = ["datePosted", "datePublished", "uploadDate"],
        ["dateposted"] = ["datePosted", "datePublished", "uploadDate"],
        ["posted_date"] = ["datePosted", "datePublished", "uploadDate"],
        ["salary"] = ["baseSalary.value.value", "baseSalary.value.minValue", "baseSalary.value.maxValue", "salary", "offers.price"],
        ["employmenttype"] = ["employmentType"]
    };

    public List<JsonElement> ExtractJsonLd(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return [];

        var doc = LoadDocument(html);
        var scripts = doc.DocumentNode.SelectNodes("//script[@type]");
        if (scripts is null)
            return [];

        var results = new List<JsonElement>();
        foreach (var script in scripts)
        {
            var type = script.GetAttributeValue("type", string.Empty);
            if (!type.Contains("ld+json", StringComparison.OrdinalIgnoreCase))
                continue;

            var json = HtmlEntity.DeEntitize(script.InnerText).Trim();
            if (string.IsNullOrWhiteSpace(json))
                continue;

            try
            {
                using var jsonDoc = JsonDocument.Parse(json);
                CollectJsonLdObjects(jsonDoc.RootElement, results);
            }
            catch (JsonException)
            {
                // Ignore malformed structured data blocks and continue.
            }
        }

        return results;
    }

    public Dictionary<string, string> ExtractOpenGraph(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var doc = LoadDocument(html);
        var metaNodes = doc.DocumentNode.SelectNodes("//meta[@property]");
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (metaNodes is null)
            return results;

        foreach (var node in metaNodes)
        {
            var property = node.GetAttributeValue("property", string.Empty).Trim();
            var content = node.GetAttributeValue("content", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(content))
                continue;

            if (!property.Contains(':', StringComparison.Ordinal) &&
                !property.StartsWith("og", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            results[property] = content;
        }

        return results;
    }

    public List<JsonElement> ExtractSchemaOrg(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return [];

        var doc = LoadDocument(html);
        var itemScopes = doc.DocumentNode.SelectNodes("//*[@itemscope]");
        if (itemScopes is null)
            return [];

        var results = new List<JsonElement>();
        foreach (var itemScope in itemScopes)
        {
            var data = BuildMicrodataObject(itemScope);
            if (data.Count == 0)
                continue;

            results.Add(JsonSerializer.SerializeToElement(data));
        }

        return results;
    }

    public List<string> DetectFeeds(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return [];

        var doc = LoadDocument(html);
        var links = doc.DocumentNode.SelectNodes("//link[@rel and @type and @href]");
        if (links is null)
            return [];

        var feeds = new List<string>();
        foreach (var link in links)
        {
            var rel = link.GetAttributeValue("rel", string.Empty);
            var type = link.GetAttributeValue("type", string.Empty);
            var href = link.GetAttributeValue("href", string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(href))
                continue;

            if (!rel.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(token => string.Equals(token, "alternate", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (string.Equals(type, "application/rss+xml", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "application/atom+xml", StringComparison.OrdinalIgnoreCase))
            {
                feeds.Add(href);
            }
        }

        return feeds;
    }

    public string? TryExtractField(string html, string fieldName)
        => TryExtractFieldWithSource(html, fieldName).Value;

    public (string? Value, string? Source) TryExtractFieldWithSource(string html, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(fieldName))
            return (null, null);

        var candidates = GetCandidatePaths(fieldName);

        foreach (var jsonLdObject in ExtractJsonLd(html))
        {
            var value = TryExtractFromJson(jsonLdObject, candidates);
            if (value is not null)
                return (value, "json-ld");
        }

        var openGraph = ExtractOpenGraph(html);
        foreach (var candidate in candidates)
        {
            foreach (var key in GetOpenGraphCandidates(candidate))
            {
                if (openGraph.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                    return (value, "open-graph");
            }
        }

        foreach (var schemaOrgObject in ExtractSchemaOrg(html))
        {
            var value = TryExtractFromJson(schemaOrgObject, candidates);
            if (value is not null)
                return (value, "schema-org");
        }

        return (null, null);
    }

    private static HtmlDocument LoadDocument(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc;
    }

    private static void CollectJsonLdObjects(JsonElement element, ICollection<JsonElement> results)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectJsonLdObjects(item, results);
                break;

            case JsonValueKind.Object:
                var hasGraph = element.TryGetProperty("@graph", out var graph);
                var hasMeaningfulFields = element.EnumerateObject()
                    .Any(prop => prop.Name is not "@context" and not "@graph");

                if (hasMeaningfulFields)
                    results.Add(element.Clone());

                if (hasGraph)
                    CollectJsonLdObjects(graph, results);
                break;
        }
    }

    private static Dictionary<string, object?> BuildMicrodataObject(HtmlNode scopeNode)
    {
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        var itemType = scopeNode.GetAttributeValue("itemtype", string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(itemType))
            data["@type"] = itemType;

        var itemId = scopeNode.GetAttributeValue("itemid", string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(itemId))
            data["@id"] = itemId;

        var properties = scopeNode.DescendantsAndSelf()
            .Where(node => node.Attributes["itemprop"] is not null && BelongsToScope(scopeNode, node));

        foreach (var propertyNode in properties)
        {
            var propertyNames = propertyNode.GetAttributeValue("itemprop", string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (propertyNames.Length == 0)
                continue;

            var value = ExtractMicrodataValue(propertyNode);
            foreach (var propertyName in propertyNames)
                AddProperty(data, propertyName, value);
        }

        return data;
    }

    private static bool BelongsToScope(HtmlNode scopeNode, HtmlNode candidate)
    {
        var current = candidate.ParentNode;
        while (current is not null && current != scopeNode)
        {
            if (current.Attributes["itemscope"] is not null)
                return false;

            current = current.ParentNode;
        }

        return current == scopeNode || candidate == scopeNode;
    }

    private static object? ExtractMicrodataValue(HtmlNode node)
    {
        if (node.Attributes["itemscope"] is not null)
            return BuildMicrodataObject(node);

        return node.Name.ToLowerInvariant() switch
        {
            "meta" => ValueOrNull(node.GetAttributeValue("content", string.Empty)),
            "a" or "area" or "link" => ValueOrNull(node.GetAttributeValue("href", string.Empty)),
            "audio" or "embed" or "iframe" or "img" or "source" or "track" or "video" => ValueOrNull(node.GetAttributeValue("src", string.Empty)),
            "object" => ValueOrNull(node.GetAttributeValue("data", string.Empty)),
            "data" or "meter" => ValueOrNull(node.GetAttributeValue("value", string.Empty)),
            "time" => ValueOrNull(node.GetAttributeValue("datetime", string.Empty)) ?? ValueOrNull(node.InnerText),
            "input" => ValueOrNull(node.GetAttributeValue("value", string.Empty)),
            _ => ValueOrNull(node.InnerText)
        };
    }

    private static void AddProperty(IDictionary<string, object?> target, string name, object? value)
    {
        if (!target.TryGetValue(name, out var existing))
        {
            target[name] = value;
            return;
        }

        if (existing is List<object?> list)
        {
            list.Add(value);
            return;
        }

        target[name] = new List<object?> { existing, value };
    }

    private static string? ValueOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return HtmlEntity.DeEntitize(value).Trim();
    }

    private static IReadOnlyList<string> GetCandidatePaths(string fieldName)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(fieldName))
            candidates.Add(fieldName);

        var normalized = NormalizeToken(fieldName);
        if (!string.IsNullOrWhiteSpace(normalized) &&
            FieldAliases.TryGetValue(normalized, out var aliases))
        {
            candidates.AddRange(aliases);
        }

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> GetOpenGraphCandidates(string candidate)
    {
        var normalized = NormalizeToken(candidate);
        yield return $"og:{candidate}";

        switch (normalized)
        {
            case "title":
            case "name":
                yield return "og:title";
                break;
            case "description":
                yield return "og:description";
                break;
            case "url":
                yield return "og:url";
                break;
            case "image":
                yield return "og:image";
                break;
            case "price":
                yield return "product:price:amount";
                break;
            case "pricecurrency":
            case "currency":
                yield return "product:price:currency";
                break;
        }
    }

    private static string? TryExtractFromJson(JsonElement element, IReadOnlyList<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            var value = TryExtractByPath(element, candidate, remainingDepth: 10);
            if (value is not null)
                return value;
        }

        return null;
    }

    private static string? TryExtractByPath(JsonElement element, string path, int remainingDepth)
    {
        if (remainingDepth < 0)
            return null;

        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeToken)
            .Where(segment => segment.Length > 0)
            .ToArray();

        if (segments.Length == 0)
            return null;

        return TryExtractByPath(element, segments, 0, remainingDepth);
    }

    private static string? TryExtractByPath(JsonElement element, string[] segments, int index, int remainingDepth)
    {
        if (remainingDepth < 0)
            return null;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (NormalizeToken(property.Name) != segments[index])
                        continue;

                    if (index == segments.Length - 1)
                        return JsonValueToString(property.Value);

                    var nested = TryExtractByPath(property.Value, segments, index + 1, remainingDepth - 1);
                    if (nested is not null)
                        return nested;
                }

                foreach (var property in element.EnumerateObject())
                {
                    var nested = TryExtractByPath(property.Value, segments, index, remainingDepth - 1);
                    if (nested is not null)
                        return nested;
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = TryExtractByPath(item, segments, index, remainingDepth - 1);
                    if (nested is not null)
                        return nested;
                }

                break;
        }

        return null;
    }

    private static string? JsonValueToString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => ValueOrNull(value.GetString()),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            JsonValueKind.Array => value.EnumerateArray().Select(JsonValueToString).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
            JsonValueKind.Object => FirstScalarValue(value),
            _ => null
        };
    }

    private static string? FirstScalarValue(JsonElement value)
    {
        foreach (var property in value.EnumerateObject())
        {
            var candidate = JsonValueToString(property.Value);
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;
        }

        return null;
    }

    private static string NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
}
