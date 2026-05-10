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
    private CsvGatewaySource? _source;
    private PollingService? _poller;
    private DispatcherTimer? _heartbeat;
    private int? _currentNode;
    private bool _isPolling;
    private int _totalMessages;
    private DateTime? _nextPollAt;
    private double? _prevA;
    private double? _prevB;
    private double? _prevT;

    public MainWindow()
    {
        InitializeComponent();
        _configPath = AppConfig.ResolveDefaultPath();
        _config = AppConfig.Load(_configPath);

        NodeList.ItemsSource = _nodes;
        RowsList.ItemsSource = _rows;

        LoadConfigToUi();
        ReconfigureTelegramAlerts();
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
        DiscoverButton.Click += async (_, _) => await DiscoverNodesAsync();
        LoadSampleButton.Click += async (_, _) => await LoadCsvAsync();
        ExportButton.Click += async (_, _) => await ExportCurrentNodeAsync();
        AddNodeButton.Click += AddNode;
        AddNodeButton2.Click += AddNode;
        AcknowledgeButton.Click += (_, _) => AlarmBanner.IsVisible = false;
        MuteButton.Click += (_, _) => AlarmBanner.IsVisible = false;
        NodeList.SelectionChanged += (_, _) =>
        {
            if (NodeList.SelectedItem is NodeListItem item)
            {
                _currentNode = item.NodeId;
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
                    node.UpdateFrom(buf);
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
        }

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
        _poller.Error += (nodeId, message) => Dispatcher.UIThread.Post(() => StatusText.Text = $"Node {nodeId}: {message}");
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
            StatusText.Text = "Idle";
        }

        if (ConnectionLabel is not null)
        {
            ConnectionLabel.Text = "Idle";
        }

        SetStatusDot("#768390");
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
                StatusText.Text = $"Node {node.NodeId}: {e.Message}";
            }
        }
    }

    private async Task ShowSettingsDialogAsync()
    {
        await StopTelegramAlertsAsync();
        var dialog = new SettingsDialog();
        dialog.LoadConfig(_config);

        try
        {
            var saved = await dialog.ShowDialog<bool>(this);
            if (saved)
            {
                _config.Save(_configPath);
                LoadConfigToUi();
                RefreshCurrentNode();
            }
        }
        finally
        {
            ReconfigureTelegramAlerts();
        }
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
            StatusText.Text = "Discovering…";
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

            StatusText.Text = found.Count == 0 ? "No live nodes discovered" : $"Discovered {found.Count} live node(s)";
            SaveUiToConfig();
            UpdateNodeSummary();
        }
        catch (Exception e)
        {
            StatusText.Text = $"Discover failed: {e.Message}";
        }
    }

    private async Task LoadCsvAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open readings CSV",
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
            StatusText.Text = "CSV has no Node ID";
            return;
        }

        OnReadingsReady(nodeId, NodeReadings.FromParsedCsv(nodeId, parsed));
        StatusText.Text = $"Loaded CSV — Node {nodeId}";
    }

    private async Task ExportCurrentNodeAsync()
    {
        if (_currentNode is not { } nodeId || !_buffersByNode.TryGetValue(nodeId, out var buffer) || buffer.Count == 0)
        {
            StatusText.Text = "No current node data to export";
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export current node CSV",
            SuggestedFileName = $"Node-{nodeId}-readings.csv",
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

        ReadingsCsvExporter.Export(file.Path.LocalPath, buffer.Readings, _config.Thresholds, _config.Alarm);
        StatusText.Text = $"Exported Node {nodeId}";
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
        item.UpdateFrom(buffer, nodeReadings.Model);

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
        if (latest != null && !latest.Invalid && _telegramAlertService != null)
        {
            var isACritical = latest.AAxis != null && Math.Abs(latest.AAxis.Value) >= _config.Thresholds.CriticalA;
            var isBCritical = latest.BAxis != null && Math.Abs(latest.BAxis.Value) >= _config.Thresholds.CriticalB;
            
            _ = _telegramAlertService.UpdateAlarmAsync(nodeId, "A", isACritical, latest.AAxis ?? 0, latest.Timestamp);
            _ = _telegramAlertService.UpdateAlarmAsync(nodeId, "B", isBCritical, latest.BAxis ?? 0, latest.Timestamp);
        }
    }

    private void RefreshCurrentNode(Reading? previousLatest = null)
    {
        if (_currentNode is not { } nodeId || !_buffersByNode.TryGetValue(nodeId, out var buffer))
        {
            Plot.Readings = [];
            _rows.Clear();
            HeroTempText.Text = "-";
            HeroTempDeltaText.Text = "";
            GaugeA.IsInvalid = true;
            GaugeB.IsInvalid = true;
            PlotMetaText.Text = "";
            RowsMetaText.Text = "";
            return;
        }

        var readings = buffer.Readings;
        Plot.Readings = readings.ToList();
        Plot.ExpectedIntervalSeconds = buffer.EstimatedSamplingIntervalSeconds;
        Plot.WarningThreshold = _config.Thresholds.WarningA;
        Plot.CriticalThreshold = _config.Thresholds.CriticalA;

        _rows.Clear();
        foreach (var row in readings.TakeLast(200).Reverse().Select(x => new ReadingRow(x)))
        {
            _rows.Add(row);
        }

        var latest = buffer.Latest;

        // Temperature hero tile
        if (latest?.Temperature is { } t)
        {
            HeroTempText.Text = t.ToString("F1");
            if (_prevT is { } pt)
            {
                var delta = t - pt;
                HeroTempDeltaText.Text = delta >= 0 ? $"+{delta:F2}" : $"{delta:F2}";
            }
        }
        else
        {
            HeroTempText.Text = "-";
            HeroTempDeltaText.Text = "";
        }

        // A-axis gauge
        var prevA = previousLatest?.Invalid == false ? previousLatest.AAxis : null;
        if (latest?.Invalid == true || latest?.AAxis is null)
        {
            GaugeA.IsInvalid = true;
        }
        else
        {
            GaugeA.IsInvalid = false;
            GaugeA.Value = latest.AAxis.Value;
            GaugeA.PreviousValue = prevA ?? _prevA;
            GaugeA.IsCritical = Math.Abs(latest.AAxis.Value) >= _config.Thresholds.CriticalA;
            GaugeA.WarningThreshold = _config.Thresholds.WarningA;
            GaugeA.CriticalThreshold = _config.Thresholds.CriticalA;
        }

        // B-axis gauge
        var prevB = previousLatest?.Invalid == false ? previousLatest.BAxis : null;
        if (latest?.Invalid == true || latest?.BAxis is null)
        {
            GaugeB.IsInvalid = true;
        }
        else
        {
            GaugeB.IsInvalid = false;
            GaugeB.Value = latest.BAxis.Value;
            GaugeB.PreviousValue = prevB ?? _prevB;
            GaugeB.IsCritical = Math.Abs(latest.BAxis.Value) >= _config.Thresholds.CriticalB;
            GaugeB.WarningThreshold = _config.Thresholds.WarningB;
            GaugeB.CriticalThreshold = _config.Thresholds.CriticalB;
        }

        if (_config.Alarm.Enabled && (GaugeA.IsCritical || GaugeB.IsCritical))
        {
            AlarmBanner.IsVisible = true;
            AlarmBannerText.Text = $"CRITICAL ALARM on Node {nodeId}";
        }

        // Remember previous valid values for delta
        if (latest is not null && !latest.Invalid)
        {
            _prevA = latest.AAxis;
            _prevB = latest.BAxis;
            _prevT = latest.Temperature;
        }

        PlotMetaText.Text = $"{readings.Count} pts";
        RowsMetaText.Text = $"{Math.Min(200, readings.Count)} rows";

        ExportButton.IsEnabled = _currentNode is not null;
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
            SamplingRateLabel.Text = $"~{(int)interval}s interval";
        }

        var latest = buffer.Latest;
        if (latest is not null)
        {
            LastSampleLabel.Text = ReadingSnapshot.FormatTimestamp(latest.Timestamp);
        }
    }

    private void RefreshCountdown()
    {
        if (!_isPolling || _nextPollAt is null)
        {
            CountdownLabel.Text = _totalMessages > 0 ? $"{_totalMessages} msgs" : "";
            return;
        }

        var remaining = _nextPollAt.Value - DateTime.Now;
        var secs = Math.Max(0, (int)remaining.TotalSeconds);
        CountdownLabel.Text = $"Next poll {secs}s  •  {_totalMessages} msgs";
    }

    private void SetConnectionState(bool ok, string message)
    {
        if (ok)
        {
            ConnectionLabel.Text = "Connected";
            GatewayLabel.Text = message;
            SetStatusDot("#238636");
        }
        else
        {
            ConnectionLabel.Text = "Disconnected";
            StatusText.Text = message;
            SetStatusDot("#f85149");
        }
    }

    private void SetStatusDot(string color)
    {
        StatusDot.Fill = new SolidColorBrush(Color.Parse(color));
    }

    private void UpdateNodeSummary()
    {
        NodeSummaryText.Text = _nodes.Count == 1 ? "1 node" : $"{_nodes.Count} nodes";
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
}
