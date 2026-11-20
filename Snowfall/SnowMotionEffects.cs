namespace QuietSnow;

internal sealed partial class SnowApp
{
    const int DustLimit = 900;
    float clearProgress = -1;
    Point? previousPointer;
    PointF cursorWind;
    Point cursorPoint;
    Surface? tabDrag;
    Point tabStart;
    bool wasPointerPressed;
    readonly float[] oldLedgeHeights = new float[Surface.MaxColumns];
    readonly bool[] oldLedgeRefill = new bool[Surface.MaxColumns];

    void StartClear()
    {
        CancelBallDrag();
        if (EffectsHidden) FinishClear();
        else clearProgress = 0;
        RefreshInputRegion();
    }

    void FinishClear()
    {
        foreach (var surface in ordered)
        {
            Array.Clear(surface.Snow);
            Array.Clear(surface.NewLedge);
            surface.Age = 0;
        }
        balls.Clear();
        MouseUp();
        clearProgress = -1;
    }

    void AdvanceClear(float dt)
    {
        if (clearProgress < 0) return;
        float oldFront = desktop.Left + desktop.Width * clearProgress;
        clearProgress = Math.Min(1, clearProgress + dt / .72f);
        float front = desktop.Left + desktop.Width * clearProgress;
        foreach (var surface in ordered)
        {
            if (surface.Bounds.IsEmpty || surface.Bounds.Left > front || surface.Bounds.Right < oldFront) continue;
            int first = Math.Clamp((int)MathF.Ceiling((oldFront - surface.Bounds.Left) / 12), 0, surface.Snow.Length);
            int end = Math.Min(surface.Snow.Length, (int)((front - surface.Bounds.Left) / 12) + 1);
            for (int i = first; i < end; i++) ReleaseSnow(surface, i, 1, 145, -45, false);
        }
        for (int i = balls.Count - 1; i >= 0; i--)
            if (balls[i].X <= front) BreakBall(i, 135);
        foreach (var flake in flakes)
            if (flake.Y >= flake.Region.Top && flake.X >= oldFront && flake.X <= front) ResetFlake(flake);
        if (clearProgress >= 1) FinishClear();
    }

    public bool RemoveBall(Point point)
    {
        if (EffectsHidden || clearProgress >= 0) return false;
        for (int i = balls.Count - 1; i >= 0; i--)
        {
            var ball = balls[i];
            float dx = point.X - ball.X, dy = point.Y - ball.Y;
            if (dx * dx + dy * dy > ball.R * ball.R || !BallVisibleAt(ball, point)) continue;
            var remainingHead = ball.BaseBall;
            BreakBall(i, 24);
            foreach (var part in balls)
                if (part.BaseBall == ball) part.BaseBall = null;
            if (remainingHead is not null && IsSnowmanHead(remainingHead)) BeginSnowmanAppearance(remainingHead);
            RefreshInputRegion();
            return true;
        }
        return false;
    }

    void BreakBall(int index, float velocity)
    {
        var ball = balls[index];
        balls.RemoveAt(index);
        if (ReferenceEquals(held, ball)) CancelBallDrag();
        int fragments = Math.Clamp((int)(ball.R * 1.25f), 12, 36);
        for (int j = 0; j < fragments; j++)
        {
            float angle = (float)random.NextDouble() * MathF.Tau;
            float extent = (float)random.NextDouble() * ball.R * .9f;
            float dx = MathF.Cos(angle), dy = MathF.Sin(angle);
            AddSnowGrain(ball.X + extent * dx, ball.Y + extent * dy, velocity + dx * 95,
                -45 + dy * 75, 2.2f + (float)random.NextDouble() * 3.5f, 0, false);
        }
    }

