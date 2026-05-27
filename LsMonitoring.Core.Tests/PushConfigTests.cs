using LsMonitoring.Core.Configuration;

namespace LsMonitoring.Core.Tests;

public class PushConfigTests
{
    [Fact]
    public void EffectiveServerUrl_TrimsTrailingSlash()
    {
        var config = new PushConfig { ServerUrl = " https://push.example.org/ " };

        Assert.Equal("https://push.example.org", config.EffectiveServerUrl);
    }

    [Fact]
    public void HasDeliverySettings_RequiresServerAndToken()
    {
        var config = new PushConfig
        {
            ServerUrl = "https://push.example.org",
            AppToken = "app-token"
        };

        Assert.True(config.HasDeliverySettings);
    }

    [Fact]
    public void HasClientSettings_RequiresServerAndClientToken()
    {
        var config = new PushConfig
        {
            ServerUrl = "https://push.example.org",
            ClientToken = "client-token"
        };

        Assert.True(config.HasClientSettings);
    }

    [Fact]
    public void EffectiveAppDownloadUrl_UsesStableLatestApkByDefault()
    {
        var config = new PushConfig();

        Assert.Equal(PushConfig.DefaultAppDownloadUrl, config.EffectiveAppDownloadUrl);
    }

    [Fact]
    public void EffectiveAppDownloadUrl_UsesCustomUrlWhenSet()
    {
        var config = new PushConfig
        {
            AppDownloadUrl = " https://example.org/ls-alerts.apk "
        };

        Assert.Equal("https://example.org/ls-alerts.apk", config.EffectiveAppDownloadUrl);
    }

    [Fact]
    public void EffectiveLocalServerUrl_UsesGotifyLocalhostByDefault()
    {
        var config = new PushConfig();

        Assert.Equal("http://localhost:8080", config.EffectiveLocalServerUrl);
    }

    [Fact]
    public void ShouldAutoStartTemporaryTunnel_AllowsEmptyOrTemporaryTunnelUrl()
    {
        var emptyConfig = new PushConfig { Enabled = true, ServerUrl = "" };
        var localhostRunConfig = new PushConfig
        {
            Enabled = true,
            ServerUrl = "https://abc123-something.lhr.life"
        };
        var legacyCloudflareConfig = new PushConfig
        {
            Enabled = true,
            ServerUrl = "https://throwing-expense-asin-cycles.trycloudflare.com"
        };

        Assert.True(emptyConfig.ShouldAutoStartTemporaryTunnel);
        Assert.True(localhostRunConfig.ShouldAutoStartTemporaryTunnel);
        Assert.True(legacyCloudflareConfig.ShouldAutoStartTemporaryTunnel);
    }

    [Fact]
    public void ShouldAutoStartTemporaryTunnel_DoesNotReplacePermanentUrl()
    {
        var config = new PushConfig
        {
            Enabled = true,
            ServerUrl = "https://push.example.org"
        };

        Assert.False(config.ShouldAutoStartTemporaryTunnel);
    }
}
