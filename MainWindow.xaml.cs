using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DesktopKeyboard
{
    public partial class MainWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")] public static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")] public static extern IntPtr GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const byte VK_LSHIFT   = 0xA0;
        private const byte VK_LCONTROL = 0xA2;
        private const byte VK_LMENU    = 0xA4;
        private const byte VK_LWIN     = 0x5B;

        private static readonly Color ActiveColor = Color.FromRgb(74, 144, 226);

        // HSV "value" levels that reproduce the original greys at saturation 0:
        //   panel  #111111 -> 0.067, key #252525 -> 0.145, border #333333 -> 0.20
        private const double PanelValue  = 0.067;
        private const double KeyValue    = 0.145;
        private const double BorderValue = 0.200;

        private double currentHue        = 0.0;  // 0..360
        private double currentSat        = 0.0;  // 0 = grey (default), >0 = tinted
        private double currentOpacity    = 1.0;  // background opacity 0.1..1.0
        private double currentBrightness = 1.0;  // value multiplier 0.5..3.0

        private int currentSizeState   = 1;
        private int currentLayoutState = 0;
        // Modifier behaviour: a tap arms the modifier for the next key only (OneShot),
        // then it clears automatically; a long-press locks it on until tapped again.
        private enum ModState { Off, OneShot, Locked }

        private static readonly Color LockColor = Color.FromRgb(210, 140, 30); // amber = locked

        private static readonly string[] ModTags =
            { "TOGGLE_SHIFT", "TOGGLE_CTRL", "TOGGLE_ALT", "TOGGLE_WIN", "TOGGLE_FN" };

        // Live state of each modifier (TOGGLE_FN is a local number-row -> F1-F12 remap,
        // not a real key). Single source of truth for the modifier state machine.
        private readonly Dictionary<string, ModState> _mod = new()
        {
            ["TOGGLE_SHIFT"] = ModState.Off, ["TOGGLE_CTRL"] = ModState.Off,
            ["TOGGLE_ALT"]   = ModState.Off, ["TOGGLE_WIN"]  = ModState.Off,
            ["TOGGLE_FN"]    = ModState.Off,
        };

        private readonly DispatcherTimer _longPressTimer =
            new() { Interval = TimeSpan.FromMilliseconds(300) };
        private string? _longPressTag;
        private bool _longPressActive;
        private bool _longPressFired;

        private enum KeyboardMode { Auto, Show, Hide }
        private KeyboardMode currentMode = KeyboardMode.Auto;
        private Window? _modeWindow;
        private Button? _modeButton;
        private TextBlock? _modeText;
        private const double ModeBtnW = 96;
        private const double ModeBtnH = 32;
        private bool _modeDragging;
        private POINT _modeDragStart;
        private POINT _modeDragLast;

        private bool runOnStartup = false;
        private bool _loading = false;          // suppresses SaveSettings while applying loaded values
        private bool _settingsLoaded = false;   // blocks SaveSettings until the initial load finishes
        private DateTime _suppressHideUntil = DateTime.MinValue; // keeps keyboard up after Esc moves focus

        private const string SettingsKey = @"Software\serifpersia\DesktopKeyboard";
        private const string RunKey      = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue    = "DesktopKeyboard";

        private readonly DispatcherTimer _hideTimer;

        public MainWindow()
        {
            InitializeComponent();

            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _hideTimer.Tick += HideTimer_Tick;

            _longPressTimer.Tick += LongPress_Tick;
            AttachModifierHandlers();

            // Create the floating mode button and restore settings as soon as the message
            // loop starts — this runs at startup independent of the main window ever being
            // shown (its HWND isn't created until the keyboard first becomes visible).
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                CreateModeButton();
                LoadSettings();
            });

            // Defer UIA registration until after the window is shown — initializing
            // the UIA COM infrastructure on the UI thread blocks startup for several seconds.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                Automation.AddAutomationFocusChangedEventHandler(OnFocusChanged);
            });
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            NoActivate(new WindowInteropHelper(this).Handle);
        }

        private static void MakeTopmost(IntPtr h) =>
            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);

        // Make a window non-activating (never steals focus) and topmost; tool-window
        // also keeps it out of alt-tab. Used by the keyboard and the mode button.
        private static void NoActivate(IntPtr h, bool toolWindow = false)
        {
            long ex = GetWindowLong(h, GWL_EXSTYLE).ToInt64() | WS_EX_NOACTIVATE;
            if (toolWindow) ex |= WS_EX_TOOLWINDOW;
            SetWindowLong(h, GWL_EXSTYLE, new IntPtr(ex));
            MakeTopmost(h);
        }

        private void HideTimer_Tick(object? sender, EventArgs e)
        {
            _hideTimer.Stop();
            if (currentMode != KeyboardMode.Auto) return;
            if (DateTime.Now < _suppressHideUntil) return;
            if (this.Visibility == Visibility.Visible)
            {
                this.Visibility = Visibility.Collapsed;
                ThemePopup.IsOpen = false;
            }
        }

        private void OnFocusChanged(object? sender, AutomationFocusChangedEventArgs e)
        {
            // In Show/Hide modes the floating button controls visibility, not input focus.
            if (currentMode != KeyboardMode.Auto) return;

            AutomationElement? fe = AutomationElement.FocusedElement;
            if (fe == null) return;

            Dispatcher.Invoke(() =>
            {
                if (IsEditableTextField(fe))
                {
                    _hideTimer.Stop();

                    if (this.Visibility != Visibility.Visible)
                        this.Visibility = Visibility.Visible;

                    MakeTopmost(new WindowInteropHelper(this).Handle);
                    BringModeButtonToFront();
                }
                else if (this.Visibility == Visibility.Visible)
                {
                    // Esc often moves focus off the text field; keep the keyboard up for a
                    // short window so combos like Ctrl+Alt+Esc don't dismiss it.
                    if (DateTime.Now < _suppressHideUntil)
                        return;
                    _hideTimer.Start();
                }
            });
        }

        private bool IsEditableTextField(AutomationElement element)
        {
            if (element == null) return false;

            var ct = element.Current.ControlType;

            // Standard text fields, and document surfaces used by terminals/editors
            // (e.g. consoles, Claude Code) which expose their content as a Document.
            if (ct == ControlType.Edit || ct == ControlType.ComboBox || ct == ControlType.Document)
                return true;

            // Clearly non-text controls — reject outright. Note Pane/Group/Custom are NOT
            // here: terminals (and other editors) often focus a Pane/Custom element that
            // carries a TextPattern, so let the pattern checks below decide for those.
            if (ct == ControlType.ListItem || ct == ControlType.TreeItem ||
                ct == ControlType.Button    || ct == ControlType.MenuItem ||
                ct == ControlType.TabItem   || ct == ControlType.CheckBox ||
                ct == ControlType.RadioButton || ct == ControlType.Hyperlink ||
                ct == ControlType.Image     || ct == ControlType.ScrollBar)
                return false;

            try
            {
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? valueObj) &&
                    valueObj is ValuePattern vp)
                {
                    string name = element.Current.Name ?? "";
                    if (name.Length < 3 || IsLikelyFileName(name))
                        return false;
                    return !vp.Current.IsReadOnly;
                }

                // A TextPattern means an editable/selectable text surface — covers rich
                // editors and terminal-style apps whose control type is Pane/Custom.
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out object? textObj) && textObj != null)
                    return true;
            }
            catch { }

            return false;
        }

        private bool IsLikelyFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                   (name.Contains('.') && name.Length > 6);
        }

        private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            this.DragMove();                // blocks until released
            BringModeButtonToFront();       // dragging re-raises the keyboard; restore order
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(this,
                "Close Desktop Keyboard?",
                "Desktop Keyboard",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
                Application.Current.Shutdown();
        }

        private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            SetSize((int)e.NewValue);
            SaveSettings();
        }

        private void SetSize(int state)
        {
            currentSizeState = state;
            switch (currentSizeState)
            {
                case 0: this.Width = 600;  this.Height = 254; break;
                case 1: this.Width = 850;  this.Height = 360; break;
                case 2: this.Width = 1200; this.Height = 508; break;
            }
            RepositionModeButton();
            if (SizeLabel != null)
                SizeLabel.Text = "Size: " + currentSizeState switch
                {
                    0 => "Small",
                    2 => "Large",
                    _ => "Medium",
                };
        }

        private void LayoutButton_Click(object sender, RoutedEventArgs e)
        {
            SetLayout((currentLayoutState + 1) % 2);
            SaveSettings();
        }

        // Two layouts: 0 = Base (keyboard incl. nav keys), 1 = Full (Base + numpad).
        // F-keys are reached via the Fn modifier, so there's no dedicated F-key row.
        private void SetLayout(int state)
        {
            currentLayoutState = state;
            bool full = state == 1;

            NumpadCol.Width       = full ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            NumpadGrid.Visibility = full ? Visibility.Visible : Visibility.Collapsed;

            // Widen the design surface when the numpad is shown (Viewbox scales it to fit).
            MainBorder.Width = full ? 1150 : 850;

            // Reset modifiers when changing layouts so they aren't left stuck active.
            foreach (var t in ModTags) SetModState(t, ModState.Off);
        }

        // --- Hue / Opacity theming -------------------------------------------

        // --- Floating Show/Hide/Auto mode button -----------------------------

        private void CreateModeButton()
        {
            if (_modeWindow != null) return;

            // Give the keyboard a sensible default position (bottom-centre of the work
            // area) so the mode button has a place to sit even before the keyboard shows.
            var wa = SystemParameters.WorkArea;
            this.Left = wa.Left + (wa.Width - this.Width) / 2;
            this.Top  = wa.Bottom - this.Height - 40;

            // White label with a black halo so it stays readable on a translucent key.
            _modeText = new TextBlock
            {
                Text = "Auto",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 4, ShadowDepth = 0, Opacity = 1 }
            };

            // Styled like a keyboard key (themed grey, set by ApplyTheme).
            _modeButton = new Button
            {
                Content = _modeText,
                Width = ModeBtnW,
                Height = ModeBtnH,
                Focusable = false,
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 37)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };

            // Click cycles the mode; a drag moves the keyboard (the button is the handle
            // that stays visible when the keyboard is hidden). Click (not a captured
            // up handler) is used for the tap so it works reliably for mouse and touch.
            _modeButton.Click                      += ModeButton_Click;
            _modeButton.PreviewMouseLeftButtonDown += ModeButton_Down;
            _modeButton.PreviewMouseMove           += ModeButton_Move;

            _modeWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowActivated = false,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize,
                Width = ModeBtnW,
                Height = ModeBtnH,
                Title = "Keyboard Toggle",
                Content = new Border { CornerRadius = new CornerRadius(6), Child = _modeButton }
            };

            // Non-activating + tool window so it floats above everything and never
            // steals focus from the target app or shows in alt-tab.
            _modeWindow.SourceInitialized += (s, e) =>
                NoActivate(new WindowInteropHelper(_modeWindow).Handle, toolWindow: true);

            _modeWindow.Show();

            // Keep the button glued to the keyboard's top-centre as it moves/resizes.
            this.LocationChanged += (s, e) => RepositionModeButton();
            this.SizeChanged     += (s, e) => RepositionModeButton();

            RepositionModeButton();
            ApplyTheme();          // theme the mode button to match the keys
            UpdateModeButton();
        }

        // Places the mode button centred on the keyboard's top bar. Uses this.Width
        // (the set value) rather than ActualWidth so it works while the keyboard is
        // hidden/collapsed too.
        private void RepositionModeButton()
        {
            if (_modeWindow == null) return;
            _modeWindow.Left = this.Left + (this.Width / 2) - (ModeBtnW / 2);
            _modeWindow.Top  = this.Top + 4;
        }

        // Keep the mode button drawn above the keyboard when the keyboard (re)appears.
        // Both windows are topmost, which does not define their order relative to each
        // other, so we explicitly insert the keyboard directly *below* the mode window.
        private void BringModeButtonToFront()
        {
            if (_modeWindow == null) return;
            var modeH = new WindowInteropHelper(_modeWindow).Handle;
            var kbH   = new WindowInteropHelper(this).Handle;
            if (modeH == IntPtr.Zero) return;

            // Defer so it runs after the keyboard's own show/topmost calls have settled.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                MakeTopmost(modeH);
                if (kbH != IntPtr.Zero)
                    SetWindowPos(kbH, modeH, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            });
        }

        private void ModeButton_Click(object sender, RoutedEventArgs e)
        {
            // Swallow the click that ends a drag; a real tap cycles the mode.
            if (_modeDragging) { _modeDragging = false; return; }
            CycleMode();
        }

        private void ModeButton_Down(object sender, MouseButtonEventArgs e)
        {
            GetCursorPos(out _modeDragStart);
            _modeDragLast = _modeDragStart;
            _modeDragging = false;
        }

        private void ModeButton_Move(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            GetCursorPos(out POINT cur);
            if (!_modeDragging &&
                (Math.Abs(cur.X - _modeDragStart.X) > 4 || Math.Abs(cur.Y - _modeDragStart.Y) > 4))
                _modeDragging = true;

            if (_modeDragging)
            {
                // Move the keyboard; the button follows via RepositionModeButton so the
                // pointer stays over it and further move events keep arriving (no capture).
                this.Left += cur.X - _modeDragLast.X;
                this.Top  += cur.Y - _modeDragLast.Y;
                _modeDragLast = cur;
                RepositionModeButton();
            }
        }

        private void CycleMode()
        {
            currentMode = currentMode switch
            {
                KeyboardMode.Auto => KeyboardMode.Show,
                KeyboardMode.Show => KeyboardMode.Hide,
                _                 => KeyboardMode.Auto,
            };
            ApplyMode();
            SaveSettings();
        }

        private void ApplyMode()
        {
            switch (currentMode)
            {
                case KeyboardMode.Show:
                    _hideTimer.Stop();
                    this.Visibility = Visibility.Visible;
                    MakeTopmost(new WindowInteropHelper(this).Handle);
                    RepositionModeButton();
                    BringModeButtonToFront();
                    break;

                case KeyboardMode.Hide:
                    _hideTimer.Stop();
                    this.Visibility = Visibility.Collapsed;
                    ThemePopup.IsOpen = false;
                    break;

                case KeyboardMode.Auto:
                    // Visibility now resolves from input focus on the next focus change.
                    break;
            }
            UpdateModeButton();
        }

        private void UpdateModeButton()
        {
            if (_modeText != null)
                _modeText.Text = currentMode switch
                {
                    KeyboardMode.Show => "Show",
                    KeyboardMode.Hide => "Hide",
                    _                 => "Auto",
                };
        }

        private void ThemeButton_Click(object sender, RoutedEventArgs e)
        {
            ThemePopup.IsOpen = !ThemePopup.IsOpen;
        }

        private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            currentHue = e.NewValue;
            currentSat = 0.55; // moving the hue slider tints the keyboard
            ApplyTheme();
            SaveSettings();
        }

        private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            currentBrightness = e.NewValue / 100.0;
            if (BrightnessLabel != null)
                BrightnessLabel.Text = $"Brightness: {(int)e.NewValue}%";
            ApplyTheme();
            SaveSettings();
        }

        private void HueReset_Click(object sender, RoutedEventArgs e)
        {
            // Set the slider first (which re-fires HueSlider_ValueChanged), then force
            // saturation back to grey so the reset isn't overwritten by that event.
            HueSlider.Value = 0;
            currentSat = 0.0;
            ApplyTheme();
            SaveSettings();
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            currentOpacity = e.NewValue / 100.0;
            if (OpacityLabel != null)
                OpacityLabel.Text = $"Background opacity: {(int)e.NewValue}%";
            ApplyTheme();
            SaveSettings();
        }

        private void StartupButton_Click(object sender, RoutedEventArgs e)
        {
            SetRunOnStartup(!runOnStartup);
            SaveSettings();
        }

        private void ApplyTheme()
        {
            // Guard against slider ValueChanged firing during InitializeComponent.
            if (MainBorder == null) return;

            double b = currentBrightness;
            Color panelColor  = HsvToColor(currentHue, currentSat, Math.Min(1.0, PanelValue  * b));
            Color keyColor    = HsvToColor(currentHue, currentSat, Math.Min(1.0, KeyValue    * b));
            Color borderColor = HsvToColor(currentHue, currentSat, Math.Min(1.0, BorderValue * b));

            // Opacity applies to the panel, border, and the keys themselves (but not the
            // glyphs — those keep their black halo so they stay readable when translucent).
            MainBorder.Background  = new SolidColorBrush(panelColor)  { Opacity = currentOpacity };
            MainBorder.BorderBrush = new SolidColorBrush(borderColor) { Opacity = currentOpacity };

            // Replace the resource entry rather than mutating the brush: brushes referenced
            // by DynamicResource get frozen by WPF, and mutating a frozen brush throws.
            // Swapping the entry re-resolves every DynamicResource reference safely.
            Resources["KeyBg"] = new SolidColorBrush(keyColor) { Opacity = currentOpacity };

            // Match the floating mode button to the keys.
            if (_modeButton != null)
                _modeButton.Background = new SolidColorBrush(keyColor) { Opacity = currentOpacity };
        }

        private static Color HsvToColor(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;

            double r, g, b;
            if      (h < 60)  { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else              { r = c; g = 0; b = x; }

            return Color.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }

        // --- Settings persistence (registry) ---------------------------------

        private void SaveSettings()
        {
            // Skip while applying loaded values, and skip the spurious ValueChanged events
            // raised during InitializeComponent (before settings have been loaded) so they
            // can't clobber the saved registry values with defaults.
            if (_loading || !_settingsLoaded) return;
            try
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
                if (key == null) return;
                key.SetValue("Hue",          currentHue.ToString(inv),        RegistryValueKind.String);
                key.SetValue("Sat",          currentSat.ToString(inv),        RegistryValueKind.String);
                key.SetValue("Brightness",   currentBrightness.ToString(inv), RegistryValueKind.String);
                key.SetValue("Opacity",      currentOpacity.ToString(inv),    RegistryValueKind.String);
                key.SetValue("Layout",       currentLayoutState, RegistryValueKind.DWord);
                key.SetValue("Size",         currentSizeState,   RegistryValueKind.DWord);
                key.SetValue("Mode",         (int)currentMode,   RegistryValueKind.DWord);
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

                double savedHue        = ReadDouble(key, "Hue",        0.0);
                double savedSat        = ReadDouble(key, "Sat",        0.0);
                double savedBrightness = ReadDouble(key, "Brightness", 1.0);
                double savedOpacity    = ReadDouble(key, "Opacity",    1.0);
                int    savedLayout     = key?.GetValue("Layout") is int l ? l : 0;
                int    savedSize       = key?.GetValue("Size")   is int sz ? sz : 1;
                int    savedMode       = key?.GetValue("Mode")   is int m ? m : 0;

                // Sliders (handlers run but SaveSettings is suppressed by _loading).
                HueSlider.Value        = savedHue;
                BrightnessSlider.Value = savedBrightness * 100.0;
                OpacitySlider.Value    = savedOpacity * 100.0;
                SizeSlider.Value       = Math.Clamp(savedSize, 0, 2);

                // Restore exact field values (hue handler forces sat to 0.55; override it).
                currentHue        = savedHue;
                currentSat        = savedSat;
                currentBrightness = savedBrightness;
                currentOpacity    = savedOpacity;
                ApplyTheme();

                SetLayout(Math.Clamp(savedLayout, 0, 1));
                SetSize(Math.Clamp(savedSize, 0, 2));

                currentMode = (KeyboardMode)Math.Clamp(savedMode, 0, 2);
                ApplyMode();

                // Run-on-startup: the Run key is the source of truth.
                runOnStartup = IsRunOnStartupEnabled();
                UpdateStartupButton();
            }
            catch (Exception ex) { Debug.WriteLine($"LoadSettings failed: {ex.Message}"); }
            finally
            {
                _loading = false;
                _settingsLoaded = true; // saves are now allowed
            }
        }

        private static double ReadDouble(RegistryKey? key, string name, double fallback)
        {
            if (key?.GetValue(name) is string s &&
                double.TryParse(s, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double v))
                return v;
            return fallback;
        }

        private bool IsRunOnStartupEnabled()
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey);
                return run?.GetValue(RunValue) != null;
            }
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
                    string? exe = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exe))
                        run.SetValue(RunValue, $"\"{exe}\"", RegistryValueKind.String);
                }
                else if (run.GetValue(RunValue) != null)
                {
                    run.DeleteValue(RunValue, false);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"SetRunOnStartup failed: {ex.Message}"); }

            UpdateStartupButton();
        }

        private void UpdateStartupButton()
        {
            if (BtnStartup != null)
                BtnStartup.Content = runOnStartup ? "Run on startup: On" : "Run on startup: Off";
        }

        private void Key_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag != null)
            {
                string tag = btn.Tag.ToString() ?? "";

                if (tag == "ESC")
                {
                    // Don't let Esc's focus change auto-hide the keyboard. Esc can fire
                    // several focus events (e.g. closing a dialog then refocusing), so
                    // suppress hides for a short window rather than just the next one.
                    _suppressHideUntil = DateTime.Now.AddMilliseconds(800);
                    _hideTimer.Stop();
                }

                if (_mod.ContainsKey(tag))
                {
                    // A long-press already locked this modifier; swallow the matching click.
                    if (_longPressFired && tag == _longPressTag)
                    {
                        _longPressFired = false;
                        return;
                    }
                    // Tap: Off -> OneShot, OneShot/Locked -> Off.
                    SetModState(tag, _mod[tag] == ModState.Off ? ModState.OneShot : ModState.Off);
                    return;
                }

                byte vk = GetVirtualKeyCode(tag);

                // Fn remaps the number row to F1-F12 (and Backspace to Delete) while active.
                if (_mod["TOGGLE_FN"] != ModState.Off && FnMap.TryGetValue(tag, out var fm)) vk = fm.Vk;

                if (vk != 0)
                {
                    bool ctrl  = _mod["TOGGLE_CTRL"]  != ModState.Off;
                    bool alt   = _mod["TOGGLE_ALT"]   != ModState.Off;
                    bool shift = _mod["TOGGLE_SHIFT"] != ModState.Off;
                    bool win   = _mod["TOGGLE_WIN"]   != ModState.Off;

                    if (ctrl)  keybd_event(VK_LCONTROL, 0, 0, 0);
                    if (alt)   keybd_event(VK_LMENU, 0, 0, 0);
                    if (shift) keybd_event(VK_LSHIFT, 0, 0, 0);
                    if (win)   keybd_event(VK_LWIN, 0, 0, 0);

                    keybd_event(vk, 0, 0, 0);
                    keybd_event(vk, 0, KEYEVENTF_KEYUP, 0);

                    if (win)   keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, 0);
                    if (shift) keybd_event(VK_LSHIFT, 0, KEYEVENTF_KEYUP, 0);
                    if (alt)   keybd_event(VK_LMENU, 0, KEYEVENTF_KEYUP, 0);
                    if (ctrl)  keybd_event(VK_LCONTROL, 0, KEYEVENTF_KEYUP, 0);

                    ConsumeOneShotModifiers();
                }
            }
        }

        // --- Modifier state machine ------------------------------------------

        private void SetModState(string tag, ModState state)
        {
            if (!_mod.ContainsKey(tag)) return;
            _mod[tag] = state;

            // Off -> themed key brush; OneShot -> accent; Locked -> amber.
            ApplyToTag(this, tag, btn =>
            {
                switch (state)
                {
                    case ModState.Off:     btn.ClearValue(Button.BackgroundProperty); break;
                    case ModState.OneShot: btn.Background = new SolidColorBrush(ActiveColor); break;
                    case ModState.Locked:  btn.Background = new SolidColorBrush(LockColor); break;
                }
            });

            // Shift and Fn both change key labels.
            if (tag == "TOGGLE_SHIFT" || tag == "TOGGLE_FN") UpdateKeys(this);
        }

        private void ConsumeOneShotModifiers()
        {
            foreach (var t in ModTags)
                if (_mod[t] == ModState.OneShot) SetModState(t, ModState.Off);
        }

        // Printable punctuation keys: tag -> (normal label, shifted label). The virtual-key
        // codes live in the unified Vk table below.
        private static readonly Dictionary<string, (string Normal, string Shifted)> Punct = new()
        {
            ["COMMA"]     = (",",  "<"),
            ["PERIOD"]    = (".",  ">"),
            ["SLASH"]     = ("/",  "?"),
            ["GRAVE"]     = ("`",  "~"),
            ["MINUS"]     = ("-",  "_"),
            ["EQUALS"]    = ("=",  "+"),
            ["BACKSLASH"] = ("\\", "|"),
        };

        // Single source of truth for virtual-key codes of every non-letter/non-digit key
        // (letters and digits map to their ASCII value directly, see GetVirtualKeyCode).
        private static readonly Dictionary<string, byte> Vk = new()
        {
            ["COMMA"] = 0xBC, ["PERIOD"] = 0xBE, ["SLASH"] = 0xBF, ["GRAVE"] = 0xC0,
            ["MINUS"] = 0xBD, ["EQUALS"] = 0xBB, ["BACKSLASH"] = 0xDC,

            ["BACK"] = 0x08, ["TAB"] = 0x09, ["ENTER"] = 0x0D, ["ESC"] = 0x1B, ["SPACE"] = 0x20,

            ["LEFT"] = 0x25, ["UP"] = 0x26, ["RIGHT"] = 0x27, ["DOWN"] = 0x28,
            ["PGUP"] = 0x21, ["PGDN"] = 0x22, ["END"] = 0x23, ["HOME"] = 0x24,

            ["NUMLK"] = 0x90, ["NUMSLASH"] = 0x6F, ["NUMSTAR"] = 0x6A,
            ["NUMMINUS"] = 0x6D, ["NUMPLUS"] = 0x6B,
            ["NUM0"] = 0x60, ["NUM1"] = 0x61, ["NUM2"] = 0x62, ["NUM3"] = 0x63, ["NUM4"] = 0x64,
            ["NUM5"] = 0x65, ["NUM6"] = 0x66, ["NUM7"] = 0x67, ["NUM8"] = 0x68, ["NUM9"] = 0x69,
            ["NUMDOT"] = 0x6E,
        };

        // While Fn is active: number row -> F1-F12, and Backspace -> Delete.
        private static readonly Dictionary<string, (byte Vk, string Label)> FnMap = new()
        {
            ["1"] = (0x70, "F1"),  ["2"] = (0x71, "F2"),  ["3"] = (0x72, "F3"),
            ["4"] = (0x73, "F4"),  ["5"] = (0x74, "F5"),  ["6"] = (0x75, "F6"),
            ["7"] = (0x76, "F7"),  ["8"] = (0x77, "F8"),  ["9"] = (0x78, "F9"),
            ["0"] = (0x79, "F10"), ["MINUS"] = (0x7A, "F11"), ["EQUALS"] = (0x7B, "F12"),
            ["BACK"] = (0x2E, "Del"),
        };

        // --- Long-press detection (locks a modifier) -------------------------

        private void AttachModifierHandlers()
        {
            foreach (var t in ModTags)
            {
                ApplyToTag(this, t, btn =>
                {
                    string? bt = btn.Tag?.ToString();
                    btn.PreviewMouseLeftButtonDown += (s, e) => StartLongPress(bt);
                    btn.PreviewMouseLeftButtonUp   += (s, e) => StopLongPress();
                    btn.PreviewTouchDown           += (s, e) => StartLongPress(bt);
                    btn.PreviewTouchUp             += (s, e) => StopLongPress();
                });
            }
        }

        private void StartLongPress(string? tag)
        {
            if (tag == null) return;
            // Mouse + touch can both fire for one press; only the first arms the timer.
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
                _longPressFired = true;          // tells the click handler to skip the tap toggle
                SetModState(_longPressTag, ModState.Locked);
            }
        }

        private void ApplyToTag(DependencyObject parent, string tag, Action<Button> action)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(parent))
            {
                if (child is Button btn && btn.Tag?.ToString() == tag)
                    action(btn);
                else if (child is DependencyObject dep)
                    ApplyToTag(dep, tag, action);
            }
        }

        private void UpdateKeys(DependencyObject parent)
        {
            bool isShifted = _mod["TOGGLE_SHIFT"] != ModState.Off;
            bool isFn      = _mod["TOGGLE_FN"]    != ModState.Off;

            foreach (object child in LogicalTreeHelper.GetChildren(parent))
            {
                if (child is Button btn && btn.Tag != null)
                {
                    string tag = btn.Tag.ToString() ?? "";

                    if (tag == "BACK")
                        btn.Content = isFn ? "Del" : "⌫";   // Fn turns Backspace into Delete
                    else if (isFn && FnMap.TryGetValue(tag, out var fm))
                        btn.Content = fm.Label;
                    else if (tag.Length == 1 && char.IsLetter(tag[0]))
                        btn.Content = isShifted ? tag.ToUpper() : tag.ToLower();
                    else if (tag.Length == 1 && char.IsDigit(tag[0]))
                        btn.Content = isShifted ? GetShiftedNumber(tag[0]) : tag;
                    else if (Punct.TryGetValue(tag, out var p))
                        btn.Content = isShifted ? p.Shifted : p.Normal;
                    // other tags (ENTER, SPACE, arrows, toggles, nav, numpad): leave content as-is
                }
                else if (child is DependencyObject depObj)
                {
                    UpdateKeys(depObj);
                }
            }
        }

        private string GetShiftedNumber(char c)
        {
            return c switch
            {
                '1' => "!",
                '2' => "@",
                '3' => "#",
                '4' => "$",
                '5' => "%",
                '6' => "^",
                '7' => "&",
                '8' => "*",
                '9' => "(",
                '0' => ")",
                _ => c.ToString()
            };
        }

        private static byte GetVirtualKeyCode(string keyTag)
        {
            if (keyTag.Length == 1)
            {
                char c = keyTag[0];
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                    return (byte)c;
            }

            return Vk.TryGetValue(keyTag, out byte v) ? v : (byte)0;
        }
    }
}
