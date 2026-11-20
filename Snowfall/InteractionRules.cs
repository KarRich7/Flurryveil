namespace QuietSnow;

internal sealed partial class SnowApp
{
    internal const float MaximumBallRadius = 84;
    internal bool CleanerActive => SupportsSnowballs && !GameModeActive;
    bool SupportsSnowballs => weatherMode == WeatherMode.Snow;

    bool CanAccumulateOn(Surface surface)
    {
        if (surface.Bounds.IsEmpty) return false;
        if (surface.Handle.ToInt64() < 0) return true;
        return HasRoomAbove(surface.Bounds);
    }

    bool HasRoomAbove(Rectangle bounds)
    {
        foreach (var monitor in monitorBounds)
            if (bounds.Right > monitor.Left && bounds.Left < monitor.Right &&
                bounds.Top >= monitor.Top + 12 && bounds.Top < monitor.Bottom) return true;
        return false;
    }

    static float BallHitRadius(Ball ball) => ball.R + 4;

    bool IsSnowmanHead(Ball ball) =>
        !ball.Held && ball.R >= 9 && ball.BaseBall is { Held: false } && StackHeight(ball) <= 3 && !HasBallAbove(ball);

    static void BeginSnowmanAppearance(Ball ball)
    {
        ball.SnowmanAppearance = SystemInformation.IsMenuAnimationEnabled && !SystemInformation.HighContrast ? 0 : 1;
    }

    bool BallVisibleAt(Ball ball, Point point)
    {
        if (ball.Held || snowmenOnTop) return true;
        foreach (var window in ordered)
        {
            if (window.Handle == ball.Support) break;
            if (window.Handle.ToInt64() >= 0 && window.Bounds.Contains(point)) return false;
        }
        return true;
    }

    Ball? PickableBall(Point point)
    {
        for (int i = balls.Count - 1; i >= 0; i--)
        {
            var ball = balls[i]; float x = point.X - ball.X, y = point.Y - ball.Y, radius = BallHitRadius(ball);
            if (x * x + y * y <= radius * radius && BallVisibleAt(ball, point)) return ball;
        }
        return null;
    }

    bool SnowAt(Point point)
    {
        if (!snowbanksEnabled) return false;
        foreach (var surface in ordered)
        {
            var bounds = surface.Bounds;
            if (!CanAccumulateOn(surface) || surface.Age < .2f || point.X < bounds.Left || point.X >= bounds.Right ||
                point.Y >= bounds.Top || point.Y < SnowTop(surface, point.X) - 1) continue;
            if (SnowbankOnTop(surface) || IsExposed(surface, point.X, point.Y)) return true;
        }
        return false;
    }

    bool IsSettingsPoint(Point point) => settings is { Visible: true, WindowState: not FormWindowState.Minimized } &&
        (settings.Bounds.Contains(point) || settings.OwnedForms.Any(popup => popup.Visible && popup.Bounds.Contains(point)));

    float ClearBallsInStrip(RectangleF strip)
    {
        float collected = 0;
        for (int i = balls.Count - 1; i >= 0; i--)
        {
            var root = balls[i];
            if (root.Held || root.BaseBall is not null || root.X + root.R < strip.Left || root.X - root.R > strip.Right) continue;
            var support = FindBallSupport(root.Support);
            bool ground = support is not null && support.Handle.ToInt64() < 0 && Math.Abs(support.Bounds.Top - strip.Top) < 3;
            if (!ground && !(root.Support == 0 && root.Vy == 0 && Math.Abs(root.Y + root.R - strip.Top) < 3)) continue;
            for (int j = balls.Count - 1; j >= 0; j--)
            {
                var part = balls[j]; var ancestor = part;
                while (ancestor.BaseBall is { } below) ancestor = below;
                if (!ReferenceEquals(ancestor, root) || part.Held) continue;
                collected += MathF.PI * part.R * part.R * .35f;
                BreakBall(j, 70);
            }
            i = Math.Min(i, balls.Count);
        }
        return collected;
    }
}
