using System.Text;
using System.Text.Json;
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
    public void ResolveTransport_OwnSmtp_UsesKnownProviderOrDomainFallback(string sender, string expectedHost)
    {
        var config = new EmailConfig { DeliveryMode = EmailDeliveryMode.Smtp, From = sender };

        var transport = config.ResolveTransport();

        Assert.NotNull(transport);
        Assert.Equal(expectedHost, transport!.Host);
    }

    [Fact]
    public void ResolveTransport_OwnSmtp_UsernameFallsBackToSender()
    {
        var config = new EmailConfig { DeliveryMode = EmailDeliveryMode.Smtp, From = "monitor@example.org" };

        var transport = config.ResolveTransport();

        Assert.NotNull(transport);
        Assert.Equal("monitor@example.org", transport!.Username);
    }

    [Fact]
    public void UsesService_ByDefault()
    {
        var config = new EmailConfig();

        Assert.True(config.UsesService);
        Assert.Equal(EmailDeliveryMode.Service, config.DeliveryMode);
    }

    [Fact]
    public void DeliveryMode_NormalizesLegacyRelayToService()
    {
        var config = new EmailConfig { DeliveryMode = "relay" };

        Assert.Equal(EmailDeliveryMode.Service, config.DeliveryMode);
        Assert.True(config.UsesService);
    }

    [Fact]
    public void HasDeliverySettings_OwnSmtp_RequiresRecipientsAndSender()
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
    public void HasDeliverySettings_Service_FalseWithoutEmbeddedSecret()
    {
        // Dev build (no EmailSecrets.Local.cs) → service transport is null → not deliverable.
        var config = new EmailConfig
        {
            DeliveryMode = EmailDeliveryMode.Service,
            Recipients = ["boss@example.org"]
        };

        Assert.False(config.HasDeliverySettings);
    }

    [Fact]
    public void EmailSecrets_Resolve_ParsesBase64Blob()
    {
        var transport = new EmailTransport("smtp.example.org", 465, true, "svc@example.org", "", "secret");
        var json = JsonSerializer.Serialize(transport);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        const string envVar = "LSMONITORING_EMAIL_CONFIG_B64";
        var previous = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, b64);
            var resolved = EmailSecrets.Resolve();

            Assert.NotNull(resolved);
            Assert.Equal("smtp.example.org", resolved!.Host);
            Assert.Equal(465, resolved.Port);
            Assert.Equal("svc@example.org", resolved.From);
            Assert.Equal("svc@example.org", resolved.Username); // falls back to From
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }

    [Fact]
    public void EmailSecrets_Resolve_ReturnsNullWhenAbsent()
    {
        const string envVar = "LSMONITORING_EMAIL_CONFIG_B64";
        var previous = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "");
            Assert.Null(EmailSecrets.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }
}
