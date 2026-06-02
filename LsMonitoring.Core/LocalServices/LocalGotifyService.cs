using System;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LsMonitoring.Core.IO;

namespace LsMonitoring.Core.LocalServices;

public sealed record LocalGotifyBootstrapResult(
    bool Success,
    string ServerUrl,
    string AppToken,
    string ClientToken,
    string Message);

/// <summary>
/// Manages a per-machine Gotify server: downloads the official Windows binary, writes a config file with a
/// generated admin password, supervises the process, and bootstraps Application + Client tokens via the Gotify API.
/// All state lives in %LOCALAPPDATA%\LS Monitoring\ so each installation is self-contained.
/// </summary>
public sealed class LocalGotifyService : IDisposable
{
    // Pinned to a specific Gotify release with verified hashes instead of `latest`: a `latest`
    // URL silently changes contents over time and gives us nothing to verify against, so a
    // compromised/MITM'd download could run arbitrary code. When bumping the version, update all
    // three constants together (and the CI workflow that bundles the binary).
    public const string GotifyVersion = "v2.9.1";

    public const string GotifyDownloadUrl =
        $"https://github.com/gotify/server/releases/download/{GotifyVersion}/gotify-windows-amd64.exe.zip";

    // SHA-256 of gotify-windows-amd64.exe.zip for the pinned version (verified on live download).
    public const string GotifyZipSha256 = "8E05188232E0312DBC4F5760267777590A834BA53B8FB50844CAD43BF711DE74";

    // SHA-256 of the gotify-windows-amd64.exe inside that zip (gates the bundled/cached binaries).
    public const string GotifyExeSha256 = "2A1BFAC2575C72FB5476372A0691FF223DFCC55FD83BBA32259627493720C20C";

    private const string ApplicationName = "LS Monitoring";
    private const string ClientName = "LS Monitoring Desktop";
    private const int DefaultPort = 8080;
    private const string AdminUsername = "admin";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _toolDirectory;
    private readonly string _dataDirectory;
    private readonly string _gotifyExePath;
    private readonly string _gotifyConfigPath;
    private readonly string _gotifyPidPath;
    private readonly string _adminPasswordPath;
    private readonly int _port;
    private Process? _gotifyProcess;

    public LocalGotifyService(
        string toolDirectory,
        string dataDirectory,
        HttpClient? httpClient = null,
        int port = DefaultPort)
    {
        _toolDirectory = toolDirectory;
        _dataDirectory = dataDirectory;
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient();
        _gotifyExePath = Path.Combine(_toolDirectory, "gotify-server.exe");
        _gotifyConfigPath = Path.Combine(_dataDirectory, "config.yml");
        _gotifyPidPath = Path.Combine(_dataDirectory, "gotify.pid");
        _adminPasswordPath = Path.Combine(_dataDirectory, "admin-pass.txt");
        _port = port;
    }

    public static LocalGotifyService CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var baseDirectory = string.IsNullOrWhiteSpace(localAppData)
            ? AppContext.BaseDirectory
            : Path.Combine(localAppData, "LS Monitoring");

