using System.Runtime.InteropServices;
using MoonlightHub.Native;

namespace MoonlightHub.Core;

/// <summary>读取、设置单个 Windows 显示源的真实 DPI，不改变像素分辨率。</summary>
public static class DisplayDpiScale
{
    public static readonly int[] Percentages = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];
    public sealed record ScaleInfo(int Minimum, int Current, int Maximum, int RecommendedIndex, int CurrentOffset);

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryPacket
    {
        public NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public int MinimumOffset, CurrentOffset, MaximumOffset;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SetPacket
    {
        public NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public int Offset;
    }
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetScale(ref QueryPacket packet);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")]
    private static extern int SetScale(ref SetPacket packet);

    /// <param name="display">需要读取的活动显示器，使用源 ID 而非目标 ID。</param>
    public static ScaleInfo Read(DisplayInfo display)
    {
        var packet = new QueryPacket { Header = new() { type = unchecked((uint)-3), size = (uint)Marshal.SizeOf<QueryPacket>(),
            adapterId = display.AdapterId, id = display.SourceId } };
        var result = GetScale(ref packet);
        if (result != 0) throw new InvalidOperationException($"读取显示器 DPI 失败（{result}）");
        var recommended = -packet.MinimumOffset;
        if (recommended < 0 || recommended >= Percentages.Length) throw new InvalidOperationException("系统返回的 DPI 基准无效");
        int Value(int offset) => Percentages[Math.Clamp(recommended + offset, 0, Percentages.Length - 1)];
        var actual = PointerSpeedManager.GetWindowsScalePercent(display.GdiName);
        return new(Value(packet.MinimumOffset), actual > 0 ? actual : Math.Min(Value(packet.CurrentOffset), Value(packet.MaximumOffset)),
            Value(packet.MaximumOffset), recommended, packet.CurrentOffset);
    }

    /// <param name="display">独立的虚拟显示器。</param>
    /// <param name="percent">真实 DPI 百分比；不将请求静默夹到系统推荐上限。</param>
    public static (bool Ok, string Message) TrySet(DisplayInfo display, int percent)
    {
        if (!display.IsParsec || !display.IsReady) return (false, "需要已激活的独立虚拟屏");
        var index = Array.IndexOf(Percentages, percent);
        if (index < 0) return (false, "请使用 Windows 支持的 DPI 比例");
        var before = Read(display);
        if (percent < before.Minimum || percent > before.Maximum)
            return (false, $"这块屏当前支持 {before.Minimum}–{before.Maximum}% 的真实 DPI；已保持 {before.Current}% 和原分辨率");
        var packet = new SetPacket { Header = new() { type = unchecked((uint)-4), size = (uint)Marshal.SizeOf<SetPacket>(),
            adapterId = display.AdapterId, id = display.SourceId }, Offset = index - before.RecommendedIndex };
        var result = SetScale(ref packet);
        if (result != 0) return (false, $"Windows 拒绝 {percent}% 的单屏 DPI（{result}），系统报告上限 {before.Maximum}%");
        Thread.Sleep(1000);
        var fresh = DisplayManager.Enumerate().FirstOrDefault(d => d.SunshineDeviceId == display.SunshineDeviceId && d.IsReady);
        var actual = fresh == null ? 0 : PointerSpeedManager.GetWindowsScalePercent(fresh.GdiName);
        if (fresh != null && fresh.Width == display.Width && fresh.Height == display.Height && actual == percent)
            return (true, $"真实 DPI 已设为 {actual}%，分辨率保持 {fresh.Width}×{fresh.Height}");
        packet.Offset = before.CurrentOffset;
        SetScale(ref packet);
        return (false, $"Windows 未实际应用 {percent}% DPI（实际 {actual}%），已恢复原 DPI");
    }
}
