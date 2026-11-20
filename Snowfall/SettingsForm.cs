using System.Runtime.InteropServices;

namespace QuietSnow;

internal sealed partial class SettingsForm : Form
{
    readonly Panel sidebar, content, viewport, header;
    readonly PictureBox logo;
    readonly Label brand, pageTitle;
    readonly SnowButton pauseButton;
    readonly SnowButton[] navigation, weatherModes, presets, winds, bankSizes, cleanerButtons, frameOptions, gameActions;
    readonly SnowSlider amount, speed, opacity, cleanerInterval, garlandBrightness;
    readonly SnowSwitch banks, banksOnTop, snowmen, lights, melt, power, autoGame, fullScreen, autoStart;
    readonly Label bankTitle;
    readonly SnowButton clear, applicationRules;
    readonly ComboBox displays, themeChoice;
    readonly WeatherPreview preview = new();
    readonly Panel weatherPage, effectsPage, performancePage, preferencesPage, rulesPage, garlandAppsPage, messagePage;
    readonly PauseAppsPanel pauseApps = new();
    readonly GarlandAppsPanel garlandApps = new();
    readonly SnowButton[] garlandPalettes, garlandModes;
    readonly Label messageBody;
    readonly ToolTip hints = new();
    readonly Dictionary<Panel, string> pageNames = [];
    Panel? activePage;
    int activeSection, homeScroll;
    bool syncing, updatingDisplays, paused, dark;
    string? pauseReason;
    WeatherMode mode;
    string[] hiddenApps = [], selectedApps = ["explorer"];
    string[] screenIds = [""], screenLabels = ["Все мониторы"];
    int appScope;

    public Func<string[]>? AvailableApplications;
    public event Action? ClearRequested, ExitRequested;
    public event Action<bool>? PauseChanged, SnowBanksChanged, SnowbanksOnTopChanged, SnowmenOnTopChanged,
        LightsChanged, MeltChanged, PowerSavingChanged, AutoGameModeChanged, HideFullscreenChanged, AutoStartChanged, ThemeChanged;
    public event Action<int>? IntensityChanged, WindChanged, SnowAmountChanged, FallSpeedChanged, EffectOpacityChanged,
        BankSizeChanged, CleanerChanged, CleanerIntervalChanged, GarlandPaletteChanged, GarlandModeChanged,
        GarlandBrightnessChanged, FrameLimitChanged, GameActionChanged;
    public event Action<string>? SnowDisplayChanged;
    public event Action<string[]>? HiddenApplicationsChanged;
    public event Action<int, string[]>? GarlandAppsChanged;
    public event Action<WeatherMode>? WeatherChanged;

