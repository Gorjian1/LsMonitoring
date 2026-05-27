using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using LsMonitoring.Core.Configuration;

namespace LsMonitoring.Core.LocalServices;

public sealed record CloudflareQuickTunnelResult(
    bool Success,
    string PublicUrl,
    string Message,
    string LogPath);

public sealed class CloudflareQuickTunnelService
{
    public const string CloudflaredDownloadUrl =
        "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

    private static readonly Regex TunnelUrlPattern = new(
        @"https://[a-z0-9-]+\.trycloudflare\.com",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly string _toolDirectory;
    private readonly string _stateDirectory;
    private readonly string _cloudflaredPath;
    private readonly string _pidPath;
    private readonly string _urlPath;
    private readonly string _logPath;

    public CloudflareQuickTunnelService(
        string toolDirectory,
        string stateDirectory,
        HttpClient? httpClient = null)
    {
        _toolDirectory = toolDirectory;
        _stateDirectory = stateDirectory;
        _httpClient = httpClient ?? new HttpClient();
        _cloudflaredPath = Path.Combine(_toolDirectory, "cloudflared.exe");
        _pidPath = Path.Combine(_stateDirectory, "cloudflared.pid");
        _urlPath = Path.Combine(_stateDirectory, "cloudflared.url");
        _logPath = Path.Combine(_stateDirectory, "cloudflared.log");
    }

    public static CloudflareQuickTunnelService CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var baseDirectory = string.IsNullOrWhiteSpace(localAppData)
            ? AppContext.BaseDirectory
            : Path.Combine(localAppData, "LS Monitoring");

        return new CloudflareQuickTunnelService(
            Path.Combine(baseDirectory, "tools"),
            Path.Combine(baseDirectory, "push-tunnel"));
    }

    public async Task<CloudflareQuickTunnelResult> EnsureStartedAsync(
        string? localServerUrl = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_toolDirectory);
        Directory.CreateDirectory(_stateDirectory);

        var localUrl = NormalizeLocalServerUrl(localServerUrl);
        if (!await IsGotifyHealthyAsync(localUrl, cancellationToken))
        {
            return Fail($"Gotify не отвечает на {localUrl}. Запустите Gotify и повторите.", "");
        }

        var cached = await TryUseCachedTunnelAsync(cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        StopOwnedCloudflaredProcess();

        var cloudflaredPath = await ResolveCloudflaredPathAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(cloudflaredPath))
        {
            return Fail("Не удалось скачать или найти cloudflared.exe.", "");
        }

        TryDelete(_logPath);

        var process = StartCloudflared(cloudflaredPath, localUrl);
        File.WriteAllText(_pidPath, process.Id.ToString(CultureInfo.InvariantCulture));

        var publicUrl = await WaitForPublicUrlAsync(process, cancellationToken);
        if (string.IsNullOrWhiteSpace(publicUrl))
        {
            return Fail($"Cloudflare Tunnel не вернул публичный URL. Лог: {_logPath}", "");
        }

