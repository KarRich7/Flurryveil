using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace QuietSnow;

/// <summary>
/// Full-resolution GPU output per monitor, with a layered bitmap fallback.
/// </summary>
internal sealed class SnowOverlay : Form
{
    const int WsExLayered = 0x00080000;
    const int WsExTransparent = 0x00000020;
    const int WsExToolWindow = 0x00000080;
    const int WsExNoActivate = 0x08000000;
    readonly SnowApp app;
    nint screenDc, memoryDc, dib, previousBitmap;
    Bitmap? frame;
    Graphics? canvas;
    Size surfaceSize;
    bool rendering;
    bool gpuEnabled = Environment.GetEnvironmentVariable("QUIETSNOW_FORCE_GDI") != "1";
    GpuSnowRenderer? gpu;
    public bool UsesGpu => gpu is not null;
    public GpuSnowRenderer? GpuBackend => gpu;
    public string? GpuFailureReason { get; private set; }
    public Size RenderSize => gpu?.RenderSize ?? surfaceSize;
    public int LastPresentationError { get; private set; }

    internal void RaiseForInteraction()
    {
        if (Visible && IsHandleCreated) SetWindowPos(Handle, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);
    }

    public SnowOverlay(SnowApp app, Rectangle bounds)
    {
        this.app = app;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Bounds = bounds;
        TopMost = true;
        // Ordinary Form painting and double buffering must remain disabled: the
        // compositor receives only complete alpha frames through RenderFrame.
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate;
            if (gpuEnabled) p.ExStyle |= 0x00200000;
            return p;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Initialize before showing, so the first visible frame is transparent.
        if (gpuEnabled)
        {
            SetLayeredWindowAttributes(Handle, 0, 255, 2);
            try { gpu = new GpuSnowRenderer(Handle, Size); }
            catch (Exception error) when (IsGpuError(error)) { FallBack(error); }
        }
        RenderFrame();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        gpu?.Dispose();
        gpu = null;
        ReleaseSurface();
        base.OnHandleDestroyed(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0014) { m.Result = (nint)1; return; } // WM_ERASEBKGND
        if (m.Msg == 0x0084) { m.Result = (nint)(-1); return; } // HTTRANSPARENT
        base.WndProc(ref m);
    }

    public void RenderFrame()
    {
        if (IsDisposed || Disposing || !IsHandleCreated || rendering || Width < 1 || Height < 1) return;
        rendering = true;
        try
        {
            if (gpu is not null)
            {
                try
                {
                    if (gpu.RenderSize != Size) { gpu.Dispose(); gpu = new GpuSnowRenderer(Handle, Size); }
                    LastPresentationError = gpu.Render(app, Bounds);
                    if (LastPresentationError < 0) throw new COMException("GPU presentation failed", LastPresentationError);
                    return;
                }
                catch (Exception error) when (IsGpuError(error)) { FallBack(error); }
            }
            // OnHandleCreated runs before WinForms refreshes its cached client
            // metrics. Using ClientSize there can retain the old 8px frame on
            // each side and shrink a borderless 1920px monitor layer to 1904px.
            // A borderless layered surface covers the full outer window size.
            EnsureSurface(Size);
            var g = canvas!;
            g.ResetTransform();
            g.ResetClip();
            g.CompositingMode = CompositingMode.SourceCopy;
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            app.Paint(g, Bounds);
            g.Flush(FlushIntention.Sync);
            var destination = new NativePoint(Left, Top);
            var source = new NativePoint(0, 0);
            var size = new NativeSize(surfaceSize.Width, surfaceSize.Height);
            var blend = new BlendFunction { BlendOp = 0, SourceConstantAlpha = (byte)(app.EffectOpacity * 255 / 100), AlphaFormat = 1 };
            LastPresentationError = UpdateLayeredWindow(Handle, screenDc, ref destination, ref size,
                memoryDc, ref source, 0, ref blend, 2) ? 0 : Marshal.GetLastWin32Error();
        }
        finally { rendering = false; }
    }

    static bool IsGpuError(Exception error) => error is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or ArgumentException;
    void FallBack(Exception error)
    {
        GpuFailureReason = error.Message;
        gpu?.Dispose(); gpu = null; gpuEnabled = false;
        // SetLayeredWindowAttributes and ULW cannot be mixed until LAYERED is reset.
        long style = Native.GetWindowLongPtr(Handle, -20).ToInt64() & ~0x00200000L;
        SetExtendedStyle(Handle, style & ~WsExLayered);
        SetExtendedStyle(Handle, style | WsExLayered);
    }
    static void SetExtendedStyle(nint handle, long style)
    {
        if (nint.Size == 8) SetWindowLongPtr(handle, -20, (nint)style);
        else SetWindowLong(handle, -20, (int)style);
    }
    void EnsureSurface(Size size)
    {
        if (canvas is not null && surfaceSize == size) return;
        ReleaseSurface();
        try
        {
            screenDc = GetDC(nint.Zero);
            if (screenDc == nint.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == nint.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = size.Width,
                    Height = -size.Height, Planes = 1, BitCount = 32, Compression = 0,
                    SizeImage = checked((uint)(size.Width * size.Height * 4))
                }
            };
            dib = CreateDIBSection(screenDc, ref info, 0, out nint pixels, nint.Zero, 0);
            if (dib == nint.Zero || pixels == nint.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            previousBitmap = SelectObject(memoryDc, dib);
            if (previousBitmap == nint.Zero || previousBitmap == (nint)(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            frame = new Bitmap(size.Width, size.Height, checked(size.Width * 4), PixelFormat.Format32bppPArgb, pixels);
            canvas = Graphics.FromImage(frame);
            surfaceSize = size;
        }
        catch { ReleaseSurface(); throw; }
    }

    void ReleaseSurface()
    {
        canvas?.Dispose(); canvas = null;
        frame?.Dispose(); frame = null;
        if (memoryDc != nint.Zero && previousBitmap != nint.Zero && previousBitmap != (nint)(-1)) SelectObject(memoryDc, previousBitmap);
        previousBitmap = nint.Zero;
        if (dib != nint.Zero) { DeleteObject(dib); dib = nint.Zero; }
        if (memoryDc != nint.Zero) { DeleteDC(memoryDc); memoryDc = nint.Zero; }
        if (screenDc != nint.Zero) { ReleaseDC(nint.Zero, screenDc); screenDc = nint.Zero; }
        surfaceSize = Size.Empty;
    }

    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; public NativePoint(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] struct NativeSize { public int Width, Height; public NativeSize(int w, int h) { Width = w; Height = h; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteDC(nint dc);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UpdateLayeredWindow(nint hwnd, nint dstDc, ref NativePoint dst, ref NativeSize size,
        nint srcDc, ref NativePoint src, uint colorKey, ref BlendFunction blend, uint flags);
}
