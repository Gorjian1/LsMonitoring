package ru.lsmonitoring.alerts;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import java.io.UnsupportedEncodingException;
import java.net.URLEncoder;
import java.time.OffsetDateTime;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.WebSocket;
import okhttp3.WebSocketListener;
import org.json.JSONObject;

public final class AlarmStreamService extends Service {
    static final String ACTION_START = "ru.lsmonitoring.alerts.START";
    private static final String ACTION_STOP = "ru.lsmonitoring.alerts.STOP";
    private static final String CHANNEL_CONNECTION = "connection";
    private static final String CHANNEL_ALARMS = "alarms";
    private static final int CONNECTION_NOTIFICATION_ID = 1001;
    private static final String ALARM_EXTRAS_KEY = "lsmonitoring::alarm";
    private static final String TEST_EXTRAS_KEY = "lsmonitoring::test";

    private final Handler handler = new Handler(Looper.getMainLooper());
    private OkHttpClient client;
    private WebSocket webSocket;
    private boolean stopped;
    private String currentServerUrl = "";

    @Override
    public void onCreate() {
        super.onCreate();
        client = new OkHttpClient();
        createNotificationChannels();
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null && ACTION_STOP.equals(intent.getAction())) {
            stopped = true;
            stopSelf();
            return START_NOT_STICKY;
        }

