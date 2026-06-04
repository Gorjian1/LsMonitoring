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
        public List<(string Subject, IReadOnlyList<string> Recipients)> Sends { get; } = [];

        public Task<EmailSendResult> SendAsync(
            EmailTransport transport,
            string subject,
            string body,
            IReadOnlyList<string> recipients,
            CancellationToken cancellationToken = default)
        {
            Sends.Add((subject, recipients));
            return Task.FromResult(EmailSendResult.Ok());
        }
    }
}
