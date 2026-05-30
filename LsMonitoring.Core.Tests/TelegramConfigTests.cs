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
}
