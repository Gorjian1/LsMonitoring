using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using LsMonitoring.Core.IO;
using LsMonitoring.Core.Models;

namespace LsMonitoring.Core.Configuration;

/// <summary>
/// Shared DPAPI encode/decode helper. On Windows, bytes are protected with
/// <see cref="DataProtectionScope.CurrentUser"/> so they cannot be decrypted
/// on another machine or user account. On other platforms, the value is
/// stored as plain base64 (no OS-level secret store available).
/// </summary>
public static class ProtectedSecret
{
    /// <summary>Encodes <paramref name="value"/> to a base64 string, DPAPI-protected on Windows.</summary>
    public static string Encode(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            }
            catch
            {
                // DPAPI unavailable (e.g. service account without profile) — store as plain base64.
            }
        }

        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Decodes a value previously encoded by <see cref="Encode"/>. Returns an empty string on
    /// any error so callers never receive garbage. Falls back gracefully when DPAPI is unavailable
    /// or the value was written on another machine (migration from unprotected base64).
    /// </summary>
    public static string Decode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                }
                catch
                {
                    // Saved without DPAPI (e.g. from another machine or non-Windows) — use raw bytes.
                }
            }

            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }
}

public sealed class Thresholds
{
    public const string AbsoluteMode = "absolute";
    public const string VariationMode = "variation";

    [JsonPropertyName("warning_a")]
    public double WarningA { get; set; } = 5.0;

    [JsonPropertyName("critical_a")]
    public double CriticalA { get; set; } = 10.0;

    [JsonPropertyName("warning_b")]
    public double WarningB { get; set; } = 5.0;

    [JsonPropertyName("critical_b")]
    public double CriticalB { get; set; } = 10.0;

    [JsonPropertyName("same_for_ab")]
    public bool SameForAb { get; set; } = true;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = AbsoluteMode;

    [JsonPropertyName("zero_a")]
    public double ZeroA { get; set; }

    [JsonPropertyName("zero_b")]
    public double ZeroB { get; set; }

    [JsonIgnore]
    public bool UsesVariation => string.Equals(Mode, VariationMode, StringComparison.OrdinalIgnoreCase);

    public double? SourceA(Reading reading)
    {
        return UsesVariation ? reading.AVariation : reading.AAxis;
    }

    public double? SourceB(Reading reading)
    {
        return UsesVariation ? reading.BVariation : reading.BAxis;
    }

    public double? DeviationA(Reading reading)
    {
        return DeviationA(reading, null);
    }

    public double? DeviationB(Reading reading)
    {
        return DeviationB(reading, null);
    }

    public double? DeviationA(Reading reading, double? zeroOverride)
    {
        return SourceA(reading) is { } value ? value - (zeroOverride ?? ZeroA) : null;
    }

    public double? DeviationB(Reading reading, double? zeroOverride)
    {
        return SourceB(reading) is { } value ? value - (zeroOverride ?? ZeroB) : null;
    }

    public double EffectiveWarningB()
    {
        return SameForAb ? WarningA : WarningB;
    }

    public double EffectiveCriticalB()
    {
        return SameForAb ? CriticalA : CriticalB;
    }
}

public sealed class TelegramConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    // The bot is shared and its token is embedded into release builds (injected from the
    // LS_TELEGRAM_BOT_TOKEN CI secret), so the token is NEVER persisted to config.json — it lives
    // only in memory for the session and is resolved via env → this property → embedded token.
    [JsonIgnore]
    public string BotToken { get; set; } = "";

    [JsonPropertyName("link_code")]
    public string LinkCode { get; set; } = "";

    [JsonPropertyName("chat_ids")]
    public List<long> ChatIds { get; set; } = [];

    [JsonIgnore]
    public string EffectiveBotToken => TelegramSecrets.ResolveBotToken(BotToken);

    [JsonIgnore]
    public static string EffectiveBotUsername => TelegramSecrets.ResolveBotUsername(null);

    [JsonIgnore]
    public string EffectiveLinkCode => EnsureLinkCode();

    public string EnsureLinkCode()
    {
        if (IsValidLinkCode(LinkCode))
        {
            LinkCode = LinkCode.Trim().ToUpperInvariant();
            return LinkCode;
        }

        LinkCode = GenerateLinkCode();
        return LinkCode;
    }

    public static bool IsValidLinkCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.Length is >= 6 and <= 16 &&
               trimmed.All(ch => char.IsAsciiLetterOrDigit(ch));
    }

    private static string GenerateLinkCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);

        var chars = new char[8];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        }

        return new string(chars);
    }
}