        stopped = false;
        startForeground(CONNECTION_NOTIFICATION_ID, buildConnectionNotification("Подключение..."));
        connect();
        return START_STICKY;
    }

    @Override
    public void onDestroy() {
        stopped = true;
        if (webSocket != null) {
            webSocket.close(1000, "service stopped");
            webSocket = null;
        }
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    private void connect() {
        SharedPreferences prefs = getSharedPreferences(MainActivity.PREFS, MODE_PRIVATE);
        String serverUrl = prefs.getString(MainActivity.PREF_SERVER_URL, "");
        String clientToken = prefs.getString(MainActivity.PREF_CLIENT_TOKEN, "");
        if (serverUrl == null || clientToken == null || serverUrl.trim().isEmpty() || clientToken.trim().isEmpty()) {
            updateConnectionNotification("Не настроено");
            saveStatus("Не настроено");
            return;
        }

        currentServerUrl = serverUrl.trim();
        saveStatus("Подключение к " + compact(currentServerUrl));
        String streamUrl = buildStreamUrl(serverUrl, clientToken);
        Request request = new Request.Builder().url(streamUrl).build();
        webSocket = client.newWebSocket(request, new Listener());
    }

    private String buildStreamUrl(String serverUrl, String clientToken) {
        String base = serverUrl.trim();
        if (base.endsWith("/")) {
            base = base.substring(0, base.length() - 1);
        }

        if (base.startsWith("https://")) {
            base = "wss://" + base.substring("https://".length());
        } else if (base.startsWith("http://")) {
            base = "ws://" + base.substring("http://".length());
        }

        return base + "/stream?token=" + urlEncode(clientToken.trim());
    }

    private void handleMessage(String body) {
        try {
            JSONObject root = new JSONObject(body);
            saveLastMessage(root);
            JSONObject extras = root.optJSONObject("extras");
            if (extras == null) {
                showInfoMessage(root);
                return;
            }

            JSONObject alarm = extras.optJSONObject(ALARM_EXTRAS_KEY);
            if (alarm == null) {
                if (extras.optJSONObject(TEST_EXTRAS_KEY) != null) {
                    showInfoMessage(root);
                }
                return;
            }

            String event = alarm.optString("event", "");
            if ("active".equals(event)) {
                showActiveAlarm(alarm);
            } else if ("resolved".equals(event)) {
                showResolvedAlarm(alarm);
            } else {
                showInfoMessage(root);
            }
        } catch (Exception ignored) {
        }
    }

    private void showInfoMessage(JSONObject root) {
        int id = root.optInt("id", (int) (System.currentTimeMillis() % 100000));
        String title = root.optString("title", "LS Monitoring");
        String text = root.optString("message", "");
        if (text.isEmpty()) {
            text = "Новое сообщение";
        }

        notify(infoNotificationId(id), new Notification.Builder(this, CHANNEL_ALARMS)
            .setSmallIcon(R.drawable.ic_stat_ls)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(new Notification.BigTextStyle().bigText(text))
            .setContentIntent(mainActivityIntent())
            .setOngoing(false)
            .setAutoCancel(true)
            .setWhen(System.currentTimeMillis())
            .setShowWhen(true)
            .setPriority(Notification.PRIORITY_HIGH)
            .build());
    }

    private void showActiveAlarm(JSONObject alarm) {
        int nodeId = alarm.optInt("nodeId");
        String axis = alarm.optString("axis", "?");
        double value = alarm.optDouble("value", 0);
        long startedAt = parseTimeMillis(alarm.optString("startedAt", ""));
        int notificationId = alarmNotificationId(nodeId, axis);

        String title = "Тревога: узел " + nodeId + ", ось " + axis;
        String text = "Отклонение: " + String.format("%.3f°", value);
        String bigText = text + "\nТаймер идёт с момента начала события.";

        notify(notificationId, new Notification.Builder(this, CHANNEL_ALARMS)
            .setSmallIcon(R.drawable.ic_stat_ls)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(new Notification.BigTextStyle().bigText(bigText))
            .setContentIntent(mainActivityIntent())
            .setOngoing(true)
            .setAutoCancel(false)
            .setOnlyAlertOnce(true)
            .setWhen(startedAt)
            .setShowWhen(true)
            .setUsesChronometer(true)
            .setChronometerCountDown(false)
            .setPriority(Notification.PRIORITY_HIGH)
            .build());
    }

    private void showResolvedAlarm(JSONObject alarm) {
        int nodeId = alarm.optInt("nodeId");
        String axis = alarm.optString("axis", "?");
        int durationSeconds = alarm.optInt("durationSeconds", 0);
        int notificationId = alarmNotificationId(nodeId, axis);

        String title = "Норма: узел " + nodeId + ", ось " + axis;
        String text = "Событие завершено, длительность " + formatDuration(durationSeconds);

        notify(notificationId, new Notification.Builder(this, CHANNEL_ALARMS)
            .setSmallIcon(R.drawable.ic_stat_ls)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(new Notification.BigTextStyle().bigText(text))
            .setContentIntent(mainActivityIntent())
            .setOngoing(false)
            .setAutoCancel(true)
            .setWhen(System.currentTimeMillis())
            .setShowWhen(true)
            .setPriority(Notification.PRIORITY_DEFAULT)
            .build());
    }

    private void notify(int id, Notification notification) {
        NotificationManager manager = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        manager.notify(id, notification);
    }

    private void updateConnectionNotification(String text) {
        saveStatus(text);
        notify(CONNECTION_NOTIFICATION_ID, buildConnectionNotification(text));
    }

    private Notification buildConnectionNotification(String text) {
        return new Notification.Builder(this, CHANNEL_CONNECTION)
            .setSmallIcon(R.drawable.ic_stat_ls)
            .setContentTitle("LS Monitoring")
            .setContentText(text)
            .setContentIntent(mainActivityIntent())
            .setOngoing(true)
            .setPriority(Notification.PRIORITY_LOW)
            .build();
    }

    private PendingIntent mainActivityIntent() {
        Intent intent = new Intent(this, MainActivity.class);
        return PendingIntent.getActivity(
            this,
            0,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
    }

    private void createNotificationChannels() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
            return;
        }

        NotificationManager manager = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        manager.createNotificationChannel(new NotificationChannel(
            CHANNEL_CONNECTION,
            "Подключение LS Monitoring",
            NotificationManager.IMPORTANCE_LOW));
        manager.createNotificationChannel(new NotificationChannel(
            CHANNEL_ALARMS,
            "Тревоги LS Monitoring",
            NotificationManager.IMPORTANCE_HIGH));
    }

    private int alarmNotificationId(int nodeId, String axis) {
        return 2000 + Math.abs((nodeId + ":" + axis).hashCode() % 100000);
    }

    private int infoNotificationId(int messageId) {
        return 120000 + Math.abs(messageId % 100000);
    }

    private long parseTimeMillis(String value) {
        try {
            return OffsetDateTime.parse(value).toInstant().toEpochMilli();
        } catch (Exception ignored) {
            return System.currentTimeMillis();
        }
    }

    private String formatDuration(int totalSeconds) {
        int hours = totalSeconds / 3600;
        int minutes = (totalSeconds % 3600) / 60;
        int seconds = totalSeconds % 60;
        return String.format("%02d:%02d:%02d", hours, minutes, seconds);
    }

    private void saveStatus(String value) {
        getSharedPreferences(MainActivity.PREFS, MODE_PRIVATE)
            .edit()
            .putString(MainActivity.PREF_LAST_STATUS, value)
            .apply();
    }

    private void saveLastMessage(JSONObject root) {
        String title = root.optString("title", "LS Monitoring");
        String message = root.optString("message", "");
        getSharedPreferences(MainActivity.PREFS, MODE_PRIVATE)
            .edit()
            .putString(MainActivity.PREF_LAST_MESSAGE, title + ": " + message)
            .putString(MainActivity.PREF_LAST_MESSAGE_AT, java.text.DateFormat.getTimeInstance().format(new java.util.Date()))
            .apply();
    }

    private String compact(String value) {
        if (value == null || value.isEmpty()) {
            return "";
        }

        return value.length() <= 36 ? value : value.substring(0, 33) + "...";
    }

    private String urlEncode(String value) {
        try {
            return URLEncoder.encode(value, "UTF-8");
        } catch (UnsupportedEncodingException ex) {
            return value;
        }
    }

    private final class Listener extends WebSocketListener {
        @Override
        public void onOpen(WebSocket webSocket, Response response) {
            updateConnectionNotification("Подключено к " + compact(currentServerUrl));
        }

        @Override
        public void onMessage(WebSocket webSocket, String text) {
            handleMessage(text);
        }

        @Override
        public void onFailure(WebSocket webSocket, Throwable t, Response response) {
            updateConnectionNotification("Связь потеряна: " + t.getClass().getSimpleName());
            if (!stopped) {
                handler.postDelayed(() -> {
                    if (!stopped) {
                        connect();
                    }
                }, 5000);
            }
        }

        @Override
        public void onClosed(WebSocket webSocket, int code, String reason) {
            if (!stopped) {
                updateConnectionNotification("Соединение закрыто: " + code);
                handler.postDelayed(() -> {
                    if (!stopped) {
                        connect();
                    }
                }, 5000);
            }
        }
    }
}
