using LsMonitoring.Core.Alarms;
using LsMonitoring.Core.Configuration;

namespace LsMonitoring.Core.Tests;

public sealed class EmailAlertServiceTests
{
    [Fact]
    public async Task UpdateAlarmAsync_SendsResolvedEmailWhenEnabled()
    {
        var sender = new CapturingSender();
        var service = new EmailAlertService(BuildConfig(sendResolved: true), sender);
        var start = new DateTime(2026, 5, 26, 10, 0, 0);

        await service.UpdateAlarmAsync(6989, "A", true, -12, start);
        await service.UpdateAlarmAsync(6989, "A", true, -15, start.AddSeconds(30));
        await service.UpdateAlarmAsync(6989, "A", false, -1, start.AddMinutes(2));

        // One "started" email + one "resolved" email; the mid-alarm update is deduped.
        Assert.Equal(2, sender.Sends.Count);
    }

    [Fact]
    public async Task UpdateAlarmAsync_SkipsResolvedEmailWhenDisabledAndClearsActiveAlarm()
    {
        var sender = new CapturingSender();
        var service = new EmailAlertService(BuildConfig(sendResolved: false), sender);
        var start = new DateTime(2026, 5, 26, 10, 0, 0);

        await service.UpdateAlarmAsync(6989, "A", true, -12, start);
        await service.UpdateAlarmAsync(6989, "A", false, -1, start.AddMinutes(2));
        await service.UpdateAlarmAsync(6989, "A", true, -13, start.AddMinutes(3));

        // Two "started" emails (the alarm cleared in between), no "resolved" email.
        Assert.Equal(2, sender.Sends.Count);
    }

    [Fact]
    public async Task UpdateAlarmAsync_SerializesConcurrentAxesAndSendsBothStartedEmails()
    {
        var sender = new CapturingSender { Delay = TimeSpan.FromMilliseconds(20) };
        var service = new EmailAlertService(BuildConfig(sendResolved: false), sender);
        var start = new DateTime(2026, 5, 26, 10, 0, 0);

        await Task.WhenAll(
            service.UpdateAlarmAsync(6989, "A", true, -12, start),
            service.UpdateAlarmAsync(6989, "B", true, 13, start));

        Assert.Equal(2, sender.Sends.Count);
        Assert.Contains(sender.Sends, send => send.Subject.Contains("ось A", StringComparison.Ordinal));
        Assert.Contains(sender.Sends, send => send.Subject.Contains("ось B", StringComparison.Ordinal));
    }

    private static EmailConfig BuildConfig(bool sendResolved)
    {
        // Alternative (own) SMTP mode so ResolveTransport() succeeds without embedded secrets.
        var config = new EmailConfig
        {
            Enabled = true,
            DeliveryMode = EmailDeliveryMode.Smtp,
            From = "alerts@example.org",
            SmtpHost = "smtp.example.org",
            Recipients = ["boss@example.org"],
            SendResolvedNotifications = sendResolved
        };
        return config;
    }

    private sealed class CapturingSender : IEmailSender
    {
        private readonly object _lock = new();

        public TimeSpan Delay { get; init; }
        public List<(string Subject, IReadOnlyList<string> Recipients)> Sends { get; } = [];

        public async Task<EmailSendResult> SendAsync(
            EmailTransport transport,
            string subject,
            string body,
            IReadOnlyList<string> recipients,
            CancellationToken cancellationToken = default)
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            lock (_lock)
            {
                Sends.Add((subject, recipients));
            }

            return EmailSendResult.Ok();
        }
    }
}
