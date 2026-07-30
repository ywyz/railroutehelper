using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
        private Label _statusLabel;
        private Label _statsLabel;

        private List<AlertData> _alerts = new();
        private List<TrainData> _trains = new();
        private bool _gameReady = false;
        private string _gameTime = "";                  // 游戏内模拟时钟 HH:MM:SS
        private readonly HashSet<string> _selectedTrainNames = new();  // refresh 间保留的选中车号

        // ===== 语音播报 =====
        private VoiceEngine _voice;
        private CheckBox _muteCheck;                    // 静音开关
        // 状态追踪：原始车号（合并车号未拆分）→ 上一次状态。用于检测状态变化触发播报。
        private readonly Dictionary<string, TrainPrevState> _prevStates = new();
        // 防重复：(车号|播报类型) → 上次播报的 UTC 时间
        private readonly Dictionary<string, DateTime> _lastAnnounce = new();
        private const double AnnounceCooldownSec = 30.0;  // 同车号同类型 30 秒内不重复

        private static readonly Color ColorCritical = Color.FromArgb(220, 50, 50);
        private static readonly Color ColorWarning = Color.FromArgb(230, 150, 30);
        private static readonly Color ColorInfo = Color.FromArgb(50, 130, 220);
        private static readonly Color ColorBg = Color.FromArgb(30, 30, 35);
        private static readonly Color ColorPanel = Color.FromArgb(20, 20, 25);
        private static readonly Color ColorDim = Color.FromArgb(100, 100, 100);

        public MainForm()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            _trainInfo = new TrainInfoService(_http);
            SetupUI();
            // 初始化语音播报引擎（音频目录 = 输出目录/assets/audio）
            try
            {
                string audioDir = Path.Combine(AppContext.BaseDirectory, "assets", "audio");
                if (Directory.Exists(audioDir))
                    _voice = new VoiceEngine(audioDir);
                else
                    Console.WriteLine($"[Voice] 音频目录不存在: {audioDir}");
            }
            catch (Exception ex) { Console.WriteLine($"[Voice] 初始化失败: {ex.Message}"); }
            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += async (s, e) => await RefreshData();
            _timer.Start();
            // 后台加载车次信息（不阻塞 UI）
            _ = Task.Run(async () =>
            {
                await _trainInfo.LoadAsync();
                Console.WriteLine($"[TrainInfo] 加载完成: {_trainInfo.Count} 趟车次");
            });
        }

        private void SetupUI()
        {
            Text = "Rail Route 调度助手";
            Width = 540;
            Height = 700;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(50, 50);
            TopMost = true;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            Opacity = 0.95;
            BackColor = ColorBg;

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
                Text = "  所有列车",
                ForeColor = Color.LightSkyBlue, BackColor = ColorPanel,
                Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold)
            };

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
            Controls.Add(trainHeader);      // Top
            Controls.Add(_statusLabel);     // Top - index 最大，最先处理，最顶部

            // 静音开关浮在状态栏右侧
            _statusLabel.Controls.Add(_muteCheck);
            PositionMuteCheckbox();

            // 右键菜单 - 复制
            var copyMenu = new ContextMenuStrip();
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

            // 失去焦点时恢复置顶（避免被游戏窗口盖住）
            Deactivate += (s, e) =>
            {
                if (IsDisposed) return;
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;
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

        /// <summary>
        /// 在列车列表中查找指定车号并选中+滚动到可见
        /// </summary>
        private void SelectTrainInList(string trainName)
        {
            if (string.IsNullOrEmpty(trainName)) return;
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
                    foreach (var t in trainsEl.EnumerateArray())
                        _trains.Add(new TrainData
                        {
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
                            StopReasons = t.GetProperty("stopReasons").GetString() ?? "",
                            NextPrepareSec = t.TryGetProperty("nextPrepareSec", out var np) && np.ValueKind == JsonValueKind.Number ? np.GetDouble() : null,
                            NextArrivalSec = t.TryGetProperty("nextArrivalSec", out var na) && na.ValueKind == JsonValueKind.Number ? na.GetDouble() : null,
                            NotMovingSince = t.TryGetProperty("notMovingSince", out var nm) && nm.ValueKind == JsonValueKind.Number ? nm.GetDouble() : null,
                            SignalAllocationState = t.TryGetProperty("signalAllocationState", out var sa) && sa.ValueKind == JsonValueKind.Number ? sa.GetInt32() : -1,
                            FrontAllocationState = t.TryGetProperty("frontAllocationState", out var fa) && fa.ValueKind == JsonValueKind.Number ? fa.GetInt32() : -1
                        });

                // 语音播报：状态变化检测（用原始车号追踪，拆分前调用）
                DetectAndAnnounce();

                // 拆分合并车号（如 G4545G4546 → G4545 / G4546 两行）
                _trains = ExpandMergedTrains(_trains);

                UpdateUI();

                // 周期性维持置顶：游戏窗口偶尔会盖住本窗口，每秒检查一次
                if (!TopMost) TopMost = true;
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
            if (string.IsNullOrEmpty(name)) return ColorBg;
            char c = name[0];
            return c switch
            {
                'G' => Color.FromArgb(80, 30, 30),   // 高铁 - 暗红
                'D' => Color.FromArgb(20, 40, 70),   // 动车 - 暗蓝
                'C' when name.Length <= 4 => Color.FromArgb(20, 60, 30),   // 城际三字 - 暗绿
                'C' when name.Length >= 5 => Color.FromArgb(20, 50, 55),   // 城际四字 - 暗青
                'X' => Color.FromArgb(50, 30, 60),   // 行包/直达特快X - 暗紫
                'Z' => Color.FromArgb(20, 60, 30),   // 直达 - 暗绿
                'T' => Color.FromArgb(70, 50, 20),   // 特快 - 暗橙
                'K' => Color.FromArgb(60, 55, 20),   // 快速 - 暗黄
                'L' => Color.FromArgb(40, 40, 50),   // 临客 - 暗灰蓝
                'S' => Color.FromArgb(50, 30, 60),   // 市郊 - 暗紫
                _ => ColorBg
            };
        }

        /// <summary>
        /// 列车排序：在线运行 > 在线停车 > 等待入图 > 已完成
        /// </summary>
        private static int TrainSortPriority(TrainData t)
        {
            if (t.BrokenDown) return 0;          // 故障最优先
            if (t.OnBoard && t.Speed > 0) return 1;  // 运行中
            if (t.OnBoard && t.Speed == 0) return 2;  // 在线停车
            if (t.OnBoard) return 3;              // 在线其他
            if (t.Waiting) return 4;              // 等待入图
            if (t.Finished) return 6;             // 已完成
            return 5;                              // 其他
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
                var dbStr = _trainInfo.IsLoaded ? $"  |  车次库 {_trainInfo.Count}" : "  |  车次库加载中";
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

            // 列车列表 - 排序后重建（顺序会变化）；保留选中车号
            _trains.Sort((a, b) => TrainSortPriority(a).CompareTo(TrainSortPriority(b)));

            // 记录当前选中的车号，重建后恢复
            _selectedTrainNames.Clear();
            foreach (ListViewItem sel in _trainList.SelectedItems)
                if (!string.IsNullOrEmpty(sel.Text)) _selectedTrainNames.Add(sel.Text);

            _trainList.BeginUpdate();
            _trainList.Items.Clear();
            foreach (var t in _trains)
            {
                var item = CreateTrainItem(t);
                if (_selectedTrainNames.Contains(t.Name))
                {
                    item.Selected = true;
                }
                _trainList.Items.Add(item);
            }
            _trainList.EndUpdate();
        }

        private ListViewItem CreateTrainItem(TrainData t)
        {
            var item = new ListViewItem(t.Name);
            item.SubItems.Add(""); // 始发
            item.SubItems.Add(""); // 终到
            item.SubItems.Add(""); // km/h
            item.SubItems.Add(""); // 延误
            item.SubItems.Add(""); // 信号
            item.SubItems.Add(""); // 状态
            item.SubItems.Add(""); // 前方停站
            item.SubItems.Add(""); // 站台
            UpdateTrainItem(item, t);
            return item;
        }

        /// <summary>
        /// 检测合并车号（如 G3342G3343，两个车次拼在一起，需要中途换向）
        /// 格式：字母+数字+字母+数字
        /// </summary>
        private static bool IsMergedTrainNumber(string name)
        {
            return TrySplitMergedTrainNumber(name, out _, out _);
        }

        /// <summary>
        /// 尝试拆分合并车号。如 "G4545G4546" → ("G4545","G4546")。
        /// 标准格式：字母+数字+字母+数字（两个车次拼接）。
        /// </summary>
        private static bool TrySplitMergedTrainNumber(string name, out string part1, out string part2)
        {
            part1 = null; part2 = null;
            if (string.IsNullOrEmpty(name) || name.Length < 4) return false;
            var m = System.Text.RegularExpressions.Regex.Match(name, @"^([A-Za-z]\d+)([A-Za-z]\d+)$");
            if (!m.Success) return false;
            part1 = m.Groups[1].Value;
            part2 = m.Groups[2].Value;
            return true;
        }

        /// <summary>
        /// 语音播报：检测列车状态变化并触发播报。
        /// 在 RefreshData 中、ExpandMergedTrains 之前调用，用原始车号追踪状态。
        /// 触发点：
        ///   入图：OnBoard 由 false→true（首次见到不算，避免启动时全员播报）
        ///   停站：速度 >0→0 且停车原因含 Station
        ///   发车：速度 0→>0 且此前为停站状态
        /// 防重复：同车号+同类型 30 秒内不重复
        /// </summary>
        private void DetectAndAnnounce()
        {
            if (_voice == null) return;
            if (_muteCheck?.Checked == true) return;
            if (!_trainInfo.IsLoaded) return;  // 车次库未加载则不播报（缺终到站）

            var nowUtc = DateTime.UtcNow;
            // 本次刷新见到的原始车号集合，用于清理失效的追踪状态
            var seenNames = new HashSet<string>();

            foreach (var t in _trains)
            {
                if (string.IsNullOrEmpty(t.Name) || t.Name == "?") continue;
                seenNames.Add(t.Name);

                _prevStates.TryGetValue(t.Name, out var prev);
                bool hadPrev = _prevStates.ContainsKey(t.Name);

                bool curStationStop = !string.IsNullOrEmpty(t.StopReasons) && t.StopReasons.Contains("Station");
                var cur = new TrainPrevState
                {
                    OnBoard = t.OnBoard,
                    Speed = t.Speed,
                    WasStationStop = curStationStop
                };

                // 仅在已有上一帧状态时判断状态变化（避免启动时全员播报）
                if (hadPrev)
                {
                    // 拆分合并车号：播报用第一段车号
                    string announceCode = TrySplitMergedTrainNumber(t.Name, out var p1, out _) ? p1 : t.Name;

                    // 1. 入图：OnBoard false→true
                    if (!prev.OnBoard && t.OnBoard)
                    {
                        if (ShouldAnnounce(announceCode, "arriving", nowUtc))
                        {
                            string dest = LookupDestination(announceCode);
                            _voice.Enqueue(VoiceEngine.AnnouncementType.Arriving, announceCode, dest, "", 0, 0);
                            Console.WriteLine($"[Voice] 入图: {announceCode} 开往{dest}");
                        }
                    }
                    // 2. 停站：速度 >0→0 且为到站停车
                    else if (prev.Speed > 0 && t.Speed == 0 && curStationStop)
                    {
                        if (ShouldAnnounce(announceCode, "stopped", nowUtc))
                        {
                            string dest = LookupDestination(announceCode);
                            string station = StripEnglishPrefix(t.NextStation);
                            int platform = t.Platform;
                            _voice.Enqueue(VoiceEngine.AnnouncementType.StoppedAtStation, announceCode, dest, station, platform, 0);
                            Console.WriteLine($"[Voice] 停站: {announceCode} @ {station}{platform}台 开往{dest}");
                        }
                    }
                    // 3. 发车：速度 0→>0 且此前为停站
                    else if (prev.Speed == 0 && t.Speed > 0 && prev.WasStationStop)
                    {
                        if (ShouldAnnounce(announceCode, "departed", nowUtc))
                        {
                            string dest = LookupDestination(announceCode);
                            int delayMin = t.Delay > 0 ? (int)Math.Round(t.Delay / 60.0) : 0;
                            _voice.Enqueue(VoiceEngine.AnnouncementType.Departed, announceCode, dest, "", 0, delayMin);
                            Console.WriteLine($"[Voice] 发车: {announceCode} 开往{dest} 晚点{delayMin}分");
                        }
                    }
                }

                _prevStates[t.Name] = cur;
            }

            // 清理失效追踪状态（列车已完成/消失），下次该车号再出现时按新车处理
            if (_prevStates.Count > seenNames.Count)
            {
                var stale = _prevStates.Keys.Where(k => !seenNames.Contains(k)).ToList();
                foreach (var k in stale) _prevStates.Remove(k);
            }
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
        /// 拆分合并车号：将 "G4545G4546" 拆成两行 TrainData（各自车号），共享同一列车的实时运行状态。
        /// G4545 行：车号=G4545，始发=G4545始发，终到=G4546始发（换向站）
        /// G4546 行：车号=G4546，始发=G4546始发（换向站），终到=G4546终到
        /// 非合并车号原样返回。
        /// </summary>
        private List<TrainData> ExpandMergedTrains(List<TrainData> trains)
        {
            var result = new List<TrainData>(trains.Count);
            foreach (var t in trains)
            {
                if (TrySplitMergedTrainNumber(t.Name, out var p1, out var p2))
                {
                    // 拆成两行，复制全部运行状态
                    var row1 = CloneTrain(t);
                    row1.Name = p1;
                    var row2 = CloneTrain(t);
                    row2.Name = p2;
                    result.Add(row1);
                    result.Add(row2);
                }
                else
                {
                    result.Add(t);
                }
            }
            return result;
        }

        private static TrainData CloneTrain(TrainData t)
        {
            return new TrainData
            {
                Name = t.Name, Speed = t.Speed, TargetSpeed = t.TargetSpeed, Delay = t.Delay,
                CanDepart = t.CanDepart, Finished = t.Finished, BrokenDown = t.BrokenDown,
                OnBoard = t.OnBoard, Waiting = t.Waiting, Lookahead = t.Lookahead, NeedsRoute = t.NeedsRoute,
                HasSignal = t.HasSignal, SignalState = t.SignalState,
                SignalAllocationState = t.SignalAllocationState, FrontAllocationState = t.FrontAllocationState,
                Platform = t.Platform, NextStation = t.NextStation, StopReasons = t.StopReasons,
                NextPrepareSec = t.NextPrepareSec, NextArrivalSec = t.NextArrivalSec, NotMovingSince = t.NotMovingSince
            };
        }

        /// <summary>
        /// 去掉站名开头的英文/数字前缀（含分隔符），只保留中文及之后的部分。
        /// 例如 "Nanjing南京站" -> "南京站"；"Station_01 北京南" -> "北京南"。
        /// 若站名中无中文字符，则原样返回。
        /// </summary>
        private static string StripEnglishPrefix(string station)
        {
            if (string.IsNullOrEmpty(station)) return station;
            // 找第一个 CJK 统一汉字（\u4e00-\u9fff）的位置
            for (int i = 0; i < station.Length; i++)
            {
                char c = station[i];
                if (c >= '\u4e00' && c <= '\u9fff')
                {
                    return station.Substring(i);
                }
            }
            // 无中文，原样返回
            return station;
        }

        private void UpdateTrainItem(ListViewItem item, TrainData t)
        {
            var delayStr = t.Delay > 0 ? $"+{(int)t.Delay}s" : t.Delay < 0 ? $"{(int)t.Delay}s" : "";

            // 状态：停站状态 + 停车时长 + 发车倒计时
            var statusParts = new List<string>();
            if (t.Waiting) statusParts.Add("等待入图");
            if (t.BrokenDown) statusParts.Add("故障");
            if (t.Finished) statusParts.Add("完成");

            bool isStationStop = t.OnBoard && t.Speed == 0 && !string.IsNullOrEmpty(t.StopReasons) && t.StopReasons.Contains("Station");
            bool isRunning = t.OnBoard && t.Speed > 0 && !t.BrokenDown && !t.Finished;

            if (isStationStop)
            {
                statusParts.Add("停站");
                // 停车时长（NotMovingSince 现在直接是停车时长秒数）
                if (t.NotMovingSince.HasValue && t.NotMovingSince.Value > 0)
                {
                    var sts = TimeSpan.FromSeconds(t.NotMovingSince.Value);
                    statusParts.Add(sts.TotalMinutes >= 1
                        ? $"已停{(int)sts.TotalMinutes}分{sts.Seconds}秒"
                        : $"已停{sts.Seconds}秒");
                }
                // 显示发车倒计时（NextPrepareSec 为剩余秒数）
                if (t.NextPrepareSec.HasValue && t.NextPrepareSec.Value > 0)
                {
                    var ts = TimeSpan.FromSeconds(t.NextPrepareSec.Value);
                    if (ts.TotalMinutes >= 1)
                        statusParts.Add($"{(int)ts.TotalMinutes}分{ts.Seconds}秒发车");
                    else
                        statusParts.Add($"{ts.Seconds}秒发车");
                }
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
                signalStr = "无信号";
            else if (t.SignalAllocationState == 1)
                signalStr = "开放";
            else if (t.SignalAllocationState == 0)
                signalStr = "关闭";
            else if (t.SignalAllocationState == 2)
                signalStr = "占用";
            else
                // AllocationState 未知（-1），回退到目标速度判断
                signalStr = t.TargetSpeed > 0.5f ? "开放" : "关闭";

            // 前方停站（仅站名，去掉英文前缀）+ 站台号单独一列
            var stationStr = !string.IsNullOrEmpty(t.NextStation) ? StripEnglishPrefix(t.NextStation) : "";
            var platformStr = t.Platform > 0 ? $"{t.Platform}台" : "";

            // 始发/终到：从车次信息查询；合并车号已拆分，每行用各自车号查询
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
            item.SubItems[7].Text = stationStr;
            item.SubItems[8].Text = platformStr;

            // 颜色
            item.BackColor = GetTrainBackColor(t.Name);

            // 到站停车是正常状态，不标红（即使速度为0/信号显示关闭）
            // 信号关闭的判断改为基于实际 AllocationState==0(Free)，且非到站停车时才告警
            bool signalActuallyClosed = t.OnBoard && t.HasSignal && t.SignalAllocationState == 0;

            if (t.BrokenDown)
                item.ForeColor = ColorCritical;
            else if (isStationStop)
            {
                // 正常到站停车：白色，不告警
                item.ForeColor = Color.White;
            }
            else if (signalActuallyClosed && (t.Speed == 0 || t.Speed <= 10))
                item.ForeColor = ColorCritical;
            else if (signalActuallyClosed)
                item.ForeColor = ColorWarning;
            else if (t.OnBoard && t.Lookahead == 0 && t.Speed > 0)
                item.ForeColor = ColorCritical;
            else if (t.CanDepart && t.Lookahead == 0)
                item.ForeColor = ColorCritical;
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
        public int Speed;
        public bool WasStationStop;  // 上一帧是否为到站停车（用于判断发车）
    }

    public class TrainData
    {
        public string Name; public int Speed; public float TargetSpeed; public double Delay;
        public bool CanDepart; public bool Finished; public bool BrokenDown;
        public bool OnBoard; public bool Waiting;
        public int Lookahead; public bool NeedsRoute;
        public bool HasSignal; public string SignalState;
        public int SignalAllocationState = -1;  // 信号机 AllocationState: -1=未知 0=Free 1=Allocated 2=Occupied
        public int FrontAllocationState = -1;   // 前方轨道段 AllocationState
        public int Platform; public string NextStation; public string StopReasons;
        public double? NextPrepareSec;  // 距发车剩余秒数
        public double? NextArrivalSec;  // 距到达剩余秒数
        public double? NotMovingSince;  // 停车时长（秒）
    }
}
