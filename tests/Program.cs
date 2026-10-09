using System.Reflection;
using System.Runtime.InteropServices;
using MoonlightHub.Core;

namespace MoonlightHub.Tests;

internal static class Program
{
    private static readonly MethodInfo ValidateMethod = typeof(DisplayManager).GetMethod(
        "ValidateLayoutTargets", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("找不到显示目标校验方法");
    private static int _nativeCalls;

    private static int Main()
    {
        // 测试只能使用模拟快照；即使以后引入原生调用，也必须在加载系统 DLL 前失败。
        NativeLibrary.SetDllImportResolver(typeof(DisplayManager).Assembly, BlockNativeCall);
        var tests = new List<(string Name, Action Run)>
        {
            ("没有副屏时拒绝旧虚拟屏布局", MissingVirtualDisplay),
            ("内屏复用旧 GDI 名称时拒绝 2000×1200", ReusedGdiName),
            ("虚拟屏尚未激活时拒绝设置", InactiveVirtualDisplay),
            ("虚拟屏尚无模式时拒绝设置", MissingVirtualMode),
            ("虚拟屏尚无 GDI 名称时拒绝设置", MissingGdiName),
            ("虚拟与物理屏共享显示源时拒绝设置", ClonedPhysicalDisplay),
            ("设备路径保留但 GDI 变化时拒绝旧布局", ChangedGdiName),
            ("一项断开时拒绝整个多屏布局", PartiallyDisconnectedLayout),
            ("重复显示源布局被拒绝", DuplicateSource),
            ("独立虚拟屏允许使用 2000×1200", IndependentVirtualDisplay),
            ("用户明确选择的物理屏允许调整", ExplicitPhysicalDisplay),
            ("设备名称大小写变化不影响匹配", CaseInsensitiveIdentity),
            ("空 GDI 查询不会落到默认主屏", EmptyNameQueries),
            ("空布局不调用系统接口", EmptyLayout)
        };
        ProfileSafetyTests.AddTo(tests);

        var failed = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}: {ex.GetBaseException().Message}");
            }
        }

        if (_nativeCalls != 0)
        {
            failed++;
            Console.Error.WriteLine($"FAIL 检测到 {_nativeCalls} 次原生调用尝试，已全部阻止");
        }
        Console.WriteLine($"{tests.Count - Math.Min(failed, tests.Count)}/{tests.Count} passed; native calls: {_nativeCalls}");
        return failed == 0 ? 0 : 1;
    }

    private static void MissingVirtualDisplay()
    {
        ExpectRejected([VirtualLayout()], [PhysicalDisplay()]);
    }

    private static void ReusedGdiName()
    {
        var panel = PhysicalDisplay();
        panel.GdiName = VirtualDisplay().GdiName;
        ExpectRejected([VirtualLayout()], [panel]);
        Assert(panel.Width == 2560 && panel.Height == 1600, "内屏原始分辨率被改变");
    }

    private static void InactiveVirtualDisplay()
    {
        var display = VirtualDisplay();
        display.Active = false;
        ExpectRejected([VirtualLayout()], [display, PhysicalDisplay()]);
    }

    private static void MissingVirtualMode()
    {
        var display = VirtualDisplay();
        display.Width = 0;
        display.Height = 0;
        ExpectRejected([VirtualLayout()], [display, PhysicalDisplay()]);
    }

    private static void MissingGdiName()
    {
        var display = VirtualDisplay();
        display.GdiName = string.Empty;
        ExpectRejected([new LayoutItem(display) { Width = 2000, Height = 1200 }], [display]);
    }

    private static void ClonedPhysicalDisplay()
    {
        var panel = PhysicalDisplay();
        var display = VirtualDisplay();
        panel.GdiName = display.GdiName;
        panel.AdapterId = display.AdapterId;
        panel.SourceId = display.SourceId;
        ExpectRejected([VirtualLayout()], [display, panel]);
    }

    private static void ChangedGdiName()
    {
        var display = VirtualDisplay();
        display.GdiName = @"\\.\DISPLAY3";
        ExpectRejected([VirtualLayout()], [display, PhysicalDisplay()]);
    }

    private static void PartiallyDisconnectedLayout()
    {
        var panel = PhysicalDisplay();
        ExpectRejected([new LayoutItem(panel) { X = 0 }, VirtualLayout()], [panel]);
    }

    private static void DuplicateSource()
    {
        ExpectRejected([VirtualLayout(), VirtualLayout()], [VirtualDisplay()]);
    }

    private static void IndependentVirtualDisplay()
    {
        var item = VirtualLayout();
        Assert(item.RequireParsec, "虚拟屏布局必须携带类型限制");
        ExpectAccepted([item], [PhysicalDisplay(), VirtualDisplay()]);
    }

    private static void ExplicitPhysicalDisplay()
    {
        var panel = PhysicalDisplay();
        var item = new LayoutItem(panel) { Width = 1920, Height = 1200 };
        Assert(!item.RequireParsec, "明确的物理屏布局不应被当成虚拟屏");
        ExpectAccepted([item], [panel]);
    }

    private static void CaseInsensitiveIdentity()
    {
        var display = VirtualDisplay();
        display.GdiName = display.GdiName.ToLowerInvariant();
        display.DevicePath = display.DevicePath.ToLowerInvariant();
        ExpectAccepted([VirtualLayout()], [display, PhysicalDisplay()]);
    }

    private static void EmptyNameQueries()
    {
        foreach (var name in new[] { string.Empty, " " })
        {
            Assert(DisplayManager.GetModes(name).Count == 0, "空名称不应返回主屏模式");
            Assert(DisplayManager.GetCurrentMode(name) == null, "空名称不应返回主屏当前模式");
        }
    }

    private static void EmptyLayout()
    {
        Assert(DisplayManager.ApplyLayout([]).Success, "空布局应直接完成");
    }

    private static DisplayInfo VirtualDisplay() => new()
    {
        GdiName = @"\\.\DISPLAY2",
        DevicePath = @"\\?\DISPLAY#PSCCDD0#TEST&UID256#VIRTUAL",
        Active = true,
        Width = 1920,
        Height = 1080,
        RefreshRate = 60,
        AdapterId = new() { LowPart = 2 },
        SourceId = 0,
        TargetId = 1
    };

    private static DisplayInfo PhysicalDisplay() => new()
    {
        GdiName = @"\\.\DISPLAY1",
        DevicePath = @"\\?\DISPLAY#IVO1234#TEST&UID0#INTERNAL",
        Active = true,
        Primary = true,
        IsEmbeddedTechnology = true,
        Width = 2560,
        Height = 1600,
        RefreshRate = 60,
        AdapterId = new() { LowPart = 1 },
        SourceId = 0,
        TargetId = 1
    };

    private static LayoutItem VirtualLayout() => new(VirtualDisplay()) { Width = 2000, Height = 1200 };

    /// <param name="items">待应用的布局快照。</param>
    /// <param name="displays">模拟当前连接状态的显示器列表。</param>
    private static string? Validate(IReadOnlyList<LayoutItem> items, IReadOnlyList<DisplayInfo> displays)
        => (string?)ValidateMethod.Invoke(null, [items, displays]);

    /// <param name="items">预期应被拒绝的布局。</param>
    /// <param name="displays">模拟当前连接状态的显示器列表。</param>
    private static void ExpectRejected(IReadOnlyList<LayoutItem> items, IReadOnlyList<DisplayInfo> displays)
        => Assert(!string.IsNullOrWhiteSpace(Validate(items, displays)), "危险布局未被拒绝");

    /// <param name="items">预期允许的明确目标布局。</param>
    /// <param name="displays">模拟当前连接状态的显示器列表。</param>
    private static void ExpectAccepted(IReadOnlyList<LayoutItem> items, IReadOnlyList<DisplayInfo> displays)
        => Assert(Validate(items, displays) == null, "安全布局被错误拒绝");

    /// <param name="condition">必须成立的测试条件。</param>
    /// <param name="message">条件不成立时报告的原因。</param>
    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <param name="libraryName">被生产代码请求加载的原生库。</param>
    /// <param name="assembly">发起调用的程序集。</param>
    /// <param name="searchPath">原生库查找选项；测试中不会进行查找。</param>
    private static IntPtr BlockNativeCall(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        _nativeCalls++;
        throw new InvalidOperationException($"测试禁止访问原生接口: {libraryName}");
    }
}
