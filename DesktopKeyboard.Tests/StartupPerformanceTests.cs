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

    [Fact]
    public void Startup_ShouldBeReadyWithin2Seconds()
    {
        Assert.True(File.Exists(ExePath), $"Executable not found at: {ExePath}\nBuild DesktopKeyboard first.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = true,
            }
        };

        var sw = Stopwatch.StartNew();
        process.Start();

        // WaitForInputIdle blocks until the app's UI thread is pumping messages
        // (i.e. the window is rendered and the app is ready for input).
        bool ready = process.WaitForInputIdle(TargetMs + 1000);
        sw.Stop();

        try
        {
            process.Kill();
            process.WaitForExit(2000);
        }
        catch { /* already exited */ }

        output.WriteLine($"Startup time: {sw.ElapsedMilliseconds} ms");
        output.WriteLine($"Target:       {TargetMs} ms");
        output.WriteLine($"Status:       {(ready ? "Ready" : "Timed out before ready")}");

        Assert.True(ready, $"App did not become ready within {TargetMs + 1000} ms.");
        Assert.True(sw.ElapsedMilliseconds < TargetMs,
            $"Startup took {sw.ElapsedMilliseconds} ms — exceeds {TargetMs} ms target.");
    }

    [Fact]
    public void Startup_MeasureBaseline_MultipleRuns()
    {
        Assert.True(File.Exists(ExePath), $"Executable not found at: {ExePath}\nBuild DesktopKeyboard first.");

        const int runs = 5;
        var times = new List<long>();

        for (int i = 0; i < runs; i++)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(ExePath)
                {
                    UseShellExecute = true,
                }
            };

            var sw = Stopwatch.StartNew();
            process.Start();
            bool ready = process.WaitForInputIdle(5000);
            sw.Stop();

            try { process.Kill(); process.WaitForExit(2000); } catch { }

            times.Add(sw.ElapsedMilliseconds);
            output.WriteLine($"Run {i + 1}: {sw.ElapsedMilliseconds} ms {(ready ? "" : "(timed out)")}");

            // Brief pause between runs so the OS settles.
            Thread.Sleep(500);
        }

        long avg = (long)times.Average();
        long min = times.Min();
        long max = times.Max();

        output.WriteLine($"\nMin: {min} ms | Avg: {avg} ms | Max: {max} ms");
        output.WriteLine($"Target: < {TargetMs} ms");

        Assert.True(avg < TargetMs, $"Average startup ({avg} ms) exceeds {TargetMs} ms target.");
    }
}
