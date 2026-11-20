using System.Drawing.Drawing2D;

namespace QuietSnow;

/// <summary>A small vector cleaner travelling along the taskbar's snow line.</summary>
internal sealed class SnowCleaner
{
    const float VehicleWidth = 98, VehicleHeight = 54;
    readonly List<Track> tracks = [];
    int selectedKind;
    int intervalSeconds = 40;
    float time;
    public float PaintTime => selectedKind != 0 && tracks.Any(track => track.Active) ? time : 0;

    public int IntervalSeconds
    {
        get => intervalSeconds;
        set
        {
            int next = Math.Clamp(value, 5, 180);
            if (next == intervalSeconds) return;
            foreach (var track in tracks)
                if (!track.Active && track.HasPassed)
                    track.Cooldown = Math.Max(.1f, track.Cooldown + next - intervalSeconds);
            intervalSeconds = next;
        }
    }

    /// <summary>0 off, 1 bulldozer, 2 snow blower, 3 Santa's sleigh.</summary>
    public int SelectedKind
    {
        get => selectedKind;
        set
        {
            int next = Math.Clamp(value, 0, 3);
            if (next == selectedKind) return;
            selectedKind = next;
            foreach (var track in tracks)
            {
                track.Active = track.HasPassed = false;
                track.Cooldown = .35f;
                track.Pile = 0;
            }
        }
    }

    public void Update(float dt, Rectangle[] monitorWorkAreas, Func<RectangleF, float> scrapeSnow)
    {
        ArgumentNullException.ThrowIfNull(monitorWorkAreas);
        ArgumentNullException.ThrowIfNull(scrapeSnow);
        dt = Math.Clamp(dt, 0, .15f);
        time += dt;
        SynchronizeTracks(monitorWorkAreas);
        if (selectedKind == 0) return;
        foreach (var track in tracks)
        {
            float blade = selectedKind == 3 ? 43 : 46;
            if (!track.Active)
            {
                track.Cooldown -= dt;
                if (track.Cooldown > 0) continue;
                track.Position = track.Area.Left - blade;
                track.Pile = 0;
                track.Active = true;
            }
            float old = track.Position;
            float speed = selectedKind == 3 ? 104 : selectedKind == 2 ? 78 : 88;
            float end = track.Area.Right + VehicleWidth / 2;
            track.Position = Math.Min(end, track.Position + speed * dt);
            float left = Math.Max(track.Area.Left, old + blade);
            float right = Math.Min(track.Area.Right, track.Position + blade);
            float collected = right > left
                ? scrapeSnow(new RectangleF(left, track.Area.Bottom, right - left, 1))
                : 0;
            track.Pile = Math.Min(17, Math.Max(0, track.Pile - dt * 4) + collected * .14f);
            if (track.Position >= end)
            {
                track.Active = false;
                track.HasPassed = true;
                track.Cooldown = intervalSeconds;
            }
        }
    }

    void SynchronizeTracks(Rectangle[] areas)
    {
        int index = 0;
        bool unchanged = true;
        foreach (var area in areas)
        {
            if (area.Width < 130 || area.Height < 80) continue;
            if (index >= tracks.Count || tracks[index].Area != area) unchanged = false;
            index++;
        }
        if (unchanged && index == tracks.Count) return;
        tracks.Clear();
        foreach (var area in areas)
            if (area.Width >= 130 && area.Height >= 80) tracks.Add(new Track { Area = area, Cooldown = .35f + tracks.Count * .4f });
    }

    /// <summary>
    /// Uses desktop/world coordinates, like SnowApp.Paint after its viewport
    /// translation. The caller's transform and clipping are restored on return.
    /// </summary>
    public void CollectGpuBounds(List<RectangleF> bounds)
    {
        foreach (var track in tracks)
            if (selectedKind != 0 && track.Active)
                bounds.Add(new RectangleF(track.Position - VehicleWidth / 2 - 10, track.Area.Bottom - VehicleHeight - 5, VehicleWidth + 30 + track.Pile * 2.1f, VehicleHeight + 6));
    }