        File.WriteAllText(_urlPath, publicUrl);
        return new CloudflareQuickTunnelResult(
            true,
            publicUrl,
            "Временный tunnel запущен. После перезагрузки компьютера URL нужно создать заново.",
            _logPath);
    }

    public static string NormalizeLocalServerUrl(string? localServerUrl)
    {
        return string.IsNullOrWhiteSpace(localServerUrl)
            ? PushConfig.DefaultLocalServerUrl
            : localServerUrl.Trim().TrimEnd('/');
    }

    private async Task<CloudflareQuickTunnelResult?> TryUseCachedTunnelAsync(CancellationToken cancellationToken)
    {
        var cachedUrl = ReadTextOrEmpty(_urlPath);
        if (string.IsNullOrWhiteSpace(cachedUrl))
        {
            return null;
        }

        if (!await IsPublicGotifyHealthyAsync(cachedUrl, cancellationToken))
        {
            return null;
        }

        return new CloudflareQuickTunnelResult(
            true,
            cachedUrl,
            "Используется уже запущенный временный tunnel.",
            _logPath);
    }

    private async Task<bool> IsGotifyHealthyAsync(string serverUrl, CancellationToken cancellationToken)
    {
        return await IsHealthEndpointReachableAsync(serverUrl, TimeSpan.FromSeconds(3), cancellationToken);
    }

    private async Task<bool> IsPublicGotifyHealthyAsync(string serverUrl, CancellationToken cancellationToken)
    {
        return await IsHealthEndpointReachableAsync(serverUrl, TimeSpan.FromSeconds(6), cancellationToken);
    }

    private async Task<bool> IsHealthEndpointReachableAsync(
        string serverUrl,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate($"{serverUrl.Trim().TrimEnd('/')}/health", UriKind.Absolute, out var healthUri))
        {
            return false;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            using var response = await _httpClient.GetAsync(healthUri, timeoutCts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> ResolveCloudflaredPathAsync(CancellationToken cancellationToken)
    {
        foreach (var candidate in CloudflaredCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        try
        {
            var tempPath = Path.Combine(_toolDirectory, "cloudflared.exe.download");
            TryDelete(tempPath);

            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadCts.CancelAfter(TimeSpan.FromMinutes(2));
            using var response = await _httpClient.GetAsync(
                CloudflaredDownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                downloadCts.Token);
            response.EnsureSuccessStatusCode();

            await using (var output = File.Create(tempPath))
            {
                await response.Content.CopyToAsync(output, downloadCts.Token);
            }

            File.Move(tempPath, _cloudflaredPath, overwrite: true);
            return _cloudflaredPath;
        }
        catch
        {
            return "";
        }
    }

    private IEnumerable<string> CloudflaredCandidates()
    {
        yield return _cloudflaredPath;
        yield return Path.Combine(AppContext.BaseDirectory, "cloudflared.exe");
        yield return Path.Combine(Environment.CurrentDirectory, ".local", "cloudflared", "cloudflared.exe");
    }

    private Process StartCloudflared(string cloudflaredPath, string localUrl)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = cloudflaredPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("tunnel");
        startInfo.ArgumentList.Add("--url");
        startInfo.ArgumentList.Add(localUrl);
        startInfo.ArgumentList.Add("--no-autoupdate");
        startInfo.ArgumentList.Add("--logfile");
        startInfo.ArgumentList.Add(_logPath);
        startInfo.ArgumentList.Add("--pidfile");
        startInfo.ArgumentList.Add(_pidPath);

        var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("cloudflared.exe не запустился.");
        }

        return process;
    }

    private async Task<string> WaitForPublicUrlAsync(Process process, CancellationToken cancellationToken)
    {
        // 90s — Cloudflare quick tunnel provisioning can be slow from regions with
        // high latency to Cloudflare's edge (RU networks regularly take 40–60s).
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var publicUrl = ExtractPublicUrlFromLog();
            if (!string.IsNullOrWhiteSpace(publicUrl))
            {
                return publicUrl;
            }

            if (process.HasExited)
            {
                return "";
            }

            await Task.Delay(500, cancellationToken);
        }

        return "";
    }

    private string ExtractPublicUrlFromLog()
    {
        if (!File.Exists(_logPath))
        {
            return "";
        }

        try
        {
            var log = File.ReadAllText(_logPath);
            return TunnelUrlPattern.Match(log) is { Success: true } match
                ? match.Value
                : "";
        }
        catch
        {
            return "";
        }
    }

    private void StopOwnedCloudflaredProcess()
    {
        var pidText = ReadTextOrEmpty(_pidPath);
        if (!int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.ProcessName.Contains("cloudflared", StringComparison.OrdinalIgnoreCase))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // If the old helper process is already gone, the next start will create a new tunnel.
        }
    }

    private CloudflareQuickTunnelResult Fail(string message, string publicUrl)
    {
        return new CloudflareQuickTunnelResult(false, publicUrl, message, _logPath);
    }

    private static string ReadTextOrEmpty(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Log cleanup is best effort; stale logs do not break monitoring.
        }
    }
}
