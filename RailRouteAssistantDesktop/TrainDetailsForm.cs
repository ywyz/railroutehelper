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
            IReadOnlyList<ScheduledStopData> stops)
        {
            Text = $"{trainCode} 车次详情";
            Width = 760;
            Height = 500;
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
                Height = 86,
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
            var subtitle = new Label
            {
                Dock = DockStyle.Fill,
                Text = "当前游戏地图内停车站点",
                ForeColor = Color.Silver,
                TextAlign = ContentAlignment.MiddleLeft
            };
            header.Controls.Add(subtitle);
            header.Controls.Add(route);
            header.Controls.Add(title);

            var grid = CreateGrid();
            var stopList = stops ?? Array.Empty<ScheduledStopData>();
            int sequence = 1;
            foreach (var stop in stopList)
            {
                grid.Rows.Add(
                    sequence++,
                    StripEnglishPrefix(stop.Station),
                    stop.Platform > 0 ? $"{stop.Platform}道" : "--",
                    FormatScheduleTime(stop.ArrivalTimeSec, stop.RelativeTimes),
                    FormatScheduleTime(stop.DepartureTimeSec, stop.RelativeTimes),
                    FormatStopInterval(stop));
            }

            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 46,
                BackColor = ColorPanel,
                Padding = new Padding(12, 8, 12, 8)
            };
            bool hasRelativeTimes = stopList.Any(stop => stop.RelativeTimes);
            var note = new Label
            {
                Dock = DockStyle.Fill,
                Text = stopList.Count == 0
                    ? "游戏尚未提供该车次的计划停车表。"
                    : hasRelativeTimes
                        ? "“+”表示游戏提供的是相对时刻；“--”表示该项时刻未提供。"
                        : "“--”表示游戏未提供该项时刻。",
                ForeColor = stopList.Count == 0 ? Color.Orange : Color.Gray,
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
            closeButton.FlatAppearance.BorderColor = Color.FromArgb(80, 110, 140);
            footer.Controls.Add(note);
            footer.Controls.Add(closeButton);

            Controls.Add(grid);
            Controls.Add(footer);
            Controls.Add(header);
            AcceptButton = closeButton;
            CancelButton = closeButton;
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
