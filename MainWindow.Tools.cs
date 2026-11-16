using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.System;
using Windows.UI;

namespace Glimpse;

public sealed partial class MainWindow
{
    private enum Drag { None, Draw, MoveItem, Resize, TextSelect, Marquee }

    private Drag _drag;
    private Point _start;
    private FrameworkElement? _draft;        // shape being drawn
    private FrameworkElement? _selected;     // markup selected with the Move tool
    private Rect _origBounds;
    private double _origLeft, _origTop;
    private int _anchorWord = -1;
    private int _selA = -1, _selB = -1;      // selected OCR word range
    private Rect? _marquee;

    private TextBox? _editor;
    private Border? _editTarget;
    private bool _editIsNew;

    private double PageScale => Cur?.Scale ?? 1;
    private double Zoom => Math.Max(0.01, _scroll.ZoomFactor);

    // ------------------------------------------------------------------ pointer input

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = Cur;
        if (p == null) return;
        var point = e.GetCurrentPoint(_surface);
        if (!point.Properties.IsLeftButtonPressed) return;
        CommitEdit();
        var pt = point.Position;
        _start = pt;
        _drag = Drag.None;
        _draft = null;
        _surface.CapturePointer(e.Pointer);

        switch (_tool)
        {
            case Tool.Move:
                if (_selected != null && _selected.Tag is Anno { Resizable: true } && NearHandle(pt))
                {
                    _drag = Drag.Resize;
                    _origBounds = BoundsOf(_selected);
                }
                else
                {
                    _selected = HitTest(pt);
                    if (_selected != null)
                    {
                        _drag = Drag.MoveItem;
                        _origLeft = Canvas.GetLeft(_selected);
                        _origTop = Canvas.GetTop(_selected);
                    }
                }
                break;

            case Tool.TextSelect:
                _selA = _selB = -1;
                _anchorWord = NearestWord(pt);
                _drag = Drag.TextSelect;
                break;

            case Tool.RectSelect:
                _marquee = null;
                _drag = Drag.Marquee;
                break;

            case Tool.Text:
                break;   // placed on release

            default:
                _draft = CreateDraft(pt);
                _anno.Children.Add(_draft);
                _drag = Drag.Draw;
                break;
        }
        RedrawOverlay();
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = Cur;
        if (p == null || _drag == Drag.None) return;
        var pt = e.GetCurrentPoint(_surface).Position;
        double dx = pt.X - _start.X, dy = pt.Y - _start.Y;

        switch (_drag)
        {
            case Drag.Draw when _draft != null:
                UpdateDraft(_draft, ClampToPage(pt, p));
                break;

            case Drag.MoveItem when _selected != null:
                Canvas.SetLeft(_selected, _origLeft + dx);
                Canvas.SetTop(_selected, _origTop + dy);
                break;

            case Drag.Resize when _selected != null:
                ResizeTo(_selected, Math.Max(8, _origBounds.Width + dx), Math.Max(8, _origBounds.Height + dy));
                break;

            case Drag.TextSelect:
                if (_anchorWord >= 0 && (Math.Abs(dx) > 3 || Math.Abs(dy) > 3))
                {
                    _selA = _anchorWord;
                    _selB = NearestWord(pt);
                }
                break;

            case Drag.Marquee:
                var q = ClampToPage(pt, p);
                var s = ClampToPage(_start, p);
                _marquee = new Rect(Math.Min(s.X, q.X), Math.Min(s.Y, q.Y), Math.Abs(q.X - s.X), Math.Abs(q.Y - s.Y));
                break;
        }
        RedrawOverlay();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var p = Cur;
        var drag = _drag;
        var draft = _draft;
        _drag = Drag.None;
        _draft = null;
        _surface.ReleasePointerCapture(e.Pointer);
        if (p == null) return;
        var pt = e.GetCurrentPoint(_surface).Position;

