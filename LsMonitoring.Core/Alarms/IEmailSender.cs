using System.Net;
using System.Net.Mail;
using System.Text;
using LsMonitoring.Core.Configuration;

namespace LsMonitoring.Core.Alarms;

/// <summary>Result of an attempt to deliver an email.</summary>
public readonly record struct EmailSendResult(bool Success, string? Error)
{
    public static EmailSendResult Ok() => new(true, null);
    public static EmailSendResult Fail(string error) => new(false, error);
}

/// <summary>
/// Abstraction over the actual SMTP delivery so the alert logic (dedup, started/resolved) can be
/// unit-tested with a fake sender and so <see cref="EmailAlertService"/> has no transport dependency.
/// </summary>
public interface IEmailSender
{
    Task<EmailSendResult> SendAsync(
        EmailTransport transport,
        string subject,
        string body,
        IReadOnlyList<string> recipients,
        CancellationToken cancellationToken = default);
}

/// <summary>Real SMTP sender backed by <see cref="SmtpClient"/> (System.Net.Mail).</summary>
public sealed class SmtpEmailSender : IEmailSender
{
    public async Task<EmailSendResult> SendAsync(
        EmailTransport transport,
        string subject,
        string body,
        IReadOnlyList<string> recipients,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(transport.From, "LS Monitoring", Encoding.UTF8),
                Subject = subject,
                SubjectEncoding = Encoding.UTF8,
                Body = body,
                BodyEncoding = Encoding.UTF8,
                IsBodyHtml = false
            };

            foreach (var recipient in recipients)
            {
                message.To.Add(new MailAddress(recipient));
            }

            if (message.To.Count == 0)
            {
                return EmailSendResult.Fail("Не указаны корректные получатели email.");
            }

            using var smtp = new SmtpClient(transport.Host, transport.Port)
            {
                DeliveryMethod = SmtpDeliveryMethod.Network,
                EnableSsl = transport.UseSsl,
                Timeout = 15000,
                UseDefaultCredentials = false
            };

            if (!string.IsNullOrWhiteSpace(transport.Username) || !string.IsNullOrWhiteSpace(transport.Password))
            {
                smtp.Credentials = new NetworkCredential(transport.Username, transport.Password);
            }

            await smtp.SendMailAsync(message, cancellationToken);
            return EmailSendResult.Ok();
        }
        catch (Exception ex)
        {
            return EmailSendResult.Fail(ex.Message);
        }
    }
}
