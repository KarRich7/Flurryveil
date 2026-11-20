using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace QuietSnow;

/// <summary>Schedules at most one frame on the UI thread without changing the system timer resolution.</summary>
internal sealed class FrameClock : IDisposable
{
    const uint HighResolution = 0x00000002;
    const uint TimerAccess = 0x00100002; // SYNCHRONIZE | TIMER_MODIFY_STATE
    readonly Action tick;
    RunState? run;
    double interval = 1000d / 60;
    bool disposed;

    public FrameClock(Action tick) => this.tick = tick;

    public double Interval
    {
        get => Volatile.Read(ref interval);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            if (Interlocked.Exchange(ref interval, value) != value) run?.Wake.Set();
        }
    }

    // Start, Stop, Dispose and Interval changes are called from the UI thread.
    public void Start(Control dispatcher)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (run is not null) return;
        if (!dispatcher.IsHandleCreated || dispatcher.InvokeRequired)
            throw new InvalidOperationException("The frame clock requires an existing dispatcher handle on the UI thread.");

        var state = new RunState(dispatcher, tick);
        state.Worker = new Thread(() => Schedule(state)) { IsBackground = true, Name = "Snow frame clock" };
        run = state;
        try { state.Worker.Start(); }
        catch
        {
            run = null;
            state.Wake.Dispose();
            throw;
        }
    }

    public void Stop()
    {
        var state = run;
        if (state is null) return;
        run = null;
        state.Stopping = true;
        state.Wake.Set();
        state.Worker!.Join();
        state.Wake.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        Stop();
        disposed = true;
    }

    void Schedule(RunState state)
    {
        using var timer = TryCreateTimer();
        WaitHandle[]? waits = timer is null ? null : [state.Wake, timer];
        double scheduledInterval = 0;
        long period = 0, deadline = 0;
        while (!state.Stopping)
        {
            double nextInterval = Interval;
            long now = Stopwatch.GetTimestamp();
            if (nextInterval != scheduledInterval)
            {
                scheduledInterval = nextInterval;
                period = (long)Math.Ceiling(Stopwatch.Frequency * (nextInterval / 1000d));
                deadline = now + period;
            }

            // While the UI has a frame queued or is painting it, there is no reason to wake.
            if (state.Pending)
            {
                state.Wake.WaitOne();
                continue;
            }

            long lastStart = Interlocked.Read(ref state.LastFrameStart);
            long earliest = Math.Max(deadline, lastStart + period * 9 / 10);
            if (now < earliest)
            {
                var remaining = Stopwatch.GetElapsedTime(now, earliest);
                long dueTime = -Math.Max(1, remaining.Ticks);
                if (timer is not null && SetWaitableTimer(timer.SafeWaitHandle, in dueTime, 0, 0, 0, false))
                    WaitHandle.WaitAny(waits!);
                else
                    state.Wake.WaitOne(Math.Max(1, (int)Math.Ceiling(remaining.TotalMilliseconds)));
                continue;
            }

            // Skip missed frames instead of delivering several callbacks at once.
            deadline += ((now - deadline) / period + 1) * period;
            state.Pending = true;
            try { state.Dispatcher.BeginInvoke(state.InvokeFrame); }
            catch (InvalidOperationException) { return; } // The dispatcher handle is being destroyed.
            state.Wake.WaitOne(); // Consume the completion signal even if the UI responds immediately.
        }
    }

    static TimerWaitHandle? TryCreateTimer()
    {
        var handle = CreateWaitableTimerEx(nint.Zero, null, HighResolution, TimerAccess);
        if (!handle.IsInvalid) return new TimerWaitHandle(handle);
        handle.Dispose();
        handle = CreateWaitableTimerEx(nint.Zero, null, 0, TimerAccess);
        if (!handle.IsInvalid) return new TimerWaitHandle(handle);
        handle.Dispose();
        return null;
    }

    sealed class RunState
    {
        public readonly Control Dispatcher;
        public readonly AutoResetEvent Wake = new(false);
        public readonly MethodInvoker InvokeFrame;
        public volatile bool Pending, Stopping;
        public long LastFrameStart;
        public Thread? Worker;

        public RunState(Control dispatcher, Action tick)
        {
            Dispatcher = dispatcher;
            InvokeFrame = () =>
            {
                if (Stopping) return;
                Interlocked.Exchange(ref LastFrameStart, Stopwatch.GetTimestamp());
                try { tick(); }
                finally
                {
                    Pending = false;
                    if (!Stopping) Wake.Set();
                }
            };
        }
    }

    sealed class TimerWaitHandle : WaitHandle
    {
        public TimerWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern SafeWaitHandle CreateWaitableTimerEx(nint attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetWaitableTimer(SafeWaitHandle timer, in long dueTime, int period, nint completion, nint context,
        [MarshalAs(UnmanagedType.Bool)] bool resumeFromSleep);
}
