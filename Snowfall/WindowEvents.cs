using System.Runtime.InteropServices;

namespace QuietSnow;

// Windows notifies the UI thread about detached tabs and moving top-level windows.
// The next animation frame samples current geometry and presents a complete frame.
internal sealed class WindowEvents : IDisposable
{
    readonly WinEventCallback callback;
    nint hook, foregroundHook;
    bool changed, orderChanged;
    public WindowEvents()
    {
        callback = OnEvent;
        hook = SetWinEventHook(0x8000, 0x8004, 0, callback, 0, 0, 2); // OUTOFCONTEXT | SKIPOWNPROCESS
        foregroundHook = SetWinEventHook(3, 3, 0, callback, 0, 0, 2);
    }
    void OnEvent(nint eventHook, uint kind, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (window == 0 || objectId != 0 || childId != 0) return;
        if (kind is 3 or 0x8004) orderChanged = true;
        else if (kind is 0x8000 or 0x8001 or 0x8002 or 0x8003) changed = true;
    }
    public bool ConsumeChange() { bool result = changed; changed = false; return result; }
    public bool ConsumeOrderChange() { bool result = orderChanged; orderChanged = false; return result; }
    public void Dispose()
    {
        if (hook != 0) UnhookWinEvent(hook);
        if (foregroundHook != 0) UnhookWinEvent(foregroundHook);
        hook = foregroundHook = 0;
        GC.KeepAlive(callback);
    }
    delegate void WinEventCallback(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(nint hook);
}
