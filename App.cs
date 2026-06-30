using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace DesktopKeyboard;

public class App : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The keyboard hides itself most of the time, and the floating mode button is a
            // separate window — so the app must not exit just because no window is visible.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = new MainWindow(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
