using LsMonitoring.Core.Configuration;

namespace LsMonitoring.Core.Tests;

public class EmailConfigTests
{
    [Theory]
    [InlineData("monitor@gmail.com", "smtp.gmail.com")]
    [InlineData("monitor@yandex.ru", "smtp.yandex.ru")]
    [InlineData("monitor@mail.ru", "smtp.mail.ru")]
    [InlineData("monitor@outlook.com", "smtp-mail.outlook.com")]
    [InlineData("monitor@example.org", "smtp.example.org")]
    public void EffectiveSmtpHost_UsesKnownProviderOrDomainFallback(string sender, string expectedHost)
    {
        var config = new EmailConfig { From = sender };

        Assert.Equal(expectedHost, config.EffectiveSmtpHost);
    }

    [Fact]
    public void EffectiveUsername_FallsBackToSender()
    {
        var config = new EmailConfig { From = "monitor@example.org" };

        Assert.Equal("monitor@example.org", config.EffectiveUsername);
    }

    [Fact]
    public void HasDeliverySettings_RequiresRecipientsSenderAndSmtpHost()
    {
        var config = new EmailConfig
        {
            DeliveryMode = EmailDeliveryMode.Smtp,
            From = "monitor@example.org",
            Recipients = ["boss@example.org"]
        };

        Assert.True(config.HasDeliverySettings);
    }

    [Fact]
    public void UsesRelay_ByDefault()
    {
        var config = new EmailConfig();

        Assert.True(config.UsesRelay);
    }

    [Fact]
    public void HasRelaySettings_RequiresRecipientsUrlAndToken()
    {
        var config = new EmailConfig
        {
            RelayUrl = "https://alerts.example.org/api/alerts/email",
            Recipients = ["boss@example.org"]
        };
        config.RelayToken = "installation-secret";

        Assert.True(config.HasRelaySettings);
    }
}
