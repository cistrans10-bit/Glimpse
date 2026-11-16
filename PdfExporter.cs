using System;
using System.Collections.Generic;
using System.IO;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Glimpse;

/// <summary>
/// Writes the output PDF. Untouched PDF pages are copied as-is (text stays text); markup is drawn on top
/// as a transparent layer; pages that were rotated, cropped or came from images are written as pictures.
/// </summary>
public sealed class PdfExporter : IDisposable
{
    private readonly PdfDocument _out = new();
    private readonly Dictionary<byte[], PdfDocument?> _sources = new();

    private PdfDocument? Open(byte[] bytes)
    {
        if (_sources.TryGetValue(bytes, out var doc)) return doc;
        try { doc = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import); }
        catch { doc = null; }
        _sources[bytes] = doc;
        return doc;
    }

    public bool CanImport(byte[] source, int index)
    {
        var doc = Open(source);
        return doc != null && index >= 0 && index < doc.PageCount;
    }

    /// <summary>True when an overlay drawn at (0,0)-(width,height) lines up with what the viewer shows.</summary>
    public bool CanOverlay(byte[] source, int index)
    {
        if (!CanImport(source, index)) return false;
        try
        {
            var page = Open(source)!.Pages[index];
            if (page.Rotate % 360 != 0) return false;
            var media = page.MediaBox;
            if (Math.Abs(media.X1) > 0.01 || Math.Abs(media.Y1) > 0.01) return false;
            if (page.Elements.ContainsKey("/CropBox"))
            {
                var crop = page.CropBox;
                if (Math.Abs(crop.X1 - media.X1) > 0.5 || Math.Abs(crop.Y1 - media.Y1) > 0.5 ||
                    Math.Abs(crop.X2 - media.X2) > 0.5 || Math.Abs(crop.Y2 - media.Y2) > 0.5) return false;
            }
            return true;
        }
        catch { return false; }
    }

    public void AddSourcePage(byte[] source, int index, byte[]? overlayPng)
    {
        var page = _out.AddPage(Open(source)!.Pages[index]);
        if (overlayPng == null) return;
        using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        using var image = XImage.FromStream(new MemoryStream(overlayPng));
        gfx.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
    }

    public void AddImagePage(byte[] image, double widthPoints, double heightPoints)
    {
        var page = _out.AddPage();
        page.Width = XUnit.FromPoint(widthPoints);
        page.Height = XUnit.FromPoint(heightPoints);
        using var gfx = XGraphics.FromPdfPage(page);
        using var ximage = XImage.FromStream(new MemoryStream(image));
        gfx.DrawImage(ximage, 0, 0, widthPoints, heightPoints);
    }

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        _out.Save(ms, false);
        return ms.ToArray();
    }

    public void Dispose()
    {
        _out.Dispose();
        foreach (var doc in _sources.Values) doc?.Dispose();
    }
}
