using System.Text;
using System.Text.RegularExpressions;
using ChangeDetection.Shared.Dtos;

namespace ChangeDetection.Services.GroupWatch;

public interface IJobDeduplicationService
{
    List<GroupResultItemDto> DeduplicateAcrossSources(List<GroupResultItemDto> items);
}

public sealed partial class JobDeduplicationService : IJobDeduplicationService
{
    private const double SimilarityThreshold = 0.85d;

    public List<GroupResultItemDto> DeduplicateAcrossSources(List<GroupResultItemDto> items)
    {
        if (items.Count == 0)
            return [];

        var ordered = items
            .OrderByDescending(i => i.IsNew)
            .ThenByDescending(i => i.RelevanceScore ?? 0f)
            .ThenByDescending(i => i.FirstSeen)
            .ToList();

        var deduped = new List<GroupResultItemDto>();

        foreach (var item in ordered)
        {
            var prepared = PrepareItem(item);
            var existing = deduped.FirstOrDefault(candidate => IsMatch(candidate, prepared));

            if (existing is null)
            {
                deduped.Add(prepared);
                continue;
            }

            MergeInto(existing, prepared);
        }

        return deduped
            .OrderByDescending(i => i.IsNew)
            .ThenByDescending(i => i.RelevanceScore ?? 0f)
            .ThenByDescending(i => i.FirstSeen)
            .ToList();
    }

    private static GroupResultItemDto PrepareItem(GroupResultItemDto item)
    {
        var clone = Clone(item);
        EnsureSourceLists(clone);
        clone.IsMultiSource = ComputeIsMultiSource(clone);
        return clone;
    }

    private static GroupResultItemDto Clone(GroupResultItemDto item) => new()
    {
        Title = item.Title,
        Url = item.Url,
        Company = item.Company,
        Location = item.Location,
        Source = item.Source,
        SourceWatchId = item.SourceWatchId,
        Sources = [.. item.Sources],
        SourceNames = [.. item.SourceNames],
        SourceWatchIds = [.. item.SourceWatchIds],
        IsMultiSource = item.IsMultiSource,
        RelevanceScore = item.RelevanceScore,
        FirstSeen = item.FirstSeen,
        IsNew = item.IsNew,
        ExtraFields = new Dictionary<string, string>(item.ExtraFields, StringComparer.OrdinalIgnoreCase)
    };

    private static bool IsMatch(GroupResultItemDto existing, GroupResultItemDto candidate)
    {
        if (HasSharedUrl(existing, candidate))
            return true;

        var titleSimilarity = GetTitleSimilarity(existing, candidate);
        if (titleSimilarity < SimilarityThreshold)
            return false;

        var existingCompany = NormalizeText(existing.Company);
        var candidateCompany = NormalizeText(candidate.Company);

        if (!string.IsNullOrEmpty(existingCompany) && !string.IsNullOrEmpty(candidateCompany))
            return string.Equals(existingCompany, candidateCompany, StringComparison.Ordinal);

        var existingLocation = NormalizeText(existing.Location);
        var candidateLocation = NormalizeText(candidate.Location);

        if (!string.IsNullOrEmpty(existingLocation) && !string.IsNullOrEmpty(candidateLocation))
            return string.Equals(existingLocation, candidateLocation, StringComparison.Ordinal);

        return false;
    }

    private static bool HasSharedUrl(GroupResultItemDto existing, GroupResultItemDto candidate)
    {
        var existingUrls = GetCanonicalUrls(existing);
        var candidateUrls = GetCanonicalUrls(candidate);

        return existingUrls.Count > 0 && existingUrls.Overlaps(candidateUrls);
    }