    public SettingsForm(bool paused, int intensity, int wind, bool snowBanks, bool garlands, int snowAmount = 100, int fallSpeed = 100)
    {
        Text = "Flurryveil";
        Font = new Font("Segoe UI", 10f);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(920, 756);
        MaximizeBox = false;
        DoubleBuffered = true;

        sidebar = new Panel { Dock = DockStyle.Left, Width = 208 };
        content = new Panel { Dock = DockStyle.Fill };
        Controls.AddRange([content, sidebar]);
        logo = new PictureBox { Bounds = new Rectangle(22, 24, 34, 34), SizeMode = PictureBoxSizeMode.Zoom, TabStop = false };
        brand = Caption(sidebar, "Flurryveil", 65, 27, 123, 27, 14, true);
        sidebar.Controls.Add(logo);
        navigation = new SnowButton[4];
        string[] labels = ["Погода", "Эффекты", "Производительность", "Настройки"];
        string[] icons = ["cloud", "sparkles", "gauge", "sliders"];
        string[] navNames = ["navWeather", "navEffects", "navPerformance", "navPreferences"];
        for (int i = 0; i < navigation.Length; i++)
        {
            int section = i;
            var button = Button(sidebar, labels[i], 12, 88 + i * 49, 184, 42, () => Navigate(section), icons[i], navNames[i]);
            button.Nav = true;
            navigation[i] = button;
            hints.SetToolTip(button, labels[i]);
        }

        viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        header = new Panel { Dock = DockStyle.Top, Height = 76 };
        content.Controls.AddRange([viewport, header]);
        pageTitle = Caption(header, "", 28, 21, 482, 41, 22, true);
        pageTitle.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        pauseButton = Button(header, "Пауза", 0, 25, 134, 37, () => PauseChanged?.Invoke(!this.paused), "pause", "pauseEffects");
        pauseButton.Left = header.ClientSize.Width - 162;
        pauseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        pauseButton.Primary = true;

        weatherPage = Page("Погода", 674);
        preview.Bounds = new Rectangle(28, 0, weatherPage.Width - 56, 174);
        Stretch(preview);
        weatherPage.Controls.Add(preview);
        Caption(weatherPage, "Вид осадков", 28, 199, 300, 25, 11, true);
        weatherModes = Choices(weatherPage, 28, 230, weatherPage.Width - 56, 72,
            ["Снег", "Дождь", "Листва", "Сакура"], value => WeatherChanged?.Invoke((WeatherMode)value),
            ["snow", "rain", "leaf", "flower"], ["weatherSnow", "weatherRain", "weatherAutumn", "weatherBlossom"]);
        weatherModes[2].AccessibleName = "Осенние листья";
        var motion = Card(weatherPage, "Движение осадков", 323, 290);
        Caption(motion, "Интенсивность", 18, 48, 280, 23, 9, muted: true);
        presets = Choices(motion, 18, 74, motion.Width - 36, 36, ["Лёгкая", "Средняя", "Сильная", "Своя"], ChoosePreset);
        amount = Slider(motion, "Плотность осадков", 123, 25, 200, 100, value => SnowAmountChanged?.Invoke(value), name: "densitySlider");
        speed = Slider(motion, "Скорость падения", 181, 25, 200, 100, value => FallSpeedChanged?.Invoke(value));
        Caption(motion, "Ветер", 18, 246, 118, 30, 10);
        winds = Choices(motion, 154, 242, motion.Width - 172, 34, ["Влево", "Штиль", "Вправо"], value => WindChanged?.Invoke(value - 1));
        melt = Toggle(weatherPage, "Таяние и увядание", 624, value => MeltChanged?.Invoke(value), inset: 28);

        effectsPage = Page("Эффекты", 551);
        var acc = Card(effectsPage, "Осадки на окнах", 0, 208);
        bankTitle = Caption(acc, "Сугробы", 18, 52, 290, 29);
        banks = ToggleAt(acc, bankTitle, value => SnowBanksChanged?.Invoke(value));
        bankSizes = Choices(acc, 18, 92, acc.Width - 36, 34, ["Маленькие", "Средние", "Большие"], value => BankSizeChanged?.Invoke(value));
        banksOnTop = Toggle(acc, "Сугробы у панели поверх окон", 139, value => SnowbanksOnTopChanged?.Invoke(value));
        snowmen = Toggle(acc, "Снежки и снеговики поверх окон", 174, value => SnowmenOnTopChanged?.Invoke(value));
        var decorations = Card(effectsPage, "Гирлянды", 224, 266);
        lights = Toggle(decorations, "На кромках окон", 45, value => LightsChanged?.Invoke(value));
        garlandPalettes = Choices(decorations, 18, 87, decorations.Width - 36, 34, ["Цветная", "Тёплая", "Ледяная"], value => GarlandPaletteChanged?.Invoke(value));
        garlandModes = Choices(decorations, 18, 130, decorations.Width - 36, 34, ["Постоянный", "Мерцание", "Волна"], value => GarlandModeChanged?.Invoke(value));
        garlandBrightness = Slider(decorations, "Яркость гирлянд", 171, 25, 100, 85, value => GarlandBrightnessChanged?.Invoke(value));
        Stretch(Button(decorations, "Приложения для гирлянд", 18, 227, decorations.Width - 36, 30, OpenGarlandApps, "apps"));
        clear = Button(effectsPage, "Убрать снег", 28, 496, effectsPage.Width - 56, 38, () => ClearRequested?.Invoke(), "shovel");
        Stretch(clear);

        performancePage = Page("Производительность", 435);
        var displayCard = Card(performancePage, "Отображение", 0, 230);
        opacity = Slider(displayCard, "Непрозрачность эффектов", 43, 10, 100, 100, value => EffectOpacityChanged?.Invoke(value));
        Caption(displayCard, "Частота кадров", 18, 102, 270, 23, 9, muted: true);
        frameOptions = Choices(displayCard, 18, 127, displayCard.Width - 36, 36, ["60 кадров/с", "30 кадров/с"], value => FrameLimitChanged?.Invoke(value == 0 ? 60 : 30));
        Caption(displayCard, "Мониторы", 18, 172, 250, 22, 9, muted: true);
        displays = Combo(displayCard, "Мониторы для осадков", 18, 195);
        displays.SelectedIndexChanged += (_, _) =>
        {
            if (!updatingDisplays && displays.SelectedIndex >= 0) SnowDisplayChanged?.Invoke(screenIds[displays.SelectedIndex]);
        };
        var pauseCard = Card(performancePage, "Автоматическая пауза", 246, 176);
        power = Toggle(pauseCard, "Экономить заряд батареи", 43, value => PowerSavingChanged?.Invoke(value));
        autoGame = Toggle(pauseCard, "Автопауза в играх", 74, value => AutoGameModeChanged?.Invoke(value));
        gameActions = Choices(pauseCard, 18, 105, pauseCard.Width - 36, 32, ["Уменьшать эффекты", "Выключать"], value => GameActionChanged?.Invoke(value));
        applicationRules = Button(pauseCard, "Игры и приложения…", 18, 143, pauseCard.Width - 36, 26, OpenRules, "apps");
        applicationRules.Subtle = true;
        Stretch(applicationRules);

        preferencesPage = Page("Настройки", 442);
        var app = Card(preferencesPage, "Приложение", 0, 147);
        autoStart = Toggle(app, "Запускать при входе в Windows", 45, value => AutoStartChanged?.Invoke(value));
        Caption(app, "Тема окна", 18, 84, 240, 24, 9, muted: true);
        themeChoice = Combo(app, "Тема окна", 18, 110);
        themeChoice.Name = "themeChoice";
        themeChoice.Items.AddRange(["Светлая", "Тёмная"]);
        themeChoice.SelectedIndexChanged += (_, _) =>
        {
            bool selectedDark = themeChoice.SelectedIndex == 1;
            if (themeChoice.SelectedIndex >= 0 && selectedDark != dark) ThemeChanged?.Invoke(selectedDark);
        };
        var cleaner = Card(preferencesPage, "Снегоуборщик", 163, 157);
        cleanerButtons = Choices(cleaner, 18, 43, cleaner.Width - 36, 36, ["Выкл.", "Бульдозер", "Машинка", "Санта"], value => CleanerChanged?.Invoke(value));
        cleanerInterval = Slider(cleaner, "Пауза между проходами", 90, 5, 180, 40, value => CleanerIntervalChanged?.Invoke(value), " с");
        Stretch(Button(preferencesPage, "Как пользоваться", 28, 336, preferencesPage.Width - 56, 37, ShowHelp, "help"));
        Stretch(Button(preferencesPage, "Выйти из Flurryveil", 28, 383, preferencesPage.Width - 56, 37, () => ExitRequested?.Invoke(), "exit", "exitApp"));

        rulesPage = Page("Игры и приложения", 675);
        BackButton(rulesPage);
        var rules = Card(rulesPage, "Полноэкранный режим", 48, 86);
        fullScreen = Toggle(rules, "Скрывать эффекты в полном экране", 46, value => HideFullscreenChanged?.Invoke(value));
        pauseApps.Bounds = new Rectangle(28, 150, rulesPage.Width - 56, 506);
        Stretch(pauseApps);
        pauseApps.RunningApplications = () => AvailableApplications?.Invoke() ?? [];
        pauseApps.Changed += names => { hiddenApps = names; HiddenApplicationsChanged?.Invoke(names); };
        rulesPage.Controls.Add(pauseApps);

        garlandAppsPage = Page("Приложения для гирлянд", 489);
        BackButton(garlandAppsPage);
        garlandApps.Bounds = new Rectangle(28, 48, garlandAppsPage.Width - 56, 422);
        Stretch(garlandApps);
        garlandApps.Changed += (scope, names) => { appScope = scope; selectedApps = names; GarlandAppsChanged?.Invoke(scope, names); };
        garlandAppsPage.Controls.Add(garlandApps);

        messagePage = Page("", 338);
        BackButton(messagePage);
        var message = Card(messagePage, "", 48, 266);
        messageBody = Caption(message, "", 18, 17, message.Width - 36, 232);
        messageBody.AutoEllipsis = false;
        Stretch(messageBody);

        viewport.SuspendLayout();
        foreach (var panel in pageNames.Keys) viewport.Controls.Add(panel);
        viewport.ResumeLayout(false);
        SetTheme(false);
        SetState(paused, intensity, wind, snowBanks, garlands, snowAmount, fallSpeed);
        SetWeatherState(WeatherMode.Snow, 100, false, true);
        SetFrameLimit(60);
        Navigate(0);
        Resize += (_, _) => LayoutShell();
        LayoutShell();
    }

