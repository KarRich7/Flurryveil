using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace QuietSnow;

internal static class SeasonalChecks
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, Flags)!.GetValue(obj)!;
    static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Flags)!.SetValue(obj, value);
    static int assertions;
    static void Check(bool valid, string message) { if (!valid) throw new Exception(message); assertions++; }
    static float Volume(Surface surface) => surface.Snow.Select((value, i) =>
        Math.Max(0, surface.BaseHeight * surface.SnowScale * (.82f + .12f * MathF.Sin(i * .63f) + .06f * MathF.Sin(i * 1.71f)) + value)).Sum();

    internal static int Run(bool allowUnavailableGpu)
    {
        assertions = 0;
        using var app = new SnowApp(diagnostics: true);
        try
        {
            var overlays = Field<List<SnowOverlay>>(app, "overlays");
            foreach (var overlay in overlays) overlay.Hide();
            var view = Screen.PrimaryScreen!.Bounds;
            var surface = new Surface { Handle = 123, Age = 60 };
            surface.Resize(new Rectangle(view.Left + 100, view.Top + 300, 800, 350));
            var ordered = Field<List<Surface>>(app, "ordered"); ordered.Clear(); ordered.Add(surface);
            var dust = Field<List<SnowDust>>(app, "dust"); dust.Clear();
            Array.Fill(surface.Snow, 4);
            float volume = Volume(surface), ahead = surface.Snow[22];
            Check(app.PushDrift(new Point(surface.Bounds.Left + 240, surface.Bounds.Top - 3), 1, 80) > 0, "cursor must push existing snow");
            Check(Math.Abs(Volume(surface) + dust.Sum(grain => grain.Mass) - volume) < .02f, "sweeping must conserve material in the heap and airborne powder");
            Check(surface.Snow[20] < 4 && surface.Snow[22] > ahead, "sweep must form a heap ahead of the pointer");
            Check(dust.Count > 0 && dust.All(grain => grain.Settles && grain.Mass > 0), "a sweep must also scatter powder that can settle below");
            var relax = typeof(SnowApp).GetMethod("RelaxDrifts", Flags)!.CreateDelegate<Action<float>>(app);
            volume = Volume(surface); for (int i = 0; i < 120; i++) relax(1f / 60);
            Check(Math.Abs(Volume(surface) - volume) < .02f, "slumping must conserve snow volume");
            app.PushDrift(new Point(surface.Bounds.Right - 3, surface.Bounds.Top - 3), 1, 80);
            Check(dust.Count > 0 && dust.All(grain => grain.Settles), "snow pushed past the edge must fall and settle below");
            var melt = typeof(SnowApp).GetMethod("ApplyMelting", Flags)!.CreateDelegate<Action<float>>(app);
            Set(app, "meltSnow", true); volume = Volume(surface); melt(10);
            Check(Volume(surface) < volume * .8f, "enabled melting must lower the accumulated drift");
            Set(app, "meltSnow", false); volume = Volume(surface); melt(10);
            Check(Math.Abs(Volume(surface) - volume) < .001f, "disabled melting must preserve accumulated snow");

            var update = typeof(SnowApp).GetMethod("UpdateSnowfall", Flags)!.CreateDelegate<Action<float>>(app);
            var flakes = Field<List<Flake>>(app, "flakes");
            var balls = Field<List<Ball>>(app, "balls");
            var skippedGpuDisplays = new HashSet<Rectangle>();
            var snowman = new Ball { X = surface.Bounds.Left + 500, Y = surface.Bounds.Top - 20, R = 20, Support = surface.Handle };
            balls.Add(snowman);
            using var input = new InputOverlay(app, SystemInformation.VirtualScreen); _ = input.Handle;
            Field<List<InputOverlay>>(app, "inputOverlays").Add(input);
            foreach (var mode in Enum.GetValues<WeatherMode>())
            {
                app.SetWeatherMode(mode); update(1f / 60);
                if (mode != WeatherMode.Snow)
                    Check(!balls.Contains(snowman), "changing weather must clear previous snowmen and snowballs");
                if (!balls.Contains(snowman)) balls.Add(snowman);
                app.RefreshInputRegion();
                Check(input.ContainsHit(new Point((int)snowman.X, (int)snowman.Y)), "snowballs must remain interactive in every weather mode");
                Check(flakes.Count is > 0 and <= 720, "weather particle count must stay bounded");
                Check(mode == WeatherMode.Snow || flakes.All(f => f.Size >= 6 && f.Speed > 0), "seasonal particles must have their own scale and speed");
                if (mode is WeatherMode.AutumnLeaves or WeatherMode.CherryBlossom)
                    Check(flakes.All(f => mode == WeatherMode.AutumnLeaves ? f.Variant is >= 0 and < 4 : f.Variant is >= 4 and < 8), "weather atlas variants must stay in range");
                if (mode == WeatherMode.Rain)
                {
                    flakes.Clear(); dust.Clear();
                    flakes.Add(new Flake { Region = view, X = surface.Bounds.Left + 250, Y = surface.Bounds.Top - 2,
                        Speed = 800, Size = 14, Fade = 1, Opacity = 1 });
                    update(.016f);
                    Check(dust.Any(grain => Math.Abs(grain.Y - surface.Bounds.Top) < 3 && grain.Vy < 0), "rain must splash on a window edge");
                    Check(surface.Age == 0 && surface.Snow.All(h => h == 0), "rain must never accumulate a snowbank");
                }
                surface.Age = mode == WeatherMode.Rain ? 0 : 75;
                foreach (var overlay in overlays)
                {
                    var bounds = overlay.Bounds;
                    if (!flakes.Any(f => f.Region == bounds))
                        flakes.Add(new Flake { Region = bounds, X = bounds.Left + 40, Y = bounds.Top + 40,
                            Size = mode == WeatherMode.Snow ? 4 : 12, Opacity = 1, Fade = 1,
                            Variant = mode == WeatherMode.CherryBlossom ? 4 : 0 });
                    var gpu = overlay.GpuBackend;
                    if (gpu is null && allowUnavailableGpu)
                    {
                        if (skippedGpuDisplays.Add(bounds))
                            Console.WriteLine($"SKIP: GPU weather rendering and composition opacity at {bounds} ({overlay.GpuFailureReason ?? "GPU disabled"})");
                        continue;
                    }
                    if (gpu is null) throw new InvalidOperationException(overlay.GpuFailureReason);
                    app.SetEffectOpacity(100); gpu.Render(app, bounds, false);
                    using var full = gpu.Capture(); long opaque = AlphaSum(full);
                    Check(opaque > 0, "every weather mode must render visible pixels on each monitor");
                    app.SetEffectOpacity(50); gpu.Render(app, bounds, false);
                    using var half = gpu.Capture(); long faded = AlphaSum(half);
                    Check(faded == opaque && Math.Abs(gpu.CompositionOpacity - .5f) < .001f,
                        "opacity must fade the completed composition while preserving source pixels");
                }
                using var fallback = new Bitmap(view.Width, view.Height, PixelFormat.Format32bppPArgb);
                using (var graphics = Graphics.FromImage(fallback)) app.Paint(graphics, view);
                Check(AlphaSum(fallback) > 0, "every weather mode must retain a GDI fallback");
                using (var preview = Graphics.FromImage(fallback))
                {
                    preview.Clear(Color.FromArgb(24, 31, 42));
                    using var windowFill = new SolidBrush(Color.FromArgb(42, 53, 67));
                    var windowBounds = surface.Bounds; windowBounds.Offset(-view.Left, -view.Top);
                    preview.FillRectangle(windowFill, windowBounds);
                    app.Paint(preview, view);
                }
                fallback.Save(Path.Combine(AppContext.BaseDirectory, $"weather-preview-{mode}.png"));
            }
            CheckSeasonalLanding(app, view, update);
            app.SetEffectOpacity(73); Set(app, "meltSnow", true); Set(app, "powerSaving", false);
            var saved = JsonSerializer.Deserialize<SavedSettings>(JsonSerializer.Serialize(app.CaptureSettings()))!;
            Check(saved.Weather == WeatherMode.CherryBlossom && saved.EffectOpacity == 73 && saved.MeltSnow && !saved.PowerSaving,
                "all new preferences must survive serialization");
            var legacy = JsonSerializer.Deserialize<SavedSettings>("{\"SnowAmount\":185,\"FrameLimit\":60}")!;
            Check(legacy.Weather == WeatherMode.Snow && legacy.EffectOpacity == 100 && !legacy.MeltSnow && legacy.PowerSaving,
                "older settings must retain their appearance and get compatible defaults");
            using var activity = new DesktopActivity(() => { });
            Application.DoEvents(); Set(app, "desktopActivity", activity);
            activity.ProcessNotification(0x02B1, 7, 0);
            Check(activity.Unavailable, "session lock must pause effects");
            activity.ProcessNotification(0x02B1, 1, 0);
            Check(activity.Unavailable, "console connection must not undo a session lock");
            activity.ProcessNotification(0x02B1, 8, 0);
            Check(!activity.Unavailable, "session unlock must permit effects again");
            nint notification = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.StructureToPtr(new Guid("6fe69556-704a-47a0-8f24-c28d936fda47"), notification, false);
                Marshal.WriteInt32(notification, 16, 4); Marshal.WriteInt32(notification, 20, 0);
                activity.ProcessNotification(0x0218, 0x8013, notification);
                Check(activity.Unavailable, "display-off notification must pause effects");
                var notify = typeof(SnowApp).GetMethod("RefreshDesktopActivity", Flags)!.CreateDelegate<Action>(app);
                notify();
                Check(overlays.All(overlay => !overlay.Visible), "inactive desktop must hide every rendered layer");
                double scan = Field<double>(app, "lastScan");
                typeof(SnowApp).GetMethod("Tick", Flags)!.CreateDelegate<Action>(app)();
                Check(Field<double>(app, "lastScan") == scan, "inactive desktop must not enumerate windows");
                Marshal.WriteInt32(notification, 20, 1); activity.ProcessNotification(0x0218, 0x8013, notification);
                Check(!activity.Unavailable && !app.CaptureSettings().Paused, "automatic resume must preserve manual pause preference");
            }
            finally { Marshal.FreeHGlobal(notification); }
            Set(activity, "<OnBatteryOrSaver>k__BackingField", true);
            Set(app, "autoGameMode", false); Set(app, "powerSaving", true); Set(app, "frameLimit", 60);
            Check(app.EffectiveFrameLimit == 30 && app.CaptureSettings().FrameLimit == 60,
                "battery economy must cap actual FPS while preserving the user's selection");
            Set(app, "powerSaving", false);
            Check(app.EffectiveFrameLimit == 60, "disabling battery economy must restore the selected FPS");
            Set(app, "desktopActivity", null!);
            CheckGlassMotion();
            Console.WriteLine($"PASS: {assertions} weather, opacity, pile conservation, melting, settings and desktop activity checks");
            return assertions;
        }
        finally { app.ExitThread(); }
    }

    static void CheckSeasonalLanding(SnowApp app, Rectangle view, Action<float> update)
    {
        var ordered = Field<List<Surface>>(app, "ordered");
        var flakes = Field<List<Flake>>(app, "flakes");
        var workAreas = Field<Rectangle[]>(app, "workAreas");
        Set(app, "workAreas", Field<Rectangle[]>(app, "monitorBounds"));
        Set(app, "meltSnow", true);
        Set(app, "snowbanksOnTop", true);
        Field<List<Ball>>(app, "balls").Clear();
        var coveringWindow = new Surface { Handle = 1234 };
        coveringWindow.Resize(new Rectangle(view.Left + view.Width / 2, view.Top, view.Width / 2, view.Height + 1));
        var window = new Surface { Handle = 2345, SnowScale = .45f };
        window.Resize(new Rectangle(view.Left + 32, view.Top + 180, view.Width / 2 - 42, 160));
        var taskbar = new Surface { Handle = -1, SnowScale = .45f };
        taskbar.Resize(new Rectangle(view.Left, view.Bottom, view.Width, 1));
        int windowX = window.Bounds.Left + 123;
        int taskbarX = view.Right - 117;
        ordered.Clear(); ordered.AddRange([coveringWindow, window, taskbar]);
        foreach (var weather in new[] { WeatherMode.AutumnLeaves, WeatherMode.CherryBlossom })
        {
            app.SetWeatherMode(weather);
            flakes.Add(new Flake { Region = view, X = windowX, Y = window.Bounds.Top - 2, Speed = 60, Fade = 1 });
            flakes.Add(new Flake { Region = view, X = taskbarX, Y = taskbar.Bounds.Top - 2, Speed = 60, Fade = 1 });
            update(.075f);
            flakes.Clear();
            Check(window.Snow[10] > 0, $"{weather} must accumulate from the first landing on a bare window");
            Check(taskbar.Snow[(taskbarX - view.Left) / 12] > 0, $"{weather} must settle at the taskbar when its pile is set to stay on top");

            using var fallback = new Bitmap(view.Width, view.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(fallback)) app.Paint(graphics, view, SnowPaintLayer.Background);
            var gpu = Field<List<SnowOverlay>>(app, "overlays").Single(overlay => overlay.Bounds == view).GpuBackend;
            app.SetEffectOpacity(100);
            CheckLandingPixels(fallback, "GDI");
            if (gpu is not null)
            {
                gpu.Render(app, view, false);
                using var rendered = gpu.Capture();
                CheckLandingPixels(rendered, "GPU");
            }

            void CheckLandingPixels(Bitmap frame, string backend)
            {
                Check(VisibleNear(frame, view, windowX, window.Bounds.Top), $"the first {weather} particle must be visible on the smallest window pile ({backend}, settled height {window.Snow[10]:F2})");
                Check(VisibleNear(frame, view, taskbarX, taskbar.Bounds.Top), $"{weather} must be visible above an auto-hidden taskbar ({backend})");
                Check(!VisibleNear(frame, view, taskbarX, view.Top + 9), $"{weather} must not return to the top edge of a maximized window ({backend})");
            }
        }
        Set(app, "workAreas", workAreas);

        static bool VisibleNear(Bitmap frame, Rectangle view, int screenX, int bottom)
        {
            for (int y = bottom - view.Top - 12; y < bottom - view.Top; y++)
                for (int x = screenX - view.Left - 9; x <= screenX - view.Left + 9; x++)
                    if (y >= 0 && frame.GetPixel(x, y).A > 48) return true;
            return false;
        }
    }

    static void CheckGlassMotion()
    {
        using var menu = new SettingsForm(false, 1, 0, true, true);
        menu.Show(); Application.DoEvents();
        IEnumerable<Control> Children(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => Children(child).Prepend(child));
        var toggle = Children(menu).OfType<SettingsForm.SnowSwitch>().Single(control => control.AccessibleName == "Таяние и увядание");
        Check(toggle.Visible, "the melting switch must be available on the default settings page");
        bool initial = toggle.Checked; toggle.Checked = !initial;
        var motion = Field<GlassMotion>(toggle, "toggleMotion");
        Check(toggle.Checked != initial, "switch state must apply immediately, independently of animation");
        Thread.Sleep(240); Application.DoEvents();
        Check(!motion.Running && Math.Abs(motion.Value - (toggle.Checked ? 1 : 0)) < .001f, "switch animation must finish and stop its timer");
        toggle.Checked = !toggle.Checked; menu.Hide(); Application.DoEvents();
        Check(!motion.Running, "hidden settings must stop interaction animations");
    }

    static unsafe long AlphaSum(Bitmap bitmap)
    {
        var bits = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        long sum = 0;
        try
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                byte* row = (byte*)bits.Scan0 + y * bits.Stride;
                for (int x = 0; x < bitmap.Width; x++) sum += row[x * 4 + 3];
            }
        }
        finally { bitmap.UnlockBits(bits); }
        return sum;
    }
}
