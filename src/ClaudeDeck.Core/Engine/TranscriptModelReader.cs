using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeDeck.Core.Engine;

/// <summary>Ce que l'on sait de la dernière réponse de l'agent principal.</summary>
/// <param name="ModelId">Identifiant du modèle (« claude-opus-5-5 »), ou null.</param>
/// <param name="ContextTokens">Jetons d'entrée de cette réponse (entrée + cache créé + cache lu) = remplissage du contexte.</param>
public sealed record TranscriptInfo(string? ModelId, long? ContextTokens);

/// <summary>
/// Lit la fin d'un transcript Claude Code (JSONL) : chaque réponse de l'assistant porte
/// <c>"message": { "model": "claude-…", "usage": { … } }</c>. Le fichier est ouvert en partage avec Claude Code qui l'écrit.
/// </summary>
public static partial class TranscriptModelReader
{
    private const int TailBytes = 256 * 1024;

    [GeneratedRegex("\"model\"\\s*:\\s*\"(?<m>[^\"<][^\"]*)\"")]
    private static partial Regex ModelField();

    [GeneratedRegex("\"(?<k>input_tokens|cache_creation_input_tokens|cache_read_input_tokens)\"\\s*:\\s*(?<v>\\d+)")]
    private static partial Regex UsageField();

    public static string? LastModel(string path) => Read(path)?.ModelId;

    public static TranscriptInfo? Read(string path)
    {
        if (!File.Exists(path)) return null;
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var start = Math.Max(0, fs.Length - TailBytes);
            fs.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[fs.Length - start];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = fs.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }
            text = Encoding.UTF8.GetString(buffer, 0, read);
        }

        // De la fin vers le début : la dernière réponse de l'agent principal (les sous-agents, marqués
        // « isSidechain », peuvent utiliser un autre modèle ; les messages synthétiques portent « <synthetic> »).
        var lines = text.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (!line.Contains("\"assistant\"", StringComparison.Ordinal)) continue;
            if (line.Contains("\"isSidechain\":true", StringComparison.Ordinal)) continue;

            string? model = null;
            foreach (Match m in ModelField().Matches(line))
            {
                var value = m.Groups["m"].Value;
                if (value.Contains("claude", StringComparison.OrdinalIgnoreCase))
                {
                    model = value;
                    break;
                }
            }
            if (model is null) continue;

            // Premier bloc « usage » de la ligne (celui du message ; on ne compte chaque champ qu'une fois).
            var usageAt = line.IndexOf("\"usage\"", StringComparison.Ordinal);
            long? tokens = null;
            if (usageAt >= 0)
            {
                var seen = new HashSet<string>();
                long sum = 0;
                foreach (Match u in UsageField().Matches(line, usageAt))
                {
                    if (!seen.Add(u.Groups["k"].Value)) break;
                    sum += long.Parse(u.Groups["v"].Value, CultureInfo.InvariantCulture);
                }
                if (seen.Count > 0) tokens = sum;
            }
            return new TranscriptInfo(model, tokens);
        }
        return new TranscriptInfo(null, null);
    }
}
