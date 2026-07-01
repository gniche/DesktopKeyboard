using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using UIA = Interop.UIAutomationClient;

namespace DesktopKeyboard;

public class MainWindow : Window
{
    // --- Win32 interop -------------------------------------------------------
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")] static extern IntPtr GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLong(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("psapi.dll")] static extern int EmptyWorkingSet(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, char[] name, int max);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    private const uint GA_ROOT = 2;

    private static void TrimWorkingSet() { try { EmptyWorkingSet(GetCurrentProcess()); } catch { } }

    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_LSHIFT = 0xA0, VK_LCONTROL = 0xA2, VK_LMENU = 0xA4, VK_LWIN = 0x5B;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public KEYBDINPUT ki; public int _a, _b; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();
    private readonly INPUT[] _inputBuf = new INPUT[10];

    // --- Theme model ---------------------------------------------------------
    private const double PanelValue = 0.067, KeyValue = 0.145, BorderValue = 0.200;
    private double currentHue, currentSat, currentOpacity = 1.0, currentBrightness = 1.0;
    private int currentSizeState = 1, currentLayoutState = 0;

    private static readonly IBrush ActiveBrush = new ImmutableSolidColorBrush(Color.FromRgb(74, 144, 226));
    private static readonly IBrush LockBrush = new ImmutableSolidColorBrush(Color.FromRgb(210, 140, 30));

    // --- Modifier state machine ---------------------------------------------
    private enum ModState { Off, OneShot, Locked }
    private static readonly string[] ModTags = { "TOGGLE_SHIFT", "TOGGLE_CTRL", "TOGGLE_ALT", "TOGGLE_WIN", "TOGGLE_FN" };
    private readonly Dictionary<string, ModState> _mod = new()
    {
        ["TOGGLE_SHIFT"] = ModState.Off, ["TOGGLE_CTRL"] = ModState.Off,
        ["TOGGLE_ALT"] = ModState.Off, ["TOGGLE_WIN"] = ModState.Off, ["TOGGLE_FN"] = ModState.Off,
    };

    private readonly DispatcherTimer _longPressTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private string? _longPressTag;
    private bool _longPressActive, _longPressFired;

    // Auto-repeat for a held normal key (typematic): initial delay then fast repeat.
    private readonly DispatcherTimer _repeatTimer = new();
    private string? _repeatTag;

    // --- Mode (Auto/Show/Hide) + floating button -----------------------------
    private enum KeyboardMode { Auto, Show, Hide }
    private KeyboardMode currentMode = KeyboardMode.Auto;
    private Border _modeBg = null!;       // the mode button, a child of this window (no 2nd window)
    private TextBlock? _modeText;
    private TextBlock[] _modeOutline = Array.Empty<TextBlock>();
    private const double ModeBtnW = 96, ModeBtnH = 32;
    private bool _modeDragging;
    private int _modeDownX, _modeDownY;   // screen pos where a press started (tap vs drag)
    private int _grabX, _grabY;           // pointer-to-window offset captured at drag start

    // --- Window / lifecycle --------------------------------------------------
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private IntPtr _hwnd;
    private bool _opened;
    private PixelPoint _modeAnchor;     // screen px the mode-button centre is pinned to
    private bool _bodyVisible;          // are the keys shown (vs. just the top row)
    private bool _focusEditable;        // last UIA focus classification (Auto mode)
    private UIA.IUIAutomation? _uia;    // native UIA client, created/owned by the MTA thread
    private FocusHandler? _focusHandler; // roots the COM focus-event sink for the app's lifetime

    // Focus-detection diagnostics (gated by the same Diag flag as the perf log). Written
    // from the MTA focus thread + UIA callback threads, so it needs its own locked file.
    private static bool _diagFocus;
    private static string? _focusLogPath;
    private static readonly object _focusLogLock = new();
    private bool _chromeExpanded = true; // Esc + theme/layout/close shown (vs. collapsed behind the toggle)
    private StackPanel _bodyRow = null!;          // keyboard + numpad; collapses when hidden
    private Key _escKey = null!;
    private StackPanel _chromeGroup = null!;       // theme / layout / close
    private LayoutTransformControl _scaler = null!;
    private readonly ScaleTransform _scale = new(1, 1);   // size preset -> key size; window fits via SizeToContent

    private bool runOnStartup, _loading, _settingsLoaded;
    private long _suppressHideUntil;

    private const string SettingsKey = @"Software\serifpersia\DesktopKeyboard";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DesktopKeyboard";

    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    // Opt-in perf log (registry "Diag"=1 or env DESKTOPKEYBOARD_DIAG=1).
    private DispatcherTimer? _perfTimer;
    private TimeSpan _perfLastCpu;
    private long _perfLastTick;
    private string? _perfLogPath;

    // --- UI references -------------------------------------------------------
    private Border _mainBorder = null!;
    private Grid _numpadGrid = null!;
    private Popup _themePopup = null!;
    private TouchSlider _hueSlider = null!, _brightnessSlider = null!, _opacitySlider = null!, _sizeSlider = null!;
    private TextBlock _brightnessLabel = null!, _opacityLabel = null!, _sizeLabel = null!;
    private TextBlock _startupText = null!;

    private readonly List<Key> _allKeys = new();
    private readonly Dictionary<string, List<Key>> _byTag = new();

    public MainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;

        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.WidthAndHeight;   // window fits the keyboard; grows for the numpad

        Content = BuildContent();