public static class EmailDeliveryMode
{
    public const string Relay = "relay";
    public const string Smtp = "smtp";
}

public sealed class EmailConfig
{
    private string _installationId = Guid.NewGuid().ToString("N");

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("delivery_mode")]
    public string DeliveryMode { get; set; } = EmailDeliveryMode.Relay;

    [JsonPropertyName("relay_url")]
    public string RelayUrl { get; set; } = "";

    [JsonPropertyName("relay_token_b64")]
    public string RelayTokenBase64 { get; set; } = "";

    [JsonPropertyName("installation_id")]
    public string InstallationId
    {
        get => _installationId;
        set => _installationId = string.IsNullOrWhiteSpace(value)
            ? Guid.NewGuid().ToString("N")
            : value.Trim();
    }

    [JsonPropertyName("smtp_host")]
    public string SmtpHost { get; set; } = "";

    [JsonPropertyName("smtp_port")]
    public int SmtpPort { get; set; } = 587;

    [JsonPropertyName("use_ssl")]
    public bool UseSsl { get; set; } = true;

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("password")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LegacyPassword
    {
        get => null;
        set
        {
            if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(PasswordBase64))
            {
                Password = value;
            }
        }
    }

    [JsonPropertyName("password_b64")]
    public string PasswordBase64 { get; set; } = "";

    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("recipients")]
    public List<string> Recipients { get; set; } = [];

    [JsonPropertyName("send_resolved_notifications")]
    public bool SendResolvedNotifications { get; set; } = false;

    [JsonIgnore]
    public string Password
    {
        get => ProtectedSecret.Decode(PasswordBase64);
        set => PasswordBase64 = ProtectedSecret.Encode(value);
    }

    [JsonIgnore]
    public string RelayToken
    {
        get => ProtectedSecret.Decode(RelayTokenBase64);
        set => RelayTokenBase64 = ProtectedSecret.Encode(value);
    }

    [JsonIgnore]
    public string EffectiveFrom => !string.IsNullOrWhiteSpace(From) ? From.Trim() : Username.Trim();

    [JsonIgnore]
    public string EffectiveUsername => !string.IsNullOrWhiteSpace(Username) ? Username.Trim() : EffectiveFrom;

    [JsonIgnore]
    public string EffectiveSmtpHost => !string.IsNullOrWhiteSpace(SmtpHost)
        ? SmtpHost.Trim()
        : EmailSmtpProfile.ResolveHost(EffectiveFrom);

    [JsonIgnore]
    public int EffectiveSmtpPort => SmtpPort > 0 ? SmtpPort : 587;

    [JsonIgnore]
    public bool UsesRelay => !string.Equals(DeliveryMode, EmailDeliveryMode.Smtp, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool HasRelaySettings =>
        Recipients.Count > 0 &&
        !string.IsNullOrWhiteSpace(RelayUrl) &&
        !string.IsNullOrWhiteSpace(RelayToken);

    [JsonIgnore]
    public bool HasDeliverySettings =>
        Recipients.Count > 0 &&
        !string.IsNullOrWhiteSpace(EffectiveFrom) &&
        !string.IsNullOrWhiteSpace(EffectiveSmtpHost);

}

