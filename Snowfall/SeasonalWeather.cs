namespace QuietSnow;

internal sealed partial class SnowApp
{
    WeatherMode weatherMode;
    int effectOpacity = 100;
    bool meltSnow, powerSaving = true;
    readonly WeatherArt weatherArt = new();
    public WeatherMode Weather => weatherMode;
    public int EffectOpacity => effectOpacity;
    internal Bitmap WeatherAtlas => weatherArt.Atlas;
    internal float RainSlant => Math.Clamp(flowWind * .004f, -.5f, .5f);
    internal bool LowPowerMode => powerSaving && desktopActivity?.OnBatteryOrSaver == true;
    internal int EffectiveFrameLimit => GameModeActive || LowPowerMode ? Math.Min(30, frameLimit) : frameLimit;

    void RefreshDesktopActivity()
    {
        previous = 0; lastScan = -10; nextInputUpdate = 0; previousPointer = null;
        if (desktopActivity?.Unavailable == true)
        {
            timer.Stop();
            foreach (var input in inputOverlays) input.FinishDrag();
            RefreshInputRegion();
            foreach (var overlay in overlays) overlay.Hide();
        }
        else if (!diagnostics && settings is { IsHandleCreated: true, IsDisposed: false })
        {
            Tick(); timer.Start(settings);
        }
    }

    public void SetWeatherMode(WeatherMode mode)
    {
        mode = Enum.IsDefined(mode) ? mode : WeatherMode.Snow;
        if (weatherMode == mode) return;
        CancelBallDrag();
        clearProgress = -1;
        balls.Clear();
        foreach (var surface in ordered) { Array.Clear(surface.Snow); Array.Clear(surface.NewLedge); surface.Age = 0; }
        flakes.Clear(); dust.Clear();
        weatherMode = mode;
        previousPointer = null; nextInputUpdate = 0;
        InvalidateInputRegion(); RefreshInputRegion(); UpdateMenu();
        foreach (var overlay in overlays) if (overlay.Visible) overlay.RenderFrame();
    }

    public void SetEffectOpacity(int value)
    {
        effectOpacity = Math.Clamp(value, 10, 100);
        UpdateMenu();
        foreach (var overlay in overlays) if (overlay.Visible) overlay.RenderFrame();
    }

    void ConfigureSeasonalFlake(Flake flake)
    {
        float depth = (float)random.NextDouble();
        flake.Angle = (float)random.NextDouble() * MathF.Tau;
        flake.Variant = random.Next(4) + (weatherMode == WeatherMode.CherryBlossom ? 4 : 0);
        if (weatherMode == WeatherMode.Rain)
        {
            flake.Speed = 520 + depth * 420;
            flake.Size = 9 + depth * 12;
            flake.Opacity = .22f + depth * .35f;
            flake.SwayWidth = 0;
        }
        else
        {
            flake.Speed = 24 + depth * 38;
            flake.Size = weatherMode == WeatherMode.AutumnLeaves ? 9 + depth * 8 : 6 + depth * 6;
            flake.Opacity = .75f + depth * .25f;
            flake.SwayWidth = 18 + depth * 20;
        }
    }

