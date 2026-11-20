using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace QuietSnow;

internal readonly record struct PersonalizationResult(bool Success, bool Cancelled = false, string? Error = null)
{
    public static PersonalizationResult Ok() => new(true);
    public static PersonalizationResult Cancel() => new(false, true);
    public static PersonalizationResult Fail(string error) => new(false, false, error);
}

/// <summary>Explicit user actions only; constructing/reading this object changes no Windows settings.</summary>
internal sealed class DesktopPersonalization
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "QuietSnow";
    const string DesktopKey = @"Control Panel\Desktop";
    const uint SpiGetWallpaper = 0x0073, SpiSetWallpaper = 0x0014;
    const uint UpdateProfileAndNotify = 0x0001 | 0x0002;
    static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuietSnow");
    static string WallpaperDirectory => Path.Combine(DataDirectory, "Wallpapers");
    static string HistoryPath => Path.Combine(DataDirectory, "wallpaper-history.json");

    public bool AutoStartEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return string.Equals(key?.GetValue(RunValue) as string, GetStartupCommand(), StringComparison.OrdinalIgnoreCase);
        }
    }

    public string CurrentWallpaperPath
    {
        get
        {
            var path = new StringBuilder(32768);
            if (!SystemParametersInfoRead(SpiGetWallpaper, (uint)path.Capacity, path, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows не удалось прочитать текущие обои.");
            return path.ToString();
        }
    }

    public bool CanRestoreWallpaper
    {
        get
        {
            var state = ReadHistory();
            return state.HasOriginal && (string.IsNullOrEmpty(state.OriginalPath) || File.Exists(state.OriginalPath));
        }
    }

    public PersonalizationResult SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (key is null) return PersonalizationResult.Fail("Не удалось открыть настройки автозапуска текущего пользователя.");
            if (enabled) key.SetValue(RunValue, GetStartupCommand(), RegistryValueKind.String);
            else key.DeleteValue(RunValue, false);
            return PersonalizationResult.Ok();
        }
        catch (Exception error) when (IsExpected(error))
        {
            return PersonalizationResult.Fail("Не удалось изменить автозапуск: " + error.Message);
        }
    }

    static string GetStartupCommand()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Windows не сообщила путь к приложению. Запустите Flurryveil.exe напрямую.");
        if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Для автозапуска откройте Flurryveil.exe напрямую.");
        return "\"" + path + "\" --background";
    }

    public PersonalizationResult ChooseWallpaper(IWin32Window owner)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Выберите обои рабочего стола",
            Filter = "Изображения (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|Все файлы (*.*)|*.*",
            CheckFileExists = true, Multiselect = false, RestoreDirectory = true
        };
        return dialog.ShowDialog(owner) == DialogResult.OK ? SetWallpaper(dialog.FileName) : PersonalizationResult.Cancel();
    }

    public PersonalizationResult SetWallpaper(string path)
    {
        string? managedPath = null;
        bool applied = false;
        DesktopStyle? previousStyle = null;
        bool styleAttempted = false;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return PersonalizationResult.Fail("Файл обоев не найден. Выберите существующее изображение.");
            var state = ReadHistory();
            string currentPath = CurrentWallpaperPath;
            previousStyle = ReadDesktopStyle();
            Directory.CreateDirectory(WallpaperDirectory);
            managedPath = Path.Combine(WallpaperDirectory, "wallpaper-" + Guid.NewGuid().ToString("N") + ".bmp");
            // A managed BMP remains available even if the chosen source is moved.
            // Loading/decoding here also rejects damaged/unsupported image files.
            using (var source = Image.FromFile(Path.GetFullPath(path)))
            using (var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb))
            {
                using var graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.Black);
                graphics.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                bitmap.Save(managedPath, ImageFormat.Bmp);
            }
            // Match the Fill layout used by the animated wallpaper geometry.
            // This executes only after an explicit choice in the UI.
            styleAttempted = true;
            WriteDesktopStyle(new DesktopStyle("10", "0"));
            if (!SystemParametersInfoWrite(SpiSetWallpaper, 0, managedPath, UpdateProfileAndNotify))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows не удалось установить выбранные обои.");
            applied = true;
            string oldManagedPath = state.ManagedPath;
            if (!state.HasOriginal) { state.HasOriginal = true; state.OriginalPath = currentPath; }
            if (!state.HasOriginalStyle)
            {
                state.HasOriginalStyle = true;
                state.OriginalWallpaperStyle = previousStyle.Value.WallpaperStyle;
                state.OriginalTileWallpaper = previousStyle.Value.TileWallpaper;
            }
            state.ManagedPath = managedPath;
            SaveHistory(state);
            DeleteManagedFile(oldManagedPath, state.OriginalPath);
            return PersonalizationResult.Ok();
        }
        catch (Exception error) when (IsExpected(error) || error is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            if (!applied && managedPath is not null) TryRemoveFailedConversion(managedPath);
            string rollbackError = !applied && styleAttempted && previousStyle.HasValue ? TryRestoreStyle(previousStyle.Value) : "";
            return PersonalizationResult.Fail(applied
                ? "Обои установлены, но не удалось сохранить историю восстановления: " + error.Message
                : "Не удалось установить обои: " + error.Message + rollbackError);
        }
    }

    public PersonalizationResult RestoreWallpaper()
    {
        bool restored = false;
        DesktopStyle? previousStyle = null;
        bool styleAttempted = false;
        try
        {
            var state = ReadHistory();
            if (!state.HasOriginal) return PersonalizationResult.Fail("Предыдущие обои ещё не сохранены. Сначала выберите свои обои в приложении.");
            if (!string.IsNullOrEmpty(state.OriginalPath) && !File.Exists(state.OriginalPath))
                return PersonalizationResult.Fail("Предыдущий файл обоев больше не найден: " + state.OriginalPath);
            if (state.HasOriginalStyle)
            {
                previousStyle = ReadDesktopStyle();
                styleAttempted = true;
                WriteDesktopStyle(new DesktopStyle(state.OriginalWallpaperStyle, state.OriginalTileWallpaper));
            }
            if (!SystemParametersInfoWrite(SpiSetWallpaper, 0, state.OriginalPath, UpdateProfileAndNotify))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows не удалось восстановить предыдущие обои.");
            restored = true;
            File.Delete(HistoryPath);
            DeleteManagedFile(state.ManagedPath, state.OriginalPath);
            return PersonalizationResult.Ok();
        }
        catch (Exception error) when (IsExpected(error))
        {
            string rollbackError = !restored && styleAttempted && previousStyle.HasValue ? TryRestoreStyle(previousStyle.Value) : "";
            return PersonalizationResult.Fail(restored
                ? "Обои восстановлены, но не удалось удалить историю: " + error.Message
                : "Не удалось восстановить обои: " + error.Message + rollbackError);
        }
    }

    readonly record struct DesktopStyle(string? WallpaperStyle, string? TileWallpaper);

    static DesktopStyle ReadDesktopStyle()
    {
        using var key = Registry.CurrentUser.OpenSubKey(DesktopKey, false);
        return new DesktopStyle(key?.GetValue("WallpaperStyle")?.ToString(), key?.GetValue("TileWallpaper")?.ToString());
    }

    static void WriteDesktopStyle(DesktopStyle style)
    {
        using var key = Registry.CurrentUser.CreateSubKey(DesktopKey, true)
            ?? throw new IOException("Не удалось открыть настройки размещения обоев.");
        if (style.WallpaperStyle is null) key.DeleteValue("WallpaperStyle", false);
        else key.SetValue("WallpaperStyle", style.WallpaperStyle, RegistryValueKind.String);
        if (style.TileWallpaper is null) key.DeleteValue("TileWallpaper", false);
        else key.SetValue("TileWallpaper", style.TileWallpaper, RegistryValueKind.String);
    }

    static string TryRestoreStyle(DesktopStyle style)
    {
        try { WriteDesktopStyle(style); return ""; }
        catch (Exception error) when (IsExpected(error))
        {
            return " Не удалось вернуть прежний режим размещения: " + error.Message;
        }
    }

    static WallpaperHistory ReadHistory()
    {
        if (!File.Exists(HistoryPath)) return new WallpaperHistory();
        return JsonSerializer.Deserialize<WallpaperHistory>(File.ReadAllText(HistoryPath))
            ?? throw new InvalidDataException("Файл истории обоев повреждён.");
    }

    static void SaveHistory(WallpaperHistory state)
    {
        Directory.CreateDirectory(DataDirectory);
        string temporary = HistoryPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, HistoryPath, true);
    }

    static void DeleteManagedFile(string path, string preservedPath)
    {
        if (string.IsNullOrEmpty(path) || string.Equals(path, preservedPath, StringComparison.OrdinalIgnoreCase)) return;
        string fullPath = Path.GetFullPath(path);
        string expectedDirectory = Path.GetFullPath(WallpaperDirectory) + Path.DirectorySeparatorChar;
        if (fullPath.StartsWith(expectedDirectory, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(fullPath).StartsWith("wallpaper-", StringComparison.Ordinal) &&
            string.Equals(Path.GetExtension(fullPath), ".bmp", StringComparison.OrdinalIgnoreCase)) File.Delete(fullPath);
    }

    static void TryRemoveFailedConversion(string path)
    {
        // Cleanup is best effort; the actual conversion/Windows error is returned.
        try { DeleteManagedFile(path, ""); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static bool IsExpected(Exception error) => error is IOException or UnauthorizedAccessException or SecurityException or Win32Exception or InvalidOperationException or JsonException;
    sealed class WallpaperHistory
    {
        public bool HasOriginal { get; set; }
        public string OriginalPath { get; set; } = "";
        public string ManagedPath { get; set; } = "";
        public bool HasOriginalStyle { get; set; }
        public string? OriginalWallpaperStyle { get; set; }
        public string? OriginalTileWallpaper { get; set; }
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool SystemParametersInfoRead(uint action, uint param, StringBuilder value, uint flags);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool SystemParametersInfoWrite(uint action, uint param, string value, uint flags);
}