    public void SetBrandIcon(Icon icon)
    {
        Icon = icon;
        logo.Image?.Dispose();
        logo.Image = icon.ToBitmap();
    }

    void ChoosePreset(int value)
    {
        if (value < 3) IntensityChanged?.Invoke(value);
        else amount.Focus();
    }

    public void SetTheme(bool useDark)
    {
        dark = useDark;
        themeChoice.SelectedIndex = dark ? 1 : 0;
        var ui = Palette;
        Apply(this, ui.Canvas);
        UpdateTitleBar();
        void Apply(Control control, Color background)
        {
            if (control == sidebar) background = ui.Sidebar;
            if (control is MenuCanvas { Card: true }) background = ui.Surface;
            if (control is ComboBox or TextBox or ListBox) background = ui.Input;
            control.BackColor = background;
            control.ForeColor = control is SnowLabel { Secondary: true } ? ui.Muted : ui.Ink;
            control.Invalidate();
            foreach (Control child in control.Controls) Apply(child, background);
        }
    }

    public void SetState(bool isPaused, int intensity, int wind, bool snowBanks, bool garlands, int snowAmount = 100, int fallSpeed = 100)
    {
        paused = isPaused;
        syncing = true;
        amount.Value = snowAmount;
        speed.Value = fallSpeed;
        banks.Checked = snowBanks;
        lights.Checked = garlands;
        syncing = false;
        pauseButton.Text = paused ? "Продолжить" : "Пауза";
        pauseButton.Glyph = paused ? "play" : "pause";
        pauseButton.AccessibleName = paused ? "Продолжить снегопад" : "Пауза снегопада";
        pauseButton.Invalidate();
        SelectOption(presets, intensity < 0 ? 3 : Math.Clamp(intensity, 0, 2));
        SelectOption(winds, Math.Clamp(wind, -1, 1) + 1);
        UpdateWeatherControls();
    }

