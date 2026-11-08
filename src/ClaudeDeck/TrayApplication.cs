using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ClaudeDeck.Core;
using ClaudeDeck.Core.Config;
using ClaudeDeck.Core.Model;
using ClaudeDeck.Core.Util;
using Microsoft.Win32;

namespace ClaudeDeck.App;

/// <summary>Icône de la zone de notification et son menu ; héberge le daemon.</summary>
internal sealed class TrayApplication : ApplicationContext
{
    private readonly DeckHost _host;
    private readonly AppInstaller _installer;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly Icon _iconNormal, _iconAlert, _iconOffline;
    private bool _warnedElgato;

    public TrayApplication(ConfigStore config, bool firstRun)
    {
        var platform = new WindowsPlatform(config.Directory);
        _installer = new AppInstaller(config);
        _host = new DeckHost(config, platform, _installer, useHardware: true);

        _iconNormal = MakeIcon(Color.FromArgb(96, 165, 250));
        _iconAlert = MakeIcon(Color.FromArgb(249, 115, 22));
        _iconOffline = MakeIcon(Color.FromArgb(107, 114, 128));

        _statusItem = new ToolStripMenuItem("Démarrage…") { Enabled = false };
        _autostartItem = new ToolStripMenuItem("Démarrer avec Windows", null, (_, _) => ToggleAutostart())
        {
            Checked = AppInstaller.AutostartEnabled,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Ouvrir les réglages…", null, (_, _) => OpenSettings()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add("Aperçu des touches", null, (_, _) => platform.OpenUrl(_host.BaseUrl + "/preview"));
        menu.Items.Add("Mire de test (8 s)", null, (_, _) => _host.Deck.ShowTestPattern(TimeSpan.FromSeconds(8)));
        menu.Items.Add("Rafraîchir les jauges", null, (_, _) => _host.Usage.RefreshNow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Installer l'intégration Claude Code", null, (_, _) => Install());
        menu.Items.Add("Désinstaller l'intégration", null, (_, _) => Notify(_installer.Uninstall()));
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Ouvrir le dossier des journaux", null, (_, _) => platform.OpenUrl(Path.Combine(config.Directory, "logs")));
        menu.Items.Add("Quitter", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = _iconOffline,
            Text = "ClaudeDeck",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
        _tray.BalloonTipClicked += (_, _) => OpenSettings();

        _host.Start();

        SystemEvents.SessionSwitch += OnSessionSwitch;

        _statusTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        RefreshStatus();

        if (firstRun || !_installer.IntegrationInstalled)
        {
            _tray.ShowBalloonTip(8000, "ClaudeDeck",
                "Intégration Claude Code non installée. Cliquez ici pour ouvrir les réglages, ou utilisez le menu de l'icône.",
                ToolTipIcon.Info);
        }
    }

    private void OpenSettings() => _host.Platform.OpenUrl(_host.BaseUrl + "/settings");

    private void Install()
    {
        var msg = _installer.Install();
        _autostartItem.Checked = AppInstaller.AutostartEnabled;
        Notify(msg);
        if (!AppInstaller.RunningFromInstallLocation && File.Exists(AppInstaller.InstalledExePath))
        {
            var answer = MessageBox.Show(
                $"ClaudeDeck a été copié dans {AppInstaller.InstallDirectory}.\nRelancer depuis cet emplacement maintenant ?",
                "ClaudeDeck", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppInstaller.InstalledExePath, "--restarted")
                {
                    UseShellExecute = false,
                })?.Dispose();
                ExitThread();
            }
        }
    }

    private void ToggleAutostart()
    {
        AppInstaller.SetAutostart(!AppInstaller.AutostartEnabled);
        _autostartItem.Checked = AppInstaller.AutostartEnabled;
    }

    private void Notify(string message)
    {
        Log.Info(message);
        _tray.ShowBalloonTip(6000, "ClaudeDeck", message, ToolTipIcon.Info);
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                _host.Deck.SetLocked(true);
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                _host.Deck.SetLocked(false);
                break;
        }
    }

    private void RefreshStatus()
    {
        try
        {
            var sessions = _host.Sessions.Snapshot();
            var waiting = sessions.Count(s => s.State == SessionState.Waiting);
            var connected = _host.Deck.DeviceConnected;

            _statusItem.Text = (connected ? _host.Deck.DeviceStatus : "Stream Deck non détecté") +
                               $" · {sessions.Count} session{(sessions.Count > 1 ? "s" : "")}";
            var text = $"ClaudeDeck · {sessions.Count} session(s)" + (waiting > 0 ? $" · {waiting} en attente" : "") +
                       (connected ? "" : " · deck absent");
            _tray.Text = text.Length > 63 ? text[..63] : text;
            _tray.Icon = !connected ? _iconOffline : waiting > 0 ? _iconAlert : _iconNormal;

            if (connected && !_warnedElgato && _host.Platform.IsElgatoSoftwareRunning())
            {
                _warnedElgato = true;
                _tray.ShowBalloonTip(10000, "ClaudeDeck",
                    "Le logiciel Stream Deck d'Elgato est lancé : quittez-le (ou désactivez son démarrage automatique), sinon il écrasera l'affichage.",
                    ToolTipIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Mise à jour de l'icône", ex);
        }
    }

    /// <summary>Icône 32 px : grille 3 × 2, la première case colorée selon l'état.</summary>
    private static Icon MakeIcon(Color accent)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var bg = new SolidBrush(Color.FromArgb(22, 24, 29)))
            using (var path = RoundedRect(new RectangleF(0, 0, 31, 31), 6))
                g.FillPath(bg, path);
            const float m = 4.5f, gap = 2.5f;
            var cw = (31 - 2 * m - 2 * gap) / 3f;
            var ch = (31 - 2 * m - gap) / 2f;
            for (var i = 0; i < 6; i++)
            {
                var color = i == 0 ? accent : Color.FromArgb(120, 125, 135);
                using var b = new SolidBrush(color);
                var x = m + i % 3 * (cw + gap);
                var y = m + i / 3 * (ch + gap);
                using var cell = RoundedRect(new RectangleF(x, y, cw, ch), 1.8f);
                g.FillPath(b, cell);
            }
        }
        var h = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(h).Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _statusTimer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _host.Dispose();
        base.ExitThreadCore();
    }
}
