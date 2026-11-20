using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace QuietSnow;

internal static class RegressionChecks
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static int assertions;
    static T Field<T>(object app, string name) => (T)app.GetType().GetField(name, Flags)!.GetValue(app)!;
    static void Set(object app, string name, object value) => app.GetType().GetField(name, Flags)!.SetValue(app, value);
    static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        assertions++;
    }

    [STAThread] static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Contains("--benchmark")) { OptimizationBenchmark.Run(); return 0; }
            if (args.Contains("--memory-soak")) { InputMemoryChecks.Run(); return 0; }
            if (args.Contains("--weather-benchmark")) { WeatherBenchmark.Run(); return 0; }
            bool ci = args.Contains("--ci");
            CheckFingerprints();
            CheckCleanerAllocations();
            CheckConfiguration();
            if (ci && (!Environment.UserInteractive || Screen.PrimaryScreen is null))
            {
                Console.WriteLine("SKIP: desktop integration requires an interactive Windows session with a display.");
            }
            else
            {
                CheckRasterCache(ci);
                CheckPartialPresentation(ci);
                CheckOverlayHandleRecreation();
                CheckNativeInput();
                CheckPauseAndFrameLimits();
                assertions += SeasonalChecks.Run(ci);
                assertions += InteractionFixChecks.Run();
            }
            Console.WriteLine($"PASS: {assertions} assertions");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void CheckConfiguration()
    {
        var legacy = JsonSerializer.Deserialize<SavedSettings>("{\"Intensity\":2}")!;
        Check(legacy.Intensity == 2 && legacy.SnowAmount is null,
            "older files must keep their preset and distinguish a missing density");
        Check(legacy.Weather == WeatherMode.Snow && legacy.EffectOpacity == 100 && legacy.PowerSaving,
            "older files must use compatible weather defaults");
        Check(SnowApp.NormalizeApplication(@"  ""C:\Apps\CHROME.exe""  ") == "chrome",
            "quoted executable paths must match the canonical process name");
        Check(SnowApp.NormalizeApplication(null).Length == 0 && SnowApp.NormalizeApplication("bad?.exe").Length == 0,
            "invalid executable rules must be empty");
        Check(GameActivity.MatchesGame("CS2", null), "known game names must match without probing installation paths");
        Check(GameActivity.MatchesGame("mygame", @"D:\Steam\steamapps\common\Example\MyGame.exe"),
            "unknown games in a Steam library must be detected");
        Check(!GameActivity.MatchesGame("examplelauncher", @"D:\Epic Games\Example\ExampleLauncher.exe"),
            "game launchers must leave effects active");
        Console.WriteLine("PASS: legacy configuration, executable normalization and game detection");
    }

    static unsafe void CheckFingerprints()
    {
        var random = new Random(735);
        for (int width = 1; width <= 64; width++)
        foreach (int height in new[] { 1, 7, 64 })
        {
            int stride = width * 4 + 20;
            var pixels = new byte[stride * height]; random.NextBytes(pixels);
            var packed = new byte[(width + 7) / 8 * 32 * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++) pixels[y * stride + x * 4 + 3] = 0;
                pixels.AsSpan(y * stride, width * 4).CopyTo(packed.AsSpan(y * ((width + 7) / 8 * 32)));
            }
            fixed (byte* data = pixels)
            {
                ulong hash = TileFingerprint.Compute((nint)data, stride, width, height, out bool visible);
                Check(!visible, "RGB bytes must not make a transparent tile visible");
                Check(hash == ReferenceHash(packed, (ulong)(uint)width << 32 | (uint)height), "padded XXH64 differs from reference");
                int lastAlpha = (height - 1) * stride + (width - 1) * 4 + 3;
                pixels[lastAlpha] = 1;
                Check(TileFingerprint.Compute((nint)data, stride, width, height, out visible) != hash && visible,
                    "last pixel/partial row alpha must change the fingerprint and visibility");
                pixels[lastAlpha] = 0;
                pixels[stride - 1] ^= 1; // Padding is outside the tile.
                Check(TileFingerprint.Compute((nint)data, stride, width, height, out _) == hash, "stride padding must be ignored");
            }
        }
        Console.WriteLine("PASS: all tile widths, partial rows, alpha and stride padding");
    }

    // Independent contiguous-input implementation for the padded reference data.
    static ulong ReferenceHash(byte[] bytes, ulong seed)
    {
        unchecked
        {
            const ulong p1 = 11400714785074694791UL, p2 = 14029467366897019727UL;
            const ulong p3 = 1609587929392839161UL, p4 = 9650029242287828579UL;
            ulong[] lanes = [seed + p1 + p2, seed + p2, seed, seed - p1];
            for (int offset = 0; offset < bytes.Length; offset += 32)
                for (int lane = 0; lane < 4; lane++)
                    lanes[lane] = BitOperations.RotateLeft(lanes[lane] + BitConverter.ToUInt64(bytes, offset + lane * 8) * p2, 31) * p1;
            ulong hash = BitOperations.RotateLeft(lanes[0], 1) + BitOperations.RotateLeft(lanes[1], 7) +
                BitOperations.RotateLeft(lanes[2], 12) + BitOperations.RotateLeft(lanes[3], 18);
            foreach (ulong lane in lanes) hash = (hash ^ BitOperations.RotateLeft(lane * p2, 31) * p1) * p1 + p4;
            hash += (ulong)bytes.Length;
            hash ^= hash >> 33; hash *= p2; hash ^= hash >> 29; hash *= p3;
            return hash ^ (hash >> 32);
        }
    }

    static void CheckRasterCache(bool allowUnavailableGpu)
    {
        using var app = new SnowApp(diagnostics: true);
        try
        {
            foreach (var existing in Field<List<SnowOverlay>>(app, "overlays")) existing.Hide();
            Field<Stopwatch>(app, "clock").Reset();
            Field<List<Flake>>(app, "flakes").Clear();
            var surfaces = Field<List<Surface>>(app, "ordered"); surfaces.Clear();
            var balls = Field<List<Ball>>(app, "balls"); balls.Clear();
            Set(app, "garlandMode", 0);
            var viewport = new Rectangle(-1280, -30, 1280, 720);
            for (int i = 0; i < 4; i++)
            {
                var surface = new Surface { Handle = 101 + i, Age = 75, IsExplorer = true };
                surface.Resize(new Rectangle(viewport.Left + 50 + i * 17, viewport.Top + 110 + i * 160, 960 - i * 60, 130));
                surfaces.Add(surface);
            }
            using var overlay = new SnowOverlay(app, viewport); _ = overlay.Handle;
            var gpu = overlay.GpuBackend;
            if (gpu is null && allowUnavailableGpu)
            {
                Console.WriteLine($"SKIP: GPU raster cache ({overlay.GpuFailureReason ?? "GPU disabled"})");
                return;
            }
            if (gpu is null) throw new InvalidOperationException(overlay.GpuFailureReason);
            var background = Field<GpuRasterLayer>(gpu, "background");
            void Render() => gpu.Render(app, viewport, false);
            Render(); int count = background.BandPaintCount;
            for (int i = 0; i < 10; i++) Render();
            Check(background.BandPaintCount == count && gpu.RasterUploadBytes == 0, "stable scene must reuse all bands");
            surfaces[1].Snow[12] = 8; Render();
            Check(background.BandPaintCount - count == 1, "one changed cap must repaint just its band");
            CompareWithGdi(app, viewport, gpu);
            count = background.BandPaintCount;
            surfaces[1].Bounds.Offset(2, 0); Render();
            Check(background.BandPaintCount - count is > 0 and <= 2, "local window motion must reuse bands outside the window and its occlusion");
            CompareWithGdi(app, viewport, gpu);

            var random = new Random(724);
            for (int frame = 0; frame < 50; frame++)
            {
                var surface = surfaces[frame % surfaces.Count];
                surface.Resize(new Rectangle(viewport.Left + random.Next(-50, 400), viewport.Top + random.Next(15, 700),
                    random.Next(160, 1400), random.Next(100, 500)));
                surface.Age = frame % 4 == 0 ? 0 : 75;
                for (int i = 0; i < surface.Snow.Length; i++) surface.Snow[i] = random.Next(-20, 19);
                surface.GarlandX = random.Next(-9, 10); surface.GarlandY = random.Next(-8, 10);
                Set(app, "garlandMode", frame % 3); Set(app, "garlandPalette", frame % 3);
                Set(app, "snowbanksOnTop", frame % 2 == 0);
                surface.Handle = frame % 5 == 0 ? -frame - 1 : frame + 201;
                if (frame % 7 == 0) surfaces.Reverse();
                Render(); CompareWithGdi(app, viewport, gpu);
                int paints = background.BandPaintCount;
                Render();
                Check(background.BandPaintCount == paints && gpu.RasterUploadBytes == 0,
                    "identical phase and geometry must not cause a second rasterization");
            }
            surfaces.Clear(); Render(); count = background.BandPaintCount;
            for (int i = 0; i < 10; i++) Render();
            Check(background.BandPaintCount == count, "empty layers must remain cached");
            Console.WriteLine("PASS: selective cache, movement, resizing, occlusion, negative coordinates and 52 GDI comparisons");

            // Two neighboring 64px rows need different widths when the distant
            // ball starts below the head. The cut runs exactly through its face.
            var torso = new Ball { X = viewport.Left + 256, Y = viewport.Top + 288, R = 18 };
            var head = new Ball { X = torso.X + .4f, Y = viewport.Top + 259.4f, R = 12, BaseBall = torso };
            var distant = new Ball { X = viewport.Left + 500, Y = viewport.Top + 285, R = 20 };
            balls.AddRange([torso, head, distant]);
            Render();
            using var face = gpu.Capture();
            Check(Enumerable.Range(248, 16).All(x => face.GetPixel(x, 256).A >= 250),
                "a renderer band boundary must not cut a translucent line through the snowman's face");
            Console.WriteLine("PASS: snowman face stays opaque across the packed raster boundary");

            int buttonX = (int)torso.X - viewport.Left, buttonY = (int)(torso.Y + torso.R * .43f) - viewport.Top;
            int dressed = face.GetPixel(buttonX, buttonY).R;
            head.SnowmanAppearance = 0;
            Render();
            using var plain = gpu.Capture();
            head.SnowmanAppearance = .65f;
            Render();
            using var halfway = gpu.Capture();
            int partial = halfway.GetPixel(buttonX, buttonY).R;
            Check(plain.GetPixel(buttonX, buttonY).R > partial && partial > dressed,
                "the cached GPU layer must render a button progressively along the body's vertical center");
            head.SnowmanAppearance = 1;
            Render();
            Render();
            Check(gpu.RasterUploadBytes == 0, "a finished snowman should not keep changing cached pixels");
        }
        finally { app.ExitThread(); }
    }

    static unsafe void CompareWithGdi(SnowApp app, Rectangle viewport, GpuSnowRenderer gpu)
    {
        using var actual = gpu.Capture();
        using var expected = new Bitmap(viewport.Width, viewport.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(expected))
        {
            graphics.Clear(Color.Transparent); graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            app.Paint(graphics, viewport);
        }
        var rect = new Rectangle(Point.Empty, expected.Size);
        var a = actual.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var b = expected.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        int different = 0;
        try
        {
            for (int y = 0; y < rect.Height; y++)
            {
                var aa = new ReadOnlySpan<uint>((void*)(a.Scan0 + y * a.Stride), rect.Width);
                var bb = new ReadOnlySpan<uint>((void*)(b.Scan0 + y * b.Stride), rect.Width);
                for (int x = 0; x < rect.Width; x++) if (aa[x] != bb[x]) different++;
            }
        }
        finally { actual.UnlockBits(a); expected.UnlockBits(b); }
        Check(different == 0, $"GPU decorations differ from GDI at {different} pixels");
    }

    static void CheckCleanerAllocations()
    {
        var cleaner = new SnowCleaner { SelectedKind = 1 };
        Rectangle[] areas = [new(0, 0, 1920, 1032), new(-1280, 0, 1280, 984), new(0, 0, 20, 20)];
        Func<RectangleF, float> scrape = _ => 0;
        for (int i = 0; i < 100; i++) cleaner.Update(.016f, areas, scrape);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) cleaner.Update(.016f, areas, scrape);
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "steady cleaner updates must not allocate managed memory");
        Console.WriteLine("PASS: cleaner has no managed allocations during steady updates");
    }

    static unsafe void CheckPartialPresentation(bool allowUnavailableGpu)
    {
        using var app = new SnowApp(diagnostics: true);
        string? previousMode = Environment.GetEnvironmentVariable("QUIETSNOW_FULL_PRESENT");
        try
        {
            foreach (var existing in Field<List<SnowOverlay>>(app, "overlays")) existing.Hide();
            Field<Stopwatch>(app, "clock").Reset();
            var surfaces = Field<List<Surface>>(app, "ordered"); surfaces.Clear();
            var flakes = Field<List<Flake>>(app, "flakes"); flakes.Clear();
            var dust = Field<List<SnowDust>>(app, "dust"); dust.Clear();
            var balls = Field<List<Ball>>(app, "balls"); balls.Clear();
            int verified = 0;
            foreach (var screen in Screen.AllScreens)
            foreach (var mode in Enum.GetValues<WeatherMode>())
            {
                app.SetWeatherMode(mode);
                Set(app, "snowbanksEnabled", true);
                var view = new Rectangle(screen.Bounds.Left + 80, screen.Bounds.Top + 80, 641, 479);
                var surface = new Surface { Handle = 123, Age = 75, IsExplorer = true };
                surface.Resize(new Rectangle(view.Left + 60, view.Top + 330, 500, 140)); surfaces.Add(surface);
                var flake = new Flake { Region = view, X = view.Left + 30, Y = view.Top + 25, Size = mode == WeatherMode.Snow ? 4.2f : 12, Fade = 1, Opacity = .8f,
                    Variant = mode == WeatherMode.CherryBlossom ? 4 : 0 };
                flakes.Add(flake);
                var ball = new Ball { X = view.Left + 400, Y = view.Top + 150, R = 19, Held = true }; balls.Add(ball);
                Environment.SetEnvironmentVariable("QUIETSNOW_FULL_PRESENT", "1");
                using var reference = new SnowOverlay(app, view); _ = reference.Handle;
                Environment.SetEnvironmentVariable("QUIETSNOW_FULL_PRESENT", null);
                using var partial = new SnowOverlay(app, view); _ = partial.Handle; partial.Show();
                var gpu = partial.GpuBackend;
                var full = reference.GpuBackend;
                if (allowUnavailableGpu && (gpu?.UsesPartialPresentation != true || full is null))
                {
                    string reason = partial.GpuFailureReason ?? reference.GpuFailureReason ?? "partial GPU presentation unavailable";
                    Console.WriteLine($"SKIP: {mode} partial presentation on {screen.DeviceName} ({reason})");
                    surfaces.Clear(); flakes.Clear(); balls.Clear();
                    continue;
                }
                if (gpu is null || full is null)
                    throw new InvalidOperationException(partial.GpuFailureReason ?? reference.GpuFailureReason);
                Check(gpu.UsesPartialPresentation, "partial presentation must be available on the monitor adapter");
                for (int frame = 0; frame < 24; frame++)
                {
                    var menu = Field<SettingsForm>(app, "settings");
                    if (frame == 8) { menu.StartPosition = FormStartPosition.Manual; menu.Location = new Point(view.Left + 100, view.Top + 20); menu.Show(); Application.DoEvents(); }
                    if (frame == 10) menu.Left += 30;
                    if (frame == 12) menu.Hide();
                    if (frame == 14) flakes.Clear();
                    if (frame == 15) { flakes.Add(flake); flake.X = view.Right + 3; }
                    if (frame == 16) flake.X = view.Left + 35;
                    if (frame == 18) { ball.Held = false; ball.R = 26; }
                    if (frame == 20) { var cover = new Surface { Handle = 456 }; cover.Resize(new Rectangle(view.Left + 150, view.Top + 230, 180, 200)); surfaces.Insert(0, cover); }
                    if (frame == 22) surfaces.RemoveAll(s => s.Handle == 456);
                    if (frame == 23) Set(app, "snowbanksEnabled", false);
                    flake.X += 17.3f; flake.Y += 8.1f; flake.Fade = .4f + frame % 4 * .15f;
                    flake.Angle += .37f;
                    ball.X -= 3.7f; ball.Y += 1.1f; ball.Rotation += .15f;
                    if (frame % 4 == 0) surface.Snow[12] = frame % 3 * 4;
                    if (frame % 7 == 0) surface.Bounds.Offset(3, -1);
                    Check(gpu.Render(app, view) >= 0, "Present1 must succeed");
                    using var actual = gpu.CapturePresented();
                    full.Render(app, view, false);
                    using var expected = full.Capture();
                    var rect = new Rectangle(Point.Empty, view.Size);
                    var a = actual.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                    var b = expected.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                    int differences = 0;
                    try
                    {
                        for (int y = 0; y < view.Height; y++)
                        {
                            var aa = new ReadOnlySpan<uint>((void*)(a.Scan0 + y * a.Stride), view.Width);
                            var bb = new ReadOnlySpan<uint>((void*)(b.Scan0 + y * b.Stride), view.Width);
                            for (int x = 0; x < view.Width; x++) if (aa[x] != bb[x]) differences++;
                        }
                    }
                    finally { actual.UnlockBits(a); expected.UnlockBits(b); }
                    if (differences != 0) { actual.Save(Path.Combine(AppContext.BaseDirectory,"partial-actual.png")); expected.Save(Path.Combine(AppContext.BaseDirectory,"partial-expected.png")); }
                    Check(differences == 0, $"presented partial frame {frame} differs from full rendering in {differences} pixels on {screen.DeviceName}");
                    if (frame > 0 && frame is not (8 or 10 or 12)) Check(gpu.PresentedPixels > 0 && gpu.PresentedPixels < (long)view.Width * view.Height,
                        "moving objects should update a subset of the monitor");
                }
                surfaces.Clear(); flakes.Clear(); balls.Clear();
                gpu.Render(app, view); gpu.Render(app, view);
                Check(gpu.PresentedPixels == 0, "unchanged empty frames must not be presented again");
                flakes.Add(new Flake { Region = view, X = view.Right + 3, Y = view.Top + 40, Size = 1.25f, Fade = 1, Opacity = 1 });
                gpu.Render(app, view);
                Check(gpu.PresentedPixels == 0, "fully clipped particles must not submit an empty dirty list as a full frame");
                flakes[0].X = view.Right - 10; gpu.Render(app, view);
                Check(gpu.PresentedPixels > 0, "a previously clipped particle must appear when it enters the viewport");
                flakes.Clear();
                verified++;
            }
            if (verified > 0) Console.WriteLine($"PASS: partial presented buffers match full GPU frames in {verified} weather and monitor combinations");
        }
        finally { Environment.SetEnvironmentVariable("QUIETSNOW_FULL_PRESENT", previousMode); app.ExitThread(); }
    }

    static void CheckOverlayHandleRecreation()
    {
        using var app = new SnowApp(diagnostics: true);
        string? previousMode = Environment.GetEnvironmentVariable("QUIETSNOW_FORCE_GDI");
        try
        {
            foreach (var existing in Field<List<SnowOverlay>>(app, "overlays")) existing.Hide();
            var bounds = new Rectangle(Screen.PrimaryScreen!.Bounds.Location, new Size(320, 240));
            var recreateHandle = typeof(Control).GetMethod("RecreateHandle", Flags)!;
            foreach (var mode in new[] { previousMode, "1" }.Distinct())
            {
                Environment.SetEnvironmentVariable("QUIETSNOW_FORCE_GDI", mode);
                using var overlay = new SnowOverlay(app, bounds);
                int destroyedHandles = 0;
                GpuSnowRenderer? oldBackend = null;
                overlay.HandleDestroyed += (_, _) =>
                {
                    destroyedHandles++;
                    Check(overlay.GpuBackend is null && overlay.RenderSize.IsEmpty,
                        "destroying a rendering handle must release its GPU and GDI drawing surfaces");
                    if (oldBackend is not null)
                        Check(Field<nint>(oldBackend, "device") == 0, "the renderer for the previous window must release its Direct3D device");
                };
                overlay.Show();
                oldBackend = overlay.GpuBackend;
                recreateHandle.Invoke(overlay, null);
                Check(destroyedHandles == 1 && overlay.RenderSize == bounds.Size && overlay.LastPresentationError == 0,
                    "recreating the overlay handle must create a functioning surface at the original size");
                Check(oldBackend is null || (overlay.GpuBackend is not null && !ReferenceEquals(oldBackend, overlay.GpuBackend)),
                    "the new window must have its own GPU renderer");
                oldBackend = overlay.GpuBackend;
                overlay.Close();
                Check(destroyedHandles == 2, "closing the overlay must release the replacement surface as well");
            }
            Console.WriteLine("PASS: overlay handles release and recreate graphics resources");
        }
        finally { Environment.SetEnvironmentVariable("QUIETSNOW_FORCE_GDI", previousMode); app.ExitThread(); }
    }

    [DllImport("user32.dll")] static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    static nint MouseCoordinates(Point point) => (nint)((ushort)point.X | ((uint)(ushort)point.Y << 16));

    static void CheckNativeInput()
    {
        using var app = new SnowApp(diagnostics: true);
        try
        {
            foreach (var overlay in Field<List<SnowOverlay>>(app, "overlays")) overlay.Hide();
            var area = Screen.PrimaryScreen!.Bounds;
            var surface = new Surface { Handle = 123, Age = 75 };
            surface.Resize(new Rectangle(area.Left + 100, area.Top + 250, 600, 300));
            var surfaces = Field<List<Surface>>(app, "ordered"); surfaces.Clear(); surfaces.Add(surface);
            using var input = new InputOverlay(app, SystemInformation.VirtualScreen);
            Field<List<InputOverlay>>(app, "inputOverlays").Add(input);
            input.Show(); app.RefreshInputRegion(); input.RaiseForInteraction(); Application.DoEvents();
            var retainedRegion = input.HitRegion;
            input.SetHitRegion(retainedRegion!.Clone());
            Check(ReferenceEquals(retainedRegion, input.HitRegion), "equal native regions must retain the existing hit shape");
            typeof(Control).GetMethod("RecreateHandle", Flags)!.Invoke(input, null);
            input.RaiseForInteraction(); Application.DoEvents();
            Check(ReferenceEquals(retainedRegion, input.HitRegion), "handle recreation must preserve the managed hit shape");
            Thread.Sleep(150); Application.DoEvents();
            var contact = new Point(surface.Bounds.Left + 200, surface.Bounds.Top - 4);
            Check(WindowFromPoint(contact) == input.Handle, "native hit testing must still deliver clicks on snow");
            Check(WindowFromPoint(new Point(contact.X, surface.Bounds.Top + 5)) != input.Handle,
                "native hit testing must pass title-bar clicks through the input overlay");
            var originalBounds = input.Bounds;
            SendMessage(input.Handle, 0x0201, 1, MouseCoordinates(input.PointToClient(contact)));
            Check(input.Dragging && input.Bounds == SystemInformation.VirtualScreen,
                "captured dragging must cover the virtual desktop");
            var destinationArea = Screen.AllScreens.FirstOrDefault(screen => !screen.Primary)?.WorkingArea ?? area;
            var destination = new Point(destinationArea.Left + destinationArea.Width / 2, destinationArea.Top + 180);
            SendMessage(input.Handle, 0x0200, 1, MouseCoordinates(input.PointToClient(destination)));
            var held = Field<List<Ball>>(app, "balls").Single(ball => ball.Held);
            Check(Math.Abs(held.X - destination.X) < .01f, "captured drag must cross monitor coordinates correctly");
            SendMessage(input.Handle, 0x0202, 0, MouseCoordinates(input.PointToClient(destination)));
            Check(!input.Dragging && input.Bounds == originalBounds && !held.Held,
                "release must preserve the input window bounds and release the ball");
            surfaces.Clear(); app.RefreshInputRegion(); Application.DoEvents();
            Check(WindowFromPoint(contact) != input.Handle, "empty input regions must pass clicks through");
            Console.WriteLine("PASS: native mouse targets and click-through behavior");
        }
        finally { app.ExitThread(); }
    }

    static void CheckPauseAndFrameLimits()
    {
        using var app = new SnowApp(diagnostics: true);
        try
        {
            var tick = typeof(SnowApp).GetMethod("Tick", Flags)!.CreateDelegate<Action>(app);
            Set(app, "paused", true); Set(app, "lastScan", -10d);
            tick();
            Check(Field<double>(app, "lastScan") == -10, "paused application must not enumerate windows");
            Check(Field<List<SnowOverlay>>(app, "overlays").All(overlay => !overlay.Visible), "pause must hide all overlays");
            Set(app, "paused", false); Set(app, "autoGameMode", false); Set(app, "frameLimit", 30);
            tick();
            Check(Field<double>(app, "lastScan") >= 0, "resume must immediately refresh stale window geometry");
            Check(Math.Abs(Field<FrameClock>(app, "timer").Interval - 1000d / 30) < .001, "30 FPS selection must be preserved");
            Set(app, "frameLimit", 60); tick();
            Check(Math.Abs(Field<FrameClock>(app, "timer").Interval - 1000d / 60) < .001, "60 FPS selection must be preserved");
            Check(Field<List<SnowOverlay>>(app, "overlays").All(overlay => overlay.Visible), "resume must show all overlays");
            Console.WriteLine("PASS: pause, immediate resume and both frame limits");
        }
        finally { app.ExitThread(); }
    }
}
