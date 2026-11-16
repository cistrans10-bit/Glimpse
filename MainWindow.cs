using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Glimpse;

/// <summary>The page drawing surface. A subclass is needed only to be allowed to change the mouse cursor.</summary>
public sealed class PageSurface : Grid
{
    public void SetCursor(InputSystemCursorShape shape) => ProtectedCursor = InputSystemCursor.Create(shape);
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "Default";
    public bool Sidebar { get; set; } = true;
    public bool MarkupBar { get; set; } = true;
}

public sealed partial class MainWindow : Window
{
    private const string AppName = "Glimpse";
    private static readonly Color Accent = Color.FromArgb(255, 10, 132, 255);

    // ---- document state ----
    private readonly List<DocumentPage> _pages = new();
    private readonly History _history = new();
    private int _current = -1;
    private string? _docPath;
    private string _docName = "";
    private bool _dirty;
    private bool _closing;
    private bool _fit = true;
    private bool _dialogOpen;
    private int _showVersion, _thumbVersion;
    private AppSettings _settings = new();

    // ---- tool state ----
    private Tool _tool = Tool.TextSelect;
    private Color _stroke = Color.FromArgb(255, 255, 59, 48);
    private Color _highlight = Color.FromArgb(255, 255, 214, 10);
    private Color? _fill;
    private double _thickness = 3;
    private double _fontSize = 18;

    // ---- UI ----
    private readonly Grid _root = new();
    private readonly Grid _titleBar = new();
    private readonly TextBlock _titleText = new();
    private readonly ScrollViewer _scroll = new();
    private readonly Grid _pageHost = new();
    private readonly PageSurface _surface = new();
    private readonly Image _image = new();
    private readonly Canvas _anno = new();
    private readonly Canvas _overlay = new();
    private readonly Canvas _editLayer = new();
    private readonly ListView _thumbs = new();
    private readonly Border _sidebar = new();
    private readonly StackPanel _empty = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _pageInfo = new();
    private readonly TextBlock _zoomInfo = new();
    private readonly ProgressRing _busy = new();
    private readonly Border _viewport = new();
    private CommandBar _mainBar = null!, _markupBar = null!;
    private AppBarButton _undoBtn = null!, _redoBtn = null!;
    private readonly Dictionary<Tool, AppBarToggleButton> _toolButtons = new();
    private Microsoft.UI.Xaml.Shapes.Ellipse _strokeSwatch = null!, _fillSwatch = null!;
    private readonly bool _mica = MicaController.IsSupported();

