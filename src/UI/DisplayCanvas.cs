using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Common;
using MoonlightHub.Core;

namespace MoonlightHub.UI;

/// <summary>
/// Scaled diagram of the active desktop (physical displays grey, Parsec virtual displays cyan) together with the screens
/// of the other Deskflow computers (amber), all in local desktop pixels so both share one arrangement.
/// When <see cref="Interactive"/> is on, displays and Deskflow screens can be dragged (with edge snapping) to compose a
/// new layout; the drafts are exposed through <see cref="DraftPositions"/> / <see cref="DraftPeers"/> and applied by the owning view.
/// </summary>
public sealed class DisplayCanvas : Canvas
{
    private sealed class Item
    {
        public DisplayInfo? Display;
        public PeerScreen? Peer;
        public SharedMonitor? Shared;
        public int X;
        public int Y;
        public int OrigX;
        public int OrigY;
        public int W;
        public int H;
        public Border? Visual;
        public bool Moved => X != OrigX || Y != OrigY;
    }

    private readonly List<Item> _items = new();
    private sealed record ResizeDraft(Item Item, Point Start, int Width, int Height, int Percent);
    private ResizeDraft? _resize;
    private int _resizePercent;
    private string _signature = string.Empty;
    private string _peerSignature = string.Empty;
    private Item? _dragItem;
    private bool _dragGroup;
    private Dictionary<Item, (int X, int Y)> _groupOrigins = new();
    private Point _dragStart;
    private int _dragOriginX, _dragOriginY;
    private double _scale = 1;
    private int _minX, _minY;
    private double _offX, _offY;

    public bool Interactive { get; set; }
    /// <summary>Local displays are shown but cannot be dragged (Linux: the desktop's own display settings own them).</summary>
    public bool LockDisplays { get; set; }
    public SharedDisplayDocument? SharedDocument { get; private set; }
    public bool HasSharedChanges => _items.Any(i => i.Shared != null && i.Moved);
    public bool HasChanges => HasDisplayChanges || HasPeerChanges || HasSharedChanges;
    public bool HasDisplayChanges => _items.Any(i => i.Display != null && i.Moved);
    public bool HasPeerChanges => _items.Any(i => i.Peer != null && i.Moved);
    public event Action? DraftChanged;
    public event Action<SharedMonitor, int>? ScaleRequested;

    public DisplayCanvas()
    {
        MinHeight = 200;
        ClipToBounds = true;
        Background = Brushes.Transparent;
        SizeChanged += (_, _) => Redraw();
    }

    /// <param name="document">两台电脑共同使用的逻辑桌面布局。</param>
    public void SetSharedDisplays(SharedDisplayDocument document)
    {
        if (_dragItem != null || _resize != null || HasChanges || SharedDocument?.Revision == document.Revision) return;
        SharedDocument = SharedDisplayLayout.Clone(document);
        _items.Clear();
        _items.AddRange(document.Monitors.Select(m => new Item { Shared = m, X = m.X, Y = m.Y,
            OrigX = m.X, OrigY = m.Y, W = m.CanvasWidth, H = m.CanvasHeight }));
        Redraw(); DraftChanged?.Invoke();
    }

    public SharedDisplayDocument? DraftSharedDisplays
    {
        get
        {
            if (SharedDocument == null) return null;
            var draft = SharedDisplayLayout.Clone(SharedDocument);
            draft.Monitors = _items.Where(i => i.Shared != null).Select(i => i.Shared! with { X = i.X, Y = i.Y }).ToList();
            return draft;
        }
    }

    /// <param name="document">缩放操作后服务端确认的新版本，保留尚未应用的位置草稿。</param>
    public void RebaseSharedDisplays(SharedDisplayDocument document)
    {
        if (_dragItem != null || _resize != null) return;
        var positions = _items.Where(i => i.Shared != null && i.Moved)
            .ToDictionary(i => (i.Shared!.Owner, i.Shared.Id), i => (i.X, i.Y));
        SharedDocument = null;
        _items.Clear();
        SetSharedDisplays(document);
        foreach (var item in _items.Where(i => i.Shared != null))
            if (positions.TryGetValue((item.Shared!.Owner, item.Shared.Id), out var position)) { item.X = position.X; item.Y = position.Y; }
        Redraw(); DraftChanged?.Invoke();
    }

