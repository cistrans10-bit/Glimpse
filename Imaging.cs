using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Glimpse;

public static class Imaging
{
    public static readonly string[] ImageExtensions =
        { ".png", ".jpg", ".jpeg", ".jpe", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif", ".ico", ".jxr", ".wdp" };

    public static bool IsImage(string path) => Array.IndexOf(ImageExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;
    public static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public static async Task<IRandomAccessStream> ToStreamAsync(byte[] bytes)
    {
        var ras = new InMemoryRandomAccessStream();
        await ras.WriteAsync(bytes.AsBuffer());
        ras.Seek(0);
        return ras;
    }

    public static async Task<byte[]> ToBytesAsync(IRandomAccessStream ras)
    {
        ras.Seek(0);
        var bytes = new byte[ras.Size];
        using var reader = new DataReader(ras.GetInputStreamAt(0));
        await reader.LoadAsync((uint)ras.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>Decodes every frame of an image (multi-page TIFFs become several pages).</summary>
    public static async Task<List<SoftwareBitmap>> DecodeAsync(IRandomAccessStream stream, bool allFrames = true)
    {
        var result = new List<SoftwareBitmap>();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        bool animated = decoder.DecoderInformation?.CodecId == BitmapDecoder.GifDecoderId;
        uint frames = allFrames && !animated ? decoder.FrameCount : 1;
        for (uint i = 0; i < frames; i++)
        {
            var frame = await decoder.GetFrameAsync(i);
            result.Add(await frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb));
        }
        return result;
    }

    public static async Task<List<SoftwareBitmap>> LoadImageAsync(string path)
    {
        using var ras = await ToStreamAsync(await File.ReadAllBytesAsync(path));
        return await DecodeAsync(ras);
    }

    public static async Task<SoftwareBitmap> RenderPdfPageAsync(PdfDocument doc, int index, int width, int height)
    {
        using var page = doc.GetPage((uint)index);
        using var ras = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(ras, new PdfPageRenderOptions
        {
            DestinationWidth = (uint)width,
            DestinationHeight = (uint)height,
            BackgroundColor = Microsoft.UI.Colors.White,
        });
        ras.Seek(0);
        return (await DecodeAsync(ras, false))[0];
    }

    public static Guid EncoderFor(string ext) => ext.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jpe" => BitmapEncoder.JpegEncoderId,
        ".bmp" => BitmapEncoder.BmpEncoderId,
        ".tif" or ".tiff" => BitmapEncoder.TiffEncoderId,
        ".gif" => BitmapEncoder.GifEncoderId,
        _ => BitmapEncoder.PngEncoderId,
    };

    /// <summary>Encodes one or more bitmaps (several only for TIFF) with an optional transform.</summary>
    public static async Task<byte[]> EncodeAsync(IReadOnlyList<SoftwareBitmap> bitmaps, Guid encoderId, Action<BitmapTransform>? transform = null)
    {
        using var ras = new InMemoryRandomAccessStream();
        BitmapEncoder encoder;
        if (encoderId == BitmapEncoder.JpegEncoderId)
        {
            var props = new BitmapPropertySet { { "ImageQuality", new BitmapTypedValue(0.92f, PropertyType.Single) } };
            encoder = await BitmapEncoder.CreateAsync(encoderId, ras, props);
        }
        else encoder = await BitmapEncoder.CreateAsync(encoderId, ras);

        for (int i = 0; i < bitmaps.Count; i++)
        {
            if (i > 0) await encoder.GoToNextFrameAsync();
            encoder.SetSoftwareBitmap(bitmaps[i]);
            transform?.Invoke(encoder.BitmapTransform);
        }
        await encoder.FlushAsync();
        return await ToBytesAsync(ras);
    }

    public static Task<byte[]> EncodeAsync(SoftwareBitmap bitmap, Guid encoderId) => EncodeAsync(new[] { bitmap }, encoderId);

    public static async Task<SoftwareBitmap> TransformAsync(SoftwareBitmap source, Action<BitmapTransform> transform)
    {
        // PNG keeps transparency intact through the round trip.
        var bytes = await EncodeAsync(new[] { source }, BitmapEncoder.PngEncoderId, transform);
        using var ras = await ToStreamAsync(bytes);
        var decoder = await BitmapDecoder.CreateAsync(ras);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    /// <summary>Renders a live element to a bitmap of exactly the given pixel size.</summary>
    public static async Task<SoftwareBitmap> RenderElementAsync(UIElement element, int width, int height)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(element, width, height);
        var pixels = await rtb.GetPixelsAsync();
        return SoftwareBitmap.CreateCopyFromBuffer(pixels, BitmapPixelFormat.Bgra8, rtb.PixelWidth, rtb.PixelHeight, BitmapAlphaMode.Premultiplied);
    }

    private static OcrEngine? _ocr;
    private static bool _ocrTried;

    public static OcrEngine? Ocr
    {
        get
        {
            if (_ocrTried) return _ocr;
            _ocrTried = true;
            _ocr = OcrEngine.TryCreateFromUserProfileLanguages();
            if (_ocr == null)
                foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
                    if ((_ocr = OcrEngine.TryCreateFromLanguage(lang)) != null) break;
            return _ocr;
        }
    }

    /// <summary>Runs Windows' built-in OCR and returns word boxes in the bitmap's pixel coordinates.</summary>
    public static async Task<List<OcrWord>> RecognizeAsync(SoftwareBitmap bitmap)
    {
        var words = new List<OcrWord>();
        var engine = Ocr;
        if (engine == null) return words;

        double factor = 1;
        var input = bitmap;
        int max = (int)OcrEngine.MaxImageDimension;
        int longest = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
        if (longest > max)
        {
            factor = (double)max / longest;
            uint w = (uint)Math.Max(1, Math.Floor(bitmap.PixelWidth * factor)), h = (uint)Math.Max(1, Math.Floor(bitmap.PixelHeight * factor));
            input = await TransformAsync(bitmap, t => { t.ScaledWidth = w; t.ScaledHeight = h; t.InterpolationMode = BitmapInterpolationMode.Fant; });
        }

        var result = await engine.RecognizeAsync(input);
        int line = 0;
        foreach (var l in result.Lines)
        {
            foreach (var w in l.Words)
            {
                var r = w.BoundingRect;
                words.Add(new OcrWord { Text = w.Text, Line = line, Box = new Rect(r.X / factor, r.Y / factor, r.Width / factor, r.Height / factor) });
            }
            line++;
        }
        return words;
    }
}