        switch (drag)
        {
            case Drag.Draw when draft != null:
                var el = draft;
                FinishDraft(el);
                var b = BoundsOf(el);
                bool tooSmall = el is Polyline pl ? pl.Points.Count < 2 : Math.Max(b.Width, b.Height) < 3;
                if (tooSmall) _anno.Children.Remove(el);
                else CommitNew(p, el);
                break;

            case Drag.MoveItem when _selected != null:
                {
                    var item = _selected;
                    double ol = _origLeft, ot = _origTop, nl = Canvas.GetLeft(item), nt = Canvas.GetTop(item);
                    if (Math.Abs(nl - ol) > 0.5 || Math.Abs(nt - ot) > 0.5)
                    {
                        _history.Push(() => { Canvas.SetLeft(item, ol); Canvas.SetTop(item, ot); Refresh(p); },
                                      () => { Canvas.SetLeft(item, nl); Canvas.SetTop(item, nt); Refresh(p); });
                        MarkDirty();
                    }
                    break;
                }

            case Drag.Resize when _selected != null:
                {
                    var item = _selected;
                    var ob = _origBounds;
                    var nb = BoundsOf(item);
                    if (Math.Abs(nb.Width - ob.Width) > 0.5 || Math.Abs(nb.Height - ob.Height) > 0.5)
                    {
                        _history.Push(() => { ResizeTo(item, ob.Width, ob.Height, true); Refresh(p); },
                                      () => { ResizeTo(item, nb.Width, nb.Height, true); Refresh(p); });
                        MarkDirty();
                    }
                    break;
                }

            case Drag.Marquee:
                if (_marquee is Rect m && (m.Width < 3 || m.Height < 3)) _marquee = null;
                else if (_marquee != null) SetStatus("Area selected: Ctrl+C copies it as an image, right-click for text and crop.");
                break;
        }

