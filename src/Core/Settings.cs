using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoonlightHub.Core;

public enum ProfileMode
{
    /// <summary>Physical displays only; no virtual display. Instance 1 may keep streaming the primary display.</summary>
    MainOnly,
    /// <summary>Physical displays stay, N Parsec virtual displays are added and extended.</summary>
    Extend,
    /// <summary>Only the first virtual display is active (lowest latency, physical panels off).</summary>
    VirtualOnly,
    /// <summary>Primary display is cloned onto the first virtual display.</summary>
    Clone
}

public enum Placement { Right, Left, Above, Below, Custom }

public sealed class VirtualDisplaySpec
{
    public int Slot { get; set; }
    public int Width { get; set; } = 2000;
    public int Height { get; set; } = 1200;
    public int RefreshRate { get; set; } = 60;
    public Placement Placement { get; set; } = Placement.Right;
    public int? X { get; set; }
    public int? Y { get; set; }
    /// <summary>Sunshine instance id that streams this display (0 = none).</summary>
    public int InstanceId { get; set; } = 1;

    public VirtualDisplaySpec Clone() => (VirtualDisplaySpec)MemberwiseClone();
    public string ModeText => $"{Width}×{Height} @ {RefreshRate}Hz";
}

public sealed class InstanceSpec
{
    public int Id { get; set; }
    public string Name { get; set; } = "Desktop";
    public int Port { get; set; } = 47989;
    public bool Audio { get; set; }
    public bool SystemTray { get; set; }
    public int MinimumFpsTarget { get; set; } = 60;
    public bool Enabled { get; set; } = true;

    [JsonIgnore] public int WebPort => Port + 1;
    [JsonIgnore] public int HttpsPort => Port - 5;
    [JsonIgnore] public int RtspPort => Port + 21;
    [JsonIgnore] public int[] RequiredTcpPorts => new[] { Port - 5, Port, Port + 1, Port + 21 };
}

public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新方案";
    public string Description { get; set; } = string.Empty;
    public string Glyph { get; set; } = "\uE7F4";
    public ProfileMode Mode { get; set; } = ProfileMode.Extend;
    public List<VirtualDisplaySpec> VirtualDisplays { get; set; } = new();
    /// <summary>MainOnly: keep instance 1 running and let it capture the primary display.</summary>
    public bool KeepMainInstance { get; set; } = true;
    /// <summary>Virtual display refresh rate is raised to this value while the stream fps stays the same (0 = same as stream).</summary>
    public int ComposeRefreshRate { get; set; }
    /// <summary>
    /// Raise the virtual display refresh rate to the fps a connected Moonlight client asks for. The per-display value is
    /// the floor; the result is snapped to a rate the driver actually has (exact, else a multiple, else the next higher one).
    /// </summary>
    public bool MatchClientFps { get; set; } = true;
    /// <summary>When the user drags a virtual display in Windows display settings, remember that position for this profile.</summary>
    public bool RememberManualPosition { get; set; } = true;
    /// <summary>Size the virtual display to the resolution the Moonlight client asks for (portrait requests are transposed when Sunshine pre-rotation is on).</summary>
    public bool AutoMatchClientResolution { get; set; } = true;
    /// <summary>
    /// Keep the virtual display connected but switched off while no Moonlight client is streaming; Sunshine's display
    /// device module (ensure_active / ensure_only_display + revert on disconnect) turns it on for the session.
    /// </summary>
    public bool IdleOff { get; set; } = true;

    public Profile Clone()
    {
        var c = (Profile)MemberwiseClone();
        c.VirtualDisplays = VirtualDisplays.Select(v => v.Clone()).ToList();
        return c;
    }

    public int VirtualCount => Mode == ProfileMode.MainOnly ? 0 : (Mode == ProfileMode.Extend ? VirtualDisplays.Count : Math.Min(1, VirtualDisplays.Count));
    public string ModeText => Mode switch
    {
        ProfileMode.MainOnly => "仅物理主屏",
        ProfileMode.Extend => $"主屏 + {VirtualDisplays.Count} 个虚拟副屏",
        ProfileMode.VirtualOnly => "仅虚拟副屏（低延迟）",
        ProfileMode.Clone => "复制主屏到虚拟副屏",
        _ => Mode.ToString()
    };
}

/// <summary>What Sunshine's display-device module should do for an instance (written to its sunshine.conf as dd_* keys).</summary>
public sealed record DdOptions(string Configuration, int Width, int Height, int? ManualHz, bool RevertOnDisconnect, int RevertDelayMs = 3000)
{
    public static DdOptions Disabled => new("disabled", 0, 0, null, false);
    public bool Enabled => Configuration != "disabled";
    public override string ToString() => Enabled ? $"{Configuration} {Width}x{Height}@{(ManualHz?.ToString() ?? "auto")} revert={RevertOnDisconnect}" : "disabled";
}