    public void Paint(Graphics g, Rectangle viewport)
    {
        if (selectedKind == 0) return;
        foreach (var track in tracks)
        {
            if (!track.Active || !track.Area.IntersectsWith(viewport)) continue;
            var state = g.Save();
            try
            {
                g.IntersectClip(track.Area);
                g.TranslateTransform(track.Position - VehicleWidth / 2, track.Area.Bottom - VehicleHeight);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using var shadow = new SolidBrush(Color.FromArgb(48, 14, 24, 35));
                g.FillEllipse(shadow, 6, 48, 87, 5);
                if (track.Pile > .4f) PaintPile(g, selectedKind == 3 ? 92 : 95, track.Pile);
                switch (selectedKind)
                {
                    case 1: PaintBulldozer(g); break;
                    case 2: PaintBlower(g); break;
                    case 3: PaintSanta(g); break;
                }
                if (track.Pile > 1) PaintSnowPuffs(g);
            }
            finally { g.Restore(state); }
        }
    }

    static void PaintPile(Graphics g, float blade, float height)
    {
        const float ground = VehicleHeight - .6f;
        float length = 12 + height * 2.1f;
        using var contour = new GraphicsPath();
        contour.AddBezier(blade - 1, ground - height, blade + length * .32f, ground - height * 1.08f,
            blade + length * .72f, ground - 2, blade + length, ground);
        contour.AddLine(blade + length, ground, blade - 1, ground);
        contour.CloseFigure();
        using var gradient = new LinearGradientBrush(new RectangleF(blade - 2, ground - height - 2, length + 4, height + 4),
            Color.White, Color.FromArgb(207, 229, 247), LinearGradientMode.Vertical);
        g.FillPath(gradient, contour);
    }

