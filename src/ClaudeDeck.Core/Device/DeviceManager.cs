using ClaudeDeck.Core.Config;
using ClaudeDeck.Core.Util;
using HidSharp;

namespace ClaudeDeck.Core.Device;

/// <summary>
/// Maintient la connexion au Stream Deck : détection au branchement, reconnexion après veille ou débranchement.
/// </summary>
public sealed class DeviceManager : IDisposable
{
    private readonly Func<DeviceConfig> _config;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private IDeckDevice? _current;
    private bool _disposed;

    public DeviceManager(Func<DeviceConfig> config)
    {
        _config = config;
        _timer = new Timer(_ => TryConnect(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public IDeckDevice? Current
    {
        get { lock (_gate) return _current; }
    }

    public event Action<IDeckDevice>? Connected;
    public event Action? DisconnectedEvent;
    public event Action<int, bool>? KeyStateChanged;

    public void Start()
    {
        try
        {
            DeviceList.Local.Changed += (_, _) => _timer.Change(500, Timeout.Infinite);
        }
        catch (Exception ex)
        {
            Log.Warn($"Surveillance USB indisponible : {ex.Message}");
        }
        _timer.Change(0, Timeout.Infinite);
    }

    private void TryConnect()
    {
        IDeckDevice? connected;
        lock (_gate)
        {
            if (_disposed) return;
            if (_current is not null) return;
            try
            {
                var deck = StreamDeckMini.TryOpenFirst(_config());
                if (deck is null)
                {
                    // Nouvelle tentative périodique, en plus des notifications de changement USB.
                    _timer.Change(5000, Timeout.Infinite);
                    return;
                }
                deck.KeyStateChanged += OnKey;
                deck.Disconnected += () => OnDisconnected(deck);
                _current = deck;
                connected = deck;
            }
            catch (Exception ex)
            {
                Log.Error("Recherche du Stream Deck", ex);
                _timer.Change(5000, Timeout.Infinite);
                return;
            }
        }
        Connected?.Invoke(connected);
    }

    private void OnKey(int key, bool pressed) => KeyStateChanged?.Invoke(key, pressed);

    private void OnDisconnected(IDeckDevice deck)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, deck)) return;
            _current = null;
            try { deck.Dispose(); } catch { /* déjà parti */ }
            if (!_disposed) _timer.Change(2000, Timeout.Infinite);
        }
        Log.Info("Stream Deck déconnecté");
        DisconnectedEvent?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
            _current?.Dispose();
            _current = null;
        }
    }
}