        _hideTimer.Tick += HideTimer_Tick;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettingsNow(); };
        _longPressTimer.Tick += LongPress_Tick;
        _repeatTimer.Tick += (_, _) =>
        {
            if (_repeatTag == null) { _repeatTimer.Stop(); return; }
            SendTag(_repeatTag);
            _repeatTimer.Interval = TimeSpan.FromMilliseconds(45);   // fast repeat after the initial delay
        };

        Dispatcher.UIThread.Post(() =>
        {
            Show();                     // always visible; shrinks to the mode button when hidden
            InitDefaultPosition();
            LoadSettings();
            UpdateGeometry();
            StartPerfLog();
        }, DispatcherPriority.Loaded);

        Dispatcher.UIThread.Post(() =>
        {
            RegisterFocusTracking();
            TrimWorkingSet();
        }, DispatcherPriority.Background);
    }

    // --- UI construction -----------------------------------------------------
    private Control BuildContent()
    {
        // Vertical: a persistent top row (Esc | mode button | theme/layout/close) over the
        // collapsible body (keyboard + optional numpad). The window sizes to this content
        // (SizeToContent), so the numpad grows the window instead of shrinking the keys, and
        // "hidden" simply collapses the body leaving the top row (with the mode button) in place.
        _numpadGrid = new Grid { Margin = new Thickness(6, 0, 0, 0), IsVisible = false };
        for (int i = 0; i < 5; i++) _numpadGrid.RowDefinitions.Add(new RowDefinition(new GridLength(U)));
        for (int i = 0; i < 4; i++) _numpadGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(U)));

        _bodyRow = new StackPanel { Orientation = Orientation.Horizontal };
        _bodyRow.Children.Add(BuildKeyboard());
        _bodyRow.Children.Add(BuildNav());
        _bodyRow.Children.Add(_numpadGrid);

        var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8) };
        stack.Children.Add(BuildTopBar());
        stack.Children.Add(_bodyRow);

        _mainBorder = new Border
        {
            Background = new ImmutableSolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            Child = stack,
        };

        _scaler = new LayoutTransformControl { LayoutTransform = _scale, Child = _mainBorder };
        return _scaler;
    }

    // Builds the mode button (a top-row cell). Tap cycles the mode; drag moves the window.
    private Control BuildModeButton()
    {
        _modeText = MakeModeLabel(Brushes.White, 0, 0);
        _modeOutline = new[]
        {
            MakeModeLabel(Brushes.Black, -1, -1), MakeModeLabel(Brushes.Black, 1, -1),
            MakeModeLabel(Brushes.Black, -1, 1), MakeModeLabel(Brushes.Black, 1, 1),
        };
        var labelGrid = new Grid();
        foreach (var t in _modeOutline) labelGrid.Children.Add(t);
        labelGrid.Children.Add(_modeText);

        _modeBg = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new ImmutableSolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            Cursor = new Cursor(StandardCursorType.Hand),
            Width = ModeBtnW,
            Height = ModeBtnH,
            VerticalAlignment = VerticalAlignment.Center,
            Child = labelGrid,
        };

        // Tap cycles the mode; drag moves the whole window (works shown or collapsed).
        _modeBg.PointerPressed += (_, e) =>
        {
            e.Pointer.Capture(_modeBg);
            var f = this.PointToScreen(e.GetPosition(this));
            _modeDownX = f.X; _modeDownY = f.Y;
            _grabX = f.X - Position.X; _grabY = f.Y - Position.Y;
            _modeDragging = false;
        };
        _modeBg.PointerMoved += (_, e) =>
        {
            if (!ReferenceEquals(e.Pointer.Captured, _modeBg)) return;
            var f = this.PointToScreen(e.GetPosition(this));
            if (!_modeDragging && (Math.Abs(f.X - _modeDownX) > 6 || Math.Abs(f.Y - _modeDownY) > 6))
                _modeDragging = true;
            if (_modeDragging)
            {
                Position = new PixelPoint(f.X - _grabX, f.Y - _grabY);
                SyncAnchorFromMode();
            }
        };
        _modeBg.PointerReleased += (_, e) =>
        {
            e.Pointer.Capture(null);
            if (_modeDragging) { _modeDragging = false; return; }
            CycleMode();
        };

        return _modeBg;
    }

    private Control BuildTopBar()
    {
        // Esc pinned top-left (shown only with the keyboard body); a centred cluster of
        // [mode] [toggle] [theme/layout/close] overlaid in the same cell. Only mode + toggle
        // are persistent, so the hidden strip is just those two. The window is anchored on the
        // mode button's centre, so it never moves as things expand/collapse.
        var bar = new Grid { Background = Brushes.Transparent, Height = 44, HorizontalAlignment = HorizontalAlignment.Stretch };

        _escKey = MakeKey("ESC", "Esc", 16);
        _escKey.HorizontalAlignment = HorizontalAlignment.Left;
        _escKey.VerticalAlignment = VerticalAlignment.Center;
        _escKey.Width = 60; _escKey.Height = 38;

        var mode = BuildModeButton();
        mode.Margin = new Thickness(2, 0, 2, 0);

        var toggle = ChromeButton("⋯", 18, () => SetChromeExpanded(!_chromeExpanded));

        var themeBtn = ChromeButton("🎨", 16, () => _themePopup.IsOpen = !_themePopup.IsOpen);
        var layoutBtn = ChromeButton("⌨", 16, () => { SetLayout((currentLayoutState + 1) % 2); SaveSettings(); });
        var closeBtn = ChromeButton("✕", 20, RequestClose);
        _chromeGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _chromeGroup.Children.Add(themeBtn);
        _chromeGroup.Children.Add(layoutBtn);
        _chromeGroup.Children.Add(closeBtn);

        var cluster = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        cluster.Children.Add(mode);
        cluster.Children.Add(toggle);
        cluster.Children.Add(_chromeGroup);

        bar.Children.Add(cluster);   // centred
        bar.Children.Add(_escKey);   // left, overlaid

        _themePopup = new Popup
        {
            PlacementTarget = themeBtn,
            Placement = PlacementMode.BottomEdgeAlignedRight,
            IsLightDismissEnabled = false,
            Child = BuildThemePanel(),
        };
        bar.Children.Add(_themePopup);

        // Drag the keyboard by the empty top-bar area. BeginMoveDrag uses the OS move-loop,
        // which a WS_EX_NOACTIVATE window can't enter, so move it manually. Use an absolute
        // grab-offset (not incremental deltas) from Avalonia's own pointer coordinates: touch
        // withholds moves until a drag threshold, and the OS cursor doesn't track touch — so
        // anchoring to the grabbed window point keeps it glued under the finger regardless.
        bar.PointerPressed += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, bar)) return;   // ignore presses on Esc/chrome buttons
            e.Pointer.Capture(bar);
            var grab = this.PointToScreen(e.GetPosition(this));
            _grabX = grab.X - Position.X;
            _grabY = grab.Y - Position.Y;
        };
        bar.PointerMoved += (_, e) =>
        {
            if (!ReferenceEquals(e.Pointer.Captured, bar)) return;
            var p = this.PointToScreen(e.GetPosition(this));
            Position = new PixelPoint(p.X - _grabX, p.Y - _grabY);
            SyncAnchorFromMode();
        };
        bar.PointerReleased += (_, e) => e.Pointer.Capture(null);

        return bar;
    }

    private Control BuildThemePanel()
    {
        _hueSlider = new TouchSlider { Minimum = 0, Maximum = 360 };
        _brightnessSlider = new TouchSlider { Minimum = 50, Maximum = 300, Value = 100 };
        _opacitySlider = new TouchSlider { Minimum = 10, Maximum = 100, Value = 100 };
        _sizeSlider = new TouchSlider { Minimum = 0, Maximum = 2, Step = 1, Value = 1 };

        _hueSlider.ValueChanged += v => { currentHue = v; currentSat = 0.55; ApplyTheme(); SaveSettings(); };
        _brightnessSlider.ValueChanged += v => { currentBrightness = v / 100.0; _brightnessLabel.Text = $"Brightness: {(int)v}%"; ApplyTheme(); SaveSettings(); };
        _opacitySlider.ValueChanged += v => { currentOpacity = v / 100.0; _opacityLabel.Text = $"Background opacity: {(int)v}%"; ApplyTheme(); SaveSettings(); };
        _sizeSlider.ValueChanged += v => { SetSize((int)v); SaveSettings(); };

        _brightnessLabel = Label("Brightness: 100%");
        _opacityLabel = Label("Background opacity: 100%");
        _sizeLabel = Label("Size: Medium");

        var stack = new StackPanel { Width = 340 };
        stack.Children.Add(Label("Colour"));
        stack.Children.Add(_hueSlider);
        stack.Children.Add(_brightnessLabel);
        stack.Children.Add(_brightnessSlider);
        stack.Children.Add(_opacityLabel);
        stack.Children.Add(_opacitySlider);
        stack.Children.Add(_sizeLabel);
        stack.Children.Add(_sizeSlider);

        var reset = ChromeButton("Reset to grey", 16, () =>
        {
            _hueSlider.Value = 0; currentHue = 0; currentSat = 0; ApplyTheme(); SaveSettings();
        });
        reset.Background = new ImmutableSolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
        reset.Height = 44; reset.Margin = new Thickness(0, 12, 0, 0);

        var startup = ChromeButton("Run on startup: Off", 16, () => { SetRunOnStartup(!runOnStartup); SaveSettings(); });
        startup.Background = new ImmutableSolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
        startup.Height = 44; startup.Margin = new Thickness(0, 8, 0, 0);
        _startupText = (TextBlock)startup.Child!;
        stack.Children.Add(reset);
        stack.Children.Add(startup);

        return new Border
        {
            Background = new ImmutableSolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            BorderBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            Child = stack,
        };
    }

    private static TextBlock Label(string t) => new()
    {
        Text = t,
        Foreground = Brushes.White,
        FontSize = 16,
        Margin = new Thickness(0, 8, 0, 4),
    };

    // A flat clickable chrome button (no key outline/highlight). Child is its TextBlock.
    private Border ChromeButton(string glyph, double fs, Action onClick)
    {
        var b = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 2, 8, 2),
            Child = new TextBlock
            {
                Text = glyph,
                Foreground = Brushes.White,
                FontSize = fs,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        b.PointerPressed += (_, e) => { e.Pointer.Capture(b); };
        b.PointerReleased += (_, _) => onClick();
        return b;
    }

    // One key cell = U design pixels (scaled as a whole by the size preset). Every key's width
    // is a multiple of U, so all 1-unit keys are identical; wide keys are exact multiples.
    private const double U = 54;

    private Control BuildKeyboard()
    {
        var col = new StackPanel { Orientation = Orientation.Vertical };

        col.Children.Add(Row(new[] { 1.0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2 },
            MakeKey("GRAVE", "`"), MakeKey("1", "1"), MakeKey("2", "2"), MakeKey("3", "3"), MakeKey("4", "4"),
            MakeKey("5", "5"), MakeKey("6", "6"), MakeKey("7", "7"), MakeKey("8", "8"), MakeKey("9", "9"),
            MakeKey("0", "0"), MakeKey("MINUS", "-"), MakeKey("EQUALS", "="), MakeKey("BACK", "⌫", 18)));

        col.Children.Add(Row(new[] { 1.5, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1.5 },
            MakeKey("TAB", "⇥"), MakeKey("Q", "q"), MakeKey("W", "w"), MakeKey("E", "e"), MakeKey("R", "r"),
            MakeKey("T", "t"), MakeKey("Y", "y"), MakeKey("U", "u"), MakeKey("I", "i"), MakeKey("O", "o"),
            MakeKey("P", "p"), MakeKey("BACKSLASH", "\\")));

        col.Children.Add(Row(new[] { 1.75, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2.25 },
            MakeKey("TOGGLE_FN", "Fn", 18), MakeKey("A", "a"), MakeKey("S", "s"), MakeKey("D", "d"), MakeKey("F", "f"),
            MakeKey("G", "g"), MakeKey("H", "h"), MakeKey("J", "j"), MakeKey("K", "k"), MakeKey("L", "l"),
            MakeKey("ENTER", "↵")));

        col.Children.Add(Row(new[] { 2.25, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1.75 },
            MakeKey("TOGGLE_SHIFT", "⇧", 26), MakeKey("Z", "z"), MakeKey("X", "x"), MakeKey("C", "c"), MakeKey("V", "v"),
            MakeKey("B", "b"), MakeKey("N", "n"), MakeKey("M", "m"), MakeKey("COMMA", ","), MakeKey("PERIOD", "."),
            MakeKey("SLASH", "/")));

        col.Children.Add(Row(new[] { 1.5, 1.5, 1.5, 7.5 },
            MakeKey("TOGGLE_CTRL", "Ctrl", 18), MakeKey("TOGGLE_WIN", "⊞", 20), MakeKey("TOGGLE_ALT", "Alt", 18),
            MakeKey("SPACE", "Space", 18)));

        return col;
    }

    // Nav cluster as a far-right column: PgUp/PgDn/Home/End on top, inverted-T arrows below.
    private Control BuildNav()
    {
        var top = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < 2; i++) { top.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(U))); top.RowDefinitions.Add(new RowDefinition(new GridLength(U))); }
        Place(top, MakeKey("HOME", "Home", 15), 0, 0);
        Place(top, MakeKey("PGUP", "PgUp", 15), 0, 1);
        Place(top, MakeKey("END", "End", 15), 1, 0);
        Place(top, MakeKey("PGDN", "PgDn", 15), 1, 1);

        var arrows = new Grid { Margin = new Thickness(0, U * 0.6, 0, 0) };
        for (int i = 0; i < 3; i++) arrows.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(U)));
        for (int i = 0; i < 2; i++) arrows.RowDefinitions.Add(new RowDefinition(new GridLength(U)));
        Place(arrows, MakeKey("UP", "↑", 26), 0, 1);
        Place(arrows, MakeKey("LEFT", "←", 22), 1, 0);
        Place(arrows, MakeKey("DOWN", "↓", 22), 1, 1);
        Place(arrows, MakeKey("RIGHT", "→", 22), 1, 2);

        var nav = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(6, 0, 0, 0) };
        nav.Children.Add(top);
        nav.Children.Add(arrows);
        return nav;
    }

    private static void Place(Grid g, Key k, int row, int col) { Grid.SetRow(k, row); Grid.SetColumn(k, col); g.Children.Add(k); }

    private Grid Row(double[] widths, params Key[] keys)
    {
        var g = new Grid { Height = U, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(w * U)));
        for (int i = 0; i < keys.Length; i++) { Grid.SetColumn(keys[i], i); g.Children.Add(keys[i]); }
        return g;
    }

    // Creates a key, registers it in the caches, and wires the central handlers.
    private Key MakeKey(string tag, string label, double fontSize = 22)
    {
        var k = new Key(tag, label, fontSize);
        k.Pressed += OnKeyPressed;
        k.Released += OnKeyReleased;
        _allKeys.Add(k);
        if (!_byTag.TryGetValue(tag, out var list)) _byTag[tag] = list = new List<Key>();
        list.Add(k);
        return k;
    }

    // --- Lifecycle / window styling -----------------------------------------
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        NoActivate(_hwnd);
        _opened = true;
    }

    // Default anchor: mode-button centre at bottom-centre of the work area, with the keys
    // extending downward to near the bottom edge.
    private void InitDefaultPosition()
    {
        double rs = RenderScaling > 0 ? RenderScaling : 1;
        double s = _scale.ScaleX;
        var wa = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        _modeAnchor = new PixelPoint(
            wa.X + wa.Width / 2,
            (int)(wa.Y + wa.Height - (5 * U) * s * rs - 40));
    }

    // Show/hide the keys + chrome; the window auto-sizes (SizeToContent), then we re-pin the
    // mode-button centre to _modeAnchor so it never moves as things expand/collapse.
    private void UpdateGeometry()
    {
        _bodyVisible = currentMode == KeyboardMode.Show ||
                       (currentMode == KeyboardMode.Auto && _focusEditable);
        _bodyRow.IsVisible = _bodyVisible;
        _escKey.IsVisible = _bodyVisible;   // Esc shows only with the keys, pinned top-left
        SetChromeExpanded(_bodyVisible);    // also re-anchors
    }

    // Show/hide theme/layout/close (the toggle group), then re-anchor.
    private void SetChromeExpanded(bool on)
    {
        _chromeExpanded = on;
        if (_chromeGroup != null) _chromeGroup.IsVisible = on;
        if (!on) _themePopup.IsOpen = false;
        AnchorToMode();
    }

    // Shift the window so the mode-button centre sits at _modeAnchor (after layout settles).
    // Coalesced: several layout-changing calls in a row produce a single reposition (no flicker).
    private bool _anchorPending;
    private void AnchorToMode()
    {
        if (!_opened || _anchorPending) return;
        _anchorPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _anchorPending = false;
            var b = _modeBg.Bounds;
            if (b.Width > 0)
            {
                var c = _modeBg.PointToScreen(new Point(b.Width / 2, b.Height / 2));
                Position = new PixelPoint(Position.X + (_modeAnchor.X - c.X), Position.Y + (_modeAnchor.Y - c.Y));
            }
            MakeTopmost(_hwnd);
        }, DispatcherPriority.Loaded);
    }

    private void SyncAnchorFromMode()
    {
        var b = _modeBg.Bounds;
        if (b.Width <= 0) return;
        _modeAnchor = _modeBg.PointToScreen(new Point(b.Width / 2, b.Height / 2));
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveSettingsNow(); }
        // Release any physically-held locked modifiers so they don't stick after exit.
        foreach (var t in ModTags) if (_mod[t] == ModState.Locked) SetModState(t, ModState.Off);
        base.OnClosing(e);
    }

    private static void MakeTopmost(IntPtr h)
    {
        if (h != IntPtr.Zero) SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
    }

    private static void NoActivate(IntPtr h, bool toolWindow = false)
    {
        if (h == IntPtr.Zero) return;
        long ex = GetWindowLong(h, GWL_EXSTYLE).ToInt64() | WS_EX_NOACTIVATE;
        if (toolWindow) ex |= WS_EX_TOOLWINDOW;
        SetWindowLong(h, GWL_EXSTYLE, new IntPtr(ex));
        MakeTopmost(h);
    }

    // --- Focus-driven show/hide (UI Automation) ------------------------------
    // Registered and polled from a dedicated MTA thread, per Microsoft's UI Automation
    // client threading guidance: event-handler threads should be MTA, not the app's own
    // STA UI thread, or event delivery/unregistration can misbehave.
    private void RegisterFocusTracking()
    {
        _diagFocus = DiagEnabled();
        if (_diagFocus)
            _focusLogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DesktopKeyboard_focus.log");

        var thread = new Thread(FocusTrackingThreadProc) { IsBackground = true, Name = "UIAFocusTracking" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    // Appends one line to the focus log when Diag is on. Safe from any thread; a no-op
    // (and near-zero cost) otherwise.
    private static void FocusLog(string msg)
    {
        if (!_diagFocus || _focusLogPath == null) return;
        try
        {
            lock (_focusLogLock)
                System.IO.File.AppendAllText(_focusLogPath,
                    $"{DateTime.Now:HH:mm:ss.fff} [t{Environment.CurrentManagedThreadId}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    // Last-write time of the running assembly — lets the log prove which binary is live,
    // so a stale install (MSI skipping same-version file replacement) is obvious.
    private static string BuildStamp()
    {
        try
        {
            string loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
            return string.IsNullOrEmpty(loc) ? "?" : System.IO.File.GetLastWriteTime(loc).ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch { return "?"; }
    }

    private static string CtName(int ct) => ct switch
    {
        (int)UIA.UIA_ControlTypeIds.UIA_EditControlTypeId => "Edit",
        (int)UIA.UIA_ControlTypeIds.UIA_ComboBoxControlTypeId => "ComboBox",
        (int)UIA.UIA_ControlTypeIds.UIA_DocumentControlTypeId => "Document",
        (int)UIA.UIA_ControlTypeIds.UIA_TextControlTypeId => "Text",
        (int)UIA.UIA_ControlTypeIds.UIA_ButtonControlTypeId => "Button",
        (int)UIA.UIA_ControlTypeIds.UIA_HyperlinkControlTypeId => "Hyperlink",
        (int)UIA.UIA_ControlTypeIds.UIA_ListItemControlTypeId => "ListItem",
        (int)UIA.UIA_ControlTypeIds.UIA_PaneControlTypeId => "Pane",
        (int)UIA.UIA_ControlTypeIds.UIA_GroupControlTypeId => "Group",
        (int)UIA.UIA_ControlTypeIds.UIA_CustomControlTypeId => "Custom",
        _ => "ct#" + ct,
    };

    // Full signal snapshot for one focused element (event path only, so it never spams).
    private static string SnapshotSignals(UIA.IUIAutomationElement el)
    {
        int ct = SafeCt(el);
        string name = "";
        try { name = el.GetCachedPropertyValue((int)UIA.UIA_PropertyIds.UIA_NamePropertyId) as string ?? ""; } catch { }
        if (name.Length > 40) name = name[..40] + "…";
        bool enabled = GetCachedBool(el, (int)UIA.UIA_PropertyIds.UIA_IsEnabledPropertyId, true);
        bool pwd = GetCachedBool(el, (int)UIA.UIA_PropertyIds.UIA_IsPasswordPropertyId);
        bool hasVal = GetCachedBool(el, (int)UIA.UIA_PropertyIds.UIA_IsValuePatternAvailablePropertyId);
        bool valRo = GetCachedBool(el, (int)UIA.UIA_PropertyIds.UIA_ValueIsReadOnlyPropertyId);
        bool hasTe = GetCachedBool(el, (int)UIA.UIA_PropertyIds.UIA_IsTextEditPatternAvailablePropertyId);
        bool hasLeg = GetCachedBool(el, (int)UIA.UIA_PropertyIds.UIA_IsLegacyIAccessiblePatternAvailablePropertyId);
        int role = -1, state = -1;
        try { if (el.GetCachedPropertyValue((int)UIA.UIA_PropertyIds.UIA_LegacyIAccessibleRolePropertyId) is int r) role = r; } catch { }
        try { if (el.GetCachedPropertyValue((int)UIA.UIA_PropertyIds.UIA_LegacyIAccessibleStatePropertyId) is int s) state = s; } catch { }
        return $"  signals: ct={CtName(ct)} name=\"{name}\" enabled={enabled} pwd={pwd} " +
               $"value={hasVal}(ro={valRo}) textEdit={hasTe} legacy={hasLeg}(role=0x{role:X} state=0x{state:X}) " +
               $"hostClass=\"{GetHostClass(el)}\"";
    }

    private static int SafeCt(UIA.IUIAutomationElement el)
    {
        try { return (int)el.GetCachedPropertyValue((int)UIA.UIA_PropertyIds.UIA_ControlTypePropertyId); }
        catch { return -1; }
    }

    // Property IDs cached on every focused element (one cross-process fetch per focus
    // change) so classification below is a pure local read — no per-property round trips.
    private static readonly int[] _cacheProps =
    {
        (int)UIA.UIA_PropertyIds.UIA_ControlTypePropertyId,
        (int)UIA.UIA_PropertyIds.UIA_NamePropertyId,
        (int)UIA.UIA_PropertyIds.UIA_IsEnabledPropertyId,
        (int)UIA.UIA_PropertyIds.UIA_IsPasswordPropertyId,
        (int)UIA.UIA_PropertyIds.UIA_IsValuePatternAvailablePropertyId,
        (int)UIA.UIA_PropertyIds.UIA_ValueIsReadOnlyPropertyId,
        (int)UIA.UIA_PropertyIds.UIA_IsTextEditPatternAvailablePropertyId,
        (int)UIA.UIA_PropertyIds.UIA_IsLegacyIAccessiblePatternAvailablePropertyId,
        (int)UIA.UIA_PropertyIds.UIA_LegacyIAccessibleRolePropertyId,
        (int)UIA.UIA_PropertyIds.UIA_LegacyIAccessibleStatePropertyId,
        (int)UIA.UIA_PropertyIds.UIA_NativeWindowHandlePropertyId,
        (int)UIA.UIA_PropertyIds.UIA_ProcessIdPropertyId,
    };

    // True when the focused element belongs to our own keyboard window. Tapping a key can
    // briefly move UIA focus onto the OSK; treating that as "focus left the text field"
    // would hide the keyboard mid-type, so these focus changes must be ignored outright.
    private static bool IsOwnProcess(UIA.IUIAutomationElement element)
    {
        try
        {
            object p = element.GetCachedPropertyValue((int)UIA.UIA_PropertyIds.UIA_ProcessIdPropertyId);
            int pid = p switch { int i => i, long l => (int)l, _ => 0 };
            return pid != 0 && pid == Environment.ProcessId;
        }
        catch { return false; }
    }

    // Console/terminal hosts draw their own text and expose no editable UIA signal, so
    // they're detected by window class instead. Extend as needed.
    private static readonly string[] _consoleWindowClasses =
    {
        "CASCADIA_HOSTING_WINDOW_CLASS", // Windows Terminal
        "ConsoleWindowClass",            // classic conhost (cmd.exe, powershell.exe)
        "PseudoConsoleWindow",
    };

    private void FocusTrackingThreadProc()
    {
        FocusLog($"=== focus tracking start (native UIA) pid={Environment.ProcessId} built={BuildStamp()} ===");

        UIA.IUIAutomation uia;
        UIA.IUIAutomationCacheRequest cache;
        try
        {
            uia = (UIA.IUIAutomation)new UIA.CUIAutomation8();
            FocusLog("CUIAutomation8 created");

            // IUIAutomation6 (Win10 1809+) hardens the shared event pipe: recover the
            // connection if a provider crashes, and coalesce event storms instead of
            // letting one chatty app stall focus delivery for every UIA client.
            if (uia is UIA.IUIAutomation6 uia6)
            {
                try
                {
                    uia6.ConnectionRecoveryBehavior = UIA.ConnectionRecoveryBehaviorOptions.ConnectionRecoveryBehaviorOptions_Enabled;
                    uia6.CoalesceEvents = UIA.CoalesceEventsOptions.CoalesceEventsOptions_Enabled;
                    FocusLog("IUIAutomation6 reliability knobs enabled");
                }
                catch (Exception ex) { FocusLog("IUIAutomation6 knobs failed: " + ex.Message); }
            }
            else FocusLog("IUIAutomation6 not available");

            cache = uia.CreateCacheRequest();
            foreach (int p in _cacheProps) cache.AddProperty(p);

            _uia = uia;
            _focusHandler = new FocusHandler(this);
            uia.AddFocusChangedEventHandler(cache, _focusHandler);
            FocusLog("focus-changed handler registered — waiting for events");
        }
        catch (Exception ex)
        {
            // no UIA available — auto mode silently degrades to manual
            FocusLog("COM init FAILED (auto show/hide disabled): " + ex);
            return;
        }

        // Safety-net poll: event delivery can be missed (a Chromium tab's accessibility
        // tree not yet warmed up, a dropped WinEvent, another process's UIA provider
        // stalling the shared event pipe) so periodically reconcile against the actual
        // focused element rather than trusting the event alone.
        bool firstPoll = true;
        bool lastEditable = false;
        int lastCt = int.MinValue;
        while (true)
        {
            if (currentMode == KeyboardMode.Auto)
            {
                try
                {
                    var fe = uia.GetFocusedElementBuildCache(cache);
                    if (fe != null && !IsOwnProcess(fe))
                    {
                        bool editable = IsEditableTextField(fe, out string reason);
                        int ct = SafeCt(fe);
                        // Only log when the picture changes, so the 1 Hz poll doesn't flood.
                        if (firstPoll || editable != lastEditable || ct != lastCt)
                        {
                            FocusLog($"[poll] ct={CtName(ct)} editable={editable} ({reason})");
                            firstPoll = false; lastEditable = editable; lastCt = ct;
                        }
                        Dispatcher.UIThread.Post(() => ApplyFocusVisibility(editable));
                    }
                }
                catch (Exception ex) { FocusLog("[poll] error: " + ex.Message); }
            }

            Thread.Sleep(1000);
        }
    }

    // The COM sink UIA calls (on its own MTA thread) for every system-wide focus change.
    private sealed class FocusHandler(MainWindow owner) : UIA.IUIAutomationFocusChangedEventHandler
    {
        public void HandleFocusChangedEvent(UIA.IUIAutomationElement? sender)
        {
            if (sender == null) { FocusLog("[event] sender=null"); return; }
            if (owner.currentMode != KeyboardMode.Auto) { FocusLog("[event] ignored (mode != Auto)"); return; }
            if (IsOwnProcess(sender)) { FocusLog("[event] ignored (own keyboard window)"); return; }

            bool editable;
            try
            {
                if (_diagFocus) FocusLog("[event]" + Environment.NewLine + SnapshotSignals(sender));
                editable = IsEditableTextField(sender, out string reason);
                FocusLog($"[event] -> editable={editable} ({reason})");
            }
            catch (Exception ex) { FocusLog("[event] error: " + ex.Message); return; }

            Dispatcher.UIThread.Post(() => owner.ApplyFocusVisibility(editable));
        }
    }

    private void ApplyFocusVisibility(bool editable)
    {
        if (editable)
        {
            _hideTimer.Stop();
            _focusEditable = true;
            if (!_bodyVisible) { FocusLog("[apply] editable -> SHOW"); UpdateGeometry(); }
        }
        else if (_bodyVisible)
        {
            if (Environment.TickCount64 < _suppressHideUntil) return;
            FocusLog("[apply] not editable -> start hide timer");
            _hideTimer.Start();
        }
    }

    // MSAA role/state bits (oleacc.h) exposed via the LegacyIAccessible pattern.
    private const int ROLE_SYSTEM_TEXT = 0x2A;
    private const int STATE_SYSTEM_UNAVAILABLE = 0x1;
    private const int STATE_SYSTEM_READONLY = 0x40;

    // Ordered strongest-and-cheapest first. Reads are all against the cached element.
    // `reason` names the rule that decided, for the diagnostic log.
    private static bool IsEditableTextField(UIA.IUIAutomationElement element, out string reason)
    {
        int ct;
        try { ct = (int)element.GetCachedPropertyValue((int)UIA.UIA_PropertyIds.UIA_ControlTypePropertyId); }
        catch (Exception ex) { reason = "controltype read failed: " + ex.Message; return false; }

        // 1. Hard negatives: control types that never take text entry. Slider/ProgressBar
        //    are here specifically because they expose a (settable) ValuePattern and would
        //    otherwise false-positive at step 4.
        switch (ct)
        {
            case (int)UIA.UIA_ControlTypeIds.UIA_ButtonControlTypeId:
            case (int)UIA.UIA_ControlTypeIds.UIA_CheckBoxControlTypeId:
            case (int)UIA.UIA_ControlTypeIds.UIA_RadioButtonControlTypeId:
            case (int)UIA.UIA_ControlTypeIds.UIA_MenuItemControlTypeId:
            case (int)UIA.UIA_ControlTypeIds.UIA_TabItemControlTypeId:
            case (int)UIA.UIA_ControlTypeIds.UIA_HyperlinkControlTypeId:
            case (int)UIA.UIA_ControlTypeIds.UIA_ImageControlTypeId:
            case (int)UIA.UIA_ControlTypeIds.UIA_ScrollBarControlTypeId:
            case (int)UIA.UIA_ControlTypeIds.UIA_ListItemControlTypeId:
            case (int)UIA.UIA_ControlTypeIds.UIA_TreeItemControlTypeId:
            case (int)UIA.UIA_ControlTypeIds.UIA_SliderControlTypeId:
            case (int)UIA.UIA_ControlTypeIds.UIA_ProgressBarControlTypeId:
            case (int)UIA.UIA_ControlTypeIds.UIA_MenuControlTypeId:
            case (int)UIA.UIA_ControlTypeIds.UIA_MenuBarControlTypeId:
            case (int)UIA.UIA_ControlTypeIds.UIA_ToolBarControlTypeId:
            case (int)UIA.UIA_ControlTypeIds.UIA_TitleBarControlTypeId:
            case (int)UIA.UIA_ControlTypeIds.UIA_TreeControlTypeId:
            case (int)UIA.UIA_ControlTypeIds.UIA_ListControlTypeId:
            case (int)UIA.UIA_ControlTypeIds.UIA_TabControlTypeId:
                reason = "rule1 hard-negative control type";
                return false;
        }

        // 2. Disabled elements can't take input (default when unsupported is enabled).
        if (GetCachedBool(element, (int)UIA.UIA_PropertyIds.UIA_IsEnabledPropertyId, defaultValue: true) == false)
        {
            reason = "rule2 disabled";
            return false;
        }

        // 3. Password field: unambiguously editable, and often hides its ValuePattern value.
        if (GetCachedBool(element, (int)UIA.UIA_PropertyIds.UIA_IsPasswordPropertyId))
        {
            reason = "rule3 password";
            return true;
        }

        // 4. ValuePattern is the primary oracle for text-bearing controls: Core-AAM makes
        //    editable Edit/Document expose it, and IsReadOnly is authoritative (this both
        //    accepts multi-line web editors and rejects read-only inputs and page content).
        if (GetCachedBool(element, (int)UIA.UIA_PropertyIds.UIA_IsValuePatternAvailablePropertyId))
        {
            string name = element.GetCachedPropertyValue((int)UIA.UIA_PropertyIds.UIA_NamePropertyId) as string ?? "";
            if (IsLikelyFileName(name)) { reason = "rule4 value but filename-like name"; return false; }
            bool ro = GetCachedBool(element, (int)UIA.UIA_PropertyIds.UIA_ValueIsReadOnlyPropertyId);
            reason = ro ? "rule4 value read-only" : "rule4 value editable";
            return !ro;
        }

        // 5. TextEdit pattern: raised by genuinely editable text surfaces (composition /
        //    text-edit events) — a stronger positive than plain TextPattern, which read-only
        //    documents also expose and which caused the original browser false-positives.
        if (GetCachedBool(element, (int)UIA.UIA_PropertyIds.UIA_IsTextEditPatternAvailablePropertyId))
        {
            reason = "rule5 textedit pattern";
            return true;
        }

        // 6. LegacyIAccessible (MSAA/IA2 bridge, rich in Chrome/Firefox): editable text is
        //    role TEXT without the read-only/unavailable state bits.
        if (GetCachedBool(element, (int)UIA.UIA_PropertyIds.UIA_IsLegacyIAccessiblePatternAvailablePropertyId))
        {
            if (element.GetCachedPropertyValue((int)UIA.UIA_PropertyIds.UIA_LegacyIAccessibleRolePropertyId) is int role &&
                element.GetCachedPropertyValue((int)UIA.UIA_PropertyIds.UIA_LegacyIAccessibleStatePropertyId) is int state &&
                role == ROLE_SYSTEM_TEXT &&
                (state & STATE_SYSTEM_READONLY) == 0 &&
                (state & STATE_SYSTEM_UNAVAILABLE) == 0)
            {
                reason = "rule6 legacy role=TEXT";
                return true;
            }
        }

        // 7. Inherent text controls that exposed no usable pattern (non-conformant
        //    providers): Edit/ComboBox mean text entry, so give the benefit of the doubt.
        //    Deliberately NOT Document — a Document with no editable signal is read-only page
        //    content, the exact false-positive this rewrite fixes.
        if (ct == (int)UIA.UIA_ControlTypeIds.UIA_EditControlTypeId ||
            ct == (int)UIA.UIA_ControlTypeIds.UIA_ComboBoxControlTypeId)
        {
            reason = "rule7 edit/combobox fallback";
            return true;
        }

        // 8. Console/terminal surfaces (Windows Terminal, classic conhost) draw their own
        //    text and expose NO editable UIA signal — Pane/Text control type, no Value/
        //    TextEdit, legacy role CLIENT/PANE rather than TEXT. Detect them by the hosting
        //    window class so focus inside a terminal still raises the keyboard.
        string cls = GetHostClass(element);
        foreach (var c in _consoleWindowClasses)
            if (string.Equals(cls, c, StringComparison.OrdinalIgnoreCase))
            {
                reason = "rule8 console window class=" + cls;
                return true;
            }

        reason = "default no editable signal" + (cls.Length > 0 ? " (hostClass=" + cls + ")" : "");
        return false;
    }

    // Class name of the window hosting an element: prefer the element's own native window
    // handle, falling back to the foreground window (our keyboard is WS_EX_NOACTIVATE, so
    // it never becomes foreground and can't mask the app being typed into).
    private static string GetHostClass(UIA.IUIAutomationElement element)
    {
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            object h = element.GetCachedPropertyValue((int)UIA.UIA_PropertyIds.UIA_NativeWindowHandlePropertyId);
            hwnd = h switch { int i => new IntPtr(i), long l => new IntPtr(l), _ => IntPtr.Zero };
        }
        catch { }
        if (hwnd == IntPtr.Zero) hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return "";

        // Walk to the top-level window — a terminal's class lives on its root frame, while a
        // focused element's native handle is often a child (e.g. a XAML island).
        IntPtr root = GetAncestor(hwnd, GA_ROOT);
        if (root != IntPtr.Zero) hwnd = root;

        var buf = new char[256];
        int n = GetClassName(hwnd, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : "";
    }

    // Reads a cached boolean; returns defaultValue when the provider doesn't supply one
    // (GetCachedPropertyValue hands back a non-bool "not supported" sentinel in that case).
    private static bool GetCachedBool(UIA.IUIAutomationElement element, int propertyId, bool defaultValue = false)
    {
        try { return element.GetCachedPropertyValue(propertyId) is bool b ? b : defaultValue; }
        catch { return defaultValue; }
    }

    private static bool IsLikelyFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
               (name.Contains('.') && name.Length > 6);
    }

    private void HideTimer_Tick(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        if (currentMode != KeyboardMode.Auto) return;
        if (Environment.TickCount64 < _suppressHideUntil) return;
        if (_bodyVisible) { _focusEditable = false; _themePopup.IsOpen = false; UpdateGeometry(); }
    }

    // --- Close confirmation --------------------------------------------------
    private async void RequestClose()
    {
        if (await ConfirmClose()) _desktop.Shutdown();
    }

    private Task<bool> ConfirmClose()
    {
        var tcs = new TaskCompletionSource<bool>();
        var yes = ChromeButton("Close", 16, () => { });
        var no = ChromeButton("Cancel", 16, () => { });
        yes.Background = new ImmutableSolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2));
        no.Background = new ImmutableSolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        yes.Width = no.Width = 90; yes.Height = no.Height = 40;
        yes.Margin = no.Margin = new Thickness(6, 0, 6, 0);

        var dlg = new Window
        {
            SystemDecorations = SystemDecorations.BorderOnly,
            Title = "Desktop Keyboard",
            Width = 280, Height = 120,
            CanResize = false,
            Topmost = true,   // above the topmost keyboard
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new ImmutableSolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(yes); buttons.Children.Add(no);
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = "Close Desktop Keyboard?", Foreground = Brushes.White, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(buttons);
        dlg.Content = panel;

        yes.PointerReleased += (_, _) => { tcs.TrySetResult(true); dlg.Close(); };
        no.PointerReleased += (_, _) => { tcs.TrySetResult(false); dlg.Close(); };
        dlg.Closed += (_, _) => tcs.TrySetResult(false);

        dlg.Show();
        return tcs.Task;
    }

    // --- Size / layout -------------------------------------------------------
    private void SetSize(int state)
    {
        currentSizeState = state;
        _scale.ScaleX = _scale.ScaleY = state switch { 0 => 0.78, 2 => 1.3, _ => 1.0 };
        AnchorToMode();
        if (_sizeLabel != null)
            _sizeLabel.Text = "Size: " + (state switch { 0 => "Small", 2 => "Large", _ => "Medium" });
    }

    private bool _numpadBuilt;
    private static readonly (string C, string Tag, int R, int Col, int RS, int CS, double FS)[] NumpadKeys =
    {
        ("NLk","NUMLK",0,0,1,1,14), ("/","NUMSLASH",0,1,1,1,18), ("*","NUMSTAR",0,2,1,1,18), ("-","NUMMINUS",0,3,1,1,18),
        ("7","NUM7",1,0,1,1,0), ("8","NUM8",1,1,1,1,0), ("9","NUM9",1,2,1,1,0), ("+","NUMPLUS",1,3,2,1,18),
        ("4","NUM4",2,0,1,1,0), ("5","NUM5",2,1,1,1,0), ("6","NUM6",2,2,1,1,0),
        ("1","NUM1",3,0,1,1,0), ("2","NUM2",3,1,1,1,0), ("3","NUM3",3,2,1,1,0), ("↵","ENTER",3,3,2,1,20),
        ("0","NUM0",4,0,1,2,0), (".","NUMDOT",4,2,1,1,18),
    };

    private void BuildNumpad()
    {
        if (_numpadBuilt) return;
        _numpadBuilt = true;
        foreach (var k in NumpadKeys)
        {
            var key = MakeKey(k.Tag, k.C, k.FS > 0 ? k.FS : 22);
            Grid.SetRow(key, k.R);
            Grid.SetColumn(key, k.Col);
            if (k.RS > 1) Grid.SetRowSpan(key, k.RS);
            if (k.CS > 1) Grid.SetColumnSpan(key, k.CS);
            _numpadGrid.Children.Add(key);
        }
        ApplyTheme();   // the just-created keys need their themed background applied
    }

    private void SetLayout(int state)
    {
        currentLayoutState = state;
        bool full = state == 1;
        if (full) BuildNumpad();
        _numpadGrid.IsVisible = full;   // window grows/shrinks to fit via SizeToContent

        foreach (var t in ModTags) SetModState(t, ModState.Off, updateLabels: false);
        UpdateKeys();
        AnchorToMode();
    }

    private void CycleMode()
    {
        currentMode = currentMode switch
        {
            KeyboardMode.Auto => KeyboardMode.Show,
            KeyboardMode.Show => KeyboardMode.Hide,
            _ => KeyboardMode.Auto,
        };
        ApplyMode();
        SaveSettings();
    }

    private void ApplyMode()
    {
        _hideTimer.Stop();
        UpdateGeometry();
        if (!_bodyVisible) _themePopup.IsOpen = false;
        if (currentMode == KeyboardMode.Hide) TrimWorkingSet();
        UpdateModeButton();
    }

    private static TextBlock MakeModeLabel(IBrush fg, double dx, double dy) => new()
    {
        Text = "Auto",
        FontSize = 15,
        FontWeight = FontWeight.SemiBold,
        Foreground = fg,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        RenderTransform = new TranslateTransform(dx, dy),
        IsHitTestVisible = false,
    };

    private void UpdateModeButton()
    {
        string text = currentMode switch { KeyboardMode.Show => "Show", KeyboardMode.Hide => "Hide", _ => "Auto" };
        if (_modeText != null) _modeText.Text = text;
        foreach (var t in _modeOutline) t.Text = text;
    }

    // --- Theme ---------------------------------------------------------------
    private void ApplyTheme()
    {
        if (_mainBorder == null) return;
        double b = currentBrightness;
        Color panel = HsvToColor(currentHue, currentSat, Math.Min(1.0, PanelValue * b));
        Color key = HsvToColor(currentHue, currentSat, Math.Min(1.0, KeyValue * b));
        Color border = HsvToColor(currentHue, currentSat, Math.Min(1.0, BorderValue * b));

        _mainBorder.Background = ThemeBrush(panel, currentOpacity);
        _mainBorder.BorderBrush = ThemeBrush(border, currentOpacity);

        var keyBrush = ThemeBrush(key, currentOpacity);
        foreach (var k in _allKeys) k.SetThemeBackground(keyBrush);
        if (_modeBg != null) _modeBg.Background = keyBrush;
    }

    private static IBrush ThemeBrush(Color c, double opacity) => new ImmutableSolidColorBrush(c, opacity);

    private static Color HsvToColor(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;
        double r, g, bl;
        if (h < 60) { r = c; g = x; bl = 0; }
        else if (h < 120) { r = x; g = c; bl = 0; }
        else if (h < 180) { r = 0; g = c; bl = x; }
        else if (h < 240) { r = 0; g = x; bl = c; }
        else if (h < 300) { r = x; g = 0; bl = c; }
        else { r = c; g = 0; bl = x; }
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((bl + m) * 255));
    }

    // --- Settings persistence ------------------------------------------------
    private void SaveSettings()
    {
        if (_loading || !_settingsLoaded) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveSettingsNow()
    {
        if (!_settingsLoaded) return;
        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            if (key == null) return;
            key.SetValue("Hue", currentHue.ToString(inv), RegistryValueKind.String);
            key.SetValue("Sat", currentSat.ToString(inv), RegistryValueKind.String);
            key.SetValue("Brightness", currentBrightness.ToString(inv), RegistryValueKind.String);
            key.SetValue("Opacity", currentOpacity.ToString(inv), RegistryValueKind.String);
            key.SetValue("Layout", currentLayoutState, RegistryValueKind.DWord);
            key.SetValue("Size", currentSizeState, RegistryValueKind.DWord);
            key.SetValue("Mode", (int)currentMode, RegistryValueKind.DWord);
            key.SetValue("RunOnStartup", runOnStartup ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex) { Debug.WriteLine($"SaveSettings failed: {ex.Message}"); }
    }

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            double savedHue = ReadDouble(key, "Hue", 0.0);
            double savedSat = ReadDouble(key, "Sat", 0.0);
            double savedBrightness = ReadDouble(key, "Brightness", 1.0);
            double savedOpacity = ReadDouble(key, "Opacity", 1.0);
            int savedLayout = key?.GetValue("Layout") is int l ? l : 0;
            int savedSize = key?.GetValue("Size") is int sz ? sz : 1;
            int savedMode = key?.GetValue("Mode") is int m ? m : 0;

            _hueSlider.Value = savedHue;
            _brightnessSlider.Value = savedBrightness * 100.0;
            _opacitySlider.Value = savedOpacity * 100.0;
            _sizeSlider.Value = Math.Clamp(savedSize, 0, 2);

            currentHue = savedHue;
            currentSat = savedSat;
            currentBrightness = savedBrightness;
            currentOpacity = savedOpacity;
            _brightnessLabel.Text = $"Brightness: {(int)(savedBrightness * 100)}%";
            _opacityLabel.Text = $"Background opacity: {(int)(savedOpacity * 100)}%";
            ApplyTheme();

            SetLayout(Math.Clamp(savedLayout, 0, 1));
            SetSize(Math.Clamp(savedSize, 0, 2));

            currentMode = (KeyboardMode)Math.Clamp(savedMode, 0, 2);
            ApplyMode();

            runOnStartup = IsRunOnStartupEnabled();
            UpdateStartupButton();
        }
        catch (Exception ex) { Debug.WriteLine($"LoadSettings failed: {ex.Message}"); }
        finally { _loading = false; _settingsLoaded = true; }
    }

    private static double ReadDouble(RegistryKey? key, string name, double fallback)
    {
        if (key?.GetValue(name) is string s &&
            double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v))
            return v;
        return fallback;
    }

    private bool IsRunOnStartupEnabled()
    {
        try { using var run = Registry.CurrentUser.OpenSubKey(RunKey); return run?.GetValue(RunValue) != null; }
        catch { return false; }
    }

    private void SetRunOnStartup(bool enable)
    {
        runOnStartup = enable;
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (run == null) return;
            if (enable)
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) run.SetValue(RunValue, $"\"{exe}\"", RegistryValueKind.String);
            }
            else if (run.GetValue(RunValue) != null) run.DeleteValue(RunValue, false);
        }
        catch (Exception ex) { Debug.WriteLine($"SetRunOnStartup failed: {ex.Message}"); }
        UpdateStartupButton();
    }

    private void UpdateStartupButton()
    {
        if (_startupText != null) _startupText.Text = runOnStartup ? "Run on startup: On" : "Run on startup: Off";
    }

    // --- Opt-in perf logging -------------------------------------------------
    private static bool DiagEnabled()
    {
        if (Environment.GetEnvironmentVariable("DESKTOPKEYBOARD_DIAG") == "1") return true;
        try { using var k = Registry.CurrentUser.OpenSubKey(SettingsKey); return k?.GetValue("Diag") is int d && d == 1; }
        catch { return false; }
    }

    private void StartPerfLog()
    {
        if (!DiagEnabled()) return;
        _perfLogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DesktopKeyboard_perf.log");
        using (var proc = Process.GetCurrentProcess()) _perfLastCpu = proc.TotalProcessorTime;
        _perfLastTick = Environment.TickCount64;
        PerfWrite($"--- session start (Avalonia), cores={Environment.ProcessorCount} ---");
        _perfTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _perfTimer.Tick += PerfLog_Tick;
        _perfTimer.Start();
    }

    private void PerfLog_Tick(object? sender, EventArgs e)
    {
        using var proc = Process.GetCurrentProcess();
        long now = Environment.TickCount64;
        double wallMs = now - _perfLastTick;
        TimeSpan cpu = proc.TotalProcessorTime;
        double cpuMs = (cpu - _perfLastCpu).TotalMilliseconds;
        _perfLastCpu = cpu; _perfLastTick = now;
        double cpuPct = wallMs > 0 ? cpuMs / (wallMs * Environment.ProcessorCount) * 100.0 : 0;
        double wsMb = proc.WorkingSet64 / (1024.0 * 1024.0);
        double gcMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        PerfWrite($"cpu={cpuPct,5:F1}%  ws={wsMb,6:F1}MB  gcHeap={gcMb,5:F1}MB  visible={(_bodyVisible ? 1 : 0)}");
    }

    private void PerfWrite(string body)
    {
        try { System.IO.File.AppendAllText(_perfLogPath!, $"{DateTime.Now:HH:mm:ss}  {body}{Environment.NewLine}"); }
        catch { }
    }

    // --- Key input -----------------------------------------------------------
    // Keys fire on press (responsive, and enables auto-repeat); modifiers toggle on release.
    private void OnKeyPressed(Key k)
    {
        string tag = k.KeyTag;

        // Any tap on the keyboard must not let a transient focus change hide it. Interacting
        // with the OSK can briefly perturb UIA focus (and some apps blur on external input);
        // refreshed on every press, so continuous typing always stays visible, while a real
        // focus-away still hides shortly after you stop. Belt to the own-process ignore.
        _suppressHideUntil = Environment.TickCount64 + 1500;
        _hideTimer.Stop();

        if (_mod.ContainsKey(tag)) { StartLongPress(tag); return; }

        // Normal key: send now, then auto-repeat while held (so Backspace/Del clear quickly).
        SendTag(tag);
        _repeatTag = tag;
        _repeatTimer.Interval = TimeSpan.FromMilliseconds(400);
        _repeatTimer.Start();
    }

    private void OnKeyReleased(Key k)
    {
        string tag = k.KeyTag;
        if (_mod.ContainsKey(tag))
        {
            StopLongPress();
            if (_longPressFired && tag == _longPressTag) { _longPressFired = false; return; }
            SetModState(tag, _mod[tag] == ModState.Off ? ModState.OneShot : ModState.Off);
            return;
        }
        if (_repeatTag == tag) { _repeatTimer.Stop(); _repeatTag = null; }
    }

    private void SendTag(string tag)
    {
        byte vk = GetVirtualKeyCode(tag);
        if (_mod["TOGGLE_FN"] != ModState.Off && FnMap.TryGetValue(tag, out var fm)) vk = fm.Vk;
        if (vk != 0) SendKey(vk);
    }

    private static INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 },
    };

    // Single key event (used to hold/release locked modifiers like a physically held key).
    private void SendRaw(ushort vk, bool down)
    {
        _inputBuf[0] = KeyInput(vk, up: !down);
        SendInput(1, _inputBuf, InputSize);
    }

    private void SendKey(byte vk)
    {
        // Only wrap ONE-SHOT modifiers here; locked ones are already physically held down.
        bool ctrl = _mod["TOGGLE_CTRL"] == ModState.OneShot;
        bool alt = _mod["TOGGLE_ALT"] == ModState.OneShot;
        bool shift = _mod["TOGGLE_SHIFT"] == ModState.OneShot;
        bool win = _mod["TOGGLE_WIN"] == ModState.OneShot;

        int n = 0;
        if (ctrl) _inputBuf[n++] = KeyInput(VK_LCONTROL, false);
        if (alt) _inputBuf[n++] = KeyInput(VK_LMENU, false);
        if (shift) _inputBuf[n++] = KeyInput(VK_LSHIFT, false);
        if (win) _inputBuf[n++] = KeyInput(VK_LWIN, false);
        _inputBuf[n++] = KeyInput(vk, false);
        _inputBuf[n++] = KeyInput(vk, true);
        if (win) _inputBuf[n++] = KeyInput(VK_LWIN, true);
        if (shift) _inputBuf[n++] = KeyInput(VK_LSHIFT, true);
        if (alt) _inputBuf[n++] = KeyInput(VK_LMENU, true);
        if (ctrl) _inputBuf[n++] = KeyInput(VK_LCONTROL, true);

        SendInput((uint)n, _inputBuf, InputSize);
        ConsumeOneShotModifiers();
    }

    // --- Modifier state ------------------------------------------------------
    private static readonly Dictionary<string, ushort> ModVk = new()
    {
        ["TOGGLE_SHIFT"] = VK_LSHIFT, ["TOGGLE_CTRL"] = VK_LCONTROL,
        ["TOGGLE_ALT"] = VK_LMENU, ["TOGGLE_WIN"] = VK_LWIN,   // Fn is local-only (no real key)
    };

    private void SetModState(string tag, ModState state, bool updateLabels = true)
    {
        if (!_mod.ContainsKey(tag)) return;
        ModState old = _mod[tag];
        _mod[tag] = state;

        // Locked behaves like physically holding the key: press it down on lock, release on
        // unlock. One-shot stays virtual (wrapped per-key in SendKey).
        if (ModVk.TryGetValue(tag, out var vk))
        {
            if (state == ModState.Locked && old != ModState.Locked) SendRaw(vk, down: true);
            else if (old == ModState.Locked && state != ModState.Locked) SendRaw(vk, down: false);
        }

        if (_byTag.TryGetValue(tag, out var keys))
        {
            IBrush? ov = state switch { ModState.OneShot => ActiveBrush, ModState.Locked => LockBrush, _ => null };
            foreach (var k in keys) k.SetOverride(ov);
        }

        if (updateLabels && (tag == "TOGGLE_SHIFT" || tag == "TOGGLE_FN")) UpdateKeys();
    }

    private void ConsumeOneShotModifiers()
    {
        foreach (var t in ModTags)
            if (_mod[t] == ModState.OneShot) SetModState(t, ModState.Off);
    }

    private void StartLongPress(string? tag)
    {
        if (tag == null) return;
        if (_longPressActive && _longPressTag == tag) return;
        _longPressTag = tag;
        _longPressActive = true;
        _longPressFired = false;
        _longPressTimer.Stop();
        _longPressTimer.Start();
    }

    private void StopLongPress()
    {
        _longPressActive = false;
        _longPressTimer.Stop();
    }

    private void LongPress_Tick(object? sender, EventArgs e)
    {
        _longPressTimer.Stop();
        _longPressActive = false;
        if (_longPressTag != null)
        {
            _longPressFired = true;
            SetModState(_longPressTag, ModState.Locked);
        }
    }

    private void UpdateKeys()
    {
        bool isShifted = _mod["TOGGLE_SHIFT"] != ModState.Off;
        bool isFn = _mod["TOGGLE_FN"] != ModState.Off;

        foreach (var k in _allKeys)
        {
            string tag = k.KeyTag;
            if (tag == "BACK") k.SetLabel(isFn ? "Del" : "⌫");
            else if (isFn && FnMap.TryGetValue(tag, out var fm)) k.SetLabel(fm.Label);
            else if (tag.Length == 1 && tag[0] is >= 'A' and <= 'Z') k.SetLabel(isShifted ? tag : LowerLetter[tag[0] - 'A']);
            else if (tag.Length == 1 && tag[0] is >= '0' and <= '9') k.SetLabel(isShifted ? ShiftedDigit[tag[0] - '0'] : tag);
            else if (Punct.TryGetValue(tag, out var p)) k.SetLabel(isShifted ? p.Shifted : p.Normal);
        }
    }

    // --- Lookup tables -------------------------------------------------------
    private static readonly Dictionary<string, (string Normal, string Shifted)> Punct = new()
    {
        ["COMMA"] = (",", "<"), ["PERIOD"] = (".", ">"), ["SLASH"] = ("/", "?"),
        ["GRAVE"] = ("`", "~"), ["MINUS"] = ("-", "_"), ["EQUALS"] = ("=", "+"), ["BACKSLASH"] = ("\\", "|"),
    };

    private static readonly Dictionary<string, byte> Vk = new()
    {
        ["COMMA"] = 0xBC, ["PERIOD"] = 0xBE, ["SLASH"] = 0xBF, ["GRAVE"] = 0xC0,
        ["MINUS"] = 0xBD, ["EQUALS"] = 0xBB, ["BACKSLASH"] = 0xDC,
        ["BACK"] = 0x08, ["TAB"] = 0x09, ["ENTER"] = 0x0D, ["ESC"] = 0x1B, ["SPACE"] = 0x20,
        ["LEFT"] = 0x25, ["UP"] = 0x26, ["RIGHT"] = 0x27, ["DOWN"] = 0x28,
        ["PGUP"] = 0x21, ["PGDN"] = 0x22, ["END"] = 0x23, ["HOME"] = 0x24,
        ["NUMLK"] = 0x90, ["NUMSLASH"] = 0x6F, ["NUMSTAR"] = 0x6A, ["NUMMINUS"] = 0x6D, ["NUMPLUS"] = 0x6B,
        ["NUM0"] = 0x60, ["NUM1"] = 0x61, ["NUM2"] = 0x62, ["NUM3"] = 0x63, ["NUM4"] = 0x64,
        ["NUM5"] = 0x65, ["NUM6"] = 0x66, ["NUM7"] = 0x67, ["NUM8"] = 0x68, ["NUM9"] = 0x69, ["NUMDOT"] = 0x6E,
    };

    private static readonly Dictionary<string, (byte Vk, string Label)> FnMap = new()
    {
        ["1"] = (0x70, "F1"), ["2"] = (0x71, "F2"), ["3"] = (0x72, "F3"), ["4"] = (0x73, "F4"),
        ["5"] = (0x74, "F5"), ["6"] = (0x75, "F6"), ["7"] = (0x76, "F7"), ["8"] = (0x77, "F8"),
        ["9"] = (0x78, "F9"), ["0"] = (0x79, "F10"), ["MINUS"] = (0x7A, "F11"), ["EQUALS"] = (0x7B, "F12"),
        ["BACK"] = (0x2E, "Del"),
    };

    private static readonly string[] LowerLetter = BuildLowerLetters();
    private static readonly string[] ShiftedDigit = { ")", "!", "@", "#", "$", "%", "^", "&", "*", "(" };

    private static string[] BuildLowerLetters()
    {
        var a = new string[26];
        for (int i = 0; i < 26; i++) a[i] = ((char)('a' + i)).ToString();
        return a;
    }

    private static byte GetVirtualKeyCode(string keyTag)
    {
        if (keyTag.Length == 1)
        {
            char c = keyTag[0];
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return (byte)c;
        }
        return Vk.TryGetValue(keyTag, out byte v) ? v : (byte)0;
    }
}