    public void SetWeatherState(WeatherMode value, int effectOpacity, bool melts, bool powerSaving)
    {
        mode = value;
        syncing = true;
        opacity.Value = effectOpacity;
        melt.Checked = melts;
        power.Checked = powerSaving;
        syncing = false;
        SelectOption(weatherModes, (int)mode);
        UpdateWeatherControls();
    }

    void UpdateWeatherControls()
    {
        bankTitle.Text = mode switch { WeatherMode.Rain => "Брызги на окнах", WeatherMode.AutumnLeaves => "Скопления листьев", WeatherMode.CherryBlossom => "Скопления лепестков", _ => "Сугробы" };
        banks.AccessibleName = bankTitle.Text;
        clear.Text = mode switch { WeatherMode.Rain => "Убрать капли", WeatherMode.AutumnLeaves => "Убрать листья", WeatherMode.CherryBlossom => "Убрать лепестки", _ => "Убрать снег" };
        clear.AccessibleName = clear.Text;
        banksOnTop.Enabled = banks.Checked && mode != WeatherMode.Rain;
        snowmen.Enabled = mode == WeatherMode.Snow;
        melt.Enabled = mode != WeatherMode.Rain;
        foreach (var button in bankSizes) button.Enabled = banksOnTop.Enabled;
        foreach (var button in cleanerButtons) button.Enabled = mode == WeatherMode.Snow;
        cleanerInterval.Enabled = mode == WeatherMode.Snow && cleanerButtons.Any(button => button.Selected && button != cleanerButtons[0]);
        RefreshPreview();
    }

