using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;
using RailRouteHelper.AssistantSessions;
using RailRouteHelper.Protocol;

namespace RailRouteAssistantDesktop;

public partial class MainForm
{
    private TabControl _workspaceTabs;
    private ListView _alertCenterList;
    private TimetableGraphControl _timetableGraph;
    private ComboBox _graphTrainSelector;
    private Label _sessionStatusLabel;
    private Button _sessionStartButton;
    private Button _sessionStopButton;
    private CheckBox _autoRecordCheck;
    private Button _replayOpenButton;
    private Button _replayPlayButton;
    private Button _replayFrameButton;
    private Button _replayStopButton;
    private ComboBox _replaySpeedCombo;
    private ComboBox _replayAlertCombo;
    private TrackBar _replayTimeline;
    private Label _replayModeLabel;
    private readonly AssistantSessionAdapter _sessionAdapter = new();
    private AlertCenterProjector _alertProjector = new();
    private SessionRecorder _sessionRecorder;
    private string _sessionId;
    private long _sessionSequence;
    private long _sessionFrameCount;
    private long _frameSequence;
    private DateTimeOffset? _connectionLostSinceUtc;
    private bool _replayMode;
    private readonly List<AssistantFrame> _replayFrames = new();
    private readonly List<AssistantFrame> _liveGraphFrames = new();
    private readonly Dictionary<string, string> _recordedDefinitionHashes = new(StringComparer.Ordinal);
    private readonly List<RealtimeEnvelope> _replayEnvelopes = new();
    private readonly List<int> _replayFrameEnvelopeIndexes = new();
    private int _replayIndex = -1;
    private AssistantFrame _lastSessionFrame;
    private TimetableGraphProjector _graphProjector;
    private string _graphProjectorTrain;
    private System.Windows.Forms.Timer _replayTimer;

