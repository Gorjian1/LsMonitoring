package ru.lsmonitoring.alerts;

import android.Manifest;
import android.app.Activity;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.provider.Settings;
import android.text.InputType;
import android.view.Gravity;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.io.IOException;
import okhttp3.Call;
import okhttp3.Callback;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import org.json.JSONArray;
import org.json.JSONObject;

public final class MainActivity extends Activity {
    static final String PREFS = "ls_monitoring_alerts";
    static final String PREF_SERVER_URL = "server_url";
    static final String PREF_CLIENT_TOKEN = "client_token";
    static final String PREF_LAST_STATUS = "last_status";
    static final String PREF_LAST_MESSAGE = "last_message";
    static final String PREF_LAST_MESSAGE_AT = "last_message_at";
    private static final String CHANNEL_ALARMS = "alarms";
    private static final String LATEST_RELEASE_URL = "https://api.github.com/repos/Gorjian1/LsMonitoring/releases/latest";

    private EditText serverUrlBox;
    private EditText clientTokenBox;
    private TextView statusText;
    private TextView diagnosticsText;
    private final OkHttpClient httpClient = new OkHttpClient();

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        requestNotificationPermission();
        createNotificationChannel();
        buildUi();
        loadSettings();
        applyDeepLink(getIntent());
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        loadSettings();
        applyDeepLink(intent);
    }

    @Override
    protected void onResume() {
        super.onResume();
        refreshDiagnostics();
    }

    private void buildUi() {
        float density = getResources().getDisplayMetrics().density;
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(dp(20, density), dp(20, density), dp(20, density), dp(20, density));
        root.setGravity(Gravity.CENTER_VERTICAL);

        TextView title = new TextView(this);
        title.setText("LS Alerts");
        title.setTextSize(24);
        title.setGravity(Gravity.START);
        root.addView(title);

        TextView subtitle = new TextView(this);
        subtitle.setText("Пуши тревог LS Monitoring");
        subtitle.setTextSize(14);
        subtitle.setPadding(0, dp(6, density), 0, dp(18, density));
        root.addView(subtitle);

        serverUrlBox = new EditText(this);
        serverUrlBox.setHint("Сервер Gotify");
        serverUrlBox.setSingleLine(true);
        serverUrlBox.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        root.addView(serverUrlBox);

        clientTokenBox = new EditText(this);
        clientTokenBox.setHint("Client token");
        clientTokenBox.setSingleLine(true);
        root.addView(clientTokenBox);

        Button connectButton = new Button(this);
        connectButton.setText("Подключить");
        connectButton.setOnClickListener(v -> connect());
        root.addView(connectButton);

        Button stopButton = new Button(this);
        stopButton.setText("Остановить");
        stopButton.setOnClickListener(v -> stopService(new Intent(this, AlarmStreamService.class)));
        root.addView(stopButton);

        statusText = new TextView(this);
        statusText.setText("Сканируйте QR из LS Monitoring или введите сервер и client token.");
        statusText.setTextSize(13);
        statusText.setPadding(0, dp(14, density), 0, 0);
        root.addView(statusText);

        Button localTestButton = new Button(this);
        localTestButton.setText("Тест уведомления");
        localTestButton.setOnClickListener(v -> showLocalTestNotification());
        root.addView(localTestButton);

        Button notificationSettingsButton = new Button(this);
        notificationSettingsButton.setText("Настройки уведомлений");
        notificationSettingsButton.setOnClickListener(v -> openNotificationSettings());
        root.addView(notificationSettingsButton);

        Button refreshButton = new Button(this);
        refreshButton.setText("Обновить статус");
        refreshButton.setOnClickListener(v -> refreshDiagnostics());
        root.addView(refreshButton);

        Button updateButton = new Button(this);
        updateButton.setText("Проверить обновление");
        updateButton.setOnClickListener(v -> checkForUpdate());
        root.addView(updateButton);

        diagnosticsText = new TextView(this);
        diagnosticsText.setTextSize(12);
        diagnosticsText.setPadding(0, dp(12, density), 0, 0);
        root.addView(diagnosticsText);

        setContentView(root);
    }

    private void applyDeepLink(Intent intent) {
        Uri data = intent.getData();
        if (data == null || !"lsmonitoring".equals(data.getScheme()) || !"connect".equals(data.getHost())) {
            return;
        }

        String server = data.getQueryParameter("server");
        String token = data.getQueryParameter("token");
        if (server == null || token == null || server.trim().isEmpty() || token.trim().isEmpty()) {
            return;
        }

        getPrefs().edit()
            .putString(PREF_SERVER_URL, server.trim())
            .putString(PREF_CLIENT_TOKEN, token.trim())
            .apply();

        serverUrlBox.setText(server.trim());
        clientTokenBox.setText(token.trim());
        connect(server.trim(), token.trim());
    }

    private void loadSettings() {
        SharedPreferences prefs = getPrefs();
        serverUrlBox.setText(prefs.getString(PREF_SERVER_URL, ""));
        clientTokenBox.setText(prefs.getString(PREF_CLIENT_TOKEN, ""));
        refreshDiagnostics();
    }

    private void connect() {
        String serverUrl = serverUrlBox.getText().toString().trim();
        String clientToken = clientTokenBox.getText().toString().trim();
        connect(serverUrl, clientToken);
    }

    private void connect(String serverUrl, String clientToken) {
        if (serverUrl.isEmpty() || clientToken.isEmpty()) {
            statusText.setText("Нужны сервер и client token.");
            return;
        }

        getPrefs().edit()
            .putString(PREF_SERVER_URL, serverUrl)
            .putString(PREF_CLIENT_TOKEN, clientToken)
            .apply();

        Intent intent = new Intent(this, AlarmStreamService.class);
        intent.setAction(AlarmStreamService.ACTION_START);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            startForegroundService(intent);
        } else {
            startService(intent);
        }

        statusText.setText("Подключено. Уведомления будут приходить в фоне.");
        refreshDiagnostics();
    }

    private void requestNotificationPermission() {
        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[] { Manifest.permission.POST_NOTIFICATIONS }, 10);
        }
    }

    private void showLocalTestNotification() {
        if (!notificationsAllowed()) {
            statusText.setText("Уведомления запрещены в Android. Откройте настройки уведомлений.");
            refreshDiagnostics();
            return;
        }

        Notification notification = new Notification.Builder(this, CHANNEL_ALARMS)
            .setSmallIcon(R.drawable.ic_stat_ls)
            .setContentTitle("LS Monitoring")
            .setContentText("Локальный тест уведомления LS Alerts")
            .setStyle(new Notification.BigTextStyle().bigText("Локальный тест уведомления LS Alerts"))
            .setContentIntent(mainActivityIntent())
            .setOngoing(false)
            .setAutoCancel(true)
            .setWhen(System.currentTimeMillis())
            .setShowWhen(true)
            .setPriority(Notification.PRIORITY_HIGH)
            .build();

        NotificationManager manager = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        manager.notify(999, notification);
        statusText.setText("Локальный тест отправлен.");
    }

    private void openNotificationSettings() {
        Intent intent;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            intent = new Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
                .putExtra(Settings.EXTRA_APP_PACKAGE, getPackageName());
        } else {
            intent = new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS)
                .setData(Uri.parse("package:" + getPackageName()));
        }

        startActivity(intent);
    }

    private void checkForUpdate() {
        statusText.setText("Проверка GitHub Releases...");
        Request request = new Request.Builder()
            .url(LATEST_RELEASE_URL)
            .header("Accept", "application/vnd.github+json")
            .header("User-Agent", "LS-Alerts-Android")
            .build();

        httpClient.newCall(request).enqueue(new Callback() {
            @Override
            public void onFailure(Call call, IOException e) {
                runOnUiThread(() -> statusText.setText("Не удалось проверить обновление: " + e.getMessage()));
            }

            @Override
            public void onResponse(Call call, Response response) throws IOException {
                String body = response.body() == null ? "" : response.body().string();
                if (!response.isSuccessful()) {
                    runOnUiThread(() -> statusText.setText("GitHub ответил HTTP " + response.code()));
                    return;
                }

                handleReleaseResponse(body);
            }
        });
    }

    private void handleReleaseResponse(String body) {
        try {
            JSONObject release = new JSONObject(body);
            String latestVersion = normalizeVersion(release.optString("tag_name", ""));
            String currentVersion = normalizeVersion(appVersion());
            String apkUrl = findApkAssetUrl(release.optJSONArray("assets"));

            if (latestVersion.isEmpty()) {
                runOnUiThread(() -> statusText.setText("В latest release нет tag_name."));
                return;
            }

            if (compareVersions(latestVersion, currentVersion) <= 0) {
                runOnUiThread(() -> statusText.setText("Версия актуальна: " + appVersion()));
                return;
            }

            if (apkUrl.isEmpty()) {
                runOnUiThread(() -> statusText.setText("Есть версия " + latestVersion + ", но APK asset не найден."));
                return;
            }

            runOnUiThread(() -> {
                statusText.setText("Найдена версия " + latestVersion + ". Открываю скачивание.");
                startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(apkUrl)));
            });
        } catch (Exception ex) {
            runOnUiThread(() -> statusText.setText("Ошибка проверки версии: " + ex.getMessage()));
        }
    }

    private String findApkAssetUrl(JSONArray assets) {
        if (assets == null) {
            return "";
        }

        for (int i = 0; i < assets.length(); i++) {
            JSONObject asset = assets.optJSONObject(i);
            if (asset == null) {
                continue;
            }

            String name = asset.optString("name", "").toLowerCase();
            String url = asset.optString("browser_download_url", "");
            if (name.endsWith(".apk") && name.contains("alerts") && !url.isEmpty()) {
                return url;
            }
        }

        for (int i = 0; i < assets.length(); i++) {
            JSONObject asset = assets.optJSONObject(i);
            if (asset == null) {
                continue;
            }

            String name = asset.optString("name", "").toLowerCase();
            String url = asset.optString("browser_download_url", "");
            if (name.endsWith(".apk") && !url.isEmpty()) {
                return url;
            }
        }

        return "";
    }

    private void refreshDiagnostics() {
        if (diagnosticsText == null) {
            return;
        }

        SharedPreferences prefs = getPrefs();
        String lastStatus = prefs.getString(PREF_LAST_STATUS, "ещё нет");
        String lastMessage = prefs.getString(PREF_LAST_MESSAGE, "ещё нет");
        String lastMessageAt = prefs.getString(PREF_LAST_MESSAGE_AT, "");
        String permission = notificationsAllowed() ? "разрешены" : "запрещены";
        String server = serverUrlBox == null ? "" : serverUrlBox.getText().toString().trim();
        diagnosticsText.setText(
            "Версия: " + appVersion() +
            "\nУведомления: " + permission +
            "\nСервер: " + compact(server) +
            "\nСервис: " + lastStatus +
            "\nПоследнее сообщение: " + lastMessage +
            (lastMessageAt.isEmpty() ? "" : "\nВремя: " + lastMessageAt));
    }

    private boolean notificationsAllowed() {
        boolean runtimeAllowed = Build.VERSION.SDK_INT < 33 ||
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED;
        NotificationManager manager = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        boolean appAllowed = Build.VERSION.SDK_INT < Build.VERSION_CODES.N || manager.areNotificationsEnabled();
        return runtimeAllowed && appAllowed;
    }

    private void createNotificationChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
            return;
        }

        NotificationManager manager = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        manager.createNotificationChannel(new NotificationChannel(
            CHANNEL_ALARMS,
            "Тревоги LS Monitoring",
            NotificationManager.IMPORTANCE_HIGH));
    }

    private PendingIntent mainActivityIntent() {
        Intent intent = new Intent(this, MainActivity.class);
        return PendingIntent.getActivity(
            this,
            0,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
    }

    private String appVersion() {
        try {
            return getPackageManager().getPackageInfo(getPackageName(), 0).versionName;
        } catch (Exception ignored) {
            return "unknown";
        }
    }

    private String compact(String value) {
        if (value == null || value.isEmpty()) {
            return "не задан";
        }

        return value.length() <= 42 ? value : value.substring(0, 39) + "...";
    }

    private String normalizeVersion(String value) {
        String normalized = value == null ? "" : value.trim().toLowerCase();
        if (normalized.startsWith("v")) {
            normalized = normalized.substring(1);
        }

        int dash = normalized.indexOf('-');
        if (dash >= 0) {
            normalized = normalized.substring(0, dash);
        }

        return normalized;
    }

    private int compareVersions(String left, String right) {
        String[] leftParts = left.split("\\.");
        String[] rightParts = right.split("\\.");
        int length = Math.max(leftParts.length, rightParts.length);

        for (int i = 0; i < length; i++) {
            int leftValue = i < leftParts.length ? parseVersionPart(leftParts[i]) : 0;
            int rightValue = i < rightParts.length ? parseVersionPart(rightParts[i]) : 0;
            if (leftValue != rightValue) {
                return Integer.compare(leftValue, rightValue);
            }
        }

        return 0;
    }

    private int parseVersionPart(String value) {
        String digits = value.replaceAll("[^0-9]", "");
        if (digits.isEmpty()) {
            return 0;
        }

        try {
            return Integer.parseInt(digits);
        } catch (NumberFormatException ignored) {
            return 0;
        }
    }

    private SharedPreferences getPrefs() {
        return getSharedPreferences(PREFS, MODE_PRIVATE);
    }

    private static int dp(int value, float density) {
        return Math.round(value * density);
    }
}
