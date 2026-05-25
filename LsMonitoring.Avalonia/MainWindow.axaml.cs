using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LsMonitoring.Core.Export;
using LsMonitoring.Core.Configuration;
using LsMonitoring.Core.Models;
using LsMonitoring.Core.Monitoring;
using LsMonitoring.Core.Parsing;
using LsMonitoring.Core.Polling;
using LsMonitoring.Core.Sources;
using LsMonitoring.Core.Alarms;

namespace LsMonitoring.Avalonia;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<NodeListItem> _nodes = [];
    private readonly ObservableCollection<ReadingRow> _rows = [];
    private readonly Dictionary<int, NodeListItem> _nodeItemsById = [];
    private readonly Dictionary<int, ReadingBuffer> _buffersByNode = [];
    private readonly string _configPath;
    private AppConfig _config;
    private TelegramAlertService? _telegramAlertService;
    private EmailAlertService? _emailAlertService;
    private GotifyAlertService? _gotifyAlertService;
    private CsvGatewaySource? _source;
    private PollingService? _poller;
    private DispatcherTimer? _heartbeat;
    private int? _currentNode;
    private bool _isPolling;
    private int _totalMessages;
    private DateTime? _nextPollAt;
    private double _plotVisibleWindowSeconds = 3600;
    private DateTime? _plotCutoffTime;
    private bool _isPickingPlotCutoff;

    public MainWindow()
    {
        InitializeComponent();
        _configPath = AppConfig.ResolveDefaultPath();
        _config = AppConfig.Load(_configPath);

        NodeList.ItemsSource = _nodes;
        RowsList.ItemsSource = _rows;

        LoadConfigToUi();
        ReconfigureAlertServices();
        WireEvents();
        LoadNodesFromConfig();
        StartHeartbeat();
        UpdatePollingButtons();
        RefreshCurrentNode();
    }

    protected override async void OnClosed(EventArgs e)
    {
        await StopTelegramAlertsAsync();
        await StopPollingAsync();
        _heartbeat?.Stop();
        base.OnClosed(e);
    }

    private void WireEvents()
    {
        StartButton.Click += async (_, _) => await StartPollingAsync();
        StopButton.Click += async (_, _) => await StopPollingAsync();
        PollNowButton.Click += async (_, _) => await PollOnceAsync();
        SettingsButton.Click += async (_, _) => await ShowSettingsDialogAsync();
        MessagesButton.Click += async (_, _) => await ShowMessagesDialogAsync();
        DiscoverButton.Click += async (_, _) => await DiscoverNodesAsync();
        LoadSampleButton.Click += async (_, _) => await LoadCsvAsync();
        ExportButton.Click += async (_, _) => await ExportCurrentNodeAsync();
        AddNodeButton.Click += AddNode;
        AddNodeButton2.Click += AddNode;
        LimitPlotButton.Click += async (_, _) => await ShowPlotLimitDialogAsync();
        PickPlotLimitButton.Click += (_, _) => TogglePlotLimitPicker();
        ClearPlotLimitButton.Click += (_, _) => SetPlotCutoff(null);
        PlotA.CutoffTimePicked += OnPlotCutoffPicked;
        PlotB.CutoffTimePicked += OnPlotCutoffPicked;
        TimeWindowBox.SelectionChanged += (_, _) =>
        {
            UpdatePlotWindowFromSelection();
            RefreshCurrentNode();
        };
        NodeList.SelectionChanged += (_, _) =>
        {
            if (NodeList.SelectedItem is NodeListItem item)
            {
                _currentNode = item.NodeId;
                UpdateNodeSelection();
                RefreshCurrentNode();
            }
        };
    }

    private void StartHeartbeat()
    {
        _heartbeat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _heartbeat.Tick += (_, _) =>
        {
            foreach (var node in _nodes)
            {
                if (_buffersByNode.TryGetValue(node.NodeId, out var buf))
                {
                    node.UpdateFrom(buf, null, _config.GetCalibration(node.NodeId));
                }
            }

            RefreshStatusBar();
            RefreshCountdown();
        };
        _heartbeat.Start();
    }

    private void LoadConfigToUi()
    {
        GatewayLabel.Text = _config.Connection.GatewayIp;
        RailGatewayText.Text = _config.Connection.GatewayIp;
        SourceText.Text = "CSV";
    }

    private void SaveUiToConfig()
    {
        _config.Nodes = _nodes.Select(x => x.NodeId).ToList();
        _config.Save(_configPath);
    }

    private void LoadNodesFromConfig()
    {
        foreach (var nodeId in _config.Nodes.Distinct().Order())
        {
            EnsureNode(nodeId);
        }

        if (_nodes.Count > 0)
        {
            NodeList.SelectedIndex = 0;
            if (NodeList.SelectedItem is NodeListItem item)
            {
                _currentNode = item.NodeId;
            }
        }

        UpdateNodeSelection();
        UpdateNodeSummary();
    }

    private async Task StartPollingAsync()
    {
        await StopPollingAsync();

        _source = new CsvGatewaySource(
            _config.Connection.GatewayIp,
            _config.Connection.Username,
            _config.Connection.Password,
            TimeSpan.FromSeconds(_config.Connection.RequestTimeoutSeconds));

        var interval = TimeSpan.FromSeconds(_config.Connection.PollingIntervalSeconds);
        _poller = new PollingService(_source, interval);
        _poller.SetNodes(_nodes.Select(x => x.NodeId));
        _poller.ReadingsReady += (nodeId, parsed) => Dispatcher.UIThread.Post(() => OnReadingsReady(nodeId, parsed));
        _poller.ConnectionState += (ok, message) => Dispatcher.UIThread.Post(() => SetConnectionState(ok, message));
        _poller.Error += (nodeId, message) => Dispatcher.UIThread.Post(() => StatusText.Text = $"Узел {nodeId}: {message}");
        _poller.Start();

        _isPolling = true;
        _nextPollAt = DateTime.Now + interval;
        UpdatePollingButtons();
        SetConnectionState(true, _config.Connection.GatewayIp);
    }

    private async Task StopPollingAsync()
    {
        if (_poller is not null)
        {
            await _poller.DisposeAsync();
            _poller = null;
        }

        _source = null;
        _isPolling = false;
        _nextPollAt = null;
        UpdatePollingButtons();
        if (StatusText is not null)
        {
            StatusText.Text = "Ожидание";
        }

        if (ConnectionLabel is not null)
        {
            ConnectionLabel.Text = "Ожидание";
        }

        SetStatusDot("#B0B4BB");
    }

    private async Task PollOnceAsync()
    {
        if (_poller is not null)
        {
            await _poller.PollOnceAsync();
            _nextPollAt = DateTime.Now + TimeSpan.FromSeconds(_config.Connection.PollingIntervalSeconds);
            return;
        }

        await using var source = new CsvGatewaySource(
            _config.Connection.GatewayIp,
            _config.Connection.Username,
            _config.Connection.Password,
            TimeSpan.FromSeconds(_config.Connection.RequestTimeoutSeconds));

        foreach (var node in _nodes.ToList())
        {
            try
            {
                var readings = await source.FetchReadingsAsync(node.NodeId);
                OnReadingsReady(node.NodeId, readings);
            }
            catch (Exception e)
            {
                StatusText.Text = $"Узел {node.NodeId}: {e.Message}";
            }
        }
    }

    private async Task ShowSettingsDialogAsync()
    {
        var dialog = new SettingsDialog();
        dialog.LoadConfig(_config);

        var saved = await dialog.ShowDialog<bool>(this);
        if (saved)
        {
            _config.Save(_configPath);
            LoadConfigToUi();
            RefreshCurrentNode();
        }
    }

    private async Task ShowMessagesDialogAsync()
    {
        await StopTelegramAlertsAsync();
        var dialog = new MessagesDialog();
        dialog.LoadConfig(_config);

        try
        {
            var saved = await dialog.ShowDialog<bool>(this);
            if (saved)
            {
                _config.Save(_configPath);
                RefreshCurrentNode();
            }
        }
        finally
        {
            ReconfigureAlertServices();
        }
    }

    private void ReconfigureAlertServices()
    {
        ReconfigureTelegramAlerts();
        ReconfigureEmailAlerts();
        ReconfigureGotifyAlerts();
    }

    private void ReconfigureTelegramAlerts()
    {
        StopTelegramAlerts();

        var botToken = _config.Telegram.EffectiveBotToken;
        if (_config.Telegram.Enabled && !string.IsNullOrWhiteSpace(botToken))
        {
            _telegramAlertService = new TelegramAlertService(
                botToken, 
                _config.Telegram.ChatIds.ToList(), // pass a copy or reference depending on logic, list is fine
                OnNewChatIdDiscovered);
        }
    }

    private void StopTelegramAlerts()
    {
        _telegramAlertService?.Stop();
        _telegramAlertService = null;
    }

    private async Task StopTelegramAlertsAsync()
    {
        var service = _telegramAlertService;
        _telegramAlertService = null;
        if (service is not null)
        {
            await service.StopAsync();
        }
    }

    private void ReconfigureEmailAlerts()
    {
        _emailAlertService = _config.Email.Enabled ? new EmailAlertService(_config.Email) : null;
    }

    private void ReconfigureGotifyAlerts()
    {
        _gotifyAlertService = _config.Push.Enabled ? new GotifyAlertService(_config.Push) : null;
    }

    private void OnNewChatIdDiscovered(long newChatId)
    {
        Dispatcher.UIThread.Post(() => 
        {
            if (!_config.Telegram.ChatIds.Contains(newChatId))
            {
                _config.Telegram.ChatIds.Add(newChatId);
                _config.Save(_configPath);
            }
        });
    }

    private async Task DiscoverNodesAsync()
    {
        try
        {
            StatusText.Text = "Поиск узлов…";
            await using var source = new CsvGatewaySource(
                _config.Connection.GatewayIp,
                _config.Connection.Username,
                _config.Connection.Password,
                TimeSpan.FromSeconds(_config.Connection.RequestTimeoutSeconds));

            var found = await source.DiscoverNodesAsync();
            foreach (var node in found)
            {
                EnsureNode(node.NodeId).Model = node.Model;
            }

            StatusText.Text = found.Count == 0 ? "Активные узлы не найдены" : $"Найдено активных узлов: {found.Count}";
            SaveUiToConfig();
            UpdateNodeSummary();
        }
        catch (Exception e)
        {
            StatusText.Text = $"Ошибка поиска: {e.Message}";
        }
    }

    private async Task LoadCsvAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Открыть CSV с измерениями",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("CSV") { Patterns = ["*.csv"] },
                FilePickerFileTypes.All
            ]
        });

        if (files.Count == 0)
        {
            return;
        }

        await using var stream = await files[0].OpenReadAsync();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        var parsed = CmtCsvParser.Parse(memory.ToArray());
        var nodeId = parsed.NodeId ?? _currentNode ?? 0;
        if (nodeId <= 0)
        {
            StatusText.Text = "В CSV отсутствует Node ID";
            return;
        }

        OnReadingsReady(nodeId, NodeReadings.FromParsedCsv(nodeId, parsed));
        StatusText.Text = $"CSV загружен — узел {nodeId}";
    }

    private async Task ExportCurrentNodeAsync()
    {
        if (_currentNode is not { } nodeId || !_buffersByNode.TryGetValue(nodeId, out var buffer) || buffer.Count == 0)
        {
            StatusText.Text = "Нет данных по текущему узлу для экспорта";
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Экспорт CSV по текущему узлу",
            SuggestedFileName = $"Узел-{nodeId}-измерения.csv",
            FileTypeChoices =
            [
                new FilePickerFileType("CSV") { Patterns = ["*.csv"] },
                FilePickerFileTypes.All
            ]
        });

        if (file is null)
        {
            return;
        }

        var readingsToExport = ApplyPlotCutoff(buffer.Readings).ToList();
        if (readingsToExport.Count == 0)
        {
            StatusText.Text = "Нет видимых данных для экспорта";
            return;
        }

        ReadingsCsvExporter.Export(file.Path.LocalPath, readingsToExport, _config.Thresholds, _config.Alarm, _config.GetCalibration(nodeId));
        StatusText.Text = $"Экспортирован узел {nodeId}";
    }

    private void AddNode(object? sender, RoutedEventArgs e)
    {
        if (int.TryParse(NodeIdBox.Text, out var nodeId))
        {
            EnsureNode(nodeId);
            NodeIdBox.Text = "";
            SaveUiToConfig();
            UpdateNodeSummary();
        }
    }

    private NodeListItem EnsureNode(int nodeId)
    {
        if (_nodeItemsById.TryGetValue(nodeId, out var existing))
        {
            return existing;
        }

        var item = new NodeListItem(nodeId);
        var buffer = new ReadingBuffer();
        _nodeItemsById[nodeId] = item;
        _buffersByNode[nodeId] = buffer;
        _nodes.Add(item);
        item.UpdateFrom(buffer);

        _poller?.SetNodes(_nodes.Select(x => x.NodeId));
        if (NodeList.SelectedIndex < 0)
        {
            NodeList.SelectedItem = item;
        }

        UpdateNodeSummary();
        UpdatePollingButtons();
        return item;
    }

    private void OnReadingsReady(int nodeId, NodeReadings nodeReadings)
    {
        var item = EnsureNode(nodeId);
        item.Model = nodeReadings.Model ?? item.Model;
        var buffer = _buffersByNode[nodeId];
        var before = buffer.Latest;
        buffer.Merge(nodeReadings.Readings, _config.PlotBufferPoints);
        item.UpdateFrom(buffer, nodeReadings.Model, _config.GetCalibration(nodeId));

        _totalMessages += nodeReadings.Readings.Count;

        if (_currentNode is null)
        {
            _currentNode = nodeId;
            NodeList.SelectedItem = item;
        }

        if (_currentNode == nodeId)
        {
            RefreshCurrentNode(before);
        }

        UpdateNodeSummary();
        _nextPollAt = DateTime.Now + TimeSpan.FromSeconds(_config.Connection.PollingIntervalSeconds);

        var latest = buffer.Latest;
        TriggerAlarmNotificationsIfNeeded(nodeId, latest);
    }

    private void TriggerAlarmNotificationsIfNeeded(int nodeId, Reading? latest)
    {
        if (latest is null)
        {
            TelegramAlertService.LogDiagnostic(
                $"alarm-skip node={nodeId} latest=null telegram_enabled={_config.Telegram.Enabled} email_enabled={_config.Email.Enabled} gotify_enabled={_config.Push.Enabled}");
            return;
        }

        if (IsBeforePlotCutoff(latest.Timestamp))
        {
            TelegramAlertService.LogDiagnostic(
                $"alarm-skip node={nodeId} timestamp={latest.Timestamp:O} cutoff={_plotCutoffTime:O}");
            return;
        }

        var calibration = _config.GetCalibration(nodeId);
        var aDeviation = _config.Thresholds.DeviationA(latest, calibration?.ZeroA);
        var bDeviation = _config.Thresholds.DeviationB(latest, calibration?.ZeroB);
        var criticalA = _config.Thresholds.CriticalA;
        var criticalB = _config.Thresholds.EffectiveCriticalB();
        var isACritical = IsCriticalDeviation(aDeviation, criticalA);
        var isBCritical = IsCriticalDeviation(bDeviation, criticalB);

        var effZeroA = calibration?.ZeroA ?? _config.Thresholds.ZeroA;
        var effZeroB = calibration?.ZeroB ?? _config.Thresholds.ZeroB;
        TelegramAlertService.LogDiagnostic(
            $"alarm-eval node={nodeId} mode={_config.Thresholds.Mode} " +
            $"dA={aDeviation?.ToString("F3") ?? "—"} dB={bDeviation?.ToString("F3") ?? "—"} " +
            $"thA={criticalA} thB={criticalB} zeroA={effZeroA} zeroB={effZeroB} " +
            $"critA={isACritical} critB={isBCritical} invalid={latest.Invalid}");

        if (_telegramAlertService is { } telegram)
        {
            _ = SafeUpdateTelegramAlarmAsync(telegram, nodeId, "A", isACritical, aDeviation ?? 0, latest.Timestamp);
            _ = SafeUpdateTelegramAlarmAsync(telegram, nodeId, "B", isBCritical, bDeviation ?? 0, latest.Timestamp);
        }

        if (_emailAlertService is { } email)
        {
            _ = SafeUpdateEmailAlarmAsync(email, nodeId, "A", isACritical, aDeviation ?? 0, latest.Timestamp);
            _ = SafeUpdateEmailAlarmAsync(email, nodeId, "B", isBCritical, bDeviation ?? 0, latest.Timestamp);
        }

        if (_gotifyAlertService is { } gotify)
        {
            _ = SafeUpdateGotifyAlarmAsync(gotify, nodeId, "A", isACritical, aDeviation ?? 0, latest.Timestamp);
            _ = SafeUpdateGotifyAlarmAsync(gotify, nodeId, "B", isBCritical, bDeviation ?? 0, latest.Timestamp);
        }
    }

    private static async Task SafeUpdateTelegramAlarmAsync(TelegramAlertService service, int nodeId, string axis, bool isCritical, double value, DateTime timestamp)
    {
        try
        {
            await service.UpdateAlarmAsync(nodeId, axis, isCritical, value, timestamp);
        }
        catch (Exception ex)
        {
            TelegramAlertService.LogDiagnostic($"alarm-exception node={nodeId} axis={axis}: {ex.Message}");
        }
    }

    private static async Task SafeUpdateEmailAlarmAsync(EmailAlertService service, int nodeId, string axis, bool isCritical, double value, DateTime timestamp)
    {
        try
        {
            await service.UpdateAlarmAsync(nodeId, axis, isCritical, value, timestamp);
        }
        catch (Exception ex)
        {
            EmailAlertService.LogDiagnostic($"alarm-exception node={nodeId} axis={axis}: {ex.Message}");
        }
    }

    private static async Task SafeUpdateGotifyAlarmAsync(GotifyAlertService service, int nodeId, string axis, bool isCritical, double value, DateTime timestamp)
    {
        try
        {
            await service.UpdateAlarmAsync(nodeId, axis, isCritical, value, timestamp);
        }
        catch (Exception ex)
        {
            GotifyAlertService.LogDiagnostic($"alarm-exception node={nodeId} axis={axis}: {ex.Message}");
        }
    }

    private void RefreshCurrentNode(Reading? previousLatest = null)
    {
        UpdateNodeSelection();

        if (_currentNode is not { } nodeId || !_buffersByNode.TryGetValue(nodeId, out var buffer))
        {
            PlotA.Readings = [];
            PlotB.Readings = [];
            _rows.Clear();
            PlotMetaText.Text = "";
            RowsMetaText.Text = "";
            AlarmBanner.IsVisible = false;
            ResetDashboardSummary();
            ApplyPlotLimitStateToControls();
            ExportButton.IsEnabled = false;
            return;
        }

        var calibration = _config.GetCalibration(nodeId);
        var readings = buffer.Readings;
        var visibleReadings = ApplyPlotCutoff(readings).ToList();
        var plotReadings = readings.ToList();
        PlotA.Readings = plotReadings;
        PlotA.ExpectedIntervalSeconds = buffer.EstimatedSamplingIntervalSeconds;
        PlotA.WarningThreshold = _config.Thresholds.WarningA;
        PlotA.CriticalThreshold = _config.Thresholds.CriticalA;
        PlotA.VisibleWindowSeconds = _plotVisibleWindowSeconds;
        PlotA.ValueMode = _config.Thresholds.Mode;
        PlotA.ZeroA = calibration?.ZeroA ?? _config.Thresholds.ZeroA;
        PlotA.ZeroB = calibration?.ZeroB ?? _config.Thresholds.ZeroB;
        PlotA.CutoffTime = _plotCutoffTime;
        PlotA.IsPickingCutoff = _isPickingPlotCutoff;

        PlotB.Readings = plotReadings;
        PlotB.ExpectedIntervalSeconds = buffer.EstimatedSamplingIntervalSeconds;
        PlotB.WarningThreshold = _config.Thresholds.EffectiveWarningB();
        PlotB.CriticalThreshold = _config.Thresholds.EffectiveCriticalB();
        PlotB.VisibleWindowSeconds = _plotVisibleWindowSeconds;
        PlotB.ValueMode = _config.Thresholds.Mode;
        PlotB.ZeroA = calibration?.ZeroA ?? _config.Thresholds.ZeroA;
        PlotB.ZeroB = calibration?.ZeroB ?? _config.Thresholds.ZeroB;
        PlotB.CutoffTime = _plotCutoffTime;
        PlotB.IsPickingCutoff = _isPickingPlotCutoff;

        _rows.Clear();
        foreach (var row in visibleReadings.TakeLast(200).Reverse().Select(x => new ReadingRow(x, _config.Thresholds, calibration)))
        {
            _rows.Add(row);
        }

        var latest = visibleReadings.LastOrDefault();
        var aDeviation = latest is null ? null : _config.Thresholds.DeviationA(latest, calibration?.ZeroA);
        var bDeviation = latest is null ? null : _config.Thresholds.DeviationB(latest, calibration?.ZeroB);
        var isACritical = IsCriticalDeviation(aDeviation, _config.Thresholds.CriticalA);
        var isBCritical = IsCriticalDeviation(bDeviation, _config.Thresholds.EffectiveCriticalB());
        UpdateDashboardSummary(nodeId, buffer, readings.Count, visibleReadings.Count, latest, calibration);

        // Banner всегда показывается на критике (без скрытия)
        if (isACritical || isBCritical)
        {
            AlarmBanner.IsVisible = true;
            var parts = new List<string>();
            if (isACritical && aDeviation is { } av) parts.Add($"A откл.={av:F3}°");
            if (isBCritical && bDeviation is { } bv) parts.Add($"B откл.={bv:F3}°");
            AlarmBannerText.Text = $"Узел {nodeId}: отклонение вышло за заданный максимум  •  {string.Join("  ", parts)}";
        }
        else
        {
            AlarmBanner.IsVisible = false;
        }

        PlotMetaText.Text = $"Узел {nodeId} · {PlotPointCountLabel(visibleReadings.Count, readings.Count)} · {PlotWindowLabel()} · {ThresholdModeLabel()}{PlotCutoffLabel()}";
        RowsMetaText.Text = $"{Math.Min(200, visibleReadings.Count)} строк";
        ApplyPlotLimitStateToControls();

        ExportButton.IsEnabled = _currentNode is not null;
    }

    private void ResetDashboardSummary()
    {
        RailNodeIdText.Text = "-";
        RailModelText.Text = "-";
        RailPointsText.Text = "-";
        RailSamplingText.Text = "-";
        RailLastText.Text = "-";
        RailModeText.Text = ThresholdModeLabel();
        RailWarningAText.Text = FormatThreshold(_config.Thresholds.WarningA);
        RailCriticalAText.Text = FormatThreshold(_config.Thresholds.CriticalA);
        RailWarningBText.Text = FormatThreshold(_config.Thresholds.EffectiveWarningB());
        RailCriticalBText.Text = FormatThreshold(_config.Thresholds.EffectiveCriticalB());
        RailCutoffText.Text = PlotCutoffShortLabel();
        RailZeroAText.Text = ReadingSnapshot.FormatValue(_config.Thresholds.ZeroA);
        RailZeroBText.Text = ReadingSnapshot.FormatValue(_config.Thresholds.ZeroB);
        RailGatewayText.Text = _config.Connection.GatewayIp;
    }

    private void UpdateDashboardSummary(
        int nodeId,
        ReadingBuffer buffer,
        int totalCount,
        int visibleCount,
        Reading? latest,
        NodeCalibration? calibration)
    {
        var model = _nodeItemsById.TryGetValue(nodeId, out var item) && !string.IsNullOrWhiteSpace(item.Model)
            ? item.Model
            : "модель не определена";
        var latestText = latest is null ? "нет данных" : ReadingSnapshot.FormatTimestamp(latest.Timestamp);

        RailNodeIdText.Text = nodeId.ToString();
        RailModelText.Text = model;
        RailPointsText.Text = visibleCount == totalCount ? totalCount.ToString() : $"{visibleCount}/{totalCount}";
        RailSamplingText.Text = buffer.EstimatedSamplingIntervalSeconds is { } interval
            ? $"~{(int)interval} с"
            : "-";
        RailLastText.Text = latestText;
        RailModeText.Text = ThresholdModeLabel();
        RailWarningAText.Text = FormatThreshold(_config.Thresholds.WarningA);
        RailCriticalAText.Text = FormatThreshold(_config.Thresholds.CriticalA);
        RailWarningBText.Text = FormatThreshold(_config.Thresholds.EffectiveWarningB());
        RailCriticalBText.Text = FormatThreshold(_config.Thresholds.EffectiveCriticalB());
        RailCutoffText.Text = PlotCutoffShortLabel();
        RailZeroAText.Text = ReadingSnapshot.FormatValue(calibration?.ZeroA ?? _config.Thresholds.ZeroA);
        RailZeroBText.Text = ReadingSnapshot.FormatValue(calibration?.ZeroB ?? _config.Thresholds.ZeroB);
        RailGatewayText.Text = _config.Connection.GatewayIp;
    }

    private void RefreshStatusBar()
    {
        if (_currentNode is not { } nodeId || !_buffersByNode.TryGetValue(nodeId, out var buffer))
        {
            SamplingRateLabel.Text = "";
            LastSampleLabel.Text = "";
            return;
        }

        if (buffer.EstimatedSamplingIntervalSeconds is { } interval)
        {
            SamplingRateLabel.Text = $"интервал ~{(int)interval} с";
        }

        var latest = ApplyPlotCutoff(buffer.Readings).LastOrDefault();
        if (latest is not null)
        {
            LastSampleLabel.Text = ReadingSnapshot.FormatTimestamp(latest.Timestamp);
        }
        else
        {
            LastSampleLabel.Text = "";
        }
    }

    private void RefreshCountdown()
    {
        if (!_isPolling || _nextPollAt is null)
        {
            CountdownLabel.Text = _totalMessages > 0 ? $"{_totalMessages} сообщ." : "";
            return;
        }

        var remaining = _nextPollAt.Value - DateTime.Now;
        var secs = Math.Max(0, (int)remaining.TotalSeconds);
        CountdownLabel.Text = $"Опрос через {secs} с  •  {_totalMessages} сообщ.";
    }

    private void SetConnectionState(bool ok, string message)
    {
        if (ok)
        {
            ConnectionLabel.Text = "Подключено";
            GatewayLabel.Text = message;
            SetStatusDot("#16A34A");
        }
        else
        {
            ConnectionLabel.Text = "Нет связи";
            StatusText.Text = message;
            SetStatusDot("#DC2626");
        }
    }

    private void SetStatusDot(string color)
    {
        StatusDot.Fill = new SolidColorBrush(Color.Parse(color));
    }

    private void UpdateNodeSummary()
    {
        NodeSummaryText.Text = _nodes.Count switch
        {
            1 => "1 узел",
            >= 2 and <= 4 => $"{_nodes.Count} узла",
            _ => $"{_nodes.Count} узлов"
        };
    }

    private void UpdateNodeSelection()
    {
        foreach (var node in _nodes)
        {
            node.IsSelected = _currentNode == node.NodeId;
        }
    }

    private void UpdatePollingButtons()
    {
        if (StartButton is null || StopButton is null)
        {
            return;
        }

        StartButton.IsEnabled = !_isPolling;
        StopButton.IsEnabled = _isPolling;
        PollNowButton.IsEnabled = _nodes.Count > 0;
        ExportButton.IsEnabled = _currentNode is not null;
    }

    private void UpdatePlotWindowFromSelection()
    {
        if (TimeWindowBox.SelectedItem is ComboBoxItem item &&
            double.TryParse(item.Tag?.ToString(), out var seconds))
        {
            _plotVisibleWindowSeconds = seconds;
        }
    }

    private string PlotWindowLabel()
    {
        return TimeWindowBox.SelectedItem is ComboBoxItem item && item.Content is not null
            ? item.Content.ToString() ?? "1 час"
            : "1 час";
    }

    private static bool IsCriticalDeviation(double? deviation, double threshold)
    {
        return deviation is { } value && Math.Abs(value) >= threshold;
    }

    private string ThresholdModeLabel()
    {
        return string.Equals(_config.Thresholds.Mode, Thresholds.VariationMode, StringComparison.OrdinalIgnoreCase)
            ? "ΔA/ΔB от нуля"
            : "A/B от нуля";
    }

    private async Task ShowPlotLimitDialogAsync()
    {
        var dialog = new PlotLimitDialog();
        dialog.Load(_plotCutoffTime, SuggestedPlotCutoffTime());
        var saved = await dialog.ShowDialog<bool>(this);
        if (!saved)
        {
            return;
        }

        SetPlotCutoff(dialog.CutoffTime);
    }

    private void TogglePlotLimitPicker()
    {
        _isPickingPlotCutoff = !_isPickingPlotCutoff;
        ApplyPlotLimitStateToControls();
        StatusText.Text = _isPickingPlotCutoff
            ? "Выберите начало ограничения кликом по графику"
            : "Ожидание";
    }

    private void OnPlotCutoffPicked(object? sender, DateTime cutoff)
    {
        SetPlotCutoff(cutoff);
    }

    private void SetPlotCutoff(DateTime? cutoff)
    {
        _plotCutoffTime = cutoff;
        _isPickingPlotCutoff = false;
        ApplyPlotLimitStateToControls();
        RefreshCurrentNode();
        StatusText.Text = cutoff is { } value
            ? $"График ограничен с {value:dd.MM.yyyy HH:mm:ss}"
            : "Ограничение графика сброшено";
    }

    private DateTime? SuggestedPlotCutoffTime()
    {
        if (_currentNode is { } nodeId &&
            _buffersByNode.TryGetValue(nodeId, out var buffer) &&
            buffer.Readings.Count > 0)
        {
            if (buffer.Latest is { } latest && _plotVisibleWindowSeconds > 0)
            {
                return latest.Timestamp.AddSeconds(-_plotVisibleWindowSeconds);
            }

            return buffer.Readings[0].Timestamp;
        }

        return DateTime.Now;
    }

    private void ApplyPlotLimitStateToControls()
    {
        if (PlotA is null || PlotB is null)
        {
            return;
        }

        PlotA.CutoffTime = _plotCutoffTime;
        PlotB.CutoffTime = _plotCutoffTime;
        PlotA.IsPickingCutoff = _isPickingPlotCutoff;
        PlotB.IsPickingCutoff = _isPickingPlotCutoff;

        if (LimitPlotButton is not null)
        {
            LimitPlotButton.Content = "С даты";
            ToolTip.SetTip(LimitPlotButton, _plotCutoffTime is { } cutoff
                ? $"Показываются данные с {cutoff:dd.MM.yyyy HH:mm:ss}"
                : "Показывать только данные после выбранной даты и времени");
        }

        if (PickPlotLimitButton is not null)
        {
            PickPlotLimitButton.Content = _isPickingPlotCutoff ? "Выбор..." : "Выбрать";
            ToolTip.SetTip(PickPlotLimitButton, _isPickingPlotCutoff
                ? "Режим выбора включён"
                : "Выбрать начало ограничения кликом по графику");
        }

        if (ClearPlotLimitButton is not null)
        {
            ClearPlotLimitButton.IsEnabled = _plotCutoffTime is not null;
        }

        if (RailCutoffText is not null)
        {
            RailCutoffText.Text = PlotCutoffShortLabel();
        }
    }

    private IEnumerable<Reading> ApplyPlotCutoff(IEnumerable<Reading> readings)
    {
        return _plotCutoffTime is { } cutoff
            ? readings.Where(x => x.Timestamp >= cutoff)
            : readings;
    }

    private bool IsBeforePlotCutoff(DateTime timestamp)
    {
        return _plotCutoffTime is { } cutoff && timestamp < cutoff;
    }

    private string PlotCutoffLabel()
    {
        return _plotCutoffTime is { } cutoff ? $" · с {cutoff:dd.MM HH:mm:ss}" : "";
    }

    private string PlotCutoffShortLabel()
    {
        return _plotCutoffTime is { } cutoff ? cutoff.ToString("dd.MM HH:mm:ss") : "-";
    }

    private static string PlotPointCountLabel(int visibleCount, int totalCount)
    {
        return visibleCount == totalCount
            ? $"{totalCount} точ."
            : $"{visibleCount}/{totalCount} точ.";
    }

    private static string FormatThreshold(double value)
    {
        return $"±{value:F2}°";
    }

    private void OnSetZeroClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int nodeId)
        {
            return;
        }

        if (!_buffersByNode.TryGetValue(nodeId, out var buffer) || buffer.Latest is not { } latest)
        {
            StatusText.Text = $"Узел {nodeId}: нет данных для установки нуля";
            return;
        }

        if (latest.AAxis is null && latest.BAxis is null)
        {
            StatusText.Text = $"Узел {nodeId}: последнее измерение без A/B";
            return;
        }

        _config.SetCalibration(nodeId, latest.AAxis, latest.BAxis);
        _config.Save(_configPath);

        if (_nodeItemsById.TryGetValue(nodeId, out var item))
        {
            item.UpdateFrom(buffer, null, _config.GetCalibration(nodeId));
        }

        if (_currentNode == nodeId)
        {
            RefreshCurrentNode();
        }

        StatusText.Text = $"Узел {nodeId}: ноль установлен (A={latest.AAxis:F3} B={latest.BAxis:F3})";
    }

    private void OnClearZeroClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int nodeId)
        {
            return;
        }

        _config.ClearCalibration(nodeId);
        _config.Save(_configPath);

        if (_nodeItemsById.TryGetValue(nodeId, out var item) && _buffersByNode.TryGetValue(nodeId, out var buffer))
        {
            item.UpdateFrom(buffer, null, null);
        }

        if (_currentNode == nodeId)
        {
            RefreshCurrentNode();
        }

        StatusText.Text = $"Узел {nodeId}: ноль стёрт";
    }
}
