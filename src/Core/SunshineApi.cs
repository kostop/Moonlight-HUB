using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace MoonlightHub.Core;

public sealed record ServerInfo(string Hostname, string State, string AppVersion, string CurrentGame, bool Reachable)
{
    public bool Busy => State.Contains("BUSY", StringComparison.OrdinalIgnoreCase);
}

public sealed record PairedClient(string Name, string Uuid);

/// <summary>Talks to one Sunshine instance: unauthenticated /serverinfo on the HTTP port and the authenticated web API.</summary>
public sealed class SunshineApi
{
    private static readonly HttpClient Plain = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(2) }) { Timeout = TimeSpan.FromSeconds(4) };

    private readonly HttpClient _client;
    private readonly int _port;

    public SunshineApi(int basePort, string? user, string? password)
    {
        _port = basePort;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(3),
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
        };
        _client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{basePort + 1}/"), Timeout = TimeSpan.FromSeconds(10) };
        if (!string.IsNullOrEmpty(user))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password ?? string.Empty}"));
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
    }

    public static async Task<ServerInfo> GetServerInfoAsync(int basePort, CancellationToken ct = default)
    {
        try
        {
            using var response = await Plain.GetAsync($"http://127.0.0.1:{basePort}/serverinfo", ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var xml = XDocument.Parse(text);
            var root = xml.Root;
            string Get(string name) => root?.Element(name)?.Value ?? string.Empty;
            return new ServerInfo(Get("hostname"), Get("state"), Get("appversion"), Get("currentgame"), true);
        }
        catch
        {
            return new ServerInfo(string.Empty, string.Empty, string.Empty, string.Empty, false);
        }
    }

    public async Task<(bool Ok, string Message)> TestAuthAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _client.GetAsync("api/config", ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return (false, "用户名或密码错误 (401)");
            return response.IsSuccessStatusCode ? (true, "认证成功") : (false, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Ok, string Message)> PairAsync(string pin, string clientName, CancellationToken ct = default)
    {
        try
        {
            var payload = new { pin = pin.Trim(), name = string.IsNullOrWhiteSpace(clientName) ? "Moonlight" : clientName.Trim() };
            using var response = await _client.PostAsJsonAsync("api/pin", payload, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return (false, "Web 凭据无效，请先在“客户端”页保存 Sunshine 用户名/密码");
            if (!response.IsSuccessStatusCode) return (false, $"HTTP {(int)response.StatusCode}: {body}");
            using var doc = JsonDocument.Parse(body);
            var ok = doc.RootElement.TryGetProperty("status", out var s) && (s.ValueKind == JsonValueKind.True || (s.ValueKind == JsonValueKind.String && s.GetString() == "true"));
            return ok ? (true, "配对成功") : (false, "Sunshine 拒绝了 PIN（请确认客户端正在等待配对，PIN 正确）: " + body);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<List<PairedClient>> ListClientsAsync(CancellationToken ct = default)
    {
        var list = new List<PairedClient>();
        try
        {
            using var response = await _client.GetAsync("api/clients/list", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return list;
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("named_certs", out var certs) && certs.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in certs.EnumerateArray())
                {
                    var name = c.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                    var uuid = c.TryGetProperty("uuid", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                    list.Add(new PairedClient(string.IsNullOrWhiteSpace(name) ? "(未命名客户端)" : name, uuid));
                }
            }
        }
        catch { }
        return list;
    }

    public async Task<(bool Ok, string Message)> UnpairAsync(string uuid, CancellationToken ct = default)
    {
        try
        {
            using var response = await _client.PostAsJsonAsync("api/clients/unpair", new { uuid }, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? (true, "已取消配对") : (false, $"HTTP {(int)response.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Ok, string Message)> RestartAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _client.PostAsync("api/restart", new StringContent(string.Empty), ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? (true, "已请求重启") : (false, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
