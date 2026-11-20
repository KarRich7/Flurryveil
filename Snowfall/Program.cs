using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace QuietSnow;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, AppStorage.ActivationName);
        using var mutex = new Mutex(true, AppStorage.InstanceName, out bool firstInstance);
        if (!firstInstance)
        {
            if (!background) signal.Set();
            return;
        }
        SetCurrentProcessExplicitAppUserModelID("Flurryveil.Desktop");
        ApplicationConfiguration.Initialize();
        using var app = new SnowApp(background);
        Application.Run(app);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}

internal sealed class SavedSettings
{
    public int Intensity { get; set; } = 1;
    public int? SnowAmount { get; set; }
    public int FallSpeed { get; set; } = 100;
    public int Wind { get; set; }
    public int FrameLimit { get; set; } = 60;
    public string SnowDisplay { get; set; } = "";
    public bool Paused { get; set; }
    public bool AutoGameMode { get; set; } = true;
    public int GameAction { get; set; }
    public bool SnowmenOnTop { get; set; }
    public bool SnowbanksOnTop { get; set; }
    public bool Snowbanks { get; set; } = true;
    public int BankSize { get; set; } = 2;
    public bool Garland { get; set; } = true;
    public int GarlandPalette { get; set; }
    public int GarlandMode { get; set; } = 1;
    public int GarlandBrightness { get; set; } = 85;
    public int GarlandScope { get; set; }
    public string[] GarlandApps { get; set; } = ["explorer"];
    public int Cleaner { get; set; }
    public int CleanerInterval { get; set; } = 40;
    public WeatherMode Weather { get; set; }
    public int EffectOpacity { get; set; } = 100;
    public bool MeltSnow { get; set; }
    public bool PowerSaving { get; set; } = true;
    public string[] HiddenApplications { get; set; } = [];
    public bool HideFullscreen { get; set; } = true;
    public bool DarkTheme { get; set; }
}
[Flags] internal enum SnowPaintLayer { Flakes = 1, Background = 2, Dust = 4, Foreground = 8, All = 15 }
internal sealed class Flake
{
    public float X, Y, Speed, Size, Phase, SwayRate, SwayWidth, Opacity, Fade, Angle;
    public int Variant;
    public bool CanSettle;
    public nint Landing;
    public float LandingX, Settling;
    public Rectangle Region;
}
internal sealed class Ball
{
    public float X, Y, R, Vx, Vy, Rotation;
    public bool Held;
    public nint Support;
    public float SupportX, SupportTop;
    public Ball? BaseBall;
    public float SnowmanAppearance = 1;
}
internal struct SnowDust
{
    public float X, Y, Vx, Vy, Life, Size, Mass, Angle, Spin, Phase;
    public int Variant;
    public nint Source;
    public bool Settles;
}
internal sealed class Surface
{
    public const int MaxColumns = 500;
    public nint Handle;
    public Rectangle Bounds;
    public Rectangle NativeBounds;
    public float[] Snow = [];
    public bool[] NewLedge = [];
    public PointF[] Contour = [];
    float age;
    public float Age
    {
        get => age;
        set
        {
            if (age == value) return;
            age = value;
            BaseHeight = Math.Min(22, 5 * (1 - MathF.Exp(-age * .8f)) + age * .25f);
        }
    }
    public float BaseHeight { get; private set; }
    public bool HasSeasonalDeposit;
    public float SnowScale = 1;
    public bool IsExplorer, IsBrowser;
    public string AppName = "";
    public uint ProcessId;
    public double LastSnowRelease;
    public int ResizeTravel;
    public float GarlandX, GarlandY, GarlandVx, GarlandVy;
    public void ClearAccumulation()
    {
        Array.Clear(Snow);
        Array.Clear(NewLedge);
        Age = 0;
        HasSeasonalDeposit = false;
    }

    public void Resize(Rectangle bounds)
    {
        Bounds = bounds;
        int count = Math.Clamp(bounds.Width / 12 + 1, 2, MaxColumns);
        if (Snow.Length != count)
        {
            Array.Resize(ref Snow, count);
            Array.Resize(ref NewLedge, count);
            Contour = new PointF[count + 2];
        }
    }
}

internal sealed partial class SnowApp : ApplicationContext
{
    readonly NotifyIcon tray;
    readonly Icon trayIcon;
    SettingsForm? settings;
    readonly SnowCleaner cleaner = new();
    readonly Func<RectangleF, float> scrapeSnow;
    readonly DesktopPersonalization personalization = new();
    readonly bool diagnostics;
    readonly FrameClock timer;
    readonly EventWaitHandle activation = new(false, EventResetMode.AutoReset, AppStorage.ActivationName);
    readonly Control? dispatcher;
    readonly RegisteredWaitHandle? activationWait;
    Rectangle[] monitorBounds = [];
    Rectangle[] workAreas = [];
    double screenPixels;
    readonly System.Windows.Forms.Timer settingsSaveTimer = new() { Interval = 500 };
    readonly List<SnowOverlay> overlays = [];
    readonly List<InputOverlay> inputOverlays = [];
    List<long> currentInputState = [], previousInputState = [];
    readonly List<Flake> flakes = [];
    readonly List<Ball> balls = [];
    readonly List<SnowDust> dust = [];
    readonly Dictionary<nint, Surface> surfaces = [];
    readonly List<Surface> ordered = [];
    readonly Random random = new();
    readonly ToolStripMenuItem pauseItem = new("Пауза");
    readonly ToolStripMenuItem banksItem = new("Сугробы на окнах");
    readonly ToolStripMenuItem garlandItem = new("Гирлянды");
    readonly ToolStripMenuItem[] weatherItems = [new("Снег"), new("Дождь"), new("Осенние листья"), new("Сакура")];
    readonly ToolStripMenuItem clearItem = new("Очистить снег");
    readonly ToolStripMenuItem[] intensityItems = [new("Лёгкий"), new("Ровный"), new("Метель")];
    readonly ToolStripMenuItem[] windItems = [new("Влево"), new("Штиль"), new("Вправо")];
    Rectangle desktop = SystemInformation.VirtualScreen;
    readonly Stopwatch clock = Stopwatch.StartNew();
    double previous, lastScan;
    bool paused, fullscreen, autoStartEnabled;
    bool darkTheme, settingsInitialized, preserveMalformedSettings, settingsWritePending;
    string? settingsNotice;
    bool autoGameMode = true, gameActive, snowmenOnTop, snowbanksOnTop;
    int gameAction;
    bool GameModeActive => autoGameMode && (gameActive || fullscreen);
    bool EffectsHidden => paused || hiddenByApplication || (hideFullscreen && fullscreen) || desktopActivity?.Unavailable == true || (GameModeActive && gameAction == 1);
    DesktopActivity? desktopActivity;
    double nextGameCheck;
    readonly GameActivity games = new();
    double nextInputUpdate, lastWindowMotion;
    float flowSpeed = 1, flowWind, breezePhase;
    bool snowbanksEnabled = true, garlandEnabled = true;
    int intensity = 1, wind, snowAmount = 100, fallSpeed = 100;
    int garlandPalette, garlandMode = 1, garlandBrightness = 85;
    int bankSize = 2;
    int frameLimit = 60;
    string snowDisplay = "";
    float BankScale => bankSize switch { 0 => .45f, 1 => .7f, _ => 1 };
    int garlandScope;
    HashSet<string> garlandApps = new(["explorer"], StringComparer.OrdinalIgnoreCase);
    readonly SnowballRenderer ballRenderer = new();
    WindowEvents? windowEvents;
    static readonly Color[][] GarlandPalettes =
    [
        [Color.FromArgb(255, 94, 108), Color.FromArgb(255, 207, 98), Color.FromArgb(103, 221, 180), Color.FromArgb(107, 176, 255)],
        [Color.FromArgb(255, 205, 122), Color.FromArgb(255, 230, 177), Color.FromArgb(255, 191, 91)],
        [Color.FromArgb(169, 229, 255), Color.FromArgb(226, 247, 255), Color.FromArgb(126, 172, 255)]
    ];
    Ball? held;
    PointF grabOffset;
    bool grabbedFromBank;
    Point lastMouse;

