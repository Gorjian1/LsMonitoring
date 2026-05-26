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
```

## Запуск

```powershell
dotnet run --project .\LsMonitoring.AlertRelay\LsMonitoring.AlertRelay.csproj
```

В приложении укажите relay URL вида:

```text
https://alerts.example.com/api/alerts/email
```