    private DocumentPage? Cur => _current >= 0 && _current < _pages.Count ? _pages[_current] : null;
    private IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public MainWindow(string[] args)
    {
        LoadSettings();
        BuildUi();
        Content = _root;
        Title = AppName;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(_titleBar);
        if (_mica) SystemBackdrop = new MicaBackdrop();

        _root.ActualThemeChanged += (_, _) => ApplyChrome();
        _root.Loaded += (_, _) => ApplyChrome();
        ApplyTheme();

        _history.Changed += () =>
        {
            _undoBtn.IsEnabled = _history.CanUndo;
            _redoBtn.IsEnabled = _history.CanRedo;
        };
        AppWindow.Closing += OnClosing;
        SizeChanged += (_, _) => { if (_fit) QueueFit(); };
        try { AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 860)); } catch { }

        SetTool(Tool.TextSelect);
        UpdateUiState();

        // "Open with" and drag-onto-exe: open the files once the window is ready.
        var files = args.Where(File.Exists).ToList();
        bool opened = false;
        _root.Loaded += (_, _) =>
        {
            if (opened || files.Count == 0) return;
            opened = true;
            Run(() => OpenPathsAsync(files, false));
        };
    }

    // ------------------------------------------------------------------ settings & theme

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, "settings.json");

    private void LoadSettings()
    {
        try { _settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch { _settings = new(); }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings));
        }
        catch { }
    }

    private void SetTheme(string theme)
    {
        _settings.Theme = theme;
        SaveSettings();
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        _root.RequestedTheme = _settings.Theme switch
        {
            "Dark" => ElementTheme.Dark,
            "Light" => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
        ApplyChrome();
    }

    /// <summary>Colours that are not picked up automatically from the theme.</summary>
    private void ApplyChrome()
    {
        bool dark = _root.ActualTheme == ElementTheme.Dark;
        _viewport.Background = Brush(dark ? Color.FromArgb(255, 24, 24, 26) : Color.FromArgb(255, 226, 227, 230));
        _sidebar.Background = Brush(dark ? Color.FromArgb(120, 20, 20, 22) : Color.FromArgb(120, 246, 246, 248));
        _root.Background = _mica ? Brush(Colors.Transparent)
            : Brush(dark ? Color.FromArgb(255, 32, 32, 34) : Color.FromArgb(255, 243, 243, 245));
        try
        {
            var tb = AppWindow.TitleBar;
            var fg = dark ? Colors.White : Colors.Black;
            tb.ButtonBackgroundColor = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonForegroundColor = fg;
            tb.ButtonHoverForegroundColor = fg;
            tb.ButtonPressedForegroundColor = fg;
            tb.ButtonInactiveForegroundColor = Colors.Gray;
            tb.ButtonHoverBackgroundColor = dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0);
            tb.ButtonPressedBackgroundColor = dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(50, 0, 0, 0);
        }
        catch { }
    }

    // ------------------------------------------------------------------ UI construction

    private static SolidColorBrush Brush(Color c) => new(c);

    private void BuildUi()
    {
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Title bar
        _titleText.Text = AppName;
        _titleText.VerticalAlignment = VerticalAlignment.Center;
        _titleText.Margin = new Thickness(16, 0, 150, 0);
        _titleText.FontSize = 13;
        _titleText.TextTrimming = TextTrimming.CharacterEllipsis;
        _titleBar.Background = Brush(Colors.Transparent);
        _titleBar.Children.Add(_titleText);
        _root.Children.Add(_titleBar);

        // Toolbars
        _mainBar = BuildMainBar();
        Grid.SetRow(_mainBar, 1);
        _root.Children.Add(_mainBar);
        _markupBar = BuildMarkupBar();
        _markupBar.Visibility = _settings.MarkupBar ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(_markupBar, 2);
        _root.Children.Add(_markupBar);

        // Body: sidebar + page viewport
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 3);
        _root.Children.Add(body);

        _thumbs.SelectionMode = ListViewSelectionMode.Single;
        _thumbs.Padding = new Thickness(4);
        _thumbs.SelectionChanged += (_, _) =>
        {
            int i = _thumbs.SelectedIndex;
            if (i >= 0 && i != _current) Run(() => ShowPageAsync(i));
        };
        _sidebar.Width = 176;
        _sidebar.Child = _thumbs;
        _sidebar.Visibility = Visibility.Collapsed;
        body.Children.Add(_sidebar);

        // Page surface: bitmap, markup layer, selection overlay, text editor layer
        _surface.Background = Brush(Colors.White);
        _image.Stretch = Stretch.Fill;
        _surface.Children.Add(_image);
        _surface.Children.Add(_anno);
        _overlay.IsHitTestVisible = false;
        _surface.Children.Add(_overlay);
        _surface.Children.Add(_editLayer);
        _surface.PointerPressed += OnPointerPressed;
        _surface.PointerMoved += OnPointerMoved;
        _surface.PointerReleased += OnPointerReleased;
        _surface.PointerCaptureLost += (_, _) => CancelDrag();
        _surface.DoubleTapped += OnDoubleTapped;
        _surface.RightTapped += OnRightTapped;
        _surface.SizeChanged += (_, _) => { if (_fit) QueueFit(); };

        _pageHost.HorizontalAlignment = HorizontalAlignment.Center;
        _pageHost.VerticalAlignment = VerticalAlignment.Center;
        _pageHost.Margin = new Thickness(20);
        _pageHost.Children.Add(_surface);
        _pageHost.Visibility = Visibility.Collapsed;

        _scroll.Content = _pageHost;
        _scroll.ZoomMode = ZoomMode.Enabled;
        _scroll.MinZoomFactor = 0.1f; // WinUI rejects values below 0.1
        _scroll.MaxZoomFactor = 32f;
        _scroll.HorizontalScrollMode = ScrollMode.Enabled;
        _scroll.VerticalScrollMode = ScrollMode.Enabled;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.ViewChanged += (_, _) => { UpdateZoomInfo(); RedrawOverlay(); };
        _scroll.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) _fit = false;
        }), true);

        // Empty state
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.Spacing = 12;
        _empty.Children.Add(new FontIcon { Glyph = "", FontSize = 48, Opacity = 0.6 });
        _empty.Children.Add(new TextBlock { Text = "Drop a PDF or image here", FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        _empty.Children.Add(new TextBlock { Text = "or press Ctrl+O to open, Ctrl+V to paste from the clipboard", Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center });
        var openBtn = new Button { Content = "Open…", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        openBtn.Click += (_, _) => Run(OpenAsync);
        _empty.Children.Add(openBtn);

        var viewGrid = new Grid();
        viewGrid.Children.Add(_scroll);
        viewGrid.Children.Add(_empty);
        _viewport.Child = viewGrid;
        Grid.SetColumn(_viewport, 1);
        body.Children.Add(_viewport);

        // Status bar
        var statusBar = new Grid { Padding = new Thickness(12, 4, 12, 4), ColumnSpacing = 16 };
        statusBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _busy.Width = 14; _busy.Height = 14; _busy.IsActive = false;
        _status.FontSize = 12; _status.Opacity = 0.8; _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _pageInfo.FontSize = 12; _pageInfo.Opacity = 0.8;
        _zoomInfo.FontSize = 12; _zoomInfo.Opacity = 0.8; _zoomInfo.MinWidth = 44; _zoomInfo.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(_status, 1); Grid.SetColumn(_pageInfo, 2); Grid.SetColumn(_zoomInfo, 3);
        statusBar.Children.Add(_busy); statusBar.Children.Add(_status); statusBar.Children.Add(_pageInfo); statusBar.Children.Add(_zoomInfo);
        Grid.SetRow(statusBar, 4);
        _root.Children.Add(statusBar);

        // Drag & drop
        _root.AllowDrop = true;
        _root.DragOver += (_, e) =>
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy;
        };
        _root.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var deferral = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
                deferral.Complete();
                Run(() => OpenPathsAsync(paths, false));
            }
            catch { deferral.Complete(); }
        };

        // Keyboard
        Accel(VirtualKey.O, VirtualKeyModifiers.Control, () => Run(OpenAsync));
        Accel(VirtualKey.S, VirtualKeyModifiers.Control, () => Run(() => SaveAsync(false)));
        Accel(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => Run(() => SaveAsync(true)));
        Accel(VirtualKey.Z, VirtualKeyModifiers.Control, () => { CommitEdit(); _history.Undo(); }, true);
        Accel(VirtualKey.Y, VirtualKeyModifiers.Control, () => { CommitEdit(); _history.Redo(); }, true);
        Accel(VirtualKey.Z, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => { CommitEdit(); _history.Redo(); }, true);
        Accel(VirtualKey.C, VirtualKeyModifiers.Control, () => Run(CopyAsync), true);
        Accel(VirtualKey.V, VirtualKeyModifiers.Control, () => Run(PasteAsync), true);
        Accel(VirtualKey.A, VirtualKeyModifiers.Control, () => Run(SelectAllTextAsync), true);
        Accel((VirtualKey)187, VirtualKeyModifiers.Control, ZoomIn);
        Accel(VirtualKey.Add, VirtualKeyModifiers.Control, ZoomIn);
        Accel((VirtualKey)189, VirtualKeyModifiers.Control, ZoomOut);
        Accel(VirtualKey.Subtract, VirtualKeyModifiers.Control, ZoomOut);
        Accel(VirtualKey.Number0, VirtualKeyModifiers.Control, ActualSize);
        Accel(VirtualKey.Number9, VirtualKeyModifiers.Control, Fit);
        Accel(VirtualKey.R, VirtualKeyModifiers.Control, () => Run(() => RotateAsync(true)));
        Accel(VirtualKey.L, VirtualKeyModifiers.Control, () => Run(() => RotateAsync(false)));
        Accel(VirtualKey.K, VirtualKeyModifiers.Control, () => Run(CropAsync));
        Accel(VirtualKey.PageDown, VirtualKeyModifiers.None, () => GoRelative(1), true);
        Accel(VirtualKey.PageUp, VirtualKeyModifiers.None, () => GoRelative(-1), true);
        _root.KeyDown += (_, e) =>
        {
            if (TextInputFocused()) return;
            if (e.Key == VirtualKey.Delete || e.Key == VirtualKey.Back) { DeleteSelected(); e.Handled = true; }
            else if (e.Key == VirtualKey.Escape) { ClearSelections(); e.Handled = true; }
        };
    }

    private bool TextInputFocused()
    {
        if (_root.XamlRoot == null) return false;
        var f = FocusManager.GetFocusedElement(_root.XamlRoot);
        return f is TextBox or PasswordBox or ComboBox or AutoSuggestBox;
    }

    private void Accel(VirtualKey key, VirtualKeyModifiers mods, Action action, bool yieldToTextInput = false)
    {
        var ka = new KeyboardAccelerator { Key = key, Modifiers = mods };
        ka.Invoked += (_, e) =>
        {
            if (yieldToTextInput && TextInputFocused()) return;   // let the text box do its own copy/paste/undo
            e.Handled = true;
            action();
        };
        _root.KeyboardAccelerators.Add(ka);
    }

    private static FontIcon Glyph(string g) => new() { Glyph = g };
    private static FontIcon Symbol(string s, double size = 18) =>
        new() { Glyph = s, FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = size };

    private static AppBarButton Button(IconElement icon, string label, Action click)
    {
        var b = new AppBarButton { Icon = icon, Label = label };
        ToolTipService.SetToolTip(b, label);
        b.Click += (_, _) => click();
        return b;
    }

    private static MenuFlyoutItem Item(string text, Action click, string? glyph = null)
    {
        var item = new MenuFlyoutItem { Text = text };
        if (glyph != null) item.Icon = Glyph(glyph);
        item.Click += (_, _) => click();
        return item;
    }

    private CommandBar BuildMainBar()
    {
        var bar = new CommandBar
        {
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Collapsed,
            Background = Brush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsDynamicOverflowEnabled = true,
        };
        var c = bar.PrimaryCommands;
        c.Add(Button(Glyph(""), "Show or hide page thumbnails", ToggleSidebar));
        c.Add(new AppBarSeparator());
        c.Add(Button(Glyph(""), "Open (Ctrl+O)", () => Run(OpenAsync)));
        c.Add(Button(Glyph(""), "Save (Ctrl+S)", () => Run(() => SaveAsync(false))));
        c.Add(Button(Glyph(""), "Save As / Export (Ctrl+Shift+S)", () => Run(() => SaveAsync(true))));
        c.Add(new AppBarSeparator());
        c.Add(Button(Glyph(""), "Zoom out (Ctrl+-)", ZoomOut));
        c.Add(Button(Glyph(""), "Zoom in (Ctrl++)", ZoomIn));
        c.Add(Button(Glyph(""), "Zoom to fit (Ctrl+9)", Fit));
        c.Add(new AppBarSeparator());
        c.Add(Button(Glyph(""), "Previous page (Page Up)", () => GoRelative(-1)));
        c.Add(Button(Glyph(""), "Next page (Page Down)", () => GoRelative(1)));
        c.Add(new AppBarSeparator());
        var rotL = Glyph("");
        rotL.RenderTransformOrigin = new Point(0.5, 0.5);
        rotL.RenderTransform = new ScaleTransform { ScaleX = -1 };
        c.Add(Button(rotL, "Rotate left (Ctrl+L)", () => Run(() => RotateAsync(false))));
        c.Add(Button(Glyph(""), "Rotate right (Ctrl+R)", () => Run(() => RotateAsync(true))));
        c.Add(Button(Glyph(""), "Crop to selection (Ctrl+K)", () => Run(CropAsync)));
        c.Add(new AppBarSeparator());
        _undoBtn = Button(Glyph(""), "Undo (Ctrl+Z)", () => { CommitEdit(); _history.Undo(); });
        _redoBtn = Button(Glyph(""), "Redo (Ctrl+Y)", () => { CommitEdit(); _history.Redo(); });
        _undoBtn.IsEnabled = _redoBtn.IsEnabled = false;
        c.Add(_undoBtn); c.Add(_redoBtn);
        c.Add(Button(Glyph(""), "Copy (Ctrl+C)", () => Run(CopyAsync)));
        c.Add(Button(Glyph(""), "Paste (Ctrl+V)", () => Run(PasteAsync)));
        c.Add(new AppBarSeparator());
        c.Add(Button(Glyph(""), "Show or hide the markup toolbar", ToggleMarkupBar));

        var theme = new AppBarButton { Icon = Glyph(""), Label = "Appearance" };
        ToolTipService.SetToolTip(theme, "Appearance: light, dark or system");
        var themeMenu = new MenuFlyout();
        foreach (var (name, text) in new[] { ("Default", "Use system setting"), ("Light", "Light"), ("Dark", "Dark") })
        {
            var item = new RadioMenuFlyoutItem { Text = text, GroupName = "theme", IsChecked = _settings.Theme == name };
            item.Click += (_, _) => SetTheme(name);
            themeMenu.Items.Add(item);
        }
        theme.Flyout = themeMenu;
        c.Add(theme);

        var s = bar.SecondaryCommands;
        s.Add(Button(Glyph(""), "Copy all text on this page", () => Run(CopyAllTextAsync)));
        s.Add(Button(Glyph(""), "Select all text (Ctrl+A)", () => Run(SelectAllTextAsync)));
        s.Add(new AppBarSeparator());
        s.Add(Button(Glyph(""), "Flip horizontal", () => Run(() => FlipAsync(true))));
        s.Add(Button(Glyph(""), "Flip vertical", () => Run(() => FlipAsync(false))));
        s.Add(Button(Glyph(""), "Actual size (Ctrl+0)", ActualSize));
        s.Add(new AppBarSeparator());
        s.Add(Button(Glyph(""), "Insert pages from file…", () => Run(InsertAsync)));
        s.Add(Button(Glyph(""), "Delete this page", DeleteCurrentPage));
        s.Add(Button(Glyph(""), "Export this page as image…", () => Run(ExportPageImageAsync)));
        s.Add(new AppBarSeparator());
        s.Add(Button(Glyph(""), "About Glimpse", () => Run(AboutAsync)));
        return bar;
    }

    private CommandBar BuildMarkupBar()
    {
        var bar = new CommandBar
        {
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Collapsed,
            Background = Brush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsDynamicOverflowEnabled = true,
        };
        void ToolBtn(Tool t, IconElement icon, string label)
        {
            var b = new AppBarToggleButton { Icon = icon, Label = label };
            ToolTipService.SetToolTip(b, label);
            b.Click += (_, _) => SetTool(t);
            _toolButtons[t] = b;
            bar.PrimaryCommands.Add(b);
        }
        ToolBtn(Tool.TextSelect, Symbol("⌶", 20), "Select text (recognised with OCR)");
        ToolBtn(Tool.RectSelect, Symbol("⬚", 20), "Rectangular selection: copy, crop or read text from an area");
        ToolBtn(Tool.Move, Symbol("↖", 20), "Move and resize markup");
        bar.PrimaryCommands.Add(new AppBarSeparator());
        ToolBtn(Tool.Text, Symbol("T", 18), "Text box");
        ToolBtn(Tool.Rect, Symbol("▭", 20), "Rectangle");
        ToolBtn(Tool.Ellipse, Symbol("◯", 18), "Ellipse");
        ToolBtn(Tool.Line, Symbol("╱", 18), "Line");
        ToolBtn(Tool.Arrow, Symbol("↗", 20), "Arrow");
        ToolBtn(Tool.Pen, Glyph(""), "Draw");
        ToolBtn(Tool.Highlight, Glyph(""), "Highlighter");
        bar.PrimaryCommands.Add(new AppBarSeparator());

        // Colours
        _strokeSwatch = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 18, Height = 18, StrokeThickness = 1, Stroke = Brush(Colors.Gray) };
        var strokeBtn = new AppBarButton { Content = _strokeSwatch, Label = "Colour" };
        ToolTipService.SetToolTip(strokeBtn, "Line and text colour");
        strokeBtn.Flyout = ColorFlyout(false, c => { ActiveColor = c!.Value; ApplyStyleToSelection(); UpdateSwatches(); });
        bar.PrimaryCommands.Add(strokeBtn);

        _fillSwatch = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 18, Height = 18, StrokeThickness = 2, Stroke = Brush(Colors.Gray) };
        var fillBtn = new AppBarButton { Content = _fillSwatch, Label = "Fill" };
        ToolTipService.SetToolTip(fillBtn, "Fill colour");
        fillBtn.Flyout = ColorFlyout(true, c => { _fill = c; ApplyStyleToSelection(); UpdateSwatches(); });
        bar.PrimaryCommands.Add(fillBtn);

        // Thickness
        var thickBtn = new AppBarButton { Icon = Glyph(""), Label = "Thickness" };
        ToolTipService.SetToolTip(thickBtn, "Line thickness");
        var slider = new Slider { Minimum = 1, Maximum = 24, Value = _thickness, Width = 200, StepFrequency = 1 };
        slider.ValueChanged += (_, e) => { _thickness = e.NewValue; ApplyStyleToSelection(); };
        var thickPanel = new StackPanel { Spacing = 4 };
        thickPanel.Children.Add(new TextBlock { Text = "Line thickness" });
        thickPanel.Children.Add(slider);
        thickBtn.Flyout = new Flyout { Content = thickPanel };
        bar.PrimaryCommands.Add(thickBtn);

        // Font size
        var font = new ComboBox { Width = 86, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
        ToolTipService.SetToolTip(font, "Text size");
        foreach (var size in new[] { 10, 12, 14, 18, 24, 32, 48, 64, 96 }) font.Items.Add(size);
        font.SelectedIndex = 3;
        font.SelectionChanged += (_, _) =>
        {
            if (font.SelectedItem is int v) { _fontSize = v; ApplyStyleToSelection(); }
        };
        bar.PrimaryCommands.Add(new AppBarElementContainer { Content = font, VerticalContentAlignment = VerticalAlignment.Center });
        bar.PrimaryCommands.Add(new AppBarSeparator());
        bar.PrimaryCommands.Add(Button(Glyph(""), "Delete selected markup (Delete)", DeleteSelected));
        UpdateSwatches();
        return bar;
    }

    private static readonly Color[] Palette =
    {
        Color.FromArgb(255, 0, 0, 0), Color.FromArgb(255, 99, 99, 102), Color.FromArgb(255, 255, 255, 255),
        Color.FromArgb(255, 255, 59, 48), Color.FromArgb(255, 255, 149, 0), Color.FromArgb(255, 255, 214, 10),
        Color.FromArgb(255, 52, 199, 89), Color.FromArgb(255, 0, 199, 190), Color.FromArgb(255, 10, 132, 255),
        Color.FromArgb(255, 88, 86, 214), Color.FromArgb(255, 175, 82, 222), Color.FromArgb(255, 255, 45, 85),
    };

    private Flyout ColorFlyout(bool allowNone, Action<Color?> pick)
    {
        var flyout = new Flyout();
        var panel = new StackPanel { Spacing = 8 };
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        for (int i = 0; i < 6; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < Palette.Length; i++)
        {
            var color = Palette[i];
            var sw = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = Brush(color),
                BorderBrush = Brush(Colors.Gray), BorderThickness = new Thickness(1),
            };
            sw.Tapped += (_, _) => { pick(color); flyout.Hide(); };
            Grid.SetColumn(sw, i % 6); Grid.SetRow(sw, i / 6);
            grid.Children.Add(sw);
        }
        panel.Children.Add(grid);
        if (allowNone)
        {
            var none = new Button { Content = "No fill", HorizontalAlignment = HorizontalAlignment.Stretch };
            none.Click += (_, _) => { pick(null); flyout.Hide(); };
            panel.Children.Add(none);
        }
        flyout.Content = panel;
        return flyout;
    }

    private Color ActiveColor
    {
        get => _tool == Tool.Highlight ? _highlight : _stroke;
        set { if (_tool == Tool.Highlight) _highlight = value; else _stroke = value; }
    }

    private void UpdateSwatches()
    {
        _strokeSwatch.Fill = Brush(ActiveColor);
        _fillSwatch.Fill = _fill is Color f ? Brush(f) : Brush(Colors.Transparent);
        _fillSwatch.StrokeDashArray = _fill == null ? new DoubleCollection { 2, 2 } : null;
    }

    private void SetTool(Tool tool)
    {
        CommitEdit();
        _tool = tool;
        foreach (var (t, b) in _toolButtons) b.IsChecked = t == tool;
        _surface.SetCursor(tool switch
        {
            Tool.Move => InputSystemCursorShape.Arrow,
            Tool.TextSelect or Tool.Text => InputSystemCursorShape.IBeam,
            _ => InputSystemCursorShape.Cross,
        });
        if (tool != Tool.Move) _selected = null;
        if (tool != Tool.TextSelect) { _selA = _selB = -1; }
        if (tool != Tool.RectSelect) _marquee = null;
        UpdateSwatches();
        RedrawOverlay();
        if (tool == Tool.TextSelect && Cur is { } p) Run(() => EnsureOcrAsync(p), quiet: true);
    }

    private void ToggleSidebar()
    {
        _settings.Sidebar = !_settings.Sidebar;
        SaveSettings();
        UpdateUiState();
        if (_fit) QueueFit();
    }

    private void ToggleMarkupBar()
    {
        _settings.MarkupBar = !_settings.MarkupBar;
        SaveSettings();
        _markupBar.Visibility = _settings.MarkupBar ? Visibility.Visible : Visibility.Collapsed;
        if (_fit) QueueFit();
    }

    private void UpdateUiState()
    {
        bool has = _pages.Count > 0;
        _empty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        _pageHost.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        _sidebar.Visibility = has && _settings.Sidebar ? Visibility.Visible : Visibility.Collapsed;
        _titleText.Text = has ? $"{_docName}{(_dirty ? " — Edited" : "")}" : AppName;
        Title = has ? $"{_docName} – {AppName}" : AppName;
        var p = Cur;
        _pageInfo.Text = p == null ? "" :
            $"Page {_current + 1} of {_pages.Count}   ·   {p.PixelWidth / p.Scale:0} × {p.PixelHeight / p.Scale:0}";
        UpdateZoomInfo();
    }

    private void UpdateZoomInfo()
    {
        var p = Cur;
        _zoomInfo.Text = p == null ? "" : $"{_scroll.ZoomFactor * p.Scale * 100:0}%";
    }

    private void MarkDirty()
    {
        _dirty = true;
        UpdateUiState();
    }

    // ------------------------------------------------------------------ zoom

    private void ZoomTo(double factor)
    {
        factor = Math.Clamp(factor, _scroll.MinZoomFactor, _scroll.MaxZoomFactor);
        double old = _scroll.ZoomFactor;
        double cx = (_scroll.HorizontalOffset + _scroll.ViewportWidth / 2) / old;
        double cy = (_scroll.VerticalOffset + _scroll.ViewportHeight / 2) / old;
        _scroll.ChangeView(Math.Max(0, cx * factor - _scroll.ViewportWidth / 2),
                           Math.Max(0, cy * factor - _scroll.ViewportHeight / 2), (float)factor, true);
    }

    private void ZoomIn() { _fit = false; ZoomTo(_scroll.ZoomFactor * 1.25); }
    private void ZoomOut() { _fit = false; ZoomTo(_scroll.ZoomFactor / 1.25); }
    private void ActualSize() { if (Cur is { } p) { _fit = false; ZoomTo(1 / p.Scale); } }

    private void Fit()
    {
        var p = Cur;
        if (p == null) return;
        _fit = true;
        double vw = _scroll.ActualWidth - 44, vh = _scroll.ActualHeight - 44;
        if (vw <= 0 || vh <= 0) return;
        double f = Math.Min(vw / p.PixelWidth, vh / p.PixelHeight);
        f = Math.Min(f, 4 / p.Scale);
        _scroll.ChangeView(0, 0, (float)Math.Clamp(f, _scroll.MinZoomFactor, _scroll.MaxZoomFactor), true);
    }

    private void QueueFit() =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (_fit) Fit(); });

    // ------------------------------------------------------------------ helpers

    private void SetStatus(string text) => _status.Text = text;

    public void ReportError(Exception ex) => SetStatus("Something went wrong: " + ex.Message);

    private int _busyCount;

    /// <summary>Runs a command, showing the busy ring and reporting any failure in the status bar.</summary>
    private async void Run(Func<Task> action, bool quiet = false)
    {
        try
        {
            if (!quiet) { _busyCount++; _busy.IsActive = true; }
            await action();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            if (!quiet && --_busyCount <= 0) { _busyCount = 0; _busy.IsActive = false; }
        }
    }

    private async Task<ContentDialogResult> DialogAsync(string title, object content, string? primary, string? secondary, string close)
    {
        if (_dialogOpen || _root.XamlRoot == null) return ContentDialogResult.None;
        _dialogOpen = true;
        try
        {
            var dlg = new ContentDialog
            {
                Title = title,
                Content = content is string s ? new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap } : content,
                CloseButtonText = close,
                XamlRoot = _root.XamlRoot,
                RequestedTheme = _root.ActualTheme,
                DefaultButton = primary != null ? ContentDialogButton.Primary : ContentDialogButton.Close,
            };
            if (primary != null) dlg.PrimaryButtonText = primary;
            if (secondary != null) dlg.SecondaryButtonText = secondary;
            return await dlg.ShowAsync();
        }
        finally { _dialogOpen = false; }
    }

    private Task MessageAsync(string title, string text) => DialogAsync(title, text, null, null, "OK");

    private Task AboutAsync() => MessageAsync("Glimpse",
        "A Preview-style viewer for PDFs and images.\n\n" +
        "• Select and copy text from any page or picture (Windows OCR)\n" +
        "• Text boxes, shapes, arrows, freehand drawing and highlighter\n" +
        "• Rotate, flip, crop, reorder, insert and delete pages\n" +
        "• Copy and paste images and text, export to PDF, PNG, JPEG, TIFF or BMP\n" +
        "• Light and dark appearance");

    private async void OnClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs e)
    {
        CommitEdit();
        if (!_dirty || _closing) return;
        e.Cancel = true;
        try
        {
            if (await ConfirmDiscardAsync()) { _closing = true; Close(); }
        }
        catch (Exception ex) { ReportError(ex); }
    }

    /// <summary>Asks about unsaved changes. Returns true when it is fine to throw the document away.</summary>
    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!_dirty) return true;
        var r = await DialogAsync($"Save changes to “{_docName}”?", "Your changes will be lost if you don't save them.", "Save", "Don't Save", "Cancel");
        if (r == ContentDialogResult.Primary) return await SaveAsync(false);
        return r == ContentDialogResult.Secondary;
    }
}