    private static HashSet<string> GetCanonicalUrls(GroupResultItemDto item)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in item.Sources)
        {
            var canonical = CanonicalizeUrl(source);
            if (!string.IsNullOrWhiteSpace(canonical))
                urls.Add(canonical);
        }

        var itemUrl = CanonicalizeUrl(item.Url);
        if (!string.IsNullOrWhiteSpace(itemUrl))
            urls.Add(itemUrl);

        return urls;
    }

    private static string? CanonicalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.EndsWith('/') ? trimmed.TrimEnd('/') : trimmed;
    }

    private static double GetTitleSimilarity(GroupResultItemDto existing, GroupResultItemDto candidate)
    {
        var left = NormalizeTitle(existing.Title, existing.Location, existing.Company);
        var right = NormalizeTitle(candidate.Title, candidate.Location, candidate.Company);

        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            return 0d;

        if (string.Equals(left, right, StringComparison.Ordinal))
            return 1d;

        var maxLength = Math.Max(left.Length, right.Length);
        if (maxLength < 10)
            return 0d;

        var distance = LevenshteinDistance(left, right);
        return 1d - (double)distance / maxLength;
    }

    private static string NormalizeTitle(string? title, string? location, string? company)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var value = title.Trim();
        value = GenderSuffixRegex().Replace(value, "");

        if (!string.IsNullOrWhiteSpace(company))
        {
            var escapedCompany = Regex.Escape(company.Trim());
            value = Regex.Replace(value, $@"^\s*{escapedCompany}\s*[-–—|,:]\s*", "", RegexOptions.IgnoreCase);
            value = Regex.Replace(value, $@"\s*[-–—|,:]\s*{escapedCompany}\s*$", "", RegexOptions.IgnoreCase);
            value = Regex.Replace(value, $@"\s*\({escapedCompany}\)\s*$", "", RegexOptions.IgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(location))
        {
            var escapedLocation = Regex.Escape(location.Trim());
            value = Regex.Replace(value, $@"\s*[-–—|,]\s*{escapedLocation}\s*$", "", RegexOptions.IgnoreCase);
            value = Regex.Replace(value, $@"\s*\({escapedLocation}\)\s*$", "", RegexOptions.IgnoreCase);
        }

        return NormalizeText(value);
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim().ToLowerInvariant();
        normalized = SeparatorRegex().Replace(normalized, " ");
        normalized = WhitespaceRegex().Replace(normalized, " ");
        return normalized.Trim();
    }

    private static int LevenshteinDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static void MergeInto(GroupResultItemDto target, GroupResultItemDto incoming)
    {
        EnsureSourceLists(target);
        EnsureSourceLists(incoming);

        AddUnique(target.Sources, incoming.Sources);
        AddUnique(target.SourceNames, incoming.SourceNames);
        AddUnique(target.SourceWatchIds, incoming.SourceWatchIds);

        if (ShouldPromote(incoming, target))
        {
            target.Title = incoming.Title;
            target.Url = incoming.Url;
            target.Company = incoming.Company;
            target.Location = incoming.Location;
            target.Source = incoming.Source;
            target.SourceWatchId = incoming.SourceWatchId;

            foreach (var field in incoming.ExtraFields)
                target.ExtraFields[field.Key] = field.Value;
        }
        else
        {
            foreach (var field in incoming.ExtraFields)
                target.ExtraFields.TryAdd(field.Key, field.Value);
        }

        if (string.IsNullOrWhiteSpace(target.Url) && !string.IsNullOrWhiteSpace(incoming.Url))
            target.Url = incoming.Url;

        target.RelevanceScore = Math.Max(target.RelevanceScore ?? 0f, incoming.RelevanceScore ?? 0f);
        target.FirstSeen = target.FirstSeen <= incoming.FirstSeen ? target.FirstSeen : incoming.FirstSeen;
        target.IsNew = target.IsNew && incoming.IsNew;
        target.IsMultiSource = ComputeIsMultiSource(target);
    }

    private static bool ShouldPromote(GroupResultItemDto candidate, GroupResultItemDto current)
    {
        var candidateScore = candidate.RelevanceScore ?? 0f;
        var currentScore = current.RelevanceScore ?? 0f;

        if (candidateScore > currentScore)
            return true;

        if (candidateScore.Equals(currentScore))
        {
            if (!string.IsNullOrWhiteSpace(candidate.Url) && string.IsNullOrWhiteSpace(current.Url))
                return true;

            if (candidate.IsNew && !current.IsNew)
                return true;
        }

        return false;
    }

    private static void EnsureSourceLists(GroupResultItemDto item)
    {
        item.Sources ??= [];
        item.SourceNames ??= [];
        item.SourceWatchIds ??= [];

        if (!string.IsNullOrWhiteSpace(item.Url))
            AddUnique(item.Sources, item.Url);

        if (!string.IsNullOrWhiteSpace(item.Source))
            AddUnique(item.SourceNames, item.Source);

        if (!string.IsNullOrWhiteSpace(item.SourceWatchId))
            AddUnique(item.SourceWatchIds, item.SourceWatchId);
    }

    private static bool ComputeIsMultiSource(GroupResultItemDto item) =>
        item.SourceWatchIds.Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any()
        || item.SourceNames.Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any()
        || item.Sources.Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();

    private static void AddUnique(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values)
            AddUnique(target, value);
    }

    private static void AddUnique(List<string> target, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!target.Contains(value, StringComparer.OrdinalIgnoreCase))
            target.Add(value);
    }

    [GeneratedRegex(@"\s*\((m/f/d|f/m/d|m/w/d|w/m/d|all genders?)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex GenderSuffixRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.Compiled)]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();
}
