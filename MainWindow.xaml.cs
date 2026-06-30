using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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

        [DllImport("user32.dll")] public static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")] public static extern IntPtr GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

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
        private bool isShiftActive = false;
        private bool isCtrlActive  = false;
        private bool isAltActive   = false;
        private bool isManuallyHidden = false;

        private enum KeyboardMode { Auto, Show, Hide }
        private KeyboardMode currentMode = KeyboardMode.Auto;
        private Window? _modeWindow;
        private Button? _modeButton;

        private bool runOnStartup = false;
        private bool _loading = false;   // suppresses SaveSettings while applying loaded values

        private const string SettingsKey = @"Software\serifpersia\DesktopKeyboard";
        private const string RunKey      = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue    = "DesktopKeyboard";

        private readonly string[] layoutNames = { "⌨ Base", "⌨ Nav", "⌨ Fn", "⌨ Full" };
        private readonly (double W, double H)[] layoutSizes =
        {
            (850,  360),
            (850,  360),
            (850,  360),
            (1150, 360),
        };

        private readonly DispatcherTimer _hideTimer;

        public MainWindow()
        {
            InitializeComponent();

            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _hideTimer.Tick += HideTimer_Tick;

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
            var helper = new WindowInteropHelper(this);
            SetWindowLong(helper.Handle, GWL_EXSTYLE, new IntPtr(GetWindowLong(helper.Handle, GWL_EXSTYLE).ToInt64() | WS_EX_NOACTIVATE));
            SetWindowPos(helper.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);

            // Create the floating mode button and restore saved settings immediately at
            // startup — independent of whether the keyboard itself is visible yet.
            CreateModeButton();
            LoadSettings();
        }

        private void HideTimer_Tick(object? sender, EventArgs e)
        {
            _hideTimer.Stop();
            if (currentMode != KeyboardMode.Auto) return;
            if (!isManuallyHidden && this.Visibility == Visibility.Visible)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] HIDING keyboard (no text field focused)");
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
                string elementInfo = GetElementDebugInfo(fe);
                bool isTextField = IsEditableTextField(fe);

                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Focus changed → {elementInfo} | Editable: {isTextField}");

                if (isTextField)
                {
                    _hideTimer.Stop();
                    isManuallyHidden = false;

                    if (this.Visibility != Visibility.Visible)
                    {
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] SHOWING keyboard");
                        this.Visibility = Visibility.Visible;
                    }

                    var helper = new WindowInteropHelper(this);
                    SetWindowPos(helper.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                }
                else if (!isManuallyHidden && this.Visibility == Visibility.Visible)
                {
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Starting hide timer (200ms)...");
                    _hideTimer.Start();
                }
            });
        }

        private bool IsEditableTextField(AutomationElement element)
        {
            if (element == null) return false;

            var ct = element.Current.ControlType;
            string ctName = ct?.ProgrammaticName ?? "Unknown";

            if (ct == ControlType.Edit || ct == ControlType.ComboBox)
            {
                Debug.WriteLine($"    → Accepted: {ctName} (standard text field)");
                return true;
            }

            if (ct == ControlType.ListItem || ct == ControlType.TreeItem ||
                ct == ControlType.Button || ct == ControlType.Group || ct == ControlType.Pane)
            {
                Debug.WriteLine($"    → Rejected: {ctName} (common false positive)");
                return false;
            }

            try
            {
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? valueObj) &&
                    valueObj is ValuePattern vp)
                {
                    bool readOnly = vp.Current.IsReadOnly;
                    Debug.WriteLine($"    → ValuePattern found | IsReadOnly: {readOnly}");

                    string name = element.Current.Name ?? "";
                    if (name.Length < 3 || IsLikelyFileName(name))
                    {
                        Debug.WriteLine($"    → Rejected ValuePattern (looks like filename/folder)");
                        return false;
                    }
                    return !readOnly;
                }

                if (element.TryGetCurrentPattern(TextPattern.Pattern, out object? textObj) && textObj != null)
                {
                    Debug.WriteLine("    → TextPattern found (rich editor)");
                    return true;
                }
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

        private string GetElementDebugInfo(AutomationElement element)
        {
            try
            {
                string name = element.Current.Name ?? "(no name)";
                string className = element.Current.ClassName ?? "(no class)";
                string ctName = element.Current.ControlType?.ProgrammaticName ?? "Unknown";

                if (name.Length > 60)
                    name = name.Substring(0, 57) + "...";

                return $"ControlType: {ctName} | Name: \"{name}\" | Class: {className}";
            }
            catch
            {
                return "Error getting element info";
            }
        }

        private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => this.DragMove();

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
            SetLayout((currentLayoutState + 1) % 4);
            SaveSettings();
        }

        private void SetLayout(int state)
        {
            currentLayoutState = state;

            Layout1Panel.Visibility = currentLayoutState == 0 ? Visibility.Visible : Visibility.Collapsed;
            Layout2Panel.Visibility = currentLayoutState == 1 ? Visibility.Visible : Visibility.Collapsed;
            Layout3Panel.Visibility = currentLayoutState == 2 ? Visibility.Visible : Visibility.Collapsed;
            Layout4Panel.Visibility = currentLayoutState == 3 ? Visibility.Visible : Visibility.Collapsed;

            BtnLayout.Content = layoutNames[currentLayoutState];

            var (w, h) = layoutSizes[currentLayoutState];
            MainBorder.Width  = w;
            MainBorder.Height = h;

            // Reset modifiers when changing layouts so they aren't stuck active on a hidden panel
            isShiftActive = false;
            isCtrlActive  = false;
            isAltActive   = false;
            UpdateKeys(this, false);
            SetModifierActive("TOGGLE_SHIFT", false);
            SetModifierActive("TOGGLE_CTRL",  false);
            SetModifierActive("TOGGLE_ALT",   false);
        }

        // --- Hue / Opacity theming -------------------------------------------

        // --- Floating Show/Hide/Auto mode button -----------------------------

        private void CreateModeButton()
        {
            if (_modeWindow != null) return;

            _modeButton = new Button
            {
                Content = "Auto",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Width = 90,
                Height = 56,
                Focusable = false,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            _modeButton.Click += (s, e) => CycleMode();

            var grip = new TextBlock
            {
                Text = "⠿",
                FontSize = 24,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 6, 0),
                Cursor = Cursors.SizeAll
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(grip);
            panel.Children.Add(_modeButton);

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(34, 34, 34)),
                CornerRadius = new CornerRadius(8),
                BorderBrush = new SolidColorBrush(Color.FromRgb(74, 144, 226)),
                BorderThickness = new Thickness(2),
                Child = panel
            };

            _modeWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowActivated = false,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                Title = "Keyboard Toggle",
                Content = border,
                Left = 40,
                Top = 40
            };

            // Drag the floating button anywhere via its grip handle.
            grip.MouseLeftButtonDown += (s, e) =>
            {
                try { _modeWindow?.DragMove(); } catch { }
            };

            // Non-activating + tool window so it floats above everything and never
            // steals focus from the target app or shows in alt-tab.
            _modeWindow.SourceInitialized += (s, e) =>
            {
                var h = new WindowInteropHelper(_modeWindow).Handle;
                SetWindowLong(h, GWL_EXSTYLE, new IntPtr(
                    GetWindowLong(h, GWL_EXSTYLE).ToInt64() | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
                SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
            };

            _modeWindow.Show();
            UpdateModeButton();
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
                    isManuallyHidden = false;
                    this.Visibility = Visibility.Visible;
                    var helper = new WindowInteropHelper(this);
                    SetWindowPos(helper.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                    break;

                case KeyboardMode.Hide:
                    _hideTimer.Stop();
                    this.Visibility = Visibility.Collapsed;
                    ThemePopup.IsOpen = false;
                    break;

                case KeyboardMode.Auto:
                    // Visibility now resolves from input focus on the next focus change.
                    isManuallyHidden = false;
                    break;
            }
            UpdateModeButton();
        }

        private void UpdateModeButton()
        {
            if (_modeButton == null) return;

            (string text, Color color) = currentMode switch
            {
                KeyboardMode.Show => ("Show", Color.FromRgb(46, 160, 67)),   // green
                KeyboardMode.Hide => ("Hide", Color.FromRgb(200, 60, 60)),   // red
                _                 => ("Auto", Color.FromRgb(74, 144, 226)),  // blue
            };

            _modeButton.Content = text;
            _modeButton.Background = new SolidColorBrush(color);
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
            if (_loading) return;
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

                SetLayout(Math.Clamp(savedLayout, 0, 3));
                SetSize(Math.Clamp(savedSize, 0, 2));

                currentMode = (KeyboardMode)Math.Clamp(savedMode, 0, 2);
                ApplyMode();

                // Run-on-startup: the Run key is the source of truth.
                runOnStartup = IsRunOnStartupEnabled();
                UpdateStartupButton();
            }
            catch (Exception ex) { Debug.WriteLine($"LoadSettings failed: {ex.Message}"); }
            finally { _loading = false; }
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

                if (tag == "TOGGLE_SHIFT")
                {
                    isShiftActive = !isShiftActive;
                    SetModifierActive("TOGGLE_SHIFT", isShiftActive);
                    UpdateKeys(this, isShiftActive);
                    return;
                }

                if (tag == "TOGGLE_CTRL")
                {
                    isCtrlActive = !isCtrlActive;
                    SetModifierActive("TOGGLE_CTRL", isCtrlActive);
                    return;
                }

                if (tag == "TOGGLE_ALT")
                {
                    isAltActive = !isAltActive;
                    SetModifierActive("TOGGLE_ALT", isAltActive);
                    return;
                }

                if (tag == "WIN")
                {
                    keybd_event(VK_LWIN, 0, 0, 0);
                    keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, 0);
                    return;
                }

                byte vk = GetVirtualKeyCode(tag);
                if (vk != 0)
                {
                    if (isCtrlActive) keybd_event(VK_LCONTROL, 0, 0, 0);
                    if (isAltActive)  keybd_event(VK_LMENU, 0, 0, 0);
                    if (isShiftActive) keybd_event(VK_LSHIFT, 0, 0, 0);

                    keybd_event(vk, 0, 0, 0);
                    keybd_event(vk, 0, KEYEVENTF_KEYUP, 0);

                    if (isShiftActive) keybd_event(VK_LSHIFT, 0, KEYEVENTF_KEYUP, 0);
                    if (isAltActive)  keybd_event(VK_LMENU, 0, KEYEVENTF_KEYUP, 0);
                    if (isCtrlActive) keybd_event(VK_LCONTROL, 0, KEYEVENTF_KEYUP, 0);
                }
            }
        }

        private void SetModifierActive(string tag, bool active)
        {
            // Active modifiers get the accent colour; inactive ones clear their local
            // Background so they fall back to the themed {DynamicResource KeyBg} brush
            // and recolour automatically when the hue changes.
            ApplyToTag(this, tag, btn =>
            {
                if (active) btn.Background = new SolidColorBrush(ActiveColor);
                else btn.ClearValue(Button.BackgroundProperty);
            });
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

        private void UpdateKeys(DependencyObject parent, bool isShifted)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(parent))
            {
                if (child is Button btn && btn.Tag != null)
                {
                    string tag = btn.Tag.ToString() ?? "";

                    if (tag.Length == 1 && char.IsLetter(tag[0]))
                    {
                        btn.Content = isShifted ? tag.ToUpper() : tag.ToLower();
                    }
                    else if (tag.Length == 1 && char.IsDigit(tag[0]))
                    {
                        btn.Content = isShifted ? GetShiftedNumber(tag[0]) : tag;
                    }
                    else if (tag == "COMMA" || tag == "PERIOD" || tag == "SLASH")
                    {
                        btn.Content = isShifted ? GetShiftedSymbol(tag) : GetNormalSymbol(tag);
                    }
                    // all other tags (BACK, ENTER, SPACE, arrows, toggles, F-keys, nav, numpad): leave content as-is
                }
                else if (child is DependencyObject depObj)
                {
                    UpdateKeys(depObj, isShifted);
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

        private string GetShiftedSymbol(string tag)
        {
            return tag switch
            {
                "COMMA" => "<",
                "PERIOD" => ">",
                "SLASH" => "?",
                _ => tag
            };
        }

        private string GetNormalSymbol(string tag)
        {
            return tag switch
            {
                "COMMA" => ",",
                "PERIOD" => ".",
                "SLASH" => "/",
                _ => tag
            };
        }

        private byte GetVirtualKeyCode(string keyTag)
        {
            if (keyTag.Length == 1)
            {
                char c = keyTag[0];
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                    return (byte)c;
            }

            return keyTag switch
            {
                "BACK" => 0x08,
                "TAB" => 0x09,
                "ENTER" => 0x0D,
                "ESC" => 0x1B,
                "SPACE" => 0x20,
                "COMMA" => 0xBC,
                "PERIOD" => 0xBE,
                "SLASH" => 0xBF,

                "LEFT" => 0x25,
                "UP" => 0x26,
                "RIGHT" => 0x27,
                "DOWN" => 0x28,

                "PGUP" => 0x21,
                "PGDN" => 0x22,
                "END" => 0x23,
                "HOME" => 0x24,
                "INS" => 0x2D,
                "DEL" => 0x2E,

                "F1" => 0x70,
                "F2" => 0x71,
                "F3" => 0x72,
                "F4" => 0x73,
                "F5" => 0x74,
                "F6" => 0x75,
                "F7" => 0x76,
                "F8" => 0x77,
                "F9" => 0x78,
                "F10" => 0x79,
                "F11" => 0x7A,
                "F12" => 0x7B,

                "NUMLK" => 0x90,
                "NUMSLASH" => 0x6F,
                "NUMSTAR" => 0x6A,
                "NUMMINUS" => 0x6D,
                "NUMPLUS" => 0x6B,
                "NUM0" => 0x60,
                "NUM1" => 0x61,
                "NUM2" => 0x62,
                "NUM3" => 0x63,
                "NUM4" => 0x64,
                "NUM5" => 0x65,
                "NUM6" => 0x66,
                "NUM7" => 0x67,
                "NUM8" => 0x68,
                "NUM9" => 0x69,
                "NUMDOT" => 0x6E,

                _ => 0
            };
        }
    }
}
