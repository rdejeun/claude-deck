using ClaudeDeck.Core.Config;
using ClaudeDeck.Core.Device;
using ClaudeDeck.Core.Model;
using ClaudeDeck.Core.Platform;
using ClaudeDeck.Core.Rendering;
using ClaudeDeck.Core.Usage;
using ClaudeDeck.Core.Util;

namespace ClaudeDeck.Core.Engine;

/// <summary>
/// Chef d'orchestre : compose les 6 touches à partir de l'état, les rend, les pousse au Stream Deck
/// (et à l'aperçu web), gère la luminosité et les appuis.
///
/// Disposition (Stream Deck Mini, 3 × 2) :
///   [session] [session] [session]
///   [5 h    ] [7 jours] [API    ]
/// </summary>
public sealed class DeckController : IDisposable
{
    public const int KeyCount = 6;
    public const int SessionSlots = 3;
    public const int KeyFiveHour = 3, KeySevenDay = 4, KeyBottomRight = 5;

    private readonly SessionTracker _sessions;
    private readonly PlanUsageStore _plan;
    private readonly UsageService? _usage;
    private readonly ConfigStore _config;
    private readonly DeviceManager? _devices;
    private readonly IPlatformServices _platform;
    private readonly TimeProvider _time;
    private readonly KeyRenderer _renderer = new();
    private readonly KeyViewBuilder _builder = new();
    private readonly LogoImageCache _logos = new(80);
    private readonly SlotAllocator _slots = new(SessionSlots);
    private readonly object _gate = new();

    private readonly KeyView?[] _lastViews = new KeyView?[KeyCount];
    private readonly KeyImage[] _images = new KeyImage[KeyCount];
    private readonly SessionSnapshot?[] _slotSessions = new SessionSnapshot?[SessionSlots];
    private Timer? _tick;
    private Timer? _sweep;
    private long _frame;
    private int _ticking;
    private DeviceWriter? _writer;
    private DateTimeOffset _lastActivity;
    private DateTimeOffset _testPatternUntil = DateTimeOffset.MinValue;
    private bool _locked;
    private int _appliedBrightness = -1;

    public DeckController(SessionTracker sessions, PlanUsageStore plan, UsageService? usage, ConfigStore config,
        DeviceManager? devices, IPlatformServices platform, TimeProvider? time = null)
    {
        _sessions = sessions;
        _plan = plan;
        _usage = usage;
        _config = config;
        _devices = devices;
        _platform = platform;
        _time = time ?? TimeProvider.System;
        _lastActivity = _time.GetUtcNow();

        var blank = KeyImage.Solid(_renderer.Size, 0, 0, 0);
        for (var i = 0; i < KeyCount; i++) _images[i] = blank;

        _sessions.Changed += () => _lastActivity = _time.GetUtcNow();

        if (_devices is not null)
        {
            _devices.Connected += OnDeviceConnected;
            _devices.DisconnectedEvent += OnDeviceDisconnected;
            _devices.KeyStateChanged += (key, pressed) => { if (pressed) Press(key); };
        }
    }

    /// <summary>Incrémenté à chaque changement d'image (aperçu web).</summary>
    public long Version { get; private set; }

    public string DeviceStatus => _devices?.Current?.Description ?? "Stream Deck non détecté";
    public bool DeviceConnected => _devices?.Current is not null;

    public event Action? DeviceStatusChanged;

    public KeyImage GetImage(int key)
    {
        lock (_gate) return _images[key];
    }

