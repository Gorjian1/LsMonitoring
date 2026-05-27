# LS Monitoring

> **Windows-приложение для мониторинга LoadSensing / Worldsensing LS-G6.**
> Программа опрашивает gateway, показывает текущие значения и тренды по осям A/B, ведёт локальную историю критических отклонений и отправляет оповещения.

<p align="center">
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows_10%2F11-blue?style=for-the-badge">
  <img alt="Built with" src="https://img.shields.io/badge/built_with-.NET_9_%2B_Avalonia-purple?style=for-the-badge">
  <img alt="Data" src="https://img.shields.io/badge/data-Gateway_CSV-orange?style=for-the-badge">
  <img alt="Notifications" src="https://img.shields.io/badge/alerts-Telegram_Email_SMS_Push-2ea44f?style=for-the-badge">
</p>

---

## Что это

**LS Monitoring** — прикладная программа для работы с LS-G6 tilt sensors. Компьютер с приложением подключается к gateway, регулярно забирает CSV-показания, отображает данные в одном окне и отправляет тревоги по настроенным каналам.

Основной экран остаётся one-window: слева список узлов, в центре графики и последние измерения, справа сводка по выбранному узлу и история критических отклонений.

---

## Возможности

### Мониторинг gateway

- подключение к gateway по IP, логину и паролю;
- ручное добавление Node ID;
- автоматический поиск live-узлов;
- регулярный опрос выбранных узлов;
- ручной разовый опрос;
- загрузка CSV-файла для проверки данных без live-прибора.

### Отображение данных

- текущие значения температуры, A-axis, B-axis, A variation и B variation;
- тренды по осям A/B для выбранного узла;
- выбор окна графика: 15 минут, 1 час, 6 часов или всё;
- ограничение графика с выбранной даты;
- таблица последних измерений;
- мини-графики в списке узлов;
- статус связи, счётчик сообщений и время последнего измерения.

### Пороги и отклонения

- режимы расчёта по углам A/B или вариациям ΔA/ΔB;
- настройка нуля и критических порогов для A и B;
- быстрый сброс и установка нуля по текущему измерению;
- визуальное выделение критики;
- баннер тревоги в окне;
- локальная история критических отклонений: узел, ось, время начала, длительность, последнее значение, пик и порог;
- очистка истории отклонений из правой панели.

### Оповещения

- **Telegram**: тестовое сообщение, автоопределение chat ID после `/start`, старт тревоги, обновление активной тревоги и завершение.
- **Push**: отправка через Gotify, QR для скачивания Android-приложения LS Alerts, QR для подключения телефона, обновляемое активное push-уведомление и завершение.
- **Почта**: отправка через сервисный relay или свой SMTP, тестовое письмо, старт тревоги и завершение тревоги.
- **SMS**: отправка через `sms.ru`, тестовая SMS, старт тревоги, завершение тревоги, лимит сообщений в час и защита от повторов по активной тревоге.
- **Webhook**: сохранение URL и секрета в настройках оповещений.

Telegram и Push поддерживают обновление активной тревоги с длительностью. Почта и SMS отправляют только два события: старт и завершение.

### Диагностика

Окно диагностики показывает:

- версию приложения;
- путь к `config.json`;
- путь к локальной истории отклонений;
- IP и состояние gateway;
- состояние опроса;
- состояние Telegram, почты, SMS и Push;
- последние ошибки каналов оповещений;
- публичный Push URL, состояние tunnel URL и доступность Gotify;
- проверку новой версии через GitHub Releases.

---

## Рабочий сценарий

1. Подключите компьютер к сети gateway.
2. Откройте **Настройки** и укажите IP, логин и пароль gateway.
3. Нажмите **Поиск** или добавьте Node ID вручную.
4. Нажмите **Старт** для регулярного мониторинга.
5. Настройте пороги и нули по осям A/B.
6. Откройте **Оповещения**, выберите нужные каналы и отправьте тест.
7. Проверяйте графики, последние измерения, сводку и историю отклонений в основном окне.

