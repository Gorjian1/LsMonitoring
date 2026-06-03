namespace LsMonitoring.Core.IO;

/// <summary>
/// Centralises the per-user data and log directory layout so every component
/// writes to the same well-known location regardless of the process working directory.
///
/// Layout under %LOCALAPPDATA%\LS Monitoring\ (or AppContext.BaseDirectory as fallback):
///   logs/         — rotating diagnostic log files for each alert channel
///   gotify-data/  — Gotify database and config (managed by LocalGotifyService)
///   tools/        — downloaded helper binaries (gotify-server.exe)
/// </summary>
public static class AppPaths
{
    private static readonly string BaseDirectory = ResolveBase();

    /// <summary>
    /// Absolute path to the per-user log directory.
    /// Guaranteed to end without a path separator.
    /// </summary>
    public static string LogDirectory => Path.Combine(BaseDirectory, "logs");

    /// <summary>
    /// Returns the full path to a log file inside <see cref="LogDirectory"/>.
    /// Creates the directory if it does not exist.
    /// </summary>
    public static string LogFile(string fileName)
    {
        Directory.CreateDirectory(LogDirectory);
        return Path.Combine(LogDirectory, fileName);
    }

    private static string ResolveBase()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? AppContext.BaseDirectory
            : Path.Combine(localAppData, "LS Monitoring");
    }
}
