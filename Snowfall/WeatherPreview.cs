using System.Drawing.Drawing2D;

namespace QuietSnow;

internal sealed partial class SettingsForm
{
    sealed class WeatherPreview : Control
    {
        static readonly Font Title = new("Segoe UI Semibold", 28f);
        static readonly Font Status = new("Segoe UI", 9.5f);
        static readonly string[] Names = ["Снег", "Дождь", "Листопад", "Сакура"];
        static readonly Color Frost = Color.FromArgb(211, 236, 249);
        static readonly (float X, float Y, float Size)[] Particles = CreateParticles();
        WeatherMode mode;
        string status = "";
        bool paused;

        public WeatherPreview()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            AccessibleRole = AccessibleRole.Graphic;
        }

        public void SetState(WeatherMode mode, string status, bool paused)
        {
            if (this.mode == mode && this.status == status && this.paused == paused) return;
            this.mode = mode;
            this.status = status;
            this.paused = paused;
            AccessibleName = $"{Names[(int)mode]}. {status}";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 4 || Height < 4) return;
            var g = e.Graphics;
            float s = DeviceDpi / 96f;
            bool contrast = SystemInformation.HighContrast;
            Color sky = contrast ? SystemColors.Window : Color.FromArgb(17, 32, 49);
            Color text = contrast ? SystemColors.WindowText : Frost;
            Color weatherColor = mode switch
            {
                WeatherMode.Rain => Color.FromArgb(125, 196, 231),
                WeatherMode.AutumnLeaves => Color.FromArgb(242, 166, 106),
                WeatherMode.CherryBlossom => Color.FromArgb(239, 172, 200),
                _ => Frost
            };

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 12 * s);
            using var background = new SolidBrush(sky);
            g.FillPath(background, shape);
            if (contrast)
            {
                using var border = new Pen(text, s);
                g.DrawPath(border, shape);
            }

            if (!contrast && Width >= 385 * s)
            {
                var clip = g.Save();
                g.SetClip(shape);
                PaintLandscape(g, s, weatherColor);
                g.Restore(clip);
            }

            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            var labelWidth = Width < 385 * s ? Width - (int)(44 * s) : (int)(230 * s);
            TextRenderer.DrawText(g, Names[(int)mode], Title, new Rectangle((int)(24 * s), (int)(38 * s), labelWidth, (int)(58 * s)), text, flags);
            using var indicator = new SolidBrush(contrast ? text : paused ? Color.FromArgb(236, 191, 139) : Color.FromArgb(160, 218, 201));
            g.FillEllipse(indicator, 27 * s, 116 * s, 7 * s, 7 * s);
            TextRenderer.DrawText(g, status, Status, new Rectangle((int)(42 * s), (int)(106 * s), labelWidth, (int)(26 * s)), text, flags);
        }

        void PaintLandscape(Graphics g, float s, Color accent)
        {
            float start = Math.Max(235 * s, Width * .43f);
            var save = g.Save();
            g.TranslateTransform(start, 0);
            g.ScaleTransform((Width - start) / 410f, Height / 174f);
            using var moon = new SolidBrush(Color.FromArgb(84, accent));
            using var farRidge = new SolidBrush(Color.FromArgb(37, 63, 83));
            using var nearRidge = new SolidBrush(Color.FromArgb(28, 47, 67));
            using var snow = new SolidBrush(Color.FromArgb(134, 178, 208));
            using var horizon = new Pen(Color.FromArgb(55, 83, 105), 1);
            g.FillEllipse(moon, 277, 25, 55, 55);
            g.FillPolygon(farRidge, [new(0, 174), new(79, 123), new(135, 141), new(232, 51), new(295, 115), new(337, 90), new(410, 145), new(410, 174)]);
            g.FillPolygon(snow, [new(204, 77), new(232, 51), new(259, 79), new(246, 76), new(240, 84), new(226, 72), new(217, 81)]);
            g.FillPolygon(nearRidge, [new(20, 174), new(131, 145), new(171, 149), new(242, 119), new(269, 132), new(317, 128), new(410, 103), new(410, 174)]);
            g.DrawCurve(horizon, [new(31, 172), new(123, 157), new(198, 159), new(267, 144), new(342, 145), new(410, 127)]);
            g.DrawCurve(horizon, [new(104, 174), new(191, 166), new(237, 167), new(303, 155), new(410, 155)]);

            using var window = new SolidBrush(Color.FromArgb(174, 15, 31, 46));
            using var edge = new Pen(Color.FromArgb(150, accent), 1);
            using var cap = new Pen(Color.FromArgb(205, accent), 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var flake = new SolidBrush(Color.FromArgb(paused ? 70 : 212, accent));
            using var frame = Round(new RectangleF(92, 87, 121, 64), 6);
            g.FillPath(window, frame);
            g.DrawPath(edge, frame);
            g.DrawLine(edge, 93, 102, 212, 102);
            for (int i = 0; i < 3; i++) g.FillEllipse(flake, 101 + 7 * i, 93, 2.3f, 2.3f);
            if (mode != WeatherMode.Rain)
                g.DrawCurve(cap, [new(98, 86), new(117, 84), new(142, 86), new(167, 83), new(201, 85), new(209, 86)]);

            using var rain = new Pen(flake.Color, .9f);
            foreach (var (x, y, size) in Particles)
            {
                switch (mode)
                {
                    case WeatherMode.Rain: g.DrawLine(rain, x, y, x - 4, y + 7 + size); break;
                    case WeatherMode.AutumnLeaves: g.FillEllipse(flake, x, y, size * 2, size * .7f); break;
                    case WeatherMode.CherryBlossom: g.FillEllipse(flake, x, y, size * .9f, size * 1.4f); break;
                    default: g.FillEllipse(flake, x, y, size, size); break;
                }
            }
            g.Restore(save);
        }

        static (float, float, float)[] CreateParticles()
        {
            var random = new Random(632);
            return Enumerable.Range(0, 45).Select(_ =>
                (RandomCoordinate(25, 400), RandomCoordinate(10, 163), RandomCoordinate(.8f, 3.2f))).ToArray();
            float RandomCoordinate(float min, float max) => min + random.NextSingle() * (max - min);
        }
    }
}