    void PaintBulldozer(Graphics g)
    {
        using var track = new SolidBrush(Color.FromArgb(38, 48, 60));
        using var wheel = new SolidBrush(Color.FromArgb(88, 101, 111));
        using var gold = new SolidBrush(Color.FromArgb(248, 187, 51));
        using var lightGold = new SolidBrush(Color.FromArgb(255, 218, 105));
        using var shadeGold = new SolidBrush(Color.FromArgb(205, 136, 32));
        using var glass = new SolidBrush(Color.FromArgb(80, 134, 164));
        using var shine = new Pen(Color.FromArgb(171, 224, 238), 1.3f);
        FillRound(g, track, 10, 36, 64, 14, 12);
        for (int i = 0; i < 5; i++)
        {
            float x = 18 + i * 12;
            g.FillEllipse(wheel, x - 4, 39, 8, 8);
            g.FillEllipse(track, x - 1, 42, 2, 2);
        }
        FillRound(g, shadeGold, 14, 27, 59, 12, 4);
        FillRound(g, gold, 15, 23, 53, 13, 5);
        FillRound(g, gold, 28, 6, 28, 27, 5);
        FillRound(g, glass, 31, 9, 22, 17, 3);
        g.DrawLine(shine, 34, 12, 41, 12);
        g.DrawLine(shine, 34, 13, 34, 18);
        g.FillRectangle(lightGold, 25, 5, 35, 4);
        g.FillRectangle(track, 61, 13, 4, 13);
        g.FillEllipse(lightGold, 65, 24, 6, 5);
        using var arm = new Pen(Color.FromArgb(238, 167, 40), 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(arm, [new PointF(59, 30), new PointF(76, 39), new PointF(86, 35)]);
        g.FillPolygon(shadeGold, [new PointF(85, 28), new PointF(96, 25), new PointF(95, 49), new PointF(81, 47)]);
        g.FillPolygon(lightGold, [new PointF(88, 28), new PointF(96, 25), new PointF(95, 46), new PointF(86, 45)]);
        using var blade = new SolidBrush(Color.FromArgb(228, 241, 252));
        g.FillPolygon(blade, [new PointF(91, 26), new PointF(96, 24), new PointF(95, 51), new PointF(88, 51)]);
        g.FillRectangle(shadeGold, 20, 26, 10, 2);
    }

    void PaintBlower(Graphics g)
    {
        using var tire = new SolidBrush(Color.FromArgb(36, 45, 58));
        using var hub = new SolidBrush(Color.FromArgb(154, 171, 184));
        using var red = new SolidBrush(Color.FromArgb(216, 66, 72));
        using var redLight = new SolidBrush(Color.FromArgb(249, 105, 106));
        using var blue = new SolidBrush(Color.FromArgb(83, 136, 169));
        using var metal = new Pen(Color.FromArgb(166, 190, 202), 2);
        g.FillEllipse(tire, 19, 33, 19, 19); g.FillEllipse(tire, 52, 35, 17, 17);
        g.FillEllipse(hub, 25, 39, 7, 7); g.FillEllipse(hub, 57, 40, 7, 7);
        FillRound(g, red, 20, 25, 51, 15, 5);
        FillRound(g, redLight, 31, 8, 28, 25, 4);
        FillRound(g, blue, 34, 11, 22, 16, 3);
        g.FillRectangle(hub, 29, 6, 33, 4);
        g.DrawLine(metal, 24, 28, 15, 17); g.DrawLine(metal, 15, 17, 9, 17);
        g.FillPolygon(red, [new PointF(63, 22), new PointF(66, 9), new PointF(82, 6), new PointF(85, 12), new PointF(72, 15), new PointF(71, 28)]);
        FillRound(g, red, 72, 31, 23, 18, 6);
        using var auger = new Pen(Color.FromArgb(239, 222, 211), 1.5f);
        for (int i = 0; i < 3; i++) g.DrawArc(auger, 76 + i * 5, 33, 7, 13, -70, 150);
        using var snow = new SolidBrush(Color.FromArgb(225, 245, 251, 255));
        for (int i = 0; i < 6; i++)
        {
            float p = (time * 1.8f + i / 6f) % 1;
            g.FillEllipse(snow, 82 + p * 13, 7 - MathF.Sin(p * MathF.PI) * 6 + p * 20, 2.3f, 2.3f);
        }
    }

    void PaintSanta(Graphics g)
    {
        using var red = new SolidBrush(Color.FromArgb(202, 49, 66));
        using var bright = new SolidBrush(Color.FromArgb(240, 82, 91));
        using var white = new SolidBrush(Color.FromArgb(248, 250, 246));
        using var skin = new SolidBrush(Color.FromArgb(248, 194, 151));
        using var sack = new SolidBrush(Color.FromArgb(62, 125, 99));
        using var boot = new SolidBrush(Color.FromArgb(40, 47, 61));
        using var gold = new Pen(Color.FromArgb(246, 204, 112), 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawBezier(gold, 13, 48, 30, 52, 78, 53, 87, 46);
        g.DrawArc(gold, 82, 38, 10, 10, -90, 180);
        g.DrawLine(gold, 28, 42, 25, 49); g.DrawLine(gold, 67, 42, 70, 50);
        g.FillEllipse(sack, 21, 14, 27, 25);
        g.FillPolygon(sack, [new PointF(29, 17), new PointF(28, 9), new PointF(40, 11), new PointF(36, 19)]);
        FillRound(g, red, 43, 22, 23, 19, 7);
        g.FillEllipse(skin, 49, 10, 16, 16);
        g.FillPolygon(bright, [new PointF(48, 13), new PointF(57, 2), new PointF(68, 14)]);
        FillRound(g, white, 47, 12, 20, 4, 3);
        g.FillEllipse(white, 65, 11, 6, 6);
        g.FillEllipse(white, 49, 19, 17, 13);
        g.FillEllipse(skin, 58, 18, 5, 5);
        g.FillEllipse(boot, 57, 17, 1.8f, 1.8f);
        g.FillRectangle(boot, 53, 32, 19, 4);
        g.FillPolygon(bright, [new PointF(14, 29), new PointF(21, 31), new PointF(23, 36), new PointF(73, 36), new PointF(80, 27), new PointF(82, 42), new PointF(23, 44)]);
        g.DrawLine(gold, 23, 43, 80, 42);
        using var magic = new Pen(Color.FromArgb(255, 224, 151), 1.2f);
        float sparkle = MathF.Sin(time * 3) * 2;
        g.DrawLine(magic, 88, 23 + sparkle, 88, 31 + sparkle);
        g.DrawLine(magic, 84, 27 + sparkle, 92, 27 + sparkle);
    }

    void PaintSnowPuffs(Graphics g)
    {
        using var snow = new SolidBrush(Color.FromArgb(235, 243, 251, 255));
        for (int i = 0; i < 4; i++)
        {
            float phase = (time * 2 + i * .25f) % 1;
            float r = 2 + (1 - phase) * 2;
            g.FillEllipse(snow, 91 - phase * 9, 45 - MathF.Sin(phase * MathF.PI) * 10, r, r);
        }
    }

    static void FillRound(Graphics g, Brush brush, float x, float y, float w, float h, float radius)
    {
        using var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(w, h));
        path.AddArc(x, y, d, d, 180, 90); path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90); path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure(); g.FillPath(brush, path);
    }

    sealed class Track
    {
        public Rectangle Area;
        public float Position, Cooldown, Pile;
        public bool Active, HasPassed;
    }
}
