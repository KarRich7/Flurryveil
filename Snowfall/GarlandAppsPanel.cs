namespace QuietSnow;

internal sealed class GarlandAppsPanel : SettingsForm.MenuCanvas
{
    readonly RadioButton all, selected;
    readonly CheckedListBox list;
    bool syncing;
    public event Action<int, string[]>? Changed;

    public GarlandAppsPanel()
    {
        Size = new Size(354, 422);
        Card = true;
        all = new RadioButton { Text = "Все приложения", Bounds = new Rectangle(20, 18, 310, 28), BackColor = Color.Transparent };
        selected = new RadioButton { Text = "Выбранные", Bounds = new Rectangle(20, 53, 310, 28), BackColor = Color.Transparent };
        list = new CheckedListBox { Bounds = new Rectangle(20, 92, 314, 205), BorderStyle = BorderStyle.None, CheckOnClick = true, IntegralHeight = false, BackColor = Color.White };
        var fileLabel = new SettingsForm.SnowLabel { Text = "Имя файла программы (.exe)", Bounds = new Rectangle(20, 300, 314, 22), ForeColor = SettingsForm.Ink, BackColor = Color.Transparent };
        var executable = new TextBox { PlaceholderText = "Имя файла, например chrome.exe", Bounds = new Rectangle(20, 324, 314, 26), AccessibleName = "Имя программы" };
        var add = new SettingsForm.SnowButton { Text = "Добавить программу", AccessibleName = "Добавить программу", Bounds = new Rectangle(20, 362, 314, 30) };
        var status = new SettingsForm.SnowLabel { Bounds = new Rectangle(20, 399, 314, 24), ForeColor = SettingsForm.Muted, BackColor = Color.Transparent };
        Controls.AddRange([all, selected, list, fileLabel, executable, add, status]);
        foreach (Control field in Controls) field.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        all.CheckedChanged += (_, _) => { list.Enabled = selected.Checked; if (all.Checked) Publish(); };
        selected.CheckedChanged += (_, _) => { list.Enabled = selected.Checked; if (selected.Checked) Publish(); };
        list.ItemCheck += (_, e) =>
        {
            if (syncing) return;
            var names = new List<string>();
            for (int i = 0; i < list.Items.Count; i++)
                if (i == e.Index ? e.NewValue == CheckState.Checked : list.GetItemChecked(i)) names.Add(((AppEntry)list.Items[i]).Name);
            Changed?.Invoke(all.Checked ? 1 : 0, names.ToArray());
        };
        add.Click += (_, _) => Add();
        executable.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Add(); } };
        void Add()
        {
            string name = SnowApp.NormalizeApplication(executable.Text);
            if (name.Length == 0)
            {
                status.Text = "Введите имя файла программы";
                executable.Focus();
                return;
            }
            int index = list.Items.Cast<AppEntry>().ToList().FindIndex(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) index = list.Items.Add(new AppEntry(name));
            selected.Checked = true;
            list.SetItemChecked(index, true);
            executable.Clear(); status.Text = "";
        }
    }
    public void SetState(int scope, string[] chosen, string[] available)
    {
        syncing = true;
        list.Items.Clear();
        foreach (string name in available.Concat(chosen).Append("explorer").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            list.Items.Add(new AppEntry(name), chosen.Contains(name, StringComparer.OrdinalIgnoreCase));
        all.Checked = scope == 1; selected.Checked = scope != 1; list.Enabled = selected.Checked;
        syncing = false;
    }
    void Publish()
    {
        if (!syncing) Changed?.Invoke(all.Checked ? 1 : 0, list.CheckedItems.Cast<AppEntry>().Select(item => item.Name).ToArray());
    }
    sealed record AppEntry(string Name)
    {
        public override string ToString() => Name.Equals("explorer", StringComparison.OrdinalIgnoreCase) ? "Проводник" : Name;
    }
}