    void TrackCursor(Point point, bool pressed, float dt)
    {
        cursorPoint = point;
        if (previousPointer is not Point last)
        {
            previousPointer = point;
            wasPointerPressed = pressed;
            return;
        }
        previousPointer = point;
        int dx = point.X - last.X, dy = point.Y - last.Y;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        float speed = distance / Math.Max(.01f, dt);
        if (distance > 200)
        {
            cursorWind = PointF.Empty;
            tabDrag = null;
            wasPointerPressed = pressed;
            return;
        }

        cursorWind = new PointF(Math.Clamp(dx / Math.Max(dt, .01f) * .19f, -100, 100),
            Math.Clamp(dy / Math.Max(dt, .01f) * .13f, -45, 45));
        if (pressed && !wasPointerPressed)
        {
            tabStart = point;
            tabDrag = held is null ? ordered.FirstOrDefault(surface => surface.IsBrowser &&
                surface.Bounds.Contains(point) && point.Y <= surface.Bounds.Top + 38 && IsExposed(surface, point.X)) : null;
        }
        if (!pressed) tabDrag = null;
        wasPointerPressed = pressed;

        if (settings is { Visible: true } && (settings.Bounds.Contains(point) ||
            settings.OwnedForms.Any(form => form.Visible && form.Bounds.Contains(point)))) return;

        if (pressed && tabDrag is { } browser && speed > 180 && Math.Abs(point.X - tabStart.X) > 8 &&
            browser.Bounds.Contains(point) && point.Y <= browser.Bounds.Top + 45 &&
            clock.Elapsed.TotalSeconds - lastWindowMotion > .08)
            StirTabSnow(browser, point.X, dx / Math.Max(.01f, dt));

        // The snow can be picked up after a slow approach. A quick sideways
        // stroke moves the drift without taking ordinary window clicks.
        if (!pressed && speed >= 210 && Math.Abs(dx) >= Math.Abs(dy) * .65f)
        {
            int steps = Math.Clamp((int)MathF.Ceiling(distance / 9), 1, 20);
            for (int i = 1; i <= steps; i++)
            {
                var at = new Point(last.X + dx * i / steps, last.Y + dy * i / steps);
                PushDrift(at, Math.Sign(dx), Math.Clamp(speed * .18f, 25, 150));
            }
        }
    }

    // Move material ahead of the pointer instead of deleting it. The visible
    // contour and mouse targets both use these same column heights.
    internal float PushDrift(Point point, int direction, float velocity)
    {
        if (!snowbanksEnabled || EffectsHidden || clearProgress >= 0 || direction == 0) return 0;
        foreach (var surface in ordered)
        {
            var bounds = surface.Bounds;
            if (!CanAccumulateOn(surface) || point.X < bounds.Left || point.X >= bounds.Right ||
                point.Y < SnowTop(surface, point.X) - 10 || point.Y > bounds.Top + 5 ||
                !IsExposed(surface, point.X, point.Y)) continue;
            int center = Math.Clamp((point.X - bounds.Left) / 12, 0, surface.Snow.Length - 1);
            float moved = 0;
            for (int offset = -1; offset <= 1; offset++)
            {
                int column = center + offset;
                if ((uint)column >= surface.Snow.Length) continue;
                float height = Math.Max(0, surface.BaseHeight * surface.SnowScale * SnowRipples[column] + surface.Snow[column]);
                float amount = height * (offset == 0 ? .72f : .32f);
                surface.Snow[column] -= amount;
                moved += amount;
            }
            float spray = dust.Count < DustLimit - 10 ? moved * Math.Clamp((velocity - 30) / 250, .06f, .48f) : 0;
            if (spray <= .05f) spray = 0;
            if (spray > .05f)
            {
                int grains = weatherMode is WeatherMode.AutumnLeaves or WeatherMode.CherryBlossom
                    ? Math.Clamp((int)MathF.Ceiling(spray / 3), 1, 4) : Math.Clamp((int)MathF.Ceiling(spray), 2, 8);
                for (int i = 0; i < grains; i++)
                {
                    var grain = new SnowDust { X = point.X + direction * 10 + random.Next(-5, 6),
                        Y = Math.Min(point.Y, bounds.Top - 3), Vx = direction * velocity + random.Next(-25, 26),
                        Vy = -35 - random.Next(65), Size = 1.8f + (float)random.NextDouble() * 2,
                        Life = 30, Settles = true, Source = surface.Handle, Mass = spray / grains };
                    ConfigureSeasonalGrain(ref grain); dust.Add(grain);
                }
            }
            float remaining = moved - spray;
            for (int step = 2; step <= 6 && remaining > .001f; step++)
            {
                int column = center + direction * step;
                if ((uint)column >= surface.Snow.Length) break;
                float height = Math.Max(0, surface.BaseHeight * surface.SnowScale * SnowRipples[column] + surface.Snow[column]);
                float capacity = Math.Max(0, (40 - (step - 2) * 4) * surface.SnowScale - height);
                float placed = Math.Min(remaining, capacity);
                surface.Snow[column] += placed;
                remaining -= placed;
            }
            if (remaining > .1f)
            {
                float edge = direction > 0 ? bounds.Right - 1 : bounds.Left;
                for (int i = 0; i < Math.Clamp((int)MathF.Ceiling(remaining), 2, 16); i++)
                    AddSnowGrain(edge, bounds.Top - 6, direction * velocity, -25 - random.Next(30),
                        2 + (float)random.NextDouble() * 3, surface.Handle, true);
            }
            return moved;
        }
        return 0;
    }

