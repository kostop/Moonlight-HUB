namespace MoonlightHub.Core;

/// <summary>一个可选的虚拟屏模式；<see cref="Exposed"/> 表示 Windows 当前已经发布该模式。</summary>
public sealed record VirtualModeChoice(int Width, int Height, int Hz, bool Exposed, string? Note = null)
{
    public string Label => $"{Width}×{Height} @ {Hz}Hz" + (Note ?? (Exposed ? string.Empty : "（重新插拔后可用）"));
    public override string ToString() => Label;
}

/// <summary>把驱动当前发布的模式和注册表里的自定义模式合并成完整的分辨率/刷新率列表。</summary>
public static class ParsecModeCatalog
{
    /// <summary>读取活动虚拟屏当前发布的模式，并合并在注册表中登记的自定义模式。</summary>
    /// <param name="gdiName">活动显示器名称；待机虚拟屏传空。</param>
    /// <param name="targetWidth">方案目标宽度（像素）。</param>
    /// <param name="targetHeight">方案目标高度（像素）。</param>
    /// <param name="targetHz">方案目标刷新率（Hz）。</param>
    public static List<VirtualModeChoice> Build(string? gdiName, int targetWidth, int targetHeight, int targetHz)
    {
        var exposed = string.IsNullOrWhiteSpace(gdiName)
            ? Enumerable.Empty<DisplayMode>()
            : DisplayManager.GetModes(gdiName).Where(m => m.Orientation == 0);
        return Merge(exposed, ParsecModes.Read(), targetWidth, targetHeight, targetHz,
            string.IsNullOrWhiteSpace(gdiName) ? string.Empty : null);
    }

    /// <summary>合并两组模式，去重并把方案当前模式排在最前面；单独拆出便于无系统调用的测试。</summary>
    /// <param name="exposed">Windows 当前发布的模式。</param>
    /// <param name="registered">注册表中登记的自定义模式。</param>
    /// <param name="targetWidth">方案目标宽度（像素）。</param>
    /// <param name="targetHeight">方案目标高度（像素）。</param>
    /// <param name="targetHz">方案目标刷新率（Hz）。</param>
    /// <param name="missingNote">仅注册、尚未发布时的提示文字；null 使用默认提示。</param>
    public static List<VirtualModeChoice> Merge(
        IEnumerable<DisplayMode> exposed,
        IEnumerable<ParsecCustomMode> registered,
        int targetWidth,
        int targetHeight,
        int targetHz,
        string? missingNote = null)
    {
        var choices = new List<VirtualModeChoice>();
        var seen = new HashSet<(int Width, int Height, int Hz)>();

        void Add(int width, int height, int hz, bool isExposed, string? note = null)
        {
            if (width <= 0 || height <= 0 || hz <= 0 || !seen.Add((width, height, hz))) return;
            choices.Add(new VirtualModeChoice(width, height, hz, isExposed,
                note ?? (isExposed ? string.Empty : missingNote)));
        }

        foreach (var mode in exposed) Add(mode.Width, mode.Height, mode.RefreshRate, true);
        foreach (var mode in registered) Add(mode.Width, mode.Height, mode.Hz, false);
        Add(targetWidth, targetHeight, targetHz, true);

        return choices
            .OrderByDescending(c => c.Width == targetWidth && c.Height == targetHeight)
            .ThenByDescending(c => (long)c.Width * c.Height)
            .ThenByDescending(c => c.Hz)
            .ToList();
    }
}
