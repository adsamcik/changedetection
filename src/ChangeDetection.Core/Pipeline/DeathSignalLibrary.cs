namespace ChangeDetection.Core.Pipeline;

public static class DeathSignalLibrary
{
    private static readonly Lazy<IReadOnlyList<string>> _allSignals =
        new(() => Signals.Values.SelectMany(v => v).ToList());

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Signals { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] =
            [
                "page not found",
                "this position has been filled",
                "this job is no longer available",
                "no longer accepting applications",
                "expired",
                "position is closed",
                "has been removed",
                "does not exist"
            ],
            ["da"] =
            [
                "stillingen er besat",
                "ikke tilgængelig",
                "siden blev ikke fundet",
                "denne stilling er lukket"
            ],
            ["sv"] =
            [
                "annonsen är avslutad",
                "sidan hittades inte",
                "sidan kunde inte hittas"
            ],
            ["de"] =
            [
                "seite nicht gefunden",
                "diese stelle ist nicht mehr verfügbar",
                "nicht mehr aktiv"
            ],
            ["fr"] =
            [
                "page introuvable",
                "cette offre n'est plus disponible"
            ],
            ["cs"] =
            [
                "stránka nenalezena",
                "pozice byla obsazena",
                "stažen"
            ]
        };

    public static IReadOnlyList<string> AllSignals => _allSignals.Value;

    public static bool ContainsDeathSignal(string text, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        var signals = !string.IsNullOrWhiteSpace(language) && Signals.TryGetValue(language, out var lang)
            ? lang
            : AllSignals;

        return signals.Any(s => lower.Contains(s, StringComparison.Ordinal));
    }
}
