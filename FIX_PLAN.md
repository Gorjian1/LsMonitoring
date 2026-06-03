# LS Monitoring — план исправлений (цель: 0.8)

> Самодостаточный план по итогам полного аудита кода. Каждый пункт можно выполнять
> отдельно, без доступа к исходной беседе: указаны файлы, строки, суть проблемы,
> решение, наброски кода и критерии готовности.
>
> **Вне рамок 0.8** (это уже 0.9): доделка SMS-нюансов и Webhook-отправка.

## Как пользоваться через веб-версию

1. Открой этот файл, возьми один пункт (например `M1-1`).
2. Дай ассистенту такой промпт:
   > «Возьми задачу `M1-3` из `FIX_PLAN.md`, реализуй по описанию, не трогай остальное.
   > Покажи диф и обнови чекбокс статуса в `FIX_PLAN.md`.»
3. Иди сверху вниз по майлстоунам: M1 → M2 → M3 → M4 → M5. Внутри M1–M2 порядок важен (есть зависимости), в M5 — нет.
4. После каждого пункта прогоняй `dotnet build` + `dotnet test` (см. «Чек-лист проверки» внизу).

## Легенда статусов

- `[ ]` — не начато
- `[~]` — в работе
- `[x]` — готово (сборка зелёная, тесты зелёные, критерии выполнены)

## Сводка по приоритетам

| ID | Приоритет | Тема | Тип |
|----|-----------|------|-----|
| M1-1 | 🔴 крит | Отозвать и убрать Telegram-токен с клиента | безопасность |
| M1-2 | 🔴 крит | Серверный Telegram-relay ИЛИ поле «свой бот» | архитектура |
| M1-3 | 🟠 высок | Атомарная запись config/history (+ .bak) | надёжность |
| M1-4 | 🟠 высок | Не затирать конфиг молча при ошибке чтения | надёжность |
| M2-1 | 🟡 средн | Оживить или убрать настройки тревог (dead config) | корректность |
| M2-2 | 🟡 средн | Починить счётчик сообщений | корректность |
| M2-3 | 🟡 средн | Не слать реальные тревоги при загрузке CSV | корректность |
| M3-1 | 🟡 средн | `IDisposable` для алерт-сервисов + общий HttpClient | утечки |
| M3-2 | 🟡 средн | Диалог Messages: освобождать свои сервисы | утечки |
| M3-3 | 🟡 средн | Убрать гонку параллельного getUpdates (409) | стабильность |
| M3-4 | 🟡 средн | Пиннинг сертификата gateway вместо «любой серт» | безопасность |
| M3-5 | 🟢 низк | `TryParse` в DiscoverNodes, валидация Node ID/IP | устойчивость |
| M4-1 | 🟠 высок | Constant-time сравнение Bearer в relay | безопасность |
| M4-2 | 🟠 высок | Rate-limit в relay | безопасность |
| M4-3 | 🟡 средн | Пиннинг+хеш Gotify (рантайм и CI) | supply-chain |
| M5-1 | 🟢 низк | Единое защищённое хранение всех секретов (DPAPI) | согласованность |
| M5-2 | 🟢 низк | Логи в `%LOCALAPPDATA%`, не по CWD | QoL |
| M5-3 | 🟢 низк | Производительность `TrendPlot.Render` | perf |
| M5-4 | 🟢 низк | Дебаунс/async сохранения на UI-потоке | perf |
| M5-5 | 🟢 низк | Свести двойную логику критичности к `ThresholdEvaluator` | легаси |
| M5-6 | 🟢 низк | Убрать мёртвый код | легаси |
| M5-7 | 🟢 низк | Хардненинг CI (permissions, vuln-scan, analyzers) | CI |
| M5-8 | 🟢 низк | SMS: лимит тратить только при успехе; частичные ошибки | корректность |

---

# M1 — Безопасность и надёжность (делать первым)

## [x] M1-1 — Отозвать Telegram-токен и убрать его из клиента 🔴

> Сделано (код): десктоп больше не отправляет через зашитый токен — бот вынесен в отдельную
> программу. `TelegramSecrets.ResolveBotToken` теперь предпочитает введённый пользователем токен
> встроенному (env → config → embedded).
> ⚠️ ОСТАЁТСЯ РУЧНОЕ ДЕЙСТВИЕ (с телефона/у компа):
> 1. @BotFather → `/revoke` старого бота (токен скомпрометирован).
> 2. Удалить файл `LsMonitoring.Core/Configuration/TelegramSecrets.Local.cs`, чтобы встроенный
>    токен не попадал даже в локальные сборки.

**Проблема.** Живой bot token зашит в `LsMonitoring.Core/Configuration/TelegramSecrets.Local.cs`
(строковый литерал в `GetEmbeddedBotToken`). Файл в `.gitignore`, поэтому в репозиторий не попал,
**но компилируется в локальные сборки** и достаётся из бинаря через `strings`/ILSpy. Токен уже
следует считать скомпрометированным.

**Шаги.**
1. В @BotFather: `/revoke` для текущего бота → получить новый токен. Старый мёртв — утечка закрыта.
2. Удалить встроенный токен из клиентских сборок. Варианты резолва оставить только безопасные —
   см. `LsMonitoring.Core/Configuration/TelegramSecrets.cs` (`ResolveBotToken`):
   - env-переменная `LSMONITORING_TELEGRAM_BOT_TOKEN` (для своей машины);
   - поле `bot_token` в `config.json` (вводится пользователем);
   - **embedded убрать** (или оставить только для локальной dev-сборки, но НЕ в релизных артефактах).
3. Убедиться, что CI-артефакт не содержит токена (он и так не содержит — `Local.cs` в gitignore;
   зафиксировать это явно в `README`/`AGENTS.md`, чтобы случайно не закоммитили).

**Критерий готовности.** В любом распространяемом бинаре (`grep`/`strings` по exe и DLL) нет
строки токена; Telegram включается через env или config-поле.

**Зависимость.** Логически связан с `M1-2` (как пользователь получит рабочего бота).

---

## [x] M1-2 — Telegram: отдельная прога-компаньон + свой бот 🔴