    void RelaxDrifts(float dt)
    {
        float rate = Math.Min(.45f, dt * 5);
        foreach (var surface in ordered)
        for (int i = 0; i + 1 < surface.Snow.Length; i++)
        {
            float a = Math.Max(0, surface.BaseHeight * surface.SnowScale * SnowRipples[i] + surface.Snow[i]);
            float b = Math.Max(0, surface.BaseHeight * surface.SnowScale * SnowRipples[i + 1] + surface.Snow[i + 1]);
            float excess = Math.Max(0, Math.Abs(a - b) - 7 * surface.SnowScale);
            float transfer = MathF.CopySign(excess * rate, a - b);
            surface.Snow[i] -= transfer;
            surface.Snow[i + 1] += transfer;
        }
    }

    void StirTabSnow(Surface surface, int x, float velocity)
    {
        if (!snowbanksEnabled || !CanAccumulateOn(surface) || surface.Snow.Length == 0 || !IsExposed(surface, x)) return;
        int column = Math.Clamp((x - surface.Bounds.Left) / 12, 0, surface.Snow.Length - 1);
        ReleaseSnow(surface, column, .33f, Math.Clamp(velocity * .28f, -170, 170), -42, true);
    }

    void MoveSnowSurface(Surface surface, Rectangle next, double now)
    {
        Rectangle before = surface.Bounds;
        if (before == next) return;
        if (next.IsEmpty)
        {
            if (!before.IsEmpty && snowbanksEnabled && !EffectsHidden)
                for (int i = 0; i < surface.Snow.Length; i += Math.Max(1, surface.Snow.Length / 22))
                    ReleaseSnow(surface, i, 1, 0, 30, true);
            surface.Bounds = Rectangle.Empty;
            surface.NativeBounds = Rectangle.Empty;
            return;
        }
        if (before.IsEmpty)
        {
            surface.Resize(next);
            return;
        }
        lastWindowMotion = now;
        if (!EffectsHidden && ShouldDrawGarland(surface))
        {
            float shiftX = (next.Left - before.Left + next.Right - before.Right) * .5f;
            float shiftY = (next.Top - before.Top + next.Bottom - before.Bottom) * .5f;
            float squeeze = Math.Max(0, before.Width - next.Width) + Math.Max(0, before.Height - next.Height);
            surface.GarlandVx = Math.Clamp(surface.GarlandVx - shiftX * 6, -115, 115);
            surface.GarlandVy = Math.Clamp(surface.GarlandVy + (Math.Abs(shiftX) + squeeze) * 2.8f - shiftY * 5, -115, 115);
        }
        if (before.Size != next.Size)
        {
            ResizeSnowSurface(surface, before, next, now);
            return;
        }
        surface.Resize(next);
        int dx = next.Left - before.Left, dy = next.Top - before.Top;
        int travel = Math.Abs(dx) + Math.Abs(dy);
        if (!snowbanksEnabled || EffectsHidden || clearProgress >= 0 || travel < 3 ||
            now - surface.LastSnowRelease < .045 || !next.IntersectsWith(desktop)) return;

        surface.LastSnowRelease = now;
        int count = Math.Min(surface.Snow.Length, Math.Clamp(travel / 3 + 2, 3, 16));
        float velocity = Math.Clamp(dx * 22, -230, 230);
        float lift = Math.Clamp(dy * 10 - 55, -135, 65);
        int side = dx < 0 ? surface.Snow.Length - 1 : 0;
        for (int j = 0; j < count; j++)
        {
            int column = j < count / 2 ? Math.Clamp(side + (dx < 0 ? -j : j), 0, surface.Snow.Length - 1)
                : random.Next(surface.Snow.Length);
            ReleaseSnow(surface, column, Math.Clamp(.28f + travel * .015f, .3f, .85f), velocity, lift, true);
        }
    }

