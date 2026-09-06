using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using MoonlightHub.Native;
using static MoonlightHub.Native.NativeMethods;

namespace MoonlightHub.Core;

/// <summary>One connected monitor (physical or virtual) as seen by the CCD API + GDI.</summary>
public sealed class DisplayInfo
{
    public string GdiName { get; set; } = string.Empty;     // \\.\DISPLAYn (only meaningful when active)
    public bool Active { get; set; }
    public bool Primary { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshRate { get; set; }
    public int PositionX { get; set; }
    public int PositionY { get; set; }
    public int Orientation { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public string DevicePath { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public byte[]? Edid { get; set; }
    public LUID AdapterId { get; set; }
    public uint SourceId { get; set; }
    public uint TargetId { get; set; }
    public uint ConnectorInstance { get; set; }
    public ushort EdidManufacturer { get; set; }
    public ushort EdidProduct { get; set; }
    public string SunshineDeviceId { get; set; } = string.Empty;
    public uint CloneGroup { get; set; }

    public bool IsParsec => DevicePath.Contains(ParsecVdd.MonitorId, StringComparison.OrdinalIgnoreCase) || InstanceId.Contains(ParsecVdd.MonitorId, StringComparison.OrdinalIgnoreCase);
    public bool IsInternalPanel => DevicePath.Contains("IVO", StringComparison.OrdinalIgnoreCase) || FriendlyName.Length == 0 && !IsParsec && EdidManufacturer != 0 && IsEmbeddedTechnology;
    public bool IsEmbeddedTechnology { get; set; }

    /// <summary>UID number embedded in the device path ("…&amp;UID256#…"), -1 when absent.</summary>
    public int Uid
    {
        get
        {
            var m = Regex.Match(DevicePath, @"UID(\d+)", RegexOptions.IgnoreCase);
            return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : -1;
        }
    }

    /// <summary>Best-effort Parsec driver slot (UID 256 → 0, 257 → 1 …). Validated at runtime by ProfileEngine.</summary>
    public int ParsecSlot => IsParsec && Uid >= 256 ? Uid - 256 : -1;

    /// <summary>Active and fully enumerated (GDI name and a current mode are available) — right after activation Windows can report an active path without them.</summary>
    public bool IsReady => Active && !string.IsNullOrEmpty(GdiName) && Width > 0 && Height > 0;

    public string Label => IsParsec ? $"Parsec 虚拟屏 #{ParsecSlot + 1}" : (string.IsNullOrWhiteSpace(FriendlyName) ? (IsEmbeddedTechnology ? "笔记本内置屏" : "显示器") : FriendlyName);
    public string ModeText => Active ? $"{Width}×{Height} @ {RefreshRate}Hz" : "未激活";
    public string PositionText => Active ? $"({PositionX}, {PositionY})" : "-";
    public string Key => $"{AdapterId}/{TargetId}";

    public override string ToString() => $"{GdiName} {Label} {ModeText} pos=({PositionX},{PositionY}) primary={Primary} active={Active} id={SunshineDeviceId}";
}

public sealed record DisplayMode(int Width, int Height, int RefreshRate, int Orientation)
{
    public override string ToString() => $"{Width}×{Height} @ {RefreshRate}Hz" + (Orientation != 0 ? $" (旋转 {Orientation * 90}°)" : "");
}

/// <summary>Desired state of one GDI display when applying a layout.</summary>
public sealed class LayoutItem
{
    public string GdiName { get; set; } = string.Empty;
    public bool Attached { get; set; } = true;
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? RefreshRate { get; set; }
    public int? Orientation { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool Primary { get; set; }
}

public sealed class LayoutResult
{
    public bool Success { get; set; }
    public List<string> Messages { get; } = new();
    public override string ToString() => string.Join("; ", Messages);
}

/// <summary>Enumerates and reconfigures displays using the CCD API and ChangeDisplaySettingsEx.</summary>
public static unsafe class DisplayManager
{
    private static readonly Guid MonitorInterfaceGuid = new("e6f07b5f-ee97-4a90-b076-33f57bf4eaa7");

    // ------------------------------------------------------------------ enumeration
    public static List<DisplayInfo> Enumerate()
    {
        var result = new List<DisplayInfo>();
        if (!QueryPaths(QDC_ALL_PATHS | QDC_VIRTUAL_MODE_AWARE, out var paths, out _))
        {
            return result;
        }

        // Group paths per target; a target is "connected" when targetAvailable != 0.
        var byTarget = new Dictionary<string, List<DISPLAYCONFIG_PATH_INFO>>();
        foreach (var p in paths)
        {
            if (p.targetInfo.targetAvailable == 0) continue;
            var key = $"{p.targetInfo.adapterId}/{p.targetInfo.id}";
            if (!byTarget.TryGetValue(key, out var list)) byTarget[key] = list = new List<DISPLAYCONFIG_PATH_INFO>();
            list.Add(p);
        }

        var primaryNames = GetPrimaryGdiNames();
        var monitorDb = ReadMonitorDatabase();

        foreach (var group in byTarget.Values)
        {
            var active = group.FirstOrDefault(p => (p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0);
            var isActive = (active.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0;
            var path = isActive ? active : group[0];

            var info = new DisplayInfo
            {
                Active = isActive,
                AdapterId = path.targetInfo.adapterId,
                TargetId = path.targetInfo.id,
                SourceId = path.sourceInfo.id,
                CloneGroup = isActive ? (path.sourceInfo.modeInfoIdx & 0xFFFF) : 0,
                IsEmbeddedTechnology = path.targetInfo.outputTechnology == 11 /* DISPLAYPORT_EMBEDDED */ || path.targetInfo.outputTechnology == 0x80000000 /* INTERNAL */
            };

            var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            target.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            target.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
            target.header.adapterId = path.targetInfo.adapterId;
            target.header.id = path.targetInfo.id;
            if (DisplayConfigGetDeviceInfo(ref target) == 0)
            {
                info.FriendlyName = target.monitorFriendlyDeviceName?.Trim() ?? string.Empty;
                info.DevicePath = target.monitorDevicePath ?? string.Empty;
                info.EdidManufacturer = target.edidManufactureId;
                info.EdidProduct = target.edidProductCodeId;
                info.ConnectorInstance = target.connectorInstance;
            }

            if (isActive)
            {
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                source.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                source.header.adapterId = path.sourceInfo.adapterId;
                source.header.id = path.sourceInfo.id;
                if (DisplayConfigGetDeviceInfo(ref source) == 0)
                {
                    info.GdiName = source.viewGdiDeviceName ?? string.Empty;
                }

                if (!string.IsNullOrEmpty(info.GdiName))
                {
                    var mode = DEVMODEW.Create();
                    if (EnumDisplaySettingsExW(info.GdiName, ENUM_CURRENT_SETTINGS, ref mode, 0))
                    {
                        info.Width = (int)mode.dmPelsWidth;
                        info.Height = (int)mode.dmPelsHeight;
                        info.RefreshRate = (int)mode.dmDisplayFrequency;
                        info.PositionX = mode.dmPositionX;
                        info.PositionY = mode.dmPositionY;
                        info.Orientation = (int)mode.dmDisplayOrientation;
                    }
                    info.Primary = primaryNames.Contains(info.GdiName, StringComparer.OrdinalIgnoreCase);
                }
            }

            if (!string.IsNullOrEmpty(info.DevicePath) && monitorDb.TryGetValue(info.DevicePath.ToLowerInvariant(), out var entry))
            {
                info.InstanceId = entry.InstanceId;
                info.Edid = entry.Edid;
            }
            info.SunshineDeviceId = SunshineDeviceId.Compute(info.InstanceId, info.Edid, info.DevicePath);
            result.Add(info);
        }

        return result
            .OrderByDescending(d => d.Active)
            .ThenByDescending(d => d.Primary)
            .ThenBy(d => d.IsParsec)
            .ThenBy(d => d.GdiName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<DisplayInfo> EnumerateActive() => Enumerate().Where(d => d.Active).ToList();

    private static HashSet<string> GetPrimaryGdiNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dev = DISPLAY_DEVICEW.Create();
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref dev, 0); i++)
        {
            if ((dev.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0 && (dev.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
            {
                set.Add(dev.DeviceName);
            }
            dev = DISPLAY_DEVICEW.Create();
        }
        return set;
    }

    /// <summary>All GDI adapters/sources known to the system (attached or not).</summary>
    public static List<(string Name, string Description, uint Flags)> EnumerateGdiSources()
    {
        var list = new List<(string, string, uint)>();
        var dev = DISPLAY_DEVICEW.Create();
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref dev, 0); i++)
        {
            list.Add((dev.DeviceName, dev.DeviceString, dev.StateFlags));
            dev = DISPLAY_DEVICEW.Create();
        }
        return list;
    }

    private sealed record MonitorDbEntry(string InstanceId, byte[]? Edid);

    /// <summary>Maps monitor device-interface paths (lowercase) to PnP instance id + EDID via SetupAPI.</summary>
    private static Dictionary<string, MonitorDbEntry> ReadMonitorDatabase()
    {
        var db = new Dictionary<string, MonitorDbEntry>();
        var guid = MonitorInterfaceGuid;
        var set = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return db;
        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifData); i++)
            {
                SetupDiGetDeviceInterfaceDetailW(set, ref ifData, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required == 0) { ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() }; continue; }
                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buffer, 8);
                    var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                    if (SetupDiGetDeviceInterfaceDetailW(set, ref ifData, buffer, required, out _, ref devInfo))
                    {
                        var path = ReadInterfaceDetailPath(buffer);
                        var instanceId = ReadInstanceId(set, ref devInfo);
                        var edid = ReadEdid(set, ref devInfo);
                        if (!string.IsNullOrEmpty(path))
                        {
                            db[path.ToLowerInvariant()] = new MonitorDbEntry(instanceId, edid);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
                ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return db;
    }

    private static string ReadInstanceId(IntPtr set, ref SP_DEVINFO_DATA devInfo)
    {
        SetupDiGetDeviceInstanceIdW(set, ref devInfo, IntPtr.Zero, 0, out var required);
        if (required == 0) return string.Empty;
        var buffer = Marshal.AllocHGlobal((int)required * 2);
        try
        {
            if (!SetupDiGetDeviceInstanceIdW(set, ref devInfo, buffer, required, out _)) return string.Empty;
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static byte[]? ReadEdid(IntPtr set, ref SP_DEVINFO_DATA devInfo)
    {
        var key = SetupDiOpenDevRegKey(set, ref devInfo, DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ);
        if (key == IntPtr.Zero || key == new IntPtr(-1)) return null;
        try
        {
            uint size = 0;
            var status = RegQueryValueExW(key, "EDID", IntPtr.Zero, out _, null, ref size);
            if (status != 0 || size == 0) return null;
            var data = new byte[size];
            status = RegQueryValueExW(key, "EDID", IntPtr.Zero, out _, data, ref size);
            if (status != 0) return null;
            if (size != data.Length) Array.Resize(ref data, (int)size);
            return data;
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    private static bool QueryPaths(uint flags, out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] modes)
    {
        paths = Array.Empty<DISPLAYCONFIG_PATH_INFO>();
        modes = Array.Empty<DISPLAYCONFIG_MODE_INFO>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var r = GetDisplayConfigBufferSizes(flags, out var np, out var nm);
            if (r != 0) return false;
            var p = new DISPLAYCONFIG_PATH_INFO[Math.Max(np, 1)];
            var m = new DISPLAYCONFIG_MODE_INFO[Math.Max(nm, 1)];
            r = QueryDisplayConfig(flags, ref np, p, ref nm, m, IntPtr.Zero);
            if (r == ERROR_INSUFFICIENT_BUFFER) continue;
            if (r != 0) return false;
            Array.Resize(ref p, (int)np);
            Array.Resize(ref m, (int)nm);
            paths = p;
            modes = m;
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ modes
    public static List<DisplayMode> GetModes(string gdiName, int? width = null, int? height = null)
    {
        var modes = new List<DisplayMode>();
        var seen = new HashSet<string>();
        for (var i = 0; ; i++)
        {
            var dm = DEVMODEW.Create();
            if (!EnumDisplaySettingsExW(gdiName, i, ref dm, 0)) break;
            if (dm.dmBitsPerPel != 32) continue;
            if (width.HasValue && dm.dmPelsWidth != width) continue;
            if (height.HasValue && dm.dmPelsHeight != height) continue;
            var mode = new DisplayMode((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency, (int)dm.dmDisplayOrientation);
            if (seen.Add($"{mode.Width}x{mode.Height}@{mode.RefreshRate}/{mode.Orientation}")) modes.Add(mode);
        }
        return modes.OrderByDescending(m => m.Width * m.Height).ThenByDescending(m => m.RefreshRate).ToList();
    }

    public static bool HasMode(string gdiName, int width, int height, int hz)
    {
        return GetModes(gdiName, width, height).Any(m => m.RefreshRate == hz && m.Orientation == 0);
    }

    public static DEVMODEW? GetCurrentMode(string gdiName)
    {
        var dm = DEVMODEW.Create();
        return EnumDisplaySettingsExW(gdiName, ENUM_CURRENT_SETTINGS, ref dm, 0) ? dm : null;
    }

    // ------------------------------------------------------------------ layout
    /// <summary>
    /// Stages every item with CDS_UPDATEREGISTRY|CDS_NORESET and then applies once. Positions are normalised so the
    /// primary display ends at (0,0). Detached items get a 0x0 mode which removes them from the desktop.
    /// </summary>
    public static LayoutResult ApplyLayout(IEnumerable<LayoutItem> items)
    {
        var list = items.ToList();
        var result = new LayoutResult { Success = true };
        var primary = list.FirstOrDefault(i => i.Primary && i.Attached);

        int ox = 0, oy = 0;
        if (primary != null)
        {
            var cur = GetCurrentMode(primary.GdiName);
            ox = primary.X ?? cur?.dmPositionX ?? 0;
            oy = primary.Y ?? cur?.dmPositionY ?? 0;
        }

        var staged = 0;
        foreach (var item in list)
        {
            var dm = GetCurrentMode(item.GdiName) ?? DEVMODEW.Create();
            dm.dmDeviceName = string.Empty;
            uint flags = CDS_UPDATEREGISTRY | CDS_NORESET;
            if (!item.Attached)
            {
                dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
                dm.dmPelsWidth = 0;
                dm.dmPelsHeight = 0;
            }
            else
            {
                dm.dmFields = DM_POSITION;
                if (item.Width.HasValue && item.Height.HasValue)
                {
                    var w = item.Width.Value;
                    var h = item.Height.Value;
                    dm.dmPelsWidth = (uint)w;
                    dm.dmPelsHeight = (uint)h;
                    dm.dmFields |= DM_PELSWIDTH | DM_PELSHEIGHT;
                }
                if (item.RefreshRate.HasValue)
                {
                    dm.dmDisplayFrequency = (uint)item.RefreshRate.Value;
                    dm.dmFields |= DM_DISPLAYFREQUENCY;
                }
                if (item.Orientation.HasValue)
                {
                    dm.dmDisplayOrientation = (uint)item.Orientation.Value;
                    dm.dmFields |= DM_DISPLAYORIENTATION;
                }
                dm.dmPositionX = (item.X ?? dm.dmPositionX) - ox;
                dm.dmPositionY = (item.Y ?? dm.dmPositionY) - oy;
                if (item.Primary) flags |= CDS_SET_PRIMARY;
            }

            var r = ChangeDisplaySettingsExW(item.GdiName, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
            if (r != DISP_CHANGE_SUCCESSFUL)
            {
                result.Success = false;
                result.Messages.Add($"{item.GdiName}: 暂存失败 {DispChangeName(r)}");
            }
            else
            {
                staged++;
            }
        }

        if (staged > 0)
        {
            var apply = ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            if (apply != DISP_CHANGE_SUCCESSFUL)
            {
                result.Success = false;
                result.Messages.Add("应用显示布局失败: " + DispChangeName(apply));
            }
            else
            {
                result.Messages.Add($"已应用 {staged} 个显示器的布局");
            }
        }
        return result;
    }

    public static int SetTopology(uint sdcTopologyFlag, bool allowChanges = true)
    {
        var flags = SDC_APPLY | sdcTopologyFlag;
        if (allowChanges) flags |= SDC_ALLOW_CHANGES;
        return SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, flags);
    }

    /// <summary>
    /// Activates exactly the given targets (adapter+target ids) using SDC_TOPOLOGY_SUPPLIED; Windows picks modes from
    /// its database. Used for "virtual display only" layouts where physical displays must be switched off.
    /// </summary>
    public static int SetActiveTargets(IEnumerable<(LUID Adapter, uint Target)> targets)
    {
        if (!QueryPaths(QDC_ALL_PATHS, out var all, out _)) return -1;
        var chosen = new List<DISPLAYCONFIG_PATH_INFO>();
        var usedSources = new HashSet<string>();
        foreach (var (adapter, target) in targets)
        {
            var candidates = all.Where(p => p.targetInfo.adapterId.Same(adapter) && p.targetInfo.id == target && p.targetInfo.targetAvailable != 0).ToList();
            if (candidates.Count == 0) return -2;
            var pick = candidates.FirstOrDefault(p => (p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0);
            if ((pick.flags & DISPLAYCONFIG_PATH_ACTIVE) == 0)
            {
                pick = candidates.FirstOrDefault(p => !usedSources.Contains($"{p.sourceInfo.adapterId}/{p.sourceInfo.id}"));
            }
            usedSources.Add($"{pick.sourceInfo.adapterId}/{pick.sourceInfo.id}");
            pick.flags = DISPLAYCONFIG_PATH_ACTIVE;
            pick.sourceInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            pick.targetInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            pick.sourceInfo.statusFlags = 0;
            pick.targetInfo.statusFlags = 0;
            chosen.Add(pick);
        }
        if (chosen.Count == 0) return -3;
        var arr = chosen.ToArray();
        return SetDisplayConfig((uint)arr.Length, arr, 0, null, SDC_APPLY | SDC_TOPOLOGY_SUPPLIED | SDC_ALLOW_PATH_ORDER_CHANGES | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
    }

    /// <summary>Polls Enumerate() until the predicate is satisfied or the timeout elapses.</summary>
    public static List<DisplayInfo> WaitFor(Func<List<DisplayInfo>, bool> predicate, TimeSpan timeout, int pollMs = 400)
    {
        var deadline = DateTime.UtcNow + timeout;
        List<DisplayInfo> last;
        do
        {
            last = Enumerate();
            if (predicate(last)) return last;
            Thread.Sleep(pollMs);
        } while (DateTime.UtcNow < deadline);
        return last;
    }
}