    void UpdateSeasonalWeather(float dt)
    {
        float area = (float)Math.Clamp(screenPixels / (1920 * 1080), .5, 4);
        int target = Math.Clamp((int)((weatherMode == WeatherMode.Rain ? 165 : 70) * snowAmount / 100f * area), 20, 640);
        if (GameModeActive) target = Math.Max(12, target / 3);
        if (LowPowerMode) target = Math.Max(12, target * 3 / 4);
        while (flakes.Count < target) flakes.Add(NewFlake());
        float blend = 1 - MathF.Exp(-2.8f * dt);
        flowSpeed += (fallSpeed / 100f - flowSpeed) * blend;
        flowWind += (wind * 65 - flowWind) * blend;
        bool rain = weatherMode == WeatherMode.Rain;
        if (!rain && snowbanksEnabled)
            foreach (var surface in ordered) if (CanAccumulateOn(surface)) surface.Age = Math.Min(75, surface.Age + dt * .55f);
        for (int i = flakes.Count - 1; i >= 0; i--)
        {
            var flake = flakes[i];
            flake.Fade = Math.Clamp(flake.Fade + dt * (i < target ? 2.4f : -1.8f), 0, 1);
            if (i >= target && flake.Fade == 0) { flakes.RemoveAt(i); continue; }
            float oldY = flake.Y;
            flake.Phase = (flake.Phase + dt * flake.SwayRate) % MathF.Tau;
            flake.Angle = (flake.Angle + dt * (.6f + MathF.Sin(flake.Phase) * 1.7f)) % MathF.Tau;
            flake.X += (flowWind + MathF.Sin(flake.Phase) * flake.SwayWidth) * dt;
            flake.Y += flake.Speed * flowSpeed * (rain ? 1 : .8f + .2f * MathF.Cos(flake.Phase)) * dt;
            if (!rain)
            {
                float x = flake.X - cursorPoint.X, y = flake.Y - cursorPoint.Y;
                float nearby = Math.Max(0, 1 - (x * x + y * y) / 3600);
                flake.X += cursorWind.X * nearby * dt;
                flake.Y += cursorWind.Y * nearby * dt;
            }
            if (flake.X < flake.Region.Left - 24) flake.X = flake.Region.Right + 23;
            if (flake.X > flake.Region.Right + 24) flake.X = flake.Region.Left - 23;
            float contactY = GroundAt(flake.X, flake.Y);
            Surface? landing = null;
            if (snowbanksEnabled && clearProgress < 0)
            foreach (var surface in ordered)
            {
                var bounds = surface.Bounds;
                if (!CanAccumulateOn(surface) || flake.X < bounds.Left || flake.X >= bounds.Right || oldY > bounds.Top) continue;
                float top = rain ? bounds.Top : SnowTop(surface, flake.X);
                if (oldY > top || flake.Y < top || top > contactY) continue;
                if ((rain || !SnowbankOnTop(surface)) && !IsExposed(surface, flake.X)) continue;
                landing = surface; contactY = top;
            }
            if (flake.Y < contactY) continue;
            if (rain) RainSplash(flake.X, contactY, landing?.Handle ?? 0);
            else if (landing is not null)
            {
                int column = Math.Clamp((int)((flake.X - landing.Bounds.Left) / 12), 0, landing.Snow.Length - 1);
                // One landing must be visible even in the smallest pile.
                landing.Snow[column] = Math.Min(18 * landing.SnowScale, landing.Snow[column] + 6.5f * landing.SnowScale);
            }
            ResetFlake(flake);
        }
        if (!rain) { RelaxDrifts(dt); ApplyMelting(dt); }
    }

    void ConfigureSeasonalGrain(ref SnowDust grain)
    {
        if (weatherMode is not (WeatherMode.AutumnLeaves or WeatherMode.CherryBlossom)) return;
        if (grain.Mass <= 0) grain.Mass = grain.Size * .22f * BankScale;
        grain.Size = weatherMode == WeatherMode.AutumnLeaves ? 10 + (float)random.NextDouble() * 7 : 7 + (float)random.NextDouble() * 5;
        grain.Variant = random.Next(4) + (weatherMode == WeatherMode.CherryBlossom ? 4 : 0);
        grain.Angle = (float)random.NextDouble() * MathF.Tau;
        grain.Spin = ((float)random.NextDouble() - .5f) * 4;
        grain.Phase = (float)random.NextDouble() * MathF.Tau;
    }

    void RainSplash(float x, float y, nint source)
    {
        for (int i = 0; i < 3 && dust.Count < 384; i++)
            dust.Add(new SnowDust { X = x, Y = y - 1, Vx = (i - 1) * (30 + random.Next(35)),
                Vy = -45 - random.Next(60), Size = 1.2f + (float)random.NextDouble(), Life = .24f + (float)random.NextDouble() * .16f, Source = source });
    }

    void ApplyMelting(float dt)
    {
        if (!meltSnow || !snowbanksEnabled || clearProgress >= 0) return;
        float fraction = 1 - MathF.Exp(-dt * .025f);
        foreach (var surface in ordered)
        for (int i = 0; i < surface.Snow.Length; i++)
        {
            float height = Math.Max(0, surface.BaseHeight * surface.SnowScale * SnowRipples[i] + surface.Snow[i]);
            surface.Snow[i] -= height * fraction;
        }
    }

    void PaintSeasonalPile(Graphics g, Surface surface)
    {
        if (weatherMode == WeatherMode.Rain) return;
        var state = g.Save();
        g.IntersectClip(new Rectangle(surface.Bounds.Left, surface.Bounds.Top - 48, surface.Bounds.Width, 49));
        int offset = weatherMode == WeatherMode.CherryBlossom ? 4 : 0;
        for (int i = 0; i < surface.Snow.Length; i++)
        {
            float x = Math.Min(surface.Bounds.Right - 3, surface.Bounds.Left + i * 12 + 3);
            int height = SeasonalPileHeight(surface, i);
            for (int row = 0; row < Math.Min(5, (height + 5) / 6); row++)
            {
                int hash = (i * 73 + row * 37) & 255;
                float y = surface.Bounds.Top - Math.Min(height - 1, 2 + row * 6);
                weatherArt.DrawPile(g, x + (hash % 7 - 3), y, offset + hash % 4, hash);
            }
        }
        g.Restore(state);
    }

    static int SeasonalPileHeight(Surface surface, int column)
    {
        float x = Math.Min(surface.Bounds.Right - 3, surface.Bounds.Left + column * 12 + 3);
        return (int)MathF.Floor((surface.Bounds.Top - SnowTop(surface, x)) / 2) * 2;
    }
}
