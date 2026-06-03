namespace LsMonitoring.Core.Configuration;

public static partial class TelegramSecrets
{
    private const string BotTokenEnvironmentVariable = "LSMONITORING_TELEGRAM_BOT_TOKEN";

    /// <summary>
    /// @username of the shared bot (without the leading '@'). Not a secret — the bot name is
    /// public in Telegram — so it lives as a committed constant. Set this to the username of the
    /// bot whose token is injected via the LS_TELEGRAM_BOT_TOKEN CI secret.
    /// </summary>
    public const string DefaultBotUsername = "ls_monitoringbot";

    /// <summary>Resolves the bot @username, preferring a caller-supplied override.</summary>
    public static string ResolveBotUsername(string? configuredUsername)
    {
        var trimmed = (configuredUsername ?? "").Trim().TrimStart('@');
        return trimmed.Length > 0 ? trimmed : DefaultBotUsername;
    }

    public static string ResolveBotToken(string configuredToken)
    {
        var environmentToken = Environment.GetEnvironmentVariable(BotTokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentToken))
        {
            return environmentToken.Trim();
        }

        // A user-entered token wins over any build-time embedded token, so each install runs its
        // own bot (no shared token shipped to clients, no concurrent getUpdates 409 conflicts).
        if (!string.IsNullOrWhiteSpace(configuredToken))
        {
            return configuredToken.Trim();
        }

        var embeddedToken = "";
        GetEmbeddedBotToken(ref embeddedToken);
        return embeddedToken.Trim();
    }

    static partial void GetEmbeddedBotToken(ref string token);
}
