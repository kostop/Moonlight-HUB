using System.Reflection;
using MoonlightHub.Core;

namespace MoonlightHub.Tests;

internal static class ProfileSafetyTests
{
    private const string OutputId = "{test-virtual-display}";
    private static readonly DdOptions LegacyDd = new("ensure_active", 2000, 1200, 60, true);
    private static readonly MethodInfo RestoreMethod = typeof(Hub).GetMethod(
        "RestoreInstanceTarget", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("找不到实例显示目标恢复方法");

    /// <param name="tests">接收方案及历史配置恢复用例的列表。</param>
    internal static void AddTo(List<(string Name, Action Run)> tests)
    {
        tests.Add(("仅主屏方案忽略客户端 2000×1200 请求", () => ExpectClientRequestSkipped(ProfileMode.MainOnly, OutputId)));
        tests.Add(("复制方案忽略客户端 2000×1200 请求", () => ExpectClientRequestSkipped(ProfileMode.Clone, OutputId)));
        tests.Add(("没有输出目标时忽略客户端请求", () => ExpectClientRequestSkipped(ProfileMode.Extend, null)));
        tests.Add(("空输出目标时忽略客户端请求", () => ExpectClientRequestSkipped(ProfileMode.Extend, string.Empty)));
        tests.Add(("空白输出目标时忽略客户端请求", () => ExpectClientRequestSkipped(ProfileMode.Extend, " ")));
        tests.Add(("仅主屏启动不恢复历史虚拟屏输出和尺寸", () => ExpectRestored(ProfileMode.MainOnly, OutputId, false, string.Empty)));
        tests.Add(("空输出启动不恢复历史 2000×1200", () => ExpectRestored(ProfileMode.Extend, string.Empty, false, string.Empty)));
        tests.Add(("空白输出启动不恢复历史 2000×1200", () => ExpectRestored(ProfileMode.Extend, " ", false, string.Empty)));
        tests.Add(("复制方案启动不恢复自动设置尺寸", () => ExpectRestored(ProfileMode.Clone, OutputId, false, OutputId)));
        tests.Add(("缺失方案启动不恢复自动设置尺寸", () => ExpectRestored(null, OutputId, false, OutputId)));
        tests.Add(("关闭待机功能时不恢复自动设置尺寸", () => ExpectRestored(ProfileMode.Extend, OutputId, false, OutputId, idleOff: false)));
        tests.Add(("独立扩展屏恢复明确目标的历史尺寸", () => ExpectRestored(ProfileMode.Extend, OutputId, true, OutputId)));
        tests.Add(("仅虚拟屏恢复明确目标的历史尺寸", () => ExpectRestored(ProfileMode.VirtualOnly, OutputId, true, OutputId)));
    }

    /// <param name="mode">当前方案模式。</param>
    /// <param name="outputId">历史输出目标；null 表示没有保存过目标。</param>
    private static void ExpectClientRequestSkipped(ProfileMode mode, string? outputId)
    {
        var profile = new Profile
        {
            Id = "test-profile",
            Mode = mode,
            VirtualDisplays = [new VirtualDisplaySpec { InstanceId = 1, Width = 2000, Height = 1200 }]
        };
        var settings = new HubSettings { Profiles = [profile], Instances = [new InstanceSpec { Id = 1 }] };
        var state = new HubState { ActiveProfileId = profile.Id };
        if (outputId != null) state.InstanceOutputIds[1] = outputId;
        using var vdd = new ParsecVdd();
        var engine = new ProfileEngine(settings, state, vdd, new SunshineInstanceManager(settings));

        var result = engine.ApplyClientMode(1, new ClientMode(2000, 1200, 60));

        Program.Assert(result == null, "此方案应跳过客户端驱动的显示模式修改");
        Program.Assert(!engine.IsBusy, "跳过请求后方案锁没有释放");
    }

    /// <param name="mode">恢复时的方案模式；null 表示找不到原方案。</param>
    /// <param name="outputId">历史配置记录的输出目标。</param>
    /// <param name="enabled">预期是否允许恢复 Sunshine 显示设备控制。</param>
    /// <param name="expectedOutput">恢复后预期使用的输出目标。</param>
    /// <param name="idleOff">方案是否启用虚拟屏闲时关闭。</param>
    private static void ExpectRestored(ProfileMode? mode, string outputId, bool enabled, string expectedOutput, bool idleOff = true)
    {
        var state = new HubState
        {
            InstanceOutputIds = { [1] = outputId },
            InstanceDd = { [1] = LegacyDd }
        };
        var runtime = new InstanceRuntime { Spec = new InstanceSpec { Id = 1 } };
        var profile = mode.HasValue ? new Profile { Mode = mode.Value, IdleOff = idleOff } : null;

        RestoreMethod.Invoke(null, [runtime, state, profile]);

        Program.Assert(runtime.DesiredOutputId == expectedOutput, "恢复后的输出目标不正确");
        Program.Assert(runtime.DesiredDd == (enabled ? LegacyDd : DdOptions.Disabled), "历史尺寸恢复策略不正确");
    }
}