> Реализован выбранный вариант «локальный компаньон + свой бот»:
> - Новый проект **`LsMonitoring.TelegramBot`** — localhost-HTTP-сервис (HttpListener, 127.0.0.1:8771):
>   держит токен, гоняет ОДИН getUpdates-поллер (нет 409), привязывает чат по `/start <код>`.
>   Эндпоинты `/health /config /state /test /alarm`; auth по `X-LS-Bot-Key` (кроме `/health`).
>   Переиспользует проверенный `TelegramAlertService` из Core.
> - Десктоп: **`TelegramCompanionService`** запускает/супервизит exe (как gotify), шлёт config и алармы;
>   `MainWindow` больше НЕ создаёт `TelegramAlertService` и не опрашивает Telegram; chat id
>   подтягиваются из `/state` в heartbeat и мёржатся в config.
> - **`MessagesDialog`**: добавлены поля «Bot token» и «Bot @username»; тест/привязка идут через компаньон.
> - CI бандлит `LsMonitoring.TelegramBot.exe` рядом с приложением.
> - Проверено: сборка solution чистая, тесты 31/31; компаньон smoke-тестом отвечает на
>   /health /config /state /test /alarm (с фейковым токеном /test корректно вернул Telegram 401).
>
> **Известное ограничение 0.8 (→ 0.9):** «Отвязать чат» очищает список на десктопе, но компаньон
> сбрасывает привязки только при смене токена/кода или перезапуске (нет `/unlink`).
>
> **Ручная проверка (нужен реальный бот, у компа):**
> 1. @BotFather → создать бота, скопировать токен и @username.
> 2. Оповещения → Telegram: вставить token и @username, нажать «Отправить тест».
> 3. Отсканировать QR / открыть ссылку, отправить боту `/start <код>`.
> 4. Кнопка → «Отправлено!», chat-id появился в поле, пришло тестовое сообщение.
> 5. Diagnostics → Telegram «включён, готов», ошибок нет; в Task Manager один
>    `LsMonitoring.TelegramBot.exe`; в `logs/telegram_debug.txt` нет `409 Conflict`.

**Проблема.** Сейчас модель — один общий бот на всех, токен на клиенте, опрос `getUpdates`
с клиента. Telegram **запрещает параллельный `getUpdates`** одним токеном (`409 Conflict`),
поэтому общий бот работает максимум у одного пользователя. В UI **нет поля для своего токена**
(`MessagesDialog` оперирует только `EffectiveBotToken`), значит у конечного пользователя
CI-сборки Telegram просто не включится (токена нет).

**Выбрать ОДИН путь:**

**Вариант A (правильный, как у почты) — серверный relay.**
- По образцу `LsMonitoring.AlertRelay/Program.cs` добавить эндпоинты, проксирующие
  `sendMessage`/`editMessageText`/привязку чатов. Токен живёт только на сервере.
- Клиент шлёт на relay по Bearer (как `EmailAlertService.SendRelayEmailAsync`).
- `getUpdates` крутит сервер (один процесс — нет 409); привязка `/start <код>` резолвится там.
- Плюс: токен никогда не на клиенте; нет конфликта опроса. Минус: нужен хостинг (он у тебя уже есть под почту).

**Вариант B (быстрый) — персональный бот у каждого.**
- Добавить в `MessagesDialog.axaml` поле «Bot token» → писать в `config.Telegram.BotToken`.
- Каждый пользователь заводит своего бота в @BotFather. Конфликта `getUpdates` нет (токены разные).
- Минус: ручная настройка для пользователя; `https://t.me/ls_monitoringbot` в `MessagesDialog.cs:16`
  больше не «общий», ссылку/QR строить от своего бота (нужно имя бота — добавить `bot_username`).

**Рекомендация.** Для 0.8 — Вариант B (быстро, снимает 409 и утечку). Вариант A — пометить как
цель 0.9 в разделе «Вне рамок».

**Критерий готовности.** Telegram работает у пользователя без зашитого общего токена; нет `409`
в `logs/telegram_debug.txt` при штатной работе.

---

## [x] M1-3 — Атомарная запись config/history 🟠

> Сделано: `LsMonitoring.Core/IO/AtomicFile.cs`; подключено в `AppConfig.Save`,
> `MainWindow.SaveDeviationHistory`, `LocalGotifyService` (config.yml + admin-pass.txt).

**Проблема.** Все сохранения — `File.WriteAllText` (truncate-then-write). Падение посреди записи
бьёт файл. Затронуто:
- `LsMonitoring.Core/Configuration/AppConfig.cs:698` (`Save`)
- `LsMonitoring.Avalonia/MainWindow.axaml.cs:873` (`SaveDeviationHistory`)
- `LsMonitoring.Core/LocalServices/LocalGotifyService.cs:300` (`config.yml`) и `:272/:278` (`admin-pass.txt`)

**Решение.** Добавить хелпер атомарной записи и заменить вызовы.

Новый файл `LsMonitoring.Core/IO/AtomicFile.cs`:
```csharp
namespace LsMonitoring.Core.IO;

public static class AtomicFile
{
    /// <summary>Атомарная запись: пишем во временный файл, затем подменяем целевой
    /// (rename атомарен на одном томе). Прежняя версия сохраняется в .bak.</summary>
    public static void WriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);

        if (File.Exists(path))
        {
            var backup = path + ".bak";
            // File.Replace атомарно меняет местами и пишет бэкап.
            File.Replace(tmp, path, backup, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tmp, path);
        }
    }
}
```
Заменить `File.WriteAllText(path, json)` на `AtomicFile.WriteAllText(path, json)` в перечисленных местах.

**Грабли.** `File.Replace` падает между разными томами/некоторыми ФС — целевой и `.tmp` лежат в одной
папке, так что ок; на всякий случай можно обернуть в `try { Replace } catch { Move с перезаписью }`.

**Критерий готовности.** После сохранения рядом появляется `*.bak`; ручной тест: убить процесс
во время сохранения (или симулировать исключение после записи `.tmp`) — целевой файл цел.

---

## [x] M1-4 — Не затирать конфиг/историю молча при ошибке 🟠

> Сделано: `AppConfig.Load` и `MainWindow.LoadDeviationHistory` читают `path` → `path.bak`,
> а битый файл уносят в `*.corrupt-<timestamp>` вместо тихого сброса.

**Проблема.** `AppConfig.Load` (`AppConfig.cs:692`) при любом исключении возвращает **новый дефолтный**
конфиг; `LoadDeviationHistory` (`MainWindow.axaml.cs:864`) при ошибке **чистит** историю. В связке
с неатомарной записью один сбой = тихая потеря всех настроек (включая DPAPI-пароли) и истории.

**Решение.**
1. После `M1-3` при ошибке чтения сначала пробовать `*.bak`.
2. Если и бэкап не читается — **не перезаписывать** битый файл дефолтом: переименовать битый в
   `config.corrupt-<timestamp>.json`, показать в статус-баре/диагностике, и только потом стартовать с дефолта.
3. То же для `deviation-history.json`.

