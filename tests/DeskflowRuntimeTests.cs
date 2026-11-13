using System.Reflection;
using MoonlightHub.Core;

namespace MoonlightHub.Tests;

internal static class DeskflowRuntimeTests
{
    private static readonly MethodInfo ResolveMethod = typeof(DeskflowManager).Assembly
        .GetType("MoonlightHub.Core.DeskflowRuntimeLocator")?.GetMethod("Resolve", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("找不到 Hub 随附键鼠组件解析方法");
    private static readonly MethodInfo ResolveBridgeMethod = typeof(DeskflowManager).Assembly
        .GetType("MoonlightHub.Core.DeskflowRuntimeLocator")?.GetMethod("ResolveBridge", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("找不到 Hub 随附触摸桥解析方法");

    // 参数 tests：接收只使用内存文件快照的组件选择回归，不读取真实安装目录或启动核心。
    internal static void AddTo(List<(string Name, Action Run)> tests)
    {
        tests.Add(("随附 Deskflow 优先于旧版 Hub 管理目录", BundledWins));
        tests.Add(("随附组件缺失时兼容旧版 Hub 完整组件", IncompleteBundleFallsBack));
        tests.Add(("Deskflow 核心存在但缺少过滤器不能当作完整组件", CoreOnlyRejected));
        tests.Add(("Deskflow 缺少依赖 DLL 时不能当作完整组件", MissingDependencyRejected));
        tests.Add(("Deskflow 缺少键盘释放监视器时不能当作完整组件", MissingMonitorRejected));
        tests.Add(("旧版 Hub 目录按数值选择最新完整组件", NumericVersionOrder));
        tests.Add(("旧版 Hub 最新目录不完整时回退完整版本", IncompleteNewestFallsBack));
        tests.Add(("已禁用或非整数版本目录不能被选中", InvalidVersionNamesRejected));
        tests.Add(("不从独立安装目录或旧配置目录加载 Deskflow", ExternalInstallRejected));
        tests.Add(("不同目录的 Deskflow 文件不能拼凑成完整组件", SplitRuntimeRejected));
        tests.Add(("触摸桥优先使用当前随附组件中的新版本", BundledBridgeWins));
        tests.Add(("旧版 Hub 组件没有触摸桥时兼容 tools 副本", LegacyBridgeFallsBack));
    }

    private const string ExeDir = @"C:\Hub";
    private const string BridgeRoot = @"C:\User\MoonlightDeskflowBridge";
    private static string Bundled => Path.Combine(ExeDir, "deskflow");
    private static string Legacy(string version) => Path.Combine(BridgeRoot, version);

    // 参数 roots：需要构造为完整运行时的目录；返回独立的内存文件集合。
    private static HashSet<string> Files(params string[] roots)
    {
        string[] relative =
        [
            "deskflow-core.exe", "deskflow_touch_filter.dll", "moonlight-touch-keyboard-release.exe",
            @"native\deskflow-core.exe", @"native\Qt6Core.dll", @"native\Qt6Gui.dll", @"native\Qt6Network.dll",
            @"native\Qt6Svg.dll", @"native\Qt6Widgets.dll", @"native\libcrypto-3-x64.dll", @"native\libssl-3-x64.dll"
        ];
        return roots.SelectMany(root => relative.Select(name => Path.Combine(root, name))).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // 参数 files：模拟已存在文件；directories：模拟旧版 Hub 的候选目录；expected：预期完整运行时。
    private static void Expect(HashSet<string> files, string[] directories, string? expected)
    {
        Func<string, bool> exists = files.Contains;
        Func<string, IEnumerable<string>> list = root =>
        {
            Program.Assert(root == BridgeRoot, "不应扫描独立 Deskflow 安装目录");
            return directories;
        };
        var result = (string?)ResolveMethod.Invoke(null, [ExeDir, BridgeRoot, exists, list]);
        Program.Assert(result == expected, $"预期 {expected ?? "无组件"}，实际 {result ?? "无组件"}");
    }

    private static void BundledWins() => Expect(Files(Bundled, Legacy("v99")), [Legacy("v99")], Bundled);

    private static void IncompleteBundleFallsBack()
    {
        var files = Files(Bundled, Legacy("v25"));
        files.Remove(Path.Combine(Bundled, "deskflow_touch_filter.dll"));
        Expect(files, [Legacy("v25")], Legacy("v25"));
    }

    private static void CoreOnlyRejected() =>
        Expect([Path.Combine(Bundled, "native", "deskflow-core.exe")], [], null);

    private static void MissingDependencyRejected()
    {
        var files = Files(Bundled);
        files.Remove(Path.Combine(Bundled, "native", "Qt6Network.dll"));
        Expect(files, [], null);
    }

    private static void MissingMonitorRejected()
    {
        var files = Files(Bundled);
        files.Remove(Path.Combine(Bundled, "moonlight-touch-keyboard-release.exe"));
        Expect(files, [], null);
    }

    private static void NumericVersionOrder() => Expect(Files(Legacy("v9"), Legacy("v25")), [Legacy("v9"), Legacy("v25")], Legacy("v25"));

    private static void IncompleteNewestFallsBack()
    {
        var files = Files(Legacy("v25"), Legacy("v26"));
        files.Remove(Path.Combine(Legacy("v26"), "native", "libssl-3-x64.dll"));
        Expect(files, [Legacy("v25"), Legacy("v26")], Legacy("v25"));
    }

    private static void InvalidVersionNamesRejected() => Expect(
        Files(Legacy("disabled-v25"), Legacy("v30-old"), Legacy("v-1"), Legacy("v99999999999999999999")),
        [Legacy("disabled-v25"), Legacy("v30-old"), Legacy("v-1"), Legacy("v99999999999999999999")], null);

    private static void ExternalInstallRejected() => Expect(Files(@"D:\YingYong\Deskflow", @"C:\Program Files\Deskflow", @"C:\OtherCore"), [], null);

    private static void SplitRuntimeRejected()
    {
        var files = Files(Bundled, Legacy("v25"));
        files.Remove(Path.Combine(Bundled, "native", "Qt6Core.dll"));
        files.Remove(Path.Combine(Legacy("v25"), "deskflow_touch_filter.dll"));
        Expect(files, [Legacy("v25")], null);
    }

    private static void BundledBridgeWins()
    {
        var expected = Path.Combine(Bundled, "moonlight-touch-bridge.exe");
        Func<string, bool> exists = path => path == expected;
        Program.Assert((string?)ResolveBridgeMethod.Invoke(null, [Bundled, ExeDir, exists]) == expected,
            "随附触摸桥不应被 tools 旧版本覆盖");
    }

    private static void LegacyBridgeFallsBack()
    {
        Func<string, bool> exists = _ => false;
        var expected = Path.Combine(ExeDir, "tools", "moonlight-touch-bridge.exe");
        Program.Assert((string?)ResolveBridgeMethod.Invoke(null, [Legacy("v25"), ExeDir, exists]) == expected,
            "旧版组件没有触摸桥时应沿用 Hub tools 副本");
    }
}
