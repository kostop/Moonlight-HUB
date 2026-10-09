using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MoonlightHub.Core;

/// <summary>One local display in Windows desktop pixels (GDI name, e.g. \\.\DISPLAY1).</summary>
public sealed record LocalRect(string Name, int X, int Y, int Width, int Height, bool Primary)
{
    public static LocalRect From(DisplayInfo d, int? x = null, int? y = null) => new(d.GdiName, x ?? d.PositionX, y ?? d.PositionY, d.Width, d.Height, d.Primary);
}

/// <summary>A Deskflow screen of another computer, placed in Windows desktop pixel coordinates next to the local displays.</summary>
public sealed record PeerScreen(string Name, int X, int Y, int Width, int Height, int ResolutionWidth, int ResolutionHeight, bool IsServer);

/// <summary>Snapshot of the shared Deskflow layout (served by deskflow-layout-web on the peer, port 24980).</summary>
public sealed class DeskflowLayoutSnapshot
{
    public JsonObject Layout { get; init; } = new();
    public string Server { get; init; } = string.Empty;
    public string LocalScreen { get; init; } = string.Empty;
    public string PeerStatus { get; init; } = string.Empty;
    public DateTime TimeUtc { get; init; } = DateTime.UtcNow;
    public bool HasLocalScreen => Layout["screens"]?[LocalScreen] is JsonObject;
}

/// <summary>
/// The other computer in the Deskflow setup: one manually entered address that every Deskflow component on this PC
/// uses (client-mode server address, layout service, clipboard/file side channel, layout sync task), plus a client
/// for the shared layout service so the hub's display layout and Deskflow's screen arrangement are one and the same.
/// </summary>
public static class DeskflowPeer
{
    public const int LayoutPort = 24980;

    /// <summary>Scheduled tasks of the Deskflow side channel that carry the peer address in their arguments.</summary>
    public static readonly string[] PeerTasks =
    {
        "Deskflow Layout Config Sync",
        "Deskflow Clipboard Agent",
        "Deskflow File Transfer Agent",
    };

