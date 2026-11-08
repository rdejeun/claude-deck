using System.Net;
using System.Net.Sockets;
using System.Text;
using ClaudeDeck.Core;
using ClaudeDeck.Core.Config;
using ClaudeDeck.Core.Install;
using ClaudeDeck.Core.Model;
using ClaudeDeck.Core.Platform;

namespace ClaudeDeck.Tests;

public sealed class ServerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"cd-srv-{Guid.NewGuid():N}");
    private readonly DeckHost _host;
    private readonly HttpClient _http = new();
    private readonly int _port;

    public ServerTests()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var store = new ConfigStore(_dir);
        store.Update(c => { c.Port = _port; c.Token = "secret"; c.Display.BottomRightKey = BottomRightKeys.Logo; });
        _host = new DeckHost(store, new NullPlatform(_dir), null, useHardware: false);
        _host.Start();
    }

    public void Dispose()
    {
        _host.Dispose();
        _http.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private HttpRequestMessage Post(string path, string json, string? token = "secret")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}{path}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (token is not null) req.Headers.Add(ClaudeSettingsInstaller.TokenHeader, token);
        return req;
    }

    [Fact]
    public async Task Hook_with_token_updates_sessions()
    {
        var r = await _http.SendAsync(Post("/hook", """{"session_id":"s1","hook_event_name":"UserPromptSubmit","cwd":"/w/projet"}"""));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("", await r.Content.ReadAsStringAsync());
        var s = Assert.Single(_host.Sessions.Snapshot());
        Assert.Equal(SessionState.Working, s.State);
        Assert.Equal("projet", s.DisplayName);

        // Même connexion (keep-alive) : deuxième requête.
        r = await _http.SendAsync(Post("/hook", """{"session_id":"s1","hook_event_name":"Stop"}"""));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(SessionState.Done, _host.Sessions.Snapshot()[0].State);
    }

    [Fact]
    public async Task Hook_without_token_is_rejected()
    {
        var r = await _http.SendAsync(Post("/hook", """{"session_id":"s1","hook_event_name":"Stop"}""", token: null));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        r = await _http.SendAsync(Post("/hook", """{"session_id":"s1","hook_event_name":"Stop"}""", token: "faux"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Empty(_host.Sessions.Snapshot());
    }

    [Fact]
    public async Task Foreign_host_header_is_rejected()
    {
        var req = Post("/hook", "{}");
        req.Headers.Host = "evil.example:" + _port;
        var r = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Status_line_feeds_plan_usage_and_returns_text()
    {
        var json = """
            {"session_id":"s2","model":{"display_name":"Opus 5.5"},"context_window":{"used_percentage":12},
             "rate_limits":{"five_hour":{"used_percentage":33,"resets_at":4102444800},"seven_day":{"used_percentage":7,"resets_at":4102444800}}}
            """;
        var r = await _http.SendAsync(Post("/statusline", json));
        var text = await r.Content.ReadAsStringAsync();
        Assert.Contains("Opus 5.5", text);
        Assert.Contains("5h 33%", text);
        Assert.Equal(33, _host.Plan.Snapshot().FiveHour!.UsedPercent);
    }

    [Fact]
    public async Task Raw_socket_chunked_and_expect_continue()
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _port);
        var stream = tcp.GetStream();
        var body = """{"session_id":"c1","hook_event_name":"SessionStart"}""";
        var chunked = $"{body.Length:X}\r\n{body}\r\n0\r\n\r\n";
        var head = $"POST /hook HTTP/1.1\r\nHost: 127.0.0.1:{_port}\r\n{ClaudeSettingsInstaller.TokenHeader}: secret\r\n" +
                   "Transfer-Encoding: chunked\r\nExpect: 100-continue\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(chunked));
        var response = await new StreamReader(stream).ReadToEndAsync();
        Assert.StartsWith("HTTP/1.1 100 Continue", response);
        Assert.Contains("HTTP/1.1 200 OK", response);
        Assert.Single(_host.Sessions.Snapshot(), s => s.Id == "c1");
    }

    [Fact]
    public async Task Settings_page_embeds_token_and_key_images_are_png()
    {
        var html = await _http.GetStringAsync($"http://127.0.0.1:{_port}/settings");
        Assert.Contains("const TOKEN = \"secret\"", html);
        await Task.Delay(400); // premier rendu
        var png = await _http.GetByteArrayAsync($"http://127.0.0.1:{_port}/key/0.png");
        Assert.Equal(0x89, png[0]);

        var state = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_port}/api/state")
        {
            Headers = { { ClaudeSettingsInstaller.TokenHeader, "secret" } },
        });
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        Assert.Contains("\"sessions\"", await state.Content.ReadAsStringAsync());

        var noToken = await _http.GetAsync($"http://127.0.0.1:{_port}/api/state");
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
    }
}
