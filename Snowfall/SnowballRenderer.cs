using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace QuietSnow;

// Each of four small sprites is built on first use, then reused by every ball.
internal sealed class SnowballRenderer : IDisposable
{
    readonly Bitmap?[] sprites = new Bitmap?[4];
    static Bitmap Build(int seed)
    {
        const int size = 144;
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        var random = new Random(9281 + seed);
        float phase = random.NextSingle() * MathF.Tau;
        var pixels = new int[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float nx = (x + .5f - 72) / 68, ny = (y + .5f - 72) / 68;
            float angle = MathF.Atan2(ny, nx);
            float edge = .986f + .009f * MathF.Sin(angle * 9 + phase) + .005f * MathF.Cos(angle * 15 - phase);
            float distance = MathF.Sqrt(nx * nx + ny * ny);
            int alpha = (int)(255 * Math.Clamp((edge - distance) * 68 + .5f, 0, 1));
            if (alpha == 0) continue;
            float depth = MathF.Sqrt(Math.Max(0, 1 - distance * distance));
            float light = Math.Clamp(nx * .49f - ny * .51f + depth * .71f, 0, 1);
            float grain = (random.NextSingle() - .5f) * 13 + MathF.Sin(x * .27f + MathF.Sin(y * .21f + phase) * 2.5f) * MathF.Cos(y * .38f - x * .18f + phase) * 3.2f;
            if (random.NextSingle() > .992f) grain += 19;
            float rim = (1 - depth) * Math.Max(0, -nx) * 13;
            int red = Premultiply(163 + light * 93 - rim + grain, alpha);
            int green = Premultiply(185 + light * 68 + grain, alpha);
            int blue = Premultiply(204 + light * 44 + rim + grain, alpha);
            pixels[y * size + x] = alpha << 24 | red << 16 | green << 8 | blue;
        }
        var data = bitmap.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try { System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
        finally { bitmap.UnlockBits(data); }
        return bitmap;

        static int Premultiply(float channel, int alpha) => (int)(Math.Clamp(channel, 0, 255) * alpha / 255);
    }

    public void Paint(Graphics g, Ball ball)
    {
        if (!ball.Held && ball.Vy == 0)
        {
            using var shadow = new SolidBrush(Color.FromArgb(35, 41, 64, 84));
            g.FillEllipse(shadow, ball.X - ball.R * .7f, ball.Y + ball.R - 2, ball.R * 1.4f, Math.Max(2, ball.R * .16f));
        }
        int variant = (int)(ball.R * 3) % sprites.Length;
        var sprite = sprites[variant] ??= Build(variant);
        // The sprite's 68px radius maps exactly onto the physical ball radius.
        float diameter = ball.R * 144 / 68;
        g.DrawImage(sprite, ball.X - diameter / 2, ball.Y - diameter / 2, diameter, diameter);
        using var grains = new SolidBrush(Color.FromArgb(52, 135, 164, 184));
        for (int i = 0; i < 12; i++)
        {
            float angle = i * 2.39996f + ball.Rotation;
            float distance = ball.R * (.2f + .5f * (i % 4) / 3);
            float d = Math.Max(.7f, ball.R * .025f);
            g.FillEllipse(grains, ball.X + MathF.Cos(angle) * distance, ball.Y + MathF.Sin(angle) * distance, d, d);
        }
    }
    public void PaintSnowman(Graphics g, Ball head, Ball body)
    {
        float r = head.R;
        float scarf = Fade(head.SnowmanAppearance / .76f);
        float face = Fade((head.SnowmanAppearance - .08f) / .82f);
        float buttons = Fade((head.SnowmanAppearance - .22f) / .78f);
        var state = g.Save();
        g.TranslateTransform(head.X, head.Y);
        g.ScaleTransform(r, r);
        using (var neck = new GraphicsPath())
        {
            neck.AddClosedCurve((PointF[])[new(-.81f,.58f),new(-.38f,.72f),new(.38f,.72f),new(.81f,.58f),new(.84f,.91f),new(.43f,1.04f),new(-.43f,1.04f),new(-.84f,.91f)], .44f);
            PaintWool(g, neck, -.9f, .55f, 1.8f, .55f, false, scarf);
        }
        using (var tails = new GraphicsPath())
        {
            tails.AddPolygon((PointF[])[new(-.48f,.82f),new(-.13f,.96f),new(-.28f,1.77f),new(-.66f,1.72f)]);
            tails.AddPolygon((PointF[])[new(.41f,.78f),new(.72f,.76f),new(.91f,1.53f),new(.59f,1.64f)]);
            PaintWool(g, tails, -.72f, .76f, 1.68f, 1.04f, true, scarf);
        }
        PaintCoal(g, -.32f, -.23f, .14f, face);
        PaintCoal(g, .37f, -.26f, .13f, face);
        using (var blush = new SolidBrush(WithOpacity(Color.FromArgb(44, 238, 115, 125), face)))
        {
            g.FillEllipse(blush, -.64f, -.04f, .32f, .26f);
            g.FillEllipse(blush, .44f, -.08f, .27f, .23f);
        }
        for (int i = 0; i < 6; i++)
        {
            float x = -.39f + i * .148f;
            PaintCoal(g, x, .33f - x * x * .9f, .048f + (i % 2) * .006f, face);
        }
        using (var carrot = new GraphicsPath())
        {
            carrot.AddBezier(.1f,-.03f,.23f,-.16f,.64f,-.25f,.84f,-.31f);
            carrot.AddBezier(.84f,-.31f,.90f,-.29f,.66f,-.02f,.27f,.17f);
            carrot.AddBezier(.27f,.17f,.09f,.24f,-.03f,.08f,.1f,-.03f);
            using var shade = new LinearGradientBrush(new RectangleF(0, -.32f, .92f, .57f),
                WithOpacity(Color.FromArgb(255, 180, 69), face), WithOpacity(Color.FromArgb(188, 62, 15), face), 72);
            g.FillPath(shade,carrot);
            var clip = g.Save(); g.SetClip(carrot, CombineMode.Intersect);
            using var grooves = new Pen(WithOpacity(Color.FromArgb(101, 153, 56, 18), face), .022f);
            for (int i=0;i<4;i++) { float x=.28f+i*.12f; g.DrawLine(grooves,x,-.13f-i*.021f,x+.074f,.108f-i*.044f); }
            g.Restore(clip);
        }
        g.Restore(state);
        for (Ball? torso = body; torso is not null; torso = torso.BaseBall)
        {
            float button = Math.Max(1.3f, torso.R * .088f);
            float spacing = torso.R * .2f;
            float middle = torso.Y + torso.R * .23f;
            PaintCoal(g, body.X, middle - spacing, button, buttons);
            PaintCoal(g, body.X, middle + spacing, button, buttons);
        }
    }

    static float Fade(float progress)
    {
        float t = Math.Clamp(progress, 0, 1);
        return t * t * (3 - 2 * t);
    }

    static Color WithOpacity(Color color, float opacity) => Color.FromArgb((int)MathF.Round(color.A * opacity), color);

    static void PaintCoal(Graphics g, float x, float y, float radius, float opacity)
    {
        if (opacity == 0) return;
        var rect = new RectangleF(x-radius,y-radius,radius*2,radius*2);
        using var path = new GraphicsPath(); path.AddEllipse(rect);
        using var brush = new PathGradientBrush(path)
        {
            CenterPoint = new PointF(x + radius*.35f, y - radius*.4f),
            CenterColor = WithOpacity(Color.FromArgb(130, 110, 94), opacity),
            SurroundColors = [WithOpacity(Color.FromArgb(27, 23, 23), opacity)]
        };
        g.FillPath(brush,path);
    }
    static void PaintWool(Graphics g, GraphicsPath path, float x, float y, float width, float height, bool vertical, float opacity)
    {
        using var wool = new LinearGradientBrush(new RectangleF(x, y, width, height),
            WithOpacity(Color.FromArgb(238, 62, 70), opacity), WithOpacity(Color.FromArgb(126, 15, 34), opacity), 75);
        g.FillPath(wool,path);
        var state = g.Save(); g.SetClip(path, CombineMode.Intersect);
        using var stitch = new Pen(WithOpacity(Color.FromArgb(92, 255, 156, 146), opacity), .026f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (float row = y + .055f; row < y+height; row += .105f)
        for (float column = x + .04f; column < x+width; column += .095f)
        {
            if (vertical) g.DrawLine(stitch,column,row,column+.045f,row+.074f);
            else g.DrawLine(stitch,column,row,column+.068f,row+.04f);
        }
        g.Restore(state);
    }

    public void Dispose()
    {
        foreach (var sprite in sprites) sprite?.Dispose();
        Array.Clear(sprites);
    }
}