    static void AdvanceGarland(Surface surface, float dt)
    {
        Advance(ref surface.GarlandX, ref surface.GarlandVx, dt);
        Advance(ref surface.GarlandY, ref surface.GarlandVy, dt);

        static void Advance(ref float offset, ref float speed, float dt)
        {
            if (offset == 0 && speed == 0) return;
            // Closed-form damped spring, stable even on the slower game-mode clock.
            const float decay = 7, frequency = 15;
            float impulse = (speed + decay * offset) / frequency;
            float sin = MathF.Sin(frequency * dt), cos = MathF.Cos(frequency * dt);
            float damp = MathF.Exp(-decay * dt);
            float next = damp * (offset * cos + impulse * sin);
            speed = damp * frequency * (impulse * cos - offset * sin) - decay * next;
            offset = Math.Clamp(next, -9, 9);
            if (Math.Abs(offset) < .025f && Math.Abs(speed) < .25f) offset = speed = 0;
        }
    }

    void ResizeSnowSurface(Surface surface, Rectangle before, Rectangle next, double now)
    {
        bool animate = snowbanksEnabled && !EffectsHidden && clearProgress < 0 && (surface.Handle.ToInt64() < 0 || HasRoomAbove(next));
        bool clipped = next.Left > before.Left || next.Right < before.Right;
        if (before.Width != next.Width)
        {
            int oldCount = surface.Snow.Length;
            float baseHeight = surface.BaseHeight * surface.SnowScale;
            for (int i = 0; i < oldCount; i++)
            {
                oldLedgeHeights[i] = Math.Max(0, baseHeight * SnowRipples[i] + surface.Snow[i]);
                oldLedgeRefill[i] = surface.NewLedge[i];
            }
            if (animate && clipped) SpillResizedEdge(surface, before, next, oldCount);

            surface.Resize(next);
            for (int i = 0; i < surface.Snow.Length; i++)
            {
                int x = Math.Min(next.Right - 1, next.Left + i * 12);
                float height = 0;
                bool refilling = x < before.Left || x >= before.Right;
                if (!refilling)
                {
                    float source = Math.Min(oldCount - 1, (x - before.Left) / 12f);
                    int left = (int)source;
                    int right = Math.Min(left + 1, oldCount - 1);
                    height = oldLedgeHeights[left] +
                        (oldLedgeHeights[right] - oldLedgeHeights[left]) * (source - left);
                    refilling = oldLedgeRefill[(int)MathF.Round(source)];
                }
                surface.Snow[i] = height - baseHeight * SnowRipples[i];
                surface.NewLedge[i] = refilling;
            }

            // Resizing the left edge does not drag a snowball sideways.
            foreach (var ball in balls)
                if (ball.Support == surface.Handle) ball.SupportX += before.Left - next.Left;
        }
        else
        {
            surface.Resize(next);
        }

        if (!animate || clipped)
        {
            surface.ResizeTravel = 0;
            return;
        }
        surface.ResizeTravel = Math.Min(80, surface.ResizeTravel + Math.Abs(next.Width - before.Width) + Math.Abs(next.Height - before.Height));
        if (surface.ResizeTravel < 3 || now - surface.LastSnowRelease < .055) return;

        // Widening or a bottom-edge resize still shakes the existing ledge.
        int travel = surface.ResizeTravel;
        surface.ResizeTravel = 0;
        surface.LastSnowRelease = now;
        int count = Math.Min(surface.Snow.Length, Math.Clamp(travel / 3 + 2, 3, 9));
        int direction = Math.Sign((next.Right - before.Right) + (next.Left - before.Left));
        for (int i = 0; i < count; i++)
            ReleaseSnow(surface, random.Next(surface.Snow.Length), .22f + Math.Min(travel, 25) * .008f,
                direction * 52, -36, true);
    }

