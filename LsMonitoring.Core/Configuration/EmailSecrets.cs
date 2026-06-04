using System.Text;
using System.Text.Json;

namespace LsMonitoring.Core.Configuration;

/// <summary>
/// Resolved SMTP transport settings for sending alert email.
/// </summary>
public sealed record EmailTransport(
    string Host,
    int Port,
    bool UseSsl,
    string From,
    string Username,
    string Password);

/// <summary>
/// Service-email credentials embedded into release builds. The whole transport (host/port/ssl/from/
/// username/password) is injected at release build time from CI secrets as a single Base64 blob —
/// the source repo never contains it. Base64 avoids any escaping issues with special characters in
/// the password. Dev builds without <c>EmailSecrets.Local.cs</c> resolve to <c>null</c> (service mode
/// is then unavailable; the "alternative" SMTP mode still works).
/// </summary>
public static partial class EmailSecrets
{
    private const string ConfigEnvironmentVariable = "LSMONITORING_EMAIL_CONFIG_B64";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Returns the embedded service-email transport, or <c>null</c> when no credentials are embedded
    /// (e.g. a local dev build) or the blob is malformed.
    /// </summary>
    public static EmailTransport? Resolve()
    {
        var b64 = Environment.GetEnvironmentVariable(ConfigEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(b64))
        {
            b64 = "";
            GetEmbeddedEmailConfigBase64(ref b64);
        }

        if (string.IsNullOrWhiteSpace(b64))
        {
            return null;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64.Trim()));
            var transport = JsonSerializer.Deserialize<EmailTransport>(json, JsonOptions);

            if (transport is null ||
                string.IsNullOrWhiteSpace(transport.Host) ||
                string.IsNullOrWhiteSpace(transport.From))
            {
                return null;
            }

            // Normalise: username falls back to From, port to 587.
            var username = string.IsNullOrWhiteSpace(transport.Username) ? transport.From : transport.Username;
            var port = transport.Port > 0 ? transport.Port : 587;
            return transport with { Username = username, Port = port };
        }
        catch
        {
            return null;
        }
    }

    static partial void GetEmbeddedEmailConfigBase64(ref string value);
}