Набросок для `AppConfig.Load`:
```csharp
public static AppConfig Load(string path)
{
    foreach (var candidate in new[] { path, path + ".bak" })
    {
        if (!File.Exists(candidate)) continue;
        try { return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(candidate), JsonOptions) ?? new AppConfig(); }
        catch { /* пробуем .bak, затем падаем в карантин ниже */ }
    }

    if (File.Exists(path))
    {
        try { File.Move(path, path + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}.json"); } catch { }
    }
    return new AppConfig();
}
```

**Критерий готовности.** Битый `config.json` → приложение стартует на дефолте, но битый файл
сохранён как `*.corrupt-*`, а валидный `*.bak` (если есть) подхватывается без потерь.

---

# M2 — Корректность тревог

## [x] M2-1 — Оживить или убрать «мёртвые» настройки тревог 🟡

> Сделано по варианту A: удалены неработающие настройки `alarm.*` из модели, примера конфига
> и окна настроек, а также мёртвые `AlarmLog`/`InvalidStreakTracker`. Красная UI-плашка
> критического отклонения сохранена: она по-прежнему считается отдельно по `Thresholds` и не
> зависит от каналов уведомлений.

**Проблема.** Поля в `AppConfig.cs:82` (`AlarmConfig`) и `config.example.json:17` существуют, но
не влияют ни на что:
- `alarm.enabled` — грузится/сохраняется только в `SettingsDialog.axaml.cs:25/41`, **не проверяется**
  в `MainWindow.TriggerAlarmNotificationsIfNeeded` (`MainWindow.axaml.cs:646`).
- `sound`, `popup` — нигде не реализованы (нет проигрывания звука/попапа).
- `log_to_csv` → `AlarmLog` (`LsMonitoring.Core/Alarms/AlarmLog.cs`) нигде не инстанцируется.
- `invalid_behavior`, `invalid_alarm_minutes` → `InvalidStreakTracker` нигде не используется.

**⚠️ Важная ловушка.** `AlarmConfig.Enabled` по умолчанию `false`. Если просто добавить
`if (!_config.Alarm.Enabled) return;` — у всех, кто настроил каналы без этого тумблера, тревоги
**отвалятся**. Поэтому либо мигрировать (см. ниже), либо удалить тумблер.

**Выбрать путь:**

**A. Удалить мёртвое (минимум кода).** Убрать `EnableAlarmsBox` из `SettingsDialog.axaml(.cs)`,
поля `sound/popup/log_to_csv/invalid_*` из `AlarmConfig` и `config.example.json`, удалить классы
`AlarmLog` и `InvalidStreakTracker`. Каналы и так гейтятся своими `enabled`.

**B. Доделать как мастер-выключатель + звук/попап/CSV-лог.**
- Гейт: в начале `TriggerAlarmNotificationsIfNeeded` — `if (!_config.Alarm.Enabled) return;`.
- Миграция дефолта: при загрузке старого конфига, где есть включённые каналы, выставлять
  `Alarm.Enabled = true` (или сменить дефолт на `true`).
- `sound` → `Avalonia`/`System.Media.SystemSounds` при старте критической тревоги.
- `log_to_csv` → инстанцировать `AlarmLog` и звать `Write(...)` в `ProcessAlertStateChange`.
- `invalid_behavior=alarm` → подключить `InvalidStreakTracker` в пайплайн оценки.

**Рекомендация.** Для 0.8 — **A** (быстро и честно). Звук/CSV-лог/invalid-alarm — отдельные фичи 0.9.

**Критерий готовности.** В UI/конфиге не осталось настроек, которые ничего не делают; либо все
оставшиеся настройки реально влияют на поведение (проверить вручную по каждой).

---

## [x] M2-2 — Починить счётчик сообщений 🟡

> Сделано: `OnReadingsReady` теперь прибавляет к `_totalMessages` только количество строк,
> реально добавленных `ReadingBuffer.Merge`, а не весь CSV-буфер из очередного ответа gateway.

**Проблема.** `MainWindow.axaml.cs:626`: `_totalMessages += nodeReadings.Readings.Count` берёт
**весь** CSV (gateway каждый опрос отдаёт весь текущий буфер, ~N строк), хотя новых обычно 0–1.
`buffer.Merge(...)` уже возвращает реально добавленные строки, но результат игнорируется
(`MainWindow.axaml.cs:623`). Итог: «X сообщ.» и «сообщений: X» в диагностике завышены в разы.

**Решение.** В `OnReadingsReady`:
```csharp
var before = buffer.Latest;
var added = buffer.Merge(nodeReadings.Readings, _config.PlotBufferPoints); // было: без присваивания
item.UpdateFrom(buffer, nodeReadings.Model, _config.GetCalibration(nodeId));

_totalMessages += added.Count; // было: nodeReadings.Readings.Count
```

**Критерий готовности.** При повторных опросах без новых данных `_totalMessages` не растёт;
растёт ровно на число новых строк.

---

## [x] M2-3 — Не слать реальные тревоги при загрузке CSV-файла 🟡

> Сделано: у `OnReadingsReady` появился параметр `bool live = true`; `TriggerAlarmNotificationsIfNeeded`
> вызывается только при `live`. `LoadCsvAsync` зовёт `OnReadingsReady(..., live: false)`, поэтому
> предпросмотр CSV больше не шлёт реальные уведомления. Живой поллинг и `PollOnceAsync` остаются `live: true`.

**Проблема.** `LoadCsvAsync` (`MainWindow.axaml.cs:548`) зовёт `OnReadingsReady`, который в конце
вызывает `TriggerAlarmNotificationsIfNeeded` (`:643`). README позиционирует CSV как **безопасный
предпросмотр без прибора**, но по факту, если каналы включены и последняя строка файла за порогом,
уйдут настоящие Telegram/SMS/email/push.

**Решение.** Прокинуть флаг источника:
```csharp
private void OnReadingsReady(int nodeId, NodeReadings nodeReadings, bool live = true)
{
    ...
    var latest = buffer.Latest;
    if (live)
        TriggerAlarmNotificationsIfNeeded(nodeId, latest);
}
```
В `LoadCsvAsync` звать `OnReadingsReady(nodeId, ..., live: false)`. Живой поллинг и `PollOnce`
оставить `live: true`.

**Критерий готовности.** Загрузка CSV с заведомо критическими значениями при включённых каналах
не отправляет уведомлений; live-опрос — отправляет.

---

# M3 — Ресурсы и стабильность

## [x] M3-1 — Алерт-сервисы как `IDisposable`, общий HttpClient 🟡

