using System.Diagnostics;

namespace QuietSnow;

internal static class GlassTheme
{
    public static Color Mix(Color start, Color end, float amount) => Color.FromArgb(
        MixChannel(start.R, end.R, amount), MixChannel(start.G, end.G, amount), MixChannel(start.B, end.B, amount));

    static int MixChannel(int start, int end, float amount) => (int)Math.Round(start + (end - start) * Math.Clamp(amount, 0, 1));
}

internal sealed class GlassMotion : IDisposable
{
    readonly Control owner;
    Form? host;
    System.Windows.Forms.Timer? timer;
    readonly Stopwatch clock = new();
    float from, target;
    public float Value { get; private set; }
    public bool Running => timer?.Enabled == true;

    public GlassMotion(Control owner, float initial = 0)
    {
        this.owner = owner; Value = target = initial;
        owner.Disposed += (_, _) => Dispose();
        owner.VisibleChanged += (_, _) => { if (!owner.Visible) Complete(); };
        owner.HandleCreated += (_, _) => BindHost();
        owner.ParentChanged += (_, _) => BindHost();
    }

    void BindHost()
    {
        var form = owner.FindForm();
        if (ReferenceEquals(form, host)) return;
        if (host is not null) host.VisibleChanged -= HostVisibilityChanged;
        host = form;
        if (host is not null) host.VisibleChanged += HostVisibilityChanged;
    }

    void HostVisibilityChanged(object? sender, EventArgs e) { if (host?.Visible != true) Complete(); }

    public void To(float next, float? start = null)
    {
        if (start.HasValue && !Running) Value = start.Value;
        if (Math.Abs(target - next) < .0001f && (Running || Math.Abs(Value - next) < .0001f)) return;
        from = Value; target = next;
        if (!owner.IsHandleCreated || !owner.Visible || !SystemInformation.IsMenuAnimationEnabled || SystemInformation.HighContrast)
        { Complete(); return; }
        timer ??= CreateTimer(); clock.Restart(); timer.Start();
    }

    public void Snap(float next) { target = next; Complete(); }

    System.Windows.Forms.Timer CreateTimer()
    {
        var result = new System.Windows.Forms.Timer { Interval = 15 };
        result.Tick += (_, _) =>
        {
            if (owner.IsDisposed || !owner.Visible || host?.Visible == false) { Complete(); return; }
            float t = Math.Min(1, (float)clock.Elapsed.TotalMilliseconds / 180);
            float eased = 1 - MathF.Pow(1 - t, 3);
            Value = from + (target - from) * eased;
            if (t >= 1) { Value = target; result.Stop(); }
            if (!owner.IsDisposed) owner.Invalidate();
        };
        return result;
    }

    void Complete() { timer?.Stop(); Value = target; if (!owner.IsDisposed) owner.Invalidate(); }
    public void Dispose() { timer?.Dispose(); timer = null; if (host is not null) host.VisibleChanged -= HostVisibilityChanged; host = null; }
}
