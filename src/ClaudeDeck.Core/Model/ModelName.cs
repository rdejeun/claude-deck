using System.Globalization;

namespace ClaudeDeck.Core.Model;

/// <summary>Transforme un identifiant de modèle (« claude-opus-5-5 », « claude-sonnet-4-5-20250929 »…) en nom lisible (« Opus 5.5 »).</summary>
public static class ModelName
{
    public static string? Format(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var raw = id.Trim();

        // Suffixes de contexte étendu (« [1m] ») et préfixes de fournisseur (« us.anthropic. »).
        var bracket = raw.IndexOf('[');
        if (bracket > 0) raw = raw[..bracket];
        var claude = raw.LastIndexOf("claude-", StringComparison.OrdinalIgnoreCase);
        if (claude >= 0) raw = raw[(claude + "claude-".Length)..];

        string? family = null;
        var version = new List<string>();
        foreach (var token in raw.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.All(char.IsLetter))
            {
                family ??= token;
            }
            else if (token.All(char.IsDigit) && token.Length <= 2)
            {
                version.Add(token);
            }
            // Dates (20250929), révisions (v1:0) : ignorées.
        }

        if (family is null) return id.Trim();
        var name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(family.ToLowerInvariant());
        return version.Count == 0 ? name : $"{name} {string.Join('.', version)}";
    }
}
