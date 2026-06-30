using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace DesktopKeyboardSpike;

// Transparent, topmost, non-activating, uiAccess test window. Renders keys as Borders
// (no control theme needed) and synthesizes input via SendInput, mirroring the real app.
public class SpikeWindow : Window
{
    // --- Win32 interop (same as the real app) --------------------------------
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001;
    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_LCONTROL = 0xA2;

    [DllImport("user32.dll")] static extern IntPtr GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLong(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("psapi.dll")] static extern int EmptyWorkingSet(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public KEYBDINPUT ki; public int _a, _b; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    // --- Keys: (label, virtual-key, wrap-in-ctrl) ----------------------------
    private static readonly (string L, ushort Vk, bool Ctrl)[] Keys =
    {
        ("Q", 0x51, false), ("W", 0x57, false), ("E", 0x45, false), ("R", 0x52, false), ("T", 0x54, false),
        ("A", 0x41, false), ("S", 0x53, false), ("D", 0x44, false), ("F", 0x46, false), ("G", 0x47, false),
        ("Spc", 0x20, false), ("Ent", 0x0D, false), ("Ctrl+C", 0x43, true), ("Ctrl+V", 0x56, true),
    };

    private readonly INPUT[] _buf = new INPUT[6];
    private readonly DispatcherTimer _perf = new() { Interval = TimeSpan.FromSeconds(2) };
    private TimeSpan _lastCpu;
    private long _lastTick;
    private readonly string _log = Path.Combine(Path.GetTempPath(), "DesktopKeyboardSpike_perf.log");

    public SpikeWindow()
    {
        Title = "Keyboard Spike";
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = false;
        Width = 600; Height = 170;

        Content = BuildKeyboard();

        Opened += OnOpened;
    }

    private Control BuildKeyboard()
    {
        var panel = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
        };

        var grid = new UniformGrid { Columns = 5 };
        foreach (var k in Keys)
            grid.Children.Add(BuildKey(k.L, k.Vk, k.Ctrl));

        panel.Child = grid;
        return panel;
    }

    private Control BuildKey(string label, ushort vk, bool ctrl)
    {
        var baseBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(3),
        };

        // Press highlight: fades out over 0.12s on release — exercises the compositor the
        // same way the WPF press-fade did, so the perf log reflects a comparable workload.
        var highlight = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(3),
            Opacity = 0,
            Transitions = new Transitions
            {
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromSeconds(0.12) },
            },
        };

        var text = new TextBlock
        {
            Text = label,
            Foreground = Brushes.White,
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var cell = new Panel();
        cell.Children.Add(baseBorder);
        cell.Children.Add(highlight);
        cell.Children.Add(text);

        cell.PointerPressed += (_, _) => { highlight.Opacity = 1; SendKey(vk, ctrl); };
        cell.PointerReleased += (_, _) => highlight.Opacity = 0;
        cell.PointerExited += (_, _) => highlight.Opacity = 0;
        return cell;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            long ex = GetWindowLong(hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            SetWindowLong(hwnd, GWL_EXSTYLE, new IntPtr(ex));
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        }

        var wa = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        Position = new PixelPoint(
            wa.X + (wa.Width - (int)Width) / 2,
            wa.Y + wa.Height - (int)Height - 60);

        using (var p = Process.GetCurrentProcess()) _lastCpu = p.TotalProcessorTime;
        _lastTick = Environment.TickCount64;
        Write($"--- spike start, cores={Environment.ProcessorCount} ---");

        // Trim startup pages so the idle ws reading is comparable to the WPF build's trim.
        try { EmptyWorkingSet(GetCurrentProcess()); } catch { }

        _perf.Tick += PerfTick;
        _perf.Start();
    }

    private void SendKey(ushort vk, bool ctrl)
    {
        int n = 0;
        if (ctrl) _buf[n++] = Key(VK_LCONTROL, false);
        _buf[n++] = Key(vk, false);
        _buf[n++] = Key(vk, true);
        if (ctrl) _buf[n++] = Key(VK_LCONTROL, true);
        SendInput((uint)n, _buf, InputSize);
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 },
    };

    private void PerfTick(object? sender, EventArgs e)
    {
        using var p = Process.GetCurrentProcess();
        long now = Environment.TickCount64;
        double wallMs = now - _lastTick;
        TimeSpan cpu = p.TotalProcessorTime;
        double cpuMs = (cpu - _lastCpu).TotalMilliseconds;
        _lastCpu = cpu; _lastTick = now;

        double cpuPct = wallMs > 0 ? cpuMs / (wallMs * Environment.ProcessorCount) * 100.0 : 0;
        double wsMb = p.WorkingSet64 / (1024.0 * 1024.0);
        double gcMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        Write($"cpu={cpuPct,5:F1}%  ws={wsMb,6:F1}MB  gcHeap={gcMb,5:F1}MB");
    }

    private void Write(string body)
    {
        try { File.AppendAllText(_log, $"{DateTime.Now:HH:mm:ss}  {body}{Environment.NewLine}"); }
        catch { }
    }
}
