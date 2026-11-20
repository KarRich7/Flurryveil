using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace QuietSnow;

// Pack strips containing decorations into a texture at one texel per screen pixel.
// The transparent space between strips never needs a bitmap, texture or upload.
internal sealed unsafe class GpuRasterLayer : IDisposable
{
    const int Edge = 64;
    const int PaintMargin = 1;
    const int ReleaseDelay = 5000;
    readonly Size size;
    readonly int[] leftByRow, rightByRow;
    readonly List<RectangleF> bounds = [];
    readonly List<Band> bands = [];
    readonly List<BandCache> bandCaches = [];
    List<long> currentState = [], previousState = [];
    ulong[] hashes = [];
    bool[] initialized = [];
    Bitmap? bitmap;
    Graphics? graphics;
    Size textureSize;
    long idleSince, oversizedSince;
    bool hasSnapshot;
    int bufferGeneration;
    public nint Texture, View;
    public readonly List<Block> VisibleBlocks = [];
    public long UploadBytes { get; private set; }
    public long SurfaceBytes => bitmap is null ? 0 : (long)bitmap.Width * bitmap.Height * 4;
    internal int PaintCount { get; private set; }
    internal int BandPaintCount { get; private set; }

    readonly record struct Band(Rectangle Screen, int TextureY);
    sealed class BandCache
    {
        public Band Layout;
        public int Generation = -1;
        public List<long> CurrentState = [], PreviousState = [];
        public readonly List<Block> Blocks = [];
        public bool Dirty;
    }
    public readonly record struct Block(Rectangle Screen, Point TextureOrigin);

    public GpuRasterLayer(Size size)
    {
        this.size = size;
        leftByRow = new int[(size.Height + Edge - 1) / Edge];
        rightByRow = new int[leftByRow.Length];
    }

    public void Update(SnowApp app, Rectangle viewport, SnowPaintLayer layer, GpuSnowRenderer renderer)
    {
        UploadBytes = 0;
        long now = Environment.TickCount64;
        app.GpuRasterState(currentState, viewport, layer);
        if (hasSnapshot && CollectionsMarshal.AsSpan(currentState).SequenceEqual(CollectionsMarshal.AsSpan(previousState)))
        {
            if (VisibleBlocks.Count > 0 && oversizedSince == 0) return;
            if (VisibleBlocks.Count == 0)
            {
                if (bitmap is not null && now - idleSince >= ReleaseDelay) ReleaseBuffers();
                return;
            }
        }
        hasSnapshot = true;
        (currentState, previousState) = (previousState, currentState);
        VisibleBlocks.Clear();
        var required = PackBands(app, viewport, layer);
        if (required.IsEmpty)
        {
            if (idleSince == 0) idleSince = now;
            if (bitmap is not null && now - idleSince >= ReleaseDelay) ReleaseBuffers();
            return;
        }

        var paintSize = new Size(required.Width + 2 * PaintMargin, required.Height + bands.Count * 2 * PaintMargin);
        EnsureBuffers(required, paintSize, renderer, now);
        var canvas = graphics!;
        var image = bitmap!;
        bool painted = false;
        for (int i = 0; i < bands.Count; i++)
        {
            var band = bands[i];
            if (i == bandCaches.Count) bandCaches.Add(new BandCache());
            var cache = bandCaches[i];
            var region = band.Screen;
            region.Offset(viewport.Location);
            // GDI+ can antialias a clipped bitmap along the clip edge. Paint a
            // guard pixel on each side and upload only the interior of the band.
            region.Inflate(PaintMargin, PaintMargin);
            app.GpuRasterState(cache.CurrentState, viewport, layer, rasterRegion: region);
            cache.Dirty = cache.Generation != bufferGeneration || cache.Layout != band ||
                !CollectionsMarshal.AsSpan(cache.CurrentState).SequenceEqual(CollectionsMarshal.AsSpan(cache.PreviousState));
            if (!cache.Dirty) continue;
            BandPaintCount++;
            (cache.CurrentState, cache.PreviousState) = (cache.PreviousState, cache.CurrentState);
            cache.Generation = bufferGeneration;
            cache.Layout = band;
            canvas.ResetTransform();
            canvas.ResetClip();
            canvas.CompositingMode = CompositingMode.SourceCopy;
            canvas.SmoothingMode = SmoothingMode.None;
            int scratchY = band.TextureY + i * 2 * PaintMargin;
            canvas.FillRectangle(Brushes.Transparent, 0, scratchY, region.Width, region.Height);
            canvas.CompositingMode = CompositingMode.SourceOver;
            app.Paint(canvas, viewport, layer, region, scratchY);
            painted = true;
        }
        if (painted)
        {
            PaintCount++;
            canvas.Flush(FlushIntention.Sync);
            var data = image.LockBits(new Rectangle(Point.Empty, paintSize), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int textureColumns = (textureSize.Width + Edge - 1) / Edge;
                for (int i = 0; i < bands.Count; i++)
                {
                    var cache = bandCaches[i];
                    if (!cache.Dirty) continue;
                    var band = bands[i];
                    int paintTop = band.TextureY + (i * 2 + 1) * PaintMargin;
                    cache.Blocks.Clear();
                    for (int y = 0; y < band.Screen.Height; y += Edge)
                    for (int x = 0; x < band.Screen.Width; x += Edge)
                    {
                        int ty = band.TextureY + y;
                        int width = Math.Min(Edge, band.Screen.Width - x);
                        int height = Math.Min(Edge, band.Screen.Height - y);
                        nint start = data.Scan0 + (paintTop + y) * data.Stride + (x + PaintMargin) * 4;
                        ulong hash = TileFingerprint.Compute(start, data.Stride, width, height, out bool visible);
                        if (!visible) continue;
                        cache.Blocks.Add(new Block(new Rectangle(band.Screen.X + x, band.Screen.Y + y, width, height), new Point(x, ty)));
                        int slot = ty / Edge * textureColumns + x / Edge;
                        if (initialized[slot] && hashes[slot] == hash) continue;
                        renderer.UpdateTexture(Texture, new Rectangle(x, ty, width, height), start, data.Stride);
                        UploadBytes += (long)width * height * 4;
                        hashes[slot] = hash;
                        initialized[slot] = true;
                    }
                }
            }
            finally { image.UnlockBits(data); }
        }
        for (int i = 0; i < bands.Count; i++) VisibleBlocks.AddRange(bandCaches[i].Blocks);
        if (VisibleBlocks.Count > 0) idleSince = 0;
        else if (idleSince == 0) idleSince = now;
    }

