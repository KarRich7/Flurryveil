using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;

namespace QuietSnow;

/// <summary>Small desktop-only accents for the bundled wallpapers. The caller clips out open windows.</summary>
internal sealed class WallpaperEffects : IDisposable
{
    readonly Scene? house, porch;
    readonly Bitmap[][] lightSprites;
    readonly Bitmap[] smokeSprites;
    float time;
    bool disposed;
    int selectedScene, garlandMode = 1, treeMode = 2, speed = 100;

    public int SelectedScene { get => selectedScene; set => selectedScene = Math.Clamp(value, 0, 2); }
    /// <summary>0: steady, 1: twinkle, 2: travelling wave.</summary>
    public int GarlandMode { get => garlandMode; set => garlandMode = Math.Clamp(value, 0, 2); }
    /// <summary>0: steady, 1: twinkle, 2: travelling wave.</summary>
    public int TreeMode { get => treeMode; set => treeMode = Math.Clamp(value, 0, 2); }
    public int Speed { get => speed; set => speed = Math.Clamp(value, 25, 200); }
    public bool SmokeEnabled { get; set; } = true;

    public WallpaperEffects()
    {
        // Read geometry only once, never from the animation/rendering loop.
        (house, porch) = LoadScenes();
        Color[] colours = [Color.FromArgb(255, 213, 133), Color.FromArgb(255, 237, 196),
            Color.FromArgb(255, 139, 113), Color.FromArgb(151, 222, 255), Color.FromArgb(143, 235, 186)];
        lightSprites = colours.Select(colour => Enumerable.Range(0, 16)
            .Select(level => CreateGlow(colour, level / 15f, false)).ToArray()).ToArray();
        smokeSprites = Enumerable.Range(0, 16).Select(level => CreateGlow(
            Color.FromArgb(213, 223, 240), level / 15f, true)).ToArray();
    }

    public void Update(float dt)
    {
        if (disposed || !float.IsFinite(dt)) return;
        time += Math.Clamp(dt, 0, .15f) * speed / 100f;
        // Keep trigonometry precision stable after very long sessions.
        if (time > 36000) time -= 36000;
    }

    /// <summary>
    /// Uses physical screen coordinates. Preserve the caller's viewport translation and clip.
    /// Geometry follows Windows' Fill wallpaper transform, including portrait/ultrawide cropping.
    /// </summary>
    public void Paint(Graphics g, Rectangle monitorBounds)
    {
        if (disposed || selectedScene == 0 || monitorBounds.Width < 1 || monitorBounds.Height < 1) return;
        Scene? scene = selectedScene == 1 ? house : porch;
        if (scene is null) return;
        float scale = Math.Max(monitorBounds.Width / scene.SourceWidth, monitorBounds.Height / scene.SourceHeight);
        float imageWidth = scene.SourceWidth * scale, imageHeight = scene.SourceHeight * scale;
        float left = monitorBounds.Left + (monitorBounds.Width - imageWidth) * .5f;
        float top = monitorBounds.Top + (monitorBounds.Height - imageHeight) * .5f;
        PointF Map(PointF normalized) => new(left + normalized.X * imageWidth, top + normalized.Y * imageHeight);

        var saved = g.Save();
        try
        {
            g.SetClip(monitorBounds, CombineMode.Intersect);
            float diameter = Math.Clamp(17f * scale, 13, 28);
            for (int i = 0; i < scene.EaveLights.Length; i++)
            {
                PointF p = Map(scene.EaveLights[i]);
                float level = LightLevel(garlandMode, i, scene.EaveLights.Length, time);
                DrawLight(g, p, diameter, i % 4 == 0 ? 1 : 0, level);
            }
            for (int i = 0; i < scene.TreeLights.Length; i++)
            {
                PointF p = Map(scene.TreeLights[i]);
                float level = LightLevel(treeMode, i, scene.TreeLights.Length, time);
                // Soft warm light predominates, with a few coloured Christmas bulbs.
                int colour = i % 5 == 0 ? 2 + i % 3 : 0;
                DrawLight(g, p, diameter * .9f, colour, level);
            }
            if (SmokeEnabled && scene.SmokeOrigin is PointF origin)
            {
                PointF source = Map(origin);
                for (int i = 0; i < 12; i++)
                {
                    float age = (time * .115f + i / 12f) % 1f;
                    float fade = MathF.Sin(age * MathF.PI);
                    int level = Math.Clamp((int)MathF.Round(fade * 15), 0, 15);
                    if (level == 0) continue;
                    float size = (18 + age * 79) * scale;
                    float x = source.X + (age * 49 + MathF.Sin(age * 8 + i * .7f) * age * 9) * scale;
                    float y = source.Y - age * 133 * scale;
                    g.DrawImage(smokeSprites[level], x - size * .5f, y - size * .5f, size, size * .8f);
                }
            }
        }
        finally { g.Restore(saved); }
    }

