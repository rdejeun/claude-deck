using ClaudeDeck.Core.Config;
using ClaudeDeck.Core.Device;
using ClaudeDeck.Core.Engine;
using ClaudeDeck.Core.Platform;
using ClaudeDeck.Core.Server;
using ClaudeDeck.Core.Usage;
using ClaudeDeck.Core.Util;

namespace ClaudeDeck.Core;

/// <summary>Racine de composition du daemon : partagée par l'application Windows et le simulateur.</summary>
public sealed class DeckHost : IDisposable
{
    private readonly HttpClient _http;

    public DeckHost(ConfigStore config, IPlatformServices platform, IHostActions? hostActions, bool useHardware, TimeProvider? time = null)
    {
        Config = config;
        Platform = platform;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        Sessions = new SessionTracker(time);
        Plan = new PlanUsageStore(Path.Combine(config.Directory, "plan-usage.json"));
        Usage = new UsageService(config, platform.Secrets, Plan, _http, time);
        Devices = useHardware ? new DeviceManager(() => Config.Current.Device) : null;
        Deck = new DeckController(Sessions, Plan, Usage, config, Devices, platform, time);
        Api = new DeckApi(config.Current.Port, config, Sessions, Plan, Usage, Deck, platform, hostActions, time);
        Server = new MiniHttpServer(config.Current.Port, Api.HandleAsync);
    }

    public ConfigStore Config { get; }
    public IPlatformServices Platform { get; }
    public SessionTracker Sessions { get; }
    public PlanUsageStore Plan { get; }
    public UsageService Usage { get; }
    public DeviceManager? Devices { get; }
    public DeckController Deck { get; }
    public DeckApi Api { get; }
    public MiniHttpServer Server { get; }

    public string BaseUrl => $"http://127.0.0.1:{Server.Port}";

    /// <summary>Démarre tout. Lève une exception si le port est déjà utilisé (autre instance ?).</summary>
    public void Start()
    {
        Server.Start();
        Deck.Start();
        Usage.Start();
        Devices?.Start();
        Log.Info("ClaudeDeck démarré");
    }

    public void Dispose()
    {
        Server.Dispose();
        Deck.Dispose();
        Devices?.Dispose();
        Usage.Dispose();
        _http.Dispose();
    }
}
