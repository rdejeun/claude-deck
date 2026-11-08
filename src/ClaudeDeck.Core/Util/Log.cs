namespace ClaudeDeck.Core.Util;

/// <summary>Journal minimaliste, thread-safe, écrit dans un fichier tournant (1 Mo) et sur la console de debug.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;
    private const long MaxBytes = 1024 * 1024;

    public static bool EchoToConsole { get; set; }

    public static void Init(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "claude-deck.log");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");
    public static void Debug(string message) => Write("DEBUG", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (EchoToConsole) Console.Error.WriteLine(line);
        System.Diagnostics.Debug.WriteLine(line);
        if (_path is null) return;
        lock (Gate)
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    var old = _path + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(_path, old);
                }
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch
            {
                // Le journal ne doit jamais faire tomber le daemon.
            }
        }
    }
}