    void DrawLight(Graphics g, PointF point, float diameter, int colour, float brightness)
    {
        int level = Math.Clamp((int)MathF.Round(brightness * 15), 0, 15);
        if (level == 0) return;
        g.DrawImage(lightSprites[colour][level], point.X - diameter * .5f,
            point.Y - diameter * .5f, diameter, diameter);
    }

    static float LightLevel(int mode, int index, int total, float t)
    {
        if (mode == 0) return .68f;
        if (mode == 2)
        {
            float wave = .5f + .5f * MathF.Sin(t * 1.7f - index / (float)Math.Max(1, total) * MathF.PI * 4);
            return .16f + .8f * wave * wave;
        }
        // Independent, slow, smooth twinkle: no abrupt flashing or whole-screen pulses.
        float twinkle = .5f + .5f * MathF.Sin(t * (1.15f + index % 7 * .11f) + index * 2.39996f);
        return .18f + .75f * twinkle * twinkle;
    }

    static Bitmap CreateGlow(Color colour, float intensity, bool smoke)
    {
        const int side = 48;
        var bitmap = new Bitmap(side, side, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var path = new GraphicsPath();
        path.AddEllipse(1, 1, side - 2, side - 2);
        using var gradient = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb((int)((smoke ? 18 : 95) * intensity), colour),
            SurroundColors = [Color.FromArgb(0, colour)]
        };
        g.FillPath(gradient, path);
        if (!smoke)
        {
            using var core = new SolidBrush(Color.FromArgb((int)(170 * intensity), Color.FromArgb(255, 244, 219)));
            g.FillEllipse(core, 21, 21, 6, 6);
        }
        return bitmap;
    }

    static (Scene?, Scene?) LoadScenes()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "wallpaper-points.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var size = root.GetProperty("size").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            if (size.Length != 2 || size[0] <= 0 || size[1] <= 0) return (null, null);
            return (ReadScene(root.GetProperty("house"), size[0], size[1]),
                ReadScene(root.GetProperty("porch"), size[0], size[1]));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException)
        { return (null, null); }
    }

    static Scene ReadScene(JsonElement element, float sourceWidth, float sourceHeight)
    {
        var eaves = new List<PointF>();
        foreach (var line in element.GetProperty("eavePolylines").EnumerateArray())
        {
            var points = line.EnumerateArray().Select(ReadPoint).ToArray();
            for (int i = 1; i < points.Length; i++)
            {
                PointF a = points[i - 1], b = points[i];
                float dx = (b.X - a.X) * sourceWidth, dy = (b.Y - a.Y) * sourceHeight;
                int count = Math.Max(1, (int)MathF.Round(MathF.Sqrt(dx * dx + dy * dy) / 29));
                for (int j = 0; j < count; j++)
                {
                    float t = j / (float)count;
                    eaves.Add(new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
                }
            }
        }
        PointF treeTop = ReadPoint(element.GetProperty("treeTop"));
        PointF treeBottom = ReadPoint(element.GetProperty("treeBottom"));
        float halfWidth = element.GetProperty("treeHalfWidthAtBottom").GetSingle();
        var tree = new List<PointF>();
        for (int row = 0; row < 9; row++)
        {
            float t = .12f + row * .095f;
            int count = 2 + row / 2;
            float half = halfWidth * t * .84f;
            for (int j = 0; j < count; j++)
            {
                float horizontal = (j + .25f + (row % 2) * .35f) / count;
                tree.Add(new PointF(treeTop.X + (treeBottom.X - treeTop.X) * t + (horizontal * 2 - 1) * half,
                    treeTop.Y + (treeBottom.Y - treeTop.Y) * t + MathF.Sin(horizontal * MathF.PI) * .008f));
            }
        }
        JsonElement smoke = element.GetProperty("smokeOrigin");
        return new Scene(sourceWidth, sourceHeight, eaves.ToArray(), tree.ToArray(),
            smoke.ValueKind == JsonValueKind.Null ? null : ReadPoint(smoke));
    }

    static PointF ReadPoint(JsonElement element)
    {
        var p = element.EnumerateArray().Select(v => v.GetSingle()).ToArray();
        if (p.Length != 2) throw new FormatException("Invalid wallpaper point.");
        return new PointF(p[0], p[1]);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var colour in lightSprites) foreach (var sprite in colour) sprite.Dispose();
        foreach (var sprite in smokeSprites) sprite.Dispose();
    }

    sealed record Scene(float SourceWidth, float SourceHeight, PointF[] EaveLights,
        PointF[] TreeLights, PointF? SmokeOrigin);
}
