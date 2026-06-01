namespace LsMonitoring.Core.Configuration;

public static partial class TelegramSecrets
{
    private const string BotTokenEnvironmentVariable = "LSMONITORING_TELEGRAM_BOT_TOKEN";

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
