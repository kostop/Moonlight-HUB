using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MoonlightHub.Core;

namespace MoonlightHub.UI;

/// <summary>
/// Scaled diagram of the active desktop (physical displays grey, Parsec virtual displays cyan).
/// When <see cref="Interactive"/> is on, displays can be dragged (with edge snapping) to compose a new layout;
/// the draft positions are exposed through <see cref="DraftPositions"/> and applied by the owning view.
/// </summary>
public sealed class DisplayCanvas : Canvas
{
    private sealed class Item
    {
        public DisplayInfo Display = null!;
        public int X;
        public int Y;
        public Border? Visual;
    }

    private readonly List<Item> _items = new();
    private string _signature = string.Empty;
    private Item? _dragItem;
    private Point _dragStart;
    private int _dragOriginX, _dragOriginY;
    private double _scale = 1;
    private int _minX, _minY;
    private double _offX, _offY;

    public bool Interactive { get; set; }
    public bool HasChanges { get; private set; }
    public event Action? DraftChanged;

    public DisplayCanvas()
    {
        MinHeight = 200;
        ClipToBounds = true;
        Background = Brushes.Transparent;
        SizeChanged += (_, _) => Redraw();
    }

    public void SetDisplays(IEnumerable<DisplayInfo> displays)
    {
        var list = displays.Where(d => d.Active && d.Width > 0).ToList();
        var sig = string.Join("|", list.Select(d => $"{d.GdiName}:{d.PositionX},{d.PositionY},{d.Width},{d.Height},{d.RefreshRate},{d.Primary},{d.IsParsec}"));
        if (sig == _signature) return;
        if (HasChanges && _dragItem != null) return; // do not clobber an in-progress drag
        _signature = sig;
        _items.Clear();
        foreach (var d in list) _items.Add(new Item { Display = d, X = d.PositionX, Y = d.PositionY });
        HasChanges = false;
        Redraw();
        DraftChanged?.Invoke();
    }

    /// <summary>Current draft positions (GDI name → top-left), including unchanged displays.</summary>
    public List<(DisplayInfo Display, int X, int Y)> DraftPositions => _items.Select(i => (i.Display, i.X, i.Y)).ToList();

    public void ResetDraft()
    {
        foreach (var i in _items) { i.X = i.Display.PositionX; i.Y = i.Display.PositionY; }
        HasChanges = false;
        Redraw();
        DraftChanged?.Invoke();
    }

