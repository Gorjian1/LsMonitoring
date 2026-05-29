using System.Net;
using LsMonitoring.Core.Alarms;
using LsMonitoring.Core.Configuration;

namespace LsMonitoring.Core.Tests;

public sealed class SmsAlertServiceTests
{
    [Fact]
    public async Task UpdateAlarmAsync_SendsOnlyStartAndResolvedMessages()
    {
        var handler = new CapturingHandler();
        var service = new SmsAlertService(new SmsConfig
        {
            Enabled = true,
            ApiKey = "api-key",
            ApiUrl = "https://sms.example.test/send",
            PhoneNumbers = ["+79990000000"],
            MaxMessagesPerHour = 10
        }, new HttpClient(handler));

        var start = new DateTime(2026, 5, 26, 10, 0, 0);

        await service.UpdateAlarmAsync(6989, "A", true, -12, start);
        await service.UpdateAlarmAsync(6989, "A", true, -15, start.AddSeconds(30));
        await service.UpdateAlarmAsync(6989, "A", false, -1, start.AddMinutes(2));

        Assert.Equal(2, handler.Requests.Count);

        var body1 = handler.RequestBodies[0];
        var body2 = handler.RequestBodies[1];

        Assert.Contains("api_id=api-key", body1);
        Assert.Contains("to=%2B79990000000", body1);
        Assert.Contains("to=%2B79990000000", body2);
    }

    [Fact]
    public async Task SendTestMessageAsync_RespectsHourlyLimit()
    {
        var handler = new CapturingHandler();
        var service = new SmsAlertService(new SmsConfig
        {
            Enabled = true,
            ApiKey = "api-key",
            ApiUrl = "https://sms.example.test/send",
            PhoneNumbers = ["+79990000000"],
            MaxMessagesPerHour = 1
        }, new HttpClient(handler));

        var first = await service.SendTestMessageAsync();
        var second = await service.SendTestMessageAsync();

        Assert.True(first);
        Assert.False(second);
        Assert.Single(handler.Requests);
        Assert.Contains("максимум 1 сообщений в час", service.LastError);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null)
            {
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }
            else
            {
                RequestBodies.Add("");
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"OK"}""")
            };
        }
    }
}
