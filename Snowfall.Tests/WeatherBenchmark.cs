using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace QuietSnow;

internal static class WeatherBenchmark
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] static extern int GetGuiResources(nint process, uint flags);
    public static void Run()
    {
        using var app = new SnowApp(diagnostics: true);
        bool exited = false;
        try
        {
            typeof(SnowApp).GetField("autoGameMode", Flags)!.SetValue(app, false);
            typeof(SnowApp).GetField("hideFullscreen", Flags)!.SetValue(app, false);
            app.SetSnowAmount(200); app.SetFallSpeed(200);
            var tick = typeof(SnowApp).GetMethod("Tick", Flags)!.CreateDelegate<Action>(app);
            var layers = (List<SnowOverlay>)typeof(SnowApp).GetField("overlays", Flags)!.GetValue(app)!;
            var surfaces = (List<Surface>)typeof(SnowApp).GetField("ordered", Flags)!.GetValue(app)!;
            var rows = new List<object>();
            using var process = Process.GetCurrentProcess();
            using var dispatcher = new Control(); _ = dispatcher.Handle;
            var elapsed = Stopwatch.StartNew();
            int modeIndex = 0, frames = 0;
            bool measuring = false;
            double start = 0, cpu = 0;
            using var timer = new FrameClock(() =>
            {
                tick();
                if (!measuring && elapsed.Elapsed.TotalSeconds >= 2)
                {
                    measuring = true; frames = 0; start = elapsed.Elapsed.TotalSeconds;
                    cpu = process.TotalProcessorTime.TotalSeconds;
                }
                if (measuring) frames++;
                if (elapsed.Elapsed.TotalSeconds < 9) return;
                process.Refresh(); double seconds = elapsed.Elapsed.TotalSeconds - start;
                var row = new { Cycle = modeIndex / 4 + 1, Mode = app.Weather.ToString(), Seconds = seconds,
                    Fps = frames / seconds, CpuPercent = 100 * (process.TotalProcessorTime.TotalSeconds - cpu) / seconds / Environment.ProcessorCount,
                    WorkingMiB = process.WorkingSet64 / 1048576d, PrivateMiB = process.PrivateMemorySize64 / 1048576d,
                    GdiObjects = GetGuiResources(process.Handle, 0), Particles = app.GpuFlakes.Count, Dust = app.GpuDust.Count,
                    RasterMiB = layers.Sum(layer => layer.GpuBackend?.RasterSurfaceBytes ?? 0) / 1048576d,
                    Errors = layers.Select(layer => layer.LastPresentationError).ToArray() };
                rows.Add(row); Console.WriteLine(JsonSerializer.Serialize(row));
                modeIndex++;
                if (modeIndex >= 8)
                {
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "weather-benchmark.json"),
                        JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
                    exited = true; app.ExitThread(); return;
                }
                app.SetWeatherMode((WeatherMode)(modeIndex % 4));
                foreach (var surface in surfaces) surface.Age = app.Weather == WeatherMode.Rain ? 0 : 75;
                measuring = false; elapsed.Restart();
            });
            foreach (var surface in surfaces) surface.Age = 75;
            timer.Start(dispatcher); Application.Run(app);
        }
        finally { if (!exited) app.ExitThread(); }
    }
}