    public void SetAutomaticPauseReason(string? reason) { pauseReason = reason; RefreshPreview(); }
    void RefreshPreview() => preview.SetState(mode, paused ? "Эффекты на паузе" : pauseReason ?? "Эффекты включены", paused || pauseReason is not null);
    public void SetFrameLimit(int limit) => SelectOption(frameOptions, limit == 30 ? 1 : 0);
    public void SetBankSize(int size) => SelectOption(bankSizes, Math.Clamp(size, 0, 2));
    public void SetCleanerInterval(int seconds) { syncing = true; cleanerInterval.Value = seconds; syncing = false; }
    public void SetSnowbanksOnTop(bool enabled) { syncing = true; banksOnTop.Checked = enabled; syncing = false; }
    public void SetGameMode(bool enabled, int action, bool onTop)
    {
        syncing = true;
        autoGame.Checked = enabled;
        snowmen.Checked = onTop;
        syncing = false;
        SelectOption(gameActions, Math.Clamp(action, 0, 1));
        foreach (var choice in gameActions) choice.Enabled = enabled;
    }

    public void SetPersonalizationState(int cleaner, bool runOnLogin, bool allowAutoStart = true)
    {
        SelectOption(cleanerButtons, Math.Clamp(cleaner, 0, 3));
        cleanerInterval.Enabled = mode == WeatherMode.Snow && cleaner != 0;
        syncing = true;
        autoStart.Checked = runOnLogin;
        autoStart.Enabled = allowAutoStart;
        syncing = false;
    }

    public void SetGarlandApps(int scope, string[] apps) { appScope = scope; selectedApps = apps; }
    public void SetGarlandState(int palette, int motion, int brightness)
    {
        SelectOption(garlandPalettes, Math.Clamp(palette, 0, 2));
        SelectOption(garlandModes, Math.Clamp(motion, 0, 2));
        syncing = true;
        garlandBrightness.Value = brightness;
        syncing = false;
    }

    public void SetApplicationRules(string[] names, bool fullscreen)
    {
        hiddenApps = names;
        applicationRules.Text = names.Length == 0 ? "Игры и приложения…" : $"Игры и приложения ({names.Length})";
        syncing = true;
        fullScreen.Checked = fullscreen;
        syncing = false;
    }

    public void SetSnowDisplays(Screen[] screens, string display)
    {
        string[] ids = ["", ..screens.Select(screen => screen.DeviceName)];
        string[] labels = ["Все мониторы", ..screens.Select(screen => $"{screen.DeviceName.Replace(@"\\.\DISPLAY", "Экран ")}{(screen.Primary ? " (основной)" : "")}")];
        updatingDisplays = true;
        try
        {
            if (!ids.SequenceEqual(screenIds) || !labels.SequenceEqual(screenLabels) || displays.Items.Count == 0)
            {
                screenIds = ids;
                screenLabels = labels;
                displays.BeginUpdate();
                try { displays.Items.Clear(); displays.Items.AddRange(labels); }
                finally { displays.EndUpdate(); }
            }
            int index = Array.FindIndex(screenIds, id => id.Equals(display, StringComparison.OrdinalIgnoreCase));
            if (index < 0) index = 1 + Array.FindIndex(screens, screen => screen.Primary);
            if (displays.SelectedIndex != index) displays.SelectedIndex = index;
        }
        finally { updatingDisplays = false; }
    }

