using System.Net;
using System.Net.Mail;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(_ => RelayOptions.Load(builder.Configuration));

var app = builder.Build();

app.MapGet("/health", (RelayOptions options) => Results.Ok(new
{
    ok = true,
    smtpConfigured = options.HasSmtpSettings,
    authConfigured = !string.IsNullOrWhiteSpace(options.ApiKey)
}));

app.MapPost("/api/alerts/email", async (HttpContext context, EmailAlertRequest request, RelayOptions options) =>
{
    if (!options.HasSmtpSettings || string.IsNullOrWhiteSpace(options.ApiKey))
    {
        return Results.Problem("Relay is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (!HasValidBearerToken(context, options.ApiKey))
    {
        return Results.Unauthorized();
    }

    var recipients = request.Recipients
        .Select(x => x.Trim())
        .Where(x => x.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (recipients.Count == 0 || recipients.Count > options.MaxRecipients)
    {
        return Results.BadRequest(new { error = $"Recipients count must be from 1 to {options.MaxRecipients}." });
    }

    if (string.IsNullOrWhiteSpace(request.Subject) || string.IsNullOrWhiteSpace(request.Body))
    {
        return Results.BadRequest(new { error = "Subject and body are required." });
    }

    try
    {
        using var message = new MailMessage
        {
            From = new MailAddress(options.From, options.FromName, Encoding.UTF8),
            Subject = Trim(request.Subject, 160),
            SubjectEncoding = Encoding.UTF8,
            Body = BuildBody(request),
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false
        };

        foreach (var recipient in recipients)
        {
            message.To.Add(new MailAddress(recipient));
        }

        using var smtp = new SmtpClient(options.SmtpHost, options.SmtpPort)
        {
            DeliveryMethod = SmtpDeliveryMethod.Network,
            EnableSsl = options.UseSsl,
            Timeout = 15000,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(options.SmtpUsername, options.SmtpPassword)
        };

        await smtp.SendMailAsync(message);
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Failed to send relay email for installation {InstallationId}.", request.InstallationId);
        return Results.Problem("Failed to send email.", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Run();

static bool HasValidBearerToken(HttpContext context, string expectedToken)
{
    var authorization = context.Request.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(authorization[prefix.Length..].Trim(), expectedToken, StringComparison.Ordinal);
}

static string BuildBody(EmailAlertRequest request)
{
    return
        $"{request.Body.Trim()}\n\n" +
        $"Installation: {request.InstallationId}\n" +
        "Sent via LS Monitoring Alert Relay";
}

static string Trim(string value, int maxLength)
{
    return value.Length <= maxLength ? value : value[..maxLength];
}

public sealed record EmailAlertRequest(
    string InstallationId,
    IReadOnlyList<string> Recipients,
    string Subject,
    string Body);

public sealed class RelayOptions
{
    public string ApiKey { get; init; } = "";
    public string SmtpHost { get; init; } = "";
    public int SmtpPort { get; init; } = 587;
    public bool UseSsl { get; init; } = true;
    public string SmtpUsername { get; init; } = "";
    public string SmtpPassword { get; init; } = "";
    public string From { get; init; } = "";
    public string FromName { get; init; } = "LS Monitoring";
    public int MaxRecipients { get; init; } = 10;

    public bool HasSmtpSettings =>
        !string.IsNullOrWhiteSpace(SmtpHost) &&
        !string.IsNullOrWhiteSpace(From) &&
        !string.IsNullOrWhiteSpace(SmtpUsername) &&
        !string.IsNullOrWhiteSpace(SmtpPassword);

    public static RelayOptions Load(IConfiguration configuration)
    {
        return new RelayOptions
        {
            ApiKey = Read(configuration, "LS_ALERT_RELAY_API_KEY"),
            SmtpHost = Read(configuration, "LS_ALERT_SMTP_HOST"),
            SmtpPort = ReadInt(configuration, "LS_ALERT_SMTP_PORT", 587),
            UseSsl = ReadBool(configuration, "LS_ALERT_SMTP_SSL", true),
            SmtpUsername = Read(configuration, "LS_ALERT_SMTP_USER"),
            SmtpPassword = Read(configuration, "LS_ALERT_SMTP_PASSWORD"),
            From = Read(configuration, "LS_ALERT_SMTP_FROM"),
            FromName = Read(configuration, "LS_ALERT_SMTP_FROM_NAME", "LS Monitoring"),
            MaxRecipients = ReadInt(configuration, "LS_ALERT_MAX_RECIPIENTS", 10)
        };
    }

    private static string Read(IConfiguration configuration, string key, string fallback = "")
    {
        return configuration[key] ?? fallback;
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback)
    {
        return int.TryParse(configuration[key], out var value) && value > 0 ? value : fallback;
    }

    private static bool ReadBool(IConfiguration configuration, string key, bool fallback)
    {
        return bool.TryParse(configuration[key], out var value) ? value : fallback;
    }
}
