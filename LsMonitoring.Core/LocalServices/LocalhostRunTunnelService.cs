using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using LsMonitoring.Core.Configuration;

namespace LsMonitoring.Core.LocalServices;

public sealed record LocalhostRunTunnelResult(
    bool Success,
    string PublicUrl,
    string Message,
    string LogPath);

/// <summary>
/// Reverse-tunnels the local Gotify port to a public URL via localhost.run, using the
/// OpenSSH client that ships with Windows 10/11. Replaces the previous Cloudflare quick
/// tunnel — trycloudflare.com is unreliable from many ISPs (e.g. RU) due to throttling,
/// whereas plain outbound SSH on port 22 generally goes through.
/// </summary>
public sealed class LocalhostRunTunnelService : IDisposable
{
    private const string SshHost = "nokey@localhost.run";

    private static readonly Regex TunnelUrlPattern = new(
        @"https://[a-z0-9-]+\.lhr\.life",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _stateDirectory;
    private readonly string _pidPath;
    private readonly string _urlPath;
    private readonly string _logPath;
    private Process? _sshProcess;

    public LocalhostRunTunnelService(string stateDirectory, HttpClient? httpClient = null)
    {
        _stateDirectory = stateDirectory;
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient();
        _pidPath = Path.Combine(_stateDirectory, "tunnel.pid");
        _urlPath = Path.Combine(_stateDirectory, "tunnel.url");
        _logPath = Path.Combine(_stateDirectory, "tunnel.log");
    }

    public static LocalhostRunTunnelService CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var baseDirectory = string.IsNullOrWhiteSpace(localAppData)
            ? AppContext.BaseDirectory
            : Path.Combine(localAppData, "LS Monitoring");

        return new LocalhostRunTunnelService(
            Path.Combine(baseDirectory, "push-tunnel"));
    }

    public async Task<LocalhostRunTunnelResult> EnsureStartedAsync(
        string? localServerUrl = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_stateDirectory);

        var localUrl = NormalizeLocalServerUrl(localServerUrl);
        if (!await IsGotifyHealthyAsync(localUrl, cancellationToken))
        {
            return Fail($"Gotify не отвечает на {localUrl}. Запустите Gotify и повторите.");
        }

        var cached = await TryUseCachedTunnelAsync(cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        Stop();

        var sshPath = ResolveSshPath();
        if (sshPath is null)
        {
            return Fail(
                "ssh.exe не найден. Включите OpenSSH Client в Параметры → Приложения → " +
                "Дополнительные компоненты.");
        }

        TryDelete(_logPath);

        var publicUrl = await StartSshTunnelAsync(sshPath, localUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(publicUrl))
        {
            Stop();
            return Fail($"localhost.run не вернул публичный URL за 90 с. Лог: {_logPath}");
        }

        File.WriteAllText(_urlPath, publicUrl);
        return new LocalhostRunTunnelResult(
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

    private async Task<LocalhostRunTunnelResult?> TryUseCachedTunnelAsync(
        CancellationToken cancellationToken)
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

        return new LocalhostRunTunnelResult(
            true,
            cachedUrl,
            "Используется уже запущенный временный tunnel.",
            _logPath);
    }

    private Task<bool> IsGotifyHealthyAsync(string serverUrl, CancellationToken cancellationToken) =>
        IsHealthEndpointReachableAsync(serverUrl, TimeSpan.FromSeconds(3), cancellationToken);

    private Task<bool> IsPublicGotifyHealthyAsync(string serverUrl, CancellationToken cancellationToken) =>
        IsHealthEndpointReachableAsync(serverUrl, TimeSpan.FromSeconds(8), cancellationToken);

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

    private static string? ResolveSshPath()
    {
        var systemSsh = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "OpenSSH",
            "ssh.exe");
        if (File.Exists(systemSsh))
        {
            return systemSsh;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "ssh.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Bad PATH entry — keep looking.
            }
        }

        return null;
    }

    private async Task<string> StartSshTunnelAsync(
        string sshPath,
        string localUrl,
        CancellationToken cancellationToken)
    {
        var hostAndPort = ExtractLocalHostAndPort(localUrl);

        var startInfo = new ProcessStartInfo
        {
            FileName = sshPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-R");
        startInfo.ArgumentList.Add($"80:{hostAndPort}");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("StrictHostKeyChecking=accept-new");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("UserKnownHostsFile=NUL");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("ServerAliveInterval=30");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("ExitOnForwardFailure=yes");
        startInfo.ArgumentList.Add(SshHost);

        _sshProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("ssh.exe не запустился.");

        try
        {
            File.WriteAllText(_pidPath, _sshProcess.Id.ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
            // PID file is a convenience; not having it doesn't break tunnel start.
        }

        var urlTcs = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnLine(string? line)
        {
            if (line is null)
            {
                return;
            }

            TryAppendLog(line);
            var match = TunnelUrlPattern.Match(line);
            if (match.Success)
            {
                urlTcs.TrySetResult(match.Value);
            }
        }

        _sshProcess.OutputDataReceived += (_, e) => OnLine(e.Data);
        _sshProcess.ErrorDataReceived += (_, e) => OnLine(e.Data);
        _sshProcess.EnableRaisingEvents = true;
        _sshProcess.Exited += (_, _) => urlTcs.TrySetResult("");
        _sshProcess.BeginOutputReadLine();
        _sshProcess.BeginErrorReadLine();

        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineCts.CancelAfter(TimeSpan.FromSeconds(90));
        using var deadlineReg = deadlineCts.Token.Register(() => urlTcs.TrySetResult(""));

        return await urlTcs.Task;
    }

    private static string ExtractLocalHostAndPort(string localUrl)
    {
        if (Uri.TryCreate(localUrl, UriKind.Absolute, out var uri))
        {
            return $"{uri.Host}:{uri.Port}";
        }

        return "127.0.0.1:8080";
    }

    private void TryAppendLog(string line)
    {
        try
        {
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch
        {
            // Log is best effort; failure to write must not break the tunnel.
        }
    }

    private void StopOwnedSshProcess()
    {
        var pidText = ReadTextOrEmpty(_pidPath);
        if (!int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.ProcessName.Equals("ssh", StringComparison.OrdinalIgnoreCase))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // If the old helper is already gone, the next start will create a new tunnel itself.
        }
        finally
        {
            TryDelete(_pidPath);
            TryDelete(_urlPath);
        }
    }

    public void Stop()
    {
        if (_sshProcess is not null)
        {
            try
            {
                if (!_sshProcess.HasExited)
                {
                    _sshProcess.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            finally
            {
                _sshProcess.Dispose();
                _sshProcess = null;
            }
        }

        StopOwnedSshProcess();
    }

    public void Dispose()
    {
        Stop();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private LocalhostRunTunnelResult Fail(string message)
    {
        return new LocalhostRunTunnelResult(false, "", message, _logPath);
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
            // Stale log files are not fatal.
        }
    }
}