    private void Redraw()
    {
        Children.Clear();
        if (_items.Count == 0 || ActualWidth < 20 || ActualHeight < 20)
        {
            if (ActualWidth > 20)
            {
                var empty = new TextBlock { Text = "没有激活的显示器", Foreground = Ui.Res("MutedBrush") };
                SetLeft(empty, 12);
                SetTop(empty, 12);
                Children.Add(empty);
            }
            return;
        }

        _minX = _items.Min(i => i.X);
        _minY = _items.Min(i => i.Y);
        var maxX = _items.Max(i => i.X + i.Display.Width);
        var maxY = _items.Max(i => i.Y + i.Display.Height);
        var w = Math.Max(1, maxX - _minX);
        var h = Math.Max(1, maxY - _minY);
        const double pad = 18;
        _scale = Math.Min((ActualWidth - pad * 2) / w, (ActualHeight - pad * 2) / h);
        _offX = pad + ((ActualWidth - pad * 2) - w * _scale) / 2;
        _offY = pad + ((ActualHeight - pad * 2) - h * _scale) / 2;

        foreach (var item in _items)
        {
            var d = item.Display;
            var isVirtual = d.IsParsec;
            var border = new Border
            {
                Width = Math.Max(24, d.Width * _scale - 4),
                Height = Math.Max(18, d.Height * _scale - 4),
                CornerRadius = new CornerRadius(6),
                Background = Ui.Res(isVirtual ? "AccentSoftBrush" : "Surface3Brush"),
                BorderBrush = Ui.Res(isVirtual ? "AccentBrush" : (d.Primary ? "TextBrush" : "BorderBrush")),
                BorderThickness = new Thickness(d.Primary ? 2 : 1),
                Padding = new Thickness(8, 6, 8, 6),
                ToolTip = d.ToString() + (Interactive ? "\n拖动可调整位置" : string.Empty),
                Cursor = Interactive ? Cursors.SizeAll : Cursors.Arrow,
                Tag = item
            };
            var stack = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            if (d.Primary) head.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 11, Foreground = Ui.Res("WarnBrush"), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock { Text = d.Label, FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = Ui.Res(isVirtual ? "AccentBrush" : "TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(head);
            stack.Children.Add(new TextBlock { Text = d.ModeText, FontSize = 11, Foreground = Ui.Res("MutedBrush") });
            stack.Children.Add(new TextBlock { Text = $"{d.GdiName}  ({item.X}, {item.Y})", FontSize = 10, Foreground = Ui.Res("MutedBrush") });
            border.Child = stack;
            item.Visual = border;
            SetLeft(border, _offX + (item.X - _minX) * _scale + 2);
            SetTop(border, _offY + (item.Y - _minY) * _scale + 2);
            if (Interactive)
            {
                border.MouseLeftButtonDown += Border_MouseLeftButtonDown;
                border.MouseMove += Border_MouseMove;
                border.MouseLeftButtonUp += Border_MouseLeftButtonUp;
            }
            Children.Add(border);
        }
    }

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: Item item }) return;
        _dragItem = item;
        _dragStart = e.GetPosition(this);
        _dragOriginX = item.X;
        _dragOriginY = item.Y;
        ((Border)sender).CaptureMouse();
        e.Handled = true;
    }

    private void Border_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed || _dragItem.Visual == null) return;
        var p = e.GetPosition(this);
        var dx = (p.X - _dragStart.X) / _scale;
        var dy = (p.Y - _dragStart.Y) / _scale;
        _dragItem.X = _dragOriginX + (int)Math.Round(dx);
        _dragItem.Y = _dragOriginY + (int)Math.Round(dy);
        SetLeft(_dragItem.Visual, _offX + (_dragItem.X - _minX) * _scale + 2);
        SetTop(_dragItem.Visual, _offY + (_dragItem.Y - _minY) * _scale + 2);
    }

    private void Border_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragItem == null) return;
        ((Border)sender).ReleaseMouseCapture();
        var item = _dragItem;
        _dragItem = null;
        Snap(item);
        ResolveOverlap(item);
        HasChanges = _items.Any(i => i.X != i.Display.PositionX || i.Y != i.Display.PositionY);
        Redraw();
        DraftChanged?.Invoke();
    }

    /// <summary>Aligns the moved display to the closest edges of the other displays (Windows-settings style snapping).</summary>
    private void Snap(Item moved)
    {
        var others = _items.Where(i => i != moved).ToList();
        if (others.Count == 0) return;
        const int threshold = 140;
        var w = moved.Display.Width;
        var h = moved.Display.Height;
        var best = (dist: int.MaxValue, x: moved.X, y: moved.Y);

        foreach (var o in others)
        {
            var ow = o.Display.Width;
            var oh = o.Display.Height;
            // candidate placements around the other display
            var candidates = new List<(int x, int y)>
            {
                (o.X + ow, o.Y), (o.X + ow, o.Y + oh - h), (o.X + ow, moved.Y),      // right
                (o.X - w, o.Y), (o.X - w, o.Y + oh - h), (o.X - w, moved.Y),         // left
                (o.X, o.Y - h), (o.X + ow - w, o.Y - h), (moved.X, o.Y - h),         // above
                (o.X, o.Y + oh), (o.X + ow - w, o.Y + oh), (moved.X, o.Y + oh),      // below
            };
            foreach (var (cx, cy) in candidates)
            {
                // the moved display must actually touch the other display along the shared edge
                var touchesVertically = cx == o.X + ow || cx + w == o.X;
                var touchesHorizontally = cy == o.Y + oh || cy + h == o.Y;
                var overlapY = Math.Min(cy + h, o.Y + oh) - Math.Max(cy, o.Y);
                var overlapX = Math.Min(cx + w, o.X + ow) - Math.Max(cx, o.X);
                if (touchesVertically && overlapY <= 0) continue;
                if (touchesHorizontally && overlapX <= 0) continue;
                var dist = Math.Abs(cx - moved.X) + Math.Abs(cy - moved.Y);
                if (dist < best.dist && dist <= threshold * 2) best = (dist, cx, cy);
            }
        }
        if (best.dist != int.MaxValue)
        {
            moved.X = best.x;
            moved.Y = best.y;
        }
    }

    private void ResolveOverlap(Item moved)
    {
        for (var guard = 0; guard < 8; guard++)
        {
            var hit = _items.FirstOrDefault(o => o != moved && Overlaps(moved, o));
            if (hit == null) return;
            // push out to the nearest side
            var w = moved.Display.Width;
            var h = moved.Display.Height;
            var dxRight = hit.X + hit.Display.Width - moved.X;
            var dxLeft = moved.X + w - hit.X;
            var dyDown = hit.Y + hit.Display.Height - moved.Y;
            var dyUp = moved.Y + h - hit.Y;
            var min = Math.Min(Math.Min(dxRight, dxLeft), Math.Min(dyDown, dyUp));
            if (min == dxRight) moved.X += dxRight;
            else if (min == dxLeft) moved.X -= dxLeft;
            else if (min == dyDown) moved.Y += dyDown;
            else moved.Y -= dyUp;
        }
    }

    private static bool Overlaps(Item a, Item b)
    {
        return a.X < b.X + b.Display.Width && a.X + a.Display.Width > b.X && a.Y < b.Y + b.Display.Height && a.Y + a.Display.Height > b.Y;
    }
}
