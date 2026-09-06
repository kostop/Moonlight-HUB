using System.Windows;
using System.Windows.Controls;
using MoonlightHub.Core;

namespace MoonlightHub.UI;

public partial class ProfileEditorWindow : Window
{
    private readonly HubSettings _settings;
    public Profile Result { get; }

    private static readonly (ProfileMode Mode, string Text)[] Modes =
    {
        (ProfileMode.Extend, "扩展：物理屏 + 若干虚拟副屏"),
        (ProfileMode.VirtualOnly, "仅副屏：只保留第一块虚拟屏（低延迟）"),
        (ProfileMode.Clone, "复制：主屏复制到第一块虚拟屏"),
        (ProfileMode.MainOnly, "仅主屏：不创建虚拟屏"),
    };

    private static readonly (int W, int H)[] Resolutions = { (2000, 1200), (1920, 1080), (2560, 1440), (2560, 1600), (1920, 1200), (1600, 900), (1280, 800), (3840, 2160) };
    private static readonly int[] Rates = { 60, 90, 120, 144, 165, 240 };

    public ProfileEditorWindow(Profile profile, HubSettings settings)
    {
        Result = profile;
        _settings = settings;
        InitializeComponent();
        NameBox.Text = profile.Name;
        DescBox.Text = profile.Description;
        foreach (var m in Modes) ModeBox.Items.Add(m.Text);
        ModeBox.SelectedIndex = Array.FindIndex(Modes, m => m.Mode == profile.Mode);
        ComposeBox.Text = profile.ComposeRefreshRate.ToString();
        KeepMainBox.IsChecked = profile.KeepMainInstance;
        MatchFpsBox.IsChecked = profile.MatchClientFps;
        RememberPosBox.IsChecked = profile.RememberManualPosition;
        MatchResBox.IsChecked = profile.AutoMatchClientResolution;
        IdleOffBox.IsChecked = profile.IdleOff;
        BuildVds();
    }

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ModeBox.SelectedIndex < 0) return;
        var mode = Modes[ModeBox.SelectedIndex].Mode;
        ModeHint.Text = mode switch
        {
            ProfileMode.Extend => "虚拟屏按列表顺序依次放在主屏右侧（或按每块的放置设置）。每块虚拟屏可指定一个 Sunshine 实例，多台 Moonlight 可同时连接不同实例。",
            ProfileMode.VirtualOnly => "只使用列表中的第一块虚拟屏，物理显示器会被关闭；退出该方案或切换到其他方案会恢复。",
            ProfileMode.Clone => "只使用第一块虚拟屏，Windows 复制拓扑；分辨率以主屏为准。",
            _ => "移除全部虚拟屏。"
        };
        KeepMainBox.Visibility = mode == ProfileMode.MainOnly ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildVds()
    {
        VdPanel.Children.Clear();
        for (var i = 0; i < Result.VirtualDisplays.Count; i++)
        {
            var vd = Result.VirtualDisplays[i];
            vd.Slot = i;
            var index = i;
            var res = new ComboBox { Width = 140, Margin = new Thickness(0, 0, 8, 0) };
            foreach (var (w, h) in Resolutions) res.Items.Add($"{w}×{h}");
            var custom = $"{vd.Width}×{vd.Height}";
            if (!res.Items.Contains(custom)) res.Items.Insert(0, custom);
            res.SelectedItem = custom;
            res.SelectionChanged += (_, _) =>
            {
                var parts = (res.SelectedItem as string ?? string.Empty).Split('×');
                if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)) { vd.Width = w; vd.Height = h; }
            };
            var rate = new ComboBox { Width = 90, Margin = new Thickness(0, 0, 8, 0), ToolTip = "刷新率下限；开启“跟随客户端帧率”时客户端要求更高帧率会自动提高" };
            foreach (var r in Rates.Union(ParsecModes.Read().Select(m => m.Hz)).Distinct().OrderBy(x => x)) rate.Items.Add($"{r} Hz");
            if (!rate.Items.Contains($"{vd.RefreshRate} Hz")) rate.Items.Insert(0, $"{vd.RefreshRate} Hz");
            rate.SelectedItem = $"{vd.RefreshRate} Hz";
            rate.SelectionChanged += (_, _) => { if (int.TryParse((rate.SelectedItem as string ?? "").Replace(" Hz", ""), out var r)) vd.RefreshRate = r; };
            var place = new ComboBox { Width = 110, Margin = new Thickness(0, 0, 8, 0) };
            foreach (var p in new[] { "主屏右侧", "主屏左侧", "主屏上方", "主屏下方" }) place.Items.Add(p);
            place.SelectedIndex = (int)vd.Placement switch { 0 => 0, 1 => 1, 2 => 2, 3 => 3, _ => 0 };
            place.SelectionChanged += (_, _) => vd.Placement = (Placement)Math.Max(0, place.SelectedIndex);
            var inst = new ComboBox { Width = 170, Margin = new Thickness(0, 0, 8, 0) };
            inst.Items.Add("不串流");
            foreach (var s in _settings.Instances.OrderBy(x => x.Id)) inst.Items.Add($"实例 {s.Id} ({s.Name}, :{s.Port})");
            inst.SelectedIndex = vd.InstanceId <= 0 ? 0 : Math.Max(0, _settings.Instances.OrderBy(x => x.Id).ToList().FindIndex(x => x.Id == vd.InstanceId) + 1);
            inst.SelectionChanged += (_, _) => vd.InstanceId = inst.SelectedIndex <= 0 ? 0 : _settings.Instances.OrderBy(x => x.Id).ElementAt(inst.SelectedIndex - 1).Id;
            var del = Ui.B("移除", (_, _) => { Result.VirtualDisplays.RemoveAt(index); BuildVds(); }, "DangerButton");
            del.Padding = new Thickness(8, 3, 8, 3);
            var row = Ui.Row(Ui.Pill($"#{i + 1}", "accent"), Ui.T("分辨率", "Muted"), res, Ui.T("刷新率", "Muted"), rate, Ui.T("位置", "Muted"), place, Ui.T("串流", "Muted"), inst, del);
            row.Margin = new Thickness(0, 4, 0, 4);
            VdPanel.Children.Add(row);
        }
        if (Result.VirtualDisplays.Count == 0) VdPanel.Children.Add(Ui.T("没有虚拟屏（相当于仅主屏）", "Muted"));
    }

    private void AddVd_Click(object sender, RoutedEventArgs e)
    {
        if (Result.VirtualDisplays.Count >= ParsecVdd.MaxDisplays) return;
        var nextInstance = _settings.Instances.OrderBy(x => x.Id).Select(x => x.Id).FirstOrDefault(id => Result.VirtualDisplays.All(v => v.InstanceId != id));
        Result.VirtualDisplays.Add(new VirtualDisplaySpec { Slot = Result.VirtualDisplays.Count, InstanceId = nextInstance });
        BuildVds();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "未命名方案" : NameBox.Text.Trim();
        Result.Description = DescBox.Text.Trim();
        Result.Mode = Modes[Math.Max(0, ModeBox.SelectedIndex)].Mode;
        Result.ComposeRefreshRate = int.TryParse(ComposeBox.Text, out var c) ? Math.Clamp(c, 0, 500) : 0;
        Result.KeepMainInstance = KeepMainBox.IsChecked == true;
        Result.MatchClientFps = MatchFpsBox.IsChecked == true;
        Result.RememberManualPosition = RememberPosBox.IsChecked == true;
        Result.AutoMatchClientResolution = MatchResBox.IsChecked == true;
        Result.IdleOff = IdleOffBox.IsChecked == true;
        if (Result.Mode != ProfileMode.MainOnly && Result.VirtualDisplays.Count == 0)
        {
            MessageBox.Show(this, "该模式至少需要一块虚拟屏。", "方案", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dup = Result.VirtualDisplays.Where(v => v.InstanceId > 0).GroupBy(v => v.InstanceId).FirstOrDefault(g => g.Count() > 1);
        if (dup != null)
        {
            MessageBox.Show(this, $"实例 {dup.Key} 被分配给了多块虚拟屏，一个 Sunshine 实例只能抓取一块屏。", "方案", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