public static class EmailSmtpProfile
{
    public static string ResolveHost(string emailAddress)
    {
        var domain = ResolveDomain(emailAddress);
        if (string.IsNullOrWhiteSpace(domain))
        {
            return "";
        }

        return domain switch
        {
            "gmail.com" or "googlemail.com" => "smtp.gmail.com",
            "yandex.ru" or "ya.ru" or "yandex.com" or "yandex.by" or "yandex.kz" => "smtp.yandex.ru",
            "mail.ru" or "bk.ru" or "inbox.ru" or "list.ru" or "internet.ru" => "smtp.mail.ru",
            "outlook.com" or "hotmail.com" or "live.com" or "msn.com" => "smtp-mail.outlook.com",
            "icloud.com" or "me.com" or "mac.com" => "smtp.mail.me.com",
            _ => $"smtp.{domain}"
        };
    }

    private static string ResolveDomain(string emailAddress)
    {
        var at = emailAddress.LastIndexOf('@');
        if (at < 0 || at == emailAddress.Length - 1)
        {
            return "";
        }

        return emailAddress[(at + 1)..].Trim().ToLowerInvariant();
    }
}

public sealed class SmsConfig
{
    public const string SmsRuProvider = "sms.ru";
    public const int DefaultMaxMessagesPerHour = 20;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = SmsRuProvider;

    [JsonPropertyName("api_url")]
    public string ApiUrl { get; set; } = "";

    [JsonPropertyName("api_key_b64")]
    public string ApiKeyBase64 { get; set; } = "";

    [JsonPropertyName("api_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LegacyApiKey
    {
        get => null;
        set
        {
            if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(ApiKeyBase64))
            {
                ApiKey = value;
            }
        }
    }

    [JsonIgnore]
    public string ApiKey
    {
        get => ProtectedSecret.Decode(ApiKeyBase64);
        set => ApiKeyBase64 = ProtectedSecret.Encode(value);
    }

    [JsonPropertyName("sender")]
    public string Sender { get; set; } = "";

    [JsonPropertyName("phone_numbers")]
    public List<string> PhoneNumbers { get; set; } = [];

    [JsonPropertyName("max_messages_per_hour")]
    public int MaxMessagesPerHour { get; set; } = DefaultMaxMessagesPerHour;

    [JsonPropertyName("send_resolved_notifications")]
    public bool SendResolvedNotifications { get; set; } = false;

    [JsonIgnore]
    public string EffectiveProvider => string.IsNullOrWhiteSpace(Provider)
        ? SmsRuProvider
        : Provider.Trim().ToLowerInvariant();

    [JsonIgnore]
    public string EffectiveApiUrl => string.IsNullOrWhiteSpace(ApiUrl)
        ? "https://sms.ru/sms/send"
        : ApiUrl.Trim();

    [JsonIgnore]
    public int EffectiveMaxMessagesPerHour => MaxMessagesPerHour > 0
        ? MaxMessagesPerHour
        : DefaultMaxMessagesPerHour;

    [JsonIgnore]
    public bool HasDeliverySettings =>
        PhoneNumbers.Count > 0 &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(EffectiveApiUrl);
}

public sealed class PushConfig
{
    public const string DefaultAppDownloadUrl = "https://github.com/Gorjian1/LsMonitoring/releases/latest/download/ls-alerts-latest.apk";
    public const string DefaultLocalServerUrl = "http://localhost:8080";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "Gotify";

    [JsonPropertyName("server_url")]
    public string ServerUrl { get; set; } = "";

    [JsonPropertyName("app_token_b64")]
    public string AppTokenBase64 { get; set; } = "";

