namespace MoonlightHub.Core;

/// <summary>以串流输出分辨率为基准，计算独立虚拟屏的渲染大小。</summary>
public static class VirtualContentScale
{
    public const int Minimum = 50;
    public const int Maximum = 300;

    /// <param name="width">Moonlight 输出宽度，保留在方案中。</param>
    /// <param name="height">Moonlight 输出高度，保留在方案中。</param>
    /// <param name="percent">额外画面大小百分比，100 不改变原始分辨率。</param>
    public static (int Width, int Height) RenderSize(int width, int height, int percent)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "输出分辨率必须大于零");
        if (percent is < Minimum or > Maximum) throw new ArgumentOutOfRangeException(nameof(percent), $"画面大小须为 {Minimum}–{Maximum}%");
        if (percent == 100) return (width, height);
        var w = (int)(Math.Round(width * 50.0 / percent, MidpointRounding.AwayFromZero) * 2);
        var h = (int)(Math.Round(height * 50.0 / percent, MidpointRounding.AwayFromZero) * 2);
        if (w is < 320 or > 8192 || h is < 240 or > 8192)
            throw new ArgumentOutOfRangeException(nameof(percent), "该比例超出虚拟屏支持的大小，请选择更接近 100% 的比例");
        return (w, h);
    }
}
