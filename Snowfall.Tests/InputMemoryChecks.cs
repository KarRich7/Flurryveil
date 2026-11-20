using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace QuietSnow;

// Opt-in native-memory regression. Normal functional checks stay short.
internal static class InputMemoryChecks
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(object app, string name) => (T)app.GetType().GetField(name, Flags)!.GetValue(app)!;
    [DllImport("user32.dll")] static extern int GetGuiResources(nint process, uint flags);

    public static void Run()
    {
        using var app = new SnowApp(diagnostics: true);
        try
        {
            foreach (var overlay in Field<List<SnowOverlay>>(app, "overlays")) overlay.Hide();
            var view = Screen.PrimaryScreen!.Bounds;
            var surface = new Surface { Handle = 123, Age = 75 };
            surface.Resize(new Rectangle(view.Left + 100, view.Top + 300, 800, 400));
            var surfaces = Field<List<Surface>>(app, "ordered"); surfaces.Clear(); surfaces.Add(surface);
            using var input = new InputOverlay(app, SystemInformation.VirtualScreen);
            Field<List<InputOverlay>>(app, "inputOverlays").Add(input);
            input.Show();
            using var process = Process.GetCurrentProcess();
            var clock = Stopwatch.StartNew();
            var rows = new List<MemorySample>();
            void Sample(int updates)
            {
                Application.DoEvents();
                // Collection isolates native growth; production never forces GC
                // or trims its working set to make this measurement smaller.
                GC.Collect(); GC.WaitForPendingFinalizers(); process.Refresh();
                var row = new MemorySample(updates, clock.Elapsed.TotalSeconds,
                    process.PrivateMemorySize64 / 1048576d, process.WorkingSet64 / 1048576d,
                    GC.GetTotalMemory(false) / 1048576d, GetGuiResources(process.Handle, 0));
                rows.Add(row); Console.WriteLine(JsonSerializer.Serialize(row));
            }
            Sample(0);
            const int warmup = 20_000, iterations = 1_000_000, sampleEvery = 100_000;
            for (int update = 1; update <= iterations + warmup; update++)
            {
                surface.Snow[12] = (update & 1) * 5;
                app.RefreshInputRegion();
                if ((update & 127) == 0) Application.DoEvents();
                if (update == warmup || update > warmup && (update - warmup) % sampleEvery == 0) Sample(update);
            }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "memory-soak.json"),
                JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            double growth = rows.Skip(2).Max(row => row.PrivateMiB) - rows[1].PrivateMiB;
            int gdiGrowth = rows.Skip(2).Max(row => row.GdiObjects) - rows[1].GdiObjects;
            if (growth > 16 || gdiGrowth > 2)
                throw new InvalidOperationException($"Native input-region memory grew by {growth:F2} MiB; GDI objects +{gdiGrowth}.");
            Console.WriteLine($"PASS: 1,000,000 changed hit regions after warm-up; peak private growth {growth:F2} MiB, GDI objects +{gdiGrowth}.");
        }
        finally { app.ExitThread(); }
    }

    sealed record MemorySample(int Updates, double Seconds, double PrivateMiB,
        double WorkingMiB, double ManagedMiB, int GdiObjects);
}
