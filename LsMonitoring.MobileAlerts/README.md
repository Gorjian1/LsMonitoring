# LS Monitoring Mobile Alerts

Отдельный Android-клиент для тревог LS Monitoring.

## Как это работает

1. Основное приложение LS Monitoring отправляет тревоги в Gotify.
2. В каждое сообщение добавляется `extras["lsmonitoring::alarm"]`:
   - `event = active` - тревога началась;
   - `event = resolved` - тревога закрыта;
   - `nodeId`, `axis`, `value`, `startedAt`, `updatedAt`, `resolvedAt`, `durationSeconds`.
3. Android-приложение подключается к Gotify `/stream` по `client_token`.
4. Для `active` оно создаёт/обновляет несмахиваемое уведомление с таймером.
5. Для `resolved` оно заменяет его обычным уведомлением, которое можно смахнуть.

Из-за ограничений Android постоянное подключение к своему серверу должно идти через foreground service. Поэтому у приложения будет системное уведомление "LS Monitoring подключено". Без этого Android может остановить WebSocket в фоне.

## QR и подключение

Приложение поддерживает deep link:

```text
lsmonitoring://connect?server=https%3A%2F%2Fpush.example.com&token=CLIENT_TOKEN
```

В основном приложении в `Оповещения -> Gotify -> Сервисная настройка Gotify`:

- `Сервер` - публичный URL Gotify;
- `App token` - токен отправки, остаётся только на компьютере;
- `Client token` - токен чтения для телефона;
- `Страница APK` - страница, которую открывает QR. Если страница задана, LS Monitoring добавит к ней `server` и `token` query-параметры.

## Сборка APK

На этой машине пока нет Android toolchain: нужен JDK 17, Android SDK и Gradle/Android Studio.

После установки:

```powershell
cd .\LsMonitoring.MobileAlerts
gradle assembleDebug
```

Debug APK появится в:

```text
app/build/outputs/apk/debug/app-debug.apk
```

Для установки пользователю лучше собирать release APK с подписью и выкладывать его на простую HTTPS-страницу, куда будет вести QR.

В `landing/index.html` лежит минимальная страница для QR: рядом с ней нужно положить `ls-alerts.apk`, а URL страницы указать в поле `Страница APK`.
