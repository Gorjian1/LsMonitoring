# LS Monitoring Alert Relay

Минимальный HTTP relay для технической почты LS Monitoring.

Приложение отправляет тревогу на `POST /api/alerts/email`, а relay отправляет письмо через технический SMTP-ящик. Так пароль от ящика не попадает в desktop-приложение.

## Переменные окружения

```powershell
$env:LS_ALERT_RELAY_API_KEY = "installation-secret"
$env:LS_ALERT_SMTP_HOST = "smtp.example.com"
$env:LS_ALERT_SMTP_PORT = "587"
$env:LS_ALERT_SMTP_SSL = "true"
$env:LS_ALERT_SMTP_USER = "alerts@example.com"
$env:LS_ALERT_SMTP_PASSWORD = "app-password"
$env:LS_ALERT_SMTP_FROM = "alerts@example.com"
$env:LS_ALERT_SMTP_FROM_NAME = "LS Monitoring"
$env:LS_ALERT_MAX_RECIPIENTS = "10"          # макс. получателей в одном запросе
$env:LS_ALERT_RATE_LIMIT_PER_HOUR = "60"     # лимит запросов /api/alerts/email на установку (или IP) в час
```

## Ограничение частоты (rate limit)

`POST /api/alerts/email` ограничен скользящим окном в 1 час: не более `LS_ALERT_RATE_LIMIT_PER_HOUR`
запросов на одну установку (ключ — заголовок `X-LS-Installation-Id`; при его отсутствии — клиентский
IP, с учётом `X-Forwarded-For`). При превышении relay отвечает `429 Too Many Requests`. Это не даёт
утёкшему токену превратить relay в открытый спам-шлюз через технический SMTP.

## Запуск

```powershell
dotnet run --project .\LsMonitoring.AlertRelay\LsMonitoring.AlertRelay.csproj
```

В приложении укажите relay URL вида:

```text
https://alerts.example.com/api/alerts/email
```