> Сделано (с поправкой на текущее состояние после M1-2: Telegram уже вынесен в компаньон-процесс,
> в десктопе `TelegramAlertService` больше не создаётся):
> - `TelegramAlertService` теперь `IAsyncDisposable` (`StopAsync` → `Dispose` у `HttpClient` и `CTS`).
>   `BotHost` (единственный владелец сервиса) при смене токена/`Dispose` диспозит предыдущий поллер
>   через fire-and-forget `DisposeQuietly`, не блокируя конфигурацию на завершении gone-задачи.
> - Десктоп: `MainWindow` держит один `_alertHttpClient` и передаёт его в Email/SMS/Gotify-сервисы,
>   которые пересоздаются на каждом `ReconfigureAlertServices` — больше нет утечки сокет-хендлера
>   на каждую переконфигурацию. Клиент диспозится в `OnClosed`.
> - `MessagesDialog` держит один `_testHttpClient` для кнопок «тест» каналов и диспозит его в `OnClosed`.

**Проблема.** `TelegramAlertService` (`LsMonitoring.Core/Alarms/TelegramAlertService.cs`) не реализует
`IDisposable`: каждый `ReconfigureTelegramAlerts` (`MainWindow.axaml.cs:357`) утекает `HttpClient`+`CTS`.
Email/SMS/Gotify-сервисы и тестовые сервисы в диалоге каждый раз делают `new HttpClient()` без Dispose.

**Решение.**
1. Завести один переиспользуемый `HttpClient` (статический в каждом сервисе или общий, переданный в ctor).
   Сервисы уже принимают `HttpClient?` в ctor (Email/SMS/Gotify) — этим воспользоваться.
2. `TelegramAlertService`: реализовать `IAsyncDisposable` — `Stop()` + `await _pollingTask` + `_httpClient.Dispose()` + `_cts.Dispose()`.
3. В `MainWindow` хранить и звать dispose старого сервиса перед созданием нового.

Набросок для Telegram:
```csharp
public async ValueTask DisposeAsync()
{
    await StopAsync();          // уже есть: Cancel + await polling
    _httpClient.Dispose();
    _cts.Dispose();
}
```
В `ReconfigureTelegramAlerts` — сменить `StopTelegramAlerts()` (синхронный) на await-вариант с dispose.

**Критерий готовности.** Многократное открытие/закрытие диалога Messages и пересохранение настроек
не растит число хендлов/сокетов процесса (проверить в Resource Monitor / `GetCurrentProcess().HandleCount`).

---

## [x] M3-2 — Диалог Messages освобождает свои сервисы 🟡

> Сделано по варианту «переиспользовать из `MainWindow`»: `MessagesDialog` больше не зовёт
> `CreateDefault()`, а принимает `LocalhostRunTunnelService` и `LocalGotifyService` через конструктор
> (`ShowMessagesDialogAsync` передаёт `_quickTunnelService`/`_localGotifyService`). Диалог их только
> заимствует и НЕ диспозит — единственный владелец gotify/тоннеля остаётся `MainWindow` (диспозит в
> своём `OnClosed`). Безпараметровый ctor для XAML-превьюера передаёт design-time `CreateDefault()`.

**Проблема.** `MessagesDialog` создаёт **собственные** `LocalhostRunTunnelService` и
`LocalGotifyService` (`MessagesDialog.axaml.cs:18-19`) и никогда их не Dispose (нет `OnClosed`).
Дублируют управление тем же gotify/тоннелем, что и в `MainWindow`.

**Решение.**
- Либо переиспользовать инстансы из `MainWindow` (передать в диалог через свойство/ctor).
- Либо добавить в диалог `OnClosed` и звать `Dispose()` своих сервисов.

Рекомендация: передавать из `MainWindow` (один владелец gotify/тоннеля на приложение):
```csharp
// MainWindow.ShowMessagesDialogAsync
var dialog = new MessagesDialog(_localGotifyService, _quickTunnelService);
```
и убрать `CreateDefault()` из диалога.

**Критерий готовности.** В приложении один владелец gotify/tunnel; закрытие диалога не оставляет
живых `HttpClient`/процессов сверх ожидаемого.

---

## [x] M3-3 — Убрать гонку параллельного getUpdates (409) 🟡

> Сделано (с поправкой на состояние после M1-2: единственный поллер теперь в компаньоне `BotHost`,
> десктопного `StopTelegramAlerts`/`ReconfigureTelegramAlerts` больше нет):
> - `BotHost.Configure` стал `ConfigureAsync`: смена токена/линк-кода теперь **сначала** полностью
>   останавливает прежний поллер (`DisposeAsync` = cancel getUpdates + await polling task + dispose
>   HttpClient/CTS) и только потом стартует новый. Раньше (после M3-1) старый диспозился
>   fire-and-forget, и новый поллер мог стартовать раньше остановки старого → окно для `409`.
> - Параллельные `/config` сериализованы через `SemaphoreSlim _configGate`, поэтому два запроса не
>   могут одновременно поднять два поллера. `Program.cs` теперь `await host.ConfigureAsync(...)`.
> - Пункт про `DiscoverChatIdsAsync` неактуален: десктоп больше не создаёт `TelegramAlertService`
>   напрямую — обнаружение чатов идёт через тот же единственный поллер компаньона.

**Проблема.** `StopTelegramAlerts()` (`MainWindow.axaml.cs:372`) только `Cancel()` и сразу зовёт
создание нового сервиса; старый getUpdates ещё может лететь → два опроса одним токеном → `409 Conflict`.
То же между фоновым `PollUpdatesAsync` и `DiscoverChatIdsAsync` (`TelegramAlertService.cs:81/109`).

**Решение.**
1. Перед созданием нового сервиса всегда `await StopTelegramAlertsAsync()` (он ждёт завершения задачи).
   Сейчас `ReconfigureTelegramAlerts` зовёт синхронный `StopTelegramAlerts()` — заменить на await-цепочку
   (метод `ReconfigureTelegramAlerts` сделать `async Task`, вызовы — await).
2. `DiscoverChatIdsAsync` не должен работать одновременно с фоновым опросом: в диалоге сервис
   создаётся с `startPolling: false` (уже так, `MessagesDialog.axaml.cs:149`) — оставить так и
   убедиться, что в этот момент основной сервис остановлен (он останавливается в
   `ShowMessagesDialogAsync` через `StopTelegramAlertsAsync`, `MainWindow.axaml.cs:272` — ок).

**Критерий готовности.** В `logs/telegram_debug.txt` нет `Conflict: terminated by other getUpdates`
при открытии Messages, тесте Telegram и пересохранении настроек.

