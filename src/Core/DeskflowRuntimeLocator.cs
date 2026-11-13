using System.Globalization;
using System.IO;

namespace MoonlightHub.Core;

/// <summary>只选择 Hub 随附或历史上由 Hub 管理的完整 Windows 键鼠组件。</summary>
internal static class DeskflowRuntimeLocator
{
    private static readonly string[] RequiredFiles =
    [
        "deskflow-core.exe", "deskflow_touch_filter.dll", "moonlight-touch-keyboard-release.exe",
        Path.Combine("native", "deskflow-core.exe"),
        Path.Combine("native", "Qt6Core.dll"), Path.Combine("native", "Qt6Gui.dll"),
        Path.Combine("native", "Qt6Network.dll"), Path.Combine("native", "Qt6Svg.dll"),
        Path.Combine("native", "Qt6Widgets.dll"), Path.Combine("native", "libcrypto-3-x64.dll"),
        Path.Combine("native", "libssl-3-x64.dll")
    ];

    // 参数 exeDir：Hub 所在目录；bridgeRoot：旧版 Hub 管理目录；fileExists/getDirectories：文件快照查询。
    internal static string? Resolve(
        string exeDir, string bridgeRoot, Func<string, bool> fileExists, Func<string, IEnumerable<string>> getDirectories)
    {
        var bundled = Path.Combine(exeDir, "deskflow");
        if (IsComplete(bundled, fileExists)) return bundled;

        return getDirectories(bridgeRoot)
            .Select(path => (Path: path, Version: VersionOf(path)))
            .Where(entry => entry.Version >= 0)
            .OrderByDescending(entry => entry.Version)
            .Select(entry => entry.Path)
            .FirstOrDefault(path => IsComplete(path, fileExists));
    }

    // 参数 root：同一份运行时的根目录；fileExists：文件存在性查询，禁止跨目录拼凑依赖。
    private static bool IsComplete(string root, Func<string, bool> fileExists) =>
        RequiredFiles.All(name => fileExists(Path.Combine(root, name)));

    // 参数 runtime：当前完整组件根目录；exeDir：Hub 目录；fileExists：文件快照查询。
    internal static string ResolveBridge(string? runtime, string exeDir, Func<string, bool> fileExists)
    {
        var bundled = runtime == null ? null : Path.Combine(runtime, "moonlight-touch-bridge.exe");
        return bundled != null && fileExists(bundled) ? bundled : Path.Combine(exeDir, "tools", "moonlight-touch-bridge.exe");
    }

    // 参数 requireSelectedRuntime：仅 Windows 核心需要迁移；expectedCore：当前选定核心；corePaths：运行中的原生核心映像路径。
    internal static bool? UsesSelectedCore(bool requireSelectedRuntime, string? expectedCore, IEnumerable<string?> corePaths)
    {
        if (!requireSelectedRuntime) return null;
        if (string.IsNullOrWhiteSpace(expectedCore) || !Path.IsPathFullyQualified(expectedCore)) return false;
        try
        {
            var expected = Path.GetFullPath(expectedCore);
            var found = false;
            foreach (var path in corePaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
                    !Path.GetFullPath(path).Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;
                found = true;
            }
            return found;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // 参数 path：旧版 Hub 管理的版本目录，只接受 v 后紧跟非负整数的名称。
    private static int VersionOf(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith('v') && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? version : -1;
    }
}
