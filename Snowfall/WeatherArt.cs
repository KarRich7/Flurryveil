using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace QuietSnow;

internal enum WeatherMode { Snow, Rain, AutumnLeaves, CherryBlossom }

// Original procedural artwork, shared by the GPU atlas and the GDI fallback.
internal sealed class WeatherArt : IDisposable
{
    internal const int Tile = 48;
    Bitmap? atlas;
    Bitmap? pileAtlas;
    readonly ImageAttributes attributes = new();
    readonly ColorMatrix alpha = new();
    float lastOpacity = -1;
    public Bitmap Atlas => atlas ??= CreateAtlas();

    static Bitmap CreateAtlas()
    {
        var bitmap = new Bitmap(Tile * 4, Tile * 2, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color[] leaves = [Color.FromArgb(226, 141, 40), Color.FromArgb(188, 64, 43),
            Color.FromArgb(235, 190, 64), Color.FromArgb(137, 155, 66)];
        for (int i = 0; i < 8; i++)
        {
            var state = g.Save(); g.TranslateTransform(i % 4 * Tile + 24, i / 4 * Tile + 24);
            using var path = new GraphicsPath();
            if (i < 4)
            {
                path.AddBezier(0, -20, 24, -11, 18, 13, 0, 18);
                path.AddBezier(0, 18, -18, 13, -24, -11, 0, -20);
            }
            else
            {
                path.AddBezier(0, -13, 10, -24, 22, -7, 0, 17);
                path.AddBezier(0, 17, -22, -7, -10, -24, 0, -13);
            }
            var color = i < 4 ? leaves[i] : Color.FromArgb(255, 156 + (i - 4) * 15, 186 + (i - 4) * 12);
            using var fill = new LinearGradientBrush(new Rectangle(-20, -21, 40, 42),
                ControlPaint.Light(color, .35f), ControlPaint.Dark(color, .12f), 35f);
            g.FillPath(fill, path);
            using var vein = new Pen(Color.FromArgb(i < 4 ? 120 : 65, i < 4 ? Color.FromArgb(95, 61, 26) : Color.White), .85f);
            g.DrawBezier(vein, 0, 19, -1, 7, 1, -3, 0, -15);
            if (i < 4)
            {
                g.DrawLine(vein, 0, 6, -9, -2); g.DrawLine(vein, 0, 1, 10, -8);
                g.DrawLine(vein, 0, -5, -7, -12);
            }
            g.Restore(state);
        }
        return bitmap;
    }

    public void Draw(Graphics g, float x, float y, float size, float angle, int variant, float opacity = 1)
    {
        var state = g.Save();
        g.TranslateTransform(x, y); g.RotateTransform(angle * 180 / MathF.PI);
        opacity = Math.Clamp(opacity, 0, 1);
        if (opacity != lastOpacity) { alpha.Matrix33 = opacity; attributes.SetColorMatrix(alpha); lastOpacity = opacity; }
        g.DrawImage(Atlas, new Rectangle((int)-size, (int)-size, Math.Max(1, (int)(size * 2)), Math.Max(1, (int)(size * 2))),
            variant % 4 * Tile, variant / 4 * Tile, Tile, Tile, GraphicsUnit.Pixel, attributes);
        g.Restore(state);
    }

    public void DrawPile(Graphics g, float x, float y, int variant, int angle)
    {
        if (pileAtlas is null)
        {
            pileAtlas = new Bitmap(32 * 16, 32 * 8, PixelFormat.Format32bppPArgb);
            using var cache = Graphics.FromImage(pileAtlas);
            cache.SmoothingMode = SmoothingMode.AntiAlias;
            for (int row = 0; row < 8; row++)
            for (int column = 0; column < 16; column++)
                Draw(cache, column * 32 + 16, row * 32 + 16, row < 4 ? 12 : 8,
                    column * MathF.Tau / 16, row);
        }
        g.DrawImage(pileAtlas, new Rectangle((int)x - 16, (int)y - 16, 32, 32),
            (angle & 15) * 32, variant * 32, 32, 32, GraphicsUnit.Pixel);
    }

    public void Dispose() { atlas?.Dispose(); atlas = null; pileAtlas?.Dispose(); pileAtlas = null; attributes.Dispose(); }
}