    void OpenRules() { pauseApps.SetState(hiddenApps, AvailableApplications?.Invoke() ?? []); OpenDetail(rulesPage); }
    void OpenGarlandApps() { garlandApps.SetState(appScope, selectedApps, AvailableApplications?.Invoke() ?? []); OpenDetail(garlandAppsPage); }
    public void ShowHelp() => ShowMessage("Как пользоваться",
        "Смахните снег быстрым движением курсора вдоль кромки окна или панели задач.\n\n" +
        "Зажмите снег и потяните мышь, чтобы слепить снежок. Катите его по снегу, чтобы увеличить. Поставьте два или три шара друг на друга. " +
        "Правая кнопка разбивает шар, Esc отпускает его.\n\nЗакройте окно настроек, чтобы свернуть приложение в трей. Значок Flurryveil открывает настройки снова.");

    public void ShowMessage(string title, string message)
    {
        pageNames[messagePage] = title;
        messageBody.Text = message;
        OpenDetail(messagePage);
    }

    Panel HomePage => activeSection switch { 1 => effectsPage, 2 => performancePage, 3 => preferencesPage, _ => weatherPage };

    void Navigate(int section)
    {
        activeSection = section;
        SelectOption(navigation, section);
        OpenPage(HomePage);
    }

    void OpenDetail(Panel panel)
    {
        if (activePage == HomePage) homeScroll = -viewport.AutoScrollPosition.Y;
        OpenPage(panel);
        panel.Controls["back"]?.Focus();
    }

    void GoBack()
    {
        OpenPage(HomePage, homeScroll);
        navigation[activeSection].Focus();
    }

