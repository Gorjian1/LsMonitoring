using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using LsMonitoring.Core.Models;

namespace LsMonitoring.Core.Configuration;

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

public sealed class AlarmConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("sound")]
    public bool Sound { get; set; } = true;

    [JsonPropertyName("popup")]
    public bool Popup { get; set; } = true;

    [JsonPropertyName("log_to_csv")]
    public bool LogToCsv { get; set; } = true;

    [JsonPropertyName("invalid_behavior")]
    public string InvalidBehavior { get; set; } = "mark";

    [JsonPropertyName("invalid_alarm_minutes")]
    public int InvalidAlarmMinutes { get; set; } = 5;
}

public sealed class TelegramConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("bot_token")]
    public string BotToken { get; set; } = "";

    [JsonPropertyName("chat_ids")]
    public List<long> ChatIds { get; set; } = [];

    [JsonIgnore]
    public string EffectiveBotToken => TelegramSecrets.ResolveBotToken(BotToken);
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

    [JsonIgnore]
    public string Password
    {
        get => DecodeProtectedString(PasswordBase64);
        set => PasswordBase64 = EncodeProtectedString(value);
    }

    [JsonIgnore]
    public string RelayToken
    {
        get => DecodeProtectedString(RelayTokenBase64);
        set => RelayTokenBase64 = EncodeProtectedString(value);
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

    private static string DecodeProtectedString(string value)
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
                    // Fallback for configs saved without DPAPI.
                }
            }

            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }

    private static string EncodeProtectedString(string value)
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
                // Fallback for systems where DPAPI is unavailable.
            }
        }

        return Convert.ToBase64String(bytes);
    }
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

    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = "";

    [JsonPropertyName("sender")]
    public string Sender { get; set; } = "";

    [JsonPropertyName("phone_numbers")]
    public List<string> PhoneNumbers { get; set; } = [];

    [JsonPropertyName("max_messages_per_hour")]
    public int MaxMessagesPerHour { get; set; } = DefaultMaxMessagesPerHour;

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

    [JsonPropertyName("app_token")]
    public string AppToken { get; set; } = "";

    [JsonPropertyName("client_token")]
    public string ClientToken { get; set; } = "";

    [JsonPropertyName("app_download_url")]
    public string AppDownloadUrl { get; set; } = "";

    [JsonPropertyName("auto_start_temporary_tunnel")]
    public bool AutoStartTemporaryTunnel { get; set; } = true;

    [JsonPropertyName("local_server_url")]
    public string LocalServerUrl { get; set; } = DefaultLocalServerUrl;

    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

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

    [JsonIgnore]
    public bool UsesTryCloudflareTunnel
    {
        get
        {
            if (!Uri.TryCreate(EffectiveServerUrl, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return uri.Host.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Auto-start logic intentionally does NOT require <see cref="Enabled"/>: the tunnel + local Gotify
    /// bootstrap so the Connect QR is ready before the user toggles the channel on.
    /// </summary>
    [JsonIgnore]
    public bool ShouldAutoStartTemporaryTunnel =>
        AutoStartTemporaryTunnel &&
        (string.IsNullOrWhiteSpace(EffectiveServerUrl) || UsesTryCloudflareTunnel);

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

    [JsonPropertyName("secret")]
    public string Secret { get; set; } = "";

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

    [JsonIgnore]
    public string Password
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PasswordBase64))
            {
                return "";
            }

            try
            {
                var bytes = Convert.FromBase64String(PasswordBase64);
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                    }
                    catch
                    {
                        // Fallback
                    }
                }
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return "";
            }
        }
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                PasswordBase64 = "";
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                    }
                    catch
                    {
                        // Fallback
                    }
                }
                PasswordBase64 = Convert.ToBase64String(bytes);
            }
        }
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

    [JsonPropertyName("alarm")]
    public AlarmConfig Alarm { get; set; } = new();

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
        if (!File.Exists(path))
        {
            return new AppConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
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