public sealed class Credential
{
    public string User { get; set; } = string.Empty;
    public string PasswordProtected { get; set; } = string.Empty;
}

public sealed class HubSettings
{
    public string SunshineExe { get; set; } = @"D:\YingYong\Sunshine\sunshine.exe";
    public string Instance1ConfigDir { get; set; } = @"D:\YingYong\Sunshine\config";
    public string InstancesRoot { get; set; } = @"D:\YingYong\Sunshine\instances";
    public List<InstanceSpec> Instances { get; set; } = new();
    public List<Profile> Profiles { get; set; } = new();
    public string ActiveProfileId { get; set; } = string.Empty;
    public bool AutoApplyOnStart { get; set; } = true;
    public int StartupDelaySeconds { get; set; } = 8;
    public int VddPingMs { get; set; } = 50;
    public int WatchdogSeconds { get; set; } = 5;
    public bool WatchdogEnabled { get; set; } = true;
    public bool HighProcessPriority { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public Dictionary<int, Credential> Credentials { get; set; } = new();
    public string ScriptsDir { get; set; } = string.Empty;
    public string AdbPath { get; set; } = string.Empty;
    public string ParsecVDisplayExe { get; set; } = @"D:\YingYong\ParsecVDisplay\ParsecVDisplay.exe";
    public bool LegacyTasksTakenOver { get; set; }
    public string Theme { get; set; } = "dark";
    /// <summary>Let the Hub create/reset Sunshine web credentials itself so pairing works without the web UI.</summary>
    public bool AutoManageCredentials { get; set; } = true;
    /// <summary>Pop up the PIN window automatically when a client starts pairing.</summary>
    public bool AutoPairingPrompt { get; set; } = true;
    /// <summary>Use the Sunshine runtime shipped inside the hub package (dist\sunshine) instead of a separately installed Sunshine.</summary>
    public bool UseEmbeddedSunshine { get; set; } = true;
    /// <summary>Run the hub with administrator rights so it can restart / install the Parsec driver and register modes without UAC prompts.</summary>
    public bool RunElevated { get; set; } = true;
    /// <summary>Install the bundled Parsec driver automatically when the device is missing (needs administrator rights).</summary>
    public bool AutoInstallDriver { get; set; } = true;
    /// <summary>Restart the Parsec adapter automatically when it stops responding (needs administrator rights).</summary>
    public bool AutoRestartAdapter { get; set; } = true;
    /// <summary>
    /// Extend profiles: when a tablet's Moonlight disconnects, keep the virtual display (and the windows on it) and ask
    /// before switching it off, instead of letting Sunshine revert the display configuration a few seconds later.
    /// </summary>
    public bool ConfirmStandbyOnDisconnect { get; set; } = true;
    /// <summary>Set once the legacy Sunshine config was copied into the hub data directory.</summary>
    public bool SunshineConfigMigrated { get; set; }

    [JsonIgnore] public string SunshineDir => Path.GetDirectoryName(SunshineExe) ?? @"D:\YingYong\Sunshine";
    [JsonIgnore] public bool SunshineIsEmbedded => string.Equals(SunshineExe, Paths.EmbeddedSunshineExe, StringComparison.OrdinalIgnoreCase);

    public InstanceSpec? Instance(int id) => Instances.FirstOrDefault(i => i.Id == id);
    public Profile? Profile(string id) => Profiles.FirstOrDefault(p => p.Id == id);

    public static HubSettings CreateDefault()
    {
        var s = new HubSettings();
        s.Instances = new List<InstanceSpec>
        {
            // the name Moonlight lists the host under: this computer's name (a second PC must not show up as another one)
            new() { Id = 1, Name = Environment.MachineName, Port = 47989, Audio = true, SystemTray = false, MinimumFpsTarget = 60 },
            new() { Id = 2, Name = Environment.MachineName + "-2", Port = 48989, Audio = false, SystemTray = false, MinimumFpsTarget = 60 },
            new() { Id = 3, Name = Environment.MachineName + "-3", Port = 49989, Audio = false, SystemTray = false, MinimumFpsTarget = 60 },
        };
        s.Profiles = new List<Profile>
        {
            new() { Id = "main-only", Name = "仅主屏", Description = "关闭所有虚拟副屏，只保留物理显示器。Sunshine 实例 1 继续串流主屏。", Glyph = "\uE7F4", Mode = ProfileMode.MainOnly },
            new() { Id = "extend-1", Name = "主屏 + 1 副屏", Description = "创建 1 块 2000×1200 虚拟屏放在主屏右侧，由实例 1 串流。", Glyph = "\uE8A9", Mode = ProfileMode.Extend,
                VirtualDisplays = { new VirtualDisplaySpec { Slot = 0, InstanceId = 1 } } },
            new() { Id = "extend-2", Name = "主屏 + 2 副屏", Description = "创建 2 块虚拟屏，分别由实例 1 / 实例 2 串流，可同时连接两台 Moonlight。", Glyph = "\uE8A9", Mode = ProfileMode.Extend,
                VirtualDisplays = { new VirtualDisplaySpec { Slot = 0, InstanceId = 1 }, new VirtualDisplaySpec { Slot = 1, InstanceId = 2 } } },
            new() { Id = "extend-3", Name = "主屏 + 3 副屏", Description = "创建 3 块虚拟屏，三台 Moonlight 客户端各接一块。", Glyph = "\uE8A9", Mode = ProfileMode.Extend,
                VirtualDisplays = { new VirtualDisplaySpec { Slot = 0, InstanceId = 1 }, new VirtualDisplaySpec { Slot = 1, InstanceId = 2 }, new VirtualDisplaySpec { Slot = 2, InstanceId = 3 } } },
            new() { Id = "virtual-only", Name = "仅副屏（低延迟）", Description = "关闭物理屏，只保留 1 块虚拟屏并设为主屏，DWM 只合成这一块屏。", Glyph = "\uE7F8", Mode = ProfileMode.VirtualOnly,
                VirtualDisplays = { new VirtualDisplaySpec { Slot = 0, InstanceId = 1 } } },
            new() { Id = "clone", Name = "复制到副屏", Description = "主屏画面复制到虚拟屏，平板看到与主屏相同的内容。", Glyph = "\uE8C8", Mode = ProfileMode.Clone,
                VirtualDisplays = { new VirtualDisplaySpec { Slot = 0, InstanceId = 1 } } },
        };
        s.ActiveProfileId = "extend-1";
        return s;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static HubSettings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<HubSettings>(File.ReadAllText(Paths.SettingsFile), JsonOptions);
                if (loaded != null)
                {
                    var defaults = CreateDefault();
                    if (loaded.Instances.Count == 0) loaded.Instances = defaults.Instances;
                    if (loaded.Profiles.Count == 0) loaded.Profiles = defaults.Profiles;
                    loaded.ResolveDerivedPaths();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取设置失败，使用默认设置", ex);
        }
        var fresh = CreateDefault();
        fresh.ResolveDerivedPaths();
        return fresh;
    }

    public void ResolveDerivedPaths()
    {
        ResolveSunshinePaths();
        var scriptsCandidate = Path.Combine(Paths.RepoRoot, "scripts");
        if (string.IsNullOrWhiteSpace(ScriptsDir) || (!File.Exists(Path.Combine(ScriptsDir, "toggle-usb-network.ps1")) && Directory.Exists(scriptsCandidate)))
        {
            ScriptsDir = Directory.Exists(scriptsCandidate) ? scriptsCandidate : Paths.RepoRoot;
        }
        if (string.IsNullOrWhiteSpace(AdbPath) || !File.Exists(AdbPath))
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(Paths.RepoRoot, "moonlight-client", "android-platform-tools", "platform-tools", "adb.exe"),
                         Path.Combine(Paths.RepoRoot, "android-platform-tools", "platform-tools", "adb.exe"),
                     })
            {
                if (File.Exists(candidate)) { AdbPath = candidate; break; }
            }
        }
    }

    /// <summary>
    /// Prefers the Sunshine runtime shipped with the hub. Its configuration lives in the hub data directory; the first
    /// time the embedded runtime is selected the legacy configuration (paired clients, certificates, apps) is copied
    /// over so nothing has to be paired again. A separately installed Sunshine is still used when the package has none
    /// or the user switched the embedded runtime off.
    /// </summary>
    public void ResolveSunshinePaths()
    {
        var legacyExe = SunshineExe;
        var legacyConfig = Instance1ConfigDir;
        if (UseEmbeddedSunshine && Paths.HasEmbeddedSunshine)
        {
            var newConfig = Paths.EmbeddedSunshineConfigDir;
            if (!SunshineIsEmbedded || !string.Equals(Instance1ConfigDir, newConfig, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Directory.CreateDirectory(newConfig);
                    if (!File.Exists(Path.Combine(newConfig, "sunshine.conf")) && Directory.Exists(legacyConfig) && File.Exists(Path.Combine(legacyConfig, "sunshine.conf")))
                    {
                        CopyDirectory(legacyConfig, newConfig, skip: name => name.EndsWith(".log", StringComparison.OrdinalIgnoreCase));
                        Log.Info($"已把 Sunshine 配置从 {legacyConfig} 迁移到 {newConfig}（配对信息与证书一并保留）");
                    }
                    var legacyInstances = InstancesRoot;
                    var newInstances = Paths.EmbeddedSunshineInstancesRoot;
                    if (Directory.Exists(legacyInstances) && !Directory.Exists(newInstances))
                    {
                        CopyDirectory(legacyInstances, newInstances, skip: name => name.EndsWith(".log", StringComparison.OrdinalIgnoreCase));
                    }
                    SunshineExe = Paths.EmbeddedSunshineExe;
                    Instance1ConfigDir = newConfig;
                    InstancesRoot = newInstances;
                    SunshineConfigMigrated = true;
                    Log.Info($"使用随附的 Sunshine: {SunshineExe}（配置目录 {Instance1ConfigDir}）");
                }
                catch (Exception ex)
                {
                    Log.Warn("切换到随附 Sunshine 失败，继续使用 " + legacyExe + ": " + ex.Message);
                }
            }
        }
        else if (SunshineIsEmbedded && !Paths.HasEmbeddedSunshine)
        {
            // package no longer ships Sunshine (e.g. source checkout): fall back to the last known external install
            if (File.Exists(@"D:\YingYong\Sunshine\sunshine.exe")) SunshineExe = @"D:\YingYong\Sunshine\sunshine.exe";
        }
    }

    private static void CopyDirectory(string source, string target, Func<string, bool>? skip = null)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            if (skip != null && skip(name)) continue;
            var dest = Path.Combine(target, name);
            if (!File.Exists(dest)) File.Copy(file, dest);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)), skip);
        }
    }

    public void Save()
    {
        Paths.EnsureDataDirs();
        var tmp = Paths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, Paths.SettingsFile, true);
    }

    public string? GetPassword(int instanceId)
    {
        if (!Credentials.TryGetValue(instanceId, out var c) || string.IsNullOrEmpty(c.PasswordProtected)) return null;
        try { return Dpapi.Unprotect(c.PasswordProtected); } catch { return null; }
    }

    public string? GetUser(int instanceId) => Credentials.TryGetValue(instanceId, out var c) ? c.User : null;

    public void SetCredential(int instanceId, string user, string password)
    {
        Credentials[instanceId] = new Credential { User = user, PasswordProtected = Dpapi.Protect(password) };
    }
}

