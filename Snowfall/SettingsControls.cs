using System.Drawing.Drawing2D;

namespace QuietSnow;

internal sealed partial class SettingsForm
{
    internal static readonly Color Ink = Color.FromArgb(16, 29, 43);
    internal static readonly Color Muted = Color.FromArgb(75, 96, 113);
    internal static readonly Color Tile = Color.White;

    internal sealed record InterfacePalette(Color Canvas, Color Sidebar, Color Surface, Color Input,
        Color Ink, Color Muted, Color Border, Color Accent, Color AccentText, Color Selection, Color Hover)
    {
        internal static readonly InterfacePalette Light = new(
            Color.FromArgb(242, 247, 250), Color.FromArgb(228, 239, 246), Tile, Color.FromArgb(246, 249, 252),
            SettingsForm.Ink, SettingsForm.Muted, Color.FromArgb(181, 199, 213), Color.FromArgb(31, 86, 120),
            Color.White, Color.FromArgb(209, 231, 243), Color.FromArgb(225, 237, 245));
        internal static readonly InterfacePalette Dark = new(
            Color.FromArgb(15, 26, 38), Color.FromArgb(20, 34, 48), Color.FromArgb(24, 39, 54), Color.FromArgb(18, 31, 44),
            Color.FromArgb(234, 243, 250), Color.FromArgb(163, 186, 205), Color.FromArgb(67, 92, 115), Color.FromArgb(171, 219, 246),
            Color.FromArgb(14, 33, 47), Color.FromArgb(40, 66, 87), Color.FromArgb(36, 57, 76));

        internal static InterfacePalette Current(bool dark) => SystemInformation.HighContrast
            ? new(SystemColors.Window, SystemColors.Control, SystemColors.Window, SystemColors.Window,
                SystemColors.WindowText, SystemColors.WindowText, SystemColors.WindowText, SystemColors.Highlight,
                SystemColors.HighlightText, SystemColors.Window, SystemColors.Control)
            : dark ? Dark : Light;
    }

    internal InterfacePalette Palette => InterfacePalette.Current(dark);
    static InterfacePalette Colors(Control control) => (control.FindForm() as SettingsForm)?.Palette ?? InterfacePalette.Current(false);

    internal sealed class SnowLabel : Label
    {
        public bool Secondary;
        public SnowLabel()
        {
            TextAlign = ContentAlignment.MiddleLeft;
            UseMnemonic = false;
            Margin = Padding.Empty;
        }
    }