        return new LocalGotifyService(
            Path.Combine(baseDirectory, "tools"),
            Path.Combine(baseDirectory, "gotify-data"));
    }

    public string LocalServerUrl => $"http://127.0.0.1:{_port}";

    /// <summary>
    /// Ensures Gotify is installed, running, and that we have valid App + Client tokens for it.
    /// If <paramref name="existingAppToken"/>/<paramref name="existingClientToken"/> are non-empty,
    /// they are verified against the live server and reused if still valid.
    /// </summary>
    public async Task<LocalGotifyBootstrapResult> EnsureRunningAndBootstrapAsync(
        string? existingAppToken,
        string? existingClientToken,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(_toolDirectory);
            Directory.CreateDirectory(_dataDirectory);

            var adminPassword = EnsureAdminPasswordAndConfig();

            var running = await EnsureGotifyRunningAsync(cancellationToken);
            if (!running.Success)
            {
                return new LocalGotifyBootstrapResult(false, LocalServerUrl, "", "", running.Message);
            }

            var appToken = await EnsureApplicationTokenAsync(adminPassword, existingAppToken, cancellationToken);
            if (string.IsNullOrWhiteSpace(appToken))
            {
                return new LocalGotifyBootstrapResult(false, LocalServerUrl, "", "",
                    "Не удалось получить App token у Gotify. Возможно, изменился admin-пароль.");
            }

            var clientToken = await EnsureClientTokenAsync(adminPassword, existingClientToken, cancellationToken);
            if (string.IsNullOrWhiteSpace(clientToken))
            {
                return new LocalGotifyBootstrapResult(false, LocalServerUrl, appToken, "",
                    "Не удалось получить Client token у Gotify. Возможно, изменился admin-пароль.");
            }

            return new LocalGotifyBootstrapResult(
                true,
                LocalServerUrl,
                appToken,
                clientToken,
                "Локальный Gotify готов.");
        }
        catch (Exception ex)
        {
            return new LocalGotifyBootstrapResult(false, LocalServerUrl, "", "", $"Gotify: {ex.Message}");
        }
    }

    private async Task<(bool Success, string Message)> EnsureGotifyRunningAsync(CancellationToken cancellationToken)
    {
        // If the database file is present the process that owns it should still be running.
        // If it is absent (fresh install or user wiped the data dir) any stale gotify-server.exe
        // on our port must be killed first because it was initialised with a different password.
        var dbPath = Path.Combine(_dataDirectory, "gotify.db");
        var freshDataDir = !File.Exists(dbPath);

        if (!freshDataDir && await IsHealthyAsync(cancellationToken))
        {
            return (true, "Уже запущен.");
        }

        if (freshDataDir)
        {
            KillAnyGotifyProcess();
        }

        string path;
        try
        {
            path = await ResolveGotifyExeAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return (false, $"Не удалось скачать gotify-server.exe: {ex.Message}");
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            return (false, "В архиве Gotify не найден .exe-файл.");
        }

        Stop();

        _gotifyProcess = StartGotify(path);
        try
        {
            File.WriteAllText(_gotifyPidPath, _gotifyProcess.Id.ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
            // PID file is a convenience — failures here don't stop the bootstrap.
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsHealthyAsync(cancellationToken))
            {
                return (true, "Запущен.");
            }

            if (_gotifyProcess.HasExited)
            {
                return (false, $"Gotify упал на старте (exit {_gotifyProcess.ExitCode}).");
            }

            await Task.Delay(500, cancellationToken);
        }

        return (false, "Gotify не ответил на /health за 30 с.");
    }

    private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await _httpClient.GetAsync($"{LocalServerUrl}/health", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> ResolveGotifyExeAsync(CancellationToken cancellationToken)
    {
        // 1. Bundled binary next to the app. CI ships this so end users never
        //    hit a runtime download — Windows Defender treats fresh downloads
        //    of Go binaries as PUA and quarantines mid-write. Only trust it if it
        //    matches the pinned hash, otherwise fall through and re-download.
        var bundled = Path.Combine(AppContext.BaseDirectory, "gotify-server.exe");
        if (File.Exists(bundled) && FileHashMatches(bundled, GotifyExeSha256))
        {
            return bundled;
        }

        // 2. Previously downloaded copy in the per-user data dir (same hash gate).
        if (File.Exists(_gotifyExePath) && FileHashMatches(_gotifyExePath, GotifyExeSha256))
        {
            return _gotifyExePath;
        }

        // 3. Live download — dev fallback. Will likely trigger Defender on a
        //    clean machine; the real fix is to ship the binary with the app.
        var zipPath = Path.Combine(_toolDirectory, "gotify-download.zip");
        try
        {
            TryDelete(zipPath);

            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadCts.CancelAfter(TimeSpan.FromMinutes(3));
            using var response = await _httpClient.GetAsync(
                GotifyDownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                downloadCts.Token);
            response.EnsureSuccessStatusCode();

            await using (var output = File.Create(zipPath))
            {
                await response.Content.CopyToAsync(output, downloadCts.Token);
            }

            if (!FileHashMatches(zipPath, GotifyZipSha256))
            {
                throw new InvalidOperationException(
                    $"Контрольная сумма скачанного Gotify ({GotifyVersion}) не совпала с ожидаемой — загрузка отклонена.");
            }

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var exeEntry = archive.Entries.FirstOrDefault(e =>
                    e.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                if (exeEntry is null)
                {
                    return "";
                }

                exeEntry.ExtractToFile(_gotifyExePath, overwrite: true);
            }

            return _gotifyExePath;
        }
        finally
        {
            TryDelete(zipPath);
        }
    }

    private string EnsureAdminPasswordAndConfig()
    {
        string password;
        if (File.Exists(_adminPasswordPath))
        {
            password = File.ReadAllText(_adminPasswordPath).Trim();
            if (string.IsNullOrWhiteSpace(password))
            {
                password = GenerateRandomPassword();
                AtomicFile.WriteAllText(_adminPasswordPath, password);
            }
        }
        else
        {
            password = GenerateRandomPassword();
            AtomicFile.WriteAllText(_adminPasswordPath, password);
        }

        if (!File.Exists(_gotifyConfigPath))
        {
            // Gotify reads `config.yml` from its working directory. `defaultuser` is only honored on first
            // database initialization, so this password becomes the admin password only when gotify.db is fresh.
            var config = $@"server:
  port: {_port.ToString(CultureInfo.InvariantCulture)}
  listenaddr: 127.0.0.1
  ssl:
    enabled: false
defaultuser:
  name: {AdminUsername}
  pass: ""{password}""
passstrength: 10
database:
  dialect: sqlite3
  connection: gotify.db
uploadedimagesdir: images
pluginsdir: plugins
";
            AtomicFile.WriteAllText(_gotifyConfigPath, config);
        }

        return password;
    }

    private Process StartGotify(string gotifyPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = gotifyPath,
            WorkingDirectory = _dataDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("gotify-server.exe не запустился.");

        // Drain the streams so the process doesn't block on a full pipe buffer.
        _ = Task.Run(async () =>
        {
            try { await process.StandardOutput.ReadToEndAsync(); } catch { }
        });
        _ = Task.Run(async () =>
        {
            try { await process.StandardError.ReadToEndAsync(); } catch { }
        });

        return process;
    }

    /// <summary>Kills every running gotify-server process regardless of how it was started.</summary>
    private static void KillAnyGotifyProcess()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("gotify-server"))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                finally { proc.Dispose(); }
            }
        }
        catch
        {
            // Best effort — port contention will surface as a startup failure.
        }
    }

    private void StopOwnedProcess()
    {
        var pidText = ReadTextOrEmpty(_gotifyPidPath);
        if (!int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.ProcessName.Contains("gotify", StringComparison.OrdinalIgnoreCase))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // If the previous helper is already gone, the next start will own port 8080 itself.
        }
        finally
        {
            TryDelete(_gotifyPidPath);
        }
    }

    public void Stop()
    {
        if (_gotifyProcess is not null)
        {
            try
            {
                if (!_gotifyProcess.HasExited)
                {
                    _gotifyProcess.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            finally
            {
                _gotifyProcess.Dispose();
                _gotifyProcess = null;
            }
        }

        StopOwnedProcess();
    }

    public void Dispose()
    {
        Stop();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<string> EnsureApplicationTokenAsync(
        string adminPassword,
        string? existingToken,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(existingToken) && await IsAppTokenValidAsync(existingToken!, cancellationToken))
        {
            return existingToken!;
        }

        var existing = await FindExistingTokenAsync<GotifyApplication>(
            adminPassword, "/application", ApplicationName, cancellationToken);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        return await CreateApplicationTokenAsync(adminPassword, cancellationToken);
    }

    private async Task<string> EnsureClientTokenAsync(
        string adminPassword,
        string? existingToken,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(existingToken) && await IsClientTokenValidAsync(existingToken!, cancellationToken))
        {
            return existingToken!;
        }

        var existing = await FindExistingTokenAsync<GotifyClient>(
            adminPassword, "/client", ClientName, cancellationToken);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        return await CreateClientTokenAsync(adminPassword, cancellationToken);
    }

    private async Task<bool> IsAppTokenValidAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{LocalServerUrl}/message?limit=1");
            request.Headers.Add("X-Gotify-Key", token);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> IsClientTokenValidAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{LocalServerUrl}/current/user");
            request.Headers.Add("X-Gotify-Key", token);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> FindExistingTokenAsync<T>(
        string adminPassword,
        string path,
        string name,
        CancellationToken cancellationToken) where T : GotifyTokenItem
    {
        using var request = BuildAdminRequest(HttpMethod.Get, path, adminPassword);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return "";
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var items = await JsonSerializer.DeserializeAsync<List<T>>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);
        var match = items?.FirstOrDefault(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return match?.Token ?? "";
    }

    private async Task<string> CreateApplicationTokenAsync(string adminPassword, CancellationToken cancellationToken)
    {
        using var request = BuildAdminRequest(HttpMethod.Post, "/application", adminPassword);
        request.Content = JsonContent("""{"name":"LS Monitoring","description":"Auto-created by LS Monitoring desktop"}""");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return "";
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var created = await JsonSerializer.DeserializeAsync<GotifyApplication>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);
        return created?.Token ?? "";
    }

    private async Task<string> CreateClientTokenAsync(string adminPassword, CancellationToken cancellationToken)
    {
        using var request = BuildAdminRequest(HttpMethod.Post, "/client", adminPassword);
        request.Content = JsonContent("""{"name":"LS Monitoring Desktop"}""");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return "";
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var created = await JsonSerializer.DeserializeAsync<GotifyClient>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);
        return created?.Token ?? "";
    }

    private HttpRequestMessage BuildAdminRequest(HttpMethod method, string path, string adminPassword)
    {
        var request = new HttpRequestMessage(method, $"{LocalServerUrl}{path}");
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{AdminUsername}:{adminPassword}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        return request;
    }

    private static StringContent JsonContent(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static string GenerateRandomPassword()
    {
        var bytes = RandomNumberGenerator.GetBytes(18);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('/', '_')
            .Replace('+', '-');
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

    private static bool FileHashMatches(string path, string expectedSha256)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Unreadable/locked file can't be trusted — treat as a mismatch so the caller
            // falls back to a fresh, verified download instead of running it.
            return false;
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
            // Best effort cleanup; stale temp files do not break bootstrap.
        }
    }

    private abstract class GotifyTokenItem
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("token")]
        public string Token { get; set; } = "";
    }

    private sealed class GotifyApplication : GotifyTokenItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
    }

    private sealed class GotifyClient : GotifyTokenItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
    }
}
