using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
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

        private static readonly Color ActiveColor   = Color.FromRgb(74, 144, 226);
        private static readonly Color InactiveColor = Color.FromRgb(37, 37, 37);

        private int currentSizeState   = 1;
        private int currentLayoutState = 0;
        private bool isShiftActive = false;
        private bool isCtrlActive  = false;
        private bool isAltActive   = false;
        private bool isManuallyHidden = false;

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
        }

        private void HideTimer_Tick(object? sender, EventArgs e)
        {
            _hideTimer.Stop();
            if (!isManuallyHidden && this.Visibility == Visibility.Visible)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] HIDING keyboard (no text field focused)");
                this.Visibility = Visibility.Collapsed;
            }
        }

        private void OnFocusChanged(object? sender, AutomationFocusChangedEventArgs e)
        {
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

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            isManuallyHidden = true;
            this.Visibility = Visibility.Collapsed;
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Manually minimized (hidden by user)");
        }

        private void SizeButton_Click(object sender, RoutedEventArgs e)
        {
            currentSizeState = (currentSizeState + 1) % 3;
            switch (currentSizeState)
            {
                case 0: this.Width = 600;  this.Height = 254; break;
                case 1: this.Width = 850;  this.Height = 360; break;
                case 2: this.Width = 1200; this.Height = 508; break;
            }
        }

        private void LayoutButton_Click(object sender, RoutedEventArgs e)
        {
            currentLayoutState = (currentLayoutState + 1) % 4;

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
            SetModifierButtonColor("TOGGLE_SHIFT", InactiveColor);
            SetModifierButtonColor("TOGGLE_CTRL",  InactiveColor);
            SetModifierButtonColor("TOGGLE_ALT",   InactiveColor);
        }

        private void Key_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag != null)
            {
                string tag = btn.Tag.ToString() ?? "";

                if (tag == "TOGGLE_SHIFT")
                {
                    isShiftActive = !isShiftActive;
                    SetModifierButtonColor("TOGGLE_SHIFT", isShiftActive ? ActiveColor : InactiveColor);
                    UpdateKeys(this, isShiftActive);
                    return;
                }

                if (tag == "TOGGLE_CTRL")
                {
                    isCtrlActive = !isCtrlActive;
                    SetModifierButtonColor("TOGGLE_CTRL", isCtrlActive ? ActiveColor : InactiveColor);
                    return;
                }

                if (tag == "TOGGLE_ALT")
                {
                    isAltActive = !isAltActive;
                    SetModifierButtonColor("TOGGLE_ALT", isAltActive ? ActiveColor : InactiveColor);
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

        private void SetModifierButtonColor(string tag, Color color)
        {
            ApplyColorToTag(this, tag, new SolidColorBrush(color));
        }

        private void ApplyColorToTag(DependencyObject parent, string tag, Brush brush)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(parent))
            {
                if (child is Button btn && btn.Tag?.ToString() == tag)
                    btn.Background = brush;
                else if (child is DependencyObject dep)
                    ApplyColorToTag(dep, tag, brush);
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