    private void BuildWorkspaceTabs()
    {
        var legacyControls = Controls.Cast<Control>()
            .Where(control => control != _statusLabel && control != MainMenuStrip)
            .ToList();
        var realtimePage = new TabPage("实时调度") { BackColor = ColorBg, Padding = new Padding(0) };
        var realtimePanel = new Panel { Dock = DockStyle.Fill, BackColor = ColorBg };
        foreach (var control in legacyControls)
        {
            Controls.Remove(control);
            realtimePanel.Controls.Add(control);
        }
        realtimePage.Controls.Add(realtimePanel);

        _workspaceTabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Appearance = TabAppearance.Normal,
            BackColor = ColorBg,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 9F)
        };
        _workspaceTabs.TabPages.Add(realtimePage);
        _workspaceTabs.TabPages.Add(BuildAlertCenterPage());
        _workspaceTabs.TabPages.Add(BuildTimetablePage());
        _workspaceTabs.TabPages.Add(BuildSessionReplayPage());
        Controls.Add(_workspaceTabs);

        // Keep the menu and status bar above the tab workspace regardless of
        // WinForms' reverse z-order docking rules.
        Controls.SetChildIndex(_workspaceTabs, 0);
        if (_statusLabel.Parent == this)
            Controls.SetChildIndex(_statusLabel, Controls.Count - 1);
        if (MainMenuStrip != null && MainMenuStrip.Parent == this)
            Controls.SetChildIndex(MainMenuStrip, Controls.Count - 1);

        _replayTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _replayTimer.Tick += (_, _) => AdvanceReplay();
        ConfigureReplaySpeed();
    }

    private TabPage BuildAlertCenterPage()
    {
        var page = new TabPage("告警中心") { BackColor = ColorBg, Padding = new Padding(6) };
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 35,
            BackColor = ColorPanel,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4, 4, 4, 2)
        };
        var acknowledge = CreateButton("确认", 58);
        acknowledge.Click += (_, _) => ApplySelectedAlertAction(AlertActionKind.Acknowledge, null);
        toolbar.Controls.Add(acknowledge);
        var restore = CreateButton("恢复提示", 72);
        restore.Click += (_, _) => RestoreSelectedAlert();
        toolbar.Controls.Add(restore);
        foreach (var minutes in new[] { 1, 5, 10 })
        {
            var snooze = CreateButton($"静音{minutes}分", 72);
            int captured = minutes;
            snooze.Click += (_, _) => ApplySelectedAlertAction(AlertActionKind.Snooze, DateTimeOffset.UtcNow.AddMinutes(captured));
            toolbar.Controls.Add(snooze);
        }
        var hint = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(165, 180, 188),
            Text = "  已恢复告警保留在列表中；双击或选择行可定位列车/运行图",
            Padding = new Padding(8, 4, 0, 0)
        };
        toolbar.Controls.Add(hint);

        _alertCenterList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            BackColor = ColorBg,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 8.5F)
        };
        foreach (var column in new[] { "状态", "级别", "车次", "摘要", "首次", "持续", "次数", "最后" })
            _alertCenterList.Columns.Add(column, column == "摘要" ? 320 : column == "状态" ? 76 : 95);
        _alertCenterList.SelectedIndexChanged += (_, _) => SelectAlertTrain();
        _alertCenterList.ItemActivate += (_, _) => SelectAlertTrain();
        page.Controls.Add(_alertCenterList);
        page.Controls.Add(toolbar);
        return page;
    }

    private TabPage BuildTimetablePage()
    {
        var page = new TabPage("时距运行图") { BackColor = ColorBg, Padding = new Padding(5) };
        var toolbar = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = ColorPanel };
        var label = new Label { Text = "基准列车：", ForeColor = Color.LightGray, AutoSize = true, Left = 8, Top = 8 };
        _graphTrainSelector = new ComboBox
        {
            Left = 72,
            Top = 4,
            Width = 150,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(45, 45, 52),
            ForeColor = Color.White
        };
        _graphTrainSelector.SelectedIndexChanged += (_, _) =>
        {
            if (_graphTrainSelector.SelectedItem is string name)
                _timetableGraph.SetReferenceTrain(name);
        };
        var reset = CreateButton("重置视图", 75);
        reset.Left = 232;
        reset.Top = 3;
        reset.Click += (_, _) => _timetableGraph.ResetView();
        var note = new Label
        {
            Text = "滚轮缩放，中键/左键拖动；相对时刻不推断绝对位置",
            AutoSize = true,
            ForeColor = Color.FromArgb(165, 180, 188),
            Left = 320,
            Top = 8
        };
        toolbar.Controls.Add(label);
        toolbar.Controls.Add(_graphTrainSelector);
        toolbar.Controls.Add(reset);
        toolbar.Controls.Add(note);

        _timetableGraph = new TimetableGraphControl { Dock = DockStyle.Fill };
        _timetableGraph.TrainSelected += (_, name) =>
        {
            var displayName = _trains.FirstOrDefault(item => string.Equals(item.Id, name, StringComparison.OrdinalIgnoreCase))?.Name ?? name;
            SelectTrainInList(displayName);
            if (_graphTrainSelector != null)
            {
                var item = _graphTrainSelector.Items.Cast<string>().FirstOrDefault(value => string.Equals(value, displayName, StringComparison.OrdinalIgnoreCase));
                if (item != null) _graphTrainSelector.SelectedItem = item;
            }
        };
        page.Controls.Add(_timetableGraph);
        page.Controls.Add(toolbar);
        return page;
    }

    private TabPage BuildSessionReplayPage()
    {
        var page = new TabPage("会话回放") { BackColor = ColorBg, Padding = new Padding(6) };
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 70,
            BackColor = ColorPanel,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(4)
        };
        _sessionStartButton = CreateButton("开始记录", 78);
        _sessionStopButton = CreateButton("停止记录", 78);
        _autoRecordCheck = new CheckBox { Text = "gameReady 自动记录", AutoSize = true, ForeColor = Color.LightGray, Margin = new Padding(7, 7, 7, 3) };
        _sessionStatusLabel = new Label { Text = "未记录", ForeColor = Color.DarkGray, AutoSize = true, Margin = new Padding(8, 7, 5, 3) };
        _sessionStartButton.Click += (_, _) => StartRecordingWithDialog();
        _sessionStopButton.Click += (_, _) => StopRecording("用户停止");
        _sessionStopButton.Enabled = false;
        toolbar.Controls.Add(_sessionStartButton);
        toolbar.Controls.Add(_sessionStopButton);
        toolbar.Controls.Add(_autoRecordCheck);
        toolbar.Controls.Add(_sessionStatusLabel);

        _replayOpenButton = CreateButton("打开文件", 72);
        _replayPlayButton = CreateButton("播放", 58);
        _replayFrameButton = CreateButton("单帧", 52);
        _replayStopButton = CreateButton("退出回放", 76);
        _replaySpeedCombo = new ComboBox { Width = 70, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(45, 45, 52), ForeColor = Color.White };
        _replaySpeedCombo.Items.AddRange(new object[] { "0.5x", "1x", "2x", "5x", "10x" });
        _replaySpeedCombo.SelectedIndex = 1;
        _replayAlertCombo = new ComboBox { Width = 210, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(45, 45, 52), ForeColor = Color.White };
        _replayAlertCombo.Items.Add("按告警跳转");
        _replayAlertCombo.SelectedIndex = 0;
        _replayTimeline = new TrackBar { Width = 360, Minimum = 0, Maximum = 0, TickStyle = TickStyle.None, Enabled = false };
        _replayModeLabel = new Label { Text = "实时模式（回放默认关闭语音）", ForeColor = Color.Gray, AutoSize = true, Margin = new Padding(8, 7, 5, 3) };
        _replayOpenButton.Click += (_, _) => OpenReplayFile();
        _replayPlayButton.Click += (_, _) => ToggleReplay();
        _replayFrameButton.Click += (_, _) => StepReplay();
        _replayStopButton.Click += (_, _) => StopReplay();
        _replayTimeline.Scroll += (_, _) => ApplyReplayIndex(_replayTimeline.Value);
        _replayAlertCombo.SelectedIndexChanged += (_, _) => JumpToReplayAlert();
        _replaySpeedCombo.SelectedIndexChanged += (_, _) => ConfigureReplaySpeed();
        toolbar.Controls.Add(_replayOpenButton);
        toolbar.Controls.Add(_replayPlayButton);
        toolbar.Controls.Add(_replayFrameButton);
        toolbar.Controls.Add(_replayStopButton);
        toolbar.Controls.Add(new Label { Text = "速度", ForeColor = Color.LightGray, AutoSize = true, Margin = new Padding(8, 7, 2, 3) });
        toolbar.Controls.Add(_replaySpeedCombo);
        toolbar.Controls.Add(_replayTimeline);
        toolbar.Controls.Add(_replayAlertCombo);
        toolbar.Controls.Add(_replayModeLabel);

        page.Controls.Add(toolbar);
        var help = new Label
        {
            Dock = DockStyle.Fill,
            Text = "打开 AssistantSessions JSONL 后可按时间播放、暂停、单帧和按告警跳转。\r\n回放模式明确禁止语音播报，退出回放后恢复实时轮询。",
            ForeColor = Color.FromArgb(175, 190, 198),
            Padding = new Padding(18),
            Font = new Font("Microsoft YaHei UI", 10F)
        };
        page.Controls.Add(help);
        return page;
    }

    private static Button CreateButton(string text, int width)
        => new Button { Text = text, Width = width, Height = 25, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(55, 65, 75), Margin = new Padding(3, 1, 3, 1) };

    private void UpdateWorkspacePanels()
    {
        if (_alertCenterList == null) return;
        RenderAlertCenter();
        var selectedGraphTrain = _graphTrainSelector?.SelectedItem as string;
        if (_lastSessionFrame != null && !string.IsNullOrWhiteSpace(selectedGraphTrain))
        {
            var selectedDto = _trains.FirstOrDefault(item => string.Equals(item.Name, selectedGraphTrain, StringComparison.OrdinalIgnoreCase))
                ?? _trains.FirstOrDefault(item => string.Equals(item.Id, selectedGraphTrain, StringComparison.OrdinalIgnoreCase));
            var selectedTrainId = selectedDto?.Id;
            if (string.IsNullOrWhiteSpace(selectedTrainId)) selectedTrainId = selectedGraphTrain;
            _graphProjectorTrain = selectedTrainId;
            _graphProjector = new TimetableGraphProjector(selectedTrainId);
            var graphFrames = _replayMode
                ? _replayFrames.Take(Math.Max(0, _replayIndex + 1))
                : _liveGraphFrames;
            foreach (var graphFrame in graphFrames)
                _graphProjector.Apply(graphFrame);
            var coreSnapshot = _graphProjector.Snapshot;
            _timetableGraph?.SetCoreData(coreSnapshot, _gameTime, _lastSessionFrame.GameTimeSeconds, NormalizeGraphAlerts());
        }
        else
        {
            _timetableGraph?.SetData(_trains, _gameTime, _alerts);
        }
        if (_graphTrainSelector != null)
        {
            string selected = _graphTrainSelector.SelectedItem as string;
            var names = _trains.Select(t => t.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _graphTrainSelector.BeginUpdate();
            _graphTrainSelector.Items.Clear();
            foreach (var name in names) _graphTrainSelector.Items.Add(name);
            if (!string.IsNullOrWhiteSpace(selected) && names.Contains(selected, StringComparer.OrdinalIgnoreCase))
                _graphTrainSelector.SelectedItem = names.First(n => string.Equals(n, selected, StringComparison.OrdinalIgnoreCase));
            else if (_graphTrainSelector.Items.Count > 0 && _graphTrainSelector.SelectedIndex < 0)
                _graphTrainSelector.SelectedIndex = 0;
            _graphTrainSelector.EndUpdate();
        }
    }

    private IEnumerable<AlertData> NormalizeGraphAlerts()
    {
        foreach (var alert in _alerts)
        {
            var names = (alert.TrainName ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var ids = names.Select(name => _trains.FirstOrDefault(train => string.Equals(train.Name, name, StringComparison.OrdinalIgnoreCase))?.Id ?? name);
            yield return new AlertData { TrainName = string.Join('/', ids) };
        }
    }

    private void RenderAlertCenter()
    {
        var snapshot = _alertProjector.Snapshot;
        _alertCenterList.BeginUpdate();
        _alertCenterList.Items.Clear();
        foreach (var occurrence in snapshot.Alerts
                     .OrderByDescending(a => a.Lifecycle == AlertLifecycleState.Active)
                     .ThenByDescending(a => a.Observation.Severity)
                     .ThenByDescending(a => a.LastSeenAtUtc))
        {
            var observation = occurrence.Observation;
            string status = occurrence.Lifecycle switch
            {
                AlertLifecycleState.Resolved => "已恢复",
                AlertLifecycleState.Stale => "暂失联",
                _ => occurrence.UserState == AlertUserState.Acknowledged ? "已确认" : occurrence.UserState == AlertUserState.Snoozed ? "已静音" : "活动"
            };
            string severity = observation.Severity switch
            {
                AlertSeverity.Critical => "紧急",
                AlertSeverity.Warning => "警告",
                _ => "信息"
            };
            string train = observation.SubjectDisplayName ?? observation.SubjectId ?? string.Empty;
            string summary = observation.Detail ?? observation.Title ?? observation.Code;
            string duration = occurrence.LastSeenAtUtc > occurrence.FirstSeenAtUtc
                ? (occurrence.LastSeenAtUtc - occurrence.FirstSeenAtUtc).ToString(@"dd\.hh\:mm\:ss")
                : "0秒";
            int count = occurrence.ObservationCount;
            var item = new ListViewItem(status);
            item.SubItems.Add(severity);
            item.SubItems.Add(train);
            item.SubItems.Add(summary);
            item.SubItems.Add(occurrence.FirstSeenAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss"));
            item.SubItems.Add(duration);
            item.SubItems.Add(count.ToString());
            item.SubItems.Add(occurrence.LastSeenAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss"));
            item.Tag = occurrence;
            item.ForeColor = occurrence.Lifecycle == AlertLifecycleState.Resolved ? Color.DarkGray : severity == "紧急" ? ColorCritical : severity == "警告" ? ColorWarning : ColorInfo;
            _alertCenterList.Items.Add(item);
        }
        if (_alertCenterList.Items.Count == 0)
            _alertCenterList.Items.Add(new ListViewItem("暂无告警") { ForeColor = ColorDim });
        _alertCenterList.EndUpdate();
    }

    private void SelectAlertTrain()
    {
        if (_alertCenterList?.SelectedItems.Count != 1) return;
        if (_alertCenterList.SelectedItems[0].Tag is not AlertOccurrence occurrence) return;
        var stableId = occurrence.Observation.SubjectId;
        var train = _trains.FirstOrDefault(item => !string.IsNullOrWhiteSpace(stableId) &&
            string.Equals(item.Id, stableId, StringComparison.OrdinalIgnoreCase))?.Name
            ?? occurrence.Observation.SubjectDisplayName
            ?? stableId;
        if (!string.IsNullOrWhiteSpace(train))
        {
            SelectTrainInList(train);
            _timetableGraph?.SetReferenceTrain(train.Split('/')[0].Trim());
            if (_graphTrainSelector != null)
            {
                var item = _graphTrainSelector.Items.Cast<string>().FirstOrDefault(n => string.Equals(n, train.Split('/')[0].Trim(), StringComparison.OrdinalIgnoreCase));
                if (item != null) _graphTrainSelector.SelectedItem = item;
            }
        }
    }

    private void ApplySelectedAlertAction(AlertActionKind kind, DateTimeOffset? snoozeUntil)
    {
        if (_alertCenterList?.SelectedItems.Count != 1 || _alertCenterList.SelectedItems[0].Tag is not AlertOccurrence occurrence)
            return;
        var action = new AlertAction(occurrence.AlertId, kind, DateTimeOffset.UtcNow, snoozeUntil);
        _alertProjector.ApplyAction(action);
        if (_sessionRecorder != null)
        {
            var envelope = AssistantSessionProtocol.CreateAlertActionEnvelope(++_sessionSequence, action.OccurredAtUtc, action);
            _sessionRecorder.Append(envelope);
        }
        RenderAlertCenter();
    }

    private void RestoreSelectedAlert()
    {
        if (_alertCenterList?.SelectedItems.Count != 1 || _alertCenterList.SelectedItems[0].Tag is not AlertOccurrence occurrence)
            return;
        var action = occurrence.UserState == AlertUserState.Snoozed
            ? AlertActionKind.Unsnooze
            : AlertActionKind.MarkUnseen;
        ApplySelectedAlertAction(action, null);
    }

    private void RecordLiveSnapshot(AssistantSnapshot snapshot)
    {
        var captured = DateTimeOffset.UtcNow;
        _connectionLostSinceUtc = null;
        if (_autoRecordCheck?.Checked == true && snapshot.GameReady && _sessionRecorder == null)
            StartRecording(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RailRouteAssistant", "sessions", $"session-{captured:yyyyMMdd-HHmmss}.jsonl"));

        var frame = _sessionAdapter.ToFrame(snapshot, ++_frameSequence, captured);
        _lastSessionFrame = frame;
        _liveGraphFrames.Add(frame);
        if (_liveGraphFrames.Count > 7200) _liveGraphFrames.RemoveAt(0);
        _alertProjector.Apply(frame);
        if (_sessionRecorder != null)
            RecordSessionFrame(frame, captured);
    }

    private void RecordConnectionFailure()
    {
        var now = DateTimeOffset.UtcNow;
        _connectionLostSinceUtc ??= now;
        var failed = new AssistantSnapshot
        {
            GameReady = false,
            GameTime = _gameTime,
            GameTimeSeconds = TimeSpan.TryParse(_gameTime, out var clock) ? clock.TotalSeconds : null,
            Trains = _trains.ToList(),
            Alerts = _alerts.ToList()
        };
        var frame = _sessionAdapter.ToFrame(failed, ++_frameSequence, now, isConnected: false, isSuccessful: false);
        _lastSessionFrame = frame;
        _liveGraphFrames.Add(frame);
        if (_liveGraphFrames.Count > 7200) _liveGraphFrames.RemoveAt(0);
        _alertProjector.Apply(frame);
        if (_sessionRecorder != null) RecordSessionFrame(frame, now);
        UpdateWorkspacePanels();
        if (_autoRecordCheck?.Checked == true && _sessionRecorder != null && now - _connectionLostSinceUtc.Value >= TimeSpan.FromSeconds(60))
            StopRecording("游戏断开");
    }

    private void StartRecordingWithDialog()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "Assistant session (*.jsonl)|*.jsonl|All files (*.*)|*.*",
            FileName = $"session-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RailRouteAssistant")
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            StartRecording(dialog.FileName);
    }

    private void StartRecording(string path)
    {
        if (_sessionRecorder != null) return;
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                throw new IOException("目标文件已有会话内容，请选择新文件以保持 JSONL 序列连续。");
            _sessionId = $"desktop-{Guid.NewGuid():N}";
            _sessionSequence = 0;
            _sessionFrameCount = 0;
            _frameSequence = 0;
            _recordedDefinitionHashes.Clear();
            _sessionRecorder = new SessionRecorder(path, flushToDisk: false);
            var start = new SessionStart(_sessionId, DateTimeOffset.UtcNow, "desktop", _graphTrainSelector?.SelectedItem as string);
            _sessionRecorder.Append(AssistantSessionProtocol.CreateSessionStartEnvelope(_sessionSequence, start.StartedAtUtc, start));
            _sessionStatusLabel.Text = $"记录中：{Path.GetFileName(path)}";
            _sessionStatusLabel.ForeColor = Color.LightGreen;
            _sessionStartButton.Enabled = false;
            _sessionStopButton.Enabled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法开始记录：{ex.Message}", "会话记录", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RecordSessionFrame(AssistantFrame frame, DateTimeOffset capturedAtUtc)
    {
        if (_sessionRecorder == null) return;
        foreach (var definition in frame.Trains)
        {
            string hash = DefinitionHash(definition);
            if (_recordedDefinitionHashes.TryGetValue(definition.TrainId, out var previous) && previous == hash)
                continue;
            _recordedDefinitionHashes[definition.TrainId] = hash;
            _sessionRecorder.Append(AssistantSessionProtocol.CreateTrainUpsertEnvelope(++_sessionSequence, capturedAtUtc, definition));
        }
        var compact = frame with { Trains = Array.Empty<TrainDefinition>() };
        _sessionRecorder.Append(AssistantSessionProtocol.CreateFrameEnvelope(++_sessionSequence, capturedAtUtc, compact));
        _sessionFrameCount++;
    }

    private static string DefinitionHash(TrainDefinition definition)
    {
        var json = JsonSerializer.Serialize(definition, DefinitionHashJsonOptions);
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    private static readonly JsonSerializerOptions DefinitionHashJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private void StopRecording(string reason)
    {
        if (_sessionRecorder == null) return;
        try
        {
            var end = new SessionEnd(_sessionId, DateTimeOffset.UtcNow, reason, _sessionFrameCount);
            _sessionRecorder.Append(AssistantSessionProtocol.CreateSessionEndEnvelope(++_sessionSequence, end.EndedAtUtc, end));
            _sessionRecorder.Dispose();
        }
        catch { _sessionRecorder.Dispose(); }
        finally
        {
            _sessionRecorder = null;
            if (_sessionStatusLabel != null) { _sessionStatusLabel.Text = "未记录"; _sessionStatusLabel.ForeColor = Color.DarkGray; }
            if (_sessionStartButton != null) _sessionStartButton.Enabled = true;
            if (_sessionStopButton != null) _sessionStopButton.Enabled = false;
        }
    }

    private void OpenReplayFile()
    {
        using var dialog = new OpenFileDialog { Filter = "Assistant session (*.jsonl)|*.jsonl|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var stream = File.OpenRead(dialog.FileName);
            var envelopes = new SessionReplayReader(tolerateTrailingIncompleteLine: true).ReadAll(stream);
            _replayEnvelopes.Clear();
            _replayEnvelopes.AddRange(envelopes);
            _replayFrames.Clear();
            _replayFrameEnvelopeIndexes.Clear();
            var definitions = new Dictionary<string, TrainDefinition>(StringComparer.Ordinal);
            for (int envelopeIndex = 0; envelopeIndex < envelopes.Count; envelopeIndex++)
            {
                var envelope = envelopes[envelopeIndex];
                if (envelope.MessageType == AssistantSessionMessageTypes.TrainUpsert)
                {
                    var definition = AssistantSessionProtocol.DecodeTrainUpsert(envelope);
                    definitions[definition.TrainId] = definition;
                }
                else if (envelope.MessageType == AssistantSessionMessageTypes.Frame)
                {
                    var decoded = AssistantSessionProtocol.DecodeFrame(envelope);
                    foreach (var definition in decoded.Trains)
                        definitions[definition.TrainId] = definition;
                    if (decoded.TrainStates.Count > 0)
                    {
                        var activeIds = decoded.TrainStates.Select(state => state.TrainId).ToHashSet(StringComparer.Ordinal);
                        foreach (var staleId in definitions.Keys.Where(id => !activeIds.Contains(id)).ToArray())
                            definitions.Remove(staleId);
                    }
                    _replayFrames.Add(decoded with { Trains = definitions.Values.ToArray() });
                    _replayFrameEnvelopeIndexes.Add(envelopeIndex);
                }
            }
            if (_replayFrames.Count == 0) throw new InvalidDataException("文件中没有 assistant-frame 记录。");
            _replayIndex = 0;
            _replayTimeline.Maximum = Math.Max(0, _replayFrames.Count - 1);
            _replayTimeline.Enabled = true;
            _replayAlertCombo.Items.Clear();
            _replayAlertCombo.Items.Add("按告警跳转");
            foreach (var tuple in _replayFrames.SelectMany((frame, index) => frame.ObservedAlerts.Select(alert => (index, alert))))
                _replayAlertCombo.Items.Add($"#{tuple.index + 1} {tuple.alert.Code} {tuple.alert.SubjectId}");
            _replayAlertCombo.SelectedIndex = 0;
            _replayMode = true;
            _alertProjector = new AlertCenterProjector();
            _graphProjector = null;
            _graphProjectorTrain = null;
            _replayModeLabel.Text = "回放模式 · 语音已关闭";
            _replayModeLabel.ForeColor = Color.Orange;
            ApplyReplayIndex(0);
            ConfigureReplayInterval();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开回放：{ex.Message}", "会话回放", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ToggleReplay()
    {
        if (!_replayMode || _replayFrames.Count == 0) return;
        if (_replayTimer.Enabled)
        {
            _replayTimer.Stop();
            _replayPlayButton.Text = "播放";
        }
        else
        {
            ConfigureReplayInterval();
            _replayTimer.Start();
            _replayPlayButton.Text = "暂停";
        }
    }

    private void StepReplay()
    {
        if (!_replayMode || _replayFrames.Count == 0) return;
        _replayTimer.Stop();
        _replayPlayButton.Text = "播放";
        ApplyReplayIndex(Math.Min(_replayFrames.Count - 1, _replayIndex + 1));
    }

    private void AdvanceReplay()
    {
        int next = _replayIndex + 1;
        if (next >= _replayFrames.Count)
        {
            _replayTimer.Stop();
            _replayPlayButton.Text = "播放";
            next = _replayFrames.Count - 1;
        }
        ApplyReplayIndex(next);
        ConfigureReplayInterval();
    }

    private void ApplyReplayIndex(int index)
    {
        if (!_replayMode || index < 0 || index >= _replayFrames.Count) return;
        _replayIndex = index;
        var frame = _replayFrames[index];
        _gameReady = frame.GameReady;
        _gameTime = frame.GameTimeSeconds.HasValue
            ? TimeSpan.FromSeconds(Math.Max(0, frame.GameTimeSeconds.Value)).ToString(@"hh\:mm\:ss")
            : frame.CapturedAtUtc.LocalDateTime.ToString("HH:mm:ss");
        _trains = _sessionAdapter.FromFrame(frame).ToList();
        _alerts = _sessionAdapter.AlertsFromFrame(frame).ToList();
        _lastSessionFrame = frame;
        // Rebuild the core lifecycle projector from the beginning of the
        // replay slice. This avoids mixing live state when a user drags
        // backwards, and also replays acknowledgement/snooze actions.
        _alertProjector = new AlertCenterProjector();
        int targetEnvelope = _replayFrameEnvelopeIndexes[index];
        for (int envelopeIndex = 0; envelopeIndex <= targetEnvelope; envelopeIndex++)
        {
            var envelope = _replayEnvelopes[envelopeIndex];
            if (envelope.MessageType == AssistantSessionMessageTypes.Frame)
                _alertProjector.Apply(AssistantSessionProtocol.DecodeFrame(envelope));
            else if (envelope.MessageType == AssistantSessionMessageTypes.AlertAction)
                _alertProjector.ApplyAction(AssistantSessionProtocol.DecodeAlertAction(envelope));
        }
        _replayTimeline.Value = Math.Min(_replayTimeline.Maximum, index);
        UpdateUI();
    }

    private void JumpToReplayAlert()
    {
        if (!_replayMode || _replayAlertCombo.SelectedIndex <= 0) return;
        int target = _replayAlertCombo.SelectedIndex - 1;
        int seen = 0;
        for (int i = 0; i < _replayFrames.Count; i++)
        {
            seen += _replayFrames[i].ObservedAlerts.Count;
            if (seen > target) { ApplyReplayIndex(i); break; }
        }
    }

    private void StopReplay()
    {
        _replayTimer?.Stop();
        _replayMode = false;
        _alertProjector = new AlertCenterProjector();
        _graphProjector = null;
        _graphProjectorTrain = null;
        _replayFrames.Clear();
        _replayEnvelopes.Clear();
        _replayFrameEnvelopeIndexes.Clear();
        _replayIndex = -1;
        if (_replayTimeline != null) { _replayTimeline.Enabled = false; _replayTimeline.Value = 0; }
        if (_replayPlayButton != null) _replayPlayButton.Text = "播放";
        if (_replayModeLabel != null) { _replayModeLabel.Text = "实时模式（回放默认关闭语音）"; _replayModeLabel.ForeColor = Color.Gray; }
    }

    private void ConfigureReplaySpeed()
    {
        ConfigureReplayInterval();
    }

    private void ConfigureReplayInterval()
    {
        if (_replayTimer == null) return;
        double rate = _replaySpeedCombo?.SelectedIndex switch { 0 => .5, 1 => 1, 2 => 2, 3 => 5, 4 => 10, _ => 1 };
        double interval = 1000d;
        if (_replayMode && _replayIndex >= 0 && _replayIndex + 1 < _replayFrames.Count)
        {
            var delta = (_replayFrames[_replayIndex + 1].CapturedAtUtc - _replayFrames[_replayIndex].CapturedAtUtc).TotalMilliseconds;
            if (delta > 0 && !double.IsNaN(delta) && !double.IsInfinity(delta)) interval = delta / rate;
            else interval /= rate;
        }
        else
        {
            interval /= rate;
        }
        _replayTimer.Interval = (int)Math.Clamp(Math.Round(interval), 50, 10000);
    }

    private void DisposeWorkspace()
    {
        _replayTimer?.Stop();
        _replayTimer?.Dispose();
        if (_sessionRecorder != null) StopRecording("程序关闭");
        _timetableGraph?.Dispose();
    }
}
