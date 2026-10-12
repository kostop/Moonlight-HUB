using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MoonlightHub.Core;

/// <summary>What the other computer's hub reports about itself.</summary>
public sealed record PeerHubInfo(string Screen, string Os, string Version, DeskflowRole Role, DeskflowRole Running, List<string> Peers,
    double DpiScale = 0, int DesktopWidth = 0, int DesktopHeight = 0, List<SharedMonitor>? Monitors = null)
{
    public string Text => $"{Screen} ({Os}, v{Version}) · {DeskflowManager.RoleName(Running)}" + (Peers.Count > 0 ? " · 已连接 " + string.Join(", ", Peers) : string.Empty);
}

/// <summary>
/// Links the Moonlight Hubs of the two computers so that one click switches both: when this computer becomes a client,
/// the other hub is first told to become the server (and waits until it listens); when it becomes the server, the other
/// hub is told to connect as a client (to the address it was contacted from). Requests carry the Deskflow side-channel
/// token that both computers already share.
/// </summary>
public sealed class PeerLink : IDisposable
{
    public const int Port = 24990;
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(45) };
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly DeskflowManager _deskflow;
    private HttpListener? _listener;
    private volatile bool _stopping;

    public string ListenError { get; private set; } = string.Empty;
    public bool Listening => _listener?.IsListening == true;

    public PeerLink(DeskflowManager deskflow) => _deskflow = deskflow;

    private static string Token()
    {
        try { return File.Exists(DeskflowPeer.TokenFile) ? File.ReadAllText(DeskflowPeer.TokenFile).Trim() : string.Empty; }
        catch { return string.Empty; }
    }

    // ------------------------------------------------------------------ server

    public void Start()
    {
        if (_listener != null) return;
        if (Token().Length == 0)
        {
            ListenError = "没有共享 token（" + DeskflowPeer.TokenFile + "），不接受对端联动请求";
            Log.Warn("Deskflow 联动: " + ListenError);
            return;
        }
        try
        {
            if (OperatingSystem.IsWindows()) EnsureFirewallRule();
            var listener = new HttpListener();
            // Windows: the "+" wildcard needs administrator rights (the hub runs elevated); Linux: the managed listener binds all addresses
            listener.Prefixes.Add(OperatingSystem.IsWindows() ? $"http://+:{Port}/" : $"http://*:{Port}/");
            listener.Start();
            _listener = listener;
            ListenError = string.Empty;
            new Thread(AcceptLoop) { IsBackground = true, Name = "PeerLink" }.Start();
            Log.Info($"Deskflow 联动: 监听端口 {Port}");
        }
        catch (Exception ex)
        {
            ListenError = $"无法监听端口 {Port}: {ex.Message}";
            Log.Warn("Deskflow 联动: " + ListenError);
        }
    }

    private static void EnsureFirewallRule()
    {
        const string name = "Moonlight Hub 键鼠联动";
        var (code, _) = ProcessUtil.Run("netsh.exe", $"advfirewall firewall show rule name=\"{name}\"", 15000);
        if (code == 0) return;
        var (addCode, output) = ProcessUtil.Run("netsh.exe", $"advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol=TCP localport={Port} profile=any", 15000);
        Log.Info($"Deskflow 联动: 添加防火墙规则 {(addCode == 0 ? "ok" : output.Trim())}");
    }

    private void AcceptLoop()
    {
        while (!_stopping && _listener is { IsListening: true } listener)
        {
            HttpListenerContext context;
            try { context = listener.GetContext(); }
            catch { if (_stopping) return; Thread.Sleep(500); continue; }
            _ = Task.Run(() => Handle(context));
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            var supplied = (request.Headers["Authorization"] ?? string.Empty).Replace("Bearer ", string.Empty).Trim();
            var token = Token();
            if (token.Length == 0 || !CryptographicEquals(token, supplied))
            {
                await Reply(response, 401, new { error = "unauthorized" }).ConfigureAwait(false);
                return;
            }
            var path = request.Url?.AbsolutePath ?? "/";
            if (request.HttpMethod == "GET" && path == "/api/hub/status")
            {
                await Reply(response, 200, StatusPayload()).ConfigureAwait(false);
                return;
            }
            if (OperatingSystem.IsWindows() && path is "/api/hub/display-layout" or "/api/hub/displays")
            {
                var hub = Hub.Current ?? throw new InvalidOperationException("Hub is starting");
                if (request.HttpMethod == "GET")
                {
                    if (path == "/api/hub/displays") await Reply(response, 200, SharedDisplayLayout.LocalMonitors(hub)).ConfigureAwait(false);
                    else if (hub.Deskflow.Settings.Role == DeskflowRole.Server)
                        await Reply(response, 200, await SharedDisplayLayout.SnapshotAsync(hub).ConfigureAwait(false)).ConfigureAwait(false);
                    else await Reply(response, 409, new { error = "Shared layout is owned by the Deskflow server" }).ConfigureAwait(false);
                    return;
                }
                if (request.HttpMethod == "POST")
                {
                    using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync().ConfigureAwait(false);
                    if (path == "/api/hub/displays")
                    {
                        var positions = JsonSerializer.Deserialize<List<MonitorPlacement>>(text, Json) ?? throw new InvalidDataException("Positions missing");
                        await Reply(response, 200, await SharedDisplayLayout.ApplyLocalAsync(hub, positions).ConfigureAwait(false)).ConfigureAwait(false);
                    }
                    else
                    {
                        if (hub.Deskflow.Settings.Role != DeskflowRole.Server) { await Reply(response, 409, new { error = "Only the Deskflow server can commit layouts" }).ConfigureAwait(false); return; }
                        var draft = JsonSerializer.Deserialize<SharedDisplayDocument>(text, Json) ?? throw new InvalidDataException("Layout missing");
                        await Reply(response, 200, await SharedDisplayLayout.ApplyAsync(hub, draft).ConfigureAwait(false)).ConfigureAwait(false);
                    }
                    return;
                }
            }
            if (request.HttpMethod == "POST" && path == "/api/hub/scale" && OperatingSystem.IsWindows())
            {
                using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                var body = JsonNode.Parse(await reader.ReadToEndAsync().ConfigureAwait(false)) as JsonObject;
                if (body?["slot"]?.GetValue<int>() is not { } slot || body?["percent"]?.GetValue<int>() is not { } percent)
                { await Reply(response, 400, new { error = "slot and percent are required" }).ConfigureAwait(false); return; }
                var hub = Hub.Current;
                if (hub == null) { await Reply(response, 503, new { error = "Hub is starting" }).ConfigureAwait(false); return; }
                var (ok, message) = await hub.Engine.SetVirtualContentScaleAsync(slot, percent).ConfigureAwait(false);
                await Reply(response, ok ? 200 : 400, new { ok, message }).ConfigureAwait(false);
                return;
            }
            if (request.HttpMethod == "POST" && path == "/api/hub/role")
            {
                using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                var body = JsonNode.Parse(await reader.ReadToEndAsync().ConfigureAwait(false)) as JsonObject ?? new JsonObject();
                var role = ParseRoleName(body["role"]?.GetValue<string>());
                if (role == null)
                {
                    await Reply(response, 400, new { error = "role must be server, client or off" }).ConfigureAwait(false);
                    return;
                }
                // a client connects to the hub that asked, unless it named another server
                var host = body["remoteHost"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(host)) host = request.RemoteEndPoint?.Address is { } a ? (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString() : null;
                Log.Info($"Deskflow 联动: {request.RemoteEndPoint?.Address} 请求本机切换为{DeskflowManager.RoleName(role.Value)}" + (role == DeskflowRole.Client ? $"（服务端 {host}）" : string.Empty));
                // requests from the other hub never cascade back; the local command line may ask this hub to switch both computers
                var fromHere = request.RemoteEndPoint?.Address is { } from && IPAddress.IsLoopback(from);
                var cascade = fromHere && body["cascade"]?.GetValue<bool>() == true;
                if (fromHere && role == DeskflowRole.Client && string.IsNullOrWhiteSpace(body["remoteHost"]?.GetValue<string>())) host = null;
                var (ok, message) = await _deskflow.SetRoleAsync(role.Value, role == DeskflowRole.Client ? host : null, cascade).ConfigureAwait(false);
                await Reply(response, ok ? 200 : 500, new { ok, message, status = StatusPayload() }).ConfigureAwait(false);
                return;
            }
            await Reply(response, 404, new { error = "not found" }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try { await Reply(response, 500, new { error = ex.Message }).ConfigureAwait(false); } catch { }
        }
    }

    private object StatusPayload()
    {
        var st = _deskflow.Status;
        return new
        {
            screen = _deskflow.LocalScreenName,
            os = OperatingSystem.IsWindows() ? "Windows" : "Linux",
            version = typeof(PeerLink).Assembly.GetName().Version?.ToString(3) ?? "?",
            role = _deskflow.Settings.Role.ToString().ToLowerInvariant(),
            running = st.RunningRole.ToString().ToLowerInvariant(),
            peers = st.Peers,
            // the other hub sets its pointer speed for this screen from it (Deskflow moves this cursor by its pixel deltas)
            dpiScale = PointerSpeedManager.PrimaryScale(),
            // the Deskflow screen of this computer is its whole desktop; a dual-boot machine has a different one per
            // system, so the other hub draws this screen from what is reported here rather than from a stored layout
            desktopWidth = PointerSpeedManager.DesktopSize().Width,
            desktopHeight = PointerSpeedManager.DesktopSize().Height,
            monitors = OperatingSystem.IsWindows() && Hub.Current is { } hub ? SharedDisplayLayout.LocalMonitors(hub) : null,
        };
    }

    private static bool CryptographicEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static async Task Reply(HttpListenerResponse response, int status, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    public static DeskflowRole? ParseRoleName(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        "server" => DeskflowRole.Server,
        "client" => DeskflowRole.Client,
        "off" => DeskflowRole.Off,
        _ => null
    };

    // ------------------------------------------------------------------ client

    private static HttpRequestMessage Request(HttpMethod method, string host, string path, object? body = null)
    {
        var message = new HttpRequestMessage(method, $"http://{host}:{Port}{path}");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        if (body != null) message.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        return message;
    }

    public static async Task<PeerHubInfo?> GetStatusAsync(string host, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var response = await Http.SendAsync(Request(HttpMethod.Get, host, "/api/hub/status"), cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"对端 Hub 返回 {(int)response.StatusCode}");
        return ParseInfo(JsonNode.Parse(await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false)) as JsonObject);
    }

    /// <summary>Asks the other hub to take a role; returns its answer (it only replies once its core is up).</summary>
    public static async Task<(bool Ok, string Message)> RequestRoleAsync(string host, DeskflowRole role, string? remoteHost = null, bool cascade = false)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(cascade ? 90 : 40));
            using var response = await Http.SendAsync(Request(HttpMethod.Post, host, "/api/hub/role",
                new { role = role.ToString().ToLowerInvariant(), remoteHost, cascade }), cts.Token).ConfigureAwait(false);
            var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false)) as JsonObject;
            var message = node?["message"]?.GetValue<string>() ?? node?["error"]?.GetValue<string>() ?? ((int)response.StatusCode).ToString();
            return (response.IsSuccessStatusCode && node?["ok"]?.GetValue<bool>() == true, message);
        }
        catch (Exception ex)
        {
            return (false, "对端 Hub 无法连接: " + ex.GetBaseException().Message);
        }
    }

    /// <param name="host">运行 Hub 的主机地址。</param>
    /// <param name="slot">虚拟屏槽位，从零开始。</param>
    /// <param name="percent">额外画面大小百分比。</param>
    public static async Task<(bool Ok, string Message)> RequestContentScaleAsync(string host, int slot, int percent)
    {
        try
        {
            using var request = Request(HttpMethod.Post, host, "/api/hub/scale", new { slot, percent });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            return (response.IsSuccessStatusCode, node?["message"]?.GetValue<string>() ?? node?["error"]?.GetValue<string>() ?? "操作失败");
        }
        catch (Exception ex) { return (false, ex.GetBaseException().Message); }
    }

    /// <param name="host">共享布局服务端地址。</param>
    public static Task<SharedDisplayDocument> GetSharedDisplaysAsync(string host)
        => DisplayRequestAsync<SharedDisplayDocument>(host, "/api/hub/display-layout", null);

    /// <param name="host">显示器所属电脑的地址。</param>
    /// <param name="positions">期望的显示器位置。</param>
    public static Task<List<SharedMonitor>> ApplyMonitorPositionsAsync(string host, IReadOnlyList<MonitorPlacement> positions)
        => DisplayRequestAsync<List<SharedMonitor>>(host, "/api/hub/displays", positions);

    /// <param name="host">统一提交布局的服务端地址。</param>
    /// <param name="layout">界面生成的布局草稿。</param>
    public static Task<SharedDisplayDocument> ApplySharedDisplaysAsync(string host, SharedDisplayDocument layout)
        => DisplayRequestAsync<SharedDisplayDocument>(host, "/api/hub/display-layout", layout);

    /// <param name="host">这块虚拟屏所属电脑的 Hub 地址。</param>
    /// <param name="slot">虚拟屏槽位，从零开始。</param>
    /// <param name="percent">保存到对应电脑方案中的额外缩放百分比。</param>
    public static async Task<string> SetContentScaleAsync(string host, int slot, int percent)
    {
        using var request = Request(HttpMethod.Post, host, "/api/hub/scale", new { slot, percent });
        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var message = result?["message"]?.ToString() ?? result?["error"]?.ToString() ?? "调整失败";
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(message);
        return message;
    }

    private static async Task<T> DisplayRequestAsync<T>(string host, string path, object? body)
    {
        using var request = Request(body == null ? HttpMethod.Get : HttpMethod.Post, host, path, body);
        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(JsonNode.Parse(text)?["error"]?.ToString() ?? text);
        return JsonSerializer.Deserialize<T>(text, Json) ?? throw new InvalidDataException("Empty layout response");
    }

    private static PeerHubInfo? ParseInfo(JsonObject? o)
    {
        if (o == null) return null;
        return new PeerHubInfo(
            o["screen"]?.GetValue<string>() ?? "?",
            o["os"]?.GetValue<string>() ?? "?",
            o["version"]?.GetValue<string>() ?? "?",
            ParseRoleName(o["role"]?.GetValue<string>()) ?? DeskflowRole.Off,
            ParseRoleName(o["running"]?.GetValue<string>()) ?? DeskflowRole.Off,
            (o["peers"] as JsonArray)?.Select(p => p?.GetValue<string>() ?? string.Empty).Where(p => p.Length > 0).ToList() ?? new List<string>(),
            o["dpiScale"] is JsonValue scale && scale.TryGetValue<double>(out var dpi) ? dpi : 0,
            o["desktopWidth"] is JsonValue dw && dw.TryGetValue<int>(out var w) ? w : 0,
            o["desktopHeight"] is JsonValue dh && dh.TryGetValue<int>(out var h) ? h : 0,
            o["monitors"]?.Deserialize<List<SharedMonitor>>(Json));
    }

    public void Dispose()
    {
        _stopping = true;
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        _listener = null;
    }
}
