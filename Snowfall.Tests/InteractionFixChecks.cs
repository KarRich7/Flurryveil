using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace QuietSnow;

internal static class InteractionFixChecks
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static int checks;
    static T Get<T>(object obj, string name) => (T)obj.GetType().GetField(name, Flags)!.GetValue(obj)!;
    static void Set(object obj, string name, object? value) => obj.GetType().GetField(name, Flags)!.SetValue(obj, value);
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    [DllImport("user32.dll")] static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern nint GetCapture();
    static nint Pack(Point point) => (nint)((ushort)point.X | (uint)(ushort)point.Y << 16);
    static bool Hidden(SnowApp app) => (bool)typeof(SnowApp).GetProperty("EffectsHidden", Flags)!.GetValue(app)!;

    public static int Run()
    {
        checks = 0;
        using var app = new SnowApp(diagnostics: true);
        try
        {
            foreach (var overlay in Get<List<SnowOverlay>>(app, "overlays")) overlay.Hide();
            Set(app, "autoGameMode", false); Set(app, "hideFullscreen", false); Set(app, "snowmenOnTop", true);
            var view = Screen.PrimaryScreen!.Bounds;
            var surfaces = Get<List<Surface>>(app, "ordered"); surfaces.Clear();
            var balls = Get<List<Ball>>(app, "balls"); balls.Clear();
            var dust = Get<List<SnowDust>>(app, "dust"); dust.Clear();
            var ledge = new Surface { Handle = 123, Age = 75 };
            ledge.Resize(new Rectangle(view.Left + 120, view.Top + 420, 1200, 400)); surfaces.Add(ledge);
            Array.Fill(ledge.Snow, 14);
            var ball = new Ball { X = ledge.Bounds.Left + 160, Y = ledge.Bounds.Top - 35 - 28, R = 28, Support = ledge.Handle };
            balls.Add(ball);
            var start = new Point((int)ball.X, (int)ball.Y);
            Check(app.MouseDown(start), "existing ball must be pickable again");
            app.MouseMove(new Point(start.X + 180, start.Y), true);
            Check(ball.R > 28, "regrabbing an existing ball must permit further growth");
            app.MouseMove(new Point(start.X + 520, start.Y), true);
            Check(ball.R > 42 && ball.R <= SnowApp.MaximumBallRadius, "rolling on fresh snow must grow beyond the old 42px limit");
            app.MouseMove(new Point(start.X + 520, start.Y - 250), true);
            float radius = ball.R;
            app.MouseMove(new Point(start.X + 620, start.Y - 250), true);
            Check(ball.R == radius, "a ball must not grow while being carried in the air");
            app.MouseUp();

            var front = new Surface { Handle = 456 }; front.Resize(new Rectangle((int)ball.X - 100, (int)ball.Y - 100, 200, 250));
            surfaces.Insert(0, front); ball.Support = ledge.Handle;
            var clip = typeof(SnowApp).GetMethod("ClipBall", Flags)!;
            using (var region = new Region(new RectangleF(ball.X - ball.R, ball.Y - ball.R, ball.R * 2, ball.R * 2)))
            {
                clip.Invoke(app, [region, ball]); Check(region.IsVisible(ball.X, ball.Y), "on-top balls on window ledges must remain visible above covering windows");
            }
            Set(app, "snowmenOnTop", false);
            using (var region = new Region(new RectangleF(ball.X - ball.R, ball.Y - ball.R, ball.R * 2, ball.R * 2)))
            {
                clip.Invoke(app, [region, ball]); Check(!region.IsVisible(ball.X, ball.Y), "turning off on-top must restore normal window occlusion");
            }
            surfaces.Remove(front); Set(app, "snowmenOnTop", true);

            using var input = new InputOverlay(app, SystemInformation.VirtualScreen);
            Get<List<InputOverlay>>(app, "inputOverlays").Add(input); input.Show(); app.RefreshInputRegion(); input.RaiseForInteraction(); Application.DoEvents();
            start = new Point((int)ball.X, (int)ball.Y);
            SendMessage(input.Handle, 0x0201, 1, Pack(input.PointToClient(start)));
            Check(input.Dragging && ball.Held, "mouse capture must start only after a ball was actually picked up");
            // Move back toward the display's center, including on small CI desktops.
            float beforeDragX = ball.X;
            var destination = new Point(start.X - 90, start.Y - 30);
            input.PollDrag(destination, true, false);
            Check(Math.Abs(ball.X - beforeDragX + 90) < .01f, "cursor polling must recover a missed mouse-move message");
            input.PollDrag(destination, false, false);
            Check(input.Dragging, "one transient key-state miss must not cancel a drag");
            input.PollDrag(destination, true, false);
            if (Screen.AllScreens.Length > 1)
            {
                var left = Screen.AllScreens.FirstOrDefault(screen => screen.Bounds.Right == view.Left);
                if (left is not null)
                {
                    int y = Math.Max(left.WorkingArea.Top, view.Top) + 180;
                    input.PollDrag(new Point(view.Left - 1, y), true, false); float x = ball.X;
                    input.PollDrag(new Point(view.Left + 1, y), true, false);
                    Check(Math.Abs(ball.X - x - 2) < .01f, "dragging across a monitor seam must not jump by the ball diameter");
                }
            }
            input.PollDrag(destination, true, true);
            Check(!input.Dragging && !ball.Held, "Escape must reliably release capture and the ball");
            balls.Clear(); ledge.Age = 0; Array.Clear(ledge.Snow); app.InvalidateInputRegion(); app.RefreshInputRegion();
            SendMessage(input.Handle, 0x0201, 1, Pack(input.PointToClient(new Point(ledge.Bounds.Left + 60, ledge.Bounds.Top - 2))));
            Check(!input.Dragging && balls.Count == 0, "clicking an empty ledge must not capture the whole desktop");

            CheckDragAfterRemoval(app, input, ledge, balls);

            var scrape = typeof(SnowApp).GetMethod("ClearSnowStrip", Flags)!.CreateDelegate<Func<RectangleF, float>>(app);
            var ground = new Surface { Handle = -1, Age = 60 }; ground.Resize(new Rectangle(view.Left, view.Top + 600, 800, 30));
            var work = new Rectangle(view.Left, view.Top, 800, 600);
            var cleaner = Get<SnowCleaner>(app, "cleaner");
            for (int kind = 1; kind <= 3; kind++)
            {
                surfaces.Clear(); surfaces.Add(ground); balls.Clear();
                var root = new Ball { X = work.Left + 270, Y = ground.Bounds.Top - 28, R = 28, Support = -1 };
                var body = new Ball { X = root.X, Y = root.Y - 45, R = 20, Support = -1, BaseBall = root };
                var head = new Ball { X = root.X, Y = body.Y - 31, R = 13, Support = -1, BaseBall = body };
                var untouched = new Ball { X = root.X, Y = ground.Bounds.Top - 220, R = 16, Support = 999 };
                var held = new Ball { X = root.X, Y = ground.Bounds.Top - 20, R = 20, Support = -1, Held = true };
                balls.AddRange([root, body, head, untouched, held]); cleaner.SelectedKind = kind;
                for (int frame = 0; frame < 500 && balls.Contains(root); frame++) cleaner.Update(.016f, [work], scrape);
                Check(!balls.Contains(root) && !balls.Contains(body) && !balls.Contains(head), "every cleaner must remove the entire connected ground snowman");
                Check(balls.Contains(untouched) && balls.Contains(held), "cleaning must preserve window-supported and currently held balls");
            }
            foreach (var mode in new[] { WeatherMode.Rain, WeatherMode.AutumnLeaves, WeatherMode.CherryBlossom })
            {
                app.SetWeatherMode(WeatherMode.Snow);
                balls.Clear();
                surfaces.Clear(); surfaces.Add(ledge);
                ledge.Age = 75; Array.Fill(ledge.Snow, 12);
                var edge = new Point(ledge.Bounds.Left + 120, ledge.Bounds.Top - 4);
                app.RefreshInputRegion();
                SendMessage(input.Handle, 0x0201, 1, Pack(input.PointToClient(edge)));
                Check(input.Dragging && balls.Single().Held, $"snow must create a draggable ball before switching to {mode}");

                app.SetWeatherMode(mode);
                Check(balls.Count == 0 && !input.Dragging && GetCapture() != input.Handle,
                    "switching weather must remove balls and release native dragging");
                Array.Fill(ledge.Snow, mode == WeatherMode.Rain ? 0 : 12);
                ledge.HasSeasonalDeposit = mode != WeatherMode.Rain;
                var beforeClick = ledge.Snow.ToArray();
                app.RefreshInputRegion();
                Check(!input.ContainsHit(edge) && SendMessage(input.Handle, 0x0084, 0, Pack(edge)) == -1,
                    $"{mode} must pass mouse input through to the desktop");
                SendMessage(input.Handle, 0x0201, 1, Pack(input.PointToClient(edge)));
                SendMessage(input.Handle, 0x0200, 1, Pack(input.PointToClient(new Point(edge.X + 60, edge.Y))));
                SendMessage(input.Handle, 0x0202, 0, Pack(input.PointToClient(edge)));
                Check(!input.Dragging && balls.Count == 0 && ledge.Snow.SequenceEqual(beforeClick),
                    $"clicking and dragging in {mode} must leave the pile alone");
                if (mode is WeatherMode.AutumnLeaves or WeatherMode.CherryBlossom)
                {
                    dust.Clear();
                    int firstSprite = mode == WeatherMode.CherryBlossom ? 4 : 0;
                    Check(app.PushDrift(edge, 1, 130) > 0 && dust.Count > 0 &&
                        dust.All(grain => grain.Variant >= firstSprite && grain.Variant < firstSprite + 4),
                        $"sweeping must scatter the matching particles in {mode}");
                }
                Check(!app.CleanerActive, "cleaners must be inactive outside the Snow mode");
                var bounds = new List<RectangleF>(); app.GpuLayerBounds(bounds, SnowPaintLayer.Foreground);
                Check(bounds.Count == 0, "disabled cleaners must not retain foreground render bounds");
            }

            surfaces.Clear(); surfaces.Add(ledge); app.SetWeatherMode(WeatherMode.AutumnLeaves);
            ledge.HasSeasonalDeposit = true; Array.Fill(ledge.Snow, 10); dust.Clear();
            for (int i = 0; i < 12; i++) app.PushDrift(new Point(ledge.Bounds.Left + 70 + i * 50, ledge.Bounds.Top - 3), 1, 130);
            Check(dust.Count > 12 && dust.Select(grain => grain.Variant).Distinct().Count() > 1, "scattered leaves must use varied individual leaf sprites");
            Check(dust.All(grain => grain.Variant is >= 0 and < 4 && grain.Size is >= 10 and <= 17), "scattered leaves must use full leaf sizes and varied colors");
            var updateDust = typeof(SnowApp).GetMethod("UpdateDust", Flags)!.CreateDelegate<Action<float>>(app);
            surfaces.Clear();
            float angle = dust[0].Angle;
            for (int i = 0; i < 60; i++) updateDust(1f / 60);
            Check(dust.Count > 0 && dust.All(grain => grain.Vy <= 72.01f), "scattered leaves must flutter down slowly rather than fall as snow clumps");
            Check(dust[0].Angle != angle, "scattered leaves must rotate independently");

            surfaces.Add(ledge);
            using (var preview = new Bitmap(900, 600, PixelFormat.Format32bppPArgb))
            {
                using var graphics = Graphics.FromImage(preview); graphics.Clear(Color.FromArgb(24, 31, 42));
                using var window = new SolidBrush(Color.FromArgb(43, 55, 71));
                graphics.FillRectangle(window, ledge.Bounds.Left - view.Left, ledge.Bounds.Top - view.Top, 1200, 400);
                app.Paint(graphics, new Rectangle(view.Left, view.Top, 900, 600));
                preview.Save(Path.Combine(AppContext.BaseDirectory, "scattered-leaves.png"));
            }
            surfaces.Clear();

            surfaces.Add(ledge); ledge.Resize(new Rectangle(view.Left, view.Top, view.Width, view.Height));
            Get<List<Flake>>(app, "flakes").Clear(); dust.Clear(); balls.Clear();
            using (var image = new Bitmap(view.Width, 80, PixelFormat.Format32bppPArgb))
            {
                using var graphics = Graphics.FromImage(image); app.Paint(graphics, view, SnowPaintLayer.Background);
                Check(!Enumerable.Range(0, image.Width).Any(x => image.GetPixel(x, 0).A != 0), "a maximized window must not produce a strip of leaves at the top edge");
            }

            var invalidRules = JsonSerializer.Deserialize<string[]>("[null,\"\",\".\",\"..\",\"bad?.exe\"]")!;
            app.SetHiddenApplications([@"C:\Apps\CHROME.exe", "chrome", "Telegram.EXE", ..invalidRules]);
            Check(app.CaptureSettings().HiddenApplications.SequenceEqual(new[] { "chrome", "telegram" }), "application rules must normalize paths, extensions and duplicate names");
            var games = Get<GameActivity>(app, "games");
            Set(games, "<ForegroundName>k__BackingField", "CHROME"); app.SetHiddenApplications(["chrome"]);
            Check(Hidden(app), "a selected active application must hide effects even when game detection is off");
            app.SetHiddenApplications([]); Check(!Hidden(app), "removing an application rule must restore effects");
            Set(app, "fullscreen", true); Set(app, "hideFullscreen", true); Check(Hidden(app), "full-screen hiding must work independently of game detection");
            Set(app, "hideFullscreen", false); Check(!Hidden(app), "full-screen effects must remain optional");
            var settings = JsonSerializer.Deserialize<SavedSettings>(JsonSerializer.Serialize(app.CaptureSettings()))!;
            Check(!settings.HideFullscreen && settings.HiddenApplications.Length == 0, "automatic-pause preferences must round-trip");
            CheckApplicationMenu();
            Console.WriteLine($"PASS: {checks} ball growth, native dragging, cleaner, weather reset, leaf physics, top edge and app-rule checks");
            return checks;
        }
        finally { app.ExitThread(); }
    }

    static void CheckDragAfterRemoval(SnowApp app, InputOverlay input, Surface ledge, List<Ball> balls)
    {
        ledge.Age = 75;
        Array.Fill(ledge.Snow, 12);
        var body = new Ball { X = ledge.Bounds.Left + 170, Y = ledge.Bounds.Top - 105, R = 24 };
        var head = new Ball { X = body.X, Y = body.Y - 37, R = 14, BaseBall = body };
        var spare = new Ball { X = body.X + 100, Y = body.Y - 20, R = 18 };
        balls.AddRange([body, head, spare]);
        app.RefreshInputRegion();
        void Send(uint message, Point point, int flags = 0) => SendMessage(input.Handle, message, flags, Pack(input.PointToClient(point)));
        static Point At(Ball ball) => new((int)ball.X, (int)ball.Y);

        Send(0x0204, At(head), 2);
        Send(0x0205, At(head));
        Check(GetCapture() != input.Handle, "right-button removal must not leave the input overlay with native capture");
        Check(!balls.Contains(head) && input.ContainsHit(At(body)), "deletion must immediately restore the remaining ball's hit region");
        Send(0x0201, At(body), 1);
        Check(input.Dragging && body.Held, "the remaining bottom ball must be draggable immediately after deletion");

        SendMessage(input.Handle, 0x0215, 0, 0); // WM_CAPTURECHANGED occurs for nonactivating windows.
        Send(0x0200, At(body), 0); // A single move without a button flag may arrive even though the gesture is active.
        var moved = new Point((int)body.X + 20, (int)body.Y - 45);
        input.PollDrag(moved, true, false);
        Check(input.Dragging && At(body) == moved,
            $"polling must move the ball after native capture is lost (dragging {input.Dragging}, {At(body)} versus {moved})");

        Send(0x0201, At(spare), 1); // A fresh click can arrive before polling observes the previous button-up.
        Check(input.Dragging && spare.Held && !body.Held, "a fresh click must replace a drag whose release was missed");
        Send(0x0205, At(spare));
        Check(!input.Dragging && !balls.Contains(spare) && input.ContainsHit(At(body)), "right-click after a missed release must delete cleanly");

        Send(0x0201, At(body), 1);
        typeof(SnowApp).GetMethod("StartClear", Flags)!.Invoke(app, null);
        typeof(SnowApp).GetMethod("AdvanceClear", Flags)!.Invoke(app, [.8f]);
        app.RefreshInputRegion();
        Check(!input.Dragging && balls.Count == 0 && !input.ContainsHit(At(body)), "clearing the screen must release both the ball and native hit region");
        ledge.Age = 75;
        Array.Fill(ledge.Snow, 12);
        app.RefreshInputRegion();
        var freshSnow = new Point(ledge.Bounds.Left + 120, ledge.Bounds.Top - 4);
        Send(0x0201, freshSnow, 1);
        Check(input.Dragging && balls.Single().Held, "making and dragging the next ball must work after clearing a snowman");
        Send(0x0202, freshSnow);
    }

    static void CheckApplicationMenu()
    {
        using var menu = new SettingsForm(false, 1, 0, true, true);
        menu.AvailableApplications = () => ["chrome", "telegram", "msedge"];
        menu.SetApplicationRules(["telegram"], true);
        string[] changed = [];
        menu.HiddenApplicationsChanged += names => changed = names;
        menu.Show(); Application.DoEvents();
        IEnumerable<Control> Children(Control root) => root.Controls.Cast<Control>().SelectMany(child => Children(child).Prepend(child));
        var controls = Children(menu).ToArray();
        var pause = controls.OfType<SettingsForm.SnowButton>().Single(button => button.AccessibleName == "Пауза снегопада");
        Check(pause.Text == "Пауза" && pause.Width >= 120, "pause must be a clearly labeled button");
        Check(menu.FormBorderStyle != FormBorderStyle.None && menu.ControlBox && menu.MinimizeBox,
            "settings must expose native window actions");
        controls.OfType<SettingsForm.SnowButton>().Single(button => button.Name == "navPerformance").PerformClick(); Application.DoEvents();
        controls.OfType<SettingsForm.SnowButton>().Single(button => button.AccessibleName == "Игры и приложения…").PerformClick(); Application.DoEvents();
        var panel = Children(menu).OfType<PauseAppsPanel>().Single();
        var list = panel.Controls.OfType<CheckedListBox>().Single();
        var search = panel.Controls.OfType<TextBox>().Single(box => box.AccessibleName == "Поиск приложений для автопаузы");
        search.Text = "chrome";
        Check(list.Items.Count == 1 && list.Items[0]!.ToString()!.Contains("Google Chrome"), "app chooser must support search and readable application names");
        list.SetItemChecked(0, true); search.Clear();
        Check(changed.Contains("chrome") && changed.Contains("telegram") && changed.Length == 2, "filtering must not lose selections outside the visible list");
        panel.Controls.OfType<TextBox>().Single(box => box.AccessibleName == "Имя приложения для автопаузы").Text = @"D:\Games\MyGame.exe";
        panel.Controls.OfType<SettingsForm.SnowButton>().Single(button => button.AccessibleName == "Добавить в автопаузу").PerformClick();
        Check(changed.Contains("mygame"), "manually added executables must be selected and published");
        menu.SetState(true, 1, 0, true, true);
        Check(pause.Text == "Продолжить", "paused menu must explain how to resume effects");
        menu.Hide();
    }
}
