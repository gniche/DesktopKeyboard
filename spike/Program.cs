using Avalonia;

namespace DesktopKeyboardSpike;

// Minimal Avalonia entry point. Goal of this throwaway spike: prove that a GPU-composited
// transparent, topmost, non-activating, uiAccess window can (1) type into other apps
// (incl. elevated), (2) stay above shell UI without stealing focus, and (3) cost less CPU
// per keypress than the WPF layered-window build. Measure with the perf log it writes.
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();
}
