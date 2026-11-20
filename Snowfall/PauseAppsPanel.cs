namespace QuietSnow;

internal sealed class PauseAppsPanel : SettingsForm.MenuCanvas
{
    readonly CheckedListBox list;
    readonly TextBox search, executable;
    readonly Label status;
    readonly Label empty;
    readonly HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> available = [];
    bool syncing;
    public event Action<string[]>? Changed;
    public Func<string[]>? RunningApplications;

    public PauseAppsPanel()
    {
        Size = new Size(354, 506);
        Card = true;
        var searchLabel = new SettingsForm.SnowLabel { Text = "Открытые приложения", Bounds = new Rectangle(20, 16, 205, 22), ForeColor = SettingsForm.Ink };
        var refresh = new SettingsForm.SnowButton { Text = "Обновить", Bounds = new Rectangle(242, 12, 92, 27), Subtle = true, AccessibleName = "Обновить список приложений" };
        search = new TextBox { PlaceholderText = "Найти приложение…", Bounds = new Rectangle(20, 44, 314, 26), AccessibleName = "Поиск приложений для автопаузы" };
        list = new CheckedListBox { Bounds = new Rectangle(20, 86, 314, 184), BorderStyle = BorderStyle.None,
            IntegralHeight = false, CheckOnClick = true, BackColor = SettingsForm.Tile, AccessibleName = "Приложения для автопаузы" };
        var hint = new SettingsForm.SnowLabel { Text = "Эффекты скрываются, пока окно отмеченной\nпрограммы активно, и возвращаются после.",
            Bounds = new Rectangle(20, 278, 314, 43), ForeColor = SettingsForm.Muted };
        var nameLabel = new SettingsForm.SnowLabel { Text = "Добавить программу по имени", Bounds = new Rectangle(20, 326, 314, 22), ForeColor = SettingsForm.Ink };
        executable = new TextBox { PlaceholderText = "Имя .exe, например chrome.exe", Bounds = new Rectangle(20, 350, 214, 26), AccessibleName = "Имя приложения для автопаузы" };
        var add = new SettingsForm.SnowButton { Text = "Добавить", Bounds = new Rectangle(242, 347, 92, 32), AccessibleName = "Добавить в автопаузу" };
        var browse = new SettingsForm.SnowButton { Text = "Выбрать файл приложения…", Bounds = new Rectangle(20, 394, 314, 34), AccessibleName = "Выбрать EXE для автопаузы" };
        var clear = new SettingsForm.SnowButton { Text = "Снять все отметки", Bounds = new Rectangle(20, 440, 314, 30), Subtle = true };
        status = new SettingsForm.SnowLabel { Bounds = new Rectangle(20, 480, 314, 23), ForeColor = SettingsForm.Muted };
        empty = new SettingsForm.SnowLabel { Text = "Откройте нужную программу\nи нажмите «Обновить».", Bounds = new Rectangle(35, 130, 284, 58),
            ForeColor = SettingsForm.Muted, TextAlign = ContentAlignment.MiddleCenter };
        Controls.AddRange([searchLabel, refresh, search, list, hint, nameLabel, executable, add, browse, clear, status, empty]);
        foreach (Control field in new Control[] { search, list, executable, browse, clear, status, empty })
            field.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        refresh.Anchor = add.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        empty.BringToFront();
        hint.Text = "Эффекты скрыты, пока отмеченная программа активна.";
        hint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        refresh.Click += (_, _) => { available.Clear(); available.AddRange((RunningApplications?.Invoke() ?? []).Concat(selected).Distinct(StringComparer.OrdinalIgnoreCase)); Rebuild(); };
        search.TextChanged += (_, _) => Rebuild();
        list.ItemCheck += (_, e) =>
        {
            if (syncing) return;
            var name = ((Entry)list.Items[e.Index]).Name;
            if (e.NewValue == CheckState.Checked) selected.Add(name); else selected.Remove(name);
            Publish();
        };
        add.Click += (_, _) => Add(executable.Text);
        executable.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Add(executable.Text); } };
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Title = "Выберите приложение для автопаузы", Filter = "Приложения Windows (*.exe)|*.exe", CheckFileExists = true };
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK) Add(dialog.FileName);
        };
        clear.Click += (_, _) => { selected.Clear(); Rebuild(); Publish(); };
    }

    public void SetState(string[] names, string[] running)
    {
        selected.Clear(); selected.UnionWith(names);
        available.Clear(); available.AddRange(running.Concat(names).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name));
        Rebuild();
    }

    void Add(string value)
    {
        string name = SnowApp.NormalizeApplication(value);
        if (name.Length == 0) { status.Text = "Введите имя программы или выберите .exe"; return; }
        if (!available.Contains(name, StringComparer.OrdinalIgnoreCase)) available.Add(name);
        selected.Add(name); executable.Clear(); search.Clear(); Rebuild(); Publish();
    }

    void Rebuild()
    {
        syncing = true; list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (var name in available.OrderBy(name => name))
            {
                var entry = new Entry(name);
                if (entry.ToString().Contains(search.Text, StringComparison.OrdinalIgnoreCase)) list.Items.Add(entry, selected.Contains(name));
            }
        }
        finally { list.EndUpdate(); syncing = false; }
        empty.Text = search.Text.Length == 0 ? "Откройте нужную программу\nи нажмите «Обновить»." : "Совпадений нет";
        empty.Visible = list.Items.Count == 0;
        status.Text = $"Выбрано: {selected.Count}";
    }
    void Publish() { status.Text = $"Выбрано: {selected.Count}"; Changed?.Invoke(selected.OrderBy(name => name).ToArray()); }
    sealed record Entry(string Name)
    {
        public override string ToString() => Name.ToLowerInvariant() switch {
            "chrome" => "Google Chrome · chrome.exe", "msedge" => "Microsoft Edge · msedge.exe",
            "firefox" => "Firefox · firefox.exe", "explorer" => "Проводник · explorer.exe", _ => Name + ".exe" };
    }
}
