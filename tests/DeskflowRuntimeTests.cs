using System.Reflection;
using System.Runtime.CompilerServices;
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
    private static readonly MethodInfo UsesSelectedCoreMethod = typeof(DeskflowManager).Assembly
        .GetType("MoonlightHub.Core.DeskflowRuntimeLocator")?.GetMethod("UsesSelectedCore", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("找不到 Hub 随附核心映像校验方法");
    private static readonly MethodInfo IsHealthyMethod = typeof(DeskflowManager).GetMethod("IsHealthy", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("找不到 Hub 键鼠核心健康检查方法");

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
        tests.Add(("运行核心来自当前 Hub 随附目录时允许保持", () => ExpectCore(true, CurrentCore, [CurrentCore], true)));
        tests.Add(("核心路径大小写及等价点目录不误触发迁移", () => ExpectCore(true, CurrentCore, [Path.Combine(ExeDir.ToLowerInvariant(), "deskflow", "native", ".", "deskflow-core.exe")], true)));
        tests.Add(("独立安装的 SYSTEM 客户端仍须迁移", () => ExpectCore(true, CurrentCore, [@"C:\Program Files\Deskflow\deskflow-core.exe"], false)));
        tests.Add(("暂存 Hub 不沿用安装目录旧核心", () => ExpectCore(true, @"C:\Update\staging\deskflow\native\deskflow-core.exe", [CurrentCore], false)));
        tests.Add(("读取不到核心映像路径时保守触发迁移", () => ExpectCore(true, CurrentCore, [null], false)));
        tests.Add(("相对核心路径不能被当作已确认映像", () => ExpectCore(true, CurrentCore, ["deskflow-core.exe"], false)));
        tests.Add(("没有原生核心时不把包装器当作健康核心", () => ExpectCore(true, CurrentCore, [], false)));
        tests.Add(("多个核心混入旧目录时触发迁移", () => ExpectCore(true, CurrentCore, [CurrentCore, @"C:\OldHub\deskflow-core.exe"], false)));
        tests.Add(("无效核心路径不抛异常且触发迁移", () => ExpectCore(true, CurrentCore, ["invalid\0path"], false)));
        tests.Add(("Linux 核心不要求 Windows 随附映像路径", () => ExpectCore(false, null, ["/app/bin/deskflow-core"], null)));
        tests.Add(("客户端健康检查拒绝旧目录 SYSTEM 核心", () => ExpectClientHealthy(false, true, false)));
        tests.Add(("客户端健康检查允许当前目录 SYSTEM 核心", () => ExpectClientHealthy(true, true, true)));
        tests.Add(("当前目录客户端仍保留 SYSTEM 身份要求", () => ExpectClientHealthy(true, false, false)));
    }

    private const string ExeDir = @"C:\Hub";
    private const string BridgeRoot = @"C:\User\MoonlightDeskflowBridge";
    private static string Bundled => Path.Combine(ExeDir, "deskflow");
    private static string CurrentCore => Path.Combine(Bundled, "native", "deskflow-core.exe");
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

    // 参数 required：是否要求 Windows 路径；expected/paths：选定和运行映像；result：预期判断，null 表示平台无需判断。
    private static void ExpectCore(bool required, string? expected, string?[] paths, bool? result)
    {
        Program.Assert((bool?)UsesSelectedCoreMethod.Invoke(null, [required, expected, paths]) == result,
            "核心映像路径的迁移判断与预期不符");
    }

    // 参数 selected：映像是否来自当前组件；asSystem：原有 SYSTEM 判断；expected：客户端是否健康。
    private static void ExpectClientHealthy(bool selected, bool asSystem, bool expected)
    {
        // 不执行构造函数，不读取设置、不启动计时器或平台接口；客户端分支不访问主机实例。
        var manager = (DeskflowManager)RuntimeHelpers.GetUninitializedObject(typeof(DeskflowManager));
        var settings = new DeskflowSettings { Role = DeskflowRole.Client };
        var status = new DeskflowStatus { RunningRole = DeskflowRole.Client, MainCores = 1,
            CoreAsSystem = asSystem, CoreInSelectedRuntime = selected };
        Program.Assert((bool)IsHealthyMethod.Invoke(manager, [settings, status])! == expected,
            "客户端健康检查未同时保留映像路径与 SYSTEM 要求");
    }
}
