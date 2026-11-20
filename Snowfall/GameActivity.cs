using System.Diagnostics;
using System.Runtime.InteropServices;

namespace QuietSnow;

// Queries only the active process. No hooks or modules are injected into games.
internal sealed class GameActivity
{
    uint previousProcess;
    bool knownGame;
    string processName = "";
    public string ForegroundName { get; private set; } = "";
    static readonly HashSet<string> Games = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs2", "csgo", "dota2", "fc24", "fc25", "fc26", "fc27", "fifa23",
        "valorant-win64-shipping", "fortniteclient-win64-shipping", "r5apex",
        "pubg", "tslgame", "overwatch", "rocketleague", "gta5", "gta5_enhanced",
        "rdr2", "cyberpunk2077", "eldenring", "witcher3", "minecraft.windows",
        "robloxplayerbeta", "worldoftanks", "aces", "warframe.x64", "destiny2",
        "hl2", "terraria", "stardew valley", "eurotrucks2", "league of legends"
    };
    public bool IsActive()
    {
        var foreground = Native.GetForegroundWindow();
        ForegroundName = "";
        if (foreground == 0) return false;
        Native.GetWindowThreadProcessId(foreground, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return false;
        if (pid != previousProcess)
        {
            previousProcess = pid; knownGame = false; processName = "";
            try
            {
                using var process = Process.GetProcessById((int)pid);
                string name = process.ProcessName;
                processName = name;
                string? path = null;
                if (!Games.Contains(name))
                    try { path = process.MainModule?.FileName; } catch (System.ComponentModel.Win32Exception) { }
                knownGame = MatchesGame(name, path);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        ForegroundName = processName;
        // Microsoft documents state 3 as an exclusive Direct3D fullscreen app.
        return knownGame || (SHQueryUserNotificationState(out int state) == 0 && state == 3);
    }
    internal static bool MatchesGame(string name, string? path)
    {
        if (Games.Contains(name) || name.EndsWith("-win64-shipping", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("launcher", StringComparison.OrdinalIgnoreCase) || name.Contains("crash", StringComparison.OrdinalIgnoreCase) || name.Contains("helper", StringComparison.OrdinalIgnoreCase)) return false;
        string normalized = (path ?? "").Replace('/', '\\');
        return normalized.Contains("\\steamapps\\common\\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("\\Epic Games\\", StringComparison.OrdinalIgnoreCase) && !normalized.Contains("\\Launcher\\", StringComparison.OrdinalIgnoreCase);
    }
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
}
