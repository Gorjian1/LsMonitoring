using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace LsMonitoring.Core.Configuration;

public sealed class Thresholds
{
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
    public string Mode { get; set; } = "absolute";
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
                try
                {
                    bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                }
                catch
                {
                    // Fallback
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
                try
                {
                    bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                }
                catch
                {
                    // Fallback
                }
                PasswordBase64 = Convert.ToBase64String(bytes);
            }
        }
    }
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

    [JsonPropertyName("nodes")]
    public List<int> Nodes { get; set; } = [];

    [JsonPropertyName("plot_buffer_points")]
    public int PlotBufferPoints { get; set; } = 1000;

    [JsonPropertyName("language")]
    public string Language { get; set; } = "ru";

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

        return basePath;
    }
}
