using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Glimpse;

public sealed partial class MainWindow
{
    private const double PdfRenderScale = 2.0;
    private const int MaxRenderDimension = 4096;

    // ------------------------------------------------------------------ opening

    private async Task OpenAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeFilter.Add(".pdf");
        foreach (var ext in Imaging.ImageExtensions) picker.FileTypeFilter.Add(ext);
        var files = await picker.PickMultipleFilesAsync();
        if (files == null || files.Count == 0) return;
        await OpenPathsAsync(files.Select(f => f.Path).ToList(), false);
    }

    private async Task InsertAsync()
    {
        if (_pages.Count == 0) { await OpenAsync(); return; }
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeFilter.Add(".pdf");
        foreach (var ext in Imaging.ImageExtensions) picker.FileTypeFilter.Add(ext);
        var files = await picker.PickMultipleFilesAsync();
        if (files == null || files.Count == 0) return;
        await OpenPathsAsync(files.Select(f => f.Path).ToList(), true);
    }

    private async Task OpenPathsAsync(IReadOnlyList<string> paths, bool insert)
    {
        CommitEdit();
        if (paths.Count == 0) return;
        if (!insert && !await ConfirmDiscardAsync()) return;

        SetStatus("Opening…");
        var loaded = new List<DocumentPage>();
        var failed = new List<string>();
        foreach (var path in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Imaging.IsPdf(path)) loaded.AddRange(await LoadPdfAsync(path));
                else
                    foreach (var bmp in await Imaging.LoadImageAsync(path)) loaded.Add(PageFromBitmap(bmp));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { failed.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }
        if (failed.Count > 0) await MessageAsync("Some files could not be opened", string.Join("\n", failed));
        if (loaded.Count == 0) { SetStatus(""); return; }

        if (insert && _pages.Count > 0)
        {
            int at = _current + 1;
            Action redo = () => { _pages.InsertRange(at, loaded); PagesChanged(at); };
            Action undo = () => { foreach (var p in loaded) _pages.Remove(p); PagesChanged(Math.Max(0, at - 1)); };
            redo();
            _history.Push(undo, redo);
            MarkDirty();
            SetStatus($"Inserted {loaded.Count} page{(loaded.Count == 1 ? "" : "s")}");
        }
        else
        {
            _pages.Clear();
            _pages.AddRange(loaded);
            _history.Clear();
            _docPath = paths.Count == 1 ? paths[0] : null;
            _docName = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} files";
            _dirty = false;
            _fit = true;
            _current = -1;
            RebuildThumbs();
            await ShowPageAsync(0);
            SetStatus("");
        }
        _ = LoadThumbsAsync();
    }

    private static DocumentPage PageFromBitmap(SoftwareBitmap bmp, double scale = 1) => new()
    {
        Bitmap = bmp, PixelWidth = bmp.PixelWidth, PixelHeight = bmp.PixelHeight, Scale = scale, Baked = true,
    };

    private async Task<List<DocumentPage>> LoadPdfAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var stream = await Imaging.ToStreamAsync(bytes);
        PdfDocument doc;
        bool encrypted = false;
        try { doc = await PdfDocument.LoadFromStreamAsync(stream); }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x8007052B))
        {
            encrypted = true;
            while (true)
            {
                var box = new PasswordBox { PlaceholderText = "Password" };
                var r = await DialogAsync($"“{Path.GetFileName(path)}” is password protected", box, "Open", null, "Cancel");
                if (r != ContentDialogResult.Primary) throw new OperationCanceledException();
                try { stream.Seek(0); doc = await PdfDocument.LoadFromStreamAsync(stream, box.Password); break; }
                catch (Exception ex2) when (ex2.HResult == unchecked((int)0x8007052B)) { }
            }
        }

        var source = new PdfSource { Bytes = bytes, Doc = doc, Stream = stream, Encrypted = encrypted };
        var pages = new List<DocumentPage>();
        for (int i = 0; i < doc.PageCount; i++)
        {
            Size size;
            using (var pg = doc.GetPage((uint)i)) size = pg.Size;
            double scale = PdfRenderScale;
            double longest = Math.Max(size.Width, size.Height);
            if (longest * scale > MaxRenderDimension) scale = MaxRenderDimension / longest;
            pages.Add(new DocumentPage
            {
                Pdf = source, PdfIndex = i, Scale = scale,
                PixelWidth = Math.Max(1, (int)Math.Round(size.Width * scale)),
                PixelHeight = Math.Max(1, (int)Math.Round(size.Height * scale)),
            });
        }
        return pages;
    }

    private static async Task EnsureBitmapAsync(DocumentPage p)
    {
        if (p.Bitmap == null && p.Pdf != null)
            p.Bitmap = await Imaging.RenderPdfPageAsync(p.Pdf.Doc, p.PdfIndex, p.PixelWidth, p.PixelHeight);
    }

    // ------------------------------------------------------------------ showing pages

    private async Task ShowPageAsync(int index)
    {
        CommitEdit();
        if (_pages.Count == 0)
        {
            _current = -1;
            _image.Source = null;
            _anno.Children.Clear();
            UpdateUiState();
            return;
        }
        index = Math.Clamp(index, 0, _pages.Count - 1);
        int version = ++_showVersion;
        bool pageChanged = index != _current;
        _current = index;
        var p = _pages[index];
        await EnsureBitmapAsync(p);
        if (version != _showVersion) return;
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(p.Bitmap);
        if (version != _showVersion) return;

        _image.Source = source;
        _surface.Width = p.PixelWidth; _surface.Height = p.PixelHeight;
        _anno.Width = p.PixelWidth; _anno.Height = p.PixelHeight;
        _anno.Children.Clear();
        foreach (var el in p.Annotations)
        {
            if (el.Parent is Panel old) old.Children.Remove(el);
            _anno.Children.Add(el);
        }
        if (pageChanged) { _selA = _selB = -1; _marquee = null; _selected = null; }
        else if (_selected != null && !p.Annotations.Contains(_selected)) _selected = null;
        RedrawOverlay();
        if (_thumbs.SelectedIndex != index) _thumbs.SelectedIndex = index;
        if (pageChanged && p.ThumbItem != null) _thumbs.ScrollIntoView(p.ThumbItem);
        UpdateUiState();
        if (_fit) QueueFit();

        // Keep memory flat on long PDFs: only the page on screen stays rendered.
        foreach (var other in _pages)
            if (other != p && other.IsPdfBacked) other.Bitmap = null;

        if (_tool == Tool.TextSelect) Run(() => EnsureOcrAsync(p), quiet: true);
    }

    private void GoRelative(int delta)
    {
        if (_pages.Count == 0) return;
        int i = Math.Clamp(_current + delta, 0, _pages.Count - 1);
        if (i != _current) Run(() => ShowPageAsync(i));
    }

    /// <summary>Re-displays a page after an undo/redo or edit touched it.</summary>
    private void Refresh(DocumentPage p)
    {
        int i = _pages.IndexOf(p);
        if (i >= 0) Run(() => ShowPageAsync(i), quiet: true);
    }

    private void PagesChanged(int show)
    {
        RebuildThumbs();
        _current = -1;
        Run(() => ShowPageAsync(show), quiet: true);
        _ = LoadThumbsAsync();
    }

    // ------------------------------------------------------------------ thumbnails

    private void RebuildThumbs()
    {
        _thumbs.Items.Clear();
        for (int i = 0; i < _pages.Count; i++)
        {
            var p = _pages[i];
            if (p.ThumbItem == null)
            {
                p.ThumbImage = new Image { Stretch = Stretch.Uniform, MaxHeight = 190 };
                p.ThumbLabel = new TextBlock { FontSize = 11, Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Center };
                var frame = new Border
                {
                    Background = new SolidColorBrush(Colors.White), Child = p.ThumbImage, Width = 132,
                    MinHeight = 40, BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(60, 128, 128, 128)),
                };
                var panel = new StackPanel { Spacing = 4, Padding = new Thickness(0, 8, 0, 6) };
                panel.Children.Add(frame);
                panel.Children.Add(p.ThumbLabel);
                p.ThumbItem = new ListViewItem { Content = panel, HorizontalContentAlignment = HorizontalAlignment.Center };

                var menu = new MenuFlyout();
                menu.Items.Add(Item("Rotate left", () => OnPage(p, () => RotateAsync(false))));
                menu.Items.Add(Item("Rotate right", () => OnPage(p, () => RotateAsync(true))));
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(Item("Move up", () => MovePage(p, -1)));
                menu.Items.Add(Item("Move down", () => MovePage(p, 1)));
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(Item("Export as image…", () => OnPage(p, ExportPageImageAsync)));
                menu.Items.Add(Item("Delete page", () => DeletePage(p), ""));
                p.ThumbItem.ContextFlyout = menu;
            }
            p.ThumbLabel!.Text = (i + 1).ToString();
            _thumbs.Items.Add(p.ThumbItem);
        }
        if (_current >= 0 && _current < _pages.Count) _thumbs.SelectedIndex = _current;
    }

    private void OnPage(DocumentPage p, Func<Task> action) => Run(async () =>
    {
        int i = _pages.IndexOf(p);
        if (i < 0) return;
        if (i != _current) await ShowPageAsync(i);
        await action();
    });

    private async Task LoadThumbsAsync()
    {
        int version = ++_thumbVersion;
        foreach (var p in _pages.ToList())
        {
            if (version != _thumbVersion) return;
            if (!p.ThumbStale || p.ThumbImage == null) continue;
            try { await UpdateThumbAsync(p); } catch { }
        }
    }

    private static async Task UpdateThumbAsync(DocumentPage p)
    {
        if (p.ThumbImage == null) return;
        p.ThumbStale = false;
        const int width = 264;
        if (p.IsPdfBacked)
        {
            using var page = p.Pdf!.Doc.GetPage((uint)p.PdfIndex);
            var ras = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(ras, new PdfPageRenderOptions { DestinationWidth = width, BackgroundColor = Colors.White });
            ras.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(ras);
            p.ThumbImage.Source = image;
        }
        else if (p.Bitmap is { } bmp)
        {
            var small = bmp;
            if (bmp.PixelWidth > width)
            {
                uint h = (uint)Math.Max(1, bmp.PixelHeight * (double)width / bmp.PixelWidth);
                small = await Imaging.TransformAsync(bmp, t => { t.ScaledWidth = width; t.ScaledHeight = h; t.InterpolationMode = BitmapInterpolationMode.Fant; });
            }
            var src = new SoftwareBitmapSource();
            await src.SetBitmapAsync(small);
            p.ThumbImage.Source = src;
        }
    }

    private void StaleThumb(DocumentPage p)
    {
        p.ThumbStale = true;
        _ = LoadThumbsAsync();
    }

    // ------------------------------------------------------------------ page operations

    private void DeleteCurrentPage() { if (Cur is { } p) DeletePage(p); }

    private void DeletePage(DocumentPage p)
    {
        CommitEdit();
        int at = _pages.IndexOf(p);
        if (at < 0) return;
        if (_pages.Count == 1) { SetStatus("A document needs at least one page."); return; }
        Action redo = () => { _pages.Remove(p); PagesChanged(Math.Min(at, _pages.Count - 1)); };
        Action undo = () => { _pages.Insert(Math.Min(at, _pages.Count), p); PagesChanged(at); };
        redo();
        _history.Push(undo, redo);
        MarkDirty();
    }

    private void MovePage(DocumentPage p, int delta)
    {
        CommitEdit();
        int from = _pages.IndexOf(p), to = from + delta;
        if (from < 0 || to < 0 || to >= _pages.Count) return;
        Action redo = () => { _pages.Remove(p); _pages.Insert(Math.Min(to, _pages.Count), p); PagesChanged(to); };
        Action undo = () => { _pages.Remove(p); _pages.Insert(Math.Min(from, _pages.Count), p); PagesChanged(from); };
        redo();
        _history.Push(undo, redo);
        MarkDirty();
    }

    /// <summary>Renders the page on screen, with its markup, to a bitmap.</summary>
    private async Task<SoftwareBitmap> FlattenCurrentAsync()
    {
        var p = Cur!;
        CommitEdit();
        _overlay.Visibility = Visibility.Collapsed;
        _editLayer.Visibility = Visibility.Collapsed;
        try
        {
            _surface.UpdateLayout();
            return await Imaging.RenderElementAsync(_surface, p.PixelWidth, p.PixelHeight);
        }
        finally
        {
            _overlay.Visibility = Visibility.Visible;
            _editLayer.Visibility = Visibility.Visible;
        }
    }

    /// <summary>The current page as a bitmap, including markup when there is any.</summary>
    private async Task<SoftwareBitmap> CurrentPixelsAsync(bool forceOpaque = false)
    {
        var p = Cur!;
        await EnsureBitmapAsync(p);
        return p.Annotations.Count > 0 || forceOpaque ? await FlattenCurrentAsync() : p.Bitmap!;
    }

    /// <summary>Replaces the current page's picture (rotate, flip, crop). Markup is merged into the picture first.</summary>
    private async Task ReplaceBitmapAsync(Action<BitmapTransform> transform)
    {
        var p = Cur;
        if (p == null) return;
        var source = await CurrentPixelsAsync();
        var result = await Imaging.TransformAsync(source, transform);

        var oldBitmap = p.IsPdfBacked ? null : p.Bitmap;
        var oldAnnos = p.Annotations.ToList();
        bool oldBaked = p.Baked;
        int oldW = p.PixelWidth, oldH = p.PixelHeight;

        Action redo = () =>
        {
            p.Bitmap = result; p.Baked = true; p.Annotations.Clear();
            p.PixelWidth = result.PixelWidth; p.PixelHeight = result.PixelHeight;
            p.Words = null; p.OcrTask = null;
            StaleThumb(p); Refresh(p);
        };
        Action undo = () =>
        {
            p.Bitmap = oldBitmap; p.Baked = oldBaked; p.Annotations.Clear(); p.Annotations.AddRange(oldAnnos);
            p.PixelWidth = oldW; p.PixelHeight = oldH;
            p.Words = null; p.OcrTask = null;
            StaleThumb(p); Refresh(p);
        };
        _marquee = null; _selected = null; _selA = _selB = -1;
        redo();
        _history.Push(undo, redo);
        MarkDirty();
    }

    private Task RotateAsync(bool clockwise) => ReplaceBitmapAsync(t =>
        t.Rotation = clockwise ? BitmapRotation.Clockwise90Degrees : BitmapRotation.Clockwise270Degrees);

    private Task FlipAsync(bool horizontal) => ReplaceBitmapAsync(t =>
        t.Flip = horizontal ? BitmapFlip.Horizontal : BitmapFlip.Vertical);

    private async Task CropAsync()
    {
        var p = Cur;
        if (p == null) return;
        if (_marquee is not Rect m)
        {
            SetTool(Tool.RectSelect);
            SetStatus("Drag a rectangle on the page, then choose Crop again.");
            return;
        }
        var b = ToBounds(m, p);
        if (b.Width < 2 || b.Height < 2) return;
        await ReplaceBitmapAsync(t => t.Bounds = b);
    }

    private static BitmapBounds ToBounds(Rect r, DocumentPage p)
    {
        int x = (int)Math.Clamp(Math.Round(r.X), 0, p.PixelWidth - 1), y = (int)Math.Clamp(Math.Round(r.Y), 0, p.PixelHeight - 1);
        int w = (int)Math.Clamp(Math.Round(r.Width), 1, p.PixelWidth - x), h = (int)Math.Clamp(Math.Round(r.Height), 1, p.PixelHeight - y);
        return new BitmapBounds { X = (uint)x, Y = (uint)y, Width = (uint)w, Height = (uint)h };
    }

    // ------------------------------------------------------------------ OCR

    private async Task EnsureOcrAsync(DocumentPage p)
    {
        if (p.Words != null) return;
        if (Imaging.Ocr == null)
        {
            p.Words = new();
            SetStatus("Text recognition is unavailable: add a language with OCR support in Windows Settings › Time & language.");
            return;
        }
        if (p.OcrTask == null)
        {
            p.OcrTask = RecognizeAsync(p);
        }
        await p.OcrTask;
        if (p == Cur) RedrawOverlay();
    }

    private async Task RecognizeAsync(DocumentPage p)
    {
        SetStatus("Recognising text…");
        try
        {
            await EnsureBitmapAsync(p);
            var bmp = p.Bitmap!;
            p.Words = await Imaging.RecognizeAsync(bmp);
            SetStatus(p.Words.Count == 0 ? "No text found on this page." : $"{p.Words.Count} words recognised — drag to select, Ctrl+C to copy.");
        }
        catch (Exception ex)
        {
            p.Words = new();
            SetStatus("Text recognition failed: " + ex.Message);
        }
    }

    private static string JoinWords(IEnumerable<OcrWord> words)
    {
        var sb = new StringBuilder();
        int line = -1;
        foreach (var w in words)
        {
            if (line != -1) sb.Append(w.Line != line ? Environment.NewLine : " ");
            sb.Append(w.Text);
            line = w.Line;
        }
        return sb.ToString();
    }

    private async Task SelectAllTextAsync()
    {
        var p = Cur;
        if (p == null) return;
        if (_tool != Tool.TextSelect) SetTool(Tool.TextSelect);
        await EnsureOcrAsync(p);
        if (p.Words is { Count: > 0 }) { _selA = 0; _selB = p.Words.Count - 1; }
        RedrawOverlay();
    }

    private async Task CopyAllTextAsync()
    {
        var p = Cur;
        if (p == null) return;
        await EnsureOcrAsync(p);
        if (p.Words is { Count: > 0 }) { CopyText(JoinWords(p.Words)); SetStatus("Copied all text on this page."); }
        else SetStatus("No text found on this page.");
    }

    private async Task CopyTextInMarqueeAsync()
    {
        var p = Cur;
        if (p == null || _marquee is not Rect m) return;
        await EnsureOcrAsync(p);
        var inside = p.Words!.Where(w => m.Contains(new Point(w.Box.X + w.Box.Width / 2, w.Box.Y + w.Box.Height / 2))).ToList();
        if (inside.Count == 0) { SetStatus("No text found in the selection."); return; }
        CopyText(JoinWords(inside));
        SetStatus($"Copied {inside.Count} words from the selection.");
    }

    // ------------------------------------------------------------------ clipboard

    private static void CopyText(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
        try { Clipboard.Flush(); } catch { }
    }

    private static async Task CopyBitmapAsync(SoftwareBitmap bmp, Action<BitmapTransform>? transform = null)
    {
        var png = await Imaging.EncodeAsync(new[] { bmp }, BitmapEncoder.PngEncoderId, transform);
        var stream = await Imaging.ToStreamAsync(png);
        var data = new DataPackage();
        data.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
        Clipboard.SetContent(data);
        try { Clipboard.Flush(); } catch { }
    }

    private async Task CopyAsync()
    {
        var p = Cur;
        if (p == null) return;
        if (_selA >= 0 && _selB >= 0 && p.Words != null)
        {
            int lo = Math.Min(_selA, _selB), hi = Math.Max(_selA, _selB);
            CopyText(JoinWords(p.Words.Skip(lo).Take(hi - lo + 1)));
            SetStatus("Copied text.");
        }
        else if (_marquee is Rect m)
        {
            var b = ToBounds(m, p);
            await CopyBitmapAsync(await CurrentPixelsAsync(), t => t.Bounds = b);
            SetStatus("Copied the selected area as an image.");
        }
        else
        {
            await CopyBitmapAsync(await CurrentPixelsAsync());
            SetStatus("Copied the page as an image.");
        }
    }

    private async Task PasteAsync()
    {
        CommitEdit();
        var content = Clipboard.GetContent();
        if (content.Contains(StandardDataFormats.Bitmap))
        {
            var reference = await content.GetBitmapAsync();
            using var stream = await reference.OpenReadAsync();
            var bmp = (await Imaging.DecodeAsync(stream, false))[0];
            if (_pages.Count == 0) await NewFromBitmapAsync(bmp);
            else await AddImageAnnotationAsync(bmp);
        }
        else if (content.Contains(StandardDataFormats.StorageItems))
        {
            var items = await content.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(x => !string.IsNullOrEmpty(x) && (Imaging.IsImage(x) || Imaging.IsPdf(x))).ToList();
            if (paths.Count == 0) return;
            if (_pages.Count == 0) await OpenPathsAsync(paths, false);
            else if (paths.Count == 1 && Imaging.IsImage(paths[0])) await AddImageAnnotationAsync((await Imaging.LoadImageAsync(paths[0]))[0]);
            else await OpenPathsAsync(paths, true);
        }
        else if (content.Contains(StandardDataFormats.Text))
        {
            var text = (await content.GetTextAsync()).Trim();
            if (text.Length > 0 && Cur != null) AddTextAnnotation(text);
        }
    }

    private async Task NewFromBitmapAsync(SoftwareBitmap bmp)
    {
        _pages.Clear();
        _pages.Add(PageFromBitmap(bmp));
        _history.Clear();
        _docPath = null;
        _docName = "Untitled";
        _dirty = true;
        _fit = true;
        _current = -1;
        RebuildThumbs();
        await ShowPageAsync(0);
        _ = LoadThumbsAsync();
    }

    // ------------------------------------------------------------------ saving

    private static readonly string[] WritableImages = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

    /// <summary>Saves the document. Returns false when the user cancelled.</summary>
    private async Task<bool> SaveAsync(bool saveAs)
    {
        CommitEdit();
        if (_pages.Count == 0) return false;
        string? path = _docPath;
        string ext = path == null ? "" : Path.GetExtension(path).ToLowerInvariant();
        bool multiOk = ext is ".pdf" or ".tif" or ".tiff";
        bool inPlace = !saveAs && path != null && (ext == ".pdf" || WritableImages.Contains(ext)) && (_pages.Count == 1 || multiOk);

        if (!inPlace)
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
            bool pdfFirst = _pages.Count > 1 || ext == ".pdf" || _pages.Any(p => p.Pdf != null);
            if (pdfFirst) picker.FileTypeChoices.Add("PDF document", new List<string> { ".pdf" });
            picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
            picker.FileTypeChoices.Add("JPEG image", new List<string> { ".jpg" });
            picker.FileTypeChoices.Add("TIFF image", new List<string> { ".tif" });
            picker.FileTypeChoices.Add("Bitmap image", new List<string> { ".bmp" });
            if (!pdfFirst) picker.FileTypeChoices.Add("PDF document", new List<string> { ".pdf" });
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(_docName is "" ? "Untitled" : _docName);
            var file = await picker.PickSaveFileAsync();
            if (file == null) return false;
            path = file.Path;
            ext = Path.GetExtension(path).ToLowerInvariant();
        }

        SetStatus("Saving…");
        byte[] bytes;
        bool wholeDocument = true;
        if (ext == ".pdf") bytes = await BuildPdfAsync();
        else if (ext is ".tif" or ".tiff") bytes = await BuildImageAsync(ext, _pages.ToList());
        else
        {
            wholeDocument = _pages.Count == 1;
            bytes = await BuildImageAsync(ext, new List<DocumentPage> { Cur! });
        }
        await File.WriteAllBytesAsync(path!, bytes);

        if (wholeDocument)
        {
            _docPath = path;
            _docName = Path.GetFileName(path!);
            _dirty = false;
            SetStatus($"Saved {_docName}");
        }
        else SetStatus($"Saved page {_current + 1} to {Path.GetFileName(path!)} (this format holds one page; use PDF or TIFF for all pages).");
        UpdateUiState();
        return true;
    }

    private async Task ExportPageImageAsync()
    {
        if (Cur == null) return;
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
        picker.FileTypeChoices.Add("JPEG image", new List<string> { ".jpg" });
        picker.FileTypeChoices.Add("TIFF image", new List<string> { ".tif" });
        picker.FileTypeChoices.Add("Bitmap image", new List<string> { ".bmp" });
        picker.SuggestedFileName = $"{Path.GetFileNameWithoutExtension(_docName)} page {_current + 1}";
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        var bytes = await BuildImageAsync(Path.GetExtension(file.Path), new List<DocumentPage> { Cur });
        await File.WriteAllBytesAsync(file.Path, bytes);
        SetStatus($"Exported page {_current + 1} to {file.Name}");
    }

    private async Task<byte[]> BuildImageAsync(string ext, List<DocumentPage> pages)
    {
        var encoder = Imaging.EncoderFor(ext);
        bool opaque = encoder != BitmapEncoder.PngEncoderId && encoder != BitmapEncoder.TiffEncoderId;
        int remember = _current;
        var bitmaps = new List<SoftwareBitmap>();
        foreach (var p in pages)
        {
            int i = _pages.IndexOf(p);
            if (p.Annotations.Count > 0 || opaque)
            {
                if (i != _current) await ShowPageAsync(i);
                bitmaps.Add(await FlattenCurrentAsync());
            }
            else
            {
                await EnsureBitmapAsync(p);
                bitmaps.Add(p.Bitmap!);
            }
        }
        var bytes = await Imaging.EncodeAsync(bitmaps, encoder);
        if (_current != remember) await ShowPageAsync(remember);
        return bytes;
    }

    private async Task<byte[]> BuildPdfAsync()
    {
        using var exporter = new PdfExporter();
        int remember = _current;
        for (int i = 0; i < _pages.Count; i++)
        {
            var p = _pages[i];
            bool marked = p.Annotations.Count > 0;
            bool import = p.IsPdfBacked && !p.Pdf!.Encrypted && exporter.CanImport(p.Pdf.Bytes, p.PdfIndex);
            if (import && !marked)
            {
                exporter.AddSourcePage(p.Pdf!.Bytes, p.PdfIndex, null);
            }
            else if (import && exporter.CanOverlay(p.Pdf!.Bytes, p.PdfIndex))
            {
                if (i != _current) await ShowPageAsync(i);
                CommitEdit();
                _anno.UpdateLayout();
                var layer = await Imaging.RenderElementAsync(_anno, p.PixelWidth, p.PixelHeight);
                exporter.AddSourcePage(p.Pdf.Bytes, p.PdfIndex, await Imaging.EncodeAsync(layer, BitmapEncoder.PngEncoderId));
            }
            else
            {
                if (i != _current) await ShowPageAsync(i);
                var flat = await FlattenCurrentAsync();
                var jpeg = await Imaging.EncodeAsync(flat, BitmapEncoder.JpegEncoderId);
                exporter.AddImagePage(jpeg, p.PixelWidth / p.Scale * 72 / 96, p.PixelHeight / p.Scale * 72 / 96);
            }
            if (_pages.Count > 20) SetStatus($"Saving… page {i + 1} of {_pages.Count}");
        }
        if (_current != remember) await ShowPageAsync(remember);
        var exp = exporter;
        return await Task.Run(exp.ToBytes);
    }
}
