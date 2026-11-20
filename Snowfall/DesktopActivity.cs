using System.Runtime.InteropServices;

namespace QuietSnow;

// Event-driven: no polling or rendering while the desktop is unavailable.
internal sealed class DesktopActivity : NativeWindow, IDisposable
{
    static readonly Guid DisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");
    static readonly Guid PowerSource = new("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548");
    static readonly Guid BatterySaver = new("e00958c0-c213-4ace-ac77-fecced2eeea5");
    readonly Action changed;
    readonly bool sessionRegistered;
    nint powerNotification, sourceNotification, saverNotification;
    bool locked, disconnected, displayOff, suspended;
    public bool Unavailable => locked || disconnected || displayOff || suspended;
    public bool OnBatteryOrSaver { get; private set; }

    public DesktopActivity(Action changed)
    {
        this.changed = changed;
        // A hidden top-level window also receives suspend/resume broadcasts;
        // message-only windows do not receive those system broadcasts.
        CreateHandle(new CreateParams { Caption = "QuietSnow activity notifications" });
        sessionRegistered = WTSRegisterSessionNotification(Handle, 0);
        powerNotification = RegisterPowerSettingNotification(Handle, in DisplayState, 0);
        sourceNotification = RegisterPowerSettingNotification(Handle, in PowerSource, 0);
        saverNotification = RegisterPowerSettingNotification(Handle, in BatterySaver, 0);
        RefreshPower();
    }

    void RefreshPower()
    {
        if (GetSystemPowerStatus(out var power)) OnBatteryOrSaver = power.Ac == 0 || power.Saver == 1;
    }

    protected override void WndProc(ref Message message)
    {
        if (ProcessNotification(message.Msg, message.WParam, message.LParam)) changed();
        base.WndProc(ref message);
    }

    internal bool ProcessNotification(int message, nint wParam, nint lParam)
    {
        bool wasUnavailable = Unavailable, wasLowPower = OnBatteryOrSaver;
        if (message == 0x02B1)
        {
            if ((int)wParam is 2 or 4 or 6) disconnected = true;
            else if ((int)wParam is 1 or 3 or 5) disconnected = false;
            else if ((int)wParam == 7) locked = true;
            else if ((int)wParam == 8) locked = false;
        }
        else if (message == 0x0218)
        {
            if ((int)wParam == 4) suspended = true;
            else if ((int)wParam is 6 or 7 or 18) { suspended = false; displayOff = false; }
            else if ((int)wParam == 0x8013 && lParam != 0 &&
                Marshal.PtrToStructure<Guid>(lParam) == DisplayState && Marshal.ReadInt32(lParam, 16) == 4)
                displayOff = Marshal.ReadInt32(lParam, 20) == 0;
            RefreshPower();
        }
        return wasUnavailable != Unavailable || wasLowPower != OnBatteryOrSaver;
    }

    public void Dispose()
    {
        if (powerNotification != 0) { UnregisterPowerSettingNotification(powerNotification); powerNotification = 0; }
        if (sourceNotification != 0) { UnregisterPowerSettingNotification(sourceNotification); sourceNotification = 0; }
        if (saverNotification != 0) { UnregisterPowerSettingNotification(saverNotification); saverNotification = 0; }
        if (Handle != 0)
        {
            if (sessionRegistered) WTSUnRegisterSessionNotification(Handle);
            DestroyHandle();
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct PowerStatus
    { public byte Ac, Flags, Percentage, Saver; public uint Remaining, Full; }
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("user32.dll")] static extern nint RegisterPowerSettingNotification(nint window, in Guid setting, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool UnregisterPowerSettingNotification(nint handle);
    [DllImport("wtsapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool WTSRegisterSessionNotification(nint window, uint flags);
    [DllImport("wtsapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool WTSUnRegisterSessionNotification(nint window);
}
