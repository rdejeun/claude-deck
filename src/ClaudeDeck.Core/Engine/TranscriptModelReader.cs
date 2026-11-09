using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeDeck.Core.Engine;

/// <summary>
/// Retrouve le dernier modèle utilisé dans un transcript Claude Code (JSONL) : chaque réponse de l'assistant porte
/// <c>"message": { "model": "claude-…" }</c>. Seule la fin du fichier est lue, en partage avec Claude Code qui l'écrit.
/// </summary>
public static partial class TranscriptModelReader
{
    private const int TailBytes = 256 * 1024;

    [GeneratedRegex("\"model\"\\s*:\\s*\"(?<m>[^\"<][^\"]*)\"")]
    private static partial Regex ModelField();

    public static string? LastModel(string path)
    {
        if (!File.Exists(path)) return null;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
        var text = Encoding.UTF8.GetString(buffer, 0, read);

        // De la fin vers le début : la dernière réponse de l'agent principal (les sous-agents, marqués
        // « isSidechain », peuvent utiliser un autre modèle ; les messages synthétiques portent « <synthetic> »).
        var lines = text.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (!line.Contains("\"assistant\"", StringComparison.Ordinal)) continue;
            if (line.Contains("\"isSidechain\":true", StringComparison.Ordinal)) continue;
            foreach (Match m in ModelField().Matches(line))
            {
                var value = m.Groups["m"].Value;
                if (value.Contains("claude", StringComparison.OrdinalIgnoreCase)) return value;
            }
        }
        return null;
    }
}
