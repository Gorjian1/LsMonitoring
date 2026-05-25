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
}
