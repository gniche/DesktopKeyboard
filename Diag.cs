using System.Diagnostics;
using System.IO;
using Avalonia.Threading;
using Microsoft.Win32;
using UIA = Interop.UIAutomationClient;
using static DesktopKeyboard.MainWindow;

namespace DesktopKeyboard;

// Opt-in diagnostics (registry "Diag"=1 under the settings key, or env DESKTOPKEYBOARD_DIAG=1):
// a perf log and a focus-classification log under %TEMP%. Zero-overhead when off.
internal static class Diag
{
    public static bool On { get; private set; }

    public static void Init()
    {
        On = Environment.GetEnvironmentVariable("DESKTOPKEYBOARD_DIAG") == "1";
        if (On) return;
        try { using var k = Registry.CurrentUser.OpenSubKey(SettingsKey); On = k?.GetValue("Diag") is 1; }
        catch { }
    }

    private static readonly object _lock = new();

    // Appends one line to a log file under %TEMP%. Safe from any thread (UIA callbacks,
    // the MTA focus thread, the UI thread); swallows IO errors.
    public static void Log(string file, string msg)
    {
        try
        {
            lock (_lock)
                File.AppendAllText(Path.Combine(Path.GetTempPath(), file),
                    $"{DateTime.Now:HH:mm:ss.fff} [t{Environment.CurrentManagedThreadId}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    public static void Focus(string msg) { if (On) Log("DesktopKeyboard_focus.log", msg); }

    // --- Perf log: whole-process CPU% / working set / GC heap every 2 s -------
    private const string PerfFile = "DesktopKeyboard_perf.log";
    private static TimeSpan _lastCpu;
    private static long _lastTick;

    public static void StartPerfLog(Func<bool> bodyVisible)
    {
        if (!On) return;
        using (var proc = Process.GetCurrentProcess()) _lastCpu = proc.TotalProcessorTime;
        _lastTick = Environment.TickCount64;
        Log(PerfFile, $"--- session start (Avalonia), cores={Environment.ProcessorCount} ---");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };   // rooted while running
        timer.Tick += (_, _) => PerfTick(bodyVisible);
        timer.Start();
    }

    private static void PerfTick(Func<bool> bodyVisible)
    {
        using var proc = Process.GetCurrentProcess();
        long now = Environment.TickCount64;
        double wallMs = now - _lastTick;
        TimeSpan cpu = proc.TotalProcessorTime;
        double cpuMs = (cpu - _lastCpu).TotalMilliseconds;
        _lastCpu = cpu; _lastTick = now;
        double cpuPct = wallMs > 0 ? cpuMs / (wallMs * Environment.ProcessorCount) * 100.0 : 0;
        double wsMb = proc.WorkingSet64 / (1024.0 * 1024.0);
        double gcMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        Log(PerfFile, $"cpu={cpuPct,5:F1}%  ws={wsMb,6:F1}MB  gcHeap={gcMb,5:F1}MB  visible={(bodyVisible() ? 1 : 0)}");
    }

    // --- Focus-log helpers -----------------------------------------------------

    // Last-write time of the running assembly — proves which binary is live, so a stale
    // install (MSI skipping same-version file replacement) is obvious in the log.
    public static string BuildStamp()
    {
        try
        {
            string loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
            return string.IsNullOrEmpty(loc) ? "?" : File.GetLastWriteTime(loc).ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch { return "?"; }
    }

    // Control-type id -> short name ("UIA_EditControlTypeId" -> "Edit"), reflected once
    // from the interop constants. Only ever called with Diag on.
    private static Dictionary<int, string>? _ctNames;
    public static string CtName(int ct)
    {
        if (_ctNames == null)
        {
            var d = new Dictionary<int, string>();
            foreach (var f in typeof(UIA.UIA_ControlTypeIds).GetFields())
                if (f.GetRawConstantValue() is int v) d[v] = f.Name[4..^13];
            _ctNames = d;
        }
        return _ctNames.TryGetValue(ct, out var n) ? n : "ct#" + ct;
    }

    // Full signal snapshot for one focused element (event path only, so it never spams).
    public static string Snapshot(UIA.IUIAutomationElement el)
    {
        string name = GetCachedString(el, Prop.Name);
        if (name.Length > 40) name = name[..40] + "…";
        int role = GetCachedInt(el, Prop.LegacyRole, -1);
        int state = GetCachedInt(el, Prop.LegacyState, -1);
        return $"  signals: ct={CtName(GetCachedInt(el, Prop.ControlType, -1))} name=\"{name}\" " +
               $"enabled={GetCachedBool(el, Prop.Enabled, true)} pwd={GetCachedBool(el, Prop.Password)} " +
               $"value={GetCachedBool(el, Prop.HasValue)}(ro={GetCachedBool(el, Prop.ValueReadOnly)}) " +
               $"textEdit={GetCachedBool(el, Prop.HasTextEdit)} " +
               $"legacy={GetCachedBool(el, Prop.HasLegacy)}(role=0x{role:X} state=0x{state:X}) " +
               $"hostClass=\"{GetHostClass(el)}\"";
    }
}
