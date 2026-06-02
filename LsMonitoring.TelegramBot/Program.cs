using System.Net;
using System.Text;
using System.Text.Json;
using LsMonitoring.TelegramBot;

// Local companion process for the Telegram channel. Holds the bot token, runs the single
// getUpdates poller (no 409 conflicts) and exposes a tiny localhost-only HTTP API the desktop
// app drives. Bound to 127.0.0.1 only; an optional shared key (--key) gates every endpoint
// except /health so other local processes can't drive the bot.

var port = ArgInt(args, "--port") ?? EnvInt("LSMONITORING_TELEGRAM_BOT_PORT") ?? 8771;
var key = ArgValue(args, "--key") ?? Environment.GetEnvironmentVariable("LSMONITORING_TELEGRAM_BOT_KEY") ?? "";

var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
using var host = new BotHost();

using var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
try
{
    listener.Start();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ls-telegram-bot: failed to bind 127.0.0.1:{port}: {ex.Message}");
    return 1;
}

Console.WriteLine($"ls-telegram-bot listening on 127.0.0.1:{port}");

while (true)
{
    HttpListenerContext ctx;
    try
    {
        ctx = await listener.GetContextAsync();
    }
    catch
    {
        break; // listener stopped/disposed → exit
    }

    _ = Task.Run(() => HandleAsync(ctx));
}

return 0;

async Task HandleAsync(HttpListenerContext ctx)
{
    try
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "";
        var method = ctx.Request.HttpMethod;

        if (path == "/health" && method == "GET")
        {
            await WriteJson(ctx, 200, new { ok = true });
            return;
        }

        if (!string.IsNullOrEmpty(key) && ctx.Request.Headers["X-LS-Bot-Key"] != key)
        {
            await WriteJson(ctx, 401, new { error = "unauthorized" });
            return;
        }

        switch (path, method)
        {
            case ("/config", "POST"):
            {
                var req = await ReadJson<ConfigRequest>(ctx);
                await host.ConfigureAsync(req?.BotToken ?? "", req?.LinkCode ?? "", req?.ChatIds ?? []);
                await WriteJson(ctx, 200, new { ok = true });
                break;
            }
            case ("/state", "GET"):
                await WriteJson(ctx, 200, new { chatIds = host.ChatIds, lastError = host.LastError, configured = host.Configured });
                break;
            case ("/test", "POST"):
            {
                var ok = await host.SendTestAsync();
                await WriteJson(ctx, 200, new { ok, error = ok ? null : host.LastError });
                break;
            }
            case ("/alarm", "POST"):
            {
                var req = await ReadJson<AlarmRequest>(ctx);
                if (req is not null)
                {
                    await host.UpdateAlarmAsync(req.NodeId, req.Axis ?? "", req.IsCritical, req.Value, req.Timestamp);
                }

                await WriteJson(ctx, 200, new { ok = true });
                break;
            }
            default:
                await WriteJson(ctx, 404, new { error = "not found" });
                break;
        }
    }
    catch (Exception ex)
    {
        try { await WriteJson(ctx, 500, new { error = ex.Message }); } catch { /* client gone */ }
    }
}

async Task<T?> ReadJson<T>(HttpListenerContext ctx)
{
    using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
    var body = await reader.ReadToEndAsync();
    return string.IsNullOrWhiteSpace(body) ? default : JsonSerializer.Deserialize<T>(body, json);
}

async Task WriteJson(HttpListenerContext ctx, int status, object payload)
{
    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = "application/json";
    var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, json);
    ctx.Response.ContentLength64 = bytes.Length;
    await ctx.Response.OutputStream.WriteAsync(bytes);
    ctx.Response.Close();
}

static string? ArgValue(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

static int? ArgInt(string[] args, string name) => int.TryParse(ArgValue(args, name), out var v) ? v : null;

static int? EnvInt(string name) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : null;

internal sealed record ConfigRequest(string? BotToken, string? LinkCode, List<long>? ChatIds);

internal sealed record AlarmRequest(int NodeId, string? Axis, bool IsCritical, double Value, DateTime Timestamp);
