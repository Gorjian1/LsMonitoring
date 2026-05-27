using Velopack;
using Velopack.Sources;

namespace LsMonitoring.Avalonia;

/// <summary>
/// Checks for and applies updates from GitHub Releases via Velopack.
/// Safe to use from any thread; UI interactions go through the Avalonia dispatcher.
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/Gorjian1/LsMonitoring";

    private readonly UpdateManager _mgr;
    private UpdateInfo? _pending;

    public UpdateService()
    {
        _mgr = new UpdateManager(new GithubSource(RepoUrl, null, false));
    }

    /// <summary>True when running as a Velopack-managed install (not portable).</summary>
    public bool IsInstalled => _mgr.IsInstalled;

    /// <summary>Non-null after <see cref="CheckAndDownloadAsync"/> finds and fetches an update.</summary>
    public string? PendingVersion => _pending?.TargetFullRelease.Version.ToString();

    /// <summary>
    /// Checks GitHub for a newer release. If one is found, downloads it in the background.
    /// Returns true when an update is ready to apply.
    /// </summary>
    public async Task<bool> CheckAndDownloadAsync(
        Action<int>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!_mgr.IsInstalled)
        {
            return false;
        }

        try
        {
            var update = await _mgr.CheckForUpdatesAsync();
            if (update is null)
            {
                return false;
            }

            await _mgr.DownloadUpdatesAsync(update, onProgress);
            _pending = update;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Applies the downloaded update and restarts the application.
    /// Does nothing if no update has been downloaded yet.
    /// </summary>
    public void ApplyAndRestart()
    {
        if (_pending is not null)
        {
            _mgr.ApplyUpdatesAndRestart(_pending);
        }
    }
}
