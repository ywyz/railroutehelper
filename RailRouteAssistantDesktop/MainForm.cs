using System;
using System.Collections.Generic;
using System.Drawing;
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

        private ListView _alertList;
        private ListView _trainList;
        private Label _statusLabel;
        private Label _statsLabel;

        private List<AlertData> _alerts = new();
        private List<TrainData> _trains = new();
        private bool _gameReady = false;

        private static readonly Color ColorCritical = Color.FromArgb(220, 50, 50);
        private static readonly Color ColorWarning = Color.FromArgb(230, 150, 30);
        private static readonly Color ColorInfo = Color.FromArgb(50, 130, 220);
        private static readonly Color ColorBg = Color.FromArgb(30, 30, 35);
        private static readonly Color ColorPanel = Color.FromArgb(20, 20, 25);
        private static readonly Color ColorDim = Color.FromArgb(100, 100, 100);

        public MainForm()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            SetupUI();
            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += async (s, e) => await RefreshData();
            _timer.Start();
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

            // WinForms Dock 规则：后添加的控件先 Dock
            // 所以添加顺序：Fill -> Top -> Top -> Bottom -> Top
            // 最终效果从上到下：statusLabel, alertList(Top), trainList(Fill), statsLabel(Bottom)

            // 1. trainList - Dock=Fill（最先添加，最后 Dock，填充剩余空间）
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
            _trainList.Columns.Add("车号", 60);
            _trainList.Columns.Add("km/h", 40);
            _trainList.Columns.Add("延误", 45);
            _trainList.Columns.Add("信号", 80);
            _trainList.Columns.Add("状态", 80);
            _trainList.Columns.Add("前方停站", 100);
            _trainList.Columns.Add("站台", 45);
            Controls.Add(_trainList);

            // 2. trainHeader - Dock=Top
            var trainHeader = new Label
            {
                Dock = DockStyle.Top, Height = 22,
                Text = "  所有列车",
                ForeColor = Color.LightSkyBlue, BackColor = ColorPanel,
                Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold)
            };
            Controls.Add(trainHeader);

            // 3. alertList - Dock=Top（固定高度）
            _alertList = new ListView
            {
                Dock = DockStyle.Top, Height = 250,
                View = View.Details,
                FullRowSelect = true,
                BackColor = ColorBg,
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9F),
                HeaderStyle = ColumnHeaderStyle.None
            };
            _alertList.Columns.Add("告警", 500);
            Controls.Add(_alertList);

            // 4. alertHeader - Dock=Top
            var alertHeader = new Label
            {
                Dock = DockStyle.Top, Height = 22,
                Text = "  告警信息（按紧急程度排序）",
                ForeColor = Color.LightSkyBlue, BackColor = ColorPanel,
                Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold)
            };
            Controls.Add(alertHeader);

            // 5. statusLabel - Dock=Top
            _statusLabel = new Label
            {
                Dock = DockStyle.Top, Height = 24,
                Text = "  正在连接游戏...",
                ForeColor = Color.Gray, BackColor = ColorPanel,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
            };
            Controls.Add(_statusLabel);

            // 6. statsLabel - Dock=Bottom
            _statsLabel = new Label
            {
                Dock = DockStyle.Bottom, Height = 20,
                ForeColor = Color.DimGray, BackColor = ColorPanel,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, 0, 8, 0),
                Font = new Font("Microsoft YaHei UI", 8F)
            };
            Controls.Add(_statsLabel);

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
        }

        private void CopySelectedToClipboard()
        {
            var list = _trainList.Focused ? _trainList : _alertList;
            if (list.SelectedItems.Count == 0) return;

            var sb = new StringBuilder();
            foreach (ListViewItem item in list.SelectedItems)
            {
                var parts = new List<string>();
                foreach (ListViewItem.ListViewSubItem sub in item.SubItems)
                    parts.Add(sub.Text);
                sb.AppendLine(string.Join("\t", parts));
            }
            Clipboard.SetText(sb.ToString());
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
                            StopReasons = t.GetProperty("stopReasons").GetString() ?? ""
                        });

                UpdateUI();
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
                _statusLabel.Text = $"  已连接  |  在线 {onBoard}  等待 {waiting}  总计 {_trains.Count}";
                _statusLabel.ForeColor = Color.LightGreen;
            }

            // 统计
            int crit = _alerts.FindAll(a => a.Level == "critical").Count;
            int warn = _alerts.FindAll(a => a.Level == "warning").Count;
            int info = _alerts.FindAll(a => a.Level == "info").Count;
            _statsLabel.Text = $"  紧急 {crit}   警告 {warn}   信息 {info}   ";

            // 告警列表
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
                _alertList.Items.Add(item);
            }
            if (_alerts.Count == 0)
            {
                var item = new ListViewItem("  暂无告警") { ForeColor = ColorDim };
                _alertList.Items.Add(item);
            }
            _alertList.EndUpdate();

            // 列车列表 - 排序后重建（顺序会变化）
            _trains.Sort((a, b) => TrainSortPriority(a).CompareTo(TrainSortPriority(b)));
            _trainList.BeginUpdate();
            _trainList.Items.Clear();
            foreach (var t in _trains)
                _trainList.Items.Add(CreateTrainItem(t));
            _trainList.EndUpdate();
        }

        private ListViewItem CreateTrainItem(TrainData t)
        {
            var item = new ListViewItem(t.Name);
            item.SubItems.Add("");
            item.SubItems.Add("");
            item.SubItems.Add("");
            item.SubItems.Add("");
            item.SubItems.Add("");
            item.SubItems.Add("");
            UpdateTrainItem(item, t);
            return item;
        }

        private void UpdateTrainItem(ListViewItem item, TrainData t)
        {
            var delayStr = t.Delay > 0 ? $"+{(int)t.Delay}s" : t.Delay < 0 ? $"{(int)t.Delay}s" : "";

            var statusParts = new List<string>();
            if (t.Waiting) statusParts.Add("等待入图");
            if (t.BrokenDown) statusParts.Add("故障");
            if (t.CanDepart) statusParts.Add("可发车");
            if (t.Finished) statusParts.Add("完成");
            if (t.OnBoard && t.Speed == 0 && !t.CanDepart && !t.Finished) statusParts.Add("停车");
            var status = string.Join(" ", statusParts);

            var signalStr = !t.OnBoard ? "" :
                t.TargetSpeed > 0.5f ? "开放" :
                t.HasSignal ? "关闭" : "无信号";

            // 前方停站（仅站名）+ 站台号单独一列
            var stationStr = !string.IsNullOrEmpty(t.NextStation) ? t.NextStation : "";
            var platformStr = t.Platform > 0 ? $"{t.Platform}台" : "";

            item.Text = t.Name;
            item.SubItems[1].Text = $"{t.Speed}";
            item.SubItems[2].Text = delayStr;
            item.SubItems[3].Text = signalStr;
            item.SubItems[4].Text = status;
            item.SubItems[5].Text = stationStr;
            item.SubItems[6].Text = platformStr;

            // 颜色
            item.BackColor = GetTrainBackColor(t.Name);

            bool signalClosed = t.OnBoard && t.TargetSpeed <= 0.5f;

            if (t.BrokenDown)
                item.ForeColor = ColorCritical;
            else if (signalClosed && t.OnBoard && (t.Speed == 0 || t.Speed <= 10))
                item.ForeColor = ColorCritical;
            else if (signalClosed && t.OnBoard)
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
            _http.Dispose();
            base.OnFormClosing(e);
        }
    }

    public class AlertData { public string Level; public string TrainName; public string Message; }

    public class TrainData
    {
        public string Name; public int Speed; public float TargetSpeed; public double Delay;
        public bool CanDepart; public bool Finished; public bool BrokenDown;
        public bool OnBoard; public bool Waiting;
        public int Lookahead; public bool NeedsRoute;
        public bool HasSignal; public string SignalState;
        public int Platform; public string NextStation; public string StopReasons;
    }
}