    void SpillResizedEdge(Surface surface, Rectangle before, Rectangle next, int columnCount)
    {
        int budget = 26;
        for (int i = 0; i < columnCount && budget > 0; i++)
        {
            float x = before.Left + i * 12;
            float start = Math.Max(before.Left, x - 6);
            float end = i == columnCount - 1 ? before.Right : Math.Min(before.Right, x + 6);
            float left = Math.Max(0, Math.Min(end, next.Left) - start);
            float right = Math.Max(0, end - Math.Max(start, next.Right));
            float amount = oldLedgeHeights[i] * Math.Min(12, left + right) / 12;
            if (amount < .24f) continue;
            int direction = left > right ? -1 : 1;
            float origin = direction < 0 ? start + left / 2 : end - right / 2;
            if (!IsExposed(surface, origin)) continue;
            int grains = Math.Min(budget, Math.Clamp((int)MathF.Ceiling(amount), 1, 5));
            for (int j = 0; j < grains; j++)
                AddSnowGrain(origin + random.Next(-2, 3), before.Top - oldLedgeHeights[i],
                    direction * random.Next(18, 72), random.Next(-16, 41),
                    2.2f + (float)random.NextDouble() * Math.Min(4, amount + 1), surface.Handle, true);
            budget -= grains;
        }
    }

    float ReleaseSnow(Surface surface, int column, float fraction, float velocityX, float velocityY, bool settles)
    {
        if (!CanAccumulateOn(surface)) return 0;
        float baseHeight = surface.BaseHeight * surface.SnowScale * SnowRipples[column];
        float height = Math.Max(0, baseHeight + surface.Snow[column]);
        float amount = height * fraction;
        if (settles && dust.Count >= DustLimit - 5) return 0;
        if (amount <= 0) return 0;
        surface.Snow[column] -= amount;
        if (amount < .3f) return amount;
        float x = Math.Min(surface.Bounds.Right - 1, surface.Bounds.Left + column * 12);
        if (!SnowbankOnTop(surface) && !IsExposed(surface, x, surface.Bounds.Top - height)) return amount;
        int grains = weatherMode is WeatherMode.AutumnLeaves or WeatherMode.CherryBlossom
            ? Math.Clamp((int)MathF.Ceiling(amount / 3), 1, 3) : Math.Clamp((int)MathF.Ceiling(amount * 1.25f), 1, 5);
        for (int i = 0; i < grains; i++)
            AddSnowGrain(x + random.Next(-4, 5), surface.Bounds.Top - height + random.Next(-2, 3),
                velocityX + random.Next(-45, 46), velocityY + random.Next(-38, 30),
                1.8f + (float)random.NextDouble() * Math.Min(4, amount + 1), surface.Handle, settles);
        return amount;
    }

    void AddSnowGrain(float x, float y, float vx, float vy, float size, nint source, bool settles)
    {
        if (dust.Count >= DustLimit) return;
        var grain = new SnowDust
        {
            X = x, Y = y, Vx = vx, Vy = vy, Size = size, Source = source, Settles = settles,
            Life = (settles ? 30f : .7f) + (float)random.NextDouble() * .6f
        };
        ConfigureSeasonalGrain(ref grain);
        dust.Add(grain);
    }
}
