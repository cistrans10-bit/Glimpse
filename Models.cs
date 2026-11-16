using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Glimpse;

public enum Tool { Move, TextSelect, RectSelect, Text, Rect, Ellipse, Line, Arrow, Pen, Highlight }

/// <summary>A PDF file held in memory, so the file on disk is never locked and can be saved over.</summary>
public sealed class PdfSource
{
    public required byte[] Bytes { get; init; }
    public required PdfDocument Doc { get; init; }
    public required IRandomAccessStream Stream { get; init; }
    public bool Encrypted { get; init; }
}

public sealed class OcrWord
{
    public required string Text { get; init; }
    public Rect Box { get; init; }
    public int Line { get; init; }
}

/// <summary>Markup data attached to each annotation element through its Tag.</summary>
public sealed class Anno
{
    public Tool Kind;
    public bool Resizable;
    public bool IsImage;
    public Rect? Local;   // bounds relative to the element's Canvas.Left/Top, for lines and ink
}

public sealed class DocumentPage
{
    public SoftwareBitmap? Bitmap;
    public PdfSource? Pdf;
    public int PdfIndex;
    public bool Baked;              // the bitmap no longer matches the PDF page (rotated, cropped...)
    public double Scale = 1;        // bitmap pixels per 1/96 inch
    public int PixelWidth, PixelHeight;
    public readonly List<FrameworkElement> Annotations = new();
    public List<OcrWord>? Words;
    public System.Threading.Tasks.Task? OcrTask;
    public ListViewItem? ThumbItem;
    public Image? ThumbImage;
    public TextBlock? ThumbLabel;
    public bool ThumbStale = true;

    public bool IsPdfBacked => Pdf != null && !Baked;
}

public sealed class History
{
    private readonly List<(Action undo, Action redo)> _undo = new(), _redo = new();
    public event Action? Changed;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Push(Action undo, Action redo)
    {
        _undo.Add((undo, redo));
        if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear();
        Changed?.Invoke();
    }
    public void Undo()
    {
        if (_undo.Count == 0) return;
        var a = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        a.undo(); _redo.Add(a); Changed?.Invoke();
    }
    public void Redo()
    {
        if (_redo.Count == 0) return;
        var a = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        a.redo(); _undo.Add(a); Changed?.Invoke();
    }
    public void Clear() { _undo.Clear(); _redo.Clear(); Changed?.Invoke(); }
}