    Size PackBands(SnowApp app, Rectangle viewport, SnowPaintLayer layer)
    {
        Array.Fill(leftByRow, size.Width);
        Array.Clear(rightByRow);
        bands.Clear();
        app.GpuLayerBounds(bounds, layer);
        foreach (var world in bounds)
        {
            var box = Rectangle.Intersect(Rectangle.Ceiling(world), viewport);
            if (box.Width <= 0 || box.Height <= 0) continue;
            box.Offset(-viewport.Left, -viewport.Top);
            int left = box.Left / Edge * Edge;
            int right = Math.Min(size.Width, (box.Right + Edge - 1) / Edge * Edge);
            for (int row = box.Top / Edge; row <= (box.Bottom - 1) / Edge; row++)
            {
                leftByRow[row] = Math.Min(leftByRow[row], left);
                rightByRow[row] = Math.Max(rightByRow[row], right);
            }
        }

        int width = 0, height = 0;
        for (int row = 0; row < leftByRow.Length;)
        {
            int start = row++;
            int left = leftByRow[start], right = rightByRow[start];
            if (right <= left) continue;
            while (row < leftByRow.Length && leftByRow[row] == left && rightByRow[row] == right) row++;
            var screen = Rectangle.FromLTRB(left, start * Edge, right, Math.Min(size.Height, row * Edge));
            bands.Add(new Band(screen, height));
            width = Math.Max(width, screen.Width);
            height += screen.Height;
        }
        return new Size(width, height);
    }

    void EnsureBuffers(Size required, Size paintSize, GpuSnowRenderer renderer, long now)
    {
        if (bitmap is not null && textureSize.Width >= required.Width && textureSize.Height >= required.Height)
        {
            bool oversized = (long)required.Width * required.Height * 2 <= (long)textureSize.Width * textureSize.Height;
            if (!oversized) oversizedSince = 0;
            else if (oversizedSince == 0) oversizedSince = now;
            if (!oversized || now - oversizedSince < ReleaseDelay)
            {
                if (bitmap.Width < paintSize.Width || bitmap.Height < paintSize.Height)
                    AllocateCanvas(new Size(Math.Max(bitmap.Width, paintSize.Width), Math.Max(bitmap.Height, paintSize.Height)));
                return;
            }
        }

        ReleaseBuffers();
        try
        {
            renderer.CreateTexture(required, out Texture, out View);
            textureSize = required;
            AllocateCanvas(paintSize);
            int slots = ((required.Width + Edge - 1) / Edge) * ((required.Height + Edge - 1) / Edge);
            hashes = new ulong[slots];
            initialized = new bool[slots];
        }
        catch
        {
            ReleaseBuffers();
            throw;
        }
    }

    void AllocateCanvas(Size size)
    {
        var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        Graphics canvas;
        try { canvas = Graphics.FromImage(image); }
        catch { image.Dispose(); throw; }
        canvas.PixelOffsetMode = PixelOffsetMode.HighQuality;
        canvas.CompositingQuality = CompositingQuality.HighSpeed;
        graphics?.Dispose();
        bitmap?.Dispose();
        bitmap = image;
        graphics = canvas;
        bufferGeneration++;
    }

    void ReleaseBuffers()
    {
        bufferGeneration++;
        graphics?.Dispose();
        graphics = null;
        bitmap?.Dispose();
        bitmap = null;
        GpuSnowRenderer.Release(ref View);
        GpuSnowRenderer.Release(ref Texture);
        textureSize = Size.Empty;
        hashes = [];
        initialized = [];
        oversizedSince = 0;
    }

    public void Dispose() => ReleaseBuffers();
}