**Зависимость.** Идёт вместе с `M3-1` (dispose) — удобно делать одним заходом.

---

## [x] M3-4 — Пиннинг сертификата gateway вместо «принимать любой» 🟡

> Сделано по варианту TOFU:
> - `CsvGatewaySource` больше не использует `DangerousAcceptAnyServerCertificateValidator`. Вместо
>   него — колбэк `ValidateServerCertificate`: считает SHA-256 отпечаток серта (`GetCertHashString`),
>   кладёт его в `ObservedThumbprint`. Если пин ещё не задан — принимает (первое доверие); если задан —
>   принимает только при совпадении, иначе хендшейк падает.
> - В конфиг добавлено `connection.cert_thumbprint` (+ в `config.example.json`); ctor источника
>   принимает `pinnedThumbprint`.
> - `MainWindow` создаёт источник через `CreateGatewaySource()` (прокидывает сохранённый отпечаток) и
>   после первого успешного обмена закрепляет `ObservedThumbprint` через `PersistLearnedThumbprintIfNeeded`
>   (поллинг — по событию `ConnectionState(ok)`; `PollOnce`/`DiscoverNodes` — после запросов). Отпечаток
>   нормализуется (hex, uppercase, без разделителей), сравнение регистронезависимое.

**Проблема.** `CsvGatewaySource.ConnectAsync` (`LsMonitoring.Core/Sources/CsvGatewaySource.cs:51`)
ставит `DangerousAcceptAnyServerCertificateValidator` и при этом шлёт Basic-auth с паролем gateway.
MITM в общей сети перехватит креды.

**Решение (мягкое, не ломая link-local сценарий).**
- Заменить «любой серт» на проверку по сохранённому отпечатку (TOFU): при первом подключении
  показать отпечаток и сохранить в конфиг (`connection.cert_thumbprint`), далее принимать только его.
- Колбэк валидации сверяет `cert.GetCertHashString()` с сохранённым; при несовпадении — ошибка.
- Если отпечаток ещё не задан — принять и запомнить (можно с предупреждением в статус-баре).

**Критерий готовности.** Подмена сертификата gateway после первого подключения приводит к ошибке
связи, а не к молчаливому приёму.

---

## [x] M3-5 — `TryParse` и валидация ввода 🟢

> Сделано (строки в плане немного сдвинулись от текущего кода — поправил по факту):
> - `CsvGatewaySource.DiscoverNodesAsync` (фактически строка ~159, не 148): `int.Parse(match.Groups[1].Value)`
>   → `int.TryParse(...)` с `continue`. Регекс матчит только `\d+`, поэтому `FormatException`
>   маловероятен, но длинная строка цифр давала `OverflowException` и роняла весь поиск узлов —
>   теперь такой матч просто пропускается.
> - `MainWindow.AddNode` (строка ~626): уже использовал `int.TryParse`, добавил отклонение
>   `nodeId <= 0` — при пустом/нечисловом/неположительном вводе показывает в статус-баре
>   «Node ID должен быть положительным числом» и не добавляет узел.
> - `SettingsDialog.OnSaveClick`: пустой (после `Trim`) IP шлюза больше не сохраняется — в диалог
>   добавлен `ValidationText` (скрытая красная плашка), при пустом IP показывается сообщение,
>   фокус возвращается в поле, диалог не закрывается. Заодно IP теперь сохраняется обрезанным.
> - Проверено: сборка solution чистая (0 warnings / 0 errors); тесты 31 passed / 5 skipped / 0 failed.

**Проблема/места.**
- `CsvGatewaySource.cs:148` — `int.Parse(match.Groups[1].Value)` → `OverflowException`/`FormatException`
  на кривом HTML вылетает из `DiscoverNodesAsync`. Заменить на `int.TryParse(...)` с `continue`.
- `MainWindow.AddNode` (`:581`) принимает отрицательные/нулевые Node ID. Добавить `nodeId > 0`.
- `SettingsDialog.OnSaveClick` (`:38`) — пустой gateway IP сохраняется (→ `https://`). Минимальная
  валидация непустоты/формата хоста.

**Критерий готовности.** Кривой HTML при поиске узлов не роняет операцию; нельзя добавить узел `<= 0`;
пустой gateway IP не сохраняется без предупреждения.

---

# M4 — Хардненинг relay (`LsMonitoring.AlertRelay`)

## [x] M4-1 — Constant-time сравнение Bearer 🟠

> Сделано: в `LsMonitoring.AlertRelay/Program.cs` `HasValidBearerToken` теперь сначала проверяет
> префикс `Bearer ` (`StartsWith`, ordinal-ignore-case), а само сравнение токена идёт через
> `CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), …expectedToken)`
> вместо `string.Equals(..., Ordinal)`. Добавлен `using System.Security.Cryptography;`
> (`System.Text` уже был). `FixedTimeEquals` сравнивает байты за постоянное время и
> короткозамыкается только на разнице длины (длина — допустимая утечка по плану).
> Проверено: сборка solution чистая (0/0), тесты 31 passed / 5 skipped / 0 failed.

**Проблема.** `Program.cs:88` — `string.Equals(token, expected, Ordinal)` уязвимо к тайминг-атаке
на восстановление API-ключа.

**Решение.**
```csharp
using System.Security.Cryptography;
using System.Text;

static bool HasValidBearerToken(HttpContext context, string expectedToken)
{
    var auth = context.Request.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    if (!auth.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
    var provided = auth[prefix.Length..].Trim();
    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(provided),
        Encoding.UTF8.GetBytes(expectedToken));
}
```
(`FixedTimeEquals` для разной длины вернёт false без утечки длины при равной длине входов.)

**Критерий готовности.** Сравнение не зависит по времени от совпадения префикса ключа.

---

## [x] M4-2 — Rate-limit в relay 🟠

