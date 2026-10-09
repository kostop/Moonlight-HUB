using System.Text.RegularExpressions;

namespace MoonlightHub.Core;

/// <summary>Monitors of the Linux desktop (X11, via xrandr) as <see cref="DisplayInfo"/> so the layout canvas can show them.</summary>
public static class LinuxDisplays
{
    // " 0: +*DP-1 1920/527x1080/296+0+0  DP-1"
    private static readonly Regex MonitorLine = new(@"^\s*\d+:\s+\+?(\*?)(\S+)\s+(\d+)/\d+x(\d+)/\d+([+-]\d+)([+-]\d+)", RegexOptions.Multiline);

    public static List<DisplayInfo> Enumerate()
    {
        var list = new List<DisplayInfo>();
        try
        {
            var (code, output) = ProcessUtil.Run("xrandr", "--listmonitors", 5000);
            if (code != 0) return list;
            foreach (Match m in MonitorLine.Matches(output))
            {
                list.Add(new DisplayInfo
                {
                    GdiName = m.Groups[2].Value,
                    FriendlyName = m.Groups[2].Value,
                    Active = true,
                    Primary = m.Groups[1].Value == "*",
                    Width = int.Parse(m.Groups[3].Value),
                    Height = int.Parse(m.Groups[4].Value),
                    PositionX = int.Parse(m.Groups[5].Value),
                    PositionY = int.Parse(m.Groups[6].Value),
                });
            }
            if (list.Count > 0 && !list.Any(d => d.Primary)) list[0].Primary = true;
        }
        catch (Exception ex)
        {
            Log.Warn("读取 xrandr 显示器失败: " + ex.Message);
        }
        return list;
    }
}
