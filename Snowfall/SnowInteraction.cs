using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace QuietSnow;

/// <summary>Receives clicks on snow, and follows the mouse until the snowball is released.</summary>
internal sealed class InputOverlay : Form
{
    const int Layered = 0x00080000, ToolWindow = 0x80, NoActivate = 0x08000000;
    const int NoRedirectionBitmap = 0x00200000;
    const int MouseActivate = 0x0021, HitTest = 0x0084, CancelMode = 0x001F;
    const int MouseMoveMessage = 0x0200, LeftDown = 0x0201, LeftUp = 0x0202, LeftDouble = 0x0203;
    const int RightDown = 0x0204, RightUp = 0x0205, RightDouble = 0x0206;
    const int CaptureChanged = 0x0215, LeftButton = 1;
    readonly SnowApp app;
    nint hitRegionHandle;
    Region? hitRegion;
    long releasedSince;

    public bool Dragging { get; private set; }
    internal Region? HitRegion => hitRegion;

    public InputOverlay(SnowApp app, Rectangle bounds)
    {
        this.app = app;
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        Cursor = Cursors.Hand;
        Bounds = bounds;
        SetHitRegion(EmptyRegion());
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            // Hit regions provide mouse targets; SnowOverlay owns the visible
            // pixels. This window needs no desktop redirection bitmap.
            parameters.ExStyle |= Layered | ToolWindow | NoActivate | NoRedirectionBitmap;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!SetLayeredWindowAttributes(Handle, 0, 2, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using var graphics = CreateGraphics();
        nint nativeRegion = hitRegion?.GetHrgn(graphics) ?? 0;
        try
        {
            ApplyWindowRegion(nativeRegion);
            hitRegionHandle = nativeRegion;
            nativeRegion = 0;
        }
        finally { if (nativeRegion != 0) DeleteObject(nativeRegion); }
    }

    public void SetHitRegion(Region hitRegion)
    {
        if (Dragging) { hitRegion.Dispose(); return; }
        ReplaceRegion(hitRegion);
    }

    public bool ContainsHit(Point point) => hitRegion?.IsVisible(PointToClient(point)) == true;

    public void RaiseForInteraction()
    {
        if (Visible && IsHandleCreated)
            SetWindowPos(Handle, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);
    }

    public void FinishDrag(bool releaseCapture = true, bool refresh = true)
    {
        if (!Dragging) return;
        Dragging = false;
        releasedSince = 0;
        app.MouseUp();
        if (releaseCapture && IsHandleCreated && GetCapture() == Handle) ReleaseCapture();
        app.InvalidateInputRegion();
        if (refresh) app.RefreshInputRegion();
    }

    public void PollDrag(Point point, bool pressed, bool cancel)
    {
        if (!Dragging) return;
        if (cancel || !app.HasHeldBall) { FinishDrag(); return; }
        if (pressed)
        {
            releasedSince = 0;
            app.MouseMove(point, true);
        }
        else
        {
            if (releasedSince == 0) releasedSince = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(releasedSince).TotalMilliseconds >= 80) FinishDrag();
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        FinishDrag(releaseCapture: false, refresh: false);
        if (hitRegionHandle != 0) DeleteObject(hitRegionHandle);
        hitRegionHandle = 0;
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        finally
        {
            if (disposing)
            {
                hitRegion?.Dispose();
                hitRegion = null;
            }
        }
    }

    protected override void WndProc(ref Message message)
    {
        switch (message.Msg)
        {
            case HitTest:
                var screen = new Point(unchecked((short)message.LParam.ToInt64()), unchecked((short)(message.LParam.ToInt64() >> 16)));
                message.Result = Dragging || app.CanPickUpSnow(screen) ? 1 : -1;
                return;
            case MouseActivate:
                message.Result = 3; // MA_NOACTIVATE, while delivering the click.
                return;
            case LeftDown:
            case LeftDouble:
                // A new press can arrive before polling sees the previous release.
                bool interrupted = Dragging;
                if (interrupted) FinishDrag(refresh: false);
                var point = ScreenPoint(message.LParam);
                if (!app.MouseDown(point))
                {
                    if (interrupted) app.RefreshInputRegion();
                    return;
                }
                Dragging = true;
                releasedSince = 0;
                // Even without foreground activation the window receives the
                // rest of the gesture anywhere on the virtual desktop.
                ReplaceRegion(null);
                RaiseForInteraction();
                SetCapture(Handle);
                return;
            case MouseMoveMessage:
                if (Dragging && (message.WParam.ToInt64() & LeftButton) != 0)
                    app.MouseMove(ScreenPoint(message.LParam), true);
                return;
            case LeftUp:
                if (Dragging) app.MouseMove(ScreenPoint(message.LParam), true);
                FinishDrag();
                return;
            case RightDown:
            case RightDouble:
                // Handle both halves of the click here. WinForms would acquire
                // capture on down and never release it after our own up handler.
                return;
            case RightUp:
                if (Dragging && (message.WParam.ToInt64() & LeftButton) != 0) return;
                FinishDrag();
                app.RemoveBall(ScreenPoint(message.LParam));
                return;
            case CaptureChanged:
                // A nonactivating window can lose native capture during a drag.
                // Poll the button and cursor until release without taking focus.
                return;
            case CancelMode:
                FinishDrag();
                break;
        }
        base.WndProc(ref message);
    }

    void ReplaceRegion(Region? next)
    {
        if (ReferenceEquals(next, hitRegion)) return;
        nint nextHandle = 0;
        try
        {
            if (IsHandleCreated)
            {
                if (next is not null)
                {
                    using var graphics = CreateGraphics();
                    nextHandle = next.GetHrgn(graphics);
                    if (hitRegionHandle != 0 && EqualRgn(hitRegionHandle, nextHandle)) return;
                }
                ApplyWindowRegion(nextHandle);
            }
            if (hitRegionHandle != 0) DeleteObject(hitRegionHandle);
            hitRegionHandle = nextHandle;
            nextHandle = 0;
            var previous = hitRegion;
            hitRegion = next;
            next = null;
            previous?.Dispose();
        }
        finally
        {
            if (nextHandle != 0) DeleteObject(nextHandle);
            next?.Dispose();
        }
    }

    void ApplyWindowRegion(nint nativeRegion)
    {
        // Control.Region's update path retains native memory under frequent
        // changes. Set the same HRGN directly, keeping one copy for equality
        // checks and transferring a second copy to Windows on success.
        nint windowRegion = 0;
        try
        {
            if (nativeRegion != 0)
            {
                windowRegion = CreateRectRgn(0, 0, 0, 0);
                if (windowRegion == 0 || CombineRgn(windowRegion, nativeRegion, 0, 5) == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (SetWindowRgn(Handle, windowRegion, Visible) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            windowRegion = 0; // The window owns this copy now.
        }
        finally { if (windowRegion != 0) DeleteObject(windowRegion); }
    }

    public static Region EmptyRegion()
    {
        var region = new Region();
        region.MakeEmpty();
        return region;
    }

    Point ScreenPoint(nint coordinates)
    {
        long packed = coordinates.ToInt64();
        return PointToScreen(new Point(unchecked((short)packed), unchecked((short)(packed >> 16))));
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern nint SetCapture(nint window);
    [DllImport("user32.dll")] static extern nint GetCapture();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ReleaseCapture();
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EqualRgn(nint first, nint second);
    [DllImport("gdi32.dll", SetLastError = true)] static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", SetLastError = true)] static extern int CombineRgn(nint destination, nint first, nint second, int mode);
    [DllImport("user32.dll", SetLastError = true)] static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteObject(nint value);
}