> Сделано: в `LsMonitoring.AlertRelay/Program.cs` подключён ASP.NET rate limiter
> (`builder.Services.AddRateLimiter(...)` + `app.UseRateLimiter()`), на `/api/alerts/email`
> навешана политика `RequireRateLimiting("email")`. Политика — `FixedWindowRateLimiter`,
> окно 1 час, `PermitLimit = RateLimitPerHour` (новая опция `LS_ALERT_RATE_LIMIT_PER_HOUR`,
> дефолт 60), `QueueLimit = 0`, `RejectionStatusCode = 429`.
> - **Ключ партиции** (`ResolveRateLimitPartitionKey`): сперва заголовок `X-LS-Installation-Id`
>   (его уже шлёт `EmailAlertService.SendRelayEmailAsync`), при отсутствии — клиентский IP с учётом
>   первого хопа `X-Forwarded-For` (relay за TLS-терминирующим прокси). Так лимит у каждой установки
>   свой, а отсутствие заголовка не схлопывает всех в один бакет.
> - Лимит вынесен в конфиг → правится без передеплоя.
> - **Smoke-тест локально** (`LS_ALERT_RATE_LIMIT_PER_HOUR=2`): первые 2 запроса проходят к хендлеру
>   (503 — SMTP не настроен, лимитер их пропустил), 3-й вернул 429; запрос с другим
>   `X-LS-Installation-Id` получил свой бакет (503, не 429).
> - README relay дополнен переменной и разделом про rate limit. Сборка чистая (0/0), тесты 31/5/0.
>
> **Примечание по TLS:** Bearer уходит в открытую, если хостинг не терминирует TLS — это
> ответственность деплоя (вне кода), отмечено в плане ниже.

**Проблема.** `/api/alerts/email` (`Program.cs:17`) без лимитов: утёкший токен = открытый спам-шлюз
через твой SMTP.

**Решение.** Добавить ASP.NET rate limiting (`Microsoft.AspNetCore.RateLimiting`), ключ — по
`installation_id` (или IP) + общий потолок:
```csharp
builder.Services.AddRateLimiter(o =>
{
    o.AddFixedWindowLimiter("email", opt =>
    {
        opt.Window = TimeSpan.FromHours(1);
        opt.PermitLimit = 60;       // на инсталляцию/час — подобрать
        opt.QueueLimit = 0;
    });
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});
app.UseRateLimiter();
// .MapPost(...).RequireRateLimiting("email");
```
Дополнительно: убедиться, что хостинг терминирует TLS (Bearer иначе в открытую).

**Критерий готовности.** Превышение лимита возвращает `429`; обычная нагрузка проходит.

---

## [x] M4-3 — Пиннинг версии и проверка хеша Gotify 🟡

> Сделано (версия зафиксирована на **v2.9.1**, хеши взяты с реальных артефактов релиза — у Gotify
> нет отдельного `checksums.txt`, посчитал сам):
> - **Рантайм** (`LocalGotifyService`): `GotifyDownloadUrl` теперь указывает на конкретный тег
>   (`.../releases/download/v2.9.1/...`), а не `latest`. Добавлены константы `GotifyVersion`,
>   `GotifyZipSha256` (`8E05…DE74`), `GotifyExeSha256` (`2A1B…C20C`) и хелпер `FileHashMatches`
>   (`SHA256.HashData` + `Convert.ToHexString`, сравнение `OrdinalIgnoreCase`).
>   В `ResolveGotifyExeAsync`: bundled-бинарь и ранее скачанная копия используются **только** при
>   совпадении exe-хеша (иначе fall-through на перекачку); live-download проверяет zip-хеш и при
>   несовпадении бросает исключение до распаковки. Итог: непроверенный/подменённый бинарь не
>   запускается ни одним из трёх путей.
> - **CI** (`.github/workflows/dotnet.yml`, шаг бандла Gotify): URL пиннится на v2.9.1; после
>   `Invoke-WebRequest` сверяется `Get-FileHash` zip-а, после распаковки — exe; несовпадение любого
>   из них роняет сборку (`throw`). В комментах и коде, и в CI указано бампить версию+хеши вместе.
> - Проверено: сборка solution чистая (0/0), тесты 31 passed / 5 skipped / 0 failed; хеши получены
>   из фактически скачанного `gotify-windows-amd64.exe.zip` v2.9.1 и извлечённого из него .exe.

**Проблема.** Gotify тянется как `latest` без проверки:
- рантайм: `LocalGotifyService.cs:27` (`GotifyDownloadUrl`), скачивание `:206`;
- CI: `.github/workflows/dotnet.yml:105` бандлит `latest` в релиз.

**Решение.**
1. Зафиксировать конкретную версию Gotify (например `v2.x.y`) в URL — и в коде, и в CI.
2. Проверять SHA-256 скачанного `.exe`/`.zip` против захардкоженного значения перед запуском/бандлом.
   В CI — после `Invoke-WebRequest` сверять `Get-FileHash`.

**Критерий готовности.** Подмена бинаря Gotify (несовпадение хеша) прерывает запуск/сборку.

---

# M5 — QoL, легаси, производительность, CI (порядок не важен)

## [x] M5-1 — Единое защищённое хранение секретов 🟢
> Сделано:
> - Новый публичный хелпер `ProtectedSecret` (в `AppConfig.cs`, `namespace Configuration`)
>   с `Encode`/`Decode` — бывший приватный `EncodeProtectedString`/`DecodeProtectedString` из
>   `EmailConfig`, теперь общий для всего кода. На Windows — DPAPI `CurrentUser`, на других ОС —
>   plain base64 (graceful fallback).
> - `EmailConfig` и `ConnectionConfig`: заменены вызовы private-методов на `ProtectedSecret`.
> - Добавлены `*_b64` JSON-поля + `[JsonIgnore]` C#-свойства с encode/decode для:
>   `TelegramConfig.BotToken` (`bot_token_b64`), `SmsConfig.ApiKey` (`api_key_b64`),
>   `PushConfig.AppToken` (`app_token_b64`), `PushConfig.ClientToken` (`client_token_b64`),
>   `WebhookConfig.Secret` (`secret_b64`).
> - Каждое поле имеет legacy-мигратор: старый JSON-ключ (plaintext) при десериализации
>   автоматически конвертируется в `_b64` форму — при следующем `Save()` plaintext ключ исчезает.
> - `LocalGotifyService.admin-pass.txt`: новый `ReadAdminPassword` хелпер — читает защищённый
>   файл, при обнаружении legacy-plaintext автоматически перезаписывает его через `ProtectedSecret.Encode`.
>   Запись нового пароля тоже через `ProtectedSecret.Encode`. Добавлен `using Configuration;`.
> - Проверено: сборка solution чистая (0/0); тесты 31 passed / 5 skipped / 0 failed (включая тесты,
>   напрямую задающие `ApiKey = "..."`, `AppToken = "..."` и т.д. — работают через новые свойства).

**Проблема.** Под DPAPI только gateway-пароль, SMTP-пароль, relay-токен (`AppConfig.cs`). Открытым
текстом: Telegram token, SMS `api_key`, Gotify `app_token`/`client_token`, webhook `secret`,
`admin-pass.txt` (`LocalGotifyService.cs:272`). **Решение.** Вынести DPAPI encode/decode из
`EmailConfig`/`ConnectionConfig` в общий хелпер `ProtectedSecret` и применить ко всем секретным
полям (как `*_b64` свойства). `admin-pass.txt` тоже шифровать DPAPI. **Готово:** в `config.json`
нет секретов открытым текстом.

