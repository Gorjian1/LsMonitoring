using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LsMonitoring.Core.Alarms;

namespace LsMonitoring.Avalonia;

/// <summary>
/// Desktop-side client + supervisor for the separate <c>LsMonitoring.TelegramBot.exe</c> companion.
/// The companion holds the bot token and runs the single getUpdates poller (no 409 conflicts);
/// this class launches it, pushes configuration over localhost HTTP, and implements the alert
/// channel by forwarding alarm events. The desktop never holds the token or polls Telegram itself.
/// </summary>
public sealed class TelegramCompanionService : IUpdatableAlertChannel, IAsyncDisposable
{
    private const string ExeName = "LsMonitoring.TelegramBot.exe";
    private const string ProcessName = "LsMonitoring.TelegramBot";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly string _key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly int _port;
    private readonly string _baseUrl;
    private Process? _process;

    public TelegramCompanionService(int port = 8771)
    {
        _port = port;
        _baseUrl = $"http://127.0.0.1:{port}";
    }

    public string ChannelName => "Telegram";
    public string? LastError { get; private set; }

    /// <summary>Ensures the companion is running and pushes the token, link code and known chats.</summary>
    public async Task<bool> EnsureConfiguredAsync(
        string botToken,
        string linkCode,
        IEnumerable<long> chatIds,
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return await PostAsync("/config", new
        {
            botToken,
            linkCode,
            chatIds = chatIds.ToList()
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<long>> GetBoundChatIdsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/state");
            request.Headers.Add("X-LS-Bot-Key", _key);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("lastError", out var err) && err.ValueKind == JsonValueKind.String)
            {
                LastError = err.GetString();
            }

            if (doc.RootElement.TryGetProperty("chatIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                return arr.EnumerateArray().Select(x => x.GetInt64()).ToList();
            }
        }
        catch
        {
            // Treat as no bindings; caller keeps its current list.
        }

        return [];
    }

    public async Task<bool> SendTestAsync()
    {
        var (ok, error) = await PostForResultAsync("/test", new { }).ConfigureAwait(false);
        if (!ok)
        {
            LastError = error ?? LastError;
        }

        return ok;
    }

    public Task NotifyStartedAsync(AlertEvent alertEvent) =>
        SendAlarmAsync(alertEvent.NodeId, alertEvent.Axis, true, alertEvent.CurrentValue, alertEvent.StartedAt);

    public Task NotifyActiveAsync(AlertEvent alertEvent) =>
        SendAlarmAsync(alertEvent.NodeId, alertEvent.Axis, true, alertEvent.CurrentValue, alertEvent.UpdatedAt);

    public Task NotifyResolvedAsync(AlertEvent alertEvent) =>
        SendAlarmAsync(alertEvent.NodeId, alertEvent.Axis, false, alertEvent.CurrentValue, alertEvent.ResolvedAt ?? alertEvent.UpdatedAt);

    public async Task StopAsync()
    {
        StopProcess();
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        StopProcess();
        _http.Dispose();
        await Task.CompletedTask;
    }

    private Task SendAlarmAsync(int nodeId, string axis, bool isCritical, double value, DateTime timestamp) =>
        PostAsync("/alarm", new { nodeId, axis, isCritical, value, timestamp }, default);

    private async Task<bool> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        var probe = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (probe == Probe.Ours)
        {
            return true;
        }

        if (probe == Probe.Foreign)
        {
            // Stale companion from a previous run owns the port with a different key — replace it.
            KillStaleCompanions();
        }

        var exePath = ResolveExePath();
        if (exePath is null)
        {
            LastError = "Не найден LsMonitoring.TelegramBot.exe — companion Telegram не установлен.";
            return false;
        }

        StopProcess();

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(_port.ToString());
        startInfo.ArgumentList.Add("--key");
        startInfo.ArgumentList.Add(_key);

        try
        {
            _process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            LastError = $"Не удалось запустить companion: {ex.Message}";
            return false;
        }

        if (_process is null)
        {
            LastError = "Не удалось запустить companion.";
            return false;
        }

        DrainStreams(_process);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                LastError = $"Companion завершился на старте (exit {_process.ExitCode}).";
                return false;
            }

            if (await ProbeAsync(cancellationToken).ConfigureAwait(false) == Probe.Ours)
            {
                return true;
            }

            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        LastError = "Companion не ответил на /health за 10 с.";
        return false;
    }

    private enum Probe
    {
        Down,
        Ours,
        Foreign
    }

    private async Task<Probe> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/state");
            request.Headers.Add("X-LS-Bot-Key", _key);
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return Probe.Foreign;
            }

            return response.IsSuccessStatusCode ? Probe.Ours : Probe.Foreign;
        }
        catch
        {
            return Probe.Down;
        }
    }

    private async Task<bool> PostAsync(string path, object payload, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}{path}");
            request.Headers.Add("X-LS-Bot-Key", _key);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"companion HTTP {(int)response.StatusCode}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LastError = $"companion: {ex.Message}";
            return false;
        }
    }

    private async Task<(bool Ok, string? Error)> PostForResultAsync(string path, object payload)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}{path}");
            request.Headers.Add("X-LS-Bot-Key", _key);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, $"HTTP {(int)response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(body);
            var ok = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            string? error = doc.RootElement.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String
                ? errEl.GetString()
                : null;
            return (ok, error);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string? ResolveExePath()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, ExeName);
        if (File.Exists(bundled))
        {
            return bundled;
        }

        var fromEnv = Environment.GetEnvironmentVariable("LSMONITORING_TELEGRAM_BOT_EXE");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        // Dev fallback: walk up to the solution and look for the build output.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 7 && dir is not null; i++, dir = dir.Parent)
        {
            foreach (var cfg in new[] { "Release", "Debug" })
            {
                var candidate = Path.Combine(dir.FullName, "LsMonitoring.TelegramBot", "bin", cfg, "net9.0", ExeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static void DrainStreams(Process process)
    {
        _ = Task.Run(async () => { try { await process.StandardOutput.ReadToEndAsync(); } catch { /* ignore */ } });
        _ = Task.Run(async () => { try { await process.StandardError.ReadToEndAsync(); } catch { /* ignore */ } });
    }

    private static void KillStaleCompanions()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName(ProcessName))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                finally { proc.Dispose(); }
            }
        }
        catch
        {
            // Best effort — a surviving stale process surfaces as a config push failure.
        }
    }

    private void StopProcess()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }
}
