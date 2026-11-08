using ClaudeDeck.Core.Config;
using ClaudeDeck.Core.Rendering;
using ClaudeDeck.Core.Util;
using HidSharp;

namespace ClaudeDeck.Core.Device;

/// <summary>
/// Pilote en espace utilisateur du Stream Deck Mini (et Mini MK.2), via le pilote HID générique de Windows :
/// aucun logiciel ni pilote Elgato requis.
///
/// Protocole (identique à python-elgato-streamdeck, MIT) :
/// <list type="bullet">
/// <item>Images : rapports de sortie 0x02 de 1024 octets = en-tête 16 octets + 1008 octets de BMP.</item>
/// <item>Luminosité : rapport de fonction 0x05 55 AA D1 01 &lt;pourcentage&gt;.</item>
/// <item>Réinitialisation : rapport de fonction 0x0B 0x63.</item>
/// <item>Touches : rapport d'entrée 0x01 suivi d'un octet (0/1) par touche.</item>
/// </list>
/// </summary>
public sealed class StreamDeckMini : IDeckDevice
{
    public const int VendorId = 0x0FD9;
    private const int HeaderLength = 16;

    private readonly HidDevice _device;
    private readonly HidStream _stream;
    private readonly DeviceConfig _config;
    private readonly object _writeGate = new();
    private readonly int _outputReportLength;
    private readonly int _featureReportLength;
    private readonly Thread _reader;
    private readonly bool[] _keyStates = new bool[6];
    private volatile bool _disposed;
    private int _disconnectRaised;

    private StreamDeckMini(HidDevice device, HidStream stream, DeviceConfig config)
    {
        _device = device;
        _stream = stream;
        _config = config;

        var maxOut = SafeLength(device.GetMaxOutputReportLength);
        _outputReportLength = maxOut > HeaderLength ? maxOut : 1024;
        _featureReportLength = Math.Max(17, SafeLength(device.GetMaxFeatureReportLength));

        _stream.ReadTimeout = Timeout.Infinite;
        _stream.WriteTimeout = 3000;

        Description = $"Stream Deck Mini (PID 0x{device.ProductID:X4})";
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "StreamDeck input" };
    }

    public string Description { get; }
    public int KeyCount => 6;
    public int Columns => 3;
    public int KeyPixelSize => 80;

    public event Action<int, bool>? KeyStateChanged;
    public event Action? Disconnected;

    /// <summary>Énumère les Stream Deck Mini branchés.</summary>
    public static IReadOnlyList<HidDevice> Enumerate(DeviceConfig config)
    {
        var pids = config.ParsedProductIds;
        return DeviceList.Local.GetHidDevices(VendorId).Where(d => pids.Contains(d.ProductID)).ToList();
    }

    /// <summary>Ouvre le premier Stream Deck Mini disponible, ou null.</summary>
    public static StreamDeckMini? TryOpenFirst(DeviceConfig config)
    {
        foreach (var dev in Enumerate(config))
        {
            try
            {
                if (!dev.TryOpen(out HidStream stream)) continue;
                var deck = new StreamDeckMini(dev, stream, config);
                deck.Initialize();
                return deck;
            }
            catch (Exception ex)
            {
                Log.Warn($"Ouverture de {dev.DevicePath} impossible : {ex.Message}");
            }
        }
        return null;
    }

    private void Initialize()
    {
        // Réinitialise le flux d'images (un rapport 0x02 vide) puis démarre la lecture des touches.
        var reset = new byte[_outputReportLength];
        reset[0] = 0x02;
        Write(reset);
        _reader.Start();
        Log.Info($"{Description} ouvert (rapport sortie {_outputReportLength} o, fonction {_featureReportLength} o)");
    }

    public void SetBrightness(int percent)
    {
        var buf = new byte[_featureReportLength];
        buf[0] = 0x05; buf[1] = 0x55; buf[2] = 0xAA; buf[3] = 0xD1; buf[4] = 0x01;
        buf[5] = (byte)Math.Clamp(percent, 0, 100);
        Guard(() => _stream.SetFeature(buf));
    }

    public void SetKeyImage(int key, KeyImage image)
    {
        if (key < 0 || key >= KeyCount) throw new ArgumentOutOfRangeException(nameof(key));
        var bmp = MiniImageEncoder.ToNativeBmp(image, _config.Rotation, _config.FlipHorizontal, _config.FlipVertical);
        var payloadLength = _outputReportLength - HeaderLength;

        lock (_writeGate)
        {
            var page = 0;
            var sent = 0;
            while (sent < bmp.Length)
            {
                var chunk = Math.Min(payloadLength, bmp.Length - sent);
                var report = new byte[_outputReportLength];
                report[0] = 0x02;
                report[1] = 0x01;
                report[2] = (byte)page;
                report[3] = 0x00;
                report[4] = (byte)(sent + chunk == bmp.Length ? 1 : 0);
                report[5] = (byte)(key + 1);
                Buffer.BlockCopy(bmp, sent, report, HeaderLength, chunk);
                Write(report);
                sent += chunk;
                page++;
            }
        }
    }

    private void Write(byte[] report) => Guard(() => _stream.Write(report, 0, report.Length));

    private void Guard(Action io)
    {
        if (_disposed) return;
        try
        {
            lock (_writeGate) io();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException or InvalidOperationException)
        {
            Log.Warn($"{Description} : erreur d'écriture ({ex.Message})");
            RaiseDisconnected();
        }
    }

    private void ReadLoop()
    {
        var buf = new byte[Math.Max(1 + KeyCount, SafeLength(_device.GetMaxInputReportLength))];
        while (!_disposed)
        {
            int n;
            try
            {
                n = _stream.Read(buf, 0, buf.Length);
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (Exception ex)
            {
                if (!_disposed) Log.Warn($"{Description} : lecture interrompue ({ex.Message})");
                RaiseDisconnected();
                return;
            }

            if (n < 1 + KeyCount || buf[0] != 0x01) continue;
            for (var k = 0; k < KeyCount; k++)
            {
                var pressed = buf[1 + k] != 0;
                if (pressed == _keyStates[k]) continue;
                _keyStates[k] = pressed;
                try { KeyStateChanged?.Invoke(k, pressed); }
                catch (Exception ex) { Log.Error("Traitement d'une touche", ex); }
            }
        }
    }

    private void RaiseDisconnected()
    {
        if (Interlocked.Exchange(ref _disconnectRaised, 1) == 0) Disconnected?.Invoke();
    }

    private static int SafeLength(Func<int> f)
    {
        try { return f(); } catch { return 0; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (Volatile.Read(ref _disconnectRaised) == 0)
        try
        {
            // Écran noir et rétroéclairage faible à la fermeture.
            var black = KeyImage.Solid(KeyPixelSize, 0, 0, 0);
            for (var k = 0; k < KeyCount; k++) SetKeyImage(k, black);
            SetBrightness(5);
        }
        catch { /* périphérique déjà parti */ }
        _disposed = true;
        try { _stream.Dispose(); } catch { /* ignoré */ }
    }
}
