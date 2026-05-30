using System.Net;
using LsMonitoring.Core.Alarms;
using LsMonitoring.Core.Configuration;

namespace LsMonitoring.Core.Tests;

public sealed class EmailAlertServiceTests
{
    [Fact]
    public async Task UpdateAlarmAsync_SendsResolvedEmailWhenEnabled()
    {
        var handler = new CapturingHandler();
        var service = new EmailAlertService(BuildConfig(sendResolved: true), new HttpClient(handler));
        var start = new DateTime(2026, 5, 26, 10, 0, 0);

        await service.UpdateAlarmAsync(6989, "A", true, -12, start);
        await service.UpdateAlarmAsync(6989, "A", true, -15, start.AddSeconds(30));
        await service.UpdateAlarmAsync(6989, "A", false, -1, start.AddMinutes(2));

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task UpdateAlarmAsync_SkipsResolvedEmailWhenDisabledAndClearsActiveAlarm()
    {
        var handler = new CapturingHandler();
        var service = new EmailAlertService(BuildConfig(sendResolved: false), new HttpClient(handler));
        var start = new DateTime(2026, 5, 26, 10, 0, 0);

        await service.UpdateAlarmAsync(6989, "A", true, -12, start);
        await service.UpdateAlarmAsync(6989, "A", false, -1, start.AddMinutes(2));
        await service.UpdateAlarmAsync(6989, "A", true, -13, start.AddMinutes(3));

        Assert.Equal(2, handler.Requests.Count);
    }

    private static EmailConfig BuildConfig(bool sendResolved)
    {
        var config = new EmailConfig
        {
            Enabled = true,
            RelayUrl = "https://email.example.test/api/alerts/email",
            Recipients = ["boss@example.org"],
            SendResolvedNotifications = sendResolved
        };
        config.RelayToken = "relay-token";
        return config;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""")
            });
        }
    }
}
