using System.Numerics;
using System.Runtime.CompilerServices;

namespace QuietSnow;

// XXH64 over pixel rows padded to 32 bytes. The four independent lanes avoid
// the per-pixel dependency chain of FNV; alpha is checked in the same pass.
// Algorithm: https://github.com/Cyan4973/xxHash/blob/dev/doc/xxhash_spec.md
internal static unsafe class TileFingerprint
{
    const ulong Prime1 = 11400714785074694791UL, Prime2 = 14029467366897019727UL;
    const ulong Prime3 = 1609587929392839161UL, Prime4 = 9650029242287828579UL;

    public static ulong Compute(nint pixels, int stride, int width, int height, out bool visible)
    {
        unchecked
        {
            ulong seed = (ulong)(uint)width << 32 | (uint)height;
            ulong a = seed + Prime1 + Prime2, b = seed + Prime2, c = seed, d = seed - Prime1;
            ulong alpha = 0;
            int fullWidth = width & ~7;
            Span<ulong> tail = stackalloc ulong[4];
            tail.Clear();
            for (int y = 0; y < height; y++)
            {
                byte* row = (byte*)(pixels + y * stride);
                for (int x = 0; x < fullWidth; x += 8)
                {
                    byte* part = row + x * 4;
                    ulong p = Unsafe.ReadUnaligned<ulong>(part), q = Unsafe.ReadUnaligned<ulong>(part + 8);
                    ulong r = Unsafe.ReadUnaligned<ulong>(part + 16), s = Unsafe.ReadUnaligned<ulong>(part + 24);
                    alpha |= p | q | r | s;
                    a = Round(a, p); b = Round(b, q); c = Round(c, r); d = Round(d, s);
                }
                if (fullWidth != width)
                {
                    new ReadOnlySpan<byte>(row + fullWidth * 4, (width - fullWidth) * 4)
                        .CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(tail));
                    alpha |= tail[0] | tail[1] | tail[2] | tail[3];
                    a = Round(a, tail[0]); b = Round(b, tail[1]);
                    c = Round(c, tail[2]); d = Round(d, tail[3]);
                }
            }
            ulong hash = BitOperations.RotateLeft(a, 1) + BitOperations.RotateLeft(b, 7) +
                BitOperations.RotateLeft(c, 12) + BitOperations.RotateLeft(d, 18);
            hash = Merge(hash, a); hash = Merge(hash, b); hash = Merge(hash, c); hash = Merge(hash, d);
            hash += (ulong)((width + 7) / 8 * 32 * height);
            hash ^= hash >> 33; hash *= Prime2;
            hash ^= hash >> 29; hash *= Prime3;
            hash ^= hash >> 32;
            visible = (alpha & 0xff000000ff000000UL) != 0;
            return hash;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Round(ulong accumulator, ulong value) =>
        unchecked(BitOperations.RotateLeft(accumulator + value * Prime2, 31) * Prime1);

    static ulong Merge(ulong hash, ulong lane) => unchecked((hash ^ Round(0, lane)) * Prime1 + Prime4);
}
