using System.Net;
using System.Net.Sockets;
using System.Text;
using ClaudeDeck.Core.Util;

namespace ClaudeDeck.Core.Server;

public sealed class HttpRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required string Query { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public required byte[] Body { get; init; }
    public required IPEndPoint Remote { get; init; }
    /// <summary>Données attachées à la connexion TCP (ex. processus client résolu une fois).</summary>
    public required Dictionary<string, object?> ConnectionItems { get; init; }

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    public string BodyText => Encoding.UTF8.GetString(Body);
}

public sealed record HttpResponse(int Status, string ContentType, byte[] Body, IReadOnlyDictionary<string, string>? Headers = null)
{
    public static HttpResponse Empty(int status = 200) => new(status, "text/plain; charset=utf-8", []);
    public static HttpResponse Text(string text, int status = 200) => new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));
    public static HttpResponse Json(string json, int status = 200) => new(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
    public static HttpResponse Html(string html) => new(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
}

/// <summary>
/// Petit serveur HTTP/1.1 sur 127.0.0.1 uniquement, sans dépendance (HttpListener exigerait des droits
/// administrateur pour 127.0.0.1 sous Windows). Gère keep-alive, Content-Length, chunked et Expect: 100-continue.
/// </summary>
public sealed class MiniHttpServer : IDisposable
{
    private const int MaxHeaderBytes = 32 * 1024;
    private const int MaxBodyBytes = 64 * 1024 * 1024;

    private readonly TcpListener _listener;
    private readonly Func<HttpRequest, Task<HttpResponse>> _handler;
    private readonly CancellationTokenSource _cts = new();

    public MiniHttpServer(int port, Func<HttpRequest, Task<HttpResponse>> handler)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _handler = handler;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start(64);
        _ = Task.Run(AcceptLoop);
        Log.Info($"Serveur HTTP à l'écoute sur http://127.0.0.1:{Port}/");
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"accept : {ex.Message}");
                continue;
            }
            _ = Task.Run(() => HandleConnection(client));
        }
    }

    private async Task HandleConnection(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true;
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        var items = new Dictionary<string, object?>();
        try
        {
            var stream = client.GetStream();
            var reader = new BufferedReader(stream);
            while (!_cts.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(60));

                var head = await reader.ReadHeadAsync(MaxHeaderBytes, idle.Token).ConfigureAwait(false);
                if (head is null) return; // connexion fermée proprement

                var lines = head.Split("\r\n");
                var requestLine = lines[0].Split(' ');
                if (requestLine.Length < 3)
                {
                    await WriteResponse(stream, HttpResponse.Text("Bad Request", 400), close: true).ConfigureAwait(false);
                    return;
                }

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
                }

                if (headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
                {
                    var cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                    await stream.WriteAsync(cont, idle.Token).ConfigureAwait(false);
                }

                byte[] body;
                if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    body = await reader.ReadChunkedAsync(MaxBodyBytes, idle.Token).ConfigureAwait(false);
                }
                else if (headers.TryGetValue("Content-Length", out var cl) && long.TryParse(cl, out var len) && len > 0)
                {
                    if (len > MaxBodyBytes)
                    {
                        await WriteResponse(stream, HttpResponse.Text("Payload Too Large", 413), close: true).ConfigureAwait(false);
                        return;
                    }
                    body = await reader.ReadExactAsync((int)len, idle.Token).ConfigureAwait(false);
                }
                else
                {
                    body = [];
                }

                var target = requestLine[1];
                var q = target.IndexOf('?');
                var request = new HttpRequest
                {
                    Method = requestLine[0].ToUpperInvariant(),
                    Path = Uri.UnescapeDataString(q >= 0 ? target[..q] : target),
                    Query = q >= 0 ? target[(q + 1)..] : "",
                    Headers = headers,
                    Body = body,
                    Remote = remote,
                    ConnectionItems = items,
                };

                HttpResponse response;
                try
                {
                    response = await _handler(request).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error($"{request.Method} {request.Path}", ex);
                    response = HttpResponse.Text("Internal Server Error", 500);
                }

                var close = (headers.TryGetValue("Connection", out var conn) && conn.Equals("close", StringComparison.OrdinalIgnoreCase))
                            || requestLine[2] == "HTTP/1.0";
                await WriteResponse(stream, response, close).ConfigureAwait(false);
                if (close) return;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or InvalidDataException or ObjectDisposedException)
        {
            // Client parti, délai d'inactivité ou requête invalide : on ferme simplement.
        }
        catch (Exception ex)
        {
            Log.Error("Connexion HTTP", ex);
        }
    }

    private static async Task WriteResponse(Stream stream, HttpResponse r, bool close)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(r.Status).Append(' ').Append(Reason(r.Status)).Append("\r\n");
        sb.Append("Content-Type: ").Append(r.ContentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(r.Body.Length).Append("\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        sb.Append("X-Content-Type-Options: nosniff\r\n");
        if (r.Headers is not null)
            foreach (var (k, v) in r.Headers) sb.Append(k).Append(": ").Append(v).Append("\r\n");
        sb.Append(close ? "Connection: close\r\n" : "Connection: keep-alive\r\n");
        sb.Append("\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(head).ConfigureAwait(false);
        if (r.Body.Length > 0) await stream.WriteAsync(r.Body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        413 => "Payload Too Large",
        _ => status >= 500 ? "Internal Server Error" : "Status",
    };

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* ignoré */ }
        _cts.Dispose();
    }

    /// <summary>Lecteur avec tampon pour parser en-têtes et corps sur le même flux.</summary>
    private sealed class BufferedReader
    {
        private readonly Stream _stream;
        private readonly byte[] _buf = new byte[16 * 1024];
        private int _start, _end;

        public BufferedReader(Stream stream) => _stream = stream;

        private async Task<bool> FillAsync(CancellationToken ct)
        {
            if (_start > 0 && _start == _end) _start = _end = 0;
            if (_end == _buf.Length)
            {
                // Compacte le tampon.
                Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
                _end -= _start;
                _start = 0;
                if (_end == _buf.Length) throw new InvalidDataException("Ligne trop longue");
            }
            var n = await _stream.ReadAsync(_buf.AsMemory(_end), ct).ConfigureAwait(false);
            if (n == 0) return false;
            _end += n;
            return true;
        }

        /// <summary>Lit jusqu'à la ligne vide. Retourne null si la connexion est fermée avant toute donnée.</summary>
        public async Task<string?> ReadHeadAsync(int max, CancellationToken ct)
        {
            var acc = new MemoryStream();
            var matched = 0; // progression dans \r\n\r\n
            while (true)
            {
                if (_start == _end && !await FillAsync(ct).ConfigureAwait(false))
                {
                    if (acc.Length == 0) return null;
                    throw new IOException("Connexion fermée au milieu des en-têtes");
                }
                while (_start < _end)
                {
                    var b = _buf[_start++];
                    acc.WriteByte(b);
                    matched = b switch
                    {
                        (byte)'\r' when matched is 0 or 2 => matched + 1,
                        (byte)'\n' when matched is 1 or 3 => matched + 1,
                        (byte)'\r' => 1,
                        _ => 0,
                    };
                    if (matched == 4)
                    {
                        var text = Encoding.ASCII.GetString(acc.GetBuffer(), 0, (int)acc.Length - 4);
                        // Tolère des lignes vides parasites entre deux requêtes keep-alive.
                        return text.TrimStart('\r', '\n');
                    }
                    if (acc.Length > max) throw new InvalidDataException("En-têtes trop longs");
                }
            }
        }

        public async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
        {
            var result = new byte[count];
            var off = 0;
            while (off < count)
            {
                if (_start == _end && !await FillAsync(ct).ConfigureAwait(false))
                    throw new IOException("Corps incomplet");
                var n = Math.Min(count - off, _end - _start);
                Buffer.BlockCopy(_buf, _start, result, off, n);
                _start += n;
                off += n;
            }
            return result;
        }

        private async Task<string> ReadLineAsync(CancellationToken ct)
        {
            var sb = new StringBuilder();
            while (true)
            {
                if (_start == _end && !await FillAsync(ct).ConfigureAwait(false)) throw new IOException("Fin de flux");
                var b = _buf[_start++];
                if (b == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append((char)b);
                if (sb.Length > 1024) throw new InvalidDataException("Ligne de bloc trop longue");
            }
        }

        public async Task<byte[]> ReadChunkedAsync(int max, CancellationToken ct)
        {
            var acc = new MemoryStream();
            while (true)
            {
                var sizeLine = await ReadLineAsync(ct).ConfigureAwait(false);
                var semi = sizeLine.IndexOf(';');
                if (semi >= 0) sizeLine = sizeLine[..semi];
                var size = Convert.ToInt32(sizeLine.Trim(), 16);
                if (size == 0)
                {
                    // Trailers éventuels jusqu'à la ligne vide.
                    while ((await ReadLineAsync(ct).ConfigureAwait(false)).Length > 0) { }
                    return acc.ToArray();
                }
                if (acc.Length + size > max) throw new InvalidDataException("Corps trop volumineux");
                var chunk = await ReadExactAsync(size, ct).ConfigureAwait(false);
                acc.Write(chunk);
                await ReadLineAsync(ct).ConfigureAwait(false); // CRLF après le bloc
            }
        }
    }
}