    internal class MenuCanvas : Panel
    {
        public bool Card;
        public MenuCanvas() { DoubleBuffered = true; TabStop = false; }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!Card || Width < 2 || Height < 2) return;
            var ui = Colors(this);
            using var pen = new Pen(ui.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    internal sealed class SnowButton : Control
    {
        public string Glyph = "";
        public bool Vertical, Subtle, Choice, Primary, Nav, IconOnly;
        readonly GlassMotion hoverMotion;
        bool pressed, selected;

        public SnowButton()
        {
            hoverMotion = new GlassMotion(this);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.ResizeRedraw, true);
            Font = new Font("Segoe UI Semibold", 9f);
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Selected
        {
            get => selected;
            set
            {
                if (selected == value) return;
                selected = value;
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            }
        }

        public void PerformClick()
        {
            if (!IsHandleCreated || IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(PerformClick); return; }
            if (Enabled && Visible) OnClick(EventArgs.Empty);
        }

        protected override AccessibleObject CreateAccessibilityInstance() => new ButtonAccessibility(this);
        sealed class ButtonAccessibility(SnowButton owner) : ControlAccessibleObject(owner)
        {
            public override string DefaultAction => "Нажать";
            public override AccessibleStates State => base.State | (owner.Selected ? AccessibleStates.Selected : AccessibleStates.None);
            public override void DoDefaultAction() => owner.PerformClick();
        }

        protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Space or Keys.Enter || base.IsInputKey(keyData);
        protected override void OnKeyPress(KeyPressEventArgs e) { if (e.KeyChar is ' ' or '\r') e.Handled = true; base.OnKeyPress(e); }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Space or Keys.Enter) { e.Handled = true; PerformClick(); }
            base.OnKeyUp(e);
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hoverMotion.To(1); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); pressed = false; hoverMotion.To(0); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Focus(); pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 2 || Height <= 2) return;
            var g = e.Graphics;
            var ui = Colors(this);
            float s = DeviceDpi / 96f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(.75f, .75f, Width - 1.5f, Height - 1.5f);
            using var shape = Round(rect, 7 * s);

            var fill = Primary ? ui.Accent : Selected ? ui.Selection : Nav || Subtle ? BackColor : ui.Surface;
            var ink = !Enabled ? ui.Muted : Primary ? ui.AccentText : Selected ? ui.Accent : ui.Ink;
            if (Enabled)
                fill = GlassTheme.Mix(fill, Primary ? ui.Ink : ui.Hover, pressed ? .65f : .65f * hoverMotion.Value);
            else fill = BackColor;
            using var brush = new SolidBrush(fill);
            using var line = new Pen(Selected || Focused ? ui.Accent : ui.Border, Focused ? 2 * s : s);
            g.FillPath(brush, shape);
            if ((Choice || !Nav && !Subtle) && !Primary || Selected || Focused) g.DrawPath(line, shape);

            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            if (IconOnly || Vertical)
            {
                float icon = (Vertical ? 23 : 19) * s;
                DrawGlyph(g, Glyph, new RectangleF((Width - icon) / 2f, Vertical ? 12 * s : (Height - icon) / 2, icon, icon), ink);
                if (Vertical) TextRenderer.DrawText(g, Text, Font, new Rectangle(2, (int)(39 * s), Width - 4, (int)(23 * s)), ink, flags | TextFormatFlags.HorizontalCenter);
                return;
            }

            int left = (int)((Choice ? 5 : 12) * s), right = Width - left;
            float glyphSize = 17 * s;
            if (Glyph.Length > 0)
            {
                if (!Nav)
                {
                    int desired = TextRenderer.MeasureText(Text, Font).Width + (int)(glyphSize + 5 * s);
                    left = Math.Max(left, (Width - desired) / 2);
                }
                DrawGlyph(g, Glyph, new RectangleF(left, (Height - glyphSize) / 2, glyphSize, glyphSize), ink);
                left += (int)(glyphSize + 8 * s);
            }
            else flags |= TextFormatFlags.HorizontalCenter;
            TextRenderer.DrawText(g, Text, Font, new Rectangle(left, 0, Math.Max(1, right - left), Height), ink, flags);
        }
    }

    internal sealed class SnowSwitch : Control
    {
        bool value;
        readonly GlassMotion toggleMotion;
        public event Action<bool>? CheckedChanged;

        public SnowSwitch()
        {
            toggleMotion = new GlassMotion(this);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.CheckButton;
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Checked
        {
            get => value;
            set
            {
                if (this.value == value) return;
                this.value = value;
                toggleMotion.To(value ? 1 : 0);
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
                CheckedChanged?.Invoke(value);
            }
        }

        protected override AccessibleObject CreateAccessibilityInstance() => new SwitchAccessibility(this);
        sealed class SwitchAccessibility(SnowSwitch owner) : ControlAccessibleObject(owner)
        {
            public override AccessibleStates State => base.State | (owner.Checked ? AccessibleStates.Checked : AccessibleStates.None);
            public override string DefaultAction => "Переключить";
            public override void DoDefaultAction()
            {
                if (owner.IsDisposed || !owner.Enabled) return;
                if (owner.InvokeRequired) { owner.BeginInvoke(DoDefaultAction); return; }
                owner.Checked = !owner.Checked;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) Focus(); base.OnMouseDown(e); }
        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
        protected override void OnKeyPress(KeyPressEventArgs e) { if (e.KeyChar == ' ') e.Handled = true; base.OnKeyPress(e); }
        protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; } base.OnKeyUp(e); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 5 || Height < 5) return;
            var ui = Colors(this);
            float s = DeviceDpi / 96f;
            var box = new RectangleF(1, 1, Width - 2, Height - 2);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Round(box, box.Height / 2);
            var fill = Enabled ? GlassTheme.Mix(ui.Input, ui.Accent, toggleMotion.Value) : ui.Surface;
            using var brush = new SolidBrush(fill);
            using var outline = new Pen(Focused ? ui.Accent : ui.Muted, (Focused ? 2 : 1) * s);
            using var handle = new SolidBrush(Enabled && Checked ? ui.AccentText : ui.Muted);
            g.FillPath(brush, path);
            g.DrawPath(outline, path);
            float diameter = Height - 9 * s;
            float left = 4.5f * s + (Width - diameter - 9 * s) * toggleMotion.Value;
            g.FillEllipse(handle, left, (Height - diameter) / 2, diameter, diameter);
        }
    }

    internal sealed class SnowSlider : Control
    {
        int value = 100;
        public int Minimum = 25, Maximum = 200;
        public string Unit = "%";
        public event Action<int>? ValueChanged;

        public SnowSlider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.Slider;
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Value
        {
            get => value;
            set
            {
                int next = Math.Clamp(value, Minimum, Maximum);
                if (this.value == next) return;
                this.value = next;
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
                ValueChanged?.Invoke(next);
            }
        }

        protected override AccessibleObject CreateAccessibilityInstance() => new SliderAccessibility(this);
        sealed class SliderAccessibility(SnowSlider owner) : ControlAccessibleObject(owner)
        {
            public override string? Value
            {
                get => owner.Value.ToString();
                set
                {
                    if (owner.IsDisposed || !owner.Enabled || !int.TryParse(value, out int next)) return;
                    if (owner.InvokeRequired) owner.BeginInvoke(() => owner.Value = next);
                    else owner.Value = next;
                }
            }
        }

        float DpiScale => DeviceDpi / 96f;
        float LeftTrack => 8 * DpiScale;
        float TrackWidth => Math.Max(1, Width - 68 * DpiScale);
        void UpdateAt(int x) => Value = Minimum + (int)Math.Round(Math.Clamp((x - LeftTrack) / TrackWidth, 0, 1) * (Maximum - Minimum));
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Focus(); Capture = true; UpdateAt(e.X); } base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (Capture) UpdateAt(e.X); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e) { Capture = false; base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is
            Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Down: Value -= 5; break;
                case Keys.Right: case Keys.Up: Value += 5; break;
                case Keys.Home: Value = Minimum; break;
                case Keys.End: Value = Maximum; break;
                default: base.OnKeyDown(e); return;
            }
            e.Handled = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var ui = Colors(this);
            float s = DpiScale;
            float y = Height / 2f;
            float x = LeftTrack + TrackWidth * (Value - Minimum) / Math.Max(1, Maximum - Minimum);
            Color accent = Enabled ? ui.Accent : ui.Muted;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var track = new Pen(ui.Border, 3 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var progress = new Pen(accent, 3 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var thumb = new SolidBrush(accent);
            using var focus = new Pen(ui.Accent, s);
            g.DrawLine(track, LeftTrack, y, LeftTrack + TrackWidth, y);
            g.DrawLine(progress, LeftTrack, y, x, y);
            g.FillEllipse(thumb, x - 6 * s, y - 6 * s, 12 * s, 12 * s);
            if (Focused) g.DrawEllipse(focus, x - 9 * s, y - 9 * s, 18 * s, 18 * s);
            TextRenderer.DrawText(g, $"{Value}{Unit}", Font, new Rectangle(Width - (int)(54 * s), 0, (int)(54 * s), Height),
                ui.Ink, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    static GraphicsPath Round(RectangleF box, float radius)
    {
        var shape = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(box.Width, box.Height));
        shape.AddArc(box.Left, box.Top, d, d, 180, 90);
        shape.AddArc(box.Right - d, box.Top, d, d, 270, 90);
        shape.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
        shape.AddArc(box.Left, box.Bottom - d, d, d, 90, 90);
        shape.CloseFigure();
        return shape;
    }

    static void DrawGlyph(Graphics g, string glyph, RectangleF box, Color color)
    {
        if (glyph.Length == 0) return;
        var saved = g.Save();
        g.TranslateTransform(box.X, box.Y);
        g.ScaleTransform(box.Width / 24, box.Height / 24);
        using var p = new Pen(color, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        void Lines(params PointF[] points) => g.DrawLines(p, points);
        void Cloud()
        {
            g.DrawArc(p, 2, 7, 11, 10, 75, 197);
            g.DrawArc(p, 8, 3, 11, 13, 200, 171);
            g.DrawArc(p, 15, 8, 7, 9, 275, 175);
            g.DrawLine(p, 6, 17, 19, 17);
        }
        switch (glyph)
        {
            case "cloud":
            case "rain":
                Cloud();
                if (glyph == "rain") for (int x = 7; x <= 17; x += 5) g.DrawLine(p, x, 20, x - 1, 23);
                break;
            case "snow":
                for (int i = 0; i < 3; i++)
                {
                    var t = g.Save();
                    g.TranslateTransform(12, 12);
                    g.RotateTransform(i * 60);
                    g.DrawLine(p, -10, 0, 10, 0);
                    Lines(new(-7, -3), new(-4, 0), new(-7, 3));
                    Lines(new(7, -3), new(4, 0), new(7, 3));
                    g.Restore(t);
                }
                break;
            case "sparkles":
                Lines(new(3, 12), new(8, 10), new(10, 4), new(12, 10), new(17, 12), new(12, 14), new(10, 20), new(8, 14), new(3, 12));
                g.DrawLine(p, 18, 2, 18, 8); g.DrawLine(p, 15, 5, 21, 5);
                break;
            case "gauge":
                g.DrawArc(p, 3, 5, 18, 16, 156, 229); g.DrawLine(p, 12, 14, 17, 10); g.DrawLine(p, 5, 17, 19, 17);
                break;
            case "sliders":
                for (int y = 5; y < 21; y += 7)
                {
                    int x = y == 12 ? 16 : 8;
                    g.DrawLine(p, 3, y, x - 3, y);
                    g.DrawLine(p, x + 3, y, 21, y);
                    g.DrawEllipse(p, x - 2, y - 2, 4, 4);
                }
                break;
            case "leaf":
                g.DrawBezier(p, 4, 19, 3, 5, 13, 5, 20, 3);
                g.DrawBezier(p, 20, 3, 21, 11, 16, 21, 4, 19);
                g.DrawLine(p, 3, 22, 15, 10);
                break;
            case "flower":
                for (int i = 0; i < 5; i++)
                {
                    var t = g.Save();
                    g.TranslateTransform(12, 12);
                    g.RotateTransform(i * 72);
                    g.DrawEllipse(p, -2.5f, -10, 5, 7);
                    g.Restore(t);
                }
                g.DrawEllipse(p, 10, 10, 4, 4); break;
            case "pause": g.DrawLine(p, 9, 5, 9, 19); g.DrawLine(p, 15, 5, 15, 19); break;
            case "play": Lines(new(8, 5), new(19, 12), new(8, 19), new(8, 5)); break;
            case "back": Lines(new(12, 5), new(5, 12), new(12, 19)); g.DrawLine(p, 5, 12, 20, 12); break;
            case "apps":
                for (int row = 0; row < 2; row++) for (int col = 0; col < 2; col++) g.DrawRectangle(p, 4 + col * 9, 4 + row * 9, 6, 6);
                break;
            case "shovel": Lines(new(5, 3), new(10, 3), new(8, 7), new(5, 3)); g.DrawLine(p, 8, 7, 15, 15); Lines(new(15, 12), new(21, 16), new(17, 22), new(12, 18), new(15, 12)); break;
            case "help":
                g.DrawEllipse(p, 3, 3, 18, 18); g.DrawArc(p, 9, 7, 6, 5, 175, 230);
                g.DrawLine(p, 12, 12, 12, 14); g.DrawLine(p, 12, 17, 12, 17.1f); break;
            case "exit": Lines(new(10, 4), new(4, 4), new(4, 20), new(10, 20)); g.DrawLine(p, 9, 12, 21, 12); Lines(new(16, 7), new(21, 12), new(16, 17)); break;
        }
        g.Restore(saved);
    }
}
