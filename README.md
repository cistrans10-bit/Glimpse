# Glimpse

A macOS Preview-style viewer and markup tool for Windows 10/11, written in C# with WinUI 3 (Windows App SDK).

## Build and run

1. Install the .NET SDK, version 8 or newer (https://dot.net) if you don't have it.
2. Double-click `build.bat`. It builds into the `dist` folder and starts `dist\Glimpse.exe`.

The `dist` folder is self-contained: copy it to any Windows 10 (1809+) or Windows 11 PC and it runs with nothing else installed.
To open the project in Visual Studio 2022 instead, open `Glimpse.csproj` (needs the "Windows application development" workload).

## Features

- Opens PDFs (including password-protected ones) and PNG, JPEG, BMP, GIF, TIFF, WebP, HEIC, ICO images. Several files open as one multi-page document. Drag and drop, "Open with", and paste from the clipboard all work.
- **Text selection with OCR**: drag across any page or picture to select text, double-click a word, Ctrl+A for everything, Ctrl+C to copy. The rectangular selection tool can copy an area as an image or copy only the text inside it.
- **Markup**: text boxes, rectangles, ellipses, lines, arrows, freehand pen, highlighter; colours, fill, thickness, text size. Move, resize, restyle and delete markup; full undo and redo.
- **Copy and paste**: paste images or text onto a page as movable objects, copy a page or selection to other apps.
- **Pages**: thumbnail sidebar, rotate, flip, crop, move up/down, delete, insert pages from other files.
- **Save and export**: PDF (untouched pages stay real PDF pages with markup layered on top), PNG, JPEG, TIFF (multi-page), BMP.
- **Appearance**: light, dark, or follow Windows, with the Mica backdrop on Windows 11.

## Shortcuts

| Keys | Action |
| --- | --- |
| Ctrl+O / Ctrl+S / Ctrl+Shift+S | Open / Save / Save As |
| Ctrl+C / Ctrl+V / Ctrl+A | Copy / Paste / Select all text |
| Ctrl+Z / Ctrl+Y | Undo / Redo |
| Ctrl++ / Ctrl+- / Ctrl+0 / Ctrl+9 | Zoom in / out / actual size / fit |
| Ctrl+R / Ctrl+L / Ctrl+K | Rotate right / left / crop to selection |
| Page Up / Page Down | Previous / next page |
| Delete, Esc | Delete selected markup, clear selection |

## Notes

- Text is recognised with the OCR engine built into Windows, using your Windows display languages. If recognition is unavailable, add a language with OCR support in Settings > Time & language.
- Rotating, flipping or cropping a page merges its markup into the picture, and that page is saved to PDF as an image.

## Code layout

| File | Contents |
| --- | --- |
| `App.cs` | Entry point. The UI is built in C# with no `.xaml` files. |
| `MainWindow.cs` | Window, toolbars, theme, zoom, dialogs |
| `MainWindow.Document.cs` | Open, pages, thumbnails, OCR, clipboard, save |
| `MainWindow.Tools.cs` | Mouse handling, text selection, markup |
| `Imaging.cs` | Decoding, encoding, PDF page rendering, OCR |
| `PdfExporter.cs` | PDF writing (PDFsharp) |
