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

        var embeddedToken = "";
        GetEmbeddedBotToken(ref embeddedToken);
        if (!string.IsNullOrWhiteSpace(embeddedToken))
        {
            return embeddedToken.Trim();
        }

        return configuredToken.Trim();
    }

    static partial void GetEmbeddedBotToken(ref string token);
}
