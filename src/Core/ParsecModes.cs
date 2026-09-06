using Microsoft.Win32;

namespace MoonlightHub.Core;

public sealed record ParsecCustomMode(int Index, int Width, int Height, int Hz)
{
    public override string ToString() => $"{Width}×{Height} @ {Hz}Hz";
}

/// <summary>
/// Custom mode table of the Parsec VDD (HKLM\SOFTWARE\Parsec\vdd\&lt;n&gt; with width/height/hz DWORDs).
/// Writing needs administrator rights, so the hub re-launches itself elevated for that single operation.
/// </summary>
public static class ParsecModes
{
    public const string RegistryPath = @"SOFTWARE\Parsec\vdd";
    public const int MaxCustomModes = 8;

    public static List<ParsecCustomMode> Read()
    {
        var list = new List<ParsecCustomMode>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(RegistryPath);
            if (root == null) return list;
            foreach (var name in root.GetSubKeyNames())
            {
                if (!int.TryParse(name, out var index)) continue;
                using var key = root.OpenSubKey(name);
                if (key == null) continue;
                var w = ReadInt(key, "width");
                var h = ReadInt(key, "height");
                var hz = ReadInt(key, "hz");
                if (w is > 0 && h is > 0 && hz is > 0) list.Add(new ParsecCustomMode(index, w.Value, h.Value, hz.Value));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取 Parsec 自定义模式失败: " + ex.Message);
        }
        return list.OrderBy(m => m.Index).ToList();
    }

    public static bool Exists(int width, int height, int hz) => Read().Any(m => m.Width == width && m.Height == height && m.Hz == hz);

    /// <summary>Other tools (ParsecVDisplay) may store the values as strings or QWORDs.</summary>
    private static int? ReadInt(RegistryKey key, string name)
    {
        return key.GetValue(name) switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s.Trim(), out var p) => p,
            _ => null
        };
    }

    /// <summary>Compact fingerprint of the whole table (used to notice changes made by the user or other tools).</summary>
    public static string Signature() => string.Join(",", Read().Select(m => $"{m.Width}x{m.Height}@{m.Hz}"));

    /// <summary>Refresh rates registered for a resolution: what the driver publishes once the display is (re-)plugged.</summary>
    public static List<int> RegisteredRates(int width, int height)
        => Read().Where(m => m.Width == width && m.Height == height).Select(m => m.Hz).Distinct().OrderBy(r => r).ToList();

    /// <summary>Refresh rates Windows exposes right now for a resolution on a display (empty while it is switched off).</summary>
    public static List<int> ExposedRates(string? gdiName, int width, int height)
        => string.IsNullOrEmpty(gdiName)
            ? new List<int>()
            : DisplayManager.GetModes(gdiName, width, height).Where(m => m.Orientation == 0).Select(m => m.RefreshRate).Distinct().OrderBy(r => r).ToList();

    /// <summary>Rates usable now (exposed) or after a re-plug (registered).</summary>
    public static List<int> AvailableRates(string? gdiName, int width, int height)
        => ExposedRates(gdiName, width, height).Union(RegisteredRates(width, height)).OrderBy(r => r).ToList();

    /// <summary>
    /// True when the mode table Windows sees for a virtual display no longer matches the registry (a registered rate is
    /// not exposed, or an exposed custom rate has been deleted). The driver only re-reads the table for a freshly plugged
    /// monitor, so the display has to be removed and added again.
    /// </summary>
    public static bool NeedsReplug(DisplayInfo d, int width, int height)
    {
        if (!d.IsParsec || !d.IsReady) return false;
        var registered = RegisteredRates(width, height);
        if (registered.Count == 0) return false;
        var exposed = ExposedRates(d.GdiName, width, height);
        if (registered.Except(exposed).Any()) return true;
        // custom resolutions only exist through the registry: an exposed rate that is no longer registered is stale
        return !IsBuiltInResolution(width, height) && exposed.Except(registered).Any();
    }

    private static readonly HashSet<(int, int)> BuiltIn = new()
    {
        (4096, 2160), (3840, 2160), (3440, 1440), (2560, 1600), (2560, 1440), (1920, 1440), (1920, 1200), (1920, 1080),
        (1680, 1050), (1600, 900), (1440, 900), (1366, 768), (1280, 1024), (1280, 800), (1280, 720), (1024, 768), (800, 600)
    };

    /// <summary>Resolutions the Parsec driver publishes without any registry entry.</summary>
    public static bool IsBuiltInResolution(int width, int height) => BuiltIn.Contains((width, height));

    /// <summary>Registers a mode; when not elevated, spawns an elevated copy of the hub to do it.</summary>
    public static (bool Ok, string Message) EnsureRegistered(int width, int height, int hz)
    {
        if (Exists(width, height, hz)) return (true, "模式已存在");
        if (ProcessUtil.IsElevated())
        {
            return WriteElevated(width, height, hz);
        }
        Log.Info($"需要管理员权限注册 Parsec 自定义模式 {width}x{height}@{hz}，正在请求 UAC…");
        var code = ProcessUtil.RunSelfElevated($"--elevated add-parsec-mode {width}x{height}@{hz}");
        if (code == 0 && Exists(width, height, hz)) return (true, "模式已注册");
        return code switch
        {
            -1 => (false, "用户取消了 UAC 提示，无法写入自定义模式"),
            -2 => (false, "提权进程超时"),
            _ => (false, $"提权写入失败，退出码 {code}")
        };
    }

    public static (bool Ok, string Message) WriteElevated(int width, int height, int hz)
    {
        try
        {
            using var root = Registry.LocalMachine.CreateSubKey(RegistryPath, true);
            if (root == null) return (false, "无法打开注册表 " + RegistryPath);
            var existing = Read();
            if (existing.Any(m => m.Width == width && m.Height == height && m.Hz == hz)) return (true, "模式已存在");
            var used = existing.Select(m => m.Index).ToHashSet();
            var index = 0;
            while (used.Contains(index)) index++;
            if (index >= MaxCustomModes) return (false, $"自定义模式已达上限 {MaxCustomModes}，请先删除不用的模式");
            using var key = root.CreateSubKey(index.ToString(), true);
            key!.SetValue("width", width, RegistryValueKind.DWord);
            key.SetValue("height", height, RegistryValueKind.DWord);
            key.SetValue("hz", hz, RegistryValueKind.DWord);
            Log.Info($"已注册 Parsec 自定义模式 #{index}: {width}x{height}@{hz}");
            return (true, $"已注册为模式 #{index}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static (bool Ok, string Message) Remove(int index)
    {
        if (!ProcessUtil.IsElevated())
        {
            var code = ProcessUtil.RunSelfElevated($"--elevated remove-parsec-mode {index}");
            return code == 0 ? (true, "已删除") : (false, code == -1 ? "用户取消了 UAC 提示" : $"提权删除失败，退出码 {code}");
        }
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(RegistryPath, true);
            root?.DeleteSubKeyTree(index.ToString(), false);
            return (true, "已删除");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
