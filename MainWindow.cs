using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")] static extern IntPtr GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLong(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("psapi.dll")] static extern int EmptyWorkingSet(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, char[] name, int max);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    private const uint GA_ROOT = 2;

    // -1 = the GetCurrentProcess pseudo-handle
    private static void TrimWorkingSet() { try { EmptyWorkingSet(new IntPtr(-1)); } catch { } }

    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_LSHIFT = 0xA0, VK_LCONTROL = 0xA2, VK_LMENU = 0xA4, VK_LWIN = 0x5B;

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public KEYBDINPUT ki; public int _a, _b; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();
    private readonly INPUT[] _inputBuf = new INPUT[10];

    // --- Theme model ---------------------------------------------------------
    private const double PanelValue = 0.067, KeyValue = 0.145, BorderValue = 0.200;
    private double currentHue, currentSat, currentOpacity = 1.0, currentBrightness = 1.0;
    private int currentSizeState = 1, currentLayoutState = 0;

    // --- Modifier state machine ---------------------------------------------
    private enum ModState { Off, OneShot, Locked }

    // One modifier key's state + virtual key. Vk 0 = local-only (Fn remaps rows, no real key).
    private sealed class Mod(string tag, ushort vk)
    {
        public readonly string Tag = tag;
        public readonly ushort Vk = vk;
        public ModState State;
    }

    private readonly Mod _ctrl = new("TOGGLE_CTRL", VK_LCONTROL), _alt = new("TOGGLE_ALT", VK_LMENU),
                         _shift = new("TOGGLE_SHIFT", VK_LSHIFT), _win = new("TOGGLE_WIN", VK_LWIN),
                         _fn = new("TOGGLE_FN", 0);
    private readonly Mod[] _mods;   // also SendKey's wrap order: one-shots go down in this order, up in reverse
    private Mod? GetMod(string tag) => Array.Find(_mods, m => m.Tag == tag);

    private readonly DispatcherTimer _longPressTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private Mod? _longPress;
    private bool _longPressFired;

    // Auto-repeat for a held normal key (typematic): initial delay then fast repeat.
    private readonly DispatcherTimer _repeatTimer = new();
    private string? _repeatTag;

    // --- Mode (Auto/Show/Hide) + floating button -----------------------------
    private enum KeyboardMode { Auto, Show, Hide }
    private KeyboardMode currentMode = KeyboardMode.Auto;
    private Border _modeBg = null!;       // the mode button, a child of this window (no 2nd window)
    private OutlinedText _modeLabel = null!;
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
    private FocusHandler? _focusHandler; // roots the COM focus-event sink for the app's lifetime
    private bool _chromeExpanded = true; // Esc + theme/layout/close shown (vs. collapsed behind the toggle)
    private StackPanel _bodyRow = null!;          // keyboard + numpad; collapses when hidden
    private Key _escKey = null!;
    private StackPanel _chromeGroup = null!;       // theme / layout / close
    private LayoutTransformControl _scaler = null!;
    private readonly ScaleTransform _scale = new(1, 1);   // size preset -> key size; window fits via SizeToContent

    private bool runOnStartup, _loading, _settingsLoaded;
    private long _suppressHideUntil;

    internal const string SettingsKey = @"Software\serifpersia\DesktopKeyboard";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DesktopKeyboard";

    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

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
        _mods = new[] { _ctrl, _alt, _shift, _win, _fn };

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
            Diag.Init();
            Show();                     // always visible; shrinks to the mode button when hidden
            InitDefaultPosition();
            LoadSettings();
            UpdateGeometry();
            Diag.StartPerfLog(() => _bodyVisible);
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

        _bodyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { BuildKeyboard(), BuildNav(), _numpadGrid },
        };

        _mainBorder = new Border
        {
            Background = new ImmutableSolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)),
            CornerRadius = new CornerRadius(8),
            BorderBrush = Palette.Outline,
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(8),
                Children = { BuildTopBar(), _bodyRow },
            },
        };

        _scaler = new LayoutTransformControl { LayoutTransform = _scale, Child = _mainBorder };
        return _scaler;
    }

    // Builds the mode button (a top-row cell). Tap cycles the mode; drag moves the window.
    private Control BuildModeButton()
    {
        _modeLabel = new OutlinedText("Auto", 15, FontWeight.SemiBold);
        _modeBg = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = Palette.Button,
            Cursor = new Cursor(StandardCursorType.Hand),
            Width = ModeBtnW,
            Height = ModeBtnH,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _modeLabel,
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
        _chromeGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { themeBtn, layoutBtn, ChromeButton("✕", 20, RequestClose) },
        };

        var cluster = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { mode, toggle, _chromeGroup },
        };

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

        var reset = ChromeButton("Reset to grey", 16, () =>
        {
            _hueSlider.Value = 0; currentHue = 0; currentSat = 0; ApplyTheme(); SaveSettings();
        });
        reset.Background = Palette.Button;
        reset.Height = 44; reset.Margin = new Thickness(0, 12, 0, 0);

        var startup = ChromeButton("Run on startup: Off", 16, () => { SetRunOnStartup(!runOnStartup); SaveSettings(); });
        startup.Background = Palette.Button;
        startup.Height = 44; startup.Margin = new Thickness(0, 8, 0, 0);
        _startupText = (TextBlock)startup.Child!;

        return new Border
        {
            Background = Palette.Panel,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            BorderBrush = Palette.Outline,
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Width = 340,
                Children =
                {
                    Label("Colour"), _hueSlider,
                    _brightnessLabel, _brightnessSlider,
                    _opacityLabel, _opacitySlider,
                    _sizeLabel, _sizeSlider,
                    reset, startup,
                },
            },
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

        return new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(6, 0, 0, 0),
            Children = { top, arrows },
        };
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
        foreach (var m in _mods) if (m.State == ModState.Locked) SetModState(m, ModState.Off);
        base.OnClosing(e);
    }

    private static void MakeTopmost(IntPtr h)
    {
        if (h != IntPtr.Zero) SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
    }

    private static void NoActivate(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        SetWindowLong(h, GWL_EXSTYLE, new IntPtr(GetWindowLong(h, GWL_EXSTYLE).ToInt64() | WS_EX_NOACTIVATE));
        MakeTopmost(h);
    }

    // --- Focus-driven show/hide (UI Automation) ------------------------------
    // Registered and polled from a dedicated MTA thread, per Microsoft's UI Automation
    // client threading guidance: event-handler threads should be MTA, not the app's own
    // STA UI thread, or event delivery/unregistration can misbehave.
    private void RegisterFocusTracking()
    {
        var thread = new Thread(FocusTrackingThreadProc) { IsBackground = true, Name = "UIAFocusTracking" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    // Property IDs cached on every focused element (one cross-process fetch per focus
    // change) so classification below is a pure local read — no per-property round trips.
    private static readonly int[] _cacheProps =
    {
        Prop.ControlType, Prop.Name, Prop.Enabled, Prop.Password,
        Prop.HasValue, Prop.ValueReadOnly, Prop.HasTextEdit,
        Prop.HasLegacy, Prop.LegacyRole, Prop.LegacyState, Prop.Hwnd, Prop.Pid,
    };

    // True when the focused element belongs to our own keyboard window. Tapping a key can
    // briefly move UIA focus onto the OSK; treating that as "focus left the text field"
    // would hide the keyboard mid-type, so these focus changes must be ignored outright.
    private static bool IsOwnProcess(UIA.IUIAutomationElement element)
    {
        int pid = GetCachedInt(element, Prop.Pid);
        return pid != 0 && pid == Environment.ProcessId;
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
        Diag.Focus($"=== focus tracking start (native UIA) pid={Environment.ProcessId} built={Diag.BuildStamp()} ===");

        UIA.IUIAutomation uia;
        UIA.IUIAutomationCacheRequest cache;
        try
        {
            uia = (UIA.IUIAutomation)new UIA.CUIAutomation8();

            // IUIAutomation6 (Win10 1809+) hardens the shared event pipe: recover the
            // connection if a provider crashes, and coalesce event storms instead of
            // letting one chatty app stall focus delivery for every UIA client.
            if (uia is UIA.IUIAutomation6 uia6)
            {
                try
                {
                    uia6.ConnectionRecoveryBehavior = UIA.ConnectionRecoveryBehaviorOptions.ConnectionRecoveryBehaviorOptions_Enabled;
                    uia6.CoalesceEvents = UIA.CoalesceEventsOptions.CoalesceEventsOptions_Enabled;
                }
                catch (Exception ex) { Diag.Focus("IUIAutomation6 knobs failed: " + ex.Message); }
            }

            cache = uia.CreateCacheRequest();
            foreach (int p in _cacheProps) cache.AddProperty(p);

            _focusHandler = new FocusHandler(this);
            uia.AddFocusChangedEventHandler(cache, _focusHandler);
            Diag.Focus("focus-changed handler registered — waiting for events");
        }
        catch (Exception ex)
        {
            // no UIA available — auto mode silently degrades to manual
            Diag.Focus("COM init FAILED (auto show/hide disabled): " + ex);
            return;
        }

        // Safety-net poll: event delivery can be missed (a Chromium tab's accessibility
        // tree not yet warmed up, a dropped WinEvent, another process's UIA provider
        // stalling the shared event pipe) so periodically reconcile against the actual
        // focused element rather than trusting the event alone.
        while (true)
        {
            if (currentMode == KeyboardMode.Auto)
            {
                try { ClassifyAndApply(uia.GetFocusedElementBuildCache(cache), "poll", dedupLog: true); }
                catch (Exception ex) { Diag.Focus("[poll] error: " + ex.Message); }
            }
            Thread.Sleep(1000);
        }
    }

    // The COM sink UIA calls (on its own MTA thread) for every system-wide focus change.
    private sealed class FocusHandler(MainWindow owner) : UIA.IUIAutomationFocusChangedEventHandler
    {
        public void HandleFocusChangedEvent(UIA.IUIAutomationElement? sender)
        {
            if (owner.currentMode != KeyboardMode.Auto) return;
            if (Diag.On && sender != null) Diag.Focus("[event]" + Environment.NewLine + Diag.Snapshot(sender));
            owner.ClassifyAndApply(sender, "event");
        }
    }

    // Shared tail of the event and poll paths: classify a cached element that isn't our own
    // window, then post the show/hide decision to the UI thread. dedupLog makes the 1 Hz
    // poll only log when the picture changes, so it doesn't flood the focus log.
    private bool? _lastPollEditable;
    private int _lastPollCt = int.MinValue;
    private void ClassifyAndApply(UIA.IUIAutomationElement? element, string source, bool dedupLog = false)
    {
        if (element == null) { if (!dedupLog) Diag.Focus($"[{source}] sender=null"); return; }
        if (IsOwnProcess(element)) { if (!dedupLog) Diag.Focus($"[{source}] ignored (own keyboard window)"); return; }

        bool editable;
        string reason;
        try { editable = IsEditableTextField(element, out reason); }
        catch (Exception ex) { Diag.Focus($"[{source}] error: {ex.Message}"); return; }

        if (Diag.On)
        {
            int ct = GetCachedInt(element, Prop.ControlType, -1);
            if (!dedupLog || editable != _lastPollEditable || ct != _lastPollCt)
                Diag.Focus($"[{source}] ct={Diag.CtName(ct)} -> editable={editable} ({reason})");
            if (dedupLog) { _lastPollEditable = editable; _lastPollCt = ct; }
        }

        Dispatcher.UIThread.Post(() => ApplyFocusVisibility(editable));
    }

    private void ApplyFocusVisibility(bool editable)
    {
        if (editable)
        {
            _hideTimer.Stop();
            _focusEditable = true;
            if (!_bodyVisible) { Diag.Focus("[apply] editable -> SHOW"); UpdateGeometry(); }
        }
        else if (_bodyVisible)
        {
            if (Environment.TickCount64 < _suppressHideUntil) return;
            Diag.Focus("[apply] not editable -> start hide timer");
            _hideTimer.Start();
        }
    }

    // MSAA role/state bits (oleacc.h) exposed via the LegacyIAccessible pattern.
    private const int ROLE_SYSTEM_TEXT = 0x2A;
    private const int STATE_SYSTEM_UNAVAILABLE = 0x1;
    private const int STATE_SYSTEM_READONLY = 0x40;

    // Control types that never take text entry. Slider/ProgressBar are here specifically
    // because they expose a (settable) ValuePattern and would otherwise false-positive at
    // rule 4 below.
    private static readonly int[] HardNegativeCts =
    {
        Ct.Button, Ct.CheckBox, Ct.RadioButton, Ct.MenuItem, Ct.TabItem, Ct.Hyperlink,
        Ct.Image, Ct.ScrollBar, Ct.ListItem, Ct.TreeItem, Ct.Slider, Ct.ProgressBar,
        Ct.Menu, Ct.MenuBar, Ct.ToolBar, Ct.TitleBar, Ct.Tree, Ct.List, Ct.Tab,
    };

    // Ordered strongest-and-cheapest first. Reads are all against the cached element.
    // `reason` names the rule that decided, for the diagnostic log.
    private static bool IsEditableTextField(UIA.IUIAutomationElement element, out string reason)
    {
        int ct = GetCachedInt(element, Prop.ControlType, -1);
        if (ct == -1) { reason = "no control type"; return false; }

        // 1. Hard negatives: control types that never take text entry.
        if (Array.IndexOf(HardNegativeCts, ct) >= 0) { reason = "rule1 hard-negative control type"; return false; }

        // 2. Disabled elements can't take input (default when unsupported is enabled).
        if (!GetCachedBool(element, Prop.Enabled, defaultValue: true)) { reason = "rule2 disabled"; return false; }

        // 3. Password field: unambiguously editable, and often hides its ValuePattern value.
        if (GetCachedBool(element, Prop.Password)) { reason = "rule3 password"; return true; }

        // 4. ValuePattern is the primary oracle for text-bearing controls: Core-AAM makes
        //    editable Edit/Document expose it, and IsReadOnly is authoritative (this both
        //    accepts multi-line web editors and rejects read-only inputs and page content).
        if (GetCachedBool(element, Prop.HasValue))
        {
            if (IsLikelyFileName(GetCachedString(element, Prop.Name))) { reason = "rule4 value but filename-like name"; return false; }
            bool ro = GetCachedBool(element, Prop.ValueReadOnly);
            reason = ro ? "rule4 value read-only" : "rule4 value editable";
            return !ro;
        }

        // 5. TextEdit pattern: raised by genuinely editable text surfaces (composition /
        //    text-edit events) — a stronger positive than plain TextPattern, which read-only
        //    documents also expose and which caused the original browser false-positives.
        if (GetCachedBool(element, Prop.HasTextEdit)) { reason = "rule5 textedit pattern"; return true; }

        // 6. LegacyIAccessible (MSAA/IA2 bridge, rich in Chrome/Firefox): editable text is
        //    role TEXT without the read-only/unavailable state bits.
        if (GetCachedBool(element, Prop.HasLegacy) &&
            GetCachedInt(element, Prop.LegacyRole, -1) == ROLE_SYSTEM_TEXT &&
            (GetCachedInt(element, Prop.LegacyState, -1) & (STATE_SYSTEM_READONLY | STATE_SYSTEM_UNAVAILABLE)) == 0)
        {
            reason = "rule6 legacy role=TEXT";
            return true;
        }

        // 7. Inherent text controls that exposed no usable pattern (non-conformant
        //    providers): Edit/ComboBox mean text entry, so give the benefit of the doubt.
        //    Deliberately NOT Document — a Document with no editable signal is read-only page
        //    content, the exact false-positive this classification exists to avoid.
        if (ct == Ct.Edit || ct == Ct.ComboBox) { reason = "rule7 edit/combobox fallback"; return true; }

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

        reason = "default no editable signal" + (cls.Length > 0 ? $" (hostClass={cls})" : "");
        return false;
    }

    // Class name of the window hosting an element: prefer the element's own native window
    // handle, falling back to the foreground window (our keyboard is WS_EX_NOACTIVATE, so
    // it never becomes foreground and can't mask the app being typed into).
    internal static string GetHostClass(UIA.IUIAutomationElement element)
    {
        IntPtr hwnd = new(GetCachedInt(element, Prop.Hwnd));
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

    // Cached-property readers: GetCachedPropertyValue hands back a non-matching sentinel
    // (or throws) when a provider doesn't supply the property, so fall to the default.
    internal static bool GetCachedBool(UIA.IUIAutomationElement element, int propertyId, bool defaultValue = false)
    {
        try { return element.GetCachedPropertyValue(propertyId) is bool b ? b : defaultValue; }
        catch { return defaultValue; }
    }

    // Int-valued properties arrive as int or long depending on the provider (e.g. HWNDs).
    internal static int GetCachedInt(UIA.IUIAutomationElement element, int propertyId, int defaultValue = 0)
    {
        try { return element.GetCachedPropertyValue(propertyId) switch { int i => i, long l => (int)l, _ => defaultValue }; }
        catch { return defaultValue; }
    }

    internal static string GetCachedString(UIA.IUIAutomationElement element, int propertyId)
    {
        try { return element.GetCachedPropertyValue(propertyId) as string ?? ""; }
        catch { return ""; }
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
        var dlg = new Window
        {
            SystemDecorations = SystemDecorations.BorderOnly,
            Title = "Desktop Keyboard",
            Width = 280, Height = 120,
            CanResize = false,
            Topmost = true,   // above the topmost keyboard
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Palette.Panel,
        };
        var yes = ChromeButton("Close", 16, () => { tcs.TrySetResult(true); dlg.Close(); });
        var no = ChromeButton("Cancel", 16, () => { tcs.TrySetResult(false); dlg.Close(); });
        yes.Background = Palette.Accent;
        no.Background = new ImmutableSolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        yes.Width = no.Width = 90; yes.Height = no.Height = 40;
        yes.Margin = no.Margin = new Thickness(6, 0, 6, 0);

        dlg.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = "Close Desktop Keyboard?", Foreground = Brushes.White, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { yes, no } },
            },
        };
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

        foreach (var m in _mods) SetModState(m, ModState.Off, updateLabels: false);
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

    private void UpdateModeButton() =>
        _modeLabel.Text = currentMode switch { KeyboardMode.Show => "Show", KeyboardMode.Hide => "Hide", _ => "Auto" };

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
            var inv = CultureInfo.InvariantCulture;
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
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
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

        var mod = GetMod(tag);
        if (mod != null) { StartLongPress(mod); return; }

        // Normal key: send now, then auto-repeat while held (so Backspace/Del clear quickly).
        SendTag(tag);
        _repeatTag = tag;
        _repeatTimer.Interval = TimeSpan.FromMilliseconds(400);
        _repeatTimer.Start();
    }

    private void OnKeyReleased(Key k)
    {
        string tag = k.KeyTag;
        var mod = GetMod(tag);
        if (mod != null)
        {
            _longPressTimer.Stop();
            if (_longPressFired && mod == _longPress) { _longPressFired = false; return; }
            SetModState(mod, mod.State == ModState.Off ? ModState.OneShot : ModState.Off);
            return;
        }
        if (_repeatTag == tag) { _repeatTimer.Stop(); _repeatTag = null; }
    }

    private void SendTag(string tag)
    {
        byte vk = GetVirtualKeyCode(tag);
        if (_fn.State != ModState.Off && FnMap.TryGetValue(tag, out var fm)) vk = fm.Vk;
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
        int n = 0;
        foreach (var m in _mods)
            if (m.State == ModState.OneShot && m.Vk != 0) _inputBuf[n++] = KeyInput(m.Vk, false);
        _inputBuf[n++] = KeyInput(vk, false);
        _inputBuf[n++] = KeyInput(vk, true);
        for (int i = _mods.Length - 1; i >= 0; i--)
            if (_mods[i].State == ModState.OneShot && _mods[i].Vk != 0) _inputBuf[n++] = KeyInput(_mods[i].Vk, true);

        SendInput((uint)n, _inputBuf, InputSize);
        ConsumeOneShotModifiers();
    }

    // --- Modifier state ------------------------------------------------------
    private void SetModState(Mod m, ModState state, bool updateLabels = true)
    {
        ModState old = m.State;
        m.State = state;

        // Locked behaves like physically holding the key: press it down on lock, release on
        // unlock. One-shot stays virtual (wrapped per-key in SendKey).
        if (m.Vk != 0)
        {
            if (state == ModState.Locked && old != ModState.Locked) SendRaw(m.Vk, down: true);
            else if (old == ModState.Locked && state != ModState.Locked) SendRaw(m.Vk, down: false);
        }

        if (_byTag.TryGetValue(m.Tag, out var keys))
        {
            IBrush? ov = state switch { ModState.OneShot => Palette.Accent, ModState.Locked => Palette.ModLock, _ => null };
            foreach (var k in keys) k.SetOverride(ov);
        }

        if (updateLabels && (m == _shift || m == _fn)) UpdateKeys();
    }

    private void ConsumeOneShotModifiers()
    {
        foreach (var m in _mods)
            if (m.State == ModState.OneShot) SetModState(m, ModState.Off);
    }

    private void StartLongPress(Mod m)
    {
        _longPress = m;
        _longPressFired = false;
        _longPressTimer.Stop();
        _longPressTimer.Start();
    }

    private void LongPress_Tick(object? sender, EventArgs e)
    {
        _longPressTimer.Stop();
        if (_longPress != null)
        {
            _longPressFired = true;
            SetModState(_longPress, ModState.Locked);
        }
    }

    private void UpdateKeys()
    {
        bool isShifted = _shift.State != ModState.Off;
        bool isFn = _fn.State != ModState.Off;

        foreach (var k in _allKeys)
        {
            string tag = k.KeyTag;
            string label =
                isFn && FnMap.TryGetValue(tag, out var fm) ? fm.Label :
                tag.Length == 1 && tag[0] is >= 'A' and <= 'Z' ? (isShifted ? tag : char.ToLowerInvariant(tag[0]).ToString()) :
                tag.Length == 1 && tag[0] is >= '0' and <= '9' ? (isShifted ? ShiftedDigit[tag[0] - '0'] : tag) :
                Punct.TryGetValue(tag, out var p) ? (isShifted ? p.Shifted : p.Normal) :
                k.DefaultLabel;
            k.SetLabel(label);
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

    private static readonly string[] ShiftedDigit = { ")", "!", "@", "#", "$", "%", "^", "&", "*", "(" };

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

// Int aliases for the interop UIA property / control-type enum ids (every use would
// otherwise need an (int) cast of the full enum member name).
internal static class Prop
{
    public const int ControlType = (int)UIA.UIA_PropertyIds.UIA_ControlTypePropertyId;
    public const int Name = (int)UIA.UIA_PropertyIds.UIA_NamePropertyId;
    public const int Enabled = (int)UIA.UIA_PropertyIds.UIA_IsEnabledPropertyId;
    public const int Password = (int)UIA.UIA_PropertyIds.UIA_IsPasswordPropertyId;
    public const int HasValue = (int)UIA.UIA_PropertyIds.UIA_IsValuePatternAvailablePropertyId;
    public const int ValueReadOnly = (int)UIA.UIA_PropertyIds.UIA_ValueIsReadOnlyPropertyId;
    public const int HasTextEdit = (int)UIA.UIA_PropertyIds.UIA_IsTextEditPatternAvailablePropertyId;
    public const int HasLegacy = (int)UIA.UIA_PropertyIds.UIA_IsLegacyIAccessiblePatternAvailablePropertyId;
    public const int LegacyRole = (int)UIA.UIA_PropertyIds.UIA_LegacyIAccessibleRolePropertyId;
    public const int LegacyState = (int)UIA.UIA_PropertyIds.UIA_LegacyIAccessibleStatePropertyId;
    public const int Hwnd = (int)UIA.UIA_PropertyIds.UIA_NativeWindowHandlePropertyId;
    public const int Pid = (int)UIA.UIA_PropertyIds.UIA_ProcessIdPropertyId;
}

internal static class Ct
{
    public const int Edit = (int)UIA.UIA_ControlTypeIds.UIA_EditControlTypeId;
    public const int ComboBox = (int)UIA.UIA_ControlTypeIds.UIA_ComboBoxControlTypeId;
    public const int Button = (int)UIA.UIA_ControlTypeIds.UIA_ButtonControlTypeId;
    public const int CheckBox = (int)UIA.UIA_ControlTypeIds.UIA_CheckBoxControlTypeId;
    public const int RadioButton = (int)UIA.UIA_ControlTypeIds.UIA_RadioButtonControlTypeId;
    public const int MenuItem = (int)UIA.UIA_ControlTypeIds.UIA_MenuItemControlTypeId;
    public const int TabItem = (int)UIA.UIA_ControlTypeIds.UIA_TabItemControlTypeId;
    public const int Hyperlink = (int)UIA.UIA_ControlTypeIds.UIA_HyperlinkControlTypeId;
    public const int Image = (int)UIA.UIA_ControlTypeIds.UIA_ImageControlTypeId;
    public const int ScrollBar = (int)UIA.UIA_ControlTypeIds.UIA_ScrollBarControlTypeId;
    public const int ListItem = (int)UIA.UIA_ControlTypeIds.UIA_ListItemControlTypeId;
    public const int TreeItem = (int)UIA.UIA_ControlTypeIds.UIA_TreeItemControlTypeId;
    public const int Slider = (int)UIA.UIA_ControlTypeIds.UIA_SliderControlTypeId;
    public const int ProgressBar = (int)UIA.UIA_ControlTypeIds.UIA_ProgressBarControlTypeId;
    public const int Menu = (int)UIA.UIA_ControlTypeIds.UIA_MenuControlTypeId;
    public const int MenuBar = (int)UIA.UIA_ControlTypeIds.UIA_MenuBarControlTypeId;
    public const int ToolBar = (int)UIA.UIA_ControlTypeIds.UIA_ToolBarControlTypeId;
    public const int TitleBar = (int)UIA.UIA_ControlTypeIds.UIA_TitleBarControlTypeId;
    public const int Tree = (int)UIA.UIA_ControlTypeIds.UIA_TreeControlTypeId;
    public const int List = (int)UIA.UIA_ControlTypeIds.UIA_ListControlTypeId;
    public const int Tab = (int)UIA.UIA_ControlTypeIds.UIA_TabControlTypeId;
}
