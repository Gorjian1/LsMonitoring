using System.Text.Json;
using LsMonitoring.Core.Configuration;

namespace LsMonitoring.Core.Tests;

public class TelegramConfigTests
{
    [Fact]
    public void EnsureLinkCode_GeneratesStableValidCode()
    {
        var config = new TelegramConfig();

        var first = config.EnsureLinkCode();
        var second = config.EnsureLinkCode();

        Assert.Equal(first, second);
        Assert.True(TelegramConfig.IsValidLinkCode(first));
    }

    [Fact]
    public void EnsureLinkCode_NormalizesExistingCode()
    {
        var config = new TelegramConfig { LinkCode = " ab12cd " };

        Assert.Equal("AB12CD", config.EnsureLinkCode());
    }

    [Fact]
    public void BotToken_IsNeverSerializedToJson()
    {
        // The token is embedded in the binary (injected at release build from CI secrets).
        // It must NEVER end up in config.json — keeping it off disk is the whole point.
        var config = new TelegramConfig
        {
            BotToken = "123456:AABBCCDD-token",
            Enabled = true,
            ChatIds = [42L]
        };

        var json = JsonSerializer.Serialize(config);

        Assert.DoesNotContain("123456", json);
        Assert.DoesNotContain("AABBCCDD", json);
    }

    [Fact]
    public void BotToken_IsNotRestoredAfterRoundTrip()
    {
        // Round-trip: serialize then deserialize. The token must not survive.
        // chat_ids should survive (they are not secrets).
        var config = new TelegramConfig
        {
            BotToken = "123456:AABBCCDD-token",
            Enabled = true,
            ChatIds = [42L, 99L]
        };

        var json = JsonSerializer.Serialize(config);
        var back = JsonSerializer.Deserialize<TelegramConfig>(json)!;

        Assert.Equal("", back.BotToken);
        Assert.Contains(42L, back.ChatIds);
        Assert.Contains(99L, back.ChatIds);
    }

    [Fact]
    public void ResolveBotToken_EnvOverridesEmbedded()
    {
        // env variable has the highest priority — operators on their own machine can
        // override the embedded token without rebuilding.
        const string envVar = "LSMONITORING_TELEGRAM_BOT_TOKEN";
        var previous = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "env-token-999");
            var resolved = TelegramSecrets.ResolveBotToken("config-token");
            Assert.Equal("env-token-999", resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }

    [Fact]
    public void ResolveBotToken_ConfigOverridesEmbedded()
    {
        const string envVar = "LSMONITORING_TELEGRAM_BOT_TOKEN";
        var previous = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, null);
            // No env var and no embedded token → config token wins.
            var resolved = TelegramSecrets.ResolveBotToken("config-token-42");
            Assert.Equal("config-token-42", resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }
}
