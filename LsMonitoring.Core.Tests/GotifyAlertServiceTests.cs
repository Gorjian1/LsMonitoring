using System.Net;
using System.Text.Json;
using LsMonitoring.Core.Alarms;
using LsMonitoring.Core.Configuration;

namespace LsMonitoring.Core.Tests;

public class GotifyAlertServiceTests
{
    [Fact]
    public async Task UpdateAlarmAsync_SendsStructuredActiveAndResolvedEvents()
    {
        var handler = new CapturingHandler();
        var service = new GotifyAlertService(new PushConfig
        {
            Enabled = true,
            ServerUrl = "https://push.example.org/",
            AppToken = "app-token",
            Priority = 7
        }, new HttpClient(handler));

        var startTime = new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Local);
        var resolveTime = startTime.AddSeconds(95);

        await service.UpdateAlarmAsync(6989, "A", true, -18.747, startTime);
        await service.UpdateAlarmAsync(6989, "A", false, -1.25, resolveTime);

        Assert.Equal(2, handler.RequestBodies.Count);

        using var activePayload = JsonDocument.Parse(handler.RequestBodies[0]);
        var activeAlarm = activePayload.RootElement
            .GetProperty("extras")
            .GetProperty(GotifyAlertExtras.AlarmKey);

        Assert.Equal("active", activeAlarm.GetProperty("event").GetString());
        Assert.Equal(6989, activeAlarm.GetProperty("nodeId").GetInt32());
        Assert.Equal("A", activeAlarm.GetProperty("axis").GetString());
        Assert.Equal(-18.747, activeAlarm.GetProperty("value").GetDouble(), 3);
        Assert.Equal(7, activePayload.RootElement.GetProperty("priority").GetInt32());

        using var resolvedPayload = JsonDocument.Parse(handler.RequestBodies[1]);
        var resolvedAlarm = resolvedPayload.RootElement
            .GetProperty("extras")
            .GetProperty(GotifyAlertExtras.AlarmKey);

        Assert.Equal("resolved", resolvedAlarm.GetProperty("event").GetString());
        Assert.Equal(95, resolvedAlarm.GetProperty("durationSeconds").GetInt32());
        Assert.True(resolvedAlarm.TryGetProperty("resolvedAt", out _));
    }

    [Fact]
    public async Task UpdateAlarmAsync_SerializesConcurrentAxesAndSendsBothActiveEvents()
    {
        var handler = new CapturingHandler { Delay = TimeSpan.FromMilliseconds(20) };
        var service = new GotifyAlertService(new PushConfig
        {
            Enabled = true,
            ServerUrl = "https://push.example.org/",
            AppToken = "app-token",
            Priority = 7
        }, new HttpClient(handler));
        var startTime = new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Local);

        await Task.WhenAll(
            service.UpdateAlarmAsync(6989, "A", true, -18.747, startTime),
            service.UpdateAlarmAsync(6989, "B", true, 11.25, startTime));

        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Contains(handler.RequestBodies, body => ReadAlarmAxis(body) == "A");
        Assert.Contains(handler.RequestBodies, body => ReadAlarmAxis(body) == "B");
    }

    private static string? ReadAlarmAxis(string body)
    {
        using var payload = JsonDocument.Parse(body);
        return payload.RootElement
            .GetProperty("extras")
            .GetProperty(GotifyAlertExtras.AlarmKey)
            .GetProperty("axis")
            .GetString();
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly object _lock = new();

        public TimeSpan Delay { get; init; }
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);

            lock (_lock)
            {
                RequestBodies.Add(body);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        }
    }
}