    public SnowApp(bool background = false, bool diagnostics = false)
    {
        this.diagnostics = diagnostics;
        scrapeSnow = ClearSnowStrip;
        timer = new FrameClock(Tick);
        if (!diagnostics) LoadSettings();
        RefreshAutoStart();
        settingsSaveTimer.Tick += (_, _) => { settingsSaveTimer.Stop(); SaveSettings(); };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть настройки", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        pauseItem.Click += (_, _) => { paused = !paused; UpdateMenu(); };
        menu.Items.Add(pauseItem);
        var modes = new ToolStripMenuItem("Погода");
        for (int i = 0; i < weatherItems.Length; i++)
        {
            var mode = (WeatherMode)i; weatherItems[i].Click += (_, _) => SetWeatherMode(mode); modes.DropDownItems.Add(weatherItems[i]);
        }
        menu.Items.Add(modes);
        var weather = new ToolStripMenuItem("Интенсивность");
        for (int i = 0; i < intensityItems.Length; i++) { int value = i; intensityItems[i].Click += (_, _) => SetIntensity(value); weather.DropDownItems.Add(intensityItems[i]); }
        menu.Items.Add(weather);
        var windMenu = new ToolStripMenuItem("Ветер");
        for (int i = 0; i < windItems.Length; i++) { int value = i - 1; windItems[i].Click += (_, _) => { wind = value; UpdateMenu(); }; windMenu.DropDownItems.Add(windItems[i]); }
        menu.Items.Add(windMenu);
        banksItem.Click += (_, _) => SetSnowbanksEnabled(!snowbanksEnabled);
        garlandItem.Click += (_, _) => SetGarlandEnabled(!garlandEnabled);
        menu.Items.Add(banksItem); menu.Items.Add(garlandItem);
        menu.Items.Add(new ToolStripSeparator());
        clearItem.Click += (_, _) => ClearSnow(); menu.Items.Add(clearItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Как пользоваться", null, (_, _) =>
        {
            ShowSettings();
            settings?.ShowHelp();
        });
        menu.Items.Add("Выход", null, (_, _) => ExitThread());
        trayIcon = LoadBrandIcon();
        tray = new NotifyIcon { Text = "Flurryveil", Icon = trayIcon, ContextMenuStrip = menu, Visible = !diagnostics };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings(dockToTray: true); };
        RebuildScreens();
        UpdateMenu();
        ScanWindows();
        settings = new SettingsForm(paused, intensity, wind, snowbanksEnabled, garlandEnabled, snowAmount, fallSpeed);
        settings.SetBrandIcon(trayIcon);
        ApplyTheme();
        settings.PauseChanged += value => { paused = value; UpdateMenu(); };
        settings.IntensityChanged += SetIntensity;
        settings.WindChanged += value => { wind = Math.Clamp(value, -1, 1); UpdateMenu(); };
        settings.SnowAmountChanged += SetSnowAmount;
        settings.FallSpeedChanged += SetFallSpeed;
        settings.FrameLimitChanged += value => { frameLimit = NormalizeFrameLimit(value); UpdateMenu(); };
        settings.SnowDisplayChanged += SetSnowDisplay;
        settings.HiddenApplicationsChanged += SetHiddenApplications;
        settings.HideFullscreenChanged += value => { hideFullscreen = value; UpdateMenu(); };
        settings.WeatherChanged += SetWeatherMode;
        settings.EffectOpacityChanged += SetEffectOpacity;
        settings.MeltChanged += value => { meltSnow = value; UpdateMenu(); };
        settings.PowerSavingChanged += value => { powerSaving = value; UpdateMenu(); };
        UpdateDisplayMenu(Screen.AllScreens);
        settings.ClearRequested += ClearSnow;
        settings.SnowBanksChanged += SetSnowbanksEnabled;
        settings.BankSizeChanged += SetBankSize;
        settings.LightsChanged += SetGarlandEnabled;
        settings.ExitRequested += () => ExitThread();
        settings.GarlandPaletteChanged += value => { garlandPalette = Math.Clamp(value, 0, 2); UpdateMenu(); };
        settings.GarlandModeChanged += value => { garlandMode = Math.Clamp(value, 0, 2); UpdateMenu(); };
        settings.GarlandBrightnessChanged += value => { garlandBrightness = Math.Clamp(value, 25, 100); UpdateMenu(); };
        settings.AvailableApplications = () => ordered.Where(surface => surface.Handle.ToInt64() > 0 && surface.AppName.Length > 0).Select(surface => surface.AppName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        settings.GarlandAppsChanged += (scope, apps) => { garlandScope = Math.Clamp(scope, 0, 1); garlandApps = NormalizeApps(apps); UpdateMenu(); };
        settings.CleanerChanged += value => { cleaner.SelectedKind = value; UpdateMenu(); };
        settings.CleanerIntervalChanged += value => { cleaner.IntervalSeconds = value; UpdateMenu(); };
        settings.GameActionChanged += value => { gameAction = Math.Clamp(value, 0, 1); UpdateMenu(); };
        settings.SnowbanksOnTopChanged += SetSnowbanksOnTop;
        settings.SnowmenOnTopChanged += value => { snowmenOnTop = value; RefreshInputRegion(); UpdateMenu(); };
        settings.AutoGameModeChanged += value => { autoGameMode = value; nextGameCheck = 0; if (!value) gameActive = false; UpdateMenu(); };
        settings.AutoStartChanged += value =>
        {
            if (AppStorage.Isolated) return;
            var result = personalization.SetAutoStart(value);
            RefreshAutoStart();
            ShowPersonalizationResult(result);
            UpdateMenu();
        };
        settings.ThemeChanged += value => { darkTheme = value; ApplyTheme(); UpdateMenu(); };
        UpdateMenu();
        settingsInitialized = true;
        if (!background && !diagnostics)
        {
            settings.Show();
            ShowSettingsNotice();
        }
        if (!diagnostics)
        {
            var ui = new Control();
            _ = ui.Handle;
            dispatcher = ui;
            activationWait = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) =>
            {
                try { ui.BeginInvoke(() => { if (!ui.IsDisposed) ShowSettings(); }); }
                catch (InvalidOperationException) { } // A queued activation can race with shutdown.
            }, null, Timeout.Infinite, executeOnlyOnce: false);
            windowEvents = new WindowEvents();
            desktopActivity = new DesktopActivity(RefreshDesktopActivity);
            timer.Start(ui);
        }
    }

    static string SettingsPath => Path.Combine(AppStorage.DirectoryPath, "settings.json");
    static HashSet<string> NormalizeApps(IEnumerable<string> names)
        => new(names.Select(NormalizeApplication).Where(name => name.Length > 0), StringComparer.OrdinalIgnoreCase);

    void LoadSettings()
    {
        try
        {
            var data = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(SettingsPath))
                ?? throw new JsonException("The settings file does not contain a settings object.");
            int oldAmount = data.Intensity switch { 0 => 50, 2 => 185, _ => 100 };
            snowAmount = Math.Clamp(data.SnowAmount ?? oldAmount, 25, 200); fallSpeed = Math.Clamp(data.FallSpeed, 25, 200);
            weatherMode = Enum.IsDefined(data.Weather) ? data.Weather : WeatherMode.Snow;
            effectOpacity = Math.Clamp(data.EffectOpacity, 10, 100); meltSnow = data.MeltSnow; powerSaving = data.PowerSaving;
            hiddenApps = NormalizeApps(data.HiddenApplications ?? []);
            hideFullscreen = data.HideFullscreen;
            intensity = Array.IndexOf(new[] { 50, 100, 185 }, snowAmount); wind = Math.Clamp(data.Wind, -1, 1);
            bankSize = Math.Clamp(data.BankSize, 0, 2);
            frameLimit = NormalizeFrameLimit(data.FrameLimit);
            snowDisplay = data.SnowDisplay ?? "";
            snowbanksEnabled = data.Snowbanks; garlandEnabled = data.Garland;
            cleaner.SelectedKind = Math.Clamp(data.Cleaner, 0, 3); cleaner.IntervalSeconds = data.CleanerInterval;
            paused = data.Paused; autoGameMode = data.AutoGameMode; gameAction = Math.Clamp(data.GameAction, 0, 1); snowmenOnTop = data.SnowmenOnTop;
            snowbanksOnTop = data.SnowbanksOnTop;
            garlandScope = Math.Clamp(data.GarlandScope, 0, 1);
            garlandApps = NormalizeApps(data.GarlandApps ?? ["explorer"]);
            garlandPalette = Math.Clamp(data.GarlandPalette, 0, 2); garlandMode = Math.Clamp(data.GarlandMode, 0, 2); garlandBrightness = Math.Clamp(data.GarlandBrightness, 25, 100);
            darkTheme = data.DarkTheme;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            preserveMalformedSettings = true;
            settingsNotice = "Не удалось прочитать настройки. Flurryveil запущен с исходными параметрами. При следующем сохранении прежний файл будет скопирован рядом с settings.json.";
            AppStorage.Log("Read settings", error);
        }
    }
    void SaveSettings()
    {
        if (diagnostics || !settingsWritePending) return;
        try
        {
            Directory.CreateDirectory(AppStorage.DirectoryPath);
            if (preserveMalformedSettings)
            {
                string backup = Path.Combine(AppStorage.DirectoryPath, $"settings.recovery-{Guid.NewGuid():N}.json");
                try { File.Copy(SettingsPath, backup); }
                catch (FileNotFoundException) { }
                preserveMalformedSettings = false;
            }
            string temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(CaptureSettings()));
            File.Move(temporary, SettingsPath, true);
            settingsWritePending = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppStorage.Log("Write settings", error);
            settingsNotice = "Не удалось сохранить настройки. Проверьте права на папку QuietSnow и свободное место на диске.";
            ShowSettingsNotice();
        }
    }
    internal SavedSettings CaptureSettings() => new() { Intensity = intensity, SnowAmount = snowAmount,
        FallSpeed = fallSpeed, Wind = wind, FrameLimit = frameLimit, SnowDisplay = snowDisplay,
        Paused = paused, AutoGameMode = autoGameMode, GameAction = gameAction, SnowmenOnTop = snowmenOnTop,
        SnowbanksOnTop = snowbanksOnTop, Snowbanks = snowbanksEnabled, BankSize = bankSize,
        Garland = garlandEnabled, GarlandPalette = garlandPalette, GarlandMode = garlandMode,
        GarlandBrightness = garlandBrightness, GarlandScope = garlandScope,
        GarlandApps = garlandApps.OrderBy(name => name).ToArray(), Cleaner = cleaner.SelectedKind,
        CleanerInterval = cleaner.IntervalSeconds, Weather = weatherMode, EffectOpacity = effectOpacity,
        MeltSnow = meltSnow, PowerSaving = powerSaving, HiddenApplications = hiddenApps.OrderBy(name => name).ToArray(),
        HideFullscreen = hideFullscreen, DarkTheme = darkTheme };
    void ShowSettings(bool dockToTray = false)
    {
        if (settings is null || settings.IsDisposed) return;
        RefreshAutoStart();
        settings.SetPersonalizationState(cleaner.SelectedKind, autoStartEnabled, !AppStorage.Isolated);
        if (!settings.Visible && dockToTray)
        {
            var work = Screen.FromPoint(Cursor.Position).WorkingArea;
            settings.Location = new Point(work.Right - settings.Width - 12, work.Bottom - settings.Height - 12);
        }
        settings.Show();
        if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
        settings.Activate();
        ShowSettingsNotice();
    }

    void ShowSettingsNotice()
    {
        if (settings is not { Visible: true } || settingsNotice is not { } notice) return;
        settingsNotice = null;
        settings.ShowMessage("Локальные настройки", notice);
    }

    static Icon LoadBrandIcon()
    {
        using var stream = typeof(SnowApp).Assembly.GetManifestResourceStream("Snowfall.Assets.Flurryveil.ico")
            ?? throw new InvalidOperationException("The Flurryveil icon resource is missing.");
        using var icon = new Icon(stream, new Size(32, 32));
        return (Icon)icon.Clone();
    }

    void ApplyTheme()
    {
        settings?.SetTheme(darkTheme);
        if (tray.ContextMenuStrip is { } menu) TrayTheme.Apply(menu, darkTheme);
    }
    void UpdateMenu()
    {
        pauseItem.Text = paused ? "Возобновить эффекты" : "Пауза";
        banksItem.Checked = snowbanksEnabled; garlandItem.Checked = garlandEnabled;
        for (int i = 0; i < weatherItems.Length; i++) weatherItems[i].Checked = i == (int)weatherMode;
        banksItem.Text = weatherMode switch { WeatherMode.Rain => "Брызги на окнах", WeatherMode.AutumnLeaves => "Листья на окнах", WeatherMode.CherryBlossom => "Лепестки на окнах", _ => "Сугробы на окнах" };
        clearItem.Text = weatherMode switch { WeatherMode.Rain => "Убрать капли", WeatherMode.AutumnLeaves => "Убрать листья", WeatherMode.CherryBlossom => "Убрать лепестки", _ => "Очистить снег" };
        for (int i = 0; i < 3; i++) { intensityItems[i].Checked = i == intensity; windItems[i].Checked = i - 1 == wind; }
        settings?.SetState(paused, intensity, wind, snowbanksEnabled, garlandEnabled, snowAmount, fallSpeed);
        settings?.SetFrameLimit(frameLimit);
        settings?.SetGameMode(autoGameMode, gameAction, snowmenOnTop);
        settings?.SetBankSize(bankSize);
        settings?.SetSnowbanksOnTop(snowbanksOnTop);
        settings?.SetGarlandApps(garlandScope, garlandApps.ToArray());
        settings?.SetGarlandState(garlandPalette, garlandMode, garlandBrightness);
        settings?.SetPersonalizationState(cleaner.SelectedKind, autoStartEnabled, !AppStorage.Isolated);
        settings?.SetCleanerInterval(cleaner.IntervalSeconds);
        settings?.SetWeatherState(weatherMode, effectOpacity, meltSnow, powerSaving);
        settings?.SetApplicationRules(hiddenApps.ToArray(), hideFullscreen);
        if (!diagnostics && settingsInitialized)
        {
            settingsWritePending = true;
            settingsSaveTimer.Stop();
            settingsSaveTimer.Start();
        }
    }
    static int NormalizeFrameLimit(int limit) => limit is 15 or 30 ? 30 : 60;

    void RefreshAutoStart()
    {
        if (diagnostics || AppStorage.Isolated) return;
        try { autoStartEnabled = personalization.AutoStartEnabled; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or InvalidOperationException) { autoStartEnabled = false; }
    }

    void SetSnowDisplay(string display)
    {
        if (snowDisplay.Equals(display, StringComparison.OrdinalIgnoreCase)) return;
        snowDisplay = display;
        ScanWindows();
        RefreshInputRegion();
        UpdateMenu();
    }

    Screen[] SnowScreens(Screen[] screens)
    {
        if (snowDisplay.Length == 0) return screens;
        var selected = screens.FirstOrDefault(screen => screen.DeviceName.Equals(snowDisplay, StringComparison.OrdinalIgnoreCase))
            ?? screens.FirstOrDefault(screen => screen.Primary);
        return selected is null ? screens : [selected];
    }

    void UpdateDisplayMenu(Screen[] screens)
    {
        if (settings is not null) settings.SetSnowDisplays(screens, snowDisplay);
    }

    public int SnowAmount => snowAmount;
    public int FallSpeed => fallSpeed;
    void SetIntensity(int value)
    {
        intensity = Math.Clamp(value, 0, 2);
        (snowAmount, fallSpeed, wind) = intensity switch
        {
            0 => (50, 82, 0),
            1 => (100, 100, 0),
            _ => (185, 130, 1)
        };
        UpdateMenu();
    }
    public void SetSnowAmount(int value) { snowAmount = Math.Clamp(value, 25, 200); intensity = Array.IndexOf(new[] { 50, 100, 185 }, snowAmount); UpdateMenu(); }
    public void SetFallSpeed(int value) { fallSpeed = Math.Clamp(value, 25, 200); UpdateMenu(); }
    public void SetSnowbanksEnabled(bool value) { snowbanksEnabled = value; UpdateMenu(); foreach (var overlay in overlays) overlay.RenderFrame(); }
    void SetSnowbanksOnTop(bool enabled)
    {
        snowbanksOnTop = enabled;
        RefreshInputRegion();
        UpdateMenu();
        foreach (var overlay in overlays)
            if (overlay.Visible && !EffectsHidden) overlay.RenderFrame();
    }
    void SetBankSize(int size)
    {
        bankSize = Math.Clamp(size, 0, 2);
        foreach (var surface in surfaces.Values)
        {
            float ratio = BankScale / surface.SnowScale;
            for (int i = 0; i < surface.Snow.Length; i++) surface.Snow[i] *= ratio;
            surface.SnowScale = BankScale;
        }
        RefreshInputRegion();
        UpdateMenu();
        foreach (var overlay in overlays) overlay.RenderFrame();
    }
    public void SetGarlandEnabled(bool value) { garlandEnabled = value; UpdateMenu(); foreach (var overlay in overlays) overlay.RenderFrame(); }
    void ShowInputOverlay()
    {
        var overlay = new InputOverlay(this, desktop);
        try
        {
            overlay.Show();
            overlay.RaiseForInteraction();
            inputOverlays.Add(overlay);
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            overlay.Dispose();
            Debug.WriteLine(error);
            if (!diagnostics) tray.ShowBalloonTip(4000, "Flurryveil", "Windows не удалось включить управление снежками.", ToolTipIcon.Warning);
        }
    }

    public void InvalidateInputRegion() => previousInputState.Clear();

    public void RefreshInputRegion()
    {
        if (inputOverlays.Count == 0 || inputOverlays[0].Dragging) return;
        GpuRasterState(currentInputState, desktop, SnowPaintLayer.All, inputRegion: true);
        if (System.Runtime.InteropServices.CollectionsMarshal.AsSpan(currentInputState).SequenceEqual(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(previousInputState))) return;
        (currentInputState, previousInputState) = (previousInputState, currentInputState);
        var region = InputOverlay.EmptyRegion();
        if (SupportsSnowballs && !EffectsHidden && clearProgress < 0)
        {
            if (snowbanksEnabled)
            {
                foreach (var surface in ordered)
                {
                    if (!CanAccumulateOn(surface) || surface.Bounds.Width < 24 || surface.Age < .2f) continue;
                    using var cap = MakeSnowCap(surface);
                    using var target = new Region(cap);
                    target.Intersect(new Rectangle(surface.Bounds.Left, surface.Bounds.Top - 48, surface.Bounds.Width, 48));
                    if (!SnowbankOnTop(surface)) ExcludeCoveredWindows(target, surface);
                    region.Union(target);
                }
            }

            foreach (var ball in balls)
            {
                using var circle = new GraphicsPath();
                float radius = BallHitRadius(ball);
                circle.AddEllipse(ball.X - radius, ball.Y - radius, radius * 2, radius * 2);
                using var target = new Region(circle);
                ClipBall(target, ball);
                region.Union(target);
            }
            if (settings is { Visible: true, WindowState: not FormWindowState.Minimized })
            {
                region.Exclude(settings.Bounds);
                foreach (var popup in settings.OwnedForms) if (popup.Visible) region.Exclude(popup.Bounds);
            }
        }

        if (snowDisplay.Length != 0 && monitorBounds.Length == 1) region.Intersect(monitorBounds[0]);
        region.Translate(-desktop.Left, -desktop.Top);
        inputOverlays[0].SetHitRegion(region);
    }

    public bool CanPickUpSnow(Point point) =>
        SupportsSnowballs && !EffectsHidden && clearProgress < 0 &&
        !IsSettingsPoint(point) && (PickableBall(point) is not null || SnowAt(point));

    void ExcludeCoveredWindows(Region region, Surface surface)
    {
        foreach (var other in ordered)
        {
            if (ReferenceEquals(other, surface)) break;
            if (other.Bounds.Bottom > surface.Bounds.Top - 48 && other.Bounds.Top < surface.Bounds.Top)
                region.Exclude(other.Bounds);
        }
    }

    // A snowman belongs to its supporting window/desktop, including its input region.
    void ClipBall(Region region, Ball ball)
    {
        if (ball.Held || snowmenOnTop) return;
        foreach (var window in ordered)
        {
            if (window.Handle == ball.Support) break;
            if (window.Handle.ToInt64() >= 0 && !window.Bounds.IsEmpty) region.Exclude(window.Bounds);
        }
    }
    System.Drawing.Drawing2D.GraphicsState BeginBallPaint(Graphics graphics, Ball ball)
    {
        var state = graphics.Save();
        using var visible = new Region();
        ClipBall(visible, ball);
        graphics.SetClip(visible, CombineMode.Intersect);
        return state;
    }

    void ClearSnow() => StartClear();
    protected override void ExitThreadCore()
    {
        activationWait?.Unregister(null);
        timer.Stop(); timer.Dispose();
        dispatcher?.Dispose();
        desktopActivity?.Dispose(); desktopActivity = null;
        settingsSaveTimer.Stop(); SaveSettings(); settingsSaveTimer.Dispose();
        foreach (var overlay in inputOverlays) overlay.Close();
        foreach (var overlay in overlays) overlay.Close();
        settings?.Dispose();
        ballRenderer.Dispose(); weatherArt.Dispose(); windowEvents?.Dispose();
        activation.Dispose();
        tray.Visible = false; tray.Dispose(); trayIcon.Dispose();
        base.ExitThreadCore();
    }
    void Tick()
    {
        bool pointerPressed = (Native.GetAsyncKeyState(SystemInformation.MouseButtonsSwapped ? 2 : 1) & 0x8000) != 0;
        if (inputOverlays.Count > 0)
            inputOverlays[0].PollDrag(Cursor.Position, pointerPressed, (Native.GetAsyncKeyState(0x1B) & 0x8000) != 0);
        double now = clock.Elapsed.TotalSeconds;
        float dt = previous == 0 ? .022f : (float)Math.Clamp(now - previous, 0, .075);
        previous = now;
        if (now >= nextGameCheck)
        {
            bool activeGame = !diagnostics && games.IsActive();
            gameActive = autoGameMode && activeGame;
            hiddenByApplication = !diagnostics && hiddenApps.Contains(games.ForegroundName);
            nextGameCheck = now + 1;
        }
        bool orderChanged = !paused && windowEvents?.ConsumeOrderChange() == true;
        if (!paused && desktopActivity?.Unavailable != true && (orderChanged || now - lastScan > .5 ||
            (now - lastScan > .08 && windowEvents?.ConsumeChange() == true)))
        {
            ScanWindows();
            lastScan = now;
            if (orderChanged)
            {
                foreach (var overlay in overlays) overlay.RaiseForInteraction();
                foreach (var input in inputOverlays) input.RaiseForInteraction();
            }
        }
        UpdateAutomaticPauseStatus();
        if (EffectsHidden)
        {
            timer.Interval = 150;
            previousPointer = null;
            if (clearProgress >= 0) FinishClear();
            if (inputOverlays.Count > 0 && inputOverlays[0].Dragging) inputOverlays[0].FinishDrag();
            RefreshInputRegion();
            foreach (var overlay in overlays) if (overlay.Visible) overlay.Hide();
            return;
        }
        RefreshSurfaceBounds(now);
        foreach (var surface in ordered) AdvanceGarland(surface, dt);
        TrackCursor(Cursor.Position, pointerPressed, dt);
        bool layerShown = false;
        foreach (var overlay in overlays)
        {
            if (overlay.Visible) continue;
            overlay.Show();
            layerShown = true;
        }
        if (layerShown) foreach (var input in inputOverlays) input.RaiseForInteraction();
        timer.Interval = 1000d / EffectiveFrameLimit;
        UpdateSnowfall(dt);
        if (CleanerActive && cleaner.SelectedKind != 0) cleaner.Update(dt, workAreas, scrapeSnow);
        if (SupportsSnowballs) UpdateBalls(dt);
        AdvanceClear(dt);
        UpdateDust(dt);
        if (balls.Count > 0 || now >= nextInputUpdate)
        {
            RefreshInputRegion();
            nextInputUpdate = now + .033;
        }
        foreach (var overlay in overlays) overlay.RenderFrame();
    }
    int GroundAt(float x, float y)
    {
        for (int i = 0; i < monitorBounds.Length; i++)
        {
            var bounds = monitorBounds[i];
            if (x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y <= bounds.Bottom)
                return i < workAreas.Length ? workAreas[i].Bottom : bounds.Bottom;
        }
        return desktop.Bottom;
    }
    void UpdateSnowfall(float dt)
    {
        if (weatherMode != WeatherMode.Snow) { UpdateSeasonalWeather(dt); return; }
        float areaFactor = (float)Math.Clamp(screenPixels / (1920 * 1080), .5, 4);
        int target = Math.Clamp((int)(150 * snowAmount / 100f * areaFactor), 20, 720);
        if (GameModeActive) target = Math.Max(20, target / 3);
        if (LowPowerMode) target = Math.Max(20, target * 3 / 4);
        while (flakes.Count < target) flakes.Add(NewFlake());

        float blend = 1 - MathF.Exp(-2.8f * dt);
        flowSpeed += (fallSpeed / 100f - flowSpeed) * blend;
        float windTarget = wind * (intensity == 2 ? 59 : 32);
        flowWind += (windTarget - flowWind) * blend;
        breezePhase = (breezePhase + dt * .32f) % MathF.Tau;
        float breeze = MathF.Sin(breezePhase) * (intensity == 2 ? 9 : 3.5f);
        if (snowbanksEnabled) foreach (var surface in ordered) if (CanAccumulateOn(surface)) surface.Age = Math.Min(75, surface.Age + dt);
        for (int i = flakes.Count - 1; i >= 0; i--)
        {
            var flake = flakes[i];
            flake.Fade = Math.Clamp(flake.Fade + dt * (i < target ? 2.4f : -1.8f), 0, 1);
            if (i >= target && flake.Fade == 0) { flakes.RemoveAt(i); continue; }
            if (flake.Settling > 0)
            {
                Surface? resting = null;
                foreach (var surface in ordered)
                    if (surface.Handle == flake.Landing) { resting = surface; break; }
                flake.Settling -= dt;
                if (resting is null || resting.Bounds.IsEmpty || flake.Settling <= 0) { ResetFlake(flake); continue; }
                flake.X = resting.Bounds.Left + flake.LandingX;
                flake.Y = SnowTop(resting, flake.X) - flake.Size * .25f;
                continue;
            }
            float oldY = flake.Y;
            flake.Phase = (flake.Phase + dt * flake.SwayRate) % MathF.Tau;
            flake.X += (flowWind + breeze + MathF.Sin(flake.Phase) * flake.SwayWidth) * dt;
            flake.Y += flake.Speed * flowSpeed * (1 + .08f * MathF.Cos(flake.Phase)) * dt;
            if (cursorWind != PointF.Empty)
            {
                float x = flake.X - cursorPoint.X, y = flake.Y - cursorPoint.Y;
                float nearby = Math.Max(0, 1 - (x * x + y * y) / 900);
                flake.X += cursorWind.X * nearby * dt;
                flake.Y += cursorWind.Y * nearby * dt;
            }
            if (flake.X < flake.Region.Left - 12) { flake.X = flake.Region.Right + 11; continue; }
            if (flake.X > flake.Region.Right + 12) { flake.X = flake.Region.Left - 11; continue; }
            if (flake.Y > GroundAt(flake.X, flake.Y)) { ResetFlake(flake); continue; }
            if (!snowbanksEnabled || !flake.CanSettle || clearProgress >= 0) continue;

            Surface? landing = null;
            float contactX = 0, contactY = float.MaxValue;
            foreach (var surface in ordered)
            {
                var bounds = surface.Bounds;
                if (!CanAccumulateOn(surface) || flake.X < bounds.Left || flake.X >= bounds.Right ||
                    oldY > bounds.Top || flake.Y < bounds.Top - 40 * surface.SnowScale) continue;
                float top = SnowTop(surface, flake.X);
                if (oldY > top || flake.Y < top || top >= contactY || !IsExposed(surface, flake.X)) continue;
                landing = surface; contactX = flake.X; contactY = top;
            }
            if (landing is not null)
            {
                int column = Math.Clamp((int)((contactX - landing.Bounds.Left) / 12), 0, landing.Snow.Length - 1);
                landing.Snow[column] = Math.Min(18 * landing.SnowScale, landing.Snow[column] + .65f * landing.SnowScale);
                flake.Landing = landing.Handle; flake.LandingX = contactX - landing.Bounds.Left;
                flake.Settling = .42f; flake.Y = contactY;
            }
        }
        if (snowbanksEnabled && clearProgress < 0)
        {
            foreach (var surface in ordered)
            {
                for (int i = 1; i < surface.Snow.Length - 1; i++)
                {
                    float average = (surface.Snow[i - 1] + surface.Snow[i + 1]) / 2;
                    surface.Snow[i] += (average - surface.Snow[i]) * Math.Min(.12f, dt * 1.3f);
                }
                for (int i = 0; i < surface.NewLedge.Length; i++)
                {
                    if (!surface.NewLedge[i]) continue;
                    if (surface.Snow[i] < 0)
                        surface.Snow[i] = Math.Min(0, surface.Snow[i] + dt * 1.5f);
                    if (surface.Snow[i] >= 0) surface.NewLedge[i] = false;
                }
            }
            RelaxDrifts(dt);
            ApplyMelting(dt);
        }
    }
    Flake NewFlake()
    {
        var region = desktop;
        double pick = random.NextDouble() * screenPixels;
        foreach (var bounds in monitorBounds)
        {
            region = bounds;
            pick -= (double)bounds.Width * bounds.Height;
            if (pick < 0) break;
        }
        float depth = (float)random.NextDouble();
        var flake = new Flake
        {
            Region = region,
            X = random.Next(region.Left, region.Right),
            Y = random.Next(region.Top, region.Bottom),
            Speed = 25 + depth * 55 + random.Next(12),
            Size = 1.25f + depth * depth * 3.1f,
            Opacity = .34f + depth * .58f,
            SwayWidth = 3 + depth * 10,
            SwayRate = .45f + (float)random.NextDouble() * 1.65f,
            Phase = (float)random.NextDouble() * MathF.Tau,
            CanSettle = random.NextDouble() < .78
        };
        if (weatherMode != WeatherMode.Snow) ConfigureSeasonalFlake(flake);
        return flake;
    }
    void ResetFlake(Flake flake)
    {
        flake.Settling = 0; flake.Landing = 0;
        flake.Y = flake.Region.Top - random.Next(5, 65);
        flake.X = random.Next(flake.Region.Left, flake.Region.Right);
        flake.CanSettle = random.NextDouble() < .78;
    }
    void UpdateDust(float dt)
    {
        bool seasonal = PlantWeather;
        for (int i = dust.Count - 1; i >= 0; i--)
        {
            var grain = dust[i];
            grain.Life -= dt;
            if (grain.Life <= 0 || grain.Y > desktop.Bottom + 40)
            {
                dust[i] = dust[^1];
                dust.RemoveAt(dust.Count - 1);
                continue;
            }
            float oldY = grain.Y;
            grain.X += grain.Vx * dt;
            grain.Y += grain.Vy * dt;
            if (seasonal)
            {
                grain.Phase += dt * (1.4f + grain.Variant % 4 * .2f); grain.Angle += grain.Spin * dt;
                grain.X += MathF.Sin(grain.Phase) * 18 * dt;
                grain.Vx *= MathF.Exp(-dt * 1.3f);
                grain.Vy = Math.Min(72, grain.Vy + 55 * dt);
            }
            else { grain.Vx *= 1 - dt * .95f; grain.Vy = Math.Min(330, grain.Vy + 310 * dt); }
            if (grain.Settles && weatherMode != WeatherMode.Rain && snowbanksEnabled && grain.Vy > 0)
            {
                Surface? landing = null;
                foreach (var surface in ordered)
                {
                    var bounds = surface.Bounds;
                    if (surface.Handle == grain.Source || !CanAccumulateOn(surface) || grain.X < bounds.Left || grain.X >= bounds.Right ||
                        oldY > bounds.Top || grain.Y < bounds.Top - 40 * surface.SnowScale ||
                        (landing is not null && landing.Bounds.Top <= bounds.Top)) continue;
                    float top = SnowTop(surface, grain.X);
                    if (oldY > top || grain.Y < top || (!SnowbankOnTop(surface) && !IsExposed(surface, grain.X))) continue;
                    landing = surface;
                }
                if (landing is not null)
                {
                    int column = Math.Clamp((int)((grain.X - landing.Bounds.Left) / 12), 0, landing.Snow.Length - 1);
                    float amount = grain.Mass > 0 ? grain.Mass : grain.Size * .22f * landing.SnowScale;
                    if (seasonal) DepositSeasonalMaterial(landing, column, amount);
                    else landing.Snow[column] = Math.Min(18 * landing.SnowScale, landing.Snow[column] + amount);
                    dust[i] = dust[^1];
                    dust.RemoveAt(dust.Count - 1);
                    continue;
                }
            }
            float floor = GroundAt(grain.X, grain.Y);
            if (grain.Y >= floor) { dust[i] = dust[^1]; dust.RemoveAt(dust.Count - 1); continue; }
            dust[i] = grain;
        }
    }
    void RebuildScreens()
    {
        previousInputState.Clear();
        foreach (var layer in inputOverlays) layer.Dispose();
        inputOverlays.Clear();
        foreach (var layer in overlays) layer.Dispose();
        overlays.Clear();
        var screens = SnowScreens(Screen.AllScreens.OrderBy(screen => screen.DeviceName, StringComparer.Ordinal).ToArray());
        monitorBounds = screens.Select(screen => screen.Bounds).ToArray();
        workAreas = screens.Select(screen => screen.WorkingArea).ToArray();
        screenPixels = monitorBounds.Sum(bounds => (double)bounds.Width * bounds.Height);
        desktop = SystemInformation.VirtualScreen;
        flakes.Clear();
        foreach (var bounds in monitorBounds)
        {
            var layer = new SnowOverlay(this, bounds);
            overlays.Add(layer);
            if (!EffectsHidden) layer.Show();
        }
        if (!diagnostics) ShowInputOverlay();
    }
    void RefreshSurfaceBounds(double now)
    {
        foreach (var surface in ordered)
        {
            if (surface.Handle.ToInt64() < 0) continue;
            if (!Native.IsWindowVisible(surface.Handle) || Native.IsIconic(surface.Handle) ||
                !Native.GetWindowRect(surface.Handle, out var raw))
            {
                MoveSnowSurface(surface, Rectangle.Empty, now);
                continue;
            }
            MoveSnowSurface(surface, TrackWindowFrame(surface, raw.ToRectangle()), now);
        }
    }

    static Rectangle TrackWindowFrame(Surface surface, Rectangle raw)
    {
        Rectangle previous = surface.NativeBounds;
        surface.NativeBounds = raw;
        if (!previous.IsEmpty && !surface.Bounds.IsEmpty)
        {
            if (raw == previous) return surface.Bounds;
            if (raw.Size == previous.Size)
            {
                var translated = surface.Bounds;
                translated.Offset(raw.Left - previous.Left, raw.Top - previous.Top);
                return translated;
            }
            if (Math.Abs(raw.Width - previous.Width) < 96 && Math.Abs(raw.Height - previous.Height) < 96)
            {
                var frameDuringResize = Rectangle.FromLTRB(
                    raw.Left + surface.Bounds.Left - previous.Left,
                    raw.Top + surface.Bounds.Top - previous.Top,
                    raw.Right + surface.Bounds.Right - previous.Right,
                    raw.Bottom + surface.Bounds.Bottom - previous.Bottom);
                if (frameDuringResize.Width > 0 && frameDuringResize.Height > 0)
                    return frameDuringResize;
            }
        }

        if (Native.DwmGetWindowAttributeRect(surface.Handle, 9, out var frame, Marshal.SizeOf<Native.RECT>()) == 0)
        {
            var visible = frame.ToRectangle();
            if (Rectangle.Inflate(raw, 32, 32).Contains(visible) && !visible.IsEmpty &&
                Math.Abs(raw.Width - visible.Width) <= 32 && Math.Abs(raw.Height - visible.Height) <= 32)
                return visible;
        }
        return raw;
    }

    bool IsExposed(Surface surface, float x) => IsExposed(surface, x, surface.Bounds.Top);

    bool IsExposed(Surface surface, float x, float y)
    {
        foreach (var other in ordered)
        {
            if (ReferenceEquals(other, surface)) return true;
            if (x >= other.Bounds.Left && x < other.Bounds.Right && y >= other.Bounds.Top && y < other.Bounds.Bottom) return false;
        }
        return true;
    }
    void ScanWindows()
    {
        var screens = Screen.AllScreens.OrderBy(s => s.DeviceName, StringComparer.Ordinal).ToArray();
        var activeScreens = SnowScreens(screens);
        var currentBounds = activeScreens.Select(screen => screen.Bounds).ToArray();
        workAreas = activeScreens.Select(screen => screen.WorkingArea).ToArray();
        if (!monitorBounds.SequenceEqual(currentBounds) || desktop != SystemInformation.VirtualScreen) RebuildScreens();
        UpdateDisplayMenu(screens);
        var found = new HashSet<nint>(); ordered.Clear();
        nint foreground = Native.GetForegroundWindow();
        fullscreen = false;
        if (foreground != nint.Zero && Native.GetWindowRect(foreground, out var fr))
        {
            Native.GetWindowThreadProcessId(foreground, out uint processId);
            var s = Screen.FromRectangle(fr.ToRectangle()).Bounds;
            string foregroundClass = Native.ClassName(foreground);
            fullscreen = foregroundClass is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") && processId != (uint)Environment.ProcessId && !Native.IsZoomed(foreground) && (Native.GetWindowLongPtr(foreground, -16).ToInt64() & 0x00C00000L) == 0 && fr.Left <= s.Left && fr.Top <= s.Top && fr.Right >= s.Right && fr.Bottom >= s.Bottom && Native.IsWindowVisible(foreground);
        }
        Native.EnumWindows((handle, _) => {
            if (!Native.IsWindowVisible(handle) || Native.IsIconic(handle)) return true;
            Native.GetWindowThreadProcessId(handle, out uint pid);
            if (pid == (uint)Environment.ProcessId || Native.GetWindow(handle, 4) != nint.Zero) return true;
            if (Native.DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (Native.DwmGetWindowAttributeRect(handle, 9, out var rect, Marshal.SizeOf<Native.RECT>()) != 0 && !Native.GetWindowRect(handle, out rect)) return true;
            Rectangle bounds = rect.ToRectangle();
            if (bounds.Width < 120 || bounds.Height < 60 || !currentBounds.Any(screen => screen.IntersectsWith(bounds))) return true;
            string cls = Native.ClassName(handle);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow") return true;
            long exStyle = Native.GetWindowLongPtr(handle, -20).ToInt64();
            if ((exStyle & (0x80L | 0x08000000L)) != 0 || Native.GetWindowTextLength(handle) == 0) return true;
            if (!surfaces.TryGetValue(handle, out var surface)) surfaces[handle] = surface = new Surface { Handle = handle, SnowScale = BankScale };
            if (surface.ProcessId != pid || surface.AppName.Length == 0)
            {
                surface.ProcessId = pid;
                try { using var process = Process.GetProcessById((int)pid); surface.AppName = process.ProcessName.ToLowerInvariant(); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { surface.AppName = ""; }
            }
            surface.IsExplorer = cls is "CabinetWClass" or "ExploreWClass";
            surface.IsBrowser = cls is "Chrome_WidgetWin_1" or "MozillaWindowClass";
            if (Native.GetWindowRect(handle, out var raw))
                bounds = TrackWindowFrame(surface, raw.ToRectangle());
            MoveSnowSurface(surface, bounds, clock.Elapsed.TotalSeconds); ordered.Add(surface); found.Add(handle);
            return true;
        }, nint.Zero);
        // The ground follows the top of the taskbar.
        foreach (var screen in activeScreens)
        {
            nint key = -1 - Array.IndexOf(screens, screen);
            if (!surfaces.TryGetValue(key, out var surface)) surfaces[key] = surface = new Surface { Handle = key, SnowScale = BankScale };
            surface.Resize(new Rectangle(screen.Bounds.Left, screen.WorkingArea.Bottom, screen.Bounds.Width, Math.Max(1, screen.Bounds.Bottom - screen.WorkingArea.Bottom)));
            ordered.Add(surface); found.Add(key);
        }
        foreach (var key in surfaces.Keys.Where(k => !found.Contains(k)).ToArray())
        {
            if (key.ToInt64() >= 0) MoveSnowSurface(surfaces[key], Rectangle.Empty, clock.Elapsed.TotalSeconds);
            surfaces.Remove(key);
        }
    }
    void UpdateBalls(float dt)
    {
        const float appearanceDuration = .42f;
        foreach (var ball in balls)
            if (ball.SnowmanAppearance < 1)
                ball.SnowmanAppearance = Math.Min(1, ball.SnowmanAppearance + dt / appearanceDuration);
        balls.Sort((a, b) => b.Y.CompareTo(a.Y));
        int steps = Math.Max(1, (int)MathF.Ceiling(dt / .011f));
        for (int i = 0; i < steps; i++) StepBalls(dt / steps);
    }
    static int StackHeight(Ball ball)
    {
        int height = 1;
        while (height < 4 && ball.BaseBall is { } below)
        {
            height++;
            ball = below;
        }
        return height;
    }

    static float StackSpacing(Ball upper, Ball lower) =>
        upper.R + lower.R - Math.Clamp(Math.Min(upper.R, lower.R) * .11f, .8f, 3.5f);

    Surface? FindBallSupport(nint handle)
    {
        foreach (var surface in ordered)
            if (surface.Handle == handle && !surface.Bounds.IsEmpty) return surface;
        return null;
    }

    bool HasBallAbove(Ball support)
    {
        foreach (var ball in balls)
            if (ReferenceEquals(ball.BaseBall, support)) return true;
        return false;
    }

    void StepBalls(float dt)
    {
        // Move every ball attached to a window before resolving collisions.
        // A stack moves as a unit regardless of the order its balls were made.
        foreach (var ball in balls)
        {
            if (ball.Held) continue;
            if (ball.Support != nint.Zero)
            {
                var support = FindBallSupport(ball.Support);
                if (support is null || !CanAccumulateOn(support) || ball.SupportX < ball.R || ball.SupportX > support.Bounds.Width - ball.R)
                    ball.Support = nint.Zero;
                else { ball.X = support.Bounds.Left + ball.SupportX; ball.Y += support.Bounds.Top - ball.SupportTop; ball.SupportTop = support.Bounds.Top; }
            }
        }
        foreach (var ball in balls)
        {
            if (ball.Held) continue;
            if (ball.BaseBall is { } parent)
            {
                if (balls.Contains(parent) && !parent.Held && Math.Abs(ball.X - parent.X) < parent.R * .65f)
                {
                    ball.X = parent.X; ball.Y = parent.Y - StackSpacing(ball, parent);
                    ball.Vx = ball.Vy = 0; ball.Support = parent.Support; ball.SupportX = parent.SupportX; ball.SupportTop = parent.SupportTop;
                    continue;
                }
                ball.BaseBall = null;
            }
            float moveX = ball.Vx * dt;
            ball.X += moveX;
            ball.Rotation += moveX / Math.Max(1, ball.R);
            if (ball.Support != 0) ball.SupportX += moveX;
            var screenArea = Screen.FromPoint(new Point((int)ball.X, (int)ball.Y)).WorkingArea;
            if (ball.X - ball.R < screenArea.Left) { ball.X = screenArea.Left + ball.R; ball.Vx = Math.Abs(ball.Vx) * .15f; }
            if (ball.X + ball.R > screenArea.Right) { ball.X = screenArea.Right - ball.R; ball.Vx = -Math.Abs(ball.Vx) * .15f; }
            float oldBottom = ball.Y + ball.R;
            ball.Vy = Math.Min(ball.Vy + 350 * dt, 350);
            ball.Y += ball.Vy * dt;
            float floor = screenArea.Bottom;
            Surface? landed = null;
            foreach (var surface in ordered)
            {
                if (!CanAccumulateOn(surface) || ball.X < surface.Bounds.Left + ball.R || ball.X > surface.Bounds.Right - ball.R) continue;
                float ledgeTop = surface.Bounds.Top;
                float contact = snowbanksEnabled ? SnowTop(surface, ball.X) : ledgeTop;
                if (contact > floor || ball.Y + ball.R < contact || !IsExposed(surface, ball.X, contact)) continue;

                bool alreadySupported = ball.Support == surface.Handle;
                bool skimmedSnow = oldBottom <= ledgeTop + 3 && ball.Y <= contact;
                if (!alreadySupported && !skimmedSnow && oldBottom > contact + 3) continue;
                floor = contact;
                landed = surface;
            }
            foreach (var other in balls)
            {
                if (ReferenceEquals(ball, other) || other.Held || ball.Y > other.Y) continue;
                float dx = ball.X - other.X, sum = ball.R + other.R;
                if (Math.Abs(dx) >= sum) continue;
                float top = other.Y - MathF.Sqrt(sum * sum - dx * dx);
                if (ball.Y >= top && oldBottom - ball.R <= top + 5)
                {
                    ball.Y = top; ball.Vy = 0;
                    bool canStack = StackHeight(other) < 3 && !HasBallAbove(other);
                    if (canStack && Math.Abs(dx) < other.R * .55f && ball.R <= other.R * 1.08f && Math.Abs(ball.Vx) < 110)
                    {
                        ball.BaseBall = other; ball.X = other.X; ball.Y = other.Y - StackSpacing(ball, other); ball.Vx = 0;
                        ball.Support = other.Support; ball.SupportX = other.SupportX; ball.SupportTop = other.SupportTop;
                        if (ball.R >= 9) BeginSnowmanAppearance(ball);
                    }
                    else
                    {
                        ball.Support = 0;
                        float slope = Math.Clamp(dx / sum, -.9f, .9f);
                        if (Math.Abs(slope) < .04f)
                            slope = MathF.CopySign(.17f, dx == 0 ? (ball.Vx == 0 ? 1 : ball.Vx) : dx);
                        ball.Vx += slope * 420 * dt;
                    }
                }
            }
            if (ball.BaseBall is null && ball.Y + ball.R >= floor)
            {
                ball.Y = floor - ball.R;
                ball.Vy = 0;
                ball.Vx = MathF.CopySign(Math.Max(0, Math.Abs(ball.Vx) - 95 * dt), ball.Vx);
                ball.Support = landed?.Handle ?? nint.Zero;
                if (landed is not null)
                {
                    ball.SupportTop = landed.Bounds.Top;
                    ball.SupportX = ball.X - landed.Bounds.Left;
                }
            }
            else if (ball.BaseBall is null) ball.Support = nint.Zero;
        }
        balls.RemoveAll(b => b.Y - b.R > desktop.Bottom + 100);
    }
    public bool MouseDown(Point point)
    {
        if (!CanPickUpSnow(point)) return false;
        lastMouse = point;
        held = PickableBall(point);
        grabOffset = PointF.Empty;
        grabbedFromBank = false;
        if (held is null)
        {
            grabbedFromBank = SnowAt(point);
            if (!grabbedFromBank) return false;
            float collected = CollectSnow(point, 20, MathF.PI * 144 * .35f);
            if (collected < 8) return false;
            if (balls.Count >= 30) balls.RemoveAt(0);
            held = new Ball { R = Math.Clamp(MathF.Sqrt(collected / (MathF.PI * .35f)), 6, 12) };
            MoveHeldBall(held, point);
            balls.Add(held);
        }
        else grabOffset = new PointF(held.X - point.X, held.Y - point.Y);
        held.Held = true; held.Vx = held.Vy = 0; held.Support = nint.Zero; held.BaseBall = null;
        lastDragTime = clock.Elapsed.TotalSeconds;
        return true;
    }
    double lastDragTime;
    public void MouseMove(Point point, bool down)
    {
        if (!SupportsSnowballs || !down || point == lastMouse) return;
        if (held is not null)
        {
            float dx = point.X - lastMouse.X, dy = point.Y - lastMouse.Y;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            float nextRadius = Math.Min(MaximumBallRadius, held.R + distance * .065f);
            float needed = MathF.PI * .35f * (nextRadius * nextRadius - held.R * held.R);
            int samples = Math.Clamp((int)MathF.Ceiling(distance / 8), 1, 32);
            float gathered = 0;
            for (int i = 1; i <= samples && gathered < needed; i++)
            {
                var sample = new Point(lastMouse.X + (int)(dx * i / samples),
                    lastMouse.Y + (int)(dy * i / samples) + (grabbedFromBank ? 0 : (int)MathF.Round(grabOffset.Y + held.R)));
                gathered += CollectSnow(sample, (int)Math.Min(90, held.R + 8), needed - gathered);
            }
            float previousRadius = held.R;
            held.R = Math.Min(MaximumBallRadius, MathF.Sqrt(held.R * held.R + gathered / (MathF.PI * .35f)));
            if (!grabbedFromBank) grabOffset.Y -= held.R - previousRadius;

            double now = clock.Elapsed.TotalSeconds;
            float elapsed = (float)Math.Clamp(now - lastDragTime, .016, .15);
            held.Vx = Math.Clamp((point.X - lastMouse.X) / elapsed * .45f, -180, 180);
            held.Rotation += (point.X - lastMouse.X) / Math.Max(12, held.R);
            lastDragTime = now;
            MoveHeldBall(held, point);
        }
        lastMouse = point;
    }
    void MoveHeldBall(Ball ball, Point point)
    {
        var area = Screen.FromPoint(point).WorkingArea;
        float y = point.Y + (grabbedFromBank ? -ball.R : grabOffset.Y);
        ball.X = Math.Clamp(point.X + grabOffset.X, desktop.Left + ball.R, desktop.Right - ball.R);
        ball.Y = Math.Clamp(y, area.Top + ball.R, area.Bottom - ball.R);
    }
    public void MouseUp()
    {
        if (held != null) { held.Held = false; if (clock.Elapsed.TotalSeconds - lastDragTime > .12) held.Vx = 0; }
        held = null;
        grabbedFromBank = false;
    }

    internal bool HasHeldBall => SupportsSnowballs && held is { Held: true };

    void CancelBallDrag()
    {
        foreach (var input in inputOverlays) input.FinishDrag(refresh: false);
        if (held is not null) MouseUp();
    }
    float CollectSnow(Point point, int radius, float areaNeeded)
    {
        if (!snowbanksEnabled || areaNeeded <= 0) return 0;
        float collected = 0;
        foreach (var surface in ordered)
        {
            var bounds = surface.Bounds;
            if (!CanAccumulateOn(surface) || point.Y < bounds.Top - 48 || point.Y > bounds.Top + 6 || point.X + radius < bounds.Left || point.X - radius > bounds.Right) continue;
            for (int i = Math.Max(0, (point.X - radius - bounds.Left) / 12); i < surface.Snow.Length; i++)
            {
                float x = Math.Min(bounds.Right - 1, bounds.Left + i * 12);
                if (x > point.X + radius || collected >= areaNeeded) break;
                if (!SnowbankOnTop(surface) && !IsExposed(surface, x, point.Y)) continue;
                float height = Math.Max(0, surface.BaseHeight * surface.SnowScale * SnowRipples[i] + surface.Snow[i]);
                if (point.Y < bounds.Top - height - 4) continue;
                float take = Math.Min(height, (areaNeeded - collected) / 12);
                surface.Snow[i] -= take;
                collected += take * 12;
            }
        }
        return collected;
    }

    void Sweep(Point point, int radius, bool onlyNearSnow = false, float velocity = 0)
    {
        if (!snowbanksEnabled || clearProgress >= 0) return;
        foreach (var surface in ordered)
        {
            var bounds = surface.Bounds;
            if (bounds.IsEmpty || point.X + radius < bounds.Left || point.X - radius >= bounds.Right ||
                point.Y > bounds.Top + (onlyNearSnow ? 6 : 0) || point.Y < bounds.Top - 48) continue;
            int first = Math.Max(0, (point.X - radius - bounds.Left) / 12);
            int last = Math.Min(surface.Snow.Length - 1, (point.X + radius - bounds.Left) / 12);
            for (int i = first; i <= last; i++)
            {
                float x = Math.Min(bounds.Right - 1, bounds.Left + i * 12);
                float height = Math.Max(0, surface.BaseHeight * surface.SnowScale * SnowRipples[i] + surface.Snow[i]);
                if ((!SnowbankOnTop(surface) && !IsExposed(surface, x, point.Y)) || (onlyNearSnow && point.Y < bounds.Top - height - 6)) continue;
                ReleaseSnow(surface, i, onlyNearSnow ? .76f : .95f, velocity, -53, true);
            }
        }
    }

    public IReadOnlyList<Flake> GpuFlakes => flakes;
    public IReadOnlyList<SnowDust> GpuDust => dust;
    public void GpuExclusions(List<Rectangle> exclusions)
    {
        exclusions.Clear();
        if (settings is not { Visible: true, WindowState: not FormWindowState.Minimized }) return;
        exclusions.Add(settings.Bounds);
        foreach (Form popup in settings.OwnedForms) if (popup.Visible) exclusions.Add(popup.Bounds);
    }

    public void GpuRasterState(List<long> state, Rectangle viewport, SnowPaintLayer layer, bool inputRegion = false,
        Rectangle? rasterRegion = null)
    {
        state.Clear();
        void Float(float value) => state.Add(BitConverter.SingleToInt32Bits(value));
        void Rect(Rectangle rect)
        {
            state.Add(rect.X); state.Add(rect.Y); state.Add(rect.Width); state.Add(rect.Height);
        }

        Rect(viewport);
        state.Add((int)weatherMode);
        if (inputRegion && !SupportsSnowballs) return;
        state.Add(snowbanksOnTop ? 1 : 0);
        state.Add(snowmenOnTop ? 1 : 0);
        if (inputRegion)
        {
            state.Add(EffectsHidden || clearProgress >= 0 ? 1 : 0);
            state.Add(snowbanksEnabled ? 1 : 0);
            state.Add(snowDisplay.Length == 0 ? 0 : 1);
            if (snowDisplay.Length != 0) foreach (var monitor in monitorBounds) Rect(monitor);
        }
        if (inputRegion && settings is { Visible: true, WindowState: not FormWindowState.Minimized })
        {
            Rect(settings.Bounds);
            foreach (Form popup in settings.OwnedForms)
                if (popup.Visible) Rect(popup.Bounds);
        }
        state.Add(long.MinValue);
        // Order and bounds affect occlusion even when a window has no decoration.
        foreach (var surface in ordered)
        {
            state.Add(surface.Handle.ToInt64());
            if (rasterRegion is { } region)
            {
                // Keep every handle in z-order, including supports outside this
                // band. Only intersecting rectangles can affect its pixels.
                bool intersects = surface.Bounds.IntersectsWith(region) ||
                    (layer == SnowPaintLayer.Background && DecorationBounds(surface).IntersectsWith(region));
                state.Add(intersects ? 1 : 0);
                if (intersects) Rect(surface.Bounds);
            }
            else Rect(surface.Bounds);
            if (layer != SnowPaintLayer.Background && !inputRegion) continue;
            if (inputRegion) state.Add(surface.Age < .2f ? 0 : 1);
            if (snowbanksEnabled && CanAccumulateOn(surface) && HasAccumulation(surface) &&
                (!rasterRegion.HasValue || DecorationBounds(surface).IntersectsWith(rasterRegion.Value)))
            {
                if (PlantWeather)
                {
                    // Only these integer leaf placements affect cached pixels;
                    // retain subpixel motion in the simulation without repainting.
                    for (int i = 0; i < surface.Snow.Length; i++) state.Add(SeasonalPileHeight(surface, i));
                }
                else
                {
                    Float(surface.BaseHeight);
                    Float(surface.SnowScale);
                    foreach (float height in surface.Snow) Float(height);
                }
            }
            state.Add(long.MinValue);
            if (inputRegion) continue;
            bool garland = ShouldDrawGarland(surface) &&
                (!rasterRegion.HasValue || new Rectangle(surface.Bounds.Left, surface.Bounds.Top + 1,
                    surface.Bounds.Width, 27).IntersectsWith(rasterRegion.Value));
            state.Add(garland ? 1 : 0);
            if (!garland) continue;
            Float(surface.GarlandX); Float(surface.GarlandY);
            if (garlandMode != 0) state.Add(BitConverter.DoubleToInt64Bits(clock.Elapsed.TotalSeconds));
        }
        state.Add(long.MaxValue);
        if (layer == SnowPaintLayer.Background)
        {
            state.Add(snowbanksEnabled ? 1 : 0);
            state.Add(garlandPalette); state.Add(garlandMode); state.Add(garlandBrightness);
        }
        else if (SupportsSnowballs)
        {
            foreach (var ball in balls)
            {
                Float(ball.X); Float(ball.Y); Float(ball.R); Float(ball.Rotation);
                state.Add(ball.Held ? 1 : 0);
                state.Add(ball.Vy == 0 ? 1 : 0);
                state.Add(ball.Support.ToInt64());
                state.Add(ball.BaseBall is null ? -1 : balls.IndexOf(ball.BaseBall));
                if (!inputRegion && ball.BaseBall is not null) Float(ball.SnowmanAppearance);
            }
            if (!inputRegion) state.Add(GameModeActive ? 1 : 0);
            if (!inputRegion && CleanerActive)
            {
                state.Add(cleaner.SelectedKind);
                Float(cleaner.PaintTime);
            }
        }
    }
    public void GpuLayerBounds(List<RectangleF> bounds, SnowPaintLayer layer)
    {
        bounds.Clear();
        if (layer == SnowPaintLayer.Background)
        {
            foreach (var surface in ordered)
                if (!surface.Bounds.IsEmpty &&
                    (snowbanksEnabled && CanAccumulateOn(surface) && HasAccumulation(surface) || ShouldDrawGarland(surface)))
                    bounds.Add(DecorationBounds(surface));
        }
        else if (SupportsSnowballs)
        {
            foreach (var ball in balls) bounds.Add(BallPaintBounds(ball));
            if (CleanerActive) cleaner.CollectGpuBounds(bounds);
        }
    }

    static Rectangle DecorationBounds(Surface surface) =>
        new(surface.Bounds.Left, surface.Bounds.Top - 48, surface.Bounds.Width, 76);

    bool SnowbankOnTop(Surface surface) => snowbanksOnTop && surface.Handle.ToInt64() < 0;

    static RectangleF BallPaintBounds(Ball ball) =>
        new(ball.X - ball.R * 2 - 8, ball.Y - ball.R - 3, ball.R * 4 + 16, ball.R * 3 + 12);

    public void Paint(Graphics g, Rectangle viewport, SnowPaintLayer layer = SnowPaintLayer.All,
        Rectangle? rasterRegion = null, int textureY = 0)
    {
        var clip = rasterRegion ?? viewport;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(-clip.Left, textureY - clip.Top);
        g.SetClip(clip);
        // The GPU shader applies the settings-window exclusion to every layer.
        if (rasterRegion is null && settings is { Visible: true, WindowState: not FormWindowState.Minimized })
        {
            g.ExcludeClip(settings.Bounds);
            foreach (Form popup in settings.OwnedForms) if (popup.Visible) g.ExcludeClip(popup.Bounds);
        }
        using var flakeBrush = (layer & (SnowPaintLayer.Flakes | SnowPaintLayer.Dust)) != 0 ? new SolidBrush(Color.White) : null;
        if ((layer & SnowPaintLayer.Flakes) != 0)
        foreach (var f in flakes)
        {
            if (f.Region != viewport || f.X < viewport.Left - 4 || f.X >= viewport.Right + 4 ||
                f.Y < viewport.Top - 4 || f.Y >= viewport.Bottom + 4 || f.Fade <= 0) continue;
            flakeBrush!.Color = Color.FromArgb((int)(f.Opacity * f.Fade * (f.Settling > 0 ? Math.Min(1, f.Settling / .2f) : 1) * 255), 251, 253, 255);
            if (weatherMode is WeatherMode.AutumnLeaves or WeatherMode.CherryBlossom)
                weatherArt.Draw(g, f.X, f.Y, f.Size, f.Angle, f.Variant, f.Opacity * f.Fade);
            else if (weatherMode == WeatherMode.Rain)
            {
                using var pen = new Pen(Color.FromArgb((int)(f.Opacity * f.Fade * 255), 160, 205, 242), 1.25f);
                g.DrawLine(pen, f.X, f.Y - f.Size, f.X + flowWind * .016f, f.Y);
            }
            else g.FillEllipse(flakeBrush, f.X - f.Size / 2, f.Y - f.Size / 2, f.Size, f.Size);
        }
        if ((layer & SnowPaintLayer.Background) != 0)
        foreach (var surface in ordered)
        {
            if (surface.Bounds.IsEmpty) continue;
            bool garland = ShouldDrawGarland(surface);
            bool onTop = SnowbankOnTop(surface);
            if (!garland && (onTop || !snowbanksEnabled || !HasAccumulation(surface))) continue;
            var decoration = DecorationBounds(surface);
            if (!decoration.IntersectsWith(clip)) continue;
            var state = g.Save();
            // Decorations are clipped to this window's real DWM frame, including
            // samples between visibility probes, so they never spill past its sides.
            g.SetClip(decoration, CombineMode.Intersect);
            if (!onTop || garland)
            {
                foreach (var other in ordered)
                {
                    if (ReferenceEquals(other, surface)) break;
                    if (other.Bounds.IntersectsWith(decoration) && other.Bounds.IntersectsWith(clip))
                        g.ExcludeClip(other.Bounds);
                }
            }
            if (!onTop) PaintSnowCap(g, surface);
            if (garland) DrawGarland(g, surface);
            g.Restore(state);
        }
        if ((layer & SnowPaintLayer.Background) != 0 && snowbanksOnTop && snowbanksEnabled)
        {
            for (int i = ordered.Count - 1; i >= 0; i--)
            {
                var surface = ordered[i];
                if (!SnowbankOnTop(surface) || surface.Bounds.IsEmpty) continue;
                var cap = new Rectangle(surface.Bounds.Left, surface.Bounds.Top - 48, surface.Bounds.Width, 49);
                if (!HasAccumulation(surface) || !cap.IntersectsWith(clip)) continue;
                var state = g.Save();
                g.IntersectClip(cap);
                PaintSnowCap(g, surface);
                g.Restore(state);
            }
        }
        if ((layer & SnowPaintLayer.Dust) != 0)
        foreach (var grain in dust)
        {
            if (!viewport.Contains((int)grain.X, (int)grain.Y)) continue;
            flakeBrush!.Color = Color.FromArgb((int)(235 * (grain.Settles ? 1 : Math.Clamp(grain.Life * 2.8f, 0, 1))), Color.White);
            float trail = Math.Min(4, Math.Abs(grain.Vy) * .016f);
            if (weatherMode is WeatherMode.AutumnLeaves or WeatherMode.CherryBlossom)
                weatherArt.Draw(g, grain.X, grain.Y, grain.Size, grain.Angle, grain.Variant, Math.Clamp(grain.Life * 2.8f, 0, 1));
            else
            {
                if (weatherMode == WeatherMode.Rain) flakeBrush.Color = Color.FromArgb(flakeBrush.Color.A, 160, 205, 242);
                g.FillEllipse(flakeBrush, grain.X, grain.Y - trail / 2, grain.Size, grain.Size + trail);
            }
        }
        if (SupportsSnowballs && (layer & SnowPaintLayer.Foreground) != 0)
        {
            foreach (var ball in balls)
            {
                if (rasterRegion.HasValue && !BallPaintBounds(ball).IntersectsWith(clip)) continue;
                var state = BeginBallPaint(g, ball);
                ballRenderer.Paint(g, ball);
                g.Restore(state);
            }
            foreach (var head in balls)
            {
                if (!IsSnowmanHead(head) || head.SnowmanAppearance <= 0) continue;
                var state = BeginBallPaint(g, head);
                ballRenderer.PaintSnowman(g, head, head.BaseBall!);
                g.Restore(state);
            }
            if (CleanerActive) cleaner.Paint(g, viewport);
        }
    }
    float ClearSnowStrip(RectangleF strip)
    {
        if (!CleanerActive || clearProgress >= 0) return 0;
        float collected = ClearBallsInStrip(strip);
        foreach (var surface in ordered)
        {
            var bounds = surface.Bounds;
            if (surface.Handle.ToInt64() >= 0 || Math.Abs(bounds.Top - strip.Top) > 3 ||
                strip.Right <= bounds.Left || strip.Left >= bounds.Right) continue;
            int first = Math.Max(0, (int)((strip.Left - bounds.Left + 6) / 12));
            int last = Math.Min(surface.Snow.Length - 1, (int)((strip.Right - bounds.Left + 6) / 12));
            for (int i = first; i <= last; i++)
            {
                float center = bounds.Left + i * 12;
                float cellLeft = Math.Max(bounds.Left, center - 6);
                float cellRight = i == surface.Snow.Length - 1 ? bounds.Right : Math.Min(bounds.Right, center + 6);
                float from = Math.Max(cellLeft, strip.Left);
                float to = Math.Min(cellRight, strip.Right);
                if (to <= from) continue;
                float baseHeight = surface.BaseHeight * surface.SnowScale * SnowRipples[i];
                float height = Math.Max(0, baseHeight + surface.Snow[i]);
                float amount = height * Math.Clamp((to - from) / (cellRight - from), 0, 1);
                surface.Snow[i] = Math.Max(-baseHeight, surface.Snow[i] - amount);
                collected += amount * (cellRight - cellLeft);
            }
        }
        return collected;
    }

    bool ShouldDrawGarland(Surface surface) => garlandEnabled && CanAccumulateOn(surface) && surface.Handle.ToInt64() > 0 &&
        (garlandScope == 1 || garlandApps.Contains(surface.AppName) || (surface.IsExplorer && garlandApps.Contains("explorer")));

    void ShowPersonalizationResult(PersonalizationResult result)
    {
        if (!result.Success && !result.Cancelled)
        {
            ShowSettings();
            settings?.ShowMessage("Настройки", result.Error ?? "Не удалось изменить настройку.");
        }
    }
    static readonly float[] SnowRipples = Array.ConvertAll(
        Enumerable.Range(0, Surface.MaxColumns).ToArray(),
        column => .82f + .12f * MathF.Sin(column * .63f) + .06f * MathF.Sin(column * 1.71f));

    static float SnowTop(Surface surface, float x)
    {
        if (surface.Snow.Length == 0) return surface.Bounds.Top;
        float column = Math.Clamp((x - surface.Bounds.Left) / 12, 0, surface.Snow.Length - 1);
        int left = (int)column, right = Math.Min(left + 1, surface.Snow.Length - 1);
        float baseHeight = surface.BaseHeight * surface.SnowScale;
        float h0 = Math.Clamp(baseHeight * SnowRipples[left] + surface.Snow[left], 0, 40 * surface.SnowScale);
        float h1 = Math.Clamp(baseHeight * SnowRipples[right] + surface.Snow[right], 0, 40 * surface.SnowScale);
        float taper = surface.Handle.ToInt64() < 0 ? 1 : Math.Clamp(Math.Min(x - surface.Bounds.Left, surface.Bounds.Right - x) / 10, 0, 1);
        return surface.Bounds.Top - (h0 + (h1 - h0) * (column - left)) * taper;
    }

    void PaintSnowCap(Graphics graphics, Surface surface)
    {
        if (!snowbanksEnabled || !CanAccumulateOn(surface) || !HasAccumulation(surface)) return;
        if (weatherMode != WeatherMode.Snow) { PaintSeasonalPile(graphics, surface); return; }
        using var cap = MakeSnowCap(surface);
        using var volume = new LinearGradientBrush(new RectangleF(surface.Bounds.Left, surface.Bounds.Top - 44, surface.Bounds.Width, 45),
            Color.White, Color.FromArgb(204, 220, 232), 90f);
        graphics.FillPath(volume, cap);
        using var sparkle = new SolidBrush(Color.FromArgb(125, 255, 255, 255));
        for (int i = 1; i < surface.Snow.Length; i += 2)
        {
            float x = Math.Min(surface.Bounds.Right - 2, surface.Bounds.Left + i * 12 + 3);
            float top = SnowTop(surface, x);
            if (surface.Bounds.Top - top > 4) graphics.FillEllipse(sparkle, x, top + 2, 2, 1.2f);
        }
    }

    static GraphicsPath MakeSnowCap(Surface s)
    {
        float baseline = s.Bounds.Top + .5f;
        float baseHeight = s.BaseHeight * s.SnowScale;
        var shape = new GraphicsPath();
        var points = s.Contour;
        points[0] = new PointF(s.Bounds.Left, baseline);
        for (int i = 0; i < s.Snow.Length; i++)
        {
            float x = Math.Min(s.Bounds.Right, s.Bounds.Left + i * 12);
            float taper = s.Handle.ToInt64() < 0 ? 1 : Math.Min(1, Math.Min(x - s.Bounds.Left, s.Bounds.Right - x) / 10);
            float height = Math.Clamp(baseHeight * SnowRipples[i] + s.Snow[i], 0, 40 * s.SnowScale) * taper;
            points[i + 1] = new PointF(x, baseline - height);
        }
        points[^1] = new PointF(s.Bounds.Right, baseline);
        shape.AddCurve(points, 1, s.Snow.Length - 1, .42f);
        shape.AddLine(points[^2], points[^1]);
        shape.AddLine(points[^1], points[0]);
        shape.CloseFigure();
        return shape;
    }
    void DrawGarland(Graphics g, Surface s)
    {
        var colors = GarlandPalettes[garlandPalette];
        float y = s.Bounds.Top + 2;

        using var wire = new Pen(Color.FromArgb(216, 52, 65, 59), .9f);
        using var bulb = new SolidBrush(Color.White);
        using var halo = new SolidBrush(Color.White);
        using var socket = new SolidBrush(Color.FromArgb(225, 63, 71, 64));
        int spans = Math.Max(1, (s.Bounds.Width - 16) / 68);
        float width = (s.Bounds.Width - 16f) / spans;
        float seconds = (float)(clock.Elapsed.TotalSeconds % 3600);
        for (int i = 0; i < spans; i++)
        {
            float x = s.Bounds.Left + 8 + i * width;
            float sway = s.GarlandX * (i % 2 == 0 ? 1 : .85f);
            float drop = Math.Clamp(11 + s.GarlandY, 4, 20);
            float control1 = x + Math.Clamp(width / 3 + sway, 0, width);
            float control2 = x + Math.Clamp(width * 2 / 3 + sway, 0, width);
            g.DrawBezier(wire, x, y, control1, y + drop, control2, y + drop, x + width, y);
            const int lightsPerSpan = 4;
            for (int j = 0; j < lightsPerSpan; j++)
            {
                int lightIndex = i * lightsPerSpan + j;
                float t = (j + .5f) / lightsPerSpan;
                float u = 1 - t;
                float cx = u * u * u * x + 3 * u * u * t * control1 + 3 * u * t * t * control2 + t * t * t * (x + width);
                float cy = y + 3 * t * (1 - t) * drop;
                float pulse = garlandMode switch
                {
                    1 => .75f + .25f * MathF.Sin(seconds * 2.1f + lightIndex * 2.4f),
                    2 => .52f + .48f * MathF.Sin(seconds * 2.4f - lightIndex * .3f),
                    _ => 1
                };
                float light = garlandBrightness / 100f * (.28f + .72f * pulse);
                Color color = colors[lightIndex % colors.Length];
                g.FillRectangle(socket, cx - 1.1f, cy, 2.2f, 2.4f);
                halo.Color = Color.FromArgb((int)(48 * light), color);
                g.FillEllipse(halo, cx - 4.2f, cy, 8.4f, 9);
                bulb.Color = Color.FromArgb((int)(255 * light), color);
                g.FillEllipse(bulb, cx - 1.75f, cy + 1.5f, 3.5f, 4.8f);
                bulb.Color = Color.FromArgb((int)(192 * light), Color.Ivory);
                g.FillEllipse(bulb, cx - .65f, cy + 2.1f, 1.15f, 1.5f);
            }
        }
    }

}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom); }
    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(nint handle);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern bool IsZoomed(nint hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(nint hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern nint GetWindow(nint hWnd, uint command);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hWnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] public static extern int DwmGetWindowAttributeRect(nint hWnd, int attribute, out RECT value, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint hWnd, System.Text.StringBuilder text, int max);
    public static string ClassName(nint hWnd) { var sb = new System.Text.StringBuilder(256); GetClassName(hWnd, sb, sb.Capacity); return sb.ToString(); }
}