## [x] M5-2 — Логи в `%LOCALAPPDATA%`, не по CWD 🟢
> Сделано: новый `LsMonitoring.Core/IO/AppPaths.cs` — статический хелпер с `LogDirectory`
> (`%LOCALAPPDATA%\LS Monitoring\logs`) и фабричным методом `LogFile(name)` (создаёт каталог,
> возвращает полный путь). Во всех четырёх сервисах `EmailAlertService`, `SmsAlertService`,
> `GotifyAlertService`, `TelegramAlertService`: `const LogFilePath = "logs/..."` → `static readonly
> LogFilePath = AppPaths.LogFile("...")`, убрана строка `Directory.CreateDirectory("logs")` (теперь
> делается внутри `AppPaths.LogFile`). Добавлен `using LsMonitoring.Core.IO;` в каждый файл.
> Сборка 0/0, тесты 31/5/0.

**Проблема.** `logs/*_debug.txt` пишутся относительным путём (`TelegramAlertService.cs:15`,
`EmailAlertService.cs:18`, `SmsAlertService.cs:15`, `GotifyAlertService.cs:22`) → из `Program Files`
запись молча падает. **Решение.** Единый путь `%LOCALAPPDATA%\LS Monitoring\logs` (как
`ResolveDeviationHistoryPath` в `MainWindow.axaml.cs:926`). Вынести в общий хелпер. **Готово:** логи
пишутся независимо от рабочей директории.

## [x] M5-3 — Производительность `TrendPlot.Render` 🟢
> Сделано:
> - Убран `OrderBy().ToList()` в `Render()` — `ReadingBuffer.Merge` всегда вызывает `Sort` после
>   добавления, поэтому `Readings` уже отсортирован. Убрана дублирующая проверка `ordered.Count == 0`.
> - `GapThresholdSeconds` теперь вызывается один раз и результат передаётся в
>   `DrawInvalidZones`, `DrawSeries` и `RightPaddingSeconds` (новый параметр).
> - 20+ `SolidColorBrush`/`Pen` объектов, создававшихся на каждый кадр, вынесены в
>   `private static readonly` поля (`s_bgBrush`, `s_borderPen`, `s_warnBandBrush`, `s_critBandBrush`,
>   `s_warnLinePen`, `s_critLinePen`, `s_invalidBandBrush`, `s_gapBandBrush`, `s_invalidDashPen`,
>   `s_gridPen`, `s_timeTickPen`, `s_cutoffPickingPen`, `s_cutoffLinePen`,
>   `s_textBrush`, `s_textMutedBrush`, `s_accentABrush`, `s_accentBBrush`, `s_accentAPen`, `s_accentBPen`).
>   Особо важно: `s_gapBandBrush` — бывшая кисть **внутри цикла** `DrawInvalidZones` (до 1000 пр/с).
> - `DrawText` принимает `IBrush` вместо `string color`; `DrawLegendItem` — `IBrush` вместо `string`.
> - `DrawSeries` принимает `Pen seriesPen` вместо `string color + double thickness`; убраны
>   `SeriesColor()` → заменены `SeriesBrush()` и `SeriesPen()`.
> - Сборка 0/0, тесты 31/5/0.

**Проблема.** `Controls/TrendPlot.cs:135` на каждый кадр: `OrderBy().ToList()` (буфер уже отсортирован
в `ReadingBuffer`!), повторные `Where/Select/ToList`, `GapThresholdSeconds()` считается 3+ раза,
кисти/перья аллоцируются заново. Два графика × до 1000 точек. **Решение.** Не сортировать повторно
(данные уже сортированы), посчитать `GapThresholdSeconds` один раз, закешировать иммутабельные
`IBrush`/`Pen` в статических полях. **Готово:** заметно меньше аллокаций на кадр (профайлер/визуально
плавнее), картинка не изменилась.

## [x] M5-4 — Дебаунс/async сохранения на UI-потоке 🟢
> Сделано: два `DispatcherTimer`-дебаунса (1.5 с) в `MainWindow`:
> - `ScheduleDeviationHistorySave()` / `_saveHistoryTimer` — заменены прямые `SaveDeviationHistory()`
>   на горячем пути `ProcessAlertStateChange` (строки 824/846): при активной тревоге каждый опрос (5 с)
>   обновлял историю и сразу сериализовал JSON + писал файл; теперь запись происходит через 1.5 с
>   после последнего изменения.
> - `ScheduleConfigSave()` / `_saveConfigTimer` — `_config.Save` в `SyncTelegramChatIdsAsync` (heartbeat,
>   может срабатывать при каждом тике при новых chat_id).
> - `OnClosed`: перед teardown флашит обе очереди сразу (stop + save) — данные не теряются при закрытии.
> - Все другие `_config.Save` (диалоги, кнопки) оставлены прямыми: это редкие user-actions.
> - Сборка 0/0, тесты 31/5/0.

**Проблема.** `SaveDeviationHistory` и `_config.Save` пишут синхронно на UI-потоке при каждом
изменении (каждый «ноль», chat_id, шаг активной тревоги) — `MainWindow` в нескольких местах.
**Решение.** Дебаунс (например, таймер на 1–2 с, коалесцировать частые записи) и/или вынести запись
в `Task.Run`. Учесть атомарность из `M1-3`. **Готово:** при активной тревоге каждый опрос не делает
полную сериализацию+запись синхронно.

## [x] M5-5 — Свести двойную логику критичности к `ThresholdEvaluator` 🟢
> Сделано: в `MainWindow` ручные вычисления `DeviationA/B` + `IsCriticalDeviation` заменены на
> `ThresholdEvaluator.EvaluateAxisThresholds` в двух местах:
> - `TriggerAlarmNotificationsIfNeeded`: `eval = EvaluateAxisThresholds(latest, ...)`, `aDeviation = eval.AValue`,
>   `bDeviation = eval.BValue`, per-axis критичность от `|AValue| >= criticalA` — теперь логика в одном месте.
> - `RefreshCurrentNode` (баннер): аналогично через `latestEval?.AValue/BValue`.
> - Мёртвый `IsCriticalDeviation` удалён.
> - `AlarmConfig` в `ThresholdEvaluator` — не было: параметр не присутствует в текущем коде, уже убран ранее.
> - Сборка 0/0, тесты 31/5/0.

