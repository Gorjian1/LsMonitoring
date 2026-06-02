using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(_ => RelayOptions.Load(builder.Configuration));

// Rate-limit the email endpoint so a leaked Bearer token can't turn the relay into an open
// spam gateway through our SMTP. Fixed window, partitioned per installation (falling back to
// client IP when the header is absent/spoofed). Limits come from config so they can be tuned
// without a redeploy.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("email", context =>
    {
        var relayOptions = context.RequestServices.GetRequiredService<RelayOptions>();
        return RateLimitPartition.GetFixedWindowLimiter(
            ResolveRateLimitPartitionKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromHours(1),
                PermitLimit = relayOptions.RateLimitPerHour,
                QueueLimit = 0
            });
    });
});

var app = builder.Build();
app.UseRateLimiter();

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
}).RequireRateLimiting("email");

app.Run();

static bool HasValidBearerToken(HttpContext context, string expectedToken)
{
    var authorization = context.Request.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var provided = authorization[prefix.Length..].Trim();
    // Constant-time compare so a leaked API key can't be reconstructed byte-by-byte
    // via response-timing analysis. FixedTimeEquals short-circuits on length only.
    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(provided),
        Encoding.UTF8.GetBytes(expectedToken));
}

static string ResolveRateLimitPartitionKey(HttpContext context)
{
    var installationId = context.Request.Headers["X-LS-Installation-Id"].ToString().Trim();
    if (installationId.Length > 0)
    {
        return $"inst:{installationId}";
    }

    // No installation header — fall back to the client IP. Honor the first X-Forwarded-For hop
    // when the relay sits behind a TLS-terminating reverse proxy.
    var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
    var ip = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault();
    if (string.IsNullOrEmpty(ip))
    {
        ip = context.Connection.RemoteIpAddress?.ToString();
    }

    return $"ip:{ip ?? "unknown"}";
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
    public int RateLimitPerHour { get; init; } = 60;

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
            MaxRecipients = ReadInt(configuration, "LS_ALERT_MAX_RECIPIENTS", 10),
            RateLimitPerHour = ReadInt(configuration, "LS_ALERT_RATE_LIMIT_PER_HOUR", 60)
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
