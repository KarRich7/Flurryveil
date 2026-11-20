namespace QuietSnow;

internal static class TrayTheme
{
    public static void Apply(ContextMenuStrip menu, bool dark)
    {
        var palette = SettingsForm.InterfacePalette.Current(dark);
        menu.Renderer = new ToolStripProfessionalRenderer(new MenuColors(palette)) { RoundedEdges = false };
        ApplyColors(menu.Items);
        void ApplyColors(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                item.ForeColor = palette.Ink;
                if (item is ToolStripMenuItem { HasDropDownItems: true } group) ApplyColors(group.DropDownItems);
            }
        }
    }

    sealed class MenuColors(SettingsForm.InterfacePalette ui) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => ui.Surface;
        public override Color ImageMarginGradientBegin => ui.Surface;
        public override Color ImageMarginGradientMiddle => ui.Surface;
        public override Color ImageMarginGradientEnd => ui.Surface;
        public override Color MenuBorder => ui.Border;
        public override Color MenuItemBorder => ui.Border;
        public override Color MenuItemSelected => ui.Selection;
        public override Color MenuItemSelectedGradientBegin => ui.Selection;
        public override Color MenuItemSelectedGradientEnd => ui.Selection;
        public override Color MenuItemPressedGradientBegin => ui.Selection;
        public override Color MenuItemPressedGradientEnd => ui.Selection;
        public override Color CheckBackground => ui.Selection;
        public override Color CheckSelectedBackground => ui.Selection;
        public override Color SeparatorDark => ui.Border;
        public override Color SeparatorLight => ui.Surface;
    }
}
