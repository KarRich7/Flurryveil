namespace QuietSnow;

internal sealed partial class SnowApp
{
    HashSet<string> hiddenApps = new(StringComparer.OrdinalIgnoreCase);
    bool hiddenByApplication, hideFullscreen = true;
    string? previousPauseReason;

    internal static string NormalizeApplication(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        string name = Path.GetFileName(value.Trim().Trim('"').Trim());
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "" : name.ToLowerInvariant();
    }

    internal void SetHiddenApplications(string[] names)
    {
        hiddenApps = new(names.Select(NormalizeApplication).Where(name => name.Length > 0), StringComparer.OrdinalIgnoreCase);
        nextGameCheck = 0; hiddenByApplication = hiddenApps.Contains(games.ForegroundName);
        UpdateMenu();
    }

    void UpdateAutomaticPauseStatus()
    {
        string? reason = hiddenByApplication ? $"Автопауза · {games.ForegroundName}" :
            hideFullscreen && fullscreen ? "Автопауза · полный экран" : GameModeActive && gameAction == 1 ? "Автопауза · игра" : null;
        if (reason == previousPauseReason) return;
        previousPauseReason = reason; settings?.SetAutomaticPauseReason(reason);
    }
}