---

## Что показывает интерфейс

| Блок | Назначение |
|---|---|
| **Gateway status** | IP gateway и состояние связи. |
| **Nodes** | Узлы, модель, актуальность данных, мини-тренд и быстрые действия нуля. |
| **Trend** | Графики A/B по выбранному узлу. |
| **Recent readings** | Последние строки данных: время, температура, оси, вариации, отклонения и флаги. |
| **Summary** | Сводка по выбранному узлу: модель, точки, интервал, последнее измерение, пороги и нули. |
| **Deviation history** | Локальная история критических отклонений. |
| **Notifications** | Настройка Telegram, почты, Push, SMS и webhook-параметров. |
| **Diagnostics** | Версия, пути файлов, состояние gateway, каналов, Gotify и обновлений. |

---

## Данные и безопасность

LS Monitoring работает с gateway и локальными файлами на компьютере пользователя.

- параметры подключения хранятся в `config.json`;
- пароль gateway сохраняется в `password_b64`;
- пароль SMTP и relay token сохраняются в защищённом base64-поле с DPAPI на Windows;
- локальная история отклонений хранится в `%LOCALAPPDATA%\LS Monitoring\deviation-history.json`;
- runtime-файлы `config.json`, `data/`, `logs/`, `dist/`, `.dotnet-cli-home/` не предназначены для Git;
- Telegram token, chat ID, SMS API key, SMTP-пароли и Gotify token не должны попадать в публичный репозиторий.

---

## Быстрый старт

```powershell
dotnet restore .\LsMonitoring.sln
dotnet run --project .\LsMonitoring.Avalonia\LsMonitoring.Avalonia.csproj
```

Для проверки без gateway можно нажать **CSV** и выбрать CSV-файл с показаниями LS-G6. Данные из файла отображаются в том же интерфейсе мониторинга.

---

## Для разработчиков

### Требования

- .NET SDK 9.0;
- Windows 10/11 для основной desktop-сборки;
- Java 17 и Android SDK для сборки Android-приложения LS Alerts;
- Inno Setup для локальной сборки Windows installer.

### Команды

```powershell
dotnet restore .\LsMonitoring.sln
dotnet build .\LsMonitoring.sln
dotnet test .\LsMonitoring.sln
dotnet run --project .\LsMonitoring.Avalonia\LsMonitoring.Avalonia.csproj
```

### Windows publish

```powershell
dotnet publish .\LsMonitoring.Avalonia\LsMonitoring.Avalonia.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

### Release workflow

GitHub Actions запускает release job по тегам `v*`.

Workflow собирает:

- self-contained desktop exe;
- portable zip;
- Inno Setup installer;
- signed Android APK `ls-alerts-<version>.apk`;
- stable Android APK asset `ls-alerts-latest.apk` for the QR download link.

Перед публикацией релиза workflow проверяет наличие артефактов, версию desktop exe и подпись APK.

---

## Структура проекта

```text
LsMonitoring.Core/          # gateway, CSV parser, buffers, thresholds, alerts, update check
LsMonitoring.Avalonia/      # desktop UI на Avalonia
LsMonitoring.Core.Tests/    # unit-тесты ядра
LsMonitoring.MobileAlerts/  # Android-приложение LS Alerts для push
LsMonitoring.AlertRelay/    # HTTP relay для сервисной почты
build/installer/            # Inno Setup installer script
.github/workflows/          # build, test и release workflow
```

---

## Назначение проекта

LS Monitoring используется как рабочая панель наблюдения за LS-G6: подключиться к gateway, увидеть состояние узлов, контролировать отклонения по A/B и получить оповещение, когда значение вышло за заданный порог.

<p align="center">
  <strong>LS Monitoring</strong><br>
  Мониторинг LS-G6 без лишней ручной работы.
</p>