    public void SetDisplays(IEnumerable<DisplayInfo> displays)
    {
        if (SharedDocument != null) return;
        var list = displays.Where(d => d.Active && d.Width > 0).ToList();
        var sig = string.Join("|", list.Select(d => $"{d.GdiName}:{d.PositionX},{d.PositionY},{d.Width},{d.Height},{d.RefreshRate},{d.Primary},{d.IsParsec}"));
        if (sig == _signature) return;
        if (_dragItem != null) return; // do not clobber an in-progress drag
        _signature = sig;
        // a dragged display keeps its draft while Windows still has it at the same place; a display that went away
        // (detached, unplugged) drops out — kept in the draft, "应用摆放" sent it back and was refused (2026-10-10)
        var drafts = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in _items.Where(i => i.Display != null && i.Moved)) drafts[i.Display!.GdiName] = i;
        _items.RemoveAll(i => i.Display != null);
        _items.InsertRange(0, list.Select(d =>
        {
            var item = new Item { Display = d, X = d.PositionX, Y = d.PositionY, OrigX = d.PositionX, OrigY = d.PositionY, W = d.Width, H = d.Height };
            if (drafts.TryGetValue(d.GdiName, out var old) && old.OrigX == d.PositionX && old.OrigY == d.PositionY && old.W == d.Width && old.H == d.Height)
            {
                item.X = old.X;
                item.Y = old.Y;
            }
            return item;
        }));
        Redraw();
        DraftChanged?.Invoke();
    }

    /// <summary>Screens of the other Deskflow computers, already mapped into local desktop pixels (see <see cref="DeskflowPeer.MapPeers"/>).</summary>
    public void SetPeers(IEnumerable<PeerScreen> peers)
    {
        if (SharedDocument != null) return;
        var list = peers.Where(p => p.Width > 0 && p.Height > 0).ToList();
        var sig = string.Join("|", list.Select(p => $"{p.Name}:{p.X},{p.Y},{p.Width},{p.Height},{p.IsServer}"));
        if (sig == _peerSignature) return;
        if (_dragItem != null || HasPeerChanges) return; // keep an unapplied peer draft
        _peerSignature = sig;
        _items.RemoveAll(i => i.Peer != null);
        _items.AddRange(list.Select(p => new Item { Peer = p, X = p.X, Y = p.Y, OrigX = p.X, OrigY = p.Y, W = p.Width, H = p.Height }));
        Redraw();
        DraftChanged?.Invoke();
    }

    /// <summary>Current draft positions of the local displays, including unchanged ones.</summary>
    public List<(DisplayInfo Display, int X, int Y)> DraftPositions => _items.Where(i => i.Display != null).Select(i => (i.Display!, i.X, i.Y)).ToList();

    /// <summary>Current draft positions of the Deskflow peer screens (local desktop pixels).</summary>
    public List<(string Name, int X, int Y)> DraftPeers => _items.Where(i => i.Peer != null).Select(i => (i.Peer!.Name, i.X, i.Y)).ToList();

    public void ResetDraft()
    {
        foreach (var i in _items) { i.X = i.OrigX; i.Y = i.OrigY; }
        Redraw();
        DraftChanged?.Invoke();
    }

    /// <summary>Drops the peer draft so the next <see cref="SetPeers"/> shows what the layout service stored.</summary>
    public void ClearPeerDraft()
    {
        if (SharedDocument != null) { SharedDocument = null; _items.Clear(); return; }
        _peerSignature = string.Empty;
        foreach (var i in _items.Where(i => i.Peer != null)) { i.X = i.OrigX; i.Y = i.OrigY; }
    }

    private bool CanDrag(Item item) => Interactive && (item.Shared != null || item.Peer != null || !LockDisplays);

    private void Redraw()
    {
        Children.Clear();
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (_items.Count == 0 || width < 20 || height < 20)
        {
            if (width > 20)
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
        var maxX = _items.Max(i => i.X + i.W);
        var maxY = _items.Max(i => i.Y + i.H);
        var w = Math.Max(1, maxX - _minX);
        var h = Math.Max(1, maxY - _minY);
        const double pad = 18;
        _scale = Math.Min((width - pad * 2) / w, (height - pad * 2) / h);
        _offX = pad + ((width - pad * 2) - w * _scale) / 2;
        _offY = pad + ((height - pad * 2) - h * _scale) / 2;

        foreach (var item in _items)
        {
            var border = item.Shared is { } shared ? SharedVisual(item, shared, _items.IndexOf(item) + 1) : item.Display is { } d ? DisplayVisual(item, d) : PeerVisual(item, item.Peer!);
            item.Visual = border;
            SetLeft(border, _offX + (item.X - _minX) * _scale + 2);
            SetTop(border, _offY + (item.Y - _minY) * _scale + 2);
            if (CanDrag(item))
            {
                border.Cursor = new Cursor(StandardCursorType.SizeAll);
                border.PointerPressed += Border_PointerPressed;
                border.PointerMoved += Border_PointerMoved;
                border.PointerReleased += Border_PointerReleased;
            }
            Children.Add(border);
        }
    }

    private Border DisplayVisual(Item item, DisplayInfo d)
    {
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
            ClipToBounds = true,
            Tag = item
        };
        ToolTip.SetTip(border, d + (CanDrag(item) ? "\n拖动可调整位置" : string.Empty));
        var stack = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        if (d.Primary) head.Children.Add(new FluentIcons.Avalonia.SymbolIcon { Symbol = Symbol.Star, FontSize = 11, Foreground = Ui.Res("WarnBrush"), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = d.Label, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = Ui.Res(isVirtual ? "AccentBrush" : "TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(head);
        stack.Children.Add(new TextBlock { Text = d.ModeText, FontSize = 11, Foreground = Ui.Res("MutedBrush") });
        stack.Children.Add(new TextBlock { Text = $"{d.GdiName}  ({item.X}, {item.Y})", FontSize = 10, Foreground = Ui.Res("MutedBrush") });
        border.Child = stack;
        return border;
    }

    /// <param name="item">按可用桌面尺寸绘制的屏幕。</param>
    /// <param name="monitor">该屏幕的完整信息，显示在图例和提示中。</param>
    /// <param name="number">与下方图例对应的屏幕编号。</param>
    private Border SharedVisual(Item item, SharedMonitor monitor, int number)
    {
        var border = new Border { Width = Math.Max(24, item.W * _scale - 4), Height = Math.Max(18, item.H * _scale - 4),
            CornerRadius = new CornerRadius(6), Background = Ui.Res(monitor.Virtual ? "AccentSoftBrush" : "Surface3Brush"),
            BorderBrush = Ui.Res(monitor.Virtual ? "AccentBrush" : "BorderBrush"), BorderThickness = new Thickness(monitor.Primary ? 2 : 1),
            Padding = new Thickness(4), ClipToBounds = true, Tag = item, Opacity = monitor.Active ? 1 : .6 };
        ToolTip.SetTip(border, $"{number} · {monitor.Owner} · {monitor.Name}\n渲染 {monitor.Width}×{monitor.Height}，可用桌面 {monitor.CanvasWidth}×{monitor.CanvasHeight}\nWindows {monitor.WindowsScalePercent}% · 额外 {monitor.ContentScalePercent}%{(monitor.ScalePending ? " · 待生效" : "")}\n拖动主屏或按 Shift 可移动整台电脑，其它屏幕单独移动。间隙自动跨越。" );
        // 屏幕框只显示编号，文字信息放到图例，避免小屏缩放后被裁切；边框尺寸仍严格保持比例。
        var content = new Grid();
        content.Children.Add(new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = number.ToString(), FontSize = 24, FontWeight = FontWeight.SemiBold,
                Foreground = Ui.Res(monitor.Virtual ? "AccentBrush" : "TextBrush") } });
        if (monitor.CanScale && Interactive)
        {
            var handle = new Border { Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom, Cursor = new Cursor(StandardCursorType.BottomRightCorner),
                Background = Brushes.Transparent, BorderBrush = Ui.Res("AccentBrush"), BorderThickness = new Thickness(0, 0, 3, 3) };
            ToolTip.SetTip(handle, "拖动调节缩放：缩小矩形会放大文字，放大矩形会显示更多内容；松开自动保存。");
            handle.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
                _resize = new ResizeDraft(item, e.GetPosition(this), item.W, item.H, monitor.ContentScalePercent);
                _resizePercent = monitor.ContentScalePercent;
                e.Pointer.Capture(handle); e.Handled = true;
            };
            handle.PointerMoved += Resize_PointerMoved;
            handle.PointerReleased += (_, e) =>
            {
                if (_resize is not { } draft) return;
                _resize = null; e.Pointer.Capture(null); e.Handled = true;
                if (_resizePercent != draft.Percent) ScaleRequested?.Invoke(monitor, _resizePercent);
                else ResetSizePreview();
            };
            content.Children.Add(handle);
        }
        border.Child = content;
        return border;
    }

    /// <param name="sender">虚拟屏右下角的尺寸手柄。</param>
    /// <param name="e">画布坐标下的拖动事件。</param>
    private void Resize_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_resize is not { } draft || draft.Item.Visual is not { } visual) return;
        var delta = (e.GetPosition(this) - draft.Start) / _scale;
        var factor = Math.Abs(delta.X / draft.Width) >= Math.Abs(delta.Y / draft.Height)
            ? 1 + delta.X / draft.Width : 1 + delta.Y / draft.Height;
        _resizePercent = Math.Clamp((int)Math.Round(draft.Percent / Math.Max(.01, factor)), VirtualContentScale.Minimum, VirtualContentScale.Maximum);
        draft.Item.W = Math.Max(1, (int)Math.Round(draft.Width * (double)draft.Percent / _resizePercent));
        draft.Item.H = Math.Max(1, (int)Math.Round(draft.Height * (double)draft.Percent / _resizePercent));
        visual.Width = Math.Max(24, draft.Item.W * _scale - 4);
        visual.Height = Math.Max(18, draft.Item.H * _scale - 4);
        ToolTip.SetTip(visual, $"额外放大 {_resizePercent}% · 松开自动保存");
        e.Handled = true;
    }

    public void ResetSizePreview()
    {
        foreach (var item in _items.Where(i => i.Shared != null)) { item.W = item.Shared!.CanvasWidth; item.H = item.Shared.CanvasHeight; }
        Redraw();
    }

    private Border PeerVisual(Item item, PeerScreen p)
    {
        var border = new Border
        {
            Width = Math.Max(24, item.W * _scale - 4),
            Height = Math.Max(18, item.H * _scale - 4),
            CornerRadius = new CornerRadius(6),
            Background = Ui.Res("WarnSoftBrush"),
            BorderBrush = Ui.Res("WarnBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
            ClipToBounds = true,
            Tag = item
        };
        ToolTip.SetTip(border, $"Deskflow 屏幕 {p.Name}（{p.ResolutionWidth}×{p.ResolutionHeight}{(p.IsServer ? "，服务端" : string.Empty)}）\n鼠标从相邻的边移过去就会切到这台电脑" + (CanDrag(item) ? "\n拖动可调整它和本机显示器的相对位置" : string.Empty));
        var stack = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new FluentIcons.Avalonia.SymbolIcon { Symbol = Symbol.Keyboard, FontSize = 11, Foreground = Ui.Res("WarnBrush"), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = p.Name, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = Ui.Res("WarnBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(head);
        stack.Children.Add(new TextBlock { Text = $"Deskflow · {p.ResolutionWidth}×{p.ResolutionHeight}{(p.IsServer ? " · 服务端" : string.Empty)}", FontSize = 11, Foreground = Ui.Res("MutedBrush") });
        border.Child = stack;
        return border;
    }

    private void Border_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: Item item } border || !e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
        _dragItem = item;
        _dragStart = e.GetPosition(this);
        _dragOriginX = item.X;
        _dragOriginY = item.Y;
        _dragGroup = item.Shared != null && (item.Shared.Primary || e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        _groupOrigins = _dragGroup ? _items.Where(i => i.Shared?.Owner == item.Shared!.Owner).ToDictionary(i => i, i => (i.X, i.Y)) : new();
        e.Pointer.Capture(border);
        e.Handled = true;
    }

    private void Border_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem?.Visual == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        var dx = (p.X - _dragStart.X) / _scale;
        var dy = (p.Y - _dragStart.Y) / _scale;
        _dragItem.X = _dragOriginX + (int)Math.Round(dx);
        _dragItem.Y = _dragOriginY + (int)Math.Round(dy);
        if (_dragGroup)
        {
            foreach (var (other, origin) in _groupOrigins)
            {
                other.X = origin.X + (int)Math.Round(dx); other.Y = origin.Y + (int)Math.Round(dy);
                if (other.Visual != null) { SetLeft(other.Visual, _offX + (other.X - _minX) * _scale + 2); SetTop(other.Visual, _offY + (other.Y - _minY) * _scale + 2); }
            }
        }
        SetLeft(_dragItem.Visual, _offX + (_dragItem.X - _minX) * _scale + 2);
        SetTop(_dragItem.Visual, _offY + (_dragItem.Y - _minY) * _scale + 2);
    }

    private void Border_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragItem == null) return;
        e.Pointer.Capture(null);
        var item = _dragItem;
        _dragItem = null;
        var beforeX = item.X; var beforeY = item.Y;
        Snap(item);
        ResolveOverlap(item);
        if (_dragGroup) foreach (var other in _groupOrigins.Keys.Where(i => i != item)) { other.X += item.X - beforeX; other.Y += item.Y - beforeY; }
        _dragGroup = false;
        Redraw();
        DraftChanged?.Invoke();
    }

    /// <summary>Aligns the moved item to the closest edges of the others (Windows-settings style snapping).</summary>
    private void Snap(Item moved)
    {
        // local displays must form one desktop, so they only snap to each other; Deskflow screens snap to anything
        var others = _items.Where(i => i != moved && (!_dragGroup || i.Shared?.Owner != moved.Shared?.Owner) && (moved.Shared != null || moved.Peer != null || i.Display != null)).ToList();
        if (others.Count == 0) return;
        var threshold = moved.Peer != null ? 400 : 140;
        var w = moved.W;
        var h = moved.H;
        var best = (dist: int.MaxValue, x: moved.X, y: moved.Y);

        foreach (var o in others)
        {
            var ow = o.W;
            var oh = o.H;
            // candidate placements around the other item
            var candidates = new List<(int x, int y)>
            {
                (o.X + ow, o.Y), (o.X + ow, o.Y + oh - h), (o.X + ow, moved.Y),      // right
                (o.X - w, o.Y), (o.X - w, o.Y + oh - h), (o.X - w, moved.Y),         // left
                (o.X, o.Y - h), (o.X + ow - w, o.Y - h), (moved.X, o.Y - h),         // above
                (o.X, o.Y + oh), (o.X + ow - w, o.Y + oh), (moved.X, o.Y + oh),      // below
            };
            foreach (var (cx, cy) in candidates)
            {
                // the moved item must actually touch the other one along the shared edge
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
            var hit = _items.FirstOrDefault(o => o != moved && (!_dragGroup || o.Shared?.Owner != moved.Shared?.Owner) && Overlaps(moved, o));
            if (hit == null) return;
            // push out to the nearest side
            var w = moved.W;
            var h = moved.H;
            var dxRight = hit.X + hit.W - moved.X;
            var dxLeft = moved.X + w - hit.X;
            var dyDown = hit.Y + hit.H - moved.Y;
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
        return a.X < b.X + b.W && a.X + a.W > b.X && a.Y < b.Y + b.H && a.Y + a.H > b.Y;
    }
}