    void OpenPage(Panel panel, int scrollY = 0)
    {
        viewport.SuspendLayout();
        if (activePage is not null && activePage != panel) activePage.Hide();
        activePage = panel;
        pageTitle.Text = pageNames[panel];
        panel.Show();
        panel.BringToFront();
        viewport.ResumeLayout(performLayout: true);
        viewport.AutoScrollPosition = new Point(0, scrollY);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape && activePage != HomePage) { GoBack(); return true; }
        if (keyData >= (Keys.Control | Keys.D1) && keyData <= (Keys.Control | Keys.D4))
        {
            Navigate((keyData & Keys.KeyCode) - Keys.D1);
            navigation[activeSection].Focus();
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitToWorkArea();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        BeginInvoke(() => { LayoutShell(); FitToWorkArea(); });
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateTitleBar(); }
    void UpdateTitleBar()
    {
        if (!IsHandleCreated) return;
        int value = dark && !SystemInformation.HighContrast ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref value, sizeof(int));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // WinForms classifies a direct WM_CLOSE as TaskManagerClosing.
        if (e.CloseReason is CloseReason.UserClosing or CloseReason.TaskManagerClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            hints.Dispose();
            logo?.Image?.Dispose();
        }
        base.Dispose(disposing);
    }

    void FitToWorkArea()
    {
        var work = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(work.Width - 16, Px(480)), Math.Min(work.Height - 16, Px(396)));
        Width = Math.Min(Width, work.Width - 16);
        Height = Math.Min(Height, work.Height - 16);
        Left = Math.Clamp(Left, work.Left + 8, Math.Max(work.Left + 8, work.Right - Width - 8));
        Top = Math.Clamp(Top, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - Height - 8));
    }

    void LayoutShell()
    {
        bool compact = ClientSize.Width < Px(790);
        sidebar.Width = Px(compact ? 66 : 208);
        logo.Left = Px(compact ? 16 : 22);
        brand.Visible = !compact;
        foreach (var button in navigation)
        {
            button.Width = Px(compact ? 42 : 184);
            button.IconOnly = compact;
            button.Invalidate();
        }
        pageTitle.Font = compact ? CompactHeading : Heading;
        pauseButton.Left = header.ClientSize.Width - Px(162);
        pageTitle.Width = Math.Max(Px(35), pauseButton.Left - pageTitle.Left - Px(12));
    }

    static readonly Font Heading = new("Segoe UI Semibold", 22f);
    static readonly Font CompactHeading = new("Segoe UI Semibold", 15f);
    int Px(int value) => (int)Math.Round(value * DeviceDpi / 96f);

    Panel Page(string title, int height)
    {
        var panel = new Panel { Dock = DockStyle.Top, Size = new Size(712, height), Visible = false };
        pageNames.Add(panel, title);
        // The document is sized at design width before child controls are anchored.
        return panel;
    }

    MenuCanvas Card(Panel page, string title, int y, int height)
    {
        var box = new MenuCanvas { Card = true, Bounds = new Rectangle(28, y, page.Width - 56, height) };
        Stretch(box);
        page.Controls.Add(box);
        if (title.Length > 0) Stretch(Caption(box, title, 18, 13, box.Width - 36, 28, 12, true));
        return box;
    }

    void BackButton(Panel panel) => Button(panel, "Назад", 28, 0, 110, 34, GoBack, "back", "back");
    static void Stretch(Control control) => control.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

    static SnowLabel Caption(Control parent, string text, int x, int y, int width, int height, float points = 10, bool bold = false, bool muted = false)
    {
        var label = new SnowLabel { Text = text, Bounds = new Rectangle(x, y, width, height), Secondary = muted, AutoEllipsis = true,
            Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", points) };
        parent.Controls.Add(label);
        return label;
    }

    static SnowButton Button(Control parent, string text, int x, int y, int width, int height, Action click, string glyph = "", string name = "")
    {
        var button = new SnowButton { Text = text, AccessibleName = text, Name = name, Glyph = glyph, Bounds = new Rectangle(x, y, width, height) };
        button.Click += (_, _) => click();
        parent.Controls.Add(button);
        return button;
    }

    static SnowButton[] Choices(Control parent, int x, int y, int width, int height, string[] labels, Action<int> click, string[]? icons = null, string[]? names = null)
    {
        var row = new TableLayoutPanel { Bounds = new Rectangle(x, y, width, height), ColumnCount = labels.Length, RowCount = 1, Margin = Padding.Empty };
        Stretch(row);
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        parent.Controls.Add(row);
        var options = new SnowButton[labels.Length];
        for (int i = 0; i < options.Length; i++)
        {
            int value = i;
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / labels.Length));
            var choice = new SnowButton { Text = labels[i], AccessibleName = labels[i], Name = names?[i] ?? "", Glyph = icons?[i] ?? "",
                Vertical = icons is not null, Choice = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, i == labels.Length - 1 ? 0 : 6, 0) };
            choice.Click += (_, _) => click(value);
            row.Controls.Add(choice, i, 0);
            options[i] = choice;
        }
        return options;
    }

    static void SelectOption(SnowButton[] options, int value)
    {
        for (int i = 0; i < options.Length; i++) options[i].Selected = i == value;
    }

    SnowSwitch Toggle(Control parent, string text, int y, Action<bool> change, int inset = 18)
        => ToggleAt(parent, Caption(parent, text, inset, y, parent.Width - 2 * inset - 56, 29), change, inset);

    SnowSwitch ToggleAt(Control parent, Label label, Action<bool> change, int inset = 18)
    {
        Stretch(label);
        var toggle = new SnowSwitch { Bounds = new Rectangle(parent.Width - inset - 42, label.Top, 42, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Right, AccessibleName = label.Text };
        toggle.CheckedChanged += value => { if (!syncing) change(value); };
        label.Click += (_, _) => { if (toggle.Enabled) { toggle.Focus(); toggle.Checked = !toggle.Checked; } };
        parent.Controls.Add(toggle);
        return toggle;
    }

    SnowSlider Slider(Control parent, string text, int y, int min, int max, int initial, Action<int> change, string unit = "%", string name = "")
    {
        Caption(parent, text, 18, y, parent.Width - 36, 21, 9, muted: true);
        var slider = new SnowSlider { Bounds = new Rectangle(18, y + 23, parent.Width - 36, 29), Minimum = min, Maximum = max,
            Value = initial, AccessibleName = text, Unit = unit, Name = name };
        Stretch(slider);
        slider.ValueChanged += value => { if (!syncing) change(value); };
        parent.Controls.Add(slider);
        return slider;
    }

    static ComboBox Combo(Control parent, string name, int x, int y)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(x, y, parent.Width - x * 2, 28), AccessibleName = name };
        Stretch(combo);
        parent.Controls.Add(combo);
        return combo;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(nint hWnd, int attribute, ref int value, int size);
}
