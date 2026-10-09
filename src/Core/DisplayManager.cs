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
    /// <summary>从已枚举的显示器创建布局项，记录设备路径，防止显示编号变化后误操作其他屏幕。</summary>
    /// <param name="display">此次布局操作实际对应的显示器快照。</param>
    public LayoutItem(DisplayInfo display)
    {
        GdiName = display.GdiName;
        ExpectedDevicePath = display.DevicePath;
        RequireParsec = display.IsParsec;
    }

    public string GdiName { get; set; } = string.Empty;
    public string ExpectedDevicePath { get; set; } = string.Empty;
    public bool RequireParsec { get; set; }
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
        if (string.IsNullOrWhiteSpace(gdiName)) return modes;
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
        if (string.IsNullOrWhiteSpace(gdiName)) return null;
        var dm = DEVMODEW.Create();
        return EnumDisplaySettingsExW(gdiName, ENUM_CURRENT_SETTINGS, ref dm, 0) ? dm : null;
    }

    // ------------------------------------------------------------------ layout
    /// <summary>核对布局目标的真实身份；返回错误说明时，调用方必须停止全部写入。</summary>
    /// <param name="items">携带原始设备路径的待应用布局项。</param>
    /// <param name="displays">写入前重新枚举的显示器，用于检测断开、编号变化和复制模式。</param>
    internal static string? ValidateLayoutTargets(IReadOnlyList<LayoutItem> items, IReadOnlyList<DisplayInfo> displays)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.GdiName) || string.IsNullOrWhiteSpace(item.ExpectedDevicePath))
                return "显示器名称或设备路径为空，请等待显示器连接完成";
            if (!names.Add(item.GdiName)) return $"{item.GdiName}: 多个布局项使用同一显示源";

            var target = displays.FirstOrDefault(d => d.DevicePath.Equals(item.ExpectedDevicePath, StringComparison.OrdinalIgnoreCase));
            if (target == null || !target.IsReady) return $"{item.GdiName}: 原目标显示器已断开或尚未就绪";
            if (!target.GdiName.Equals(item.GdiName, StringComparison.OrdinalIgnoreCase))
                return $"{item.GdiName}: 显示编号已变化，请重新读取布局";
            if (item.RequireParsec && !target.IsParsec) return $"{item.GdiName}: 原目标不再是 Parsec 虚拟屏";
            if (item.RequireParsec && displays.Any(d => d.Active && !d.IsParsec && d.GdiName.Equals(item.GdiName, StringComparison.OrdinalIgnoreCase)))
                return $"{item.GdiName}: 虚拟屏与物理屏共用显示源，无法单独修改虚拟屏";
        }
        return null;
    }

    /// <summary>
    /// Stages every item with CDS_UPDATEREGISTRY|CDS_NORESET and then applies once. Positions are normalised so the
    /// primary display ends at (0,0). Detached items get a 0x0 mode which removes them from the desktop.
    /// </summary>
    /// <param name="items">带原显示器设备路径的布局项；目标缺失或变化时拒绝整批应用。</param>
    public static LayoutResult ApplyLayout(IEnumerable<LayoutItem> items)
    {
        var list = items.ToList();
        var result = new LayoutResult { Success = true };
        if (list.Count == 0) return result;
        var error = ValidateLayoutTargets(list, Enumerate());
        if (error != null)
        {
            result.Success = false;
            result.Messages.Add("拒绝应用显示布局: " + error);
            return result;
        }

        // 所有目标的模式都必须可读；检查完成前不暂存任何改动，避免半套布局生效。
        var currentModes = new Dictionary<string, DEVMODEW>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list)
        {
            var mode = GetCurrentMode(item.GdiName);
            if (!mode.HasValue)
            {
                result.Success = false;
                result.Messages.Add($"拒绝应用显示布局: {item.GdiName} 的当前模式不可用");
                return result;
            }
            currentModes[item.GdiName] = mode.Value;
        }
        // 模式读取期间也可能发生热插拔；在暂存前再次核对设备身份。
        error = ValidateLayoutTargets(list, Enumerate());
        if (error != null)
        {
            result.Success = false;
            result.Messages.Add("拒绝应用显示布局: " + error);
            return result;
        }
        var primary = list.FirstOrDefault(i => i.Primary && i.Attached);

        int ox = 0, oy = 0;
        if (primary != null)
        {
            var cur = currentModes[primary.GdiName];
            ox = primary.X ?? cur.dmPositionX;
            oy = primary.Y ?? cur.dmPositionY;
        }

        var staged = 0;
        foreach (var item in list)
        {
            var dm = currentModes[item.GdiName];
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
        // Same recipe as Sunshine's libdisplaydevice (win_api_utils::makePathsForNewTopology + doSetTopology):
        // virtual-mode-aware paths, every mode index cleared, one clone group per display, path marked active.
        if (!QueryPaths(QDC_ALL_PATHS | QDC_VIRTUAL_MODE_AWARE, out var all, out _)) return -1;
        var chosen = new List<DISPLAYCONFIG_PATH_INFO>();
        var usedSources = new HashSet<string>();
        uint group = 0;
        foreach (var (adapter, target) in targets)
        {
            var candidates = all.Where(p => p.targetInfo.adapterId.Same(adapter) && p.targetInfo.id == target && p.targetInfo.targetAvailable != 0).ToList();
            if (candidates.Count == 0) return -2;
            // 复制拓扑中的多个目标可能共用一个活动源；拆为独立屏时，每个源只能使用一次。
            var active = candidates.Where(p => (p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0 &&
                !usedSources.Contains($"{p.sourceInfo.adapterId}/{p.sourceInfo.id}")).ToList();
            DISPLAYCONFIG_PATH_INFO pick;
            if (active.Count > 0)
            {
                pick = active[0];
            }
            else
            {
                // lowest free source id on that adapter (closest to what Windows itself would choose)
                var free = candidates.Where(p => !usedSources.Contains($"{p.sourceInfo.adapterId}/{p.sourceInfo.id}")).OrderBy(p => p.sourceInfo.id).ToList();
                if (free.Count == 0) return -4;
                pick = free[0];
            }
            usedSources.Add($"{pick.sourceInfo.adapterId}/{pick.sourceInfo.id}");
            pick.flags = DISPLAYCONFIG_PATH_ACTIVE;
            pick.sourceInfo.modeInfoIdx = (0xFFFFu << 16) | (group & 0xFFFF); // sourceModeInfoIdx = invalid, cloneGroupId = group
            pick.targetInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;   // desktopModeInfoIdx + targetModeInfoIdx = invalid
            chosen.Add(pick);
            group++;
        }
        if (chosen.Count == 0) return -3;
        var arr = chosen.ToArray();
        var result = SetDisplayConfig((uint)arr.Length, arr, 0, null, SDC_APPLY | SDC_TOPOLOGY_SUPPLIED | SDC_ALLOW_PATH_ORDER_CHANGES | SDC_VIRTUAL_MODE_AWARE);
        if (result == 31 /* ERROR_GEN_FAILURE: no database entry for this topology */)
        {
            result = SetDisplayConfig((uint)arr.Length, arr, 0, null, SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_VIRTUAL_MODE_AWARE | SDC_SAVE_TO_DATABASE);
        }
        return result;
    }

    /// <summary>
    /// Re-enables displays that are connected but switched off (stand-by) by supplying an explicit topology:
    /// every currently active target plus the given ones. SDC_TOPOLOGY_EXTEND cannot be used for this because Windows
    /// would just replay the database entry that recorded the display as detached.
    /// </summary>
    public static int ActivateDisplays(IEnumerable<DisplayInfo> toEnable, IEnumerable<DisplayInfo> currentlyActive)
    {
        var targets = currentlyActive.Where(d => d.Active).Select(d => (d.AdapterId, d.TargetId)).ToList();
        foreach (var d in toEnable)
        {
            if (!targets.Any(t => t.AdapterId.Same(d.AdapterId) && t.TargetId == d.TargetId)) targets.Add((d.AdapterId, d.TargetId));
        }
        return SetActiveTargets(targets);
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
