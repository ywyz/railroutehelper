using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using RailRouteHelper.Core;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RailRouteAssistantDesktop
{
    public class MainForm : Form
    {
        private readonly HttpClient _http;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly TrainInfoService _trainInfo;

        private ListView _alertList;
        private ListView _trainList;
        private TextBox _trainSearchBox;
        private Label _statusLabel;
        private Label _statsLabel;

        private List<AlertData> _alerts = new();
        private List<TrainData> _trains = new();
        private bool _gameReady = false;
        private string _gameTime = "";                  // 游戏内模拟时钟 HH:MM:SS
        private bool _openingTrainDetails;
        private readonly HashSet<string> _selectedTrainNames = new();  // refresh 间保留的选中车号

        // ===== 语音播报 =====
        private VoiceEngine _voice;
        private CheckBox _muteCheck;                    // 静音开关
        private ToolStripMenuItem _voiceMenu;           // 语音包切换菜单
        private ToolStripMenuItem _voiceRateMenu;       // TTS 补全语速菜单
        private List<VoiceEngine.VoiceOption> _voiceOptions = new();
        private string _selectedVoiceKey;               // 当前选中的补全 TTS Key（持久化用）
        private int _selectedSpeechRate = VoiceEngine.DefaultSpeechRate;
        private bool _modalDialogShowing;               // 模态窗体显示中，暂停 TopMost 维持
        // 状态追踪：游戏列车 ID（无 ID 时回退原始车号）→ 上一次状态。用于检测状态变化触发播报。
        private readonly Dictionary<string, TrainPrevState> _prevStates = new();
        // 防重复：(车号|播报类型) → 上次播报的 UTC 时间
        private readonly Dictionary<string, DateTime> _lastAnnounce = new();
        private const double AnnounceCooldownSec = 30.0;  // 同车号同类型 30 秒内不重复
        private const double PreDepartureAnnouncementSeconds = 60.0;
        private const double PrePassingAnnouncementSeconds = 180.0;

        private static readonly Color ColorCritical = Color.FromArgb(220, 50, 50);
        private static readonly Color ColorWarning = Color.FromArgb(230, 150, 30);
        private static readonly Color ColorInfo = Color.FromArgb(50, 130, 220);
        private static readonly Color ColorBg = Color.FromArgb(30, 30, 35);
        private static readonly Color ColorPanel = Color.FromArgb(20, 20, 25);
        private static readonly Color ColorDim = Color.FromArgb(100, 100, 100);

        private static string DisplayVersion
        {
            get
            {
                Version version = typeof(MainForm).Assembly.GetName().Version;
                return version == null ? "未知" : $"{version.Major}.{version.Minor}.{version.Build}";
            }
        }

        public MainForm()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            _trainInfo = new TrainInfoService(_http);
            SetupUI();
            // 初始化语音播报引擎（音频目录 = 输出目录/assets/audio）
                string audioDir = Path.Combine(AppContext.BaseDirectory, "assets", "audio");
                try
                {
                    if (Directory.Exists(audioDir))
                        _voice = new VoiceEngine(audioDir);
                    else
                        Console.WriteLine($"[Voice] 音频目录不存在: {audioDir}");
                }
                catch (Exception ex) { Console.WriteLine($"[Voice] 初始化失败: {ex.Message}"); }
                InitVoiceMenu(audioDir);
            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += async (s, e) => await RefreshData();
            _timer.Start();
            FormClosed += (s, e) => _trainInfo.Dispose();
            // 后台加载车次信息（不阻塞 UI）
            _ = Task.Run(async () =>
            {
                await _trainInfo.LoadAsync();
                Console.WriteLine($"[TrainInfo] 加载完成: 在线 {_trainInfo.OnlineCount}，路路通 {_trainInfo.LulutongOfflineCount}，12306快照 {_trainInfo.Legacy12306Count}");
            });
        }

        private void SetupUI()
        {
            // 把版本直接放在标题栏，方便确认当前启动的是否为最新程序。
            Text = $"Rail Route 调度助手 v{DisplayVersion}";
            Width = 680;
            Height = 700;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(50, 50);
            TopMost = true;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            Opacity = 0.95;
            BackColor = ColorBg;

                // 顶部菜单栏：语音包切换（必须在其它 Dock=Top 控件之前添加，z-order 才会在最顶）
                BuildVoiceMenu();

                // WinForms Dock 规则：Dock=Top 的控件按"添加顺序"从顶部依次向下堆叠
            // Dock=Fill 填充所有 Top/Bottom 排列后的剩余中间空间
            //
            // 期望布局从上到下：statusLabel, trainHeader, trainList(Fill), alertHeader, alertList, statsLabel
            // 因此添加顺序：statusLabel, trainHeader, trainList(Fill), alertHeader, alertList, statsLabel

            // 先创建所有控件
            _statusLabel = new Label
            {
                Dock = DockStyle.Top, Height = 24,
                Text = "  正在连接游戏...",
                ForeColor = Color.Gray, BackColor = ColorPanel,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
            };

            // 静音开关：作为状态栏子控件浮在右侧
            _muteCheck = new CheckBox
            {
                Text = "静音",
                ForeColor = Color.LightGray, BackColor = ColorPanel,
                Font = new Font("Microsoft YaHei UI", 8F),
                AutoSize = false, Size = new Size(56, 20),
                CheckAlign = ContentAlignment.MiddleLeft,
                TextAlign = ContentAlignment.MiddleRight,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            var trainHeader = new Label
            {
                Dock = DockStyle.Top, Height = 22,
                Text = "  所有列车（双击或选中后按回车查看详情）",
                ForeColor = Color.LightSkyBlue, BackColor = ColorPanel,
                Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold)
            };

            var searchPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 32,
                BackColor = ColorPanel,
                Padding = new Padding(8, 4, 8, 4)
            };
            var searchLabel = new Label
            {
                Dock = DockStyle.Left,
                Width = 46,
                Text = "搜索：",
                ForeColor = Color.LightGray,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Microsoft YaHei UI", 8.5F)
            };
            _trainSearchBox = new TextBox
            {
                Dock = DockStyle.Fill,
                PlaceholderText = "输入车次（支持部分匹配）",
                BackColor = Color.FromArgb(45, 45, 52),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            searchPanel.Controls.Add(_trainSearchBox);
            searchPanel.Controls.Add(searchLabel);

            _trainList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                BackColor = ColorBg,
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 8.5F)
            };
            _trainList.Columns.Add("车号", 70);
            _trainList.Columns.Add("始发", 80);
            _trainList.Columns.Add("终到", 80);
            _trainList.Columns.Add("km/h", 40);
            _trainList.Columns.Add("延误", 45);
            _trainList.Columns.Add("信号", 50);
            _trainList.Columns.Add("状态", 160);
            _trainList.Columns.Add("当前停站", 110);
            _trainList.Columns.Add("前方停站", 90);
            _trainList.Columns.Add("站台", 40);

            var alertHeader = new Label
            {
                Dock = DockStyle.Top, Height = 22,
                Text = "  告警信息（按紧急程度排序）",
                ForeColor = Color.LightSkyBlue, BackColor = ColorPanel,
                Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold)
            };

            _alertList = new ListView
            {
                Dock = DockStyle.Top, Height = 180,
                View = View.Details,
                FullRowSelect = true,
                BackColor = ColorBg,
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9F),
                HeaderStyle = ColumnHeaderStyle.None
            };
            _alertList.Columns.Add("告警", 500);

            _statsLabel = new Label
            {
                Dock = DockStyle.Bottom, Height = 20,
                ForeColor = Color.DimGray, BackColor = ColorPanel,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, 0, 8, 0),
                Font = new Font("Microsoft YaHei UI", 8F)
            };

            // WinForms Dock 规则：z-order 由 Controls 集合 index 决定
            // index 大的先处理 Dock（先占据位置），index 小的后处理
            // 即 Controls.Add 顺序的反序 = Dock 处理顺序
            //
            // 期望布局从上到下：statusLabel, trainHeader, trainList(Fill), alertHeader, alertList, statsLabel
            // Dock 处理顺序（从上到下）：statusLabel, trainHeader, alertHeader, alertList, statsLabel, trainList(Fill 最后填剩余)
            // 因此 Add 顺序（反序）：trainList(Fill先add,最后处理), statsLabel, alertList, alertHeader, trainHeader, statusLabel

            Controls.Add(_trainList);       // Fill - index 0，最后处理，填充剩余
            Controls.Add(_statsLabel);      // Bottom
            Controls.Add(_alertList);       // Top
            Controls.Add(alertHeader);      // Top
            Controls.Add(searchPanel);      // Top - 列车标题下方
            Controls.Add(trainHeader);      // Top
            Controls.Add(_statusLabel);     // Top - index 最大，最先处理，最顶部

            // 静音开关浮在状态栏右侧
            _statusLabel.Controls.Add(_muteCheck);
            PositionMuteCheckbox();

            // 右键菜单 - 复制
            var copyMenu = new ContextMenuStrip();
            var detailsItem = new ToolStripMenuItem("查看车次详情（双击）");
            detailsItem.Click += (s, e) => ShowSelectedTrainDetails();
            copyMenu.Items.Add(detailsItem);
            copyMenu.Items.Add(new ToolStripSeparator());

            var copyItem = new ToolStripMenuItem("复制选中行");
            copyItem.Click += (s, e) => CopySelectedToClipboard();
            copyMenu.Items.Add(copyItem);

            var copyAllItem = new ToolStripMenuItem("复制全部列车数据");
            copyAllItem.Click += (s, e) => CopyAllToClipboard();
            copyMenu.Items.Add(copyAllItem);

            _trainList.ContextMenuStrip = copyMenu;
            _alertList.ContextMenuStrip = copyMenu;

            // 右键点击时选中点击的行，并记录当前操作的列表
            _trainList.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    _lastRightClickedList = _trainList;
                    var hit = _trainList.HitTest(e.Location);
                    if (hit.Item != null) hit.Item.Selected = true;
                }
            };
            _alertList.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    _lastRightClickedList = _alertList;
                    var hit = _alertList.HitTest(e.Location);
                    if (hit.Item != null) hit.Item.Selected = true;
                }
            };

            // 输入即筛选；车次按不区分大小写的部分匹配显示。
            _trainSearchBox.TextChanged += (s, e) => RefreshTrainList();
            _trainSearchBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && _trainList.Items.Count > 0)
                {
                    _trainList.Items[0].Selected = true;
                    _trainList.Items[0].Focused = true;
                    _trainList.Items[0].EnsureVisible();
                    _trainList.Focus();
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    _trainSearchBox.Clear();
                    e.SuppressKeyPress = true;
                }
            };

            // 点击告警条目 -> 定位并高亮列车列表中对应车次
            _alertList.MouseClick += (s, e) =>
            {
                var hit = _alertList.HitTest(e.Location);
                if (hit?.Item == null) return;
                var tag = hit.Item.Tag as string;
                if (string.IsNullOrEmpty(tag)) return;
                // 站台冲突/进路相交的告警 TrainName 形如 "G123/G456"，取第一个
                var firstName = tag.Split('/')[0].Trim();
                SelectTrainInList(firstName);
            };

            // ItemActivate 同时覆盖鼠标双击和选中行后按回车，比坐标命中判断更可靠。
            _trainList.ItemActivate += (s, e) => ShowSelectedTrainDetails();

            // 失去焦点时恢复置顶（避免被游戏窗口盖住）
            Deactivate += (s, e) =>
            {
                if (IsDisposed || _modalDialogShowing) return;
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || _modalDialogShowing) return;
                    TopMost = false;
                    TopMost = true;
                }));
            };

            // 窗口尺寸变化时重新定位静音开关
            Resize += (s, e) => PositionMuteCheckbox();
        }

        /// <summary>把静音开关定位到状态栏右侧</summary>
        private void PositionMuteCheckbox()
        {
            if (_muteCheck == null || _statusLabel == null) return;
            _muteCheck.Top = (_statusLabel.Height - _muteCheck.Height) / 2;
            _muteCheck.Left = _statusLabel.Width - _muteCheck.Width - 6;
        }

        // ===== 语音包切换 =====
        private void BuildVoiceMenu()
        {
            _voiceMenu = new ToolStripMenuItem("语音");
            var menuStrip = new MenuStrip { BackColor = ColorPanel, ForeColor = Color.LightGray,
                Font = new Font("Microsoft YaHei UI", 9F) };
            menuStrip.Items.Add(_voiceMenu);
            MainMenuStrip = menuStrip;
            Controls.Add(menuStrip);
        }

        private void InitVoiceMenu(string audioDir)
        {
            _voiceOptions = VoiceEngine.GetAvailableVoices(audioDir);
            PopulateVoiceMenu();
            RestoreVoiceSettings();
            // 所有 Dock 控件已添加后，把菜单栏送到底层 z-order——
            // dock 布局从最高索引开始，MenuStrip 会最先占据顶部边缘，不被状态栏挤下去。
            MainMenuStrip?.SendToBack();
        }

        private void PopulateVoiceMenu()
        {
            if (_voiceMenu == null) return;
            _voiceMenu.DropDownItems.Clear();
            if (_voiceOptions.Count == 0)
            {
                var empty = _voiceMenu.DropDownItems.Add("（未发现可用语音）");
                empty.Enabled = false;
                return;
            }
            foreach (var opt in _voiceOptions)
            {
                var item = new ToolStripMenuItem(opt.DisplayName) { Tag = opt };
                item.Click += (s, e) => OnVoiceOptionClicked(opt);
                _voiceMenu.DropDownItems.Add(item);
            }
            _voiceMenu.DropDownItems.Add(new ToolStripSeparator());

            _voiceRateMenu = new ToolStripMenuItem("补全语音速度");
            for (int rate = VoiceEngine.MinimumSpeechRate; rate <= VoiceEngine.MaximumSpeechRate; rate++)
            {
                int selectedRate = rate;
                string suffix = rate switch
                {
                    VoiceEngine.MinimumSpeechRate => "（最慢）",
                    4 => "（正常）",
                    VoiceEngine.MaximumSpeechRate => "（最快，默认）",
                    _ => ""
                };
                var rateItem = new ToolStripMenuItem($"{rate}{suffix}") { Tag = selectedRate };
                rateItem.Click += (s, e) => OnSpeechRateClicked(selectedRate);
                _voiceRateMenu.DropDownItems.Add(rateItem);
            }
            _voiceMenu.DropDownItems.Add(_voiceRateMenu);

            var previewItem = _voiceMenu.DropDownItems.Add("试听当前补全语音");
            previewItem.Click += (s, e) => _voice?.EnqueuePreview();

            _voiceMenu.DropDownItems.Add(new ToolStripSeparator());
            var installItem = _voiceMenu.DropDownItems.Add("安装更多语音…");
            installItem.Click += (s, e) =>
            {
                _modalDialogShowing = true;
                try
                {
                    MessageBox.Show(
                        "Windows 设置 → 时间和语言 → 语音 → 添加语音，选中文（简体）。\n" +
                        "Microsoft Kangkang 为男声，Huihui / Yaoyao 为女声。\n" +
                        "安装后重启桌面助手，菜单里就会出现新语音。",
                        "安装更多语音", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                finally { _modalDialogShowing = false; }
            };
            UpdateVoiceMenuChecks();
        }

        private void OnVoiceOptionClicked(VoiceEngine.VoiceOption opt)
        {
            if (_voice == null)
            {
                _modalDialogShowing = true;
                try { MessageBox.Show("语音引擎未初始化，无法切换。", "语音切换",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                finally { _modalDialogShowing = false; }
                return;
            }
            if (!_voice.ApplyVoice(opt))
            {
                _modalDialogShowing = true;
                try { MessageBox.Show("所选语音当前不可用，请重新打开菜单后再试。", "语音切换",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                finally { _modalDialogShowing = false; }
                return;
            }
            _selectedVoiceKey = opt.Key;
            VoiceSettings.Save(_selectedVoiceKey, _selectedSpeechRate);
            UpdateVoiceMenuChecks();
        }

        private void OnSpeechRateClicked(int rate)
        {
            _selectedSpeechRate = VoiceEngine.ClampSpeechRate(rate);
            _voice?.SetSpeechRate(_selectedSpeechRate);
            VoiceSettings.Save(_selectedVoiceKey, _selectedSpeechRate);
            UpdateVoiceMenuChecks();
        }

        private void UpdateVoiceMenuChecks()
        {
            if (_voiceMenu == null) return;
            foreach (var item in _voiceMenu.DropDownItems)
            {
                if (item is ToolStripMenuItem mi && mi.Tag is VoiceEngine.VoiceOption opt)
                    mi.Checked = string.Equals(opt.Key, _selectedVoiceKey, StringComparison.Ordinal);
            }
            if (_voiceRateMenu != null)
            {
                foreach (var item in _voiceRateMenu.DropDownItems)
                {
                    if (item is ToolStripMenuItem mi && mi.Tag is int rate)
                        mi.Checked = rate == _selectedSpeechRate;
                }
            }
        }

        private void RestoreVoiceSettings()
        {
            var settings = VoiceSettings.Load();
            string saved = settings.SelectedVoiceKey;
            // v2.6.0-v2.6.2 把两个并不存在的 Edge 音色映射到了同一个百度 voice；
            // 旧配置统一迁移为真实的百度选项。
            if (!string.IsNullOrEmpty(saved) && saved.StartsWith("edge:", StringComparison.Ordinal))
                saved = "baidu:default";
            if (string.IsNullOrEmpty(saved)) saved = "baidu:default";

            _selectedSpeechRate = VoiceEngine.ClampSpeechRate(settings.SpeechRate);
            _voice?.SetSpeechRate(_selectedSpeechRate);
            var opt = _voiceOptions.FirstOrDefault(o =>
                string.Equals(o.Key, saved, StringComparison.Ordinal));
            if (opt == null) opt = _voiceOptions.FirstOrDefault();
            if (opt != null && _voice != null && _voice.ApplyVoice(opt))
            {
                _selectedVoiceKey = opt.Key;
            }
            UpdateVoiceMenuChecks();
        }

        /// <summary>
        /// 在列车列表中查找指定车号并选中+滚动到可见
        /// </summary>
        private void SelectTrainInList(string trainName)
        {
            if (string.IsNullOrEmpty(trainName)) return;
            // 当前筛选条件若隐藏了告警对应车次，直接把搜索条件切到该车次。
            if (_trainSearchBox != null &&
                trainName.IndexOf(_trainSearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
            {
                _trainSearchBox.Text = trainName;
            }
            _trainList.BeginUpdate();
            try
            {
                // 清除现有选中
                foreach (ListViewItem it in _trainList.SelectedItems)
                    it.Selected = false;

                bool found = false;
                foreach (ListViewItem it in _trainList.Items)
                {
                    if (it.Text == trainName)
                    {
                        it.Selected = true;
                        it.Focused = true;
                        it.EnsureVisible();
                        found = true;
                        break;
                    }
                }
                if (found)
                {
                    _trainList.Focus();
                    _selectedTrainNames.Clear();
                    _selectedTrainNames.Add(trainName);
                }
            }
            finally { _trainList.EndUpdate(); }
        }

        private ListView _lastRightClickedList;

        private void CopySelectedToClipboard()
        {
            // 优先用右键点击记录的列表，否则取有选中项的列表
            var list = _lastRightClickedList;
            if (list == null || list.SelectedItems.Count == 0)
            {
                list = _trainList.SelectedItems.Count > 0 ? _trainList :
                       _alertList.SelectedItems.Count > 0 ? _alertList : null;
            }
            if (list == null || list.SelectedItems.Count == 0) return;

            var sb = new StringBuilder();
            foreach (ListViewItem item in list.SelectedItems)
            {
                var parts = new List<string>();
                foreach (ListViewItem.ListViewSubItem sub in item.SubItems)
                    parts.Add(sub.Text);
                sb.AppendLine(string.Join("\t", parts));
            }
            try { Clipboard.SetText(sb.ToString()); } catch { }
        }

        private void CopyAllToClipboard()
        {
            var sb = new StringBuilder();
            // 表头
            foreach (ColumnHeader col in _trainList.Columns)
                sb.Append(col.Text + "\t");
            sb.AppendLine();

            foreach (ListViewItem item in _trainList.Items)
            {
                var parts = new List<string>();
                foreach (ListViewItem.ListViewSubItem sub in item.SubItems)
                    parts.Add(sub.Text);
                sb.AppendLine(string.Join("\t", parts));
            }
            Clipboard.SetText(sb.ToString());
        }

        private async Task RefreshData()
        {
            try
            {
                var resp = await _http.GetStringAsync("http://localhost:8787/data");
                var root = JsonDocument.Parse(resp).RootElement;

                _gameReady = root.GetProperty("gameReady").GetBoolean();

                // 游戏内时间
                if (root.TryGetProperty("gameTime", out var gtEl) && gtEl.ValueKind == JsonValueKind.String)
                    _gameTime = gtEl.GetString() ?? "";
                else
                    _gameTime = "";

                _alerts.Clear();
                if (root.TryGetProperty("alerts", out var alertsEl))
                    foreach (var a in alertsEl.EnumerateArray())
                        _alerts.Add(new AlertData
                        {
                            Level = a.GetProperty("level").GetString(),
                            TrainName = a.GetProperty("train").GetString(),
                            Message = a.GetProperty("message").GetString()
                        });

                _trains.Clear();
                if (root.TryGetProperty("trains", out var trainsEl))
                {
                    foreach (var t in trainsEl.EnumerateArray())
                    {
                        var train = new TrainData
                        {
                            Id = t.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : "",
                            Name = t.GetProperty("name").GetString() ?? "?",
                            Speed = t.GetProperty("speed").GetInt32(),
                            TargetSpeed = t.GetProperty("targetSpeed").GetSingle(),
                            Delay = t.GetProperty("delay").GetDouble(),
                            CanDepart = t.GetProperty("canDepart").GetBoolean(),
                            Finished = t.GetProperty("finished").GetBoolean(),
                            BrokenDown = t.GetProperty("brokenDown").GetBoolean(),
                            OnBoard = t.GetProperty("onBoard").GetBoolean(),
                            Waiting = t.GetProperty("waiting").GetBoolean(),
                            Lookahead = t.GetProperty("lookahead").GetInt32(),
                            NeedsRoute = t.GetProperty("needsRoute").GetBoolean(),
                            HasSignal = t.GetProperty("hasSignal").GetBoolean(),
                            SignalState = t.GetProperty("signalState").GetString() ?? "",
                            Platform = t.GetProperty("platform").GetInt32(),
                            NextStation = t.GetProperty("nextStation").GetString() ?? "",
                            NextStationNonStop = t.TryGetProperty("nextStationNonStop", out var nsn) && nsn.ValueKind is JsonValueKind.True or JsonValueKind.False && nsn.GetBoolean(),
                            ActualVisitCount = t.TryGetProperty("actualVisitCount", out var avc) && avc.ValueKind == JsonValueKind.Number ? avc.GetInt32() : 0,
                            ScheduledVisitCount = t.TryGetProperty("scheduledVisitCount", out var svc) && svc.ValueKind == JsonValueKind.Number ? svc.GetInt32() : 0,
                            ScheduledVisitIndex = t.TryGetProperty("scheduledVisitIndex", out var svi) && svi.ValueKind == JsonValueKind.Number ? svi.GetInt32() : -1,
                            LastVisitStation = t.TryGetProperty("lastVisitStation", out var lvs) && lvs.ValueKind == JsonValueKind.String ? lvs.GetString() ?? "" : "",
                            LastVisitPlatform = t.TryGetProperty("lastVisitPlatform", out var lvp) && lvp.ValueKind == JsonValueKind.Number ? lvp.GetInt32() : 0,
                            LastVisitNonStop = t.TryGetProperty("lastVisitNonStop", out var lvns) && lvns.ValueKind is JsonValueKind.True or JsonValueKind.False && lvns.GetBoolean(),
                            LastVisitStopMinutes = t.TryGetProperty("lastVisitStopMinutes", out var lvsm) && lvsm.ValueKind == JsonValueKind.Number ? lvsm.GetInt32() : 0,
                            LastVisitDeparted = t.TryGetProperty("lastVisitDeparted", out var lvd) && lvd.ValueKind is JsonValueKind.True or JsonValueKind.False && lvd.GetBoolean(),
                            LastArrivalScheduleDeviationSec = t.TryGetProperty("lastArrivalScheduleDeviationSec", out var las) && las.ValueKind == JsonValueKind.Number ? las.GetDouble() : null,
                            LastDepartureScheduleDelaySec = t.TryGetProperty("lastDepartureScheduleDelaySec", out var lds) && lds.ValueKind == JsonValueKind.Number ? lds.GetDouble() : null,
                            RequiresDirectionChange = t.TryGetProperty("requiresDirectionChange", out var rdc) && rdc.ValueKind is JsonValueKind.True or JsonValueKind.False && rdc.GetBoolean(),
                            CurrentStation = t.TryGetProperty("currentStation", out var cs) && cs.ValueKind == JsonValueKind.String ? cs.GetString() ?? "" : "",
                            CurrentPlatform = t.TryGetProperty("currentPlatform", out var cp) && cp.ValueKind == JsonValueKind.Number ? cp.GetInt32() : 0,
                            CurrentStopMinutes = t.TryGetProperty("currentStopMinutes", out var csm) && csm.ValueKind == JsonValueKind.Number ? csm.GetInt32() : 0,
                            DepartureRemainingSec = t.TryGetProperty("departureRemainingSec", out var drs) && drs.ValueKind == JsonValueKind.Number ? drs.GetDouble() : null,
                            CurrentDepartureScheduleDelaySec = t.TryGetProperty("currentDepartureScheduleDelaySec", out var cds) && cds.ValueKind == JsonValueKind.Number ? cds.GetDouble() : null,
                            StopReasons = t.GetProperty("stopReasons").GetString() ?? "",
                            NextPrepareSec = t.TryGetProperty("nextPrepareSec", out var np) && np.ValueKind == JsonValueKind.Number ? np.GetDouble() : null,
                            NextArrivalSec = t.TryGetProperty("nextArrivalSec", out var na) && na.ValueKind == JsonValueKind.Number ? na.GetDouble() : null,
                            NotMovingSince = t.TryGetProperty("notMovingSince", out var nm) && nm.ValueKind == JsonValueKind.Number ? nm.GetDouble() : null,
                            SignalAllocationState = t.TryGetProperty("signalAllocationState", out var sa) && sa.ValueKind == JsonValueKind.Number ? sa.GetInt32() : -1,
                            FrontAllocationState = t.TryGetProperty("frontAllocationState", out var fa) && fa.ValueKind == JsonValueKind.Number ? fa.GetInt32() : -1,
                            MapEntryTimeSec = t.TryGetProperty("mapEntryTimeSec", out var me) && me.ValueKind == JsonValueKind.Number ? me.GetDouble() : null,
                            MapExitTimeSec = t.TryGetProperty("mapExitTimeSec", out var mx) && mx.ValueKind == JsonValueKind.Number ? mx.GetDouble() : null,
                            MapEntryStation = t.TryGetProperty("mapEntryStation", out var mes) && mes.ValueKind == JsonValueKind.String ? mes.GetString() ?? "" : "",
                            MapExitStation = t.TryGetProperty("mapExitStation", out var mxs) && mxs.ValueKind == JsonValueKind.String ? mxs.GetString() ?? "" : "",
                            MapEntryPlatform = t.TryGetProperty("mapEntryPlatform", out var mep) && mep.ValueKind == JsonValueKind.Number ? mep.GetInt32() : 0,
                            MapExitPlatform = t.TryGetProperty("mapExitPlatform", out var mxp) && mxp.ValueKind == JsonValueKind.Number ? mxp.GetInt32() : 0,
                            MapEntryNonStop = t.TryGetProperty("mapEntryNonStop", out var mens) && mens.ValueKind is JsonValueKind.True or JsonValueKind.False && mens.GetBoolean(),
                            MapExitNonStop = t.TryGetProperty("mapExitNonStop", out var mxns) && mxns.ValueKind is JsonValueKind.True or JsonValueKind.False && mxns.GetBoolean()
                        };

                        if (t.TryGetProperty("scheduledStops", out var stopsEl) && stopsEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var stop in stopsEl.EnumerateArray())
                            {
                                train.ScheduledStops.Add(new ScheduledStopData
                                {
                                    Station = stop.TryGetProperty("station", out var station) && station.ValueKind == JsonValueKind.String ? station.GetString() ?? "" : "",
                                    Platform = stop.TryGetProperty("platform", out var platform) && platform.ValueKind == JsonValueKind.Number ? platform.GetInt32() : 0,
                                    ArrivalTimeSec = stop.TryGetProperty("arrivalTimeSec", out var arrival) && arrival.ValueKind == JsonValueKind.Number ? arrival.GetDouble() : null,
                                    DepartureTimeSec = stop.TryGetProperty("departureTimeSec", out var departure) && departure.ValueKind == JsonValueKind.Number ? departure.GetDouble() : null,
                                    StopMinutes = stop.TryGetProperty("stopMinutes", out var stopMinutes) && stopMinutes.ValueKind == JsonValueKind.Number ? stopMinutes.GetInt32() : 0,
                                    RelativeTimes = stop.TryGetProperty("relativeTimes", out var relative) && relative.ValueKind is JsonValueKind.True or JsonValueKind.False && relative.GetBoolean(),
                                    NonStop = stop.TryGetProperty("nonStop", out var nonStop) && nonStop.ValueKind is JsonValueKind.True or JsonValueKind.False && nonStop.GetBoolean()
                                });
                            }
                        }

                        _trains.Add(train);
                    }
                }

                // 每趟出现的列车都在后台按车号精确查询；不阻塞本次 UI/语音刷新。
                PreloadTrainInfo();

                // 语音播报：状态变化检测（用原始车号追踪，拆分前调用）
                DetectAndAnnounce();

                // 复合车次只显示当前运行段；到达首个计划停车站后切换到第二段车号。
                _trains = ResolveActiveCompositeTrainCodes(_trains);

                UpdateUI();

                // 周期性维持置顶：游戏窗口偶尔会盖住本窗口，每秒检查一次。
                // 模态窗体显示时暂停，避免抢走子窗体焦点导致按钮无法点击。
                if (!_modalDialogShowing && !TopMost) TopMost = true;
            }
            catch (HttpRequestException)
            {
                _statusLabel.Text = "  未连接游戏 - 请启动 Rail Route";
                _statusLabel.ForeColor = Color.OrangeRed;
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"  错误: {ex.Message}";
                _statusLabel.ForeColor = Color.Red;
            }
        }

        /// <summary>
        /// 根据车次前缀获取背景色
        /// </summary>
        private static Color GetTrainBackColor(string name)
        {
            string code = TrainCodeRules.NormalizeLookupCode(name) ?? "";
            if (string.IsNullOrEmpty(code)) return ColorBg;

            // DJ 是“动检”列车，不与普通 D 字头动车使用同一背景色。
            if (code.StartsWith("DJ", StringComparison.Ordinal))
                return Color.FromArgb(20, 55, 65);       // 动检 - 暗蓝绿

            char c = code[0];
            return c switch
            {
                'G' => Color.FromArgb(80, 30, 30),   // 高铁 - 暗红
                'D' => Color.FromArgb(20, 40, 70),   // 动车 - 暗蓝
                'C' when code.Length <= 4 => Color.FromArgb(20, 60, 30),   // 城际三字 - 暗绿
                'C' => Color.FromArgb(20, 50, 55),   // 城际四字 - 暗青
                'X' => Color.FromArgb(50, 30, 60),   // 行包/直达特快X - 暗紫
                'Z' => Color.FromArgb(20, 60, 30),   // 直达 - 暗绿
                'T' => Color.FromArgb(70, 50, 20),   // 特快 - 暗橙
                'K' => Color.FromArgb(60, 55, 20),   // 快速 - 暗黄
                'L' => Color.FromArgb(40, 40, 50),   // 临客 - 暗灰蓝
                'S' => Color.FromArgb(50, 30, 60),   // 市郊 - 暗紫
                'Y' => Color.FromArgb(70, 30, 55),   // 游车 - 暗玫红
                'J' => Color.FromArgb(20, 55, 65),   // 检测/特殊列车 - 暗蓝绿
                'P' => Color.FromArgb(65, 42, 25),   // 特殊/临时列车 - 暗棕
                'Q' => Color.FromArgb(25, 55, 52),   // 特殊列车 - 暗青绿
                'N' => Color.FromArgb(50, 50, 25),   // 管内/特殊列车 - 暗橄榄
                'A' => Color.FromArgb(55, 35, 45),   // 按需/特殊列车 - 暗褐红
                >= '0' and <= '9' => Color.FromArgb(52, 42, 30), // 纯数字普速 - 暗棕灰
                >= 'A' and <= 'Z' => Color.FromArgb(45, 35, 55), // 其他字头 - 暗紫灰
                _ => ColorBg
            };
        }

        /// <summary>
        /// 列车排序：故障 > 在线停车（按剩余发车时间升序）> 运行中 > 等待入图 > 已完成
        /// </summary>
        private static int TrainSortPriority(TrainData t)
        {
            if (t.BrokenDown) return 0;          // 故障最优先
            if (t.OnBoard && t.Speed == 0) return 1;  // 在线停车（停站）
            if (t.OnBoard && t.Speed > 0) return 2;   // 运行中
            if (t.OnBoard) return 3;              // 在线其他
            if (t.Waiting) return 4;              // 等待入图
            if (t.Finished) return 6;             // 已完成
            return 5;                              // 其他
        }

        /// <summary>
        /// 列车完整排序比较：先按 TrainSortPriority 分组，停站组内按剩余发车时间升序。
        /// </summary>
        private static int CompareTrains(TrainData a, TrainData b)
        {
            int pa = TrainSortPriority(a);
            int pb = TrainSortPriority(b);
            if (pa != pb) return pa.CompareTo(pb);
            // 同组内：停站组（优先级 1）按剩余发车时间从少到多排序
            if (pa == 1)
            {
                double da = a.DepartureRemainingSec ?? double.MaxValue;
                double db = b.DepartureRemainingSec ?? double.MaxValue;
                return da.CompareTo(db);
            }
            // 其他组按车号稳定排序
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateUI()
        {
            // 状态栏
            int onBoard = _trains.FindAll(t => t.OnBoard).Count;
            int waiting = _trains.FindAll(t => t.Waiting).Count;

            if (!_gameReady)
            {
                _statusLabel.Text = "  游戏未就绪 - 请进入地图";
                _statusLabel.ForeColor = Color.Gray;
            }
            else
            {
                var timeStr = !string.IsNullOrEmpty(_gameTime) ? $"游戏时间 {_gameTime}  |  " : "";
                var dbStr = _trainInfo.IsLoaded
                    ? $"  |  车次 在线 {_trainInfo.OnlineCount} / 离线 {_trainInfo.OfflineCount}"
                    : "  |  车次库加载中";
                _statusLabel.Text = $"  {timeStr}已连接  |  在线 {onBoard}  等待 {waiting}  总计 {_trains.Count}{dbStr}";
                _statusLabel.ForeColor = Color.LightGreen;
            }

            // 统计
            int crit = _alerts.FindAll(a => a.Level == "critical").Count;
            int warn = _alerts.FindAll(a => a.Level == "warning").Count;
            int info = _alerts.FindAll(a => a.Level == "info").Count;
            _statsLabel.Text = $"  紧急 {crit}   警告 {warn}   信息 {info}   ";

            // 告警列表（每个 item 的 Tag 存车号，用于点击定位）
            _alertList.BeginUpdate();
            _alertList.Items.Clear();
            foreach (var a in _alerts)
            {
                var tag = a.Level == "critical" ? "[!]" : a.Level == "warning" ? "[~]" : "[i]";
                var item = new ListViewItem($"{tag} {a.TrainName} - {a.Message}");
                item.ForeColor = a.Level switch
                {
                    "critical" => ColorCritical,
                    "warning" => ColorWarning,
                    _ => ColorInfo
                };
                item.Tag = a.TrainName ?? "";
                _alertList.Items.Add(item);
            }
            if (_alerts.Count == 0)
            {
                var item = new ListViewItem("  暂无告警") { ForeColor = ColorDim };
                _alertList.Items.Add(item);
            }
            _alertList.EndUpdate();

            RefreshTrainList();
        }

        /// <summary>按搜索框筛选并重建列车列表，同时保留仍可见的选中车号和滚动位置。</summary>
        private void RefreshTrainList()
        {
            if (_trainList == null) return;

            // 列车列表 - 排序后重建（顺序会变化）；保留选中车号
            _trains.Sort(CompareTrains);
            string query = _trainSearchBox?.Text.Trim() ?? "";

            // 记录当前选中的车号，重建后恢复
            _selectedTrainNames.Clear();
            foreach (ListViewItem sel in _trainList.SelectedItems)
                if (!string.IsNullOrEmpty(sel.Text)) _selectedTrainNames.Add(sel.Text);

            // 保存当前滚动位置（顶部可见项的车号），重建后恢复
            string topTrainName = null;
            if (_trainList.Items.Count > 0)
            {
                try { topTrainName = _trainList.TopItem?.Text; }
                catch { /* TopItem 在某些状态下可能抛异常，忽略 */ }
            }

            _trainList.BeginUpdate();
            _trainList.Items.Clear();
            foreach (var t in _trains)
            {
                if (!string.IsNullOrEmpty(query) &&
                    t.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var item = CreateTrainItem(t);
                if (_selectedTrainNames.Contains(t.Name))
                {
                    item.Selected = true;
                }
                _trainList.Items.Add(item);
            }
            _trainList.EndUpdate();

            // 恢复滚动位置，避免每次刷新都跳回顶部
            if (!string.IsNullOrEmpty(topTrainName))
            {
                for (int i = 0; i < _trainList.Items.Count; i++)
                {
                    if (_trainList.Items[i].Text == topTrainName)
                    {
                        try { _trainList.TopItem = _trainList.Items[i]; }
                        catch { }
                        break;
                    }
                }
            }
        }

        private ListViewItem CreateTrainItem(TrainData t)
        {
            var item = new ListViewItem(t.Name) { Tag = t };
            item.SubItems.Add(""); // 始发
            item.SubItems.Add(""); // 终到
            item.SubItems.Add(""); // km/h
            item.SubItems.Add(""); // 延误
            item.SubItems.Add(""); // 信号
            item.SubItems.Add(""); // 状态
            item.SubItems.Add(""); // 当前停站
            item.SubItems.Add(""); // 前方停站
            item.SubItems.Add(""); // 站台
            UpdateTrainItem(item, t);
            return item;
        }

        private void ShowSelectedTrainDetails()
        {
            if (_trainList?.SelectedItems.Count > 0 &&
                _trainList.SelectedItems[0].Tag is TrainData train)
            {
                ShowTrainDetails(train);
            }
        }

        private async void ShowTrainDetails(TrainData train)
        {
            if (train == null || _openingTrainDetails) return;

            string origin = "未知";
            string destination = "未知";
            if (_trainInfo.TryLookup(train.Name, out var info))
            {
                origin = StripEnglishPrefix(info.Origin);
                destination = StripEnglishPrefix(info.Destination);
            }
            else
            {
                _ = _trainInfo.EnsureResolvedAsync(train.Name);
            }

            OnlineTrainDetails onlineDetails = null;
            _openingTrainDetails = true;
            UseWaitCursor = true;
            try
            {
                onlineDetails = await _trainInfo.GetOnlineDetailsAsync(train.Name);
                if (onlineDetails?.Stops.Count > 0)
                {
                    // 详情接口成功时优先使用当天实际运行图的首末站。
                    origin = StripEnglishPrefix(onlineDetails.Stops[0].StationName);
                    destination = StripEnglishPrefix(
                        onlineDetails.Stops[onlineDetails.Stops.Count - 1].StationName);
                }
            }
            finally
            {
                UseWaitCursor = false;
                _openingTrainDetails = false;
            }

            if (IsDisposed) return;

            using var dialog = new TrainDetailsForm(
                train.Name,
                origin,
                destination,
                train.ScheduledStops,
                onlineDetails,
                train.MapEntryTimeSec,
                train.MapExitTimeSec,
                train.MapEntryStation,
                train.MapExitStation,
                train.MapEntryPlatform,
                train.MapExitPlatform,
                train.MapEntryNonStop,
                train.MapExitNonStop);
            _modalDialogShowing = true;
            try { dialog.ShowDialog(this); }
            finally { _modalDialogShowing = false; }
        }

        /// <summary>
        /// 尝试拆分复合车号。支持相邻编号和斜杠形式，例如
        /// G4545G4546、DJ8598G3401、0G1703/G1704、0Y2/Y1。
        /// </summary>
        private static bool TrySplitMergedTrainNumber(string name, out string part1, out string part2)
        {
            return TrainCodeRules.TrySplitCompositeCode(name, out part1, out part2);
        }

        /// <summary>
        /// 预热当前地图上所有车号。合并车号会分别查询两段，保证列表和播报都能尽早拿到
        /// 始发终到；服务内部会去重、限流并在失败时立即让离线表接管。
        /// </summary>
        private void PreloadTrainInfo()
        {
            if (!_trainInfo.IsLoaded) return;

            foreach (var train in _trains)
            {
                if (string.IsNullOrWhiteSpace(train.Name) || train.Name == "?") continue;

                if (TrySplitMergedTrainNumber(train.Name, out var part1, out var part2))
                {
                    _ = _trainInfo.EnsureResolvedAsync(part1);
                    _ = _trainInfo.EnsureResolvedAsync(part2);
                }
                else
                {
                    _ = _trainInfo.EnsureResolvedAsync(train.Name);
                }
            }
        }

        /// <summary>
        /// 语音播报：检测列车状态变化并触发播报。
        /// 在 RefreshData 中、活动复合车号解析之前调用，用原始车号追踪状态。
        /// 触发点：
        ///   等待入图：Waiting 由 false→true（首次见到不算，避免启动时全员播报）
        ///   到站/通过：ActualVisitCount 增加；LastVisitNonStop 区分两类访问
        ///   发车前预告：中间站的发车倒计时首次进入 60 秒窗口
        ///   发车：最近一次实际访问的 Departed 由 false→true，速度变化仅作兼容性兜底
        /// 防重复：同车号+同类型 30 秒内不重复
        /// </summary>
        private void DetectAndAnnounce()
        {
            if (_voice == null) return;
            if (_muteCheck?.Checked == true) return;
            if (!_trainInfo.IsLoaded) return;  // 车次库未加载则不播报（缺终到站）

            var nowUtc = DateTime.UtcNow;
            // 本次刷新见到的列车状态键集合，用于清理失效的追踪状态
            var seenStateKeys = new HashSet<string>();

            foreach (var t in _trains)
            {
                if (string.IsNullOrEmpty(t.Name) || t.Name == "?") continue;
                string stateKey = GetTrainStateKey(t);
                seenStateKeys.Add(stateKey);

                _prevStates.TryGetValue(stateKey, out var prev);
                bool hadPrev = _prevStates.ContainsKey(stateKey);

                bool curStationStop = IsStationStop(t);
                var cur = new TrainPrevState
                {
                    OnBoard = t.OnBoard,
                    Waiting = t.Waiting,
                    Speed = t.Speed,
                    WasStationStop = curStationStop,
                    ActualVisitCount = t.ActualVisitCount,
                    LastVisitDeparted = t.LastVisitDeparted,
                    DepartureRemainingSec = t.DepartureRemainingSec,
                    NextArrivalSec = t.NextArrivalSec,
                    NextStation = t.NextStation,
                    PrePassingStation = hadPrev && string.Equals(prev.NextStation, t.NextStation, StringComparison.Ordinal)
                        ? prev.PrePassingStation : null,
                    PreDepartureAnnouncementVisitCount = hadPrev ? prev.PreDepartureAnnouncementVisitCount : 0,
                    DirectionChangeAnnouncementVisitCount = hadPrev ? prev.DirectionChangeAnnouncementVisitCount : -1
                };

                // 仅在已有上一帧状态时判断状态变化（避免启动时全员播报）
                if (hadPrev)
                {
                    // 复合车次在首个计划停车站前使用第一段，到站后切换为第二段。
                    string announceCode = GetActiveTrainCode(t);
                    string dest = LookupDestination(announceCode);

                    // 1. 等待入图：优先在 Waiting 出现时播报；首站为通过站的地图可能
                    // 不提供该状态，因此在 OnBoard 由 false 变 true 时兜底补报。
                    if ((!prev.Waiting && t.Waiting) || (!prev.OnBoard && t.OnBoard))
                    {
                        if (ShouldAnnounce(announceCode, "arriving", nowUtc))
                        {
                            _voice.Enqueue(new VoiceEngine.Announcement
                            {
                                Type = VoiceEngine.AnnouncementType.Arriving,
                                TrainCode = announceCode,
                                Destination = dest
                            });
                            Console.WriteLine($"[Voice] 等待入图: {announceCode} 开往{dest}");
                        }
                    }

                    // 2. 通过站前三个游戏分钟预告。下一访问刚切换且已落入窗口时也播，
                    // 避免刷新间隔或短区间造成越过 180 秒边界后漏报。
                    if (EnteredPrePassingWindow(t, prev) &&
                        !string.Equals(cur.PrePassingStation, t.NextStation, StringComparison.Ordinal) &&
                        ShouldAnnounce(announceCode, $"pre-passing-{t.ActualVisitCount}-{t.NextStation}", nowUtc))
                    {
                        _voice.Enqueue(new VoiceEngine.Announcement
                        {
                            Type = VoiceEngine.AnnouncementType.ApproachingPass,
                            TrainCode = announceCode,
                            Station = StripEnglishPrefix(t.NextStation),
                            Platform = t.Platform
                        });
                        cur.PrePassingStation = t.NextStation;
                        Console.WriteLine($"[Voice] 通过预告: {announceCode} -> {t.NextStation}{t.Platform}道，还有{(t.NextArrivalSec ?? 0):F0}秒");
                    }

                    // 3. 一次实际访问已经完成：NonStop=true 为通过，否则为到站停车。
                    bool hasNewVisit = t.ActualVisitCount > prev.ActualVisitCount;
                    if (hasNewVisit && t.ActualVisitCount > 0 && t.LastVisitNonStop)
                    {
                        if (ShouldAnnounce(announceCode, $"passing-{t.ActualVisitCount}", nowUtc))
                        {
                            _voice.Enqueue(new VoiceEngine.Announcement
                            {
                                Type = VoiceEngine.AnnouncementType.Passing,
                                TrainCode = announceCode,
                                Destination = dest,
                                Station = StripEnglishPrefix(t.LastVisitStation),
                                Platform = t.LastVisitPlatform,
                                // 通过播报必须给出早点/正点/晚点。优先使用本站实测偏差；
                                // RelativeTimes 无绝对时刻时才退回游戏提供的列车偏差。
                                DelayMinutes = GetScheduleDeviationMinutes(
                                    t.LastArrivalScheduleDeviationSec ?? t.Delay),
                                NextStation = StripEnglishPrefix(t.NextStation),
                                NextPlatform = t.Platform,
                                NextStationNonStop = t.NextStationNonStop
                            });
                            Console.WriteLine($"[Voice] 通过: {announceCode} @ {t.LastVisitStation}{t.LastVisitPlatform}道 -> {t.NextStation}{t.Platform}道");
                        }
                    }
                    else if (hasNewVisit && t.ActualVisitCount > 0)
                    {
                        if (ShouldAnnounce(announceCode, $"stopped-{t.ActualVisitCount}", nowUtc))
                        {
                            string station = !string.IsNullOrEmpty(t.CurrentStation) ? t.CurrentStation : t.LastVisitStation;
                            int platform = t.CurrentPlatform > 0 ? t.CurrentPlatform : t.LastVisitPlatform;
                            int stopMinutes = t.CurrentStopMinutes > 0 ? t.CurrentStopMinutes : t.LastVisitStopMinutes;
                            bool requiresDirectionChange = t.RequiresDirectionChange;
                            _voice.Enqueue(new VoiceEngine.Announcement
                            {
                                Type = VoiceEngine.AnnouncementType.StoppedAtStation,
                                TrainCode = announceCode,
                                Destination = dest,
                                Station = StripEnglishPrefix(station),
                                Platform = platform,
                                StopMinutes = stopMinutes,
                                RequiresDirectionChange = requiresDirectionChange,
                                DelayMinutes = GetScheduleDeviationMinutes(t.LastArrivalScheduleDeviationSec)
                            });
                            if (requiresDirectionChange)
                                cur.DirectionChangeAnnouncementVisitCount = t.ActualVisitCount;
                            Console.WriteLine($"[Voice] 停站: {announceCode} @ {station}{platform}台 停{stopMinutes}分 开往{dest}");
                        }
                    }

                    // 某些游戏版本会在列车到站后的下一次刷新才更新调向标志；
                    // 此时补播独立提示，避免漏报，同时用实际访问序号保证每次停站只播报一次。
                    if (t.RequiresDirectionChange && IsStationStop(t) &&
                        cur.DirectionChangeAnnouncementVisitCount != t.ActualVisitCount &&
                        ShouldAnnounce(announceCode, $"direction-change-{t.ActualVisitCount}", nowUtc))
                    {
                        _voice.Enqueue(new VoiceEngine.Announcement
                        {
                            Type = VoiceEngine.AnnouncementType.DirectionChange
                        });
                        cur.DirectionChangeAnnouncementVisitCount = t.ActualVisitCount;
                        Console.WriteLine($"[Voice] 调向: {announceCode}");
                    }

                    // 4. 中间站发车前一分钟预告。以倒计时越过 60 秒为触发点，
                    // 既能容忍刷新间隔，也避免在整分钟内重复播报。
                    if (EnteredPreDepartureWindow(t, prev) && prev.PreDepartureAnnouncementVisitCount != t.ActualVisitCount)
                    {
                        string station = !string.IsNullOrEmpty(t.CurrentStation) ? t.CurrentStation : t.LastVisitStation;
                        int platform = t.CurrentPlatform > 0 ? t.CurrentPlatform : t.LastVisitPlatform;
                        if (!string.IsNullOrEmpty(station) &&
                            ShouldAnnounce(announceCode, $"pre-departure-{t.ActualVisitCount}", nowUtc))
                        {
                            _voice.Enqueue(new VoiceEngine.Announcement
                            {
                                Type = VoiceEngine.AnnouncementType.PreDeparture,
                                TrainCode = announceCode,
                                Station = StripEnglishPrefix(station),
                                Platform = platform
                            });
                            cur.PreDepartureAnnouncementVisitCount = t.ActualVisitCount;
                            Console.WriteLine($"[Voice] 发车预告: {station}{platform}道 {announceCode}，还有{(t.DepartureRemainingSec ?? 0):F0}秒");
                        }
                    }

                    // 5. 发车：优先使用游戏的 Departed 标记；旧插件数据缺该字段时回退速度变化。
                    bool departed = prev.WasStationStop &&
                        ((!prev.LastVisitDeparted && t.LastVisitDeparted) || (prev.Speed == 0 && t.Speed > 0));
                    if (departed && ShouldAnnounce(announceCode, $"departed-{t.ActualVisitCount}", nowUtc))
                    {
                        bool hasNextMapVisit = IsMapVisit(t.NextStation);
                        _voice.Enqueue(new VoiceEngine.Announcement
                        {
                            Type = VoiceEngine.AnnouncementType.Departed,
                            TrainCode = announceCode,
                            Destination = dest,
                            NextStation = hasNextMapVisit ? StripEnglishPrefix(t.NextStation) : "",
                            NextPlatform = hasNextMapVisit ? t.Platform : 0,
                            NextStationNonStop = hasNextMapVisit && t.NextStationNonStop,
                            // 只使用插件按“本站计划发车时刻 - 游戏时钟”固定的结果。
                            // Train.Delay 会跨站累积，不能代表本次实际发车是否晚点。
                            DelayMinutes = GetDepartureDelayMinutes(t.LastDepartureScheduleDelaySec)
                        });
                        Console.WriteLine($"[Voice] 发车: {announceCode} 开往{dest}" +
                            (hasNextMapVisit ? $" -> {t.NextStation}{t.Platform}道" : "（出图/无下一站）"));
                    }
                }

                _prevStates[stateKey] = cur;
            }

            // 清理失效追踪状态（列车已完成/消失），下次该车号再出现时按新车处理
            if (_prevStates.Count > seenStateKeys.Count)
            {
                var stale = _prevStates.Keys.Where(k => !seenStateKeys.Contains(k)).ToList();
                foreach (var k in stale) _prevStates.Remove(k);
            }
        }

        private static string GetTrainStateKey(TrainData t)
        {
            return !string.IsNullOrEmpty(t.Id) ? $"id:{t.Id}" : $"name:{t.Name}";
        }

        private static bool IsStationStop(TrainData t)
        {
            return t.OnBoard && t.Speed == 0 && !string.IsNullOrEmpty(t.StopReasons) && t.StopReasons.Contains("Station");
        }

        private static bool IsMapVisit(string station)
        {
            return !string.IsNullOrWhiteSpace(station) &&
                !station.Contains("方向", StringComparison.Ordinal);
        }

        private static bool EnteredPrePassingWindow(TrainData train, TrainPrevState prev)
        {
            if (!train.NextStationNonStop || string.IsNullOrWhiteSpace(train.NextStation) ||
                !train.NextArrivalSec.HasValue)
                return false;

            double remaining = train.NextArrivalSec.Value;
            if (remaining <= 0 || remaining > PrePassingAnnouncementSeconds)
                return false;

            return !string.Equals(prev.NextStation, train.NextStation, StringComparison.Ordinal) ||
                !prev.NextArrivalSec.HasValue ||
                prev.NextArrivalSec.Value > PrePassingAnnouncementSeconds;
        }

        /// <summary>
        /// 发车播报采用本站计划时刻相对游戏时钟的实际晚点分钟数；不超过一分钟按正点处理。
        /// 计划绝对时刻不可用时返回 null，避免把累计 Train.Delay 误报为本次晚点。
        /// </summary>
        private static int? GetDepartureDelayMinutes(double? delaySeconds)
        {
            if (!delaySeconds.HasValue) return null;
            return delaySeconds.Value > 60.0
                ? Math.Max(1, (int)Math.Ceiling(delaySeconds.Value / 60.0))
                : 0;
        }

        /// <summary>
        /// 将有符号到站偏差换算为播报分钟。绝对值不超过一分钟按正点处理；
        /// 负数表示早点，正数表示晚点。
        /// </summary>
        private static int? GetScheduleDeviationMinutes(double? deviationSeconds)
        {
            if (!deviationSeconds.HasValue) return null;
            if (Math.Abs(deviationSeconds.Value) <= 60.0) return 0;

            int minutes = Math.Max(1, (int)Math.Floor(Math.Abs(deviationSeconds.Value) / 60.0));
            return deviationSeconds.Value < 0 ? -minutes : minutes;
        }

        /// <summary>只有计划访问序列的中间站才播报通过信息与发车前预告。</summary>
        private static bool IsIntermediateScheduledVisit(TrainData t)
        {
            return t.ScheduledVisitCount >= 3 &&
                t.ScheduledVisitIndex > 0 &&
                t.ScheduledVisitIndex < t.ScheduledVisitCount - 1;
        }

        /// <summary>只有中间停站的倒计时进入一分钟窗口时才发车预告。</summary>
        private static bool EnteredPreDepartureWindow(TrainData t, TrainPrevState prev)
        {
            if (!IsStationStop(t) || !IsIntermediateScheduledVisit(t) || !t.DepartureRemainingSec.HasValue)
                return false;

            double remaining = t.DepartureRemainingSec.Value;
            if (remaining <= 0 || remaining > PreDepartureAnnouncementSeconds)
                return false;

            return !prev.WasStationStop ||
                !prev.DepartureRemainingSec.HasValue ||
                prev.DepartureRemainingSec.Value > PreDepartureAnnouncementSeconds ||
                prev.ActualVisitCount != t.ActualVisitCount;
        }

        /// <summary>
        /// 查询车次终到站名（已去英文前缀）。查不到返回 null。
        /// </summary>
        private string LookupDestination(string trainCode)
        {
            if (string.IsNullOrEmpty(trainCode)) return null;
            if (_trainInfo.TryLookup(trainCode, out var info) && !string.IsNullOrEmpty(info.Destination))
                return StripEnglishPrefix(info.Destination);
            return null;
        }

        /// <summary>防重复：同车号+同类型在冷却时间内返回 false</summary>
        private bool ShouldAnnounce(string trainCode, string type, DateTime nowUtc)
        {
            string key = $"{trainCode}|{type}";
            if (_lastAnnounce.TryGetValue(key, out var last))
            {
                if ((nowUtc - last).TotalSeconds < AnnounceCooldownSec) return false;
            }
            _lastAnnounce[key] = nowUtc;
            return true;
        }

        /// <summary>
        /// 复合车次仅保留一行：首个计划停车站前显示第一段，到站及之后显示第二段。
        /// 原始对象仍在状态检测阶段使用，避免车号切换破坏连续状态追踪。
        /// </summary>
        private List<TrainData> ResolveActiveCompositeTrainCodes(List<TrainData> trains)
        {
            var result = new List<TrainData>(trains.Count);
            foreach (var t in trains)
            {
                string activeCode = GetActiveTrainCode(t);
                if (!string.Equals(activeCode, t.Name, StringComparison.Ordinal))
                {
                    var row = CloneTrain(t);
                    row.Name = activeCode;
                    result.Add(row);
                }
                else
                {
                    result.Add(t);
                }
            }
            return result;
        }

        private static string GetActiveTrainCode(TrainData train)
        {
            if (train == null) return null;
            bool secondLegActive = IsStationStop(train) ||
                (train.ActualVisitCount > 0 && !train.LastVisitNonStop);
            if (!secondLegActive && train.ScheduledVisitIndex >= 0 && train.ScheduledStops.Count > 0)
            {
                // 程序在换号站之后才启动时，最近一次访问可能已经变成后续通过站。
                // 回看已完成的计划访问，只要其中存在有停车时长的站点，就保持第二段车号。
                int lastVisitedIndex = Math.Min(train.ScheduledVisitIndex, train.ScheduledStops.Count - 1);
                secondLegActive = train.ScheduledStops
                    .Take(lastVisitedIndex + 1)
                    .Any(stop => stop.StopMinutes > 0);
            }
            return TrainCodeRules.SelectActiveCode(train.Name, secondLegActive);
        }

        private static TrainData CloneTrain(TrainData t)
        {
            return new TrainData
            {
                Id = t.Id, Name = t.Name, Speed = t.Speed, TargetSpeed = t.TargetSpeed, Delay = t.Delay,
                CanDepart = t.CanDepart, Finished = t.Finished, BrokenDown = t.BrokenDown,
                OnBoard = t.OnBoard, Waiting = t.Waiting, Lookahead = t.Lookahead, NeedsRoute = t.NeedsRoute,
                HasSignal = t.HasSignal, SignalState = t.SignalState,
                SignalAllocationState = t.SignalAllocationState, FrontAllocationState = t.FrontAllocationState,
                Platform = t.Platform, NextStation = t.NextStation, NextStationNonStop = t.NextStationNonStop,
                ActualVisitCount = t.ActualVisitCount, ScheduledVisitCount = t.ScheduledVisitCount, ScheduledVisitIndex = t.ScheduledVisitIndex, LastVisitStation = t.LastVisitStation,
                LastVisitPlatform = t.LastVisitPlatform, LastVisitNonStop = t.LastVisitNonStop,
                LastVisitStopMinutes = t.LastVisitStopMinutes, LastVisitDeparted = t.LastVisitDeparted,
                LastArrivalScheduleDeviationSec = t.LastArrivalScheduleDeviationSec,
                LastDepartureScheduleDelaySec = t.LastDepartureScheduleDelaySec,
                RequiresDirectionChange = t.RequiresDirectionChange,
                CurrentStation = t.CurrentStation, CurrentPlatform = t.CurrentPlatform,
                CurrentStopMinutes = t.CurrentStopMinutes, DepartureRemainingSec = t.DepartureRemainingSec,
                CurrentDepartureScheduleDelaySec = t.CurrentDepartureScheduleDelaySec,
                StopReasons = t.StopReasons,
                ScheduledStops = t.ScheduledStops.Select(stop => new ScheduledStopData
                {
                    Station = stop.Station,
                    Platform = stop.Platform,
                    ArrivalTimeSec = stop.ArrivalTimeSec,
                    DepartureTimeSec = stop.DepartureTimeSec,
                    StopMinutes = stop.StopMinutes,
                    RelativeTimes = stop.RelativeTimes,
                    NonStop = stop.NonStop
                }).ToList(),
                NextPrepareSec = t.NextPrepareSec, NextArrivalSec = t.NextArrivalSec, NotMovingSince = t.NotMovingSince,
                MapEntryTimeSec = t.MapEntryTimeSec, MapExitTimeSec = t.MapExitTimeSec,
                MapEntryStation = t.MapEntryStation, MapExitStation = t.MapExitStation,
                MapEntryPlatform = t.MapEntryPlatform, MapExitPlatform = t.MapExitPlatform,
                MapEntryNonStop = t.MapEntryNonStop, MapExitNonStop = t.MapExitNonStop
            };
        }

        /// <summary>
        /// 去掉站名开头的英文/数字前缀（含分隔符），只保留中文及之后的部分。
        /// 例如 "Nanjing南京站" -> "南京站"；"Station_01 北京南" -> "北京南"。
        /// 若站名中无中文字符，则原样返回。
        /// </summary>
        private static string StripEnglishPrefix(string station) =>
            StationNameFormatter.ChineseOrOriginal(station);

        private static string FormatDepartureStatus(TrainData t)
        {
            if (!t.DepartureRemainingSec.HasValue) return "距发车--";

            double remaining = Math.Max(0, t.DepartureRemainingSec.Value);
            if (remaining <= 0)
                return t.CanDepart ? "即将发车" : "发车时刻已到（待进路）";

            var ts = TimeSpan.FromSeconds(remaining);
            return ts.TotalMinutes >= 1
                ? $"还有{(int)ts.TotalMinutes}分{ts.Seconds}秒开车"
                : $"还有{ts.Seconds}秒开车";
        }

        private void UpdateTrainItem(ListViewItem item, TrainData t)
        {
            // 停站时以本站计划发车时刻和游戏时钟为准，避免展示跨站累积的游戏 Delay。
            // 运行中没有可比的本站发车时刻，继续显示游戏原始值作为运行诊断。
            var displayDelaySeconds = IsStationStop(t) && t.CurrentDepartureScheduleDelaySec.HasValue
                ? t.CurrentDepartureScheduleDelaySec.Value
                : t.Delay;
            var delayStr = displayDelaySeconds > 0 ? $"+{(int)displayDelaySeconds}s" :
                displayDelaySeconds < 0 ? $"{(int)displayDelaySeconds}s" : "";

            // 状态：停站状态 + 停车时长 + 本站发车倒计时
            var statusParts = new List<string>();
            if (t.Waiting) statusParts.Add("等待入图");
            if (t.BrokenDown) statusParts.Add("故障");
            if (t.Finished) statusParts.Add("完成");

            bool isStationStop = IsStationStop(t);
            bool isRunning = t.OnBoard && t.Speed > 0 && !t.BrokenDown && !t.Finished;

            if (isStationStop)
            {
                statusParts.Add("停站");
                if (t.RequiresDirectionChange) statusParts.Add("需调向");
                // 停车时长（NotMovingSince 现在直接是停车时长秒数）
                if (t.NotMovingSince.HasValue && t.NotMovingSince.Value > 0)
                {
                    var sts = TimeSpan.FromSeconds(t.NotMovingSince.Value);
                    statusParts.Add(sts.TotalMinutes >= 1
                        ? $"已停{(int)sts.TotalMinutes}分{sts.Seconds}秒"
                        : $"已停{sts.Seconds}秒");
                }
                // 当前 StationVisit.To - 游戏当前时间；绝不使用下一交路的 NextPrepareSec。
                statusParts.Add(FormatDepartureStatus(t));
            }
            else if (isRunning)
            {
                statusParts.Add("运行中");
            }
            else if (t.CanDepart)
            {
                statusParts.Add("可发车");
            }
            else if (t.OnBoard && t.Speed == 0 && !t.Finished)
            {
                statusParts.Add("停车");
                // 非到站停车也显示停车时长
                if (t.NotMovingSince.HasValue && t.NotMovingSince.Value > 0)
                {
                    var sts = TimeSpan.FromSeconds(t.NotMovingSince.Value);
                    statusParts.Add(sts.TotalMinutes >= 1
                        ? $"已停{(int)sts.TotalMinutes}分"
                        : $"已停{sts.Seconds}秒");
                }
            }

            var status = string.Join(" ", statusParts);

            // 信号显示：优先用 AllocationState 判断（1=Allocated 开放，0=Free 关闭），
            // 到站停车时信号可能是开放的（进路已排），不应一律显示关闭
            string signalStr;
            if (!t.OnBoard)
                signalStr = "";
            else if (!t.HasSignal)
                signalStr = "—";
            else if (t.SignalAllocationState == 1)
                signalStr = "开放";
            else if (t.SignalAllocationState == 0)
                signalStr = "关闭";
            else if (t.SignalAllocationState == 2)
                signalStr = "占用";
            else
                // 未能读到紧邻下一座信号时保守显示未知，绝不再用减速臆断“关闭”。
                signalStr = "未知";

            // 当前停站：仅在确实因 Station 停车时显示，优先使用当前访问数据，
            // 旧插件/短暂刷新缺字段时回退最近一次实际访问。
            string currentStopStr = "";
            if (isStationStop)
            {
                string currentStation = !string.IsNullOrEmpty(t.CurrentStation)
                    ? StripEnglishPrefix(t.CurrentStation)
                    : StripEnglishPrefix(t.LastVisitStation);
                int currentPlatform = t.CurrentPlatform > 0 ? t.CurrentPlatform : t.LastVisitPlatform;
                if (!string.IsNullOrEmpty(currentStation) && currentPlatform > 0)
                    currentStopStr = $"{currentStation} {currentPlatform}道";
                else if (!string.IsNullOrEmpty(currentStation))
                    currentStopStr = currentStation;
                else if (currentPlatform > 0)
                    currentStopStr = $"{currentPlatform}道";
            }

            // 前方停站（仅站名，去掉英文前缀）+ 站台号单独一列
            var stationStr = !string.IsNullOrEmpty(t.NextStation) ? StripEnglishPrefix(t.NextStation) : "";
            var platformStr = t.Platform > 0 ? $"{t.Platform}台" : "";

            // 始发/终到：复合车次已选出当前运行段；0 前缀只在查询层移除。
            var originStr = "";
            var destStr = "";
            if (_trainInfo.TryLookup(t.Name, out var info))
            {
                originStr = StripEnglishPrefix(info.Origin);
                destStr = StripEnglishPrefix(info.Destination);
            }

            item.Text = t.Name;
            item.SubItems[1].Text = originStr;
            item.SubItems[2].Text = destStr;
            item.SubItems[3].Text = $"{t.Speed}";
            item.SubItems[4].Text = delayStr;
            item.SubItems[5].Text = signalStr;
            item.SubItems[6].Text = status;
            item.SubItems[7].Text = currentStopStr;
            item.SubItems[8].Text = stationStr;
            item.SubItems[9].Text = platformStr;

            // 颜色
            item.BackColor = GetTrainBackColor(t.Name);

            // 到站停车是正常状态，不标红（即使速度为0/信号显示关闭）。
            // 颜色仅依据紧邻下一座信号的实际分配状态，未知状态不再以目标速度替代。
            bool signalActuallyBlocked = t.OnBoard && t.HasSignal &&
                t.SignalAllocationState >= 0 && t.SignalAllocationState != 1;

            if (t.BrokenDown)
                item.ForeColor = ColorCritical;
            else if (isStationStop)
            {
                // 正常到站停车：白色，不告警
                item.ForeColor = Color.White;
            }
            else if (signalActuallyBlocked && (t.Speed == 0 || t.Speed <= 10))
                item.ForeColor = ColorCritical;
            else if (signalActuallyBlocked)
                item.ForeColor = ColorWarning;
            else if (t.CanDepart)
                item.ForeColor = ColorWarning;
            else if (t.Finished)
                item.ForeColor = ColorDim;
            else if (t.Waiting || !t.OnBoard)
                item.ForeColor = Color.FromArgb(160, 160, 160);
            else
                item.ForeColor = Color.White;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _timer.Stop();
            _voice?.Dispose();
            _http.Dispose();
            base.OnFormClosing(e);
        }
    }

    public class AlertData { public string Level; public string TrainName; public string Message; }

    /// <summary>语音播报用的列车上一帧状态（用于检测状态变化）</summary>
    public struct TrainPrevState
    {
        public bool OnBoard;
        public bool Waiting;
        public int Speed;
        public bool WasStationStop;  // 上一帧是否为到站停车（用于判断发车）
        public int ActualVisitCount;
        public bool LastVisitDeparted;
        public double? DepartureRemainingSec;
        public double? NextArrivalSec;
        public string NextStation;
        public string PrePassingStation;
        public int PreDepartureAnnouncementVisitCount;
        public int DirectionChangeAnnouncementVisitCount;
    }

    public class TrainData
    {
        public string Id; public string Name; public int Speed; public float TargetSpeed; public double Delay;
        public bool CanDepart; public bool Finished; public bool BrokenDown;
        public bool OnBoard; public bool Waiting;
        public int Lookahead; public bool NeedsRoute;
        public bool HasSignal; public string SignalState;
        public int SignalAllocationState = -1;  // 紧邻下一信号: -1=未知 0=Free 1=Allocated 2=Occupied 3=Shunting
        public int FrontAllocationState = -1;   // 兼容旧 API；不参与信号告警判断
        public int Platform; public string NextStation; public bool NextStationNonStop;
        public int ActualVisitCount; public int ScheduledVisitCount; public int ScheduledVisitIndex = -1;
        public string LastVisitStation; public int LastVisitPlatform; public bool LastVisitNonStop;
        public int LastVisitStopMinutes; public bool LastVisitDeparted;
        // 最近一次到站相对计划到达时刻的有符号秒数：负数早点、正数晚点。
        public double? LastArrivalScheduleDeviationSec;
        // 本次刚发车时按游戏时钟固定的实际晚点秒数；null 表示插件无法取得绝对计划时刻。
        public double? LastDepartureScheduleDelaySec;
        public bool RequiresDirectionChange;
        public string CurrentStation; public int CurrentPlatform; public int CurrentStopMinutes;
        public double? DepartureRemainingSec;  // 当前停站距发车剩余秒数
        public double? CurrentDepartureScheduleDelaySec;  // 当前停站相对计划发车时刻的晚点秒数
        public string StopReasons;
        public List<ScheduledStopData> ScheduledStops = new();
        public double? NextPrepareSec;  // 下一交路准备剩余秒数（不用于当前停站倒计时）
        public double? NextArrivalSec;  // 距到达剩余秒数
        public double? NotMovingSince;  // 停车时长（秒）
        public double? MapEntryTimeSec;  // 列车进入地图的计划时刻（游戏内绝对秒数）
        public double? MapExitTimeSec;   // 列车离开地图的计划时刻（游戏内绝对秒数）
        public string MapEntryStation;   // 列车进入地图的站名
        public string MapExitStation;    // 列车离开地图的站名（游戏地图内终点站）
        public int MapEntryPlatform; public int MapExitPlatform;
        public bool MapEntryNonStop; public bool MapExitNonStop;
    }

    public class ScheduledStopData
        {
            public string Station;
            public int Platform;
            public double? ArrivalTimeSec;
            public double? DepartureTimeSec;
            public int StopMinutes;
            public bool RelativeTimes;
            public bool NonStop;
        }
    }

    /// <summary>补全 TTS 引擎与语速持久化：读写 %LOCALAPPDATA%\RailRouteAssistant\voice.json</summary>
    public static class VoiceSettings
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RailRouteAssistant");
        private static readonly string FilePath = Path.Combine(Dir, "voice.json");

        public sealed class Preferences
        {
            public string SelectedVoiceKey { get; set; }
            public int SpeechRate { get; set; } = RailRouteAssistantDesktop.VoiceEngine.DefaultSpeechRate;
        }

        public static Preferences Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new Preferences();
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                string key = doc.RootElement.TryGetProperty("selectedVoiceKey", out var voice) &&
                    voice.ValueKind == JsonValueKind.String ? voice.GetString() : null;
                int rate = doc.RootElement.TryGetProperty("speechRate", out var speed) &&
                    speed.ValueKind == JsonValueKind.Number && speed.TryGetInt32(out int value)
                        ? RailRouteAssistantDesktop.VoiceEngine.ClampSpeechRate(value)
                        : RailRouteAssistantDesktop.VoiceEngine.DefaultSpeechRate;
                return new Preferences { SelectedVoiceKey = key, SpeechRate = rate };
            }
            catch { return new Preferences(); }
        }

        public static void Save(string key, int speechRate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(new
                    {
                        selectedVoiceKey = key,
                        speechRate = RailRouteAssistantDesktop.VoiceEngine.ClampSpeechRate(speechRate)
                    }));
            }
            catch { /* 持久化失败不影响运行时切换 */ }
        }
    }
