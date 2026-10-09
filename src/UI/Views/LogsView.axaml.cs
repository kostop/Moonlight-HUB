using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MoonlightHub.Core;

namespace MoonlightHub.UI.Views;

public partial class LogsView : UserControl, IRefreshable
{
    private readonly MainWindow _window;
    private int _lastCount = -1;
    private DateTime _lastFileRead = DateTime.MinValue;

    public LogsView(MainWindow window)
    {
        _window = window;
        InitializeComponent();
        Source.Items.Add("Moonlight Hub");
        foreach (var inst in window.Hub.Settings.Instances) Source.Items.Add($"Sunshine 实例 {inst.Id} ({inst.Name})");
        Source.SelectedIndex = 0;
    }

    private Hub Hub => _window.Hub;

    public void Refresh()
    {
        if (Source.SelectedIndex <= 0)
        {
            var entries = Log.Recent;
            if (entries.Count == _lastCount) return;
            _lastCount = entries.Count;
            Text.Text = string.Join(Environment.NewLine, entries.TakeLast(600).Select(e => $"{e.Time:HH:mm:ss.fff} [{e.LevelText}] {e.Message}"));
        }
        else
        {
            if ((DateTime.UtcNow - _lastFileRead) < TimeSpan.FromSeconds(3)) return;
            _lastFileRead = DateTime.UtcNow;
            var spec = Hub.Settings.Instances.ElementAtOrDefault(Source.SelectedIndex - 1);
            if (spec != null) Text.Text = Hub.Instances.TailLog(spec, 400);
        }
        if (AutoScroll.IsChecked == true) ScrollToEnd();
    }

    /// <summary>Avalonia's TextBox has no ScrollToEnd(): scroll the ScrollViewer of its template once the new text is laid out.</summary>
    private void ScrollToEnd()
    {
        Dispatcher.UIThread.Post(() => Text.FindDescendantOfType<ScrollViewer>()?.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void Source_Changed(object? sender, SelectionChangedEventArgs e)
    {
        _lastCount = -1;
        _lastFileRead = DateTime.MinValue;
        if (IsLoaded) Refresh();
    }

    private void OpenFile_Click(object? sender, RoutedEventArgs e)
    {
        if (Source.SelectedIndex <= 0) ProcessUtil.OpenInShell(Log.CurrentFile);
        else
        {
            var spec = Hub.Settings.Instances.ElementAtOrDefault(Source.SelectedIndex - 1);
            if (spec != null && File.Exists(Hub.Instances.Get(spec).LogPath)) ProcessUtil.OpenInShell(Hub.Instances.Get(spec).LogPath);
        }
    }

    private void OpenDir_Click(object? sender, RoutedEventArgs e) => ProcessUtil.OpenInShell(Paths.LogDir);
}