        if (_tool == Tool.Text && drag == Drag.None)
        {
            if (HitTest(pt) is Border existing && existing.Tag is Anno { Kind: Tool.Text }) BeginEdit(existing, false);
            else
            {
                var border = MakeText("", _stroke, _fontSize * PageScale);
                Canvas.SetLeft(border, pt.X);
                Canvas.SetTop(border, pt.Y - _fontSize * PageScale * 0.8);
                _anno.Children.Add(border);
                BeginEdit(border, true);
            }
        }
        RedrawOverlay();
        e.Handled = true;
    }

    private void CancelDrag()
    {
        if (_drag == Drag.Draw && _draft != null) _anno.Children.Remove(_draft);
        _draft = null;
        _drag = Drag.None;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var p = Cur;
        if (p == null) return;
        var pt = e.GetPosition(_surface);
        if (_tool == Tool.TextSelect && p.Words != null)
        {
            int i = p.Words.FindIndex(w => Inflate(w.Box, 2).Contains(pt));
            if (i >= 0) { _selA = _selB = i; RedrawOverlay(); }
        }
        else if (_tool == Tool.Move && HitTest(pt) is Border b && b.Tag is Anno { Kind: Tool.Text })
        {
            BeginEdit(b, false);
        }
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var p = Cur;
        if (p == null) return;
        var menu = new MenuFlyout();
        bool hasText = _selA >= 0 && _selB >= 0;
        menu.Items.Add(Item(hasText ? "Copy text" : _marquee != null ? "Copy selected area" : "Copy page as image", () => Run(CopyAsync), ""));
        if (_marquee != null)
        {
            menu.Items.Add(Item("Copy text in selected area", () => Run(CopyTextInMarqueeAsync)));
            menu.Items.Add(Item("Crop to selected area", () => Run(CropAsync), ""));
        }
        menu.Items.Add(Item("Paste", () => Run(PasteAsync), ""));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Select all text", () => Run(SelectAllTextAsync)));
        menu.Items.Add(Item("Copy all text on this page", () => Run(CopyAllTextAsync)));
        if (_selected != null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("Delete markup", DeleteSelected, ""));
        }
        menu.ShowAt(_surface, e.GetPosition(_surface));
        e.Handled = true;
    }

    private static Point ClampToPage(Point pt, DocumentPage p) =>
        new(Math.Clamp(pt.X, 0, p.PixelWidth), Math.Clamp(pt.Y, 0, p.PixelHeight));

    private static Rect Inflate(Rect r, double by) => new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);

    // ------------------------------------------------------------------ OCR text selection

    /// <summary>Finds the word to anchor a selection to: the closest line first, then the closest word on it.</summary>
    private int NearestWord(Point pt)
    {
        var words = Cur?.Words;
        if (words == null || words.Count == 0) return -1;
        int bestLine = -1;
        double bestDy = double.MaxValue;
        foreach (var w in words)
        {
            double dy = pt.Y < w.Box.Top ? w.Box.Top - pt.Y : pt.Y > w.Box.Bottom ? pt.Y - w.Box.Bottom : 0;
            if (dy < bestDy) { bestDy = dy; bestLine = w.Line; }
        }
        int best = -1;
        double bestDx = double.MaxValue;
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (w.Line != bestLine) continue;
            double dx = pt.X < w.Box.Left ? w.Box.Left - pt.X : pt.X > w.Box.Right ? pt.X - w.Box.Right : 0;
            if (dx < bestDx) { bestDx = dx; best = i; }
        }
        return best;
    }

    // ------------------------------------------------------------------ overlay (selection visuals)

    private void RedrawOverlay()
    {
        _overlay.Children.Clear();
        var p = Cur;
        if (p == null) return;
        double z = Zoom;

        if (_selA >= 0 && _selB >= 0 && p.Words is { } words && words.Count > 0)
        {
            int lo = Math.Clamp(Math.Min(_selA, _selB), 0, words.Count - 1), hi = Math.Clamp(Math.Max(_selA, _selB), 0, words.Count - 1);
            var brush = Brush(Color.FromArgb(90, Accent.R, Accent.G, Accent.B));
            int i = lo;
            while (i <= hi)
            {
                int line = words[i].Line;
                Rect r = words[i].Box;
                int j = i + 1;
                while (j <= hi && words[j].Line == line) { r.Union(words[j].Box); j++; }
                r = Inflate(r, 1.5 * p.Scale);
                var hl = new Rectangle { Width = r.Width, Height = r.Height, Fill = brush, RadiusX = 2 * p.Scale, RadiusY = 2 * p.Scale };
                Canvas.SetLeft(hl, r.X); Canvas.SetTop(hl, r.Y);
                _overlay.Children.Add(hl);
                i = j;
            }
        }

        if (_marquee is Rect m)
        {
            var shade = Brush(Color.FromArgb(70, 0, 0, 0));
            void Shade(double x, double y, double w, double h)
            {
                if (w <= 0 || h <= 0) return;
                var s = new Rectangle { Width = w, Height = h, Fill = shade };
                Canvas.SetLeft(s, x); Canvas.SetTop(s, y);
                _overlay.Children.Add(s);
            }
            Shade(0, 0, p.PixelWidth, m.Top);
            Shade(0, m.Bottom, p.PixelWidth, p.PixelHeight - m.Bottom);
            Shade(0, m.Top, m.Left, m.Height);
            Shade(m.Right, m.Top, p.PixelWidth - m.Right, m.Height);
            AddOutline(m, z, Colors.White, false);
            AddOutline(m, z, Colors.Black, true);
        }

        if (_selected != null && _tool == Tool.Move && p.Annotations.Contains(_selected))
        {
            var b = Inflate(BoundsOf(_selected), 3 / z);
            AddOutline(b, z, Accent, true);
            if (_selected.Tag is Anno { Resizable: true })
            {
                double hs = 10 / z;
                var handle = new Rectangle { Width = hs, Height = hs, Fill = Brush(Colors.White), Stroke = Brush(Accent), StrokeThickness = 1.5 / z };
                Canvas.SetLeft(handle, b.Right - hs / 2); Canvas.SetTop(handle, b.Bottom - hs / 2);
                _overlay.Children.Add(handle);
            }
        }
    }

    private void AddOutline(Rect r, double zoom, Color color, bool dashed)
    {
        var o = new Rectangle { Width = Math.Max(0, r.Width), Height = Math.Max(0, r.Height), Stroke = Brush(color), StrokeThickness = 1.5 / zoom };
        if (dashed) o.StrokeDashArray = new DoubleCollection { 4, 3 };
        Canvas.SetLeft(o, r.X); Canvas.SetTop(o, r.Y);
        _overlay.Children.Add(o);
    }

    private void ClearSelections()
    {
        CommitEdit();
        _selA = _selB = -1; _marquee = null; _selected = null;
        RedrawOverlay();
    }

    // ------------------------------------------------------------------ markup elements

    private static Rect BoundsOf(FrameworkElement el)
    {
        double l = Canvas.GetLeft(el), t = Canvas.GetTop(el);
        if (el.Tag is Anno { Local: Rect lb }) return new Rect(l + lb.X, t + lb.Y, lb.Width, lb.Height);
        double w = double.IsNaN(el.Width) ? el.ActualWidth : el.Width, h = double.IsNaN(el.Height) ? el.ActualHeight : el.Height;
        return new Rect(l, t, w, h);
    }

    private FrameworkElement? HitTest(Point pt)
    {
        var p = Cur;
        if (p == null) return null;
        for (int i = p.Annotations.Count - 1; i >= 0; i--)
            if (Inflate(BoundsOf(p.Annotations[i]), 4 / Zoom).Contains(pt)) return p.Annotations[i];
        return null;
    }

    private bool NearHandle(Point pt)
    {
        if (_selected == null) return false;
        var b = Inflate(BoundsOf(_selected), 3 / Zoom);
        double r = 12 / Zoom;
        return Math.Abs(pt.X - b.Right) <= r && Math.Abs(pt.Y - b.Bottom) <= r;
    }

    private static void ResizeTo(FrameworkElement el, double w, double h, bool exact = false)
    {
        var a = el.Tag as Anno;
        if (a?.Kind == Tool.Text) { el.Width = w; return; }
        if (a is { IsImage: true } && !exact && el.Width > 0 && el.Height > 0)
        {
            double k = Math.Max(w / el.Width, h / el.Height);
            w = el.Width * k; h = el.Height * k;
        }
        el.Width = w; el.Height = h;
    }

    private FrameworkElement CreateDraft(Point pt)
    {
        double th = _thickness * PageScale;
        var stroke = Brush(_stroke);
        Brush? fill = _fill is Color f ? Brush(f) : null;
        switch (_tool)
        {
            case Tool.Rect:
            case Tool.Ellipse:
                Shape s = _tool == Tool.Rect ? new Rectangle() : new Ellipse();
                s.Stroke = stroke; s.StrokeThickness = th; s.Fill = fill; s.Width = 0; s.Height = 0;
                s.Tag = new Anno { Kind = _tool, Resizable = true };
                Canvas.SetLeft(s, pt.X); Canvas.SetTop(s, pt.Y);
                return s;

            case Tool.Line:
            case Tool.Arrow:
                var path = new Path
                {
                    Stroke = stroke, StrokeThickness = th, Fill = stroke,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                    Tag = new Anno { Kind = _tool, Local = new Rect(pt.X, pt.Y, 0, 0) },
                };
                return path;

            default:
                bool hi = _tool == Tool.Highlight;
                var c = hi ? Color.FromArgb(110, _highlight.R, _highlight.G, _highlight.B) : _stroke;
                var line = new Polyline
                {
                    Stroke = Brush(c), StrokeThickness = hi ? Math.Max(14, _thickness * 5) * PageScale : th,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = hi ? PenLineCap.Flat : PenLineCap.Round,
                    StrokeEndLineCap = hi ? PenLineCap.Flat : PenLineCap.Round,
                    Tag = new Anno { Kind = _tool, Local = new Rect(pt.X, pt.Y, 0, 0) },
                };
                line.Points.Add(pt);
                return line;
        }
    }

    private void UpdateDraft(FrameworkElement el, Point pt)
    {
        switch (el)
        {
            case Path path:
                bool arrow = ((Anno)path.Tag).Kind == Tool.Arrow;
                path.Data = LineGeometry(_start, pt, arrow, path.StrokeThickness);
                break;
            case Polyline line:
                line.Points.Add(pt);
                break;
            default:
                Canvas.SetLeft(el, Math.Min(_start.X, pt.X)); Canvas.SetTop(el, Math.Min(_start.Y, pt.Y));
                el.Width = Math.Abs(pt.X - _start.X); el.Height = Math.Abs(pt.Y - _start.Y);
                break;
        }
    }

    /// <summary>Records the final bounds of lines and ink so they can be hit-tested and moved.</summary>
    private static void FinishDraft(FrameworkElement el)
    {
        if (el.Tag is not Anno a) return;
        if (el is Polyline line && line.Points.Count > 0)
        {
            double x1 = line.Points.Min(q => q.X), y1 = line.Points.Min(q => q.Y), x2 = line.Points.Max(q => q.X), y2 = line.Points.Max(q => q.Y);
            double pad = line.StrokeThickness / 2;
            a.Local = new Rect(x1 - pad, y1 - pad, x2 - x1 + 2 * pad, y2 - y1 + 2 * pad);
        }
        else if (el is Path path && path.Data != null)
        {
            var g = path.Data.Bounds;
            double pad = path.StrokeThickness / 2;
            a.Local = new Rect(g.X - pad, g.Y - pad, g.Width + 2 * pad, g.Height + 2 * pad);
        }
    }

    private static Geometry LineGeometry(Point a, Point b, bool arrow, double thickness)
    {
        var geo = new PathGeometry();
        double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        var end = b;
        if (arrow && len > 0.01)
        {
            double ux = (b.X - a.X) / len, uy = (b.Y - a.Y) / len;
            double head = Math.Min(len, Math.Max(12, thickness * 4.5)), half = head * 0.45;
            var bx = new Point(b.X - ux * head, b.Y - uy * head);
            end = new Point(b.X - ux * head * 0.6, b.Y - uy * head * 0.6);
            var tri = new PathFigure { StartPoint = b, IsClosed = true, IsFilled = true };
            tri.Segments.Add(new LineSegment { Point = new Point(bx.X - uy * half, bx.Y + ux * half) });
            tri.Segments.Add(new LineSegment { Point = new Point(bx.X + uy * half, bx.Y - ux * half) });
            geo.Figures.Add(tri);
        }
        var fig = new PathFigure { StartPoint = a, IsClosed = false, IsFilled = false };
        fig.Segments.Add(new LineSegment { Point = end });
        geo.Figures.Add(fig);
        return geo;
    }

    private void CommitNew(DocumentPage p, FrameworkElement el)
    {
        if (!p.Annotations.Contains(el)) p.Annotations.Add(el);
        _history.Push(() => { p.Annotations.Remove(el); Refresh(p); },
                      () => { if (!p.Annotations.Contains(el)) p.Annotations.Add(el); Refresh(p); });
        MarkDirty();
    }

    private void DeleteSelected()
    {
        var p = Cur;
        var el = _selected;
        if (p == null || el == null) return;
        int at = p.Annotations.IndexOf(el);
        if (at < 0) return;
        _selected = null;
        p.Annotations.RemoveAt(at);
        _anno.Children.Remove(el);
        _history.Push(() => { p.Annotations.Insert(Math.Min(at, p.Annotations.Count), el); Refresh(p); },
                      () => { p.Annotations.Remove(el); Refresh(p); });
        MarkDirty();
        RedrawOverlay();
    }

    /// <summary>Applies the toolbar's colour, fill, thickness and text size to the selected markup.</summary>
    private void ApplyStyleToSelection()
    {
        if (_selected == null || _tool != Tool.Move || _selected.Tag is not Anno a) return;
        switch (_selected)
        {
            case Border { Child: TextBlock t }:
                t.Foreground = Brush(_stroke);
                t.FontSize = _fontSize * PageScale;
                break;
            case Shape s when a.Kind != Tool.Highlight:
                s.Stroke = Brush(_stroke);
                s.StrokeThickness = _thickness * PageScale;
                if (s is Path) s.Fill = s.Stroke;
                else if (s is Rectangle or Ellipse) s.Fill = _fill is Color f ? Brush(f) : null;
                break;
            case Shape s:
                s.Stroke = Brush(Color.FromArgb(110, _stroke.R, _stroke.G, _stroke.B));
                break;
        }
        MarkDirty();
        RedrawOverlay();
    }

    // ------------------------------------------------------------------ text boxes

    private static Border MakeText(string text, Color color, double fontPixels) => new()
    {
        Padding = new Thickness(fontPixels * 0.3, fontPixels * 0.15, fontPixels * 0.3, fontPixels * 0.15),
        Child = new TextBlock { Text = text, FontSize = fontPixels, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap },
        Tag = new Anno { Kind = Tool.Text, Resizable = true },
    };

    private void BeginEdit(Border target, bool isNew)
    {
        var p = Cur;
        if (p == null) return;
        CommitEdit();
        var block = (TextBlock)target.Child;
        double left = Canvas.GetLeft(target), top = Canvas.GetTop(target);
        var box = new TextBox
        {
            Text = block.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            FontSize = block.FontSize, Foreground = block.Foreground, RequestedTheme = ElementTheme.Light,
            Padding = target.Padding, MinWidth = block.FontSize * 6, MinHeight = 0,
            BorderThickness = new Thickness(Math.Max(1, 1 / Zoom)),
        };
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundFocused", "TextControlForegroundPointerOver" })
            box.Resources[key] = block.Foreground;
        if (!double.IsNaN(target.Width)) box.Width = target.Width;
        else box.MaxWidth = Math.Max(block.FontSize * 6, p.PixelWidth - left);
        Canvas.SetLeft(box, left); Canvas.SetTop(box, top);
        target.Opacity = 0;
        _editTarget = target; _editIsNew = isNew; _editor = box;
        box.LostFocus += (_, _) => { if (_editor == box) CommitEdit(); };
        box.KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape) { CommitEdit(); e.Handled = true; } };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectionStart = box.Text.Length; };
        _editLayer.Children.Add(box);
    }

    /// <summary>Finishes typing in a text box and turns it into markup (or removes it when left empty).</summary>
    private void CommitEdit()
    {
        var box = _editor;
        var target = _editTarget;
        if (box == null || target == null) return;
        _editor = null; _editTarget = null;
        var p = Cur;
        var block = (TextBlock)target.Child;
        string text = box.Text.TrimEnd(), old = block.Text;
        _editLayer.Children.Remove(box);
        target.Opacity = 1;
        if (p == null) return;

        if (_editIsNew)
        {
            if (text.Length == 0) _anno.Children.Remove(target);
            else { block.Text = text; CommitNew(p, target); }
        }
        else if (text.Length == 0)
        {
            _selected = target;
            DeleteSelected();
        }
        else if (text != old)
        {
            block.Text = text;
            _history.Push(() => { block.Text = old; Refresh(p); }, () => { block.Text = text; Refresh(p); });
            MarkDirty();
        }
    }

    private Point ViewCenter()
    {
        var p = Cur!;
        try
        {
            var pt = _scroll.TransformToVisual(_surface).TransformPoint(new Point(_scroll.ActualWidth / 2, _scroll.ActualHeight / 2));
            return ClampToPage(pt, p);
        }
        catch { return new Point(p.PixelWidth / 2.0, p.PixelHeight / 2.0); }
    }

    private void AddTextAnnotation(string text)
    {
        var p = Cur!;
        var c = ViewCenter();
        var border = MakeText(text, _stroke, _fontSize * PageScale);
        border.Width = Math.Min(p.PixelWidth * 0.6, Math.Max(_fontSize * PageScale * 8, text.Length * _fontSize * PageScale * 0.6));
        Canvas.SetLeft(border, Math.Max(0, c.X - border.Width / 2));
        Canvas.SetTop(border, Math.Max(0, c.Y - _fontSize * PageScale));
        _anno.Children.Add(border);
        CommitNew(p, border);
        SetTool(Tool.Move);
        _selected = border;
        RedrawOverlay();
    }

    private async Task AddImageAnnotationAsync(SoftwareBitmap bmp)
    {
        var p = Cur!;
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bmp);
        double w = bmp.PixelWidth * p.Scale, h = bmp.PixelHeight * p.Scale;
        double k = Math.Min(1, Math.Min(p.PixelWidth * 0.7 / w, p.PixelHeight * 0.7 / h));
        w *= k; h *= k;
        var image = new Image
        {
            Source = source, Width = w, Height = h, Stretch = Stretch.Fill,
            Tag = new Anno { Kind = Tool.Move, Resizable = true, IsImage = true },
        };
        var c = ViewCenter();
        Canvas.SetLeft(image, Math.Clamp(c.X - w / 2, 0, Math.Max(0, p.PixelWidth - w)));
        Canvas.SetTop(image, Math.Clamp(c.Y - h / 2, 0, Math.Max(0, p.PixelHeight - h)));
        _anno.Children.Add(image);
        CommitNew(p, image);
        SetTool(Tool.Move);
        _selected = image;
        RedrawOverlay();
        SetStatus("Pasted. Drag to move it, or drag the corner handle to resize.");
    }
}