    [JsonPropertyName("app_token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LegacyAppToken
    {
        get => null;
        set
        {
            if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(AppTokenBase64))
            {
                AppToken = value;
            }
        }
    }

    [JsonIgnore]
    public string AppToken
    {
        get => ProtectedSecret.Decode(AppTokenBase64);
        set => AppTokenBase64 = ProtectedSecret.Encode(value);
    }

    [JsonPropertyName("client_token_b64")]
    public string ClientTokenBase64 { get; set; } = "";

    [JsonPropertyName("client_token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LegacyClientToken
    {
        get => null;
        set
        {
            if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(ClientTokenBase64))
            {
                ClientToken = value;
            }
        }
    }

    [JsonIgnore]
    public string ClientToken
    {
        get => ProtectedSecret.Decode(ClientTokenBase64);
        set => ClientTokenBase64 = ProtectedSecret.Encode(value);
    }

    [JsonPropertyName("app_download_url")]
    public string AppDownloadUrl { get; set; } = "";

    [JsonPropertyName("auto_start_temporary_tunnel")]
    public bool AutoStartTemporaryTunnel { get; set; } = true;

    [JsonPropertyName("local_server_url")]
    public string LocalServerUrl { get; set; } = DefaultLocalServerUrl;

    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 5;

    [JsonIgnore]
    public string EffectiveServerUrl => ServerUrl.Trim().TrimEnd('/');

    [JsonIgnore]
    public string EffectiveClientToken => ClientToken.Trim();

    [JsonIgnore]
    public string EffectiveAppDownloadUrl => string.IsNullOrWhiteSpace(AppDownloadUrl)
        ? DefaultAppDownloadUrl
        : AppDownloadUrl.Trim();

    [JsonIgnore]
    public string EffectiveLocalServerUrl => string.IsNullOrWhiteSpace(LocalServerUrl)
        ? DefaultLocalServerUrl
        : LocalServerUrl.Trim().TrimEnd('/');

    /// <summary>
    /// True when the configured server URL looks like an ephemeral tunnel that we created
    /// ourselves (localhost.run today, trycloudflare.com previously). Such URLs change on
    /// every restart, so we recreate them automatically on startup.
    /// </summary>
    [JsonIgnore]
    public bool UsesTemporaryTunnel
    {
        get
        {
            if (!Uri.TryCreate(EffectiveServerUrl, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return uri.Host.EndsWith(".lhr.life", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Auto-start logic intentionally does NOT require <see cref="Enabled"/>: the tunnel + local Gotify
    /// bootstrap so the Connect QR is ready before the user toggles the channel on.
    /// </summary>
    [JsonIgnore]
    public bool ShouldAutoStartTemporaryTunnel =>
        AutoStartTemporaryTunnel &&
        (string.IsNullOrWhiteSpace(EffectiveServerUrl) || UsesTemporaryTunnel);

    [JsonIgnore]
    public bool HasDeliverySettings =>
        !string.IsNullOrWhiteSpace(EffectiveServerUrl) &&
        !string.IsNullOrWhiteSpace(AppToken);

    [JsonIgnore]
    public bool HasClientSettings =>
        !string.IsNullOrWhiteSpace(EffectiveServerUrl) &&
        !string.IsNullOrWhiteSpace(EffectiveClientToken);
}

public sealed class WebhookConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("method")]
    public string Method { get; set; } = "POST";

    [JsonPropertyName("secret_b64")]
    public string SecretBase64 { get; set; } = "";

    [JsonPropertyName("secret")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LegacySecret
    {
        get => null;
        set
        {
            if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(SecretBase64))
            {
                Secret = value;
            }
        }
    }

    [JsonIgnore]
    public string Secret
    {
        get => ProtectedSecret.Decode(SecretBase64);
        set => SecretBase64 = ProtectedSecret.Encode(value);
    }

    [JsonPropertyName("headers")]
    public string Headers { get; set; } = "";

    [JsonPropertyName("payload_template")]
    public string PayloadTemplate { get; set; } = "";
}

public sealed class ConnectionConfig
{
    [JsonPropertyName("gateway_ip")]
    public string GatewayIp { get; set; } = "169.254.0.1";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "admin";

    [JsonPropertyName("password_b64")]
    public string PasswordBase64 { get; set; } = "";

    [JsonPropertyName("polling_interval_s")]
    public int PollingIntervalSeconds { get; set; } = 5;

    [JsonPropertyName("request_timeout_s")]
    public int RequestTimeoutSeconds { get; set; } = 8;

    /// <summary>
    /// SHA-256 thumbprint (hex) of the gateway TLS certificate, pinned on first successful
    /// connection (trust-on-first-use). Empty means "not yet learned"; once set, a certificate
    /// whose thumbprint differs is rejected instead of silently accepted.
    /// </summary>
    [JsonPropertyName("cert_thumbprint")]
    public string CertThumbprint { get; set; } = "";

    [JsonIgnore]
    public string Password
    {
        get => ProtectedSecret.Decode(PasswordBase64);
        set => PasswordBase64 = ProtectedSecret.Encode(value);
    }
}

public sealed class NodeCalibration
{
    [JsonPropertyName("zero_a")]
    public double? ZeroA { get; set; }

    [JsonPropertyName("zero_b")]
    public double? ZeroB { get; set; }

    [JsonIgnore]
    public bool HasZero => ZeroA.HasValue || ZeroB.HasValue;
}

public sealed class AppConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    [JsonPropertyName("connection")]
    public ConnectionConfig Connection { get; set; } = new();

    [JsonPropertyName("thresholds")]
    public Thresholds Thresholds { get; set; } = new();

    [JsonPropertyName("telegram")]
    public TelegramConfig Telegram { get; set; } = new();

    [JsonPropertyName("email")]
    public EmailConfig Email { get; set; } = new();

    [JsonPropertyName("sms")]
    public SmsConfig Sms { get; set; } = new();

    [JsonPropertyName("push")]
    public PushConfig Push { get; set; } = new();

    [JsonPropertyName("webhook")]
    public WebhookConfig Webhook { get; set; } = new();

    [JsonPropertyName("nodes")]
    public List<int> Nodes { get; set; } = [];

    [JsonPropertyName("node_calibration")]
    public Dictionary<int, NodeCalibration> NodeCalibration { get; set; } = [];

    [JsonPropertyName("plot_buffer_points")]
    public int PlotBufferPoints { get; set; } = 1000;

    [JsonPropertyName("language")]
    public string Language { get; set; } = "ru";

    public NodeCalibration? GetCalibration(int nodeId)
    {
        return NodeCalibration.TryGetValue(nodeId, out var c) ? c : null;
    }

    public void SetCalibration(int nodeId, double? zeroA, double? zeroB)
    {
        if (zeroA is null && zeroB is null)
        {
            NodeCalibration.Remove(nodeId);
            return;
        }

        NodeCalibration[nodeId] = new NodeCalibration { ZeroA = zeroA, ZeroB = zeroB };
    }

    public void ClearCalibration(int nodeId)
    {
        NodeCalibration.Remove(nodeId);
    }

    public static AppConfig LoadDefault()
    {
        return Load(ResolveDefaultPath());
    }

    public static AppConfig Load(string path)
    {
        // Try the primary file, then the atomic-write backup. Only fall back to defaults if both
        // are unreadable — and never silently clobber a corrupt file (quarantine it instead) so a
        // single bad read can't wipe gateway credentials, thresholds and saved chat ids.
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var text = File.ReadAllText(candidate);
                var loaded = JsonSerializer.Deserialize<AppConfig>(text, JsonOptions);
                if (loaded is not null)
                {
                    // One-time scrub: older versions persisted the Telegram bot token to disk
                    // (bot_token / bot_token_b64). The token is no longer stored, so re-save once to
                    // drop the stale key from config.json (the property no longer serializes it).
                    if (text.Contains("bot_token", StringComparison.OrdinalIgnoreCase))
                    {
                        try { loaded.Save(path); } catch { /* best-effort scrub */ }
                    }

                    return loaded;
                }
            }
            catch
            {
                // Try the backup next, then quarantine below.
            }
        }

        QuarantineCorruptFile(path);
        return new AppConfig();
    }

    private static void QuarantineCorruptFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Move(path, $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}");
            }
        }
        catch
        {
            // If we cannot move it, leave it; the next atomic Save overwrites it safely.
        }
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static string ResolveDefaultPath()
    {
        var basePath = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (File.Exists(basePath))
        {
            return basePath;
        }

        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "config.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? basePath
            : Path.Combine(localAppData, "LS Monitoring", "config.json");
    }
}
