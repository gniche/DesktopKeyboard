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

    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002,
                       KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_SCANCODE = 0x0008;
    private const ushort VK_LSHIFT = 0xA0, VK_LCONTROL = 0xA2, VK_LMENU = 0xA4, VK_LWIN = 0x5B,
                         VK_RCONTROL = 0xA3, VK_RMENU = 0xA5;

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public KEYBDINPUT ki; public int _a, _b; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();
    // Worst case: 6 one-shot modifier downs + key down/up + 6 modifier ups.
    private readonly INPUT[] _inputBuf = new INPUT[16];

    // Extended-bit keys (E0-prefixed scan codes) and their scans. Injected with wVk alone,
    // RCtrl/RAlt degrade to their left variants in many apps and nav keys can be misread as
    // numpad by scan-aware consumers, so these VKs always carry scan + KEYEVENTF_EXTENDEDKEY.
    private static readonly Dictionary<ushort, byte> ExtScan = new()
    {
        [0xA3] = 0x1D /*RCtrl*/, [0xA5] = 0x38 /*RAlt*/, [0x5B] = 0x5B /*LWin*/, [0x5D] = 0x5D /*Apps*/,
        [0x21] = 0x49 /*PgUp*/, [0x22] = 0x51 /*PgDn*/, [0x23] = 0x4F /*End*/, [0x24] = 0x47 /*Home*/,
        [0x25] = 0x4B /*←*/, [0x26] = 0x48 /*↑*/, [0x27] = 0x4D /*→*/, [0x28] = 0x50 /*↓*/,
        [0x2C] = 0x37 /*PrtSc*/, [0x2D] = 0x52 /*Ins*/, [0x2E] = 0x53 /*Del*/, [0x6F] = 0x35 /*Num-slash*/,
    };

    // --- Theme model ---------------------------------------------------------
    private const double PanelValue = 0.067, KeyValue = 0.145, BorderValue = 0.200;
    private double currentHue, currentSat, currentOpacity = 1.0, currentBrightness = 1.0;
    private int currentSizeState = 1;

    // --- Arrangement (which clusters show, and on which side) ----------------
    private enum SidePos { Off = 0, Left = 1, Right = 2 }
    private SidePos _navPos = SidePos.Right, _numpadPos = SidePos.Off;
    private bool _numpadOnly;

    // --- Modifier state machine ---------------------------------------------
    private enum ModState { Off, OneShot, Locked }

    // One modifier key's state + virtual key. Vk 0 = local-only (Fn remaps rows, no real key).
    // Keys holds the (possibly several) on-screen Key controls carrying this tag.
    private sealed class Mod(string tag, ushort vk)
    {
        public readonly string Tag = tag;
        public readonly ushort Vk = vk;
        public readonly List<Key> Keys = new();
        public ModState State;
    }

    // Ctrl ordered before Alt so an AltGr sent as one-shot Ctrl+Alt (if ever needed as a
    // fallback) would sequence correctly; otherwise order only affects the wrap sequence.
    private readonly Mod _ctrl = new("TOGGLE_CTRL", VK_LCONTROL), _rctrl = new("TOGGLE_RCTRL", VK_RCONTROL),
                         _alt = new("TOGGLE_ALT", VK_LMENU), _ralt = new("TOGGLE_RALT", VK_RMENU),
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

    private bool runOnStartup, _settingsReady;
    private long _suppressHideUntil;

    internal const string SettingsKey = @"Software\serifpersia\DesktopKeyboard";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DesktopKeyboard";

    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    // --- UI references -------------------------------------------------------
    private Border _mainBorder = null!;
    private Control _alphaBlock = null!;
    private Control? _navPanel;           // built lazily; a user with nav Off never pays for it
    private Grid _numpadGrid = null!;
    private Popup _themePopup = null!, _layoutPopup = null!;
    private TouchSlider _hueSlider = null!, _brightnessSlider = null!, _opacitySlider = null!, _sizeSlider = null!;
    private TextBlock _brightnessLabel = null!, _opacityLabel = null!, _sizeLabel = null!;
    private TextBlock _startupText = null!;
    private Control _navSegRow = null!, _numSegRow = null!;
    private readonly Border[] _navSegs = new Border[3], _numSegs = new Border[3];
    private Border _numOnlyBtn = null!;

    private readonly List<Key> _allKeys = new();
    private readonly Dictionary<string, KeyDef> _defByTag = new();

    // --- Active Windows keyboard layout (HKL) --------------------------------
    private IntPtr _activeHkl;      // UI thread: layout the labels currently reflect
    private long _lastSeenHkl;      // UIA thread: last foreground HKL, to post changes once

    public MainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;
        _mods = new[] { _ctrl, _rctrl, _alt, _ralt, _shift, _win, _fn };

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
            _activeHkl = InputLayout.ForegroundHkl();   // before the first UpdateKeys renders labels
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
        _numpadGrid = new Grid { IsVisible = false };
        for (int i = 0; i < 5; i++) _numpadGrid.RowDefinitions.Add(new RowDefinition(new GridLength(U)));
        for (int i = 0; i < 4; i++) _numpadGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(U)));

        // Side clusters (nav/numpad) are built lazily and re-parented by ApplyArrangement.
        _alphaBlock = BuildKeyboard();
        _bodyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _alphaBlock },
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
        AttachDrag(_modeBg, onTap: CycleMode);
        return _modeBg;
    }

    // Manual window drag for a control. BeginMoveDrag uses the OS move-loop, which a
    // WS_EX_NOACTIVATE window can't enter, so we move it ourselves. An absolute grab-offset
    // (not incremental deltas) from Avalonia's own pointer coords keeps it glued under the
    // finger: touch withholds moves until a threshold and the OS cursor doesn't track touch.
    // onTap != null makes a press-without-drag (within 6px) a click instead; sourceMustBeSelf
    // ignores presses that bubbled up from a child (e.g. Esc/chrome buttons on the top bar).
    private void AttachDrag(Control c, Action? onTap = null, bool sourceMustBeSelf = false)
    {
        bool dragging = false;
        int downX = 0, downY = 0;
        c.PointerPressed += (_, e) =>
        {
            if (sourceMustBeSelf && !ReferenceEquals(e.Source, c)) return;
            e.Pointer.Capture(c);
            var f = this.PointToScreen(e.GetPosition(this));
            downX = f.X; downY = f.Y;
            _grabX = f.X - Position.X; _grabY = f.Y - Position.Y;
            dragging = onTap == null;   // no tap action -> drag from the first move
        };
        c.PointerMoved += (_, e) =>
        {
            if (!ReferenceEquals(e.Pointer.Captured, c)) return;
            var f = this.PointToScreen(e.GetPosition(this));
            if (!dragging && (Math.Abs(f.X - downX) > 6 || Math.Abs(f.Y - downY) > 6)) dragging = true;
            if (dragging) { Position = new PixelPoint(f.X - _grabX, f.Y - _grabY); SyncAnchorFromMode(); }
        };
        c.PointerReleased += (_, e) =>
        {
            bool wasDragging = dragging;
            e.Pointer.Capture(null);
            dragging = false;
            if (!wasDragging) onTap?.Invoke();
        };
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

        var themeBtn = ChromeButton("🎨", 16, ToggleThemePopup);
        var layoutBtn = ChromeButton("⌨", 16, ToggleLayoutPopup);
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

        // The heavy panels (Child) are built on first open — see Toggle*Popup.
        _themePopup = new Popup
        {
            PlacementTarget = themeBtn,
            Placement = PlacementMode.BottomEdgeAlignedRight,
            IsLightDismissEnabled = false,
        };
        bar.Children.Add(_themePopup);

        _layoutPopup = new Popup
        {
            PlacementTarget = layoutBtn,
            Placement = PlacementMode.BottomEdgeAlignedRight,
            IsLightDismissEnabled = false,
        };
        bar.Children.Add(_layoutPopup);

        // Drag the keyboard by the empty top-bar area (ignore presses on Esc/chrome buttons).
        AttachDrag(bar, sourceMustBeSelf: true);
        return bar;
    }

    // The theme panel (sliders + toggles, ~20 controls) is built on first open — most sessions
    // never touch it, so it stays off the startup path and out of the live visual tree.
    private void ToggleThemePopup()
    {
        bool open = !_themePopup.IsOpen;
        CloseAllPopups();
        _themePopup.Child ??= BuildThemePanel();
        _themePopup.IsOpen = open;
    }

    private void ToggleLayoutPopup()
    {
        bool open = !_layoutPopup.IsOpen;
        CloseAllPopups();
        _layoutPopup.Child ??= BuildLayoutPanel();
        _layoutPopup.IsOpen = open;
    }

    private void CloseAllPopups()
    {
        _themePopup.IsOpen = false;
        _layoutPopup.IsOpen = false;
    }

    // The arrangement chooser: segmented Off/Left/Right rows for the nav cluster and the
    // numpad, plus a numpad-only toggle that overrides (and dims) both rows. Built lazily,
    // initialised from the already-loaded arrangement fields.
    private Control BuildLayoutPanel()
    {
        _navSegRow = SegRow(_navSegs, i => { _navPos = (SidePos)i; OnArrangementPicked(); });
        _numSegRow = SegRow(_numSegs, i => { _numpadPos = (SidePos)i; OnArrangementPicked(); });

        _numOnlyBtn = ChromeButton("Numpad only: Off", 16, () => { _numpadOnly = !_numpadOnly; OnArrangementPicked(); });
        _numOnlyBtn.Height = 44;
        _numOnlyBtn.Margin = new Thickness(0, 12, 0, 0);

        RefreshLayoutPanel();

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
                    Label("Nav keys"), _navSegRow,
                    Label("Numpad"), _numSegRow,
                    _numOnlyBtn,
                },
            },
        };
    }

    private Control SegRow(Border[] segs, Action<int> onPick)
    {
        string[] names = { "Off", "Left", "Right" };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        for (int i = 0; i < segs.Length; i++)
        {
            int pick = i;
            var b = ChromeButton(names[i], 15, () => onPick(pick));
            b.Width = 98; b.Height = 44;
            segs[i] = b;
            row.Children.Add(b);
        }
        return row;
    }

    private void OnArrangementPicked()
    {
        ApplyArrangement();
        RefreshLayoutPanel();
        SaveSettings();
    }

    private void RefreshLayoutPanel()
    {
        if (_numOnlyBtn == null) return;   // panel not built yet (arrangement set from LoadSettings)
        for (int i = 0; i < 3; i++)
        {
            _navSegs[i].Background = (int)_navPos == i ? Palette.Accent : Palette.Button;
            _numSegs[i].Background = (int)_numpadPos == i ? Palette.Accent : Palette.Button;
        }
        // Numpad-only overrides both rows: keep the remembered selections visible but inert.
        double dim = _numpadOnly ? 0.45 : 1.0;
        _navSegRow.Opacity = dim; _navSegRow.IsEnabled = !_numpadOnly;
        _numSegRow.Opacity = dim; _numSegRow.IsEnabled = !_numpadOnly;
        _numOnlyBtn.Background = _numpadOnly ? Palette.Accent : Palette.Button;
        ((TextBlock)_numOnlyBtn.Child!).Text = _numpadOnly ? "Numpad only: On" : "Numpad only: Off";
    }

    // Built lazily by ToggleThemePopup; every control is initialised from the already-loaded
    // theme model (LoadSettings only touches the model, never this UI).
    private Control BuildThemePanel()
    {
        _hueSlider = new TouchSlider { Minimum = 0, Maximum = 360, Value = currentHue };
        _brightnessSlider = new TouchSlider { Minimum = 50, Maximum = 300, Value = currentBrightness * 100.0 };
        _opacitySlider = new TouchSlider { Minimum = 10, Maximum = 100, Value = currentOpacity * 100.0 };
        _sizeSlider = new TouchSlider { Minimum = 0, Maximum = 2, Step = 1, Value = currentSizeState };

        _hueSlider.ValueChanged += v => { currentHue = v; currentSat = 0.55; ApplyTheme(); SaveSettings(); };
        _brightnessSlider.ValueChanged += v => { currentBrightness = v / 100.0; _brightnessLabel.Text = $"Brightness: {(int)v}%"; ApplyTheme(); SaveSettings(); };
        _opacitySlider.ValueChanged += v => { currentOpacity = v / 100.0; _opacityLabel.Text = $"Background opacity: {(int)v}%"; ApplyTheme(); SaveSettings(); };
        _sizeSlider.ValueChanged += v => { SetSize((int)v); SaveSettings(); };

        _brightnessLabel = Label($"Brightness: {(int)(currentBrightness * 100)}%");
        _opacityLabel = Label($"Background opacity: {(int)(currentOpacity * 100)}%");
        _sizeLabel = Label("Size: " + SizeName(currentSizeState));

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
        UpdateStartupButton();   // reflect the already-loaded runOnStartup state

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

    // One key slot in the base grid. Sc != 0 marks a positional key: its physical position is
    // the scan code, and both the VK sent and the glyphs shown come from the active Windows
    // layout (InputLayout). Sc == 0 is a fixed key: Vk-table tag + constructor label, as ever.
    private readonly record struct KeyDef(string Tag, double W, byte Sc = 0, string? Label = null, double FS = 22);
    private static KeyDef P(byte sc, double w = 1) => new("SC" + sc.ToString("X2"), w, sc);

    // The base block: standard 60%-style physical positions, every row exactly 15 U wide.
    private static readonly KeyDef[][] BaseRows =
    {
        new[] { P(0x29), P(0x02), P(0x03), P(0x04), P(0x05), P(0x06), P(0x07), P(0x08), P(0x09),
                P(0x0A), P(0x0B), P(0x0C), P(0x0D), new KeyDef("BACK", 2, Label: "⌫", FS: 18) },
        new[] { new KeyDef("TAB", 1.5, Label: "⇥"), P(0x10), P(0x11), P(0x12), P(0x13), P(0x14), P(0x15),
                P(0x16), P(0x17), P(0x18), P(0x19), P(0x1A), P(0x1B), P(0x2B, 1.5) },
        new[] { new KeyDef("TOGGLE_FN", 1.75, Label: "Fn", FS: 18), P(0x1E), P(0x1F), P(0x20), P(0x21), P(0x22),
                P(0x23), P(0x24), P(0x25), P(0x26), P(0x27), P(0x28), new KeyDef("ENTER", 2.25, Label: "↵") },
        new[] { new KeyDef("TOGGLE_SHIFT", 2.25, Label: "⇧", FS: 26), P(0x2C), P(0x2D), P(0x2E), P(0x2F), P(0x30),
                P(0x31), P(0x32), P(0x33), P(0x34), P(0x35), new KeyDef("TOGGLE_SHIFT", 2.75, Label: "⇧", FS: 26) },
        new[] { new KeyDef("TOGGLE_CTRL", 1.25, Label: "Ctrl", FS: 16), new KeyDef("TOGGLE_WIN", 1.25, Label: "⊞", FS: 20),
                new KeyDef("TOGGLE_ALT", 1.25, Label: "Alt", FS: 16), new KeyDef("SPACE", 6.25, Label: "Space", FS: 18),
                new KeyDef("TOGGLE_RALT", 1.25, Label: "AltGr", FS: 13), new KeyDef("LANG", 1.25, Label: "…", FS: 14),
                new KeyDef("APPS", 1.25, Label: "☰", FS: 18), new KeyDef("TOGGLE_RCTRL", 1.25, Label: "Ctrl", FS: 16) },
    };

    // Every positional scan code in the grid — the set InputLayout builds glyph tables for.
    private static readonly byte[] PositionalScans =
        BaseRows.SelectMany(r => r).Where(d => d.Sc != 0).Select(d => d.Sc).ToArray();

    private Control BuildKeyboard()
    {
        var col = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var defs in BaseRows)
        {
            var widths = new double[defs.Length];
            var keys = new Key[defs.Length];
            for (int i = 0; i < defs.Length; i++)
            {
                widths[i] = defs[i].W;
                keys[i] = MakeKey(defs[i].Tag, defs[i].Label ?? "", defs[i].FS);
                _defByTag.TryAdd(defs[i].Tag, defs[i]);   // TryAdd: TOGGLE_SHIFT appears twice
            }
            col.Children.Add(Row(widths, keys));
        }
        return col;
    }

    // Nav cluster (side column): Ins/Home/PgUp over Del/End/PgDn, inverted-T arrows below with
    // PrtSc in the spare corner. Built on first use — nav can be switched Off entirely.
    private void EnsureNavBuilt()
    {
        if (_navPanel != null) return;

        var top = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < 3; i++) top.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(U)));
        for (int i = 0; i < 2; i++) top.RowDefinitions.Add(new RowDefinition(new GridLength(U)));
        Place(top, MakeKey("INS", "Ins", 15), 0, 0);
        Place(top, MakeKey("HOME", "Home", 14), 0, 1);
        Place(top, MakeKey("PGUP", "PgUp", 14), 0, 2);
        Place(top, MakeKey("DEL", "Del", 15), 1, 0);
        Place(top, MakeKey("END", "End", 14), 1, 1);
        Place(top, MakeKey("PGDN", "PgDn", 14), 1, 2);

        var arrows = new Grid { Margin = new Thickness(0, U * 0.6, 0, 0) };
        for (int i = 0; i < 3; i++) arrows.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(U)));
        for (int i = 0; i < 2; i++) arrows.RowDefinitions.Add(new RowDefinition(new GridLength(U)));
        Place(arrows, MakeKey("UP", "↑", 26), 0, 1);
        Place(arrows, MakeKey("PRTSC", "PrtSc", 12), 0, 2);
        Place(arrows, MakeKey("LEFT", "←", 22), 1, 0);
        Place(arrows, MakeKey("DOWN", "↓", 22), 1, 1);
        Place(arrows, MakeKey("RIGHT", "→", 22), 1, 2);

        _navPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children = { top, arrows },
        };
        ApplyTheme();   // the just-created keys need their themed background applied
    }

    private static void Place(Grid g, Key k, int row, int col, int rowSpan = 1, int colSpan = 1)
    {
        Grid.SetRow(k, row); Grid.SetColumn(k, col);
        if (rowSpan > 1) Grid.SetRowSpan(k, rowSpan);
        if (colSpan > 1) Grid.SetColumnSpan(k, colSpan);
        g.Children.Add(k);
    }

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
        GetMod(tag)?.Keys.Add(k);   // modifier keys also register under their Mod for highlighting
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
        if (!on) CloseAllPopups();
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
            CheckForegroundHkl();   // catches Win+Space layout changes with unchanged focus
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
            owner.CheckForegroundHkl();   // focus moved — the new app may use another layout
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
        if (_bodyVisible) { _focusEditable = false; CloseAllPopups(); UpdateGeometry(); }
    }

    // --- Close confirmation --------------------------------------------------
    private async void RequestClose()
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

        if (await tcs.Task) _desktop.Shutdown();
    }

    // --- Size / layout -------------------------------------------------------
    private static string SizeName(int s) => s switch { 0 => "Small", 2 => "Large", _ => "Medium" };

    private void SetSize(int state)
    {
        currentSizeState = state;
        _scale.ScaleX = _scale.ScaleY = state switch { 0 => 0.78, 2 => 1.3, _ => 1.0 };
        AnchorToMode();
        if (_sizeLabel != null) _sizeLabel.Text = "Size: " + SizeName(state);
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
            Place(_numpadGrid, MakeKey(k.Tag, k.C, k.FS > 0 ? k.FS : 22), k.R, k.Col, k.RS, k.CS);
        ApplyTheme();   // the just-created keys need their themed background applied
    }

    // Recomposes the body from the arrangement fields. Clusters are built once and
    // re-parented (never rebuilt — keys keep their _allKeys registration and event wiring);
    // the window then grows/shrinks to fit via SizeToContent and re-pins on the mode button.
    private void ApplyArrangement()
    {
        bool navOn = !_numpadOnly && _navPos != SidePos.Off;
        bool numOn = _numpadOnly || _numpadPos != SidePos.Off;
        if (navOn) EnsureNavBuilt();
        if (numOn) BuildNumpad();

        _bodyRow.Children.Clear();
        if (_numpadOnly)
        {
            _numpadGrid.Margin = default;
            _bodyRow.Children.Add(_numpadGrid);
        }
        else
        {
            if (numOn && _numpadPos == SidePos.Left) AddSide(_numpadGrid, left: true);
            if (navOn && _navPos == SidePos.Left) AddSide(_navPanel!, left: true);
            _bodyRow.Children.Add(_alphaBlock);
            if (navOn && _navPos == SidePos.Right) AddSide(_navPanel!, left: false);
            if (numOn && _numpadPos == SidePos.Right) AddSide(_numpadGrid, left: false);
        }
        _numpadGrid.IsVisible = numOn;

        foreach (var m in _mods) SetModState(m, ModState.Off, updateLabels: false);
        UpdateKeys();
        AnchorToMode();
    }

    private void AddSide(Control c, bool left)
    {
        c.Margin = left ? new Thickness(0, 0, 6, 0) : new Thickness(6, 0, 0, 0);
        _bodyRow.Children.Add(c);
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
        if (!_bodyVisible) CloseAllPopups();
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
        if (!_settingsReady) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveSettingsNow()
    {
        if (!_settingsReady) return;
        try
        {
            var inv = CultureInfo.InvariantCulture;
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            if (key == null) return;
            key.SetValue("Hue", currentHue.ToString(inv), RegistryValueKind.String);
            key.SetValue("Sat", currentSat.ToString(inv), RegistryValueKind.String);
            key.SetValue("Brightness", currentBrightness.ToString(inv), RegistryValueKind.String);
            key.SetValue("Opacity", currentOpacity.ToString(inv), RegistryValueKind.String);
            key.SetValue("Nav", (int)_navPos, RegistryValueKind.DWord);
            key.SetValue("Numpad", (int)_numpadPos, RegistryValueKind.DWord);
            key.SetValue("NumpadOnly", _numpadOnly ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("Size", currentSizeState, RegistryValueKind.DWord);
            key.SetValue("Mode", (int)currentMode, RegistryValueKind.DWord);
            key.SetValue("RunOnStartup", runOnStartup ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex) { Debug.WriteLine($"SaveSettings failed: {ex.Message}"); }
    }

    // Populates the theme model + applies it. Deliberately touches no popup control — the
    // theme panel may not be built yet (ToggleThemePopup builds it from these fields on demand).
    private void LoadSettings()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            currentHue = ReadDouble(key, "Hue", 0.0);
            currentSat = ReadDouble(key, "Sat", 0.0);
            currentBrightness = ReadDouble(key, "Brightness", 1.0);
            currentOpacity = ReadDouble(key, "Opacity", 1.0);
            if (key?.GetValue("Nav") is int nv)
            {
                _navPos = (SidePos)Math.Clamp(nv, 0, 2);
                _numpadPos = (SidePos)Math.Clamp(key.GetValue("Numpad") is int np ? np : 0, 0, 2);
                _numpadOnly = key.GetValue("NumpadOnly") is int no && no != 0;
            }
            else
            {
                // Migrate the old two-state "Layout": 0 = keyboard+nav, 1 = keyboard+nav+numpad.
                _navPos = SidePos.Right;
                _numpadPos = key?.GetValue("Layout") is int l && l == 1 ? SidePos.Right : SidePos.Off;
            }
            int savedSize = key?.GetValue("Size") is int sz ? sz : 1;
            int savedMode = key?.GetValue("Mode") is int m ? m : 0;

            ApplyTheme();
            ApplyArrangement();
            SetSize(Math.Clamp(savedSize, 0, 2));

            currentMode = (KeyboardMode)Math.Clamp(savedMode, 0, 2);
            ApplyMode();

            runOnStartup = IsRunOnStartupEnabled();
            UpdateStartupButton();
        }
        catch (Exception ex) { Debug.WriteLine($"LoadSettings failed: {ex.Message}"); }
        finally { _settingsReady = true; }
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

        // Local action: cycle the foreground app's keyboard layout. No send, no auto-repeat.
        if (tag == "LANG") { CycleInputLayout(); return; }

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
        // Fn layer first (F1–F12 are VKs, not positions), then positional keys as scan codes,
        // then the fixed-VK table.
        if (_fn.State != ModState.Off && FnMap.TryGetValue(tag, out var fm)) { SendKey(fm.Vk); return; }
        if (_defByTag.TryGetValue(tag, out var d) && d.Sc != 0) { SendScan(d.Sc); return; }
        byte vk = GetVirtualKeyCode(tag);
        if (vk != 0) SendKey(vk);
    }

    private static INPUT KeyInput(ushort vk, bool up)
    {
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        ushort scan = 0;
        if (ExtScan.TryGetValue(vk, out byte sc)) { scan = sc; flags |= KEYEVENTF_EXTENDEDKEY; }
        return new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } };
    }

    // Scan-code event: the receiving thread's own layout translates position → VK → char at
    // delivery time, so positional keys always produce what their label shows, even if the
    // foreground layout changed between our last relabel and the press.
    private static INPUT ScanInput(byte sc, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wScan = sc, dwFlags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0) },
    };

    // Single key event (used to hold/release locked modifiers like a physically held key).
    private void SendRaw(ushort vk, bool down)
    {
        _inputBuf[0] = KeyInput(vk, up: !down);
        SendInput(1, _inputBuf, InputSize);
    }

    private void SendKey(byte vk) => SendWrapped(KeyInput(vk, false), KeyInput(vk, true));
    private void SendScan(byte sc) => SendWrapped(ScanInput(sc, false), ScanInput(sc, true));

    private void SendWrapped(INPUT down, INPUT up)
    {
        // Only wrap ONE-SHOT modifiers here; locked ones are already physically held down.
        int n = 0;
        foreach (var m in _mods)
            if (m.State == ModState.OneShot && m.Vk != 0) _inputBuf[n++] = KeyInput(m.Vk, false);
        _inputBuf[n++] = down;
        _inputBuf[n++] = up;
        for (int i = _mods.Length - 1; i >= 0; i--)
            if (_mods[i].State == ModState.OneShot && _mods[i].Vk != 0) _inputBuf[n++] = KeyInput(_mods[i].Vk, true);

        SendInput((uint)n, _inputBuf, InputSize);
        ConsumeOneShotModifiers();
    }

    // --- Windows keyboard layout (HKL) ----------------------------------------
    // Cycle the FOREGROUND app's layout — ours never matters, this window is WS_EX_NOACTIVATE
    // and never foreground. The request is posted (async, refusable), so relabel optimistically
    // and reconcile shortly after; the UIA-thread checks are the long-term backstop.
    private void CycleInputLayout()
    {
        if (InputLayout.InstalledLayouts().Length <= 1) return;
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return;

        IntPtr next = InputLayout.NextLayout(InputLayout.ForegroundHkl());
        InputLayout.RequestSwitch(fg, next);
        _activeHkl = next;
        UpdateKeys();
        DispatcherTimer.RunOnce(OnHklMaybeChanged, TimeSpan.FromMilliseconds(300));
    }

    // Called on the UIA thread (focus events + 1 Hz poll): detect foreground layout changes —
    // including Win+Space with unchanged focus — and hand them to the UI thread exactly once.
    private void CheckForegroundHkl()
    {
        long hkl = InputLayout.ForegroundHkl().ToInt64();
        if (Interlocked.Exchange(ref _lastSeenHkl, hkl) == hkl) return;
        Dispatcher.UIThread.Post(OnHklMaybeChanged);
    }

    private void OnHklMaybeChanged()
    {
        IntPtr hkl = InputLayout.ForegroundHkl();
        if (hkl == _activeHkl) return;
        _activeHkl = hkl;
        UpdateKeys();
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

        IBrush? ov = state switch { ModState.OneShot => Palette.Accent, ModState.Locked => Palette.ModLock, _ => null };
        foreach (var k in m.Keys) k.SetOverride(ov);

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
        var glyphs = InputLayout.Table(_activeHkl, PositionalScans);

        foreach (var k in _allKeys)
        {
            string tag = k.KeyTag;
            string label =
                isFn && FnMap.TryGetValue(tag, out var fm) ? fm.Label :
                tag == "LANG" ? InputLayout.ShortName(_activeHkl) :
                _defByTag.TryGetValue(tag, out var d) && d.Sc != 0 && glyphs.TryGetValue(d.Sc, out var g)
                    ? (isShifted ? g.Shifted : g.Normal)
                    : k.DefaultLabel;
            k.SetLabel(label);
        }
    }

    // --- Lookup tables -------------------------------------------------------
    private static readonly Dictionary<string, byte> Vk = new()
    {
        ["BACK"] = 0x08, ["TAB"] = 0x09, ["ENTER"] = 0x0D, ["ESC"] = 0x1B, ["SPACE"] = 0x20,
        ["LEFT"] = 0x25, ["UP"] = 0x26, ["RIGHT"] = 0x27, ["DOWN"] = 0x28,
        ["PGUP"] = 0x21, ["PGDN"] = 0x22, ["END"] = 0x23, ["HOME"] = 0x24,
        ["PRTSC"] = 0x2C, ["INS"] = 0x2D, ["DEL"] = 0x2E, ["APPS"] = 0x5D,
        ["NUMLK"] = 0x90, ["NUMSLASH"] = 0x6F, ["NUMSTAR"] = 0x6A, ["NUMMINUS"] = 0x6D, ["NUMPLUS"] = 0x6B,
        ["NUM0"] = 0x60, ["NUM1"] = 0x61, ["NUM2"] = 0x62, ["NUM3"] = 0x63, ["NUM4"] = 0x64,
        ["NUM5"] = 0x65, ["NUM6"] = 0x66, ["NUM7"] = 0x67, ["NUM8"] = 0x68, ["NUM9"] = 0x69, ["NUMDOT"] = 0x6E,
    };

    // Fn layer, keyed by the positional tags of the number row (F-keys are VKs, not positions).
    private static readonly Dictionary<string, (byte Vk, string Label)> FnMap = new()
    {
        ["SC02"] = (0x70, "F1"), ["SC03"] = (0x71, "F2"), ["SC04"] = (0x72, "F3"), ["SC05"] = (0x73, "F4"),
        ["SC06"] = (0x74, "F5"), ["SC07"] = (0x75, "F6"), ["SC08"] = (0x76, "F7"), ["SC09"] = (0x77, "F8"),
        ["SC0A"] = (0x78, "F9"), ["SC0B"] = (0x79, "F10"), ["SC0C"] = (0x7A, "F11"), ["SC0D"] = (0x7B, "F12"),
        ["BACK"] = (0x2E, "Del"),
    };

    private static byte GetVirtualKeyCode(string keyTag) => Vk.TryGetValue(keyTag, out byte v) ? v : (byte)0;
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
