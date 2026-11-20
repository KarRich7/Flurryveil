using System.Security.Cryptography;
using System.Text;

namespace QuietSnow;

internal static class AppStorage
{
    static readonly string? OverridePath = Environment.GetEnvironmentVariable("FLURRYVEIL_DATA_DIR");
    internal static bool Isolated => !string.IsNullOrWhiteSpace(OverridePath);
    internal static readonly string DirectoryPath = Isolated ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(OverridePath!)) :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuietSnow");
    static readonly string TestSuffix = Isolated ? "." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DirectoryPath.ToUpperInvariant())))[..24] : "";

    // The shared names keep the old desktop process and the upgraded app from running together.
    internal static string InstanceName => @"Local\QuietSnow.Desktop.Instance.v2" + TestSuffix;
    internal static string ActivationName => @"Local\QuietSnow.Desktop.Activate.v2" + TestSuffix;

    internal static void Log(string operation, Exception error)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            string path = Path.Combine(DirectoryPath, "flurryveil.log");
            var log = new FileInfo(path);
            if (log.Exists && log.Length >= 128 * 1024) File.Move(path, path + ".old", overwrite: true);
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {operation} ({error.GetType().Name}, 0x{error.HResult:X8}){Environment.NewLine}");
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
    }
}
