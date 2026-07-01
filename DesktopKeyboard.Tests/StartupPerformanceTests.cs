using System.Diagnostics;
using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace DesktopKeyboard.Tests;

public class StartupPerformanceTests(ITestOutputHelper output)
{
    // Path to the built executable — adjust if your output dir differs.
    private static readonly string ExePath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "bin", "Debug", "net9.0-windows", "DesktopKeyboard.exe"));

    private const int TargetMs = 2000;

    // Launches the exe, waits until its UI thread is pumping messages (window rendered, ready
    // for input), kills it, and returns the elapsed time. `ready` is false if it timed out.
    private static long MeasureStartup(int readyTimeoutMs, out bool ready)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(ExePath) { UseShellExecute = true } };
        var sw = Stopwatch.StartNew();
        process.Start();
        ready = process.WaitForInputIdle(readyTimeoutMs);
        sw.Stop();
        try { process.Kill(); process.WaitForExit(2000); } catch { /* already exited */ }
        return sw.ElapsedMilliseconds;
    }

    [Fact]
    public void Startup_ShouldBeReadyWithin2Seconds()
    {
        Assert.True(File.Exists(ExePath), $"Executable not found at: {ExePath}\nBuild DesktopKeyboard first.");

        long ms = MeasureStartup(TargetMs + 1000, out bool ready);

        output.WriteLine($"Startup time: {ms} ms");
        output.WriteLine($"Target:       {TargetMs} ms");
        output.WriteLine($"Status:       {(ready ? "Ready" : "Timed out before ready")}");

        Assert.True(ready, $"App did not become ready within {TargetMs + 1000} ms.");
        Assert.True(ms < TargetMs, $"Startup took {ms} ms — exceeds {TargetMs} ms target.");
    }

    [Fact]
    public void Startup_MeasureBaseline_MultipleRuns()
    {
        Assert.True(File.Exists(ExePath), $"Executable not found at: {ExePath}\nBuild DesktopKeyboard first.");

        const int runs = 5;
        var times = new List<long>();

        for (int i = 0; i < runs; i++)
        {
            times.Add(MeasureStartup(5000, out bool ready));
            output.WriteLine($"Run {i + 1}: {times[^1]} ms {(ready ? "" : "(timed out)")}");
            Thread.Sleep(500);   // let the OS settle between runs
        }

        long avg = (long)times.Average();
        output.WriteLine($"\nMin: {times.Min()} ms | Avg: {avg} ms | Max: {times.Max()} ms");
        output.WriteLine($"Target: < {TargetMs} ms");

        Assert.True(avg < TargetMs, $"Average startup ({avg} ms) exceeds {TargetMs} ms target.");
    }
}