**Проблема.** `ThresholdEvaluator.Evaluate` (`Alarms/ThresholdEvaluator.cs:9`) принимает `AlarmConfig`
и игнорирует его, и используется только в тестах. В проде критичность считается вручную дважды:
`MainWindow.axaml.cs:667` (для алертов) и `:987` (для баннера/строк). Риск расхождения. **Решение.**
Привести прод к единому `ThresholdEvaluator` (или к одному приватному методу), убрать неиспользуемый
параметр `AlarmConfig`, если не нужен. **Готово:** одна реализация порогов, тесты зелёные.

## [x] M5-6 — Убрать мёртвый код 🟢
> Сделано (каждый символ проверен grep-ом перед удалением):
> - `ParsedCsv.LatestValid` — нигде не используется → удалено.
> - `PushConfig.Target` (`[JsonPropertyName("target")]`) — нигде не вызывается в C# → удалено.
> - `ModbusSource` — помечен `[Obsolete("...")]` с пояснением (нужна карта регистров Worldsensing, цель 0.9).
> - `WebhookConfig.Method/Headers/PayloadTemplate` — осознанно оставлены (0.9).
> - Сборка 0/0, тесты 31/5/0.

**Места.** `ModbusSource` (заглушка — оставить, но пометить `[Obsolete]`/TODO или вынести),
`ParsedCsv.LatestValid` (`Models/ParsedCsv.cs:13`, не используется), `PushConfig.Target`
(`AppConfig.cs:443`), `WebhookConfig.Method/Headers/PayloadTemplate` (используется только при наличии
сервиса — отложить до 0.9). Удалять только подтверждённо неиспользуемое (греп перед удалением).
**Готово:** нет неиспользуемых публичных членов (кроме осознанно отложенного webhook 0.9).

## [x] M5-7 — Хардненинг CI 🟢
> Сделано:
> - CI `permissions`: верхний уровень понижен до `contents: read`; `publish-release` job получил
>   `permissions: contents: write` — единственный job, создающий GitHub Releases.
> - Push-триггер: `branches: ['**']` → `branches: [main, master]`; PR-ветки покрывает `pull_request`
>   триггер, дублей больше нет.
> - Vuln-scan шаг в `build-test`: `dotnet list package --vulnerable --include-transitive | tee vuln.txt &&
>   grep -q "no vulnerable packages" vuln.txt` — fail при найденных CVE.
> - `EnableNETAnalyzers=true` + `AnalysisLevel=latest-recommended` в `LsMonitoring.Core.csproj` и
>   `LsMonitoring.Avalonia.csproj`.
> - Анализаторы породили 42 предупреждения — все устранены:
>   - `.editorconfig`: `CA1305` (none), `CA1859` (none), `CA1001` (none), `CA1805` (none) — suppressions
>     с обоснованием (UI-строки, интерфейсные абстракции, Avalonia-lifecycle, явные дефолты).
>   - `TelegramAlertService`: CA1854 `ContainsKey` → `TryGetValue`; CA1822 `FormatMessage/FormatResolvedMessage` → `static`.
>   - `CmtCsvParser.cs:65`: CA1854 suppressed локально (`#pragma`) — паттерн не тот (нет последующего индексера).
>   - `LocalGotifyService`: CA1869 — три `new JsonSerializerOptions{...}` заменены на `static readonly s_jsonOptions`.
>   - `UpdateService.cs`: CA2016 — `DownloadUpdatesAsync` теперь получает `ct`.
>   - `MessagesDialog.axaml.cs`: CA1861 — два `new[] { ... }` → `static readonly char[]`.
>   - `Program.cs`: CA1852 — `class Program` → `sealed class Program`.
> - Итог: полная сборка `--no-incremental` = **0 warnings / 0 errors**; тесты 31/5/0.

**Места `.github/workflows/dotnet.yml`.**
- `permissions: contents: write` (`:12`) на весь workflow → скоупить write только на `publish-release`
  (least privilege), остальным `contents: read`.
- Push-триггер `branches: ['**']` (`:5`) + `pull_request` → двойные прогоны на PR из своего репо; оставить
  push только на основные ветки/теги.
- Добавить шаг `dotnet list package --vulnerable --include-transitive` (fail при найденных).
- В csproj включить `<EnableNETAnalyzers>true</EnableNETAnalyzers>` и
  `<AnalysisLevel>latest-recommended</AnalysisLevel>` (Core + Avalonia).
**Готово:** least-privilege токены, есть vuln-scan, анализаторы включены.

## [ ] M5-8 — SMS: лимит только при успехе; частичные ошибки 🟢
**Проблема.** `SmsAlertService.TryReserveRateLimitSlot` (`:182`) занимает слот **до** отправки —
сетевая ошибка тратит квоту. `LooksLikeSmsRuError` (`:204`) ловит только верхнеуровневый
`status:ERROR`, не per-number. **Решение.** Резервировать/коммитить слот после успешной отправки
(или откатывать при ошибке); разбирать per-recipient статусы sms.ru. **Готово:** неудачная отправка
не уменьшает часовой лимит; частичные ошибки детектятся. (Можно слить с задачами 0.9 по SMS.)

---

# Чек-лист проверки (после каждого пункта)

```powershell
dotnet restore .\LsMonitoring.sln
dotnet build   .\LsMonitoring.sln -c Release   # ожидаем 0 warnings
dotnet test    .\LsMonitoring.sln              # парсер/алерты/конфиг — зелёные
```
- [ ] Сборка без новых warnings (nullable включён, держим планку).
- [ ] Тесты зелёные; для багов M2-2/M2-3/M5-8 по возможности добавить юнит-тест.
- [ ] Ручной smoke: старт опроса, тест каналов, открытие/закрытие Messages и Diagnostics.
- [ ] Обновлён статус задачи в этом файле (`[ ]` → `[x]`).

# Что НЕ трогаем в 0.8 (это 0.9)

- Webhook-отправка (сейчас только хранение URL/секрета — `WebhookConfig`).
- Полная доделка SMS (per-number статусы — частично в M5-8, остальное в 0.9).
- Серверный Telegram-relay (Вариант A в M1-2), если в 0.8 выбран Вариант B.
- Modbus-источник (нужна карта регистров Worldsensing).

# Что сейчас хорошо (не ломать при правках)

DPAPI для основных паролей; CSPRNG для link-кодов и admin-пароля Gotify; маскирование телефонов
в логах; `try/catch` вокруг всей диагностики; CSV-парсер на `InvariantCulture`; корректное
маршалирование событий поллера в UI-поток (`Dispatcher.UIThread.Post`); секреты не закоммичены;
nullable включён; сборка без warnings; юнит-тесты на ядро.
