using System.Net.Sockets;
using ClaudeDeck.Core.Config;
using ClaudeDeck.Core.Util;

namespace ClaudeDeck.App;

/// <summary>
/// Point d'entrée.
/// <list type="bullet">
/// <item><c>claude-deck.exe</c> — démarre le daemon (icône dans la zone de notification).</item>
/// <item><c>claude-deck.exe statusline</c> — appelé par Claude Code (status line).</item>
/// <item><c>claude-deck.exe install</c> / <c>uninstall</c> — intégration Claude Code en ligne de commande.</item>
/// </list>
/// </summary>
internal static class Program
{
    private const string MutexName = @"Local\ClaudeDeck.Daemon";

    [STAThread]
    private static int Main(string[] args)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        switch (command)
        {
            case "statusline":
                return StatusLineCommand.Run();
            case "install":
            case "uninstall":
                return RunCli(command);
            case "--version":
            case "version":
                Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
                Console.WriteLine($"ClaudeDeck {typeof(Program).Assembly.GetName().Version}");
                return 0;
            default:
                return RunTray(restarted: args.Contains("--restarted"));
        }
    }

    private static int RunCli(string command)
    {
        Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
        Console.WriteLine();
        var config = new ConfigStore(ConfigStore.DefaultDirectory());
        Log.Init(Path.Combine(config.Directory, "logs"));
        var installer = new AppInstaller(config);
        Console.WriteLine(command == "install" ? installer.Install() : installer.Uninstall());
        return 0;
    }

    private static int RunTray(bool restarted)
    {
        using var mutex = new Mutex(initiallyOwned: false, MutexName);
        bool owned;
        try
        {
            // Après une réinstallation, l'ancienne instance a quelques secondes pour se fermer.
            owned = mutex.WaitOne(restarted ? TimeSpan.FromSeconds(15) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        var configDir = ConfigStore.DefaultDirectory();
        var firstRun = !File.Exists(Path.Combine(configDir, "config.json"));
        var config = new ConfigStore(configDir);

        if (!owned)
        {
            // Déjà lancé : on ouvre simplement les réglages de l'instance existante.
            new WindowsPlatform(configDir).OpenUrl($"http://127.0.0.1:{config.Current.Port}/settings");
            return 0;
        }

        Log.Init(Path.Combine(configDir, "logs"));
        Log.Info($"ClaudeDeck {typeof(Program).Assembly.GetName().Version} — {Environment.ProcessPath}");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.ThreadException += (_, e) => Log.Error("Exception UI", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Exception non gérée", e.ExceptionObject as Exception);

        try
        {
            Application.Run(new TrayApplication(config, firstRun));
        }
        catch (SocketException ex)
        {
            Log.Error("Port HTTP indisponible", ex);
            MessageBox.Show(
                $"Le port {config.Current.Port} est déjà utilisé.\nModifiez « port » dans {config.Path}, puis réinstallez l'intégration.",
                "ClaudeDeck", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { /* déjà libéré */ }
        }
        return 0;
    }
}