/// <summary>Runtime state persisted between launches (what the watchdog should expect).</summary>
public sealed class HubState
{
    public string ActiveProfileId { get; set; } = string.Empty;
    public DateTime LastAppliedUtc { get; set; }
    public bool LastApplySucceeded { get; set; }
    public int ExpectedVirtualDisplays { get; set; }
    public List<int> ExpectedInstances { get; set; } = new();
    public Dictionary<int, string> InstanceOutputIds { get; set; } = new();
    /// <summary>Last frame rate requested by a client per instance id (used to pre-select the virtual display refresh rate).</summary>
    public Dictionary<int, int> LastClientFps { get; set; } = new();
    /// <summary>Last stream mode (client-side WxH@fps) requested per instance id.</summary>
    public Dictionary<int, ClientMode> LastClientMode { get; set; } = new();
    /// <summary>Display-device options written to each instance's config by the last apply.</summary>
    public Dictionary<int, DdOptions> InstanceDd { get; set; } = new();
    /// <summary>Virtual display slots whose driver mode table changed while a client was streaming; re-plugged once idle.</summary>
    public List<int> PendingReplugSlots { get; set; } = new();
    /// <summary>Refresh rates Windows last exposed per virtual display slot and resolution ("slot:WxH"): lets a switched-off (stand-by) display be configured without turning it on.</summary>
    public Dictionary<string, List<int>> ExposedRates { get; set; } = new();
    /// <summary>Device ids of the physical displays that were active right after the last apply (virtual displays come and go with stand-by).</summary>
    public List<string> LastAppliedActiveIds { get; set; } = new();
    /// <summary>Last known arrangement of the physical displays (device id → position/primary), restored on every apply and after reboots.</summary>
    public Dictionary<string, DisplayPlacement> PhysicalLayout { get; set; } = new();

    public static HubState Load()
    {
        try
        {
            if (File.Exists(Paths.StateFile))
            {
                return JsonSerializer.Deserialize<HubState>(File.ReadAllText(Paths.StateFile)) ?? new HubState();
            }
        }
        catch { }
        return new HubState();
    }

    public void Save()
    {
        try
        {
            Paths.EnsureDataDirs();
            File.WriteAllText(Paths.StateFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warn("保存运行状态失败: " + ex.Message);
        }
    }
}

/// <summary>Position of one physical display as the user arranged it (part of <see cref="HubState.PhysicalLayout"/>).</summary>
public sealed class DisplayPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Primary { get; set; }
    public string Label { get; set; } = string.Empty;
}