    public void Start()
    {
        var fps = Math.Clamp(_config.Current.Display.AnimationFps, 1, 10);
        _tick = new Timer(_ => Tick(), null, 0, 1000 / fps);
        _sweep = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    /// <summary>Affiche la mire de test pendant quelques secondes (vérification de l'orientation).</summary>
    public void ShowTestPattern(TimeSpan duration)
    {
        _testPatternUntil = _time.GetUtcNow() + duration;
        _lastActivity = _time.GetUtcNow();
    }

    /// <summary>Session Windows verrouillée : écran éteint si l'option est active.</summary>
    public void SetLocked(bool locked)
    {
        _locked = locked;
        if (!locked) _lastActivity = _time.GetUtcNow();
    }

    /// <summary>Appui sur une touche (physique ou depuis l'aperçu web).</summary>
    public void Press(int key)
    {
        var now = _time.GetUtcNow();
        var wasDimmed = IsDimmed(now);
        _lastActivity = now;
        if (wasDimmed) return; // Le premier appui réveille seulement l'écran.

        if (key < SessionSlots)
        {
            SessionSnapshot? s;
            lock (_gate) s = _slotSessions[key];
            if (s is null) return;
            try
            {
                _platform.FocusSession(s);
            }
            catch (Exception ex)
            {
                Log.Error("Mise au premier plan", ex);
            }
            _sessions.Acknowledge(s.Id);
        }
        else if (key != KeyBottomRight || _config.Current.Display.BottomRightKey == BottomRightKeys.Api)
        {
            _usage?.RefreshNow();
        }
    }

    private void Sweep()
    {
        try
        {
            _sessions.DoneFadeAfter = TimeSpan.FromMinutes(Math.Max(0, _config.Current.Display.DoneFadeMinutes));
            _sessions.Sweep(c =>
            {
                try { return _platform.IsAlive(c); }
                catch { return true; }
            });
        }
        catch (Exception ex)
        {
            Log.Error("Maintenance des sessions", ex);
        }
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            var cfg = _config.Current;
            var fps = Math.Clamp(cfg.Display.AnimationFps, 1, 10);
            var now = _time.GetUtcNow();
            var frame = _frame++;
            _builder.StaleAfter = TimeSpan.FromMinutes(Math.Max(1, cfg.PlanUsage.StaleAfterMinutes));

            var views = BuildViews(now, frame, fps, cfg);
            var changed = false;
            for (var k = 0; k < KeyCount; k++)
            {
                if (views[k] == _lastViews[k]) continue;
                var img = _renderer.Render(views[k]);
                bool keyChanged;
                lock (_gate)
                {
                    _lastViews[k] = views[k];
                    keyChanged = img.Hash != _images[k].Hash;
                    if (keyChanged) _images[k] = img;
                }
                if (!keyChanged) continue;
                changed = true;
                _writer?.Submit(k, img);
            }
            if (changed) Version++;

            ApplyBrightness(now, cfg);
        }
        catch (Exception ex)
        {
            Log.Error("Rendu des touches", ex);
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    internal KeyView[] BuildViews(DateTimeOffset now, long frame, int fps, DeckConfig cfg)
    {
        var views = new KeyView[KeyCount];
        if (now < _testPatternUntil)
        {
            for (var k = 0; k < KeyCount; k++) views[k] = KeyViewBuilder.TestPattern(k);
            return views;
        }

        var alloc = _slots.Allocate(_sessions.Snapshot());
        lock (_gate)
        {
            for (var i = 0; i < SessionSlots; i++) _slotSessions[i] = alloc.Slots[i];
        }
        for (var i = 0; i < SessionSlots; i++)
        {
            // Le compteur de sessions masquées s'affiche sur la dernière touche de la ligne.
            var last = i == SessionSlots - 1;
            views[i] = _builder.Session(alloc.Slots[i], now, frame, fps,
                last ? alloc.HiddenCount : 0, last ? alloc.HiddenWaiting : 0);
        }

        var plan = _plan.Snapshot();
        views[KeyFiveHour] = _builder.PlanWindow("SESSION · 5 H", plan.FiveHour, now, weekly: false);
        views[KeySevenDay] = _builder.PlanWindow("SEMAINE", plan.SevenDay, now, weekly: true);
        views[KeyBottomRight] = cfg.Display.BottomRightKey == BottomRightKeys.Api && _usage is not null
            ? _builder.Api(_usage.Api, cfg.Api.LowBalanceUsd, now)
            : KeyViewBuilder.Logo(_logos.Get(cfg.Display.LogoImagePath));
        return views;
    }

    private bool AnyUrgent()
    {
        foreach (var s in _sessions.Snapshot())
            if (s.State is SessionState.Waiting or SessionState.Working or SessionState.Compacting) return true;
        return false;
    }

    private bool IsDimmed(DateTimeOffset now)
    {
        var d = _config.Current.Display;
        if (d.DimAfterMinutes <= 0) return false;
        return now - _lastActivity > TimeSpan.FromMinutes(d.DimAfterMinutes) && !AnyUrgent();
    }

    private void ApplyBrightness(DateTimeOffset now, DeckConfig cfg)
    {
        var target = _locked && cfg.Display.OffWhenLocked ? 0
            : IsDimmed(now) ? cfg.Display.DimBrightness
            : cfg.Display.Brightness;
        if (target == _appliedBrightness) return;
        _appliedBrightness = target;
        _writer?.SetBrightness(target);
    }

    private void OnDeviceConnected(IDeckDevice device)
    {
        Log.Info($"Connecté : {device.Description}");
        var writer = new DeviceWriter(device);
        Interlocked.Exchange(ref _writer, writer)?.Dispose();
        _appliedBrightness = -1;
        lock (_gate)
        {
            for (var k = 0; k < KeyCount; k++) writer.Submit(k, _images[k]);
        }
        if (_platform.IsElgatoSoftwareRunning())
            Log.Warn("Le logiciel Stream Deck d'Elgato est lancé : fermez-le, sinon il écrasera l'affichage.");
        DeviceStatusChanged?.Invoke();
    }

    private void OnDeviceDisconnected()
    {
        Interlocked.Exchange(ref _writer, null)?.Dispose();
        DeviceStatusChanged?.Invoke();
    }

    public void Dispose()
    {
        _tick?.Dispose();
        _sweep?.Dispose();
        _writer?.Dispose();
        _renderer.Dispose();
    }

    /// <summary>
    /// Thread d'écriture USB : ne garde que la dernière image demandée par touche. Si le périphérique est plus lent que
    /// l'animation, les images intermédiaires sont simplement sautées.
    /// </summary>
    private sealed class DeviceWriter : IDisposable
    {
        private readonly IDeckDevice _device;
        private readonly KeyImage?[] _pending = new KeyImage?[KeyCount];
        private readonly ulong[] _sent = new ulong[KeyCount];
        private readonly AutoResetEvent _signal = new(false);
        private readonly Thread _thread;
        private readonly object _gate = new();
        private int _brightness = -1;
        private volatile bool _stop;

        public DeviceWriter(IDeckDevice device)
        {
            _device = device;
            _thread = new Thread(Run) { IsBackground = true, Name = "StreamDeck output" };
            _thread.Start();
        }

        public void Submit(int key, KeyImage image)
        {
            if (key >= _device.KeyCount) return;
            lock (_gate) _pending[key] = image;
            _signal.Set();
        }

        public void SetBrightness(int percent)
        {
            Volatile.Write(ref _brightness, percent);
            _signal.Set();
        }

        private void Run()
        {
            while (!_stop)
            {
                _signal.WaitOne(1000);
                if (_stop) break;
                try
                {
                    var b = Interlocked.Exchange(ref _brightness, -1);
                    if (b >= 0) _device.SetBrightness(b);

                    for (var k = 0; k < KeyCount && !_stop; k++)
                    {
                        KeyImage? img;
                        lock (_gate)
                        {
                            img = _pending[k];
                            _pending[k] = null;
                        }
                        if (img is null || img.Hash == _sent[k]) continue;
                        _device.SetKeyImage(k, img);
                        _sent[k] = img.Hash;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Écriture vers le Stream Deck : {ex.Message}");
                    Thread.Sleep(500);
                }
            }
        }

        public void Dispose()
        {
            _stop = true;
            _signal.Set();
            _thread.Join(TimeSpan.FromSeconds(2));
            _signal.Dispose();
        }
    }
}
