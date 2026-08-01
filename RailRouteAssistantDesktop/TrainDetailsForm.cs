using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RailRouteAssistantDesktop
{
    /// <summary>列车详情只读弹窗：始发终到 + 当前游戏地图内的计划停车表。</summary>
    internal sealed class TrainDetailsForm : Form
    {
        private static readonly Color ColorBg = Color.FromArgb(30, 30, 35);
        private static readonly Color ColorPanel = Color.FromArgb(20, 20, 25);
        private static readonly Color ColorGridAlt = Color.FromArgb(38, 38, 44);

        public TrainDetailsForm(
            string trainCode,
            string origin,
            string destination,
            IReadOnlyList<ScheduledStopData> stops,
            OnlineTrainDetails onlineDetails)
        {
            Text = $"{trainCode} 车次详情";
            Width = 760;
            Height = 550;
            MinimumSize = new Size(650, 380);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = ColorBg;
            ForeColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F);

            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 112,
                BackColor = ColorPanel,
                Padding = new Padding(14, 8, 14, 6)
            };
            var title = new Label
            {
                Dock = DockStyle.Top,
                Height = 32,
                Text = trainCode,
                ForeColor = Color.LightSkyBlue,
                Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            var route = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                Text = $"始发站：{DisplayText(origin)}    →    终点站：{DisplayText(destination)}",
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 10F),
                TextAlign = ContentAlignment.MiddleLeft
            };
            var vehicleModels = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                Text = onlineDetails?.VehicleModels.Count > 0
                    ? $"12306 车型：{string.Join(" / ", onlineDetails.VehicleModels)}"
                    : "12306 车型：暂未提供",
                ForeColor = onlineDetails?.VehicleModels.Count > 0 ? Color.LightGreen : Color.Gray,
                TextAlign = ContentAlignment.MiddleLeft
            };
            var subtitle = new Label
            {
                Dock = DockStyle.Fill,
                Text = "游戏地图内计划停车表",
                ForeColor = Color.Silver,
                TextAlign = ContentAlignment.MiddleLeft
            };
            header.Controls.Add(subtitle);
            header.Controls.Add(vehicleModels);
            header.Controls.Add(route);
            header.Controls.Add(title);

            var grid = CreateGrid();
            var stopList = stops ?? Array.Empty<ScheduledStopData>();

            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                BackColor = ColorPanel,
                Padding = new Padding(12, 8, 12, 8)
            };
            var note = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            var closeButton = new Button
            {
                Dock = DockStyle.Right,
                Width = 82,
                Text = "关闭",
                BackColor = Color.FromArgb(55, 75, 95),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                DialogResult = DialogResult.OK
            };
            var toggleButton = new Button
            {
                Dock = DockStyle.Right,
                Width = 148,
                Text = "切换到 12306 全程",
                BackColor = Color.FromArgb(48, 88, 68),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            closeButton.FlatAppearance.BorderColor = Color.FromArgb(80, 110, 140);
            toggleButton.FlatAppearance.BorderColor = Color.FromArgb(75, 125, 95);
            footer.Controls.Add(note);
            footer.Controls.Add(closeButton);
            footer.Controls.Add(toggleButton);

            bool showingOnline = false;
            void ShowGameSchedule()
            {
                showingOnline = false;
                subtitle.Text = "游戏地图内计划停车表";
                toggleButton.Text = "切换到 12306 全程";
                grid.Columns[2].Visible = true;
                PopulateGameStops(grid, stopList);

                bool hasRelativeTimes = stopList.Any(stop => stop.RelativeTimes);
                note.Text = stopList.Count == 0
                    ? "游戏尚未提供该车次的计划停车表。"
                    : hasRelativeTimes
                        ? "“+”表示游戏相对时刻；“--”表示该项时刻未提供。"
                        : "“--”表示游戏未提供该项时刻。";
                note.ForeColor = stopList.Count == 0 ? Color.Orange : Color.Gray;
            }

            void ShowOnlineSchedule()
            {
                showingOnline = true;
                subtitle.Text = "12306 当日全程时刻表";
                toggleButton.Text = "切换到游戏时刻表";
                grid.Columns[2].Visible = false;
                PopulateOnlineStops(grid, onlineDetails?.Stops);

                if (onlineDetails?.Stops.Count > 0)
                {
                    note.Text = $"12306 运行图日期：{FormatServiceDate(onlineDetails.ServiceDate)}；时刻均为北京时间。";
                    note.ForeColor = Color.Gray;
                }
                else
                {
                    note.Text = "12306 暂未返回该车次当日的全程时刻表。";
                    note.ForeColor = Color.Orange;
                }
            }

            toggleButton.Click += (sender, args) =>
            {
                if (showingOnline) ShowGameSchedule();
                else ShowOnlineSchedule();
            };

            ShowGameSchedule();

            Controls.Add(grid);
            Controls.Add(footer);
            Controls.Add(header);
            AcceptButton = closeButton;
            CancelButton = closeButton;
        }

        private static void PopulateGameStops(
            DataGridView grid,
            IReadOnlyList<ScheduledStopData> stops)
        {
            grid.Rows.Clear();
            int sequence = 1;
            foreach (var stop in stops)
            {
                grid.Rows.Add(
                    sequence++,
                    StripEnglishPrefix(stop.Station),
                    stop.Platform > 0 ? $"{stop.Platform}道" : "--",
                    FormatScheduleTime(stop.ArrivalTimeSec, stop.RelativeTimes),
                    FormatScheduleTime(stop.DepartureTimeSec, stop.RelativeTimes),
                    FormatStopInterval(stop));
            }
        }

        private static void PopulateOnlineStops(
            DataGridView grid,
            IReadOnlyList<OnlineTrainStop> stops)
        {
            grid.Rows.Clear();
            if (stops == null) return;

            for (int index = 0; index < stops.Count; index++)
            {
                var stop = stops[index];
                grid.Rows.Add(
                    ParseSequence(stop.StationNumber, index + 1),
                    StripEnglishPrefix(stop.StationName),
                    "--",
                    FormatOnlineTime(stop.ArrivalTime, stop.DayDifference),
                    FormatOnlineTime(stop.DepartureTime, stop.DayDifference),
                    FormatOnlineStopover(stop.StopoverMinutes, index, stops.Count));
            }
        }

        private static DataGridView CreateGrid()
        {
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false,
                BackgroundColor = ColorBg,
                BorderStyle = BorderStyle.None,
                GridColor = Color.FromArgb(65, 65, 72),
                EnableHeadersVisualStyles = false,
                ColumnHeadersHeight = 34,
                RowTemplate = { Height = 30 },
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = ColorBg,
                    ForeColor = Color.White,
                    SelectionBackColor = Color.FromArgb(55, 90, 125),
                    SelectionForeColor = Color.White,
                    Alignment = DataGridViewContentAlignment.MiddleCenter
                },
                AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = ColorGridAlt,
                    ForeColor = Color.White,
                    SelectionBackColor = Color.FromArgb(55, 90, 125),
                    SelectionForeColor = Color.White
                },
                ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = ColorPanel,
                    ForeColor = Color.LightSkyBlue,
                    Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                    Alignment = DataGridViewContentAlignment.MiddleCenter
                }
            };

            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "序号", FillWeight = 45 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "停车站点", FillWeight = 145 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "站台", FillWeight = 65 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "到站时间", FillWeight = 95 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "发车时间", FillWeight = 95 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "停车间隔", FillWeight = 90 });
            return grid;
        }

        private static string FormatScheduleTime(double? seconds, bool relative)
        {
            if (!seconds.HasValue || seconds.Value < 0) return "--";

            var time = TimeSpan.FromSeconds(seconds.Value);
            int hours = relative ? (int)time.TotalHours : time.Hours;
            string value = $"{hours:00}:{time.Minutes:00}:{time.Seconds:00}";
            return relative ? "+" + value : value;
        }

        private static string FormatStopInterval(ScheduledStopData stop)
        {
            if (stop.StopMinutes > 0) return $"{stop.StopMinutes}分";
            if (!stop.ArrivalTimeSec.HasValue || !stop.DepartureTimeSec.HasValue) return "--";

            double interval = stop.DepartureTimeSec.Value - stop.ArrivalTimeSec.Value;
            if (interval < 0) return "--";
            int totalSeconds = (int)Math.Round(interval, MidpointRounding.AwayFromZero);
            if (totalSeconds < 60) return $"{totalSeconds}秒";
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;
            return seconds == 0 ? $"{minutes}分" : $"{minutes}分{seconds}秒";
        }

        private static string FormatOnlineTime(string value, int dayDifference)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "----" || value == "--") return "--";
            value = value.Trim();
            if (value.Length == 4 && value.All(char.IsDigit))
                value = value.Substring(0, 2) + ":" + value.Substring(2, 2);
            string suffix = dayDifference > 0 ? $" (+{dayDifference}日)" : string.Empty;
            return value + suffix;
        }

        private static string FormatOnlineStopover(string value, int index, int count)
        {
            if (index == 0) return "始发";
            if (index == count - 1) return "终到";
            if (string.IsNullOrWhiteSpace(value) || value == "----" || value == "--") return "--";
            value = value.Trim();
            return int.TryParse(value, out int minutes) ? $"{minutes}分" : value;
        }

        private static int ParseSequence(string value, int fallback) =>
            int.TryParse(value, out int sequence) ? sequence : fallback;

        private static string FormatServiceDate(string value)
        {
            return value?.Length == 8
                ? $"{value.Substring(0, 4)}-{value.Substring(4, 2)}-{value.Substring(6, 2)}"
                : value ?? "未知";
        }

        private static string DisplayText(string value) =>
            string.IsNullOrWhiteSpace(value) ? "未知" : value;

        private static string StripEnglishPrefix(string station)
        {
            if (string.IsNullOrEmpty(station)) return "--";
            for (int i = 0; i < station.Length; i++)
            {
                char c = station[i];
                if (c >= '\u4e00' && c <= '\u9fff') return station.Substring(i);
            }
            return station;
        }
    }
}
