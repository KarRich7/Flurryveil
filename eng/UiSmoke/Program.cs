using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

internal static class Program
{
    const string ApplicationTitle = "Flurryveil";
    const string SettingsFile = "settings.json";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length != 2 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: UiSmoke <Flurryveil.exe> <screenshot-directory>");
            return 2;
        }

        ApplicationConfiguration.Initialize();
        string executable = Path.GetFullPath(args[0]);
        string screenshots = Path.GetFullPath(args[1]);
        string testProfile = Directory.CreateTempSubdirectory("flurryveil-ui-").FullName;
        string settings = Path.Combine(testProfile, SettingsFile);
        Directory.CreateDirectory(screenshots);

        try
        {
            using (var app = Launch(executable, testProfile))
            {
                try
                {
                    SmokeTest(app, settings, screenshots);
                    TestBackgroundActivation(app, executable, testProfile);
                }
                finally { StopIfRunning(app); }
            }

            using (var app = Launch(executable, testProfile))
            {
                try
                {
                    nint handle = WaitForWindow(app);
                    var window = AutomationElement.FromHandle(handle);
                    Click(window, "navPreferences", "Настройки");
                    Check(SelectedTheme(Find(window, "themeChoice", "Тема окна")) == "Тёмная",
                        "Dark theme did not persist on restart.");
                    Click(window, "navWeather", "Погода");
                    var pause = Find(window, "pauseEffects", "Продолжить");
                    Check(pause.Current.Name.Contains("Продолжить", StringComparison.OrdinalIgnoreCase),
                        "Settings did not reopen in the paused state.");
                    Check(HasSetting(settings, "Weather", 1), "Rain did not persist on restart.");
                    Exit(app, window);
                    Console.WriteLine("PASS theme, weather and pause state after restart");
                }
                finally { StopIfRunning(app); }
            }

            const string invalidSettings = "{\"Weather\":";
            File.WriteAllText(settings, invalidSettings);
            using (var app = Launch(executable, testProfile))
            {
                try
                {
                    var window = AutomationElement.FromHandle(WaitForWindow(app));
                    Click(window, "navPreferences", "Настройки");
                    Check(SelectedTheme(Find(window, "themeChoice", "Тема окна")) == "Светлая",
                        "Invalid settings did not fall back to the default theme.");
                    Check(File.ReadAllText(settings) == invalidSettings, "Startup modified the invalid settings file.");
                    Click(window, "navWeather", "Погода");
                    Click(window, "pauseEffects", "Пауза снегопада");
                    WaitFor(() => HasSetting(settings, "Weather", 0), "Default weather was not restored after invalid settings.");
                    var backups = Directory.GetFiles(testProfile, "settings.recovery-*.json");
                    Check(backups.Length == 1 && File.ReadAllText(backups[0]) == invalidSettings,
                        "The invalid settings file was not preserved before saving.");
                    Exit(app, window);
                    Console.WriteLine("PASS launch, preservation and recovery with invalid settings JSON");
                }
                finally { StopIfRunning(app); }
            }

            TestStartInTray(executable, testProfile);

            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(testProfile)) File.Delete(file);
            if (!Directory.EnumerateFileSystemEntries(testProfile).Any()) Directory.Delete(testProfile);
        }
    }

    static void SmokeTest(Process app, string settings, string screenshots)
    {
        nint handle = WaitForWindow(app);
        var window = AutomationElement.FromHandle(handle);
        Click(window, "navWeather", "Погода");
        _ = Find(window, "weatherSnow", "Снег");
        Console.WriteLine($"PASS startup at {GetDpiForWindow(handle) * 100 / 96}% DPI");
        double light = Capture(handle, screenshots, "weather-light");

        foreach (var (id, name, value) in new[]
        {
            ("weatherBlossom", "Сакура", 3), ("weatherAutumn", "Осенние листья", 2), ("weatherRain", "Дождь", 1),
        })
        {
            Click(window, id, name);
            WaitFor(() => HasSetting(settings, "Weather", value), $"Changing weather did not save {name}.");
        }

        Click(window, "pauseEffects", "Пауза снегопада");
        WaitFor(() => HasSetting(settings, "Paused", true), "Pause did not persist.");
        Click(window, "pauseEffects", "Продолжить снегопад");
        WaitFor(() => HasSetting(settings, "Paused", false), "Resume did not persist.");
        Console.WriteLine("PASS weather, pause and resume actions");

        Capture(handle, screenshots, "weather-rain-light");
        Click(window, "weatherSnow", "Снег");
        WaitFor(() => HasSetting(settings, "Weather", 0), "Changing weather back did not save snow.");
        Click(window, "navEffects", "Эффекты");
        Capture(handle, screenshots, "effects-light");
        Click(window, "navPerformance", "Производительность");
        Click(window, "frameRate30", "30 кадров/с");
        WaitFor(() => HasSetting(settings, "FrameLimit", 30), "Switching to 30 FPS did not persist.");
        Click(window, "frameRate60", "60 кадров/с");
        WaitFor(() => HasSetting(settings, "FrameLimit", 60), "Switching to 60 FPS did not persist.");
        Console.WriteLine("PASS 30 and 60 FPS preferences");
        Capture(handle, screenshots, "performance-light");
        Click(window, "navPreferences", "Настройки");
        var theme = Find(window, "themeChoice", "Тема окна");
        SelectTheme(theme, "Тёмная");
        WaitFor(() => SelectedTheme(theme) == "Тёмная", "Dark theme was not selected.");
        WaitFor(() => HasSetting(settings, "DarkTheme", true), "Dark theme was selected without committing the change.");
        Capture(handle, screenshots, "settings-dark");
        Click(window, "navWeather", "Погода");
        double dark = Capture(handle, screenshots, "weather-dark");
        Check(light - dark > 30, "The light and dark windows do not differ as expected.");
        Console.WriteLine("PASS settings navigation and light/dark rendering");

        TestCompactLayout(handle, window, screenshots);
        Click(window, "weatherRain", "Дождь");
        Click(window, "pauseEffects", "Пауза снегопада");
        WaitFor(() => HasSetting(settings, "Paused", true), "Final pause did not persist.");
    }

    static void TestCompactLayout(nint handle, AutomationElement window, string screenshots)
    {
        Check(GetWindowRect(handle, out var original), "Could not measure the initial settings size.");
        int dpi = (int)GetDpiForWindow(handle);
        int compactWidth = 500 * dpi / 96;
        int compactHeight = 415 * dpi / 96;
        try
        {
            Resize(compactWidth, compactHeight);
            Check(GetWindowRect(handle, out var smaller) &&
                smaller.Right - smaller.Left <= compactWidth && smaller.Bottom - smaller.Top <= compactHeight,
                "The settings window could not reach the compact size.");
            Click(window, "navPerformance", "Производительность");
            Click(window, "navWeather", "Погода");
            Check(Find(window, "weatherSnow", "Снег").Current.BoundingRectangle.Right <= smaller.Right,
                "The weather settings extend outside the compact window.");
            Capture(handle, screenshots, "weather-compact-dark");
            Console.WriteLine("PASS compact settings window and sidebar navigation");
        }
        finally { Resize(original.Right - original.Left, original.Bottom - original.Top); }

        void Resize(int width, int height)
        {
            const uint noMoveOrActivation = 0x0016;
            Check(SetWindowPos(handle, 0, 0, 0, width, height, noMoveOrActivation), "Could not resize settings.");
        }
    }

    static void TestBackgroundActivation(Process app, string executable, string profile)
    {
        foreach (var (name, message, parameter) in new[]
        {
            ("caption close", 0x0112u, (nint)0xF060), ("WM_CLOSE", 0x0010u, (nint)0),
        })
        {
            nint handle = WaitForWindow(app);
            Check(PostMessage(handle, message, parameter, 0), $"Could not send {name} to the settings window.");
            WaitFor(() => !app.HasExited && !IsWindowVisible(handle), $"{name} did not leave the application in the background.");
            Activate(executable, profile);
            WaitForWindow(app);
            Console.WriteLine($"PASS {name} and single-instance reactivation");
        }

        Exit(app, AutomationElement.FromHandle(WaitForWindow(app)));
        Console.WriteLine("PASS graceful exit");
    }

    static void TestStartInTray(string executable, string profile)
    {
        using var app = Launch(executable, profile, background: true);
        try
        {
            Check(app.WaitForInputIdle((int)Timeout.TotalMilliseconds) && WindowForProcess(app.Id) == 0,
                "Background launch unexpectedly opened settings.");
            Activate(executable, profile);
            Exit(app, AutomationElement.FromHandle(WaitForWindow(app)));
            Console.WriteLine("PASS background launch and foreground activation");
        }
        finally { StopIfRunning(app); }
    }

    static void Activate(string executable, string profile)
    {
        using var activation = Launch(executable, profile);
        Check(activation.WaitForExit((int)Timeout.TotalMilliseconds) && activation.ExitCode == 0,
            "The second launch did not exit normally.");
    }

    static Process Launch(string executable, string profile, bool background = false)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        if (background) info.ArgumentList.Add("--background");
        info.Environment["FLURRYVEIL_DATA_DIR"] = profile;
        return Process.Start(info) ?? throw new InvalidOperationException("Unable to launch the application.");
    }

    static nint WaitForWindow(Process app)
    {
        nint handle = 0;
        WaitFor(() =>
        {
            if (app.HasExited) throw new InvalidOperationException($"Application exited with code {app.ExitCode}.");
            handle = WindowForProcess(app.Id);
            return handle != 0;
        }, "The application did not open the settings window.");
        return handle;
    }

    static nint WindowForProcess(int processId)
    {
        nint handle = 0;
        var title = new StringBuilder(64);
        EnumWindows((candidate, _) =>
        {
            if (GetWindowThreadProcessId(candidate, out uint owner) != 0 && owner == processId && IsWindowVisible(candidate) &&
                GetWindowText(candidate, title, title.Capacity) > 0 && title.ToString() == ApplicationTitle)
            {
                handle = candidate;
                return false;
            }
            return true;
        }, 0);
        return handle;
    }

    static void Exit(Process app, AutomationElement window)
    {
        Click(window, "navPreferences", "Настройки");
        Click(window, "exitApp", "Выйти из Flurryveil");
        Check(app.WaitForExit((int)Timeout.TotalMilliseconds) && app.ExitCode == 0,
            "The application did not exit normally.");
    }

    static void StopIfRunning(Process app)
    {
        if (!app.HasExited)
        {
            app.Kill();
            app.WaitForExit();
        }
    }

    static AutomationElement Find(AutomationElement window, string id, string name)
    {
        AutomationElement? found = null;
        WaitFor(() =>
        {
            found = FindVisible(AutomationElement.AutomationIdProperty, id) ?? FindVisible(AutomationElement.NameProperty, name);
            return found is not null;
        }, $"Could not find the visible control {id} ({name}).");
        return found!;

        AutomationElement? FindVisible(AutomationProperty property, string value)
        {
            var matches = window.FindAll(TreeScope.Descendants, new PropertyCondition(property, value));
            return matches.Cast<AutomationElement>().FirstOrDefault(element =>
                !element.Current.IsOffscreen && element.Current.IsEnabled && element.Current.ControlType != ControlType.Text);
        }
    }

    static void Click(AutomationElement window, string id, string name)
    {
        var element = Find(window, id, name);
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern))
        {
            try { ((InvokePattern)pattern).Invoke(); return; }
            catch (InvalidOperationException) { /* An owner-drawn WinForms control can expose Invoke without handling it. */ }
        }

        var target = (nint)element.Current.NativeWindowHandle;
        Check(target != 0 && PostMessage(target, 0x0101, 0x20, 0), $"Unable to activate the control {id} by keyboard.");
    }

    static string SelectedTheme(AutomationElement combo)
    {
        var target = (nint)combo.Current.NativeWindowHandle;
        Check(target != 0, "The theme ComboBox has no native window handle.");
        Check(SendMessageTimeout(target, 0x0147, 0, 0, 2, 2000, out nint selection) != 0,
            "Unable to read the selected native ComboBox item.");
        return selection switch { 0 => "Светлая", 1 => "Тёмная", _ => "" };
    }

    static void SelectTheme(AutomationElement combo, string name)
    {
        var expand = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        expand.Expand();
        var choice = combo.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));
        Check(choice is not null, $"Could not find the theme {name}.");
        ((SelectionItemPattern)choice.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        if (expand.Current.ExpandCollapseState == ExpandCollapseState.Expanded) expand.Collapse();
    }

    static bool HasSetting<T>(string path, string key, T expected)
    {
        try
        {
            using var data = JsonDocument.Parse(File.ReadAllText(path));
            return data.RootElement.TryGetProperty(key, out var value) && Equals(value.Deserialize<T>(), expected);
        }
        catch (Exception error) when (error is IOException or JsonException) { return false; }
    }

    static double Capture(nint window, string output, string name)
    {
        Thread.Sleep(220);
        Check(GetWindowRect(window, out var bounds), "Could not measure the application window.");
        using var captured = new Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(captured))
        {
            nint dc = graphics.GetHdc();
            try { Check(PrintWindow(window, dc, 2), "Could not capture the application window."); }
            finally { graphics.ReleaseHdc(dc); }
        }

        var area = new Rectangle(0, 0, captured.Width, captured.Height);
        if (DwmGetWindowAttribute(window, 9, out var frame, Marshal.SizeOf<WindowRect>()) == 0)
        {
            var visible = Rectangle.FromLTRB(frame.Left - bounds.Left, frame.Top - bounds.Top,
                frame.Right - bounds.Left, frame.Bottom - bounds.Top);
            if (!visible.IsEmpty && area.Contains(visible)) area = visible;
        }
        using var image = captured.Clone(area, PixelFormat.Format24bppRgb);

        var colors = new HashSet<int>();
        double total = 0;
        int samples = 0;
        for (int y = 8; y < image.Height; y += 12)
        for (int x = 8; x < image.Width; x += 12)
        {
            Color pixel = image.GetPixel(x, y);
            total += 0.2126 * pixel.R + 0.7152 * pixel.G + 0.0722 * pixel.B;
            colors.Add(pixel.ToArgb());
            samples++;
        }

        Check(colors.Count >= 16, "The captured application window did not contain a complete rendering.");
        image.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
        Console.WriteLine($"Captured {name}.png ({image.Width} × {image.Height})");
        return total / samples;
    }

    static void WaitFor(Func<bool> ready, string message)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            if (ready()) return;
            Thread.Sleep(75);
        } while (clock.Elapsed < Timeout);
        throw new InvalidOperationException(message);
    }

    static void Check([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowRect { public int Left, Top, Right, Bottom; }

    delegate bool EnumerateWindow(nint window, nint parameter);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumerateWindow callback, nint parameter);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(nint window, StringBuilder title, int length);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(nint window, out WindowRect rect);

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    static extern bool PrintWindow(nint window, nint deviceContext, uint flags);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(nint window, int attribute, out WindowRect rect, int size);
}
