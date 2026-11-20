using System.Numerics;

namespace QuietSnow;

// Non-overlapping screen regions for Present1. Old and new particle bounds are
// both marked so moving or disappearing objects cannot leave trails.
internal sealed class DirtyTiles
{
    public const int Edge = 32;
    readonly Size size;
    public int Columns { get; }
    public int Rows { get; }
    public byte[] Mask { get; }
    public readonly List<Native.RECT> Rectangles = [];
    Dictionary<long, int> previousRuns = [], currentRuns = [];
    public long Pixels { get; private set; }

    public DirtyTiles(Size size)
    {
        this.size = size;
        Columns = (size.Width + Edge - 1) / Edge;
        Rows = (size.Height + Edge - 1) / Edge;
        Mask = new byte[Columns * Rows];
    }

    public void Reset() { Array.Clear(Mask); Rectangles.Clear(); Pixels = 0; }

    public void Mark(Vector4 rect)
    {
        int left = Math.Clamp((int)MathF.Floor(rect.X) - 1, 0, size.Width);
        int top = Math.Clamp((int)MathF.Floor(rect.Y) - 1, 0, size.Height);
        int right = Math.Clamp((int)MathF.Ceiling(rect.X + rect.Z) + 1, 0, size.Width);
        int bottom = Math.Clamp((int)MathF.Ceiling(rect.Y + rect.W) + 1, 0, size.Height);
        if (right <= left || bottom <= top) return;
        for (int y = top / Edge; y <= (bottom - 1) / Edge; y++)
            Mask.AsSpan(y * Columns + left / Edge, (right - 1) / Edge - left / Edge + 1).Fill(1);
    }

    public void Full()
    {
        Mask.AsSpan().Fill(1); Rectangles.Clear();
        Rectangles.Add(new Native.RECT { Right = size.Width, Bottom = size.Height });
        Pixels = (long)size.Width * size.Height;
    }

    public void Build()
    {
        previousRuns.Clear();
        for (int y = 0; y < Rows; y++)
        {
            currentRuns.Clear();
            for (int x = 0; x < Columns;)
            {
                if (Mask[y * Columns + x] == 0) { x++; continue; }
                int start = x++;
                while (x < Columns && Mask[y * Columns + x] != 0) x++;
                var rect = new Native.RECT { Left = start * Edge, Top = y * Edge,
                    Right = Math.Min(size.Width, x * Edge), Bottom = Math.Min(size.Height, (y + 1) * Edge) };
                Pixels += (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                long key = (long)rect.Left << 32 | (uint)rect.Right;
                int index;
                if (previousRuns.TryGetValue(key, out index))
                {
                    var previous = Rectangles[index]; previous.Bottom = rect.Bottom;
                    Rectangles[index] = previous;
                }
                else { index = Rectangles.Count; Rectangles.Add(rect); }
                currentRuns.Add(key, index);
            }
            (previousRuns, currentRuns) = (currentRuns, previousRuns);
        }
        if (Pixels * 4 > (long)size.Width * size.Height * 3) Full();
    }
}