    private static string UserHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string EndpointsFile => Path.Combine(UserHome, ".config", "deskflow-clipboard-sync", "endpoints.conf");
    public static string TokenFile => Path.Combine(UserHome, ".config", "deskflow-clipboard-sync", "token");

    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(40) };

    public static bool IsValidHost(string? host) => !string.IsNullOrWhiteSpace(host) && Regex.IsMatch(host.Trim(), @"^[A-Za-z0-9._-]+$");

    // ------------------------------------------------------------------ peer address

    /// <summary>
    /// Points every Deskflow component at <paramref name="host"/>: Deskflow.conf (client mode), endpoints.conf (keepalive),
    /// and the side-channel scheduled tasks, which are restarted when their arguments change. The layout sync task is also
    /// told to reload the server through the touch filter wrapper instead of the raw core.
    /// </summary>
    public static List<string> ApplyHost(string host, string configPath, string? wrapperExe, bool updateTasks)
    {
        host = host.Trim();
        if (!IsValidHost(host)) throw new ArgumentException("地址格式不正确: " + host);
        var notes = new List<string>();

        try
        {
            var conf = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
            var updated = DeskflowManager.IniSet(conf, "client", "remoteHost", host);
            if (updated != conf)
            {
                File.WriteAllText(configPath, updated, new UTF8Encoding(false));
                notes.Add("Deskflow.conf remoteHost → " + host);
            }
        }
        catch (Exception ex) { notes.Add("Deskflow.conf 写入失败: " + ex.Message); }

        try
        {
            if (File.Exists(EndpointsFile))
            {
                var text = File.ReadAllText(EndpointsFile);
                var updated = Regex.Replace(text, @"(?m)^(\s*(?:sideclipHost|layoutHost)\s*=\s*)\S*", m => m.Groups[1].Value + host);
                if (updated != text)
                {
                    File.WriteAllText(EndpointsFile, updated, new UTF8Encoding(false));
                    notes.Add("endpoints.conf → " + host);
                }
            }
        }
        catch (Exception ex) { notes.Add("endpoints.conf 写入失败: " + ex.Message); }

        foreach (var task in updateTasks ? PeerTasks : Array.Empty<string>())
        {
            try
            {
                var note = UpdateTask(task, host, wrapperExe);
                if (note != null) notes.Add(note);
            }
            catch (Exception ex)
            {
                notes.Add($"{task}: 更新失败 {ex.Message}");
            }
        }
        Log.Info("Deskflow 对端地址 → " + host + (notes.Count > 0 ? "：" + string.Join("；", notes) : "（无需修改）"));
        return notes;
    }

    /// <summary>Current peer address the side-channel tasks use (from endpoints.conf), empty when unknown.</summary>
    public static string ConfiguredSideChannelHost()
    {
        try
        {
            if (!File.Exists(EndpointsFile)) return string.Empty;
            var m = Regex.Match(File.ReadAllText(EndpointsFile), @"(?m)^\s*sideclipHost\s*=\s*(\S+)");
            return m.Success ? m.Groups[1].Value : string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>Points a side-channel command line at <paramref name="host"/> (--server / --url) and at the touch filter wrapper (--core-executable).</summary>
    public static string RewriteArguments(string args, string host, string? wrapperExe)
    {
        var updated = Regex.Replace(args, @"(--server\s+)(""[^""]*""|\S+)", m => m.Groups[1].Value + host);
        updated = Regex.Replace(updated, @"(--url\s+""?https?://)([^:/""\s]+)", m => m.Groups[1].Value + host);
        if (!string.IsNullOrEmpty(wrapperExe) && File.Exists(wrapperExe))
        {
            updated = Regex.Replace(updated, @"(--core-executable\s+)(""[^""]*""|\S+)", m => m.Groups[1].Value + "\"" + wrapperExe + "\"");
        }
        return updated;
    }

    private static string? UpdateTask(string task, string host, string? wrapperExe)
    {
        var (code, json) = RunPowerShell($"$a = (Get-ScheduledTask -TaskName {PsQuote(task)} -ErrorAction Stop).Actions[0]; [pscustomobject]@{{ Execute = $a.Execute; Arguments = $a.Arguments }} | ConvertTo-Json -Compress");
        if (code != 0 || !json.TrimStart().StartsWith('{')) return null; // task not present on this machine
        var node = JsonNode.Parse(json.Trim())!;
        var args = node["Arguments"]?.GetValue<string>() ?? string.Empty;

        var updated = RewriteArguments(args, host, wrapperExe);
        if (updated == args) return null;

        var (setCode, setOut) = RunPowerShell(
            $"$a = (Get-ScheduledTask -TaskName {PsQuote(task)} -ErrorAction Stop).Actions[0]; $a.Arguments = {PsQuote(updated)}; " +
            $"Set-ScheduledTask -TaskName {PsQuote(task)} -Action $a -ErrorAction Stop | Out-Null; " +
            $"if ((Get-ScheduledTask -TaskName {PsQuote(task)}).State -ne 'Disabled') {{ Stop-ScheduledTask -TaskName {PsQuote(task)} -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500; Start-ScheduledTask -TaskName {PsQuote(task)} -ErrorAction Stop }}; 'ok'");
        if (setCode == 0 && setOut.Contains("ok")) return $"{task}: 已更新并重启";
        return $"{task}: 更新失败 " + (setOut.Contains("0x80070005") || setOut.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            ? "（拒绝访问：需要管理员权限，请在以管理员运行的 Hub 里保存）"
            : ReadableError(setOut));
    }

    /// <summary>First error line of a PowerShell run whose stderr came back as CLIXML.</summary>
    private static string ReadableError(string output)
    {
        var m = Regex.Match(output, @"<S S=""Error"">(.*?)(?:_x000D__x000A_)?</S>");
        return (m.Success ? m.Groups[1].Value : output).Replace("_x000D__x000A_", " ").Trim();
    }

    private static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

    private static (int Code, string Output) RunPowerShell(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue'; " + script));
        return ProcessUtil.Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded, 30000);
    }

    // ------------------------------------------------------------------ shared layout service

    private static string BaseUrl(string host) => $"http://{host}:{LayoutPort}";

    public static async Task<DeskflowLayoutSnapshot> GetLayoutAsync(string host, string localScreen)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var text = await Http.GetStringAsync(BaseUrl(host) + "/api/layout", cts.Token).ConfigureAwait(false);
        var root = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("布局服务返回的不是 JSON 对象");
        var layout = root["layout"] as JsonObject ?? throw new InvalidDataException("布局服务没有返回 layout");
        return new DeskflowLayoutSnapshot
        {
            Layout = layout,
            Server = layout["server"]?.GetValue<string>() ?? string.Empty,
            LocalScreen = ResolveScreenName(layout, localScreen),
            PeerStatus = root["status"]?.ToString() ?? string.Empty,
        };
    }

    private static string ResolveScreenName(JsonObject layout, string name)
    {
        if (layout["screens"] is JsonObject screens)
        {
            foreach (var kv in screens)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Key;
        }
        return name;
    }

    /// <summary>
    /// Places the other computers' screens in Windows desktop pixels: the local screen's rectangle in layout units is
    /// mapped onto the bounding box of the active displays (the same topology the layout sync task reports), so a peer
    /// drawn next to a monitor in the hub is exactly where Deskflow switches to it.
    /// </summary>
    public static List<PeerScreen> MapPeers(DeskflowLayoutSnapshot snapshot, IReadOnlyCollection<LocalRect> localDisplays)
    {
        var list = new List<PeerScreen>();
        if (localDisplays.Count == 0 || snapshot.Layout["screens"] is not JsonObject screens || screens[snapshot.LocalScreen] is not JsonObject local) return list;
        var k = UnitsPerPixel(local);
        if (k <= 0) return list;
        var (minX, minY) = Anchor(local, localDisplays);
        var lx = Num(local, "x");
        var ly = Num(local, "y");
        foreach (var kv in screens)
        {
            if (kv.Key == snapshot.LocalScreen || kv.Value is not JsonObject rect) continue;
            var res = rect["resolution"] as JsonObject;
            var resW = (int)(res?["width"]?.GetValue<double>() ?? 0);
            var resH = (int)(res?["height"]?.GetValue<double>() ?? 0);
            // the layout service sizes screens in its own (scale-adjusted) units, which drew a 2560x1440 peer next to a
            // 2000x1200 display at about 1185x667; Deskflow moves the cursor pixel for pixel, so the peer is drawn at its
            // real resolution, grown away from the edge it touches (see PeerRect / MovePeers for the inverse)
            var ux = minX + (int)Math.Round((Num(rect, "x") - lx) / k);
            var uy = minY + (int)Math.Round((Num(rect, "y") - ly) / k);
            var uw = (int)Math.Round(Num(rect, "w") / k);
            var uh = (int)Math.Round(Num(rect, "h") / k);
            var (x, y, w, h) = PeerRect(localDisplays, ux, uy, uw, uh, resW, resH);
            list.Add(new PeerScreen(kv.Key, x, y, w, h, resW, resH, string.Equals(kv.Key, snapshot.Server, StringComparison.OrdinalIgnoreCase)));
        }
        return list;
    }

    /// <summary>
    /// Pixel rectangle of a peer whose layout rectangle (in pixels of the local desktop) is <c>(ux, uy, uw, uh)</c>: its
    /// real resolution, anchored at the edge it shares with the local desktop so it never grows over a local display.
    /// </summary>
    private static (int X, int Y, int W, int H) PeerRect(IReadOnlyCollection<LocalRect> local, int ux, int uy, int uw, int uh, int resW, int resH)
    {
        if (resW <= 0 || resH <= 0) return (ux, uy, uw, uh);
        var side = SideOf(local, ux, uy, uw, uh);
        return (side == "left" ? ux + uw - resW : ux, side == "up" ? uy + uh - resH : uy, resW, resH);
    }

    /// <summary>
    /// Windows desktop position of the local screen's origin in the shared layout. The layout keeps the monitor topology
    /// that was last reported (relative to its own origin), so a monitor matched by GDI name (primary first) anchors the
    /// mapping exactly even while that topology is out of date; without a match the bounding box of the displays is used.
    /// </summary>
    private static (int X, int Y) Anchor(JsonObject local, IReadOnlyCollection<LocalRect> displays)
    {
        if (local["monitors"] is JsonArray monitors)
        {
            foreach (var monitor in monitors.OfType<JsonObject>().OrderBy(m => m["primary"]?.GetValue<bool>() == true ? 0 : 1))
            {
                var name = monitor["name"]?.GetValue<string>() ?? string.Empty;
                var match = displays.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
                if (match != null) return (match.X - (int)Num(monitor, "x"), match.Y - (int)Num(monitor, "y"));
            }
        }
        return (displays.Min(d => d.X), displays.Min(d => d.Y));
    }

    private static double UnitsPerPixel(JsonObject rect)
    {
        var resHeight = (rect["resolution"] as JsonObject)?["height"]?.GetValue<double>() ?? 0;
        return resHeight > 0 ? Num(rect, "h") / resHeight : 0;
    }

    private static double Num(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    /// <summary>Tells the layout service the current monitor topology of this PC (same payload as the layout sync task).</summary>
    public static async Task<(bool Changed, string Output)> ReportDisplaysAsync(string host, string localScreen, IReadOnlyCollection<LocalRect> active)
    {
        var token = File.Exists(TokenFile) ? File.ReadAllText(TokenFile).Trim() : string.Empty;
        if (token.Length == 0) throw new InvalidOperationException("找不到布局同步 token（" + TokenFile + "）");
        var monitors = active
            .Select(d => new { name = d.Name, x = d.X, y = d.Y, width = d.Width, height = d.Height, primary = d.Primary })
            .OrderBy(m => m.x).ThenBy(m => m.y).ThenBy(m => !m.primary).ThenBy(m => m.name, StringComparer.Ordinal)
            .ToList();
        var minX = monitors.Min(m => m.x);
        var minY = monitors.Min(m => m.y);
        var payload = new
        {
            screen = localScreen,
            virtual_bounds = new { x = minX, y = minY, width = monitors.Max(m => m.x + m.width) - minX, height = monitors.Max(m => m.y + m.height) - minY },
            monitors,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl(host) + "/api/client-display")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var root = JsonNode.Parse(text);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("上报显示器失败: " + (root?["error"]?.ToString() ?? text));
        return (root?["changed"]?.GetValue<bool>() == true, root?["output"]?.ToString() ?? string.Empty);
    }

    /// <summary>
    /// Moves the given peer screens (Windows desktop pixels, in the same arrangement as <paramref name="localDisplays"/>)
    /// in the shared layout and applies it. The local screen is sent back untouched so its monitor topology is preserved.
    /// </summary>
    public static async Task<string> ApplyPeerPositionsAsync(string host, string localScreen,
        IReadOnlyCollection<LocalRect> localDisplays, IReadOnlyCollection<(string Name, int X, int Y)> peers)
    {
        var snapshot = await GetLayoutAsync(host, localScreen).ConfigureAwait(false);
        MovePeers(snapshot, localDisplays, peers);
        var body = new JsonObject { ["layout"] = snapshot.Layout.DeepClone() };
        using var response = await Http.PostAsync(BaseUrl(host) + "/api/apply", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var root = JsonNode.Parse(text);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("应用 Deskflow 布局失败: " + (root?["error"]?.ToString() ?? text) + " " + root?["output"]);
        var output = root?["output"]?.ToString() ?? string.Empty;
        Log.Info("Deskflow 共享布局已更新: " + string.Join(", ", peers.Select(p => $"{p.Name}@({p.X},{p.Y})")) + (output.Length > 0 ? " · " + output.Replace('\n', ' ') : string.Empty));
        return output;
    }

    /// <summary>Moves peer screens (Windows desktop pixels, arranged like <paramref name="localDisplays"/>) inside the layout snapshot.</summary>
    public static void MovePeers(DeskflowLayoutSnapshot snapshot, IReadOnlyCollection<LocalRect> localDisplays, IReadOnlyCollection<(string Name, int X, int Y)> peers)
    {
        if (snapshot.Layout["screens"] is not JsonObject screens || screens[snapshot.LocalScreen] is not JsonObject local)
            throw new InvalidOperationException($"共享布局里没有本机屏幕「{snapshot.LocalScreen}」");
        var k = UnitsPerPixel(local);
        if (k <= 0) throw new InvalidOperationException("本机屏幕的分辨率无效");
        var (minX, minY) = Anchor(local, localDisplays);
        var lx = Num(local, "x");
        var ly = Num(local, "y");
        foreach (var (name, x, y) in peers)
        {
            if (screens[name] is not JsonObject rect) continue;
            // inverse of PeerRect: the canvas shows the peer at its resolution, the layout keeps it in its own units,
            // anchored at the shared edge
            var res = rect["resolution"] as JsonObject;
            var resW = (int)(res?["width"]?.GetValue<double>() ?? 0);
            var resH = (int)(res?["height"]?.GetValue<double>() ?? 0);
            var w = resW > 0 && resH > 0 ? resW : (int)Math.Round(Num(rect, "w") / k);
            var h = resW > 0 && resH > 0 ? resH : (int)Math.Round(Num(rect, "h") / k);
            var side = SideOf(localDisplays, x, y, w, h);
            rect["x"] = side == "left" ? lx + (x + w - minX) * k - Num(rect, "w") : lx + (x - minX) * k;
            rect["y"] = side == "up" ? ly + (y + h - minY) * k - Num(rect, "h") : ly + (y - minY) * k;
        }
    }

    /// <summary>True when the layout service simply is not there (refused, timed out, unreachable) rather than answering with an error.</summary>
    public static bool IsUnreachable(Exception ex) => ex is TaskCanceledException or OperationCanceledException
        || ex.GetBaseException() is System.Net.Sockets.SocketException
        || ex is HttpRequestException { StatusCode: null };

    /// <summary>
    /// Side of the local desktop (bounding box of <paramref name="local"/>) that a peer screen sits against: the side
    /// where it lies furthest outside. Null when it overlaps the desktop on every side.
    /// </summary>
    public static string? SideOf(IReadOnlyCollection<LocalRect> local, int x, int y, int width, int height)
    {
        if (local.Count == 0) return null;
        int left = local.Min(d => d.X), top = local.Min(d => d.Y);
        int right = local.Max(d => d.X + d.Width), bottom = local.Max(d => d.Y + d.Height);
        var gaps = new (string Side, int Gap)[]
        {
            ("right", x - right), ("left", left - (x + width)), ("down", y - bottom), ("up", top - (y + height)),
        };
        var best = gaps.OrderByDescending(g => g.Gap).First();
        return best.Gap >= -32 ? best.Side : null; // the canvas snaps peers flush against an edge (gap 0)
    }

    /// <summary>
    /// Rebuilds the "links" section of a Deskflow server config so each peer sits on the given side of the local
    /// screen (and links back), declaring peers that are missing from the "screens" section. Everything else is kept.
    /// </summary>
    public static string LinkPeersInServerConfig(string config, string localScreen, IReadOnlyList<(string Peer, string Side)> links)
    {
        static string Opposite(string side) => side switch { "left" => "right", "right" => "left", "up" => "down", _ => "up" };
        var newline = config.Contains("\r\n") ? "\r\n" : "\n";
        var lines = config.Replace("\r\n", "\n").Split('\n').ToList();

        int SectionStart(string name) => lines.FindIndex(l => Regex.IsMatch(l, $@"^\s*section:\s*{name}\s*$", RegexOptions.IgnoreCase));
        int SectionEnd(int start) => start < 0 ? -1 : lines.FindIndex(start + 1, l => Regex.IsMatch(l, @"^\s*end\s*$", RegexOptions.IgnoreCase));

        var screensStart = SectionStart("screens");
        var screensEnd = SectionEnd(screensStart);
        if (screensStart < 0 || screensEnd < 0) throw new InvalidDataException("Deskflow 服务端配置里没有 screens 段");
        var declared = lines.Skip(screensStart + 1).Take(screensEnd - screensStart - 1)
            .Select(l => Regex.Match(l, @"^\s*([^\s=][^=]*?):\s*$")).Where(m => m.Success).Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = links.Select(l => l.Peer).Append(localScreen).Where(n => !declared.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        lines.InsertRange(screensEnd, missing.Select(n => "\t" + n + ":"));

        var section = new List<string> { "section: links", $"\t{localScreen}:" };
        section.AddRange(links.Select(l => $"\t\t{l.Side} = {l.Peer}"));
        foreach (var (peer, side) in links)
        {
            section.Add($"\t{peer}:");
            section.Add($"\t\t{Opposite(side)} = {localScreen}");
        }
        section.Add("end");

        var linksStart = SectionStart("links");
        var linksEnd = SectionEnd(linksStart);
        // the same links in another order (the layout sync writes the peer first) are no change: keep the file as it is
        static HashSet<string> LinkSet(IEnumerable<string> body)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var screen = string.Empty;
            foreach (var raw in body)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.EndsWith(':') && !line.Contains('=')) { screen = line.TrimEnd(':').Trim(); continue; }
                set.Add(screen + "|" + Regex.Replace(line, @"\s+", string.Empty));
            }
            return set;
        }
        if (missing.Count == 0 && linksStart >= 0 && linksEnd >= 0
            && LinkSet(lines.Skip(linksStart + 1).Take(linksEnd - linksStart - 1)).SetEquals(LinkSet(section.Skip(1).Take(section.Count - 2))))
        {
            return config;
        }
        if (linksStart >= 0 && linksEnd >= 0)
        {
            lines.RemoveRange(linksStart, linksEnd - linksStart + 1);
            lines.InsertRange(linksStart, section);
        }
        else
        {
            var at = SectionEnd(SectionStart("screens")) + 1;
            lines.InsertRange(at, new[] { string.Empty }.Concat(section));
        }
        return string.Join(newline, lines);
    }
}
