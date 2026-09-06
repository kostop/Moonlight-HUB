using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MoonlightHub.Core;

public sealed record PairingRequest(int InstanceId, string RemoteAddress, string UniqueId, DateTime SeenUtc)
{
    public string Key => $"{InstanceId}:{UniqueId}";
}

public sealed record ClientMode(int Width, int Height, int Fps)
{
    public override string ToString() => $"{Width}×{Height} @ {Fps}fps";
}

/// <summary>
/// Follows every running instance's sunshine.log and turns the lines added by the Hub's Sunshine patch into events:
/// pairing requests (so the Hub can prompt for the PIN), pairing outcomes and the stream mode a client asked for.
/// Only bytes appended after the monitor started are read, so old log content is never re-processed.
/// </summary>
public sealed class PairingMonitor : IDisposable
{
    private static readonly Regex PairRequest = new(@"Pairing request from \[(?<ip>[^\]]+)\] uniqueid \[(?<id>[^\]]+)\] awaiting PIN", RegexOptions.Compiled);
    private static readonly Regex PairDone = new(@"Pairing completed for client \[(?<name>[^\]]*)\] uniqueid \[(?<id>[^\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex PairFailed = new(@"Pairing failed for uniqueid \[(?<id>[^\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex ModeLine = new(@"Client requested mode \[(?<w>\d+)x(?<h>\d+)x(?<f>\d+)\] sops \[(?<s>\d)\] uniqueid \[(?<id>[^\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex LegacyFps = new(@"Requested frame rate \[(?<f>\d+)fps\]", RegexOptions.Compiled);
    private static readonly Regex ClientConnected = new(@"CLIENT CONNECTED", RegexOptions.Compiled);
    private static readonly Regex ClientDisconnected = new(@"CLIENT DISCONNECTED", RegexOptions.Compiled);

    private readonly HubSettings _settings;
    private readonly SunshineInstanceManager _instances;
    private readonly System.Threading.Timer _timer;
    private readonly Dictionary<int, long> _offsets = new();
    private readonly Dictionary<int, string> _partial = new();
    private readonly object _gate = new();
    private bool _ticking;

    /// <summary>Pairing requests still waiting for a PIN, keyed by instance id.</summary>
    public Dictionary<int, PairingRequest> Pending { get; } = new();

    /// <summary>Latest stream mode requested per instance (from the "Client requested mode" log line).</summary>
    public Dictionary<int, ClientMode> LatestMode { get; } = new();

    public event Action<PairingRequest>? PairingRequested;
    public event Action<int, string, bool, string>? PairingFinished; // instance, uniqueId, success, client name
    /// <summary>The pending pairing session went away without a result (instance restarted or request expired).</summary>
    public event Action<int>? PairingCancelled;
    public event Action<int, ClientMode, bool>? ClientModeRequested;  // instance, mode, sops
    public event Action<int, bool>? ClientConnectionChanged;          // instance, connected

    public PairingMonitor(HubSettings settings, SunshineInstanceManager instances)
    {
        _settings = settings;
        _instances = instances;
        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(1000, 1000);
    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    public void ClearPending(int instanceId)
    {
        lock (_gate) Pending.Remove(instanceId);
    }

    private void Cancel(int instanceId, string reason)
    {
        bool had;
        lock (_gate) had = Pending.Remove(instanceId);
        if (!had) return;
        Log.Info($"实例 {instanceId}: {reason}");
        try { PairingCancelled?.Invoke(instanceId); } catch { }
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_ticking) return;
            _ticking = true;
        }
        try
        {
            foreach (var spec in _settings.Instances.Where(i => i.Enabled).ToList())
            {
                try { Follow(spec); }
                catch (Exception ex) { Log.Debug($"配对监听读取实例 {spec.Id} 日志失败: {ex.Message}"); }
            }
            // expire stale requests (Moonlight gives up after a few minutes as well)
            List<int> expired;
            lock (_gate) expired = Pending.Where(kv => (DateTime.UtcNow - kv.Value.SeenUtc) > TimeSpan.FromMinutes(5)).Select(kv => kv.Key).ToList();
            foreach (var id in expired) Cancel(id, "配对请求已超时");
        }
        finally
        {
            lock (_gate) _ticking = false;
        }
    }

    private void Follow(InstanceSpec spec)
    {
        var path = _instances.Get(spec).LogPath;
        if (!File.Exists(path)) return;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = fs.Length;
        if (!_offsets.TryGetValue(spec.Id, out var offset))
        {
            // first sight: skip history
            _offsets[spec.Id] = length;
            return;
        }
        if (length < offset)
        {
            offset = 0; // log rotated / truncated (new process)
            _partial[spec.Id] = string.Empty;
            Cancel(spec.Id, "实例已重启，之前的配对会话失效");
        }
        if (length == offset) return;
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[Math.Min(length - offset, 512 * 1024)];
        var read = fs.Read(buffer, 0, buffer.Length);
        _offsets[spec.Id] = offset + read;
        var text = (_partial.GetValueOrDefault(spec.Id, string.Empty)) + Encoding.UTF8.GetString(buffer, 0, read);
        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline < 0)
        {
            _partial[spec.Id] = text;
            return;
        }
        _partial[spec.Id] = text[(lastNewline + 1)..];
        foreach (var raw in text[..lastNewline].Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            HandleLine(spec, line);
        }
    }

    private void HandleLine(InstanceSpec spec, string line)
    {
        var m = PairRequest.Match(line);
        if (m.Success)
        {
            var ip = NormalizeAddress(m.Groups["ip"].Value);
            var req = new PairingRequest(spec.Id, ip, m.Groups["id"].Value, DateTime.UtcNow);
            lock (_gate) Pending[spec.Id] = req;
            Log.Info($"实例 {spec.Id}: 客户端 {ip} 请求配对，等待输入 PIN");
            try { PairingRequested?.Invoke(req); } catch (Exception ex) { Log.Warn("PairingRequested 处理失败: " + ex.Message); }
            return;
        }
        m = PairDone.Match(line);
        if (m.Success)
        {
            lock (_gate) Pending.Remove(spec.Id);
            var name = m.Groups["name"].Value;
            Log.Info($"实例 {spec.Id}: 客户端「{name}」配对成功");
            try { PairingFinished?.Invoke(spec.Id, m.Groups["id"].Value, true, name); } catch { }
            return;
        }
        m = PairFailed.Match(line);
        if (m.Success)
        {
            lock (_gate) Pending.Remove(spec.Id);
            Log.Warn($"实例 {spec.Id}: 配对失败（PIN 错误或证书不匹配）");
            try { PairingFinished?.Invoke(spec.Id, m.Groups["id"].Value, false, string.Empty); } catch { }
            return;
        }
        m = ModeLine.Match(line);
        if (m.Success)
        {
            var mode = new ClientMode(int.Parse(m.Groups["w"].Value), int.Parse(m.Groups["h"].Value), int.Parse(m.Groups["f"].Value));
            var sops = m.Groups["s"].Value == "1";
            if (mode.Width > 0 && mode.Height > 0)
            {
                lock (_gate) LatestMode[spec.Id] = mode;
                Log.Info($"实例 {spec.Id}: 客户端请求 {mode}{(sops ? string.Empty : "（未开启“优化游戏设置”）")}");
                try { ClientModeRequested?.Invoke(spec.Id, mode, sops); } catch (Exception ex) { Log.Warn("ClientModeRequested 处理失败: " + ex.Message); }
            }
            return;
        }
        m = LegacyFps.Match(line);
        if (m.Success)
        {
            var fps = int.Parse(m.Groups["f"].Value);
            lock (_gate)
            {
                if (LatestMode.TryGetValue(spec.Id, out var known) && known.Fps != fps) LatestMode[spec.Id] = known with { Fps = fps };
            }
            return;
        }
        if (ClientConnected.IsMatch(line)) { try { ClientConnectionChanged?.Invoke(spec.Id, true); } catch { } }
        else if (ClientDisconnected.IsMatch(line)) { try { ClientConnectionChanged?.Invoke(spec.Id, false); } catch { } }
    }

    private static string NormalizeAddress(string ip)
    {
        if (ip.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase)) return ip[7..];
        return ip;
    }

    public void Dispose() => _timer.Dispose();
}
