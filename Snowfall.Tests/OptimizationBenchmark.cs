using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace QuietSnow;

internal static class OptimizationBenchmark
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Get<T>(object app, string name) => (T)app.GetType().GetField(name, Flags)!.GetValue(app)!;
    static void Set(object app, string name, object value) => app.GetType().GetField(name, Flags)!.SetValue(app, value);

    public static void Run()
    {
        using var app = new SnowApp(diagnostics: true);
        try
        {
            foreach (var overlay in Get<List<SnowOverlay>>(app, "overlays")) overlay.Hide();
            Get<Stopwatch>(app, "clock").Reset(); // Identical light phases in both builds.
            var surfaces = Get<List<Surface>>(app, "ordered");
            surfaces.Clear(); Get<List<Flake>>(app, "flakes").Clear();
            var viewport = new Rectangle(0, 0, 1920, 1080);
            for (int i = 0; i < 5; i++)
            {
                var s = new Surface { Handle = 100 + i, Age = 75, IsExplorer = i == 0, AppName = i == 0 ? "explorer" : "test" };
                s.Resize(new Rectangle(60 + i * 80, 160 + i * 160, 1420 - i * 90, 140));
                for (int col = 0; col < s.Snow.Length; col++) s.Snow[col] = 2 + MathF.Sin(col * .2f);
                surfaces.Add(s);
            }
            var ground = new Surface { Handle = -1, Age = 75 };
            ground.Resize(new Rectangle(0, 1032, 1920, 48)); surfaces.Add(ground);
            using var layer = new SnowOverlay(app, viewport); _ = layer.Handle;
            var gpu = layer.GpuBackend ?? throw new Exception(layer.GpuFailureReason);
            var results = new Dictionary<string, object>();
            void Bench(string name, Action<int> change)
            {
                for (int i = 0; i < 40; i++) { change(i); gpu.Render(app, viewport, false); }
                var ms = new List<double>(240); long uploads = 0;
                long alloc = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 240; i++)
                {
                    change(i);
                    long start = Stopwatch.GetTimestamp();
                    gpu.Render(app, viewport, false);
                    ms.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    uploads += gpu.RasterUploadBytes;
                }
                long bytes = GC.GetAllocatedBytesForCurrentThread() - alloc;
                results[name] = new { MedianMs = ms.Order().ElementAt(ms.Count / 2), MeanMs = ms.Average(), P95Ms = ms.Order().ElementAt((int)(ms.Count * .95)), BytesPerFrame = bytes / ms.Count, UploadKiB = uploads / 1024d / ms.Count, SurfaceMiB = gpu.RasterSurfaceBytes / 1048576d };
            }
            Set(app, "garlandMode", 0);
            Bench("static", _ => {});
            Bench("one-growing-bank", i => surfaces[2].Snow[20] = i % 2 == 0 ? 3 : 7);
            Set(app, "garlandMode", 1);
            Bench("animated-garland", i => surfaces[0].GarlandY = i % 2 == 0 ? 0 : .1f);
            Set(app, "garlandMode", 0);
            Bench("all-growing", i => { foreach (var s in surfaces) s.Snow[20] = i % 2 == 0 ? 3 : 7; });
            Bench("moving-window", i => surfaces[1].Bounds.Offset(i % 2 == 0 ? 2 : -2, 0));
            string json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "metrics.json"), json); Console.WriteLine(json);
            gpu.Render(app, viewport, false); using var image = gpu.Capture(); image.Save(Path.Combine(AppContext.BaseDirectory, "scene.png"));
        }
        finally { app.ExitThread(); }
    }
}
