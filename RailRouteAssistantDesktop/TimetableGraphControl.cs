using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using RailRouteHelper.AssistantSessions;

namespace RailRouteAssistantDesktop;

/// <summary>
/// Lightweight owner-drawn time-distance graph.  It deliberately consumes the
/// existing TrainData/ScheduledStopData DTOs, so the desktop does not duplicate
/// timetable or lifecycle calculations from the core assistant.
/// </summary>
public sealed class TimetableGraphControl : Control
{
    private readonly List<TrainData> _trains = new();
    private TimetableGraphSnapshot _coreSnapshot;
    private readonly HashSet<string> _alertTrains = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolTip _toolTip = new();
    private string _referenceTrain;
    private string _gameTime;
    private double? _gameTimeSeconds;
    private float _zoom = 1f;
    private float _panX;
    private float _panY;
    private Point _lastMouse;
    private bool _panning;
    private string _hoverTrain;
    private int _hoverStop = -1;

    public TimetableGraphControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                  ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(18, 22, 27);
        ForeColor = Color.Gainsboro;
        TabStop = true;
        MouseWheel += HandleMouseWheel;
        MouseDown += HandleMouseDown;
        MouseMove += HandleMouseMove;
        MouseUp += HandleMouseUp;
        MouseClick += HandleMouseClick;
    }

    public event EventHandler<string> TrainSelected;

    public string ReferenceTrainName => _referenceTrain;

    public void SetData(IEnumerable<TrainData> trains, string gameTime, IEnumerable<AlertData> alerts = null)
    {
        _coreSnapshot = null;
        _gameTimeSeconds = null;
        _trains.Clear();
        if (trains != null)
            _trains.AddRange(trains.Where(t => t != null));

        _gameTime = gameTime ?? string.Empty;
        _alertTrains.Clear();
        if (alerts != null)
        {
            foreach (var alert in alerts)
            {
                if (alert == null || string.IsNullOrWhiteSpace(alert.TrainName)) continue;
                foreach (var part in alert.TrainName.Split('/'))
                    _alertTrains.Add(part.Trim());
            }
        }

        if (!string.IsNullOrWhiteSpace(_referenceTrain) &&
            !_trains.Any(t => string.Equals(t.Name, _referenceTrain, StringComparison.OrdinalIgnoreCase)))
            _referenceTrain = null;
        Invalidate();
    }

    /// <summary>Render the shared AssistantSessions projector output.  This is
    /// preferred for live/replay data because the core has already accumulated
    /// each station's actual and predicted timestamps.</summary>
    public void SetCoreData(TimetableGraphSnapshot snapshot, string gameTime, double? gameTimeSeconds = null, IEnumerable<AlertData> alerts = null)
    {
        _coreSnapshot = snapshot;
        _gameTime = gameTime ?? string.Empty;
        _gameTimeSeconds = gameTimeSeconds;
        _alertTrains.Clear();
        if (alerts != null)
        {
            foreach (var alert in alerts ?? Array.Empty<AlertData>())
            {
                if (alert == null || string.IsNullOrWhiteSpace(alert.TrainName)) continue;
                foreach (var part in alert.TrainName.Split('/')) _alertTrains.Add(part.Trim());
            }
        }
        Invalidate();
    }

    public void SetReferenceTrain(string trainName)
    {
        _referenceTrain = string.IsNullOrWhiteSpace(trainName) ? null : trainName.Trim();
        _panX = 0;
        _panY = 0;
        Invalidate();
    }

    public void ResetView()
    {
        _zoom = 1f;
        _panX = 0;
        _panY = 0;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(BackColor);

        if (_coreSnapshot != null && _coreSnapshot.Corridor.Count > 0)
        {
            DrawCoreSnapshot(e.Graphics, _coreSnapshot);
            return;
        }

        var rows = BuildRows();
        if (rows.Count == 0)
        {
            using var emptyBrush = new SolidBrush(Color.FromArgb(140, ForeColor));
            e.Graphics.DrawString("选择一趟列车后生成走廊运行图（RelativeTimes 将保守显示）",
                Font, emptyBrush, new PointF(20, 20));
            return;
        }

        var times = GetTimeBounds();
        var plot = new RectangleF(92, 26, Math.Max(80, ClientSize.Width - 112), Math.Max(80, ClientSize.Height - 54));
        DrawGrid(e.Graphics, plot, rows, times.min, times.max);

        foreach (var train in _trains)
            DrawTrain(e.Graphics, plot, rows, times.min, times.max, train);

        DrawCurrentCursor(e.Graphics, plot, times.min, times.max);

        if (!string.IsNullOrEmpty(_hoverTrain))
        {
            using var brush = new SolidBrush(Color.FromArgb(230, 30, 35, 42));
            var text = _hoverStop >= 0 ? $"{_hoverTrain}  ·  {_hoverStop + 1}站" : _hoverTrain;
            var size = e.Graphics.MeasureString(text, Font);
            e.Graphics.FillRectangle(brush, 8, ClientSize.Height - size.Height - 8, size.Width + 10, size.Height + 4);
            e.Graphics.DrawString(text, Font, Brushes.White, 13, ClientSize.Height - size.Height - 6);
        }
    }

    private void DrawCoreSnapshot(Graphics g, TimetableGraphSnapshot snapshot)
    {
        var rows = snapshot.Corridor.Select(station => station.StationName).ToList();
        var absolute = snapshot.Trains.SelectMany(train => train.Points)
            .Where(point => point.AbsoluteTimeUtc.HasValue)
            .Select(point => point.AbsoluteTimeUtc.Value.ToUnixTimeMilliseconds() / 1000d)
            .ToList();
        double current = DateTimeOffset.UnixEpoch.AddSeconds(_gameTimeSeconds ?? ParseClock(_gameTime)).ToUnixTimeMilliseconds() / 1000d;
        if (absolute.Count == 0)
        {
            absolute.Add(current);
            absolute.Add(current + 3600);
        }
        double min = absolute.Min();
        double max = absolute.Max();
        if (max - min < 3600) { min -= 1800; max += 1800; }
        var plot = new RectangleF(92, 26, Math.Max(80, ClientSize.Width - 112), Math.Max(80, ClientSize.Height - 54));
        DrawGrid(g, plot, rows, min, max);

        foreach (var train in snapshot.Trains)
        {
            var color = TrainColor(train.TrainId);
            bool selected = string.Equals(train.TrainId, snapshot.SelectedTrainId, StringComparison.OrdinalIgnoreCase);
            DrawCoreSeries(g, plot, rows, min, max, train,
                new[] { TimetablePointKind.PlannedArrival, TimetablePointKind.PlannedDeparture },
                Color.FromArgb(selected ? 185 : 110, Color.LightGray), false, selected ? 2.1f : 1f, includeLastActual: false);
            DrawCoreSeries(g, plot, rows, min, max, train,
                new[] { TimetablePointKind.ActualArrival, TimetablePointKind.ActualDeparture },
                color, false, selected ? 3.2f : 2f, includeLastActual: false);
            DrawCoreSeries(g, plot, rows, min, max, train,
                new[] { TimetablePointKind.PredictedArrival, TimetablePointKind.PredictedDeparture },
                color, true, selected ? 2.2f : 1.3f, includeLastActual: true);
            DrawCoreMarkers(g, plot, rows, min, max, train, color);
        }

        if (absolute.Count > 0 && current > min && current < max)
        {
            float x = CoreTimeX(plot, current, min, max);
            using var pen = new Pen(Color.Cyan, 1.4f) { DashStyle = DashStyle.Dot };
            g.DrawLine(pen, x, plot.Top, x, plot.Bottom);
            g.DrawString("现在", Font, Brushes.Cyan, x + 3, plot.Top + 2);
        }
        using var titleBrush = new SolidBrush(Color.FromArgb(160, 180, 188));
        g.DrawString("计划灰线   实际彩线   预测虚线   ◆调向  !告警", Font, titleBrush, plot.Left, ClientSize.Height - 25);
    }

    private void DrawCoreSeries(Graphics g, RectangleF plot, IReadOnlyList<string> rows, double min, double max,
        TrainCorridor train, IReadOnlyCollection<TimetablePointKind> kinds, Color color, bool dashed, float width, bool includeLastActual)
    {
        var selected = train.Points.Where(point => kinds.Contains(point.Kind) && point.AbsoluteTimeUtc.HasValue);
        if (includeLastActual)
        {
            var actualPoints = train.Points.Where(point =>
                    (point.Kind == TimetablePointKind.ActualArrival || point.Kind == TimetablePointKind.ActualDeparture)
                    && point.AbsoluteTimeUtc.HasValue)
                .OrderBy(point => point.AbsoluteTimeUtc)
                .ToList();
            var lastActual = actualPoints.LastOrDefault();
            if (lastActual != null && lastActual.AbsoluteTimeUtc.HasValue)
                selected = selected.Where(point => point.AbsoluteTimeUtc > lastActual.AbsoluteTimeUtc);
            if (lastActual != null) selected = selected.Append(lastActual);
        }
        var points = selected
            .OrderBy(point => point.AbsoluteTimeUtc)
            .ThenBy(point => point.CorridorIndex)
            .Select(point => new PointF(CoreTimeX(plot, point.AbsoluteTimeUtc.Value.ToUnixTimeMilliseconds() / 1000d, min, max),
                RowY(plot, point.CorridorIndex, rows.Count)))
            .ToArray();
        using var pen = new Pen(color, width);
        if (dashed) pen.DashStyle = DashStyle.Dash;
        if (points.Length >= 2)
            g.DrawLines(pen, points);
        else if (dashed && points.Length == 1)
            // A single next-arrival prediction remains visible as a short
            // dashed stub; it is not mistaken for a full route segment.
            g.DrawLine(pen, points[0].X - 7, points[0].Y, points[0].X + 7, points[0].Y);
    }

    private void DrawCoreMarkers(Graphics g, RectangleF plot, IReadOnlyList<string> rows, double min, double max,
        TrainCorridor train, Color color)
    {
        foreach (var point in train.Points.Where(item => item.Kind == TimetablePointKind.PlannedArrival ||
                                                          item.Kind == TimetablePointKind.ActualArrival ||
                                                          item.Kind == TimetablePointKind.PredictedArrival))
        {
            float y = RowY(plot, point.CorridorIndex, rows.Count);
            if (!point.AbsoluteTimeUtc.HasValue)
            {
                using var relativePen = new Pen(Color.Goldenrod, 1.2f);
                g.DrawEllipse(relativePen, plot.Left + 2, y - 4, 8, 8);
                g.DrawString("≈", Font, Brushes.Goldenrod, plot.Left + 11, y - Font.Height / 2f);
                continue;
            }
            float x = CoreTimeX(plot, point.AbsoluteTimeUtc.Value.ToUnixTimeMilliseconds() / 1000d, min, max);
            bool alert = _alertTrains.Contains(train.TrainId);
            using var brush = new SolidBrush(alert ? Color.OrangeRed : color);
            if (point.Kind == TimetablePointKind.PredictedArrival)
                g.DrawEllipse(Pens.Goldenrod, x - 3, y - 3, 6, 6);
            else if (point.Kind == TimetablePointKind.PlannedArrival)
                g.DrawRectangle(Pens.LightGray, x - 3, y - 3, 6, 6);
            else
                g.FillEllipse(brush, x - 3, y - 3, 6, 6);
            if (alert) g.DrawString("!", Font, Brushes.OrangeRed, x + 5, y - Font.Height / 2f);
        }
        // Direction is part of the shared corridor projector.  A tiny arrow at
        // each segment communicates reversal/turnback without inventing a stop.
        if (train.Direction < 0 && train.Points.Count > 1)
        {
            var point = train.Points.FirstOrDefault(item => item.AbsoluteTimeUtc.HasValue);
            if (point?.AbsoluteTimeUtc is { } at)
            {
                float x = CoreTimeX(plot, at.ToUnixTimeMilliseconds() / 1000d, min, max);
                float y = RowY(plot, point.CorridorIndex, rows.Count);
                using var pen = new Pen(Color.Gold, 2f);
                g.DrawLine(pen, x - 5, y - 5, x + 5, y + 5);
                g.DrawLine(pen, x + 5, y - 5, x - 5, y + 5);
            }
        }
    }

    private float CoreTimeX(RectangleF plot, double value, double min, double max)
        => plot.Left + (float)((value - min) / Math.Max(1, max - min) * plot.Width) * _zoom + _panX;

    private List<string> BuildRows()
    {
        var baseline = _trains.FirstOrDefault(t => string.Equals(t.Name, _referenceTrain, StringComparison.OrdinalIgnoreCase))
                       ?? _trains.FirstOrDefault(t => t.ScheduledStops.Count > 0);
        var rows = baseline?.ScheduledStops
            .Where(s => !string.IsNullOrWhiteSpace(s.Station))
            .Select(s => s.Station)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        if (rows.Count == 0)
        {
            rows = _trains.SelectMany(t => t.ScheduledStops)
                .Where(s => !string.IsNullOrWhiteSpace(s.Station))
                .Select(s => s.Station)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        return rows;
    }

    private (double min, double max) GetTimeBounds()
    {
        var values = _trains.SelectMany(t => t.ScheduledStops)
            .Where(s => !s.RelativeTimes)
            .SelectMany(s => new[] { s.ArrivalTimeSec, s.DepartureTimeSec })
            .Where(v => v.HasValue)
            .Select(v => v.Value)
            .ToList();
        if (values.Count == 0)
        {
            var now = ParseClock(_gameTime);
            return (now - 1800, now + 7200);
        }
        var min = values.Min();
        var max = values.Max();
        var span = Math.Max(3600, max - min);
        return (min - Math.Min(1800, span * .1), max + Math.Min(1800, span * .1));
    }

    private void DrawGrid(Graphics g, RectangleF plot, IReadOnlyList<string> rows, double min, double max)
    {
        using var gridPen = new Pen(Color.FromArgb(45, 65, 75));
        using var axisPen = new Pen(Color.FromArgb(120, 145, 155));
        using var textBrush = new SolidBrush(Color.FromArgb(190, 210, 218));
        int rowCount = Math.Max(1, rows.Count);
        for (int i = 0; i < rowCount; i++)
        {
            float y = RowY(plot, i, rowCount);
            g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            if (i < rows.Count)
                g.DrawString(TrimStation(rows[i]), Font, textBrush, 6, y - Font.Height / 2f);
        }
        g.DrawRectangle(axisPen, plot.X, plot.Y, plot.Width, plot.Height);

        double step = NiceTimeStep((max - min) / 8);
        double first = Math.Ceiling(min / step) * step;
        for (double t = first; t <= max; t += step)
        {
            float x = TimeX(plot, t, min, max);
            g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
            g.DrawString(FormatClock(t), Font, textBrush, x - 22, 5);
        }
        using var titleBrush = new SolidBrush(Color.FromArgb(160, 180, 188));
        g.DrawString("计划灰线   实际彩线   预测虚线   ◆调向  !告警", Font, titleBrush, plot.Left, ClientSize.Height - 25);
    }

    private void DrawTrain(Graphics g, RectangleF plot, IReadOnlyList<string> rows, double min, double max, TrainData train)
    {
        if (train.ScheduledStops == null || train.ScheduledStops.Count == 0) return;
        var points = new List<(PointF point, int index, bool relative, bool passed)>();
        for (int i = 0; i < train.ScheduledStops.Count; i++)
        {
            var stop = train.ScheduledStops[i];
            int row = IndexOf(rows, stop.Station);
            if (row < 0) continue;
            if (stop.RelativeTimes)
            {
                // Relative schedules have no safe absolute x-coordinate. Keep
                // the station call visible as an explicit uncertainty marker
                // instead of silently projecting a fabricated timestamp.
                float relativeY = RowY(plot, row, rows.Count);
                using var relativePen = new Pen(Color.Goldenrod, 1.2f);
                g.DrawEllipse(relativePen, plot.Left + 2, relativeY - 4, 8, 8);
                g.DrawString("≈", Font, Brushes.Goldenrod, plot.Left + 11, relativeY - Font.Height / 2f);
                continue;
            }
            var scheduled = stop.ArrivalTimeSec ?? stop.DepartureTimeSec;
            if (!scheduled.HasValue) continue;
            bool passed = train.ScheduledVisitIndex >= 0 && i <= train.ScheduledVisitIndex;
            double actual = scheduled.Value + (passed ? (train.LastArrivalScheduleDeviationSec ?? train.Delay) : 0);
            points.Add((new PointF(TimeX(plot, scheduled.Value, min, max), RowY(plot, row, rows.Count)), i, false, passed));

            // Actual/forecast is represented by a second point.  For rows not yet
            // visited the line is dashed, retaining uncertainty rather than
            // inventing a precise prediction.
            if (Math.Abs(actual - scheduled.Value) > .1)
                points.Add((new PointF(TimeX(plot, actual, min, max), RowY(plot, row, rows.Count)), i, false, passed));
        }
        if (points.Count < 1) return;

        Color color = TrainColor(train.Name);
        bool selected = string.Equals(train.Name, _referenceTrain, StringComparison.OrdinalIgnoreCase);
        using var planPen = new Pen(Color.FromArgb(selected ? 185 : 110, Color.LightGray), selected ? 2.1f : 1f);
        using var actualPen = new Pen(color, selected ? 3.2f : 2f);
        using var forecastPen = new Pen(Color.FromArgb(210, color), selected ? 2.2f : 1.3f) { DashStyle = DashStyle.Dash };
        var planPoints = points.Where(p => p.passed == false).Select(p => p.point).ToArray();
        var actualPoints = points.Where(p => p.passed).Select(p => p.point).ToArray();
        if (planPoints.Length > 1) g.DrawLines(planPen, planPoints);
        if (actualPoints.Length > 1) g.DrawLines(actualPen, actualPoints);
        if (planPoints.Length > 1) g.DrawLines(forecastPen, planPoints);

        for (int i = 0; i < train.ScheduledStops.Count; i++)
        {
            var stop = train.ScheduledStops[i];
            int row = IndexOf(rows, stop.Station);
            var time = stop.ArrivalTimeSec ?? stop.DepartureTimeSec;
            if (row < 0 || stop.RelativeTimes || !time.HasValue) continue;
            float x = TimeX(plot, time.Value, min, max);
            float y = RowY(plot, row, rows.Count);
            bool isAlert = _alertTrains.Contains(train.Name);
            using var markerBrush = new SolidBrush(isAlert ? Color.OrangeRed : color);
            if (stop.NonStop || stop.StopMinutes <= 0)
                g.FillEllipse(markerBrush, x - 3, y - 3, 6, 6);
            else
                g.FillRectangle(markerBrush, x - 4, y - 4, 8, 8);
            if (train.RequiresDirectionChange && i == train.ScheduledVisitIndex)
            {
                PointF[] diamond = { new(x, y - 7), new(x + 7, y), new(x, y + 7), new(x - 7, y) };
                g.FillPolygon(Brushes.Gold, diamond);
            }
            if (isAlert)
                g.DrawString("!", Font, Brushes.OrangeRed, x + 5, y - Font.Height / 2f);
        }
    }

    private void DrawCurrentCursor(Graphics g, RectangleF plot, double min, double max)
    {
        double now = ParseClock(_gameTime);
        if (now < min || now > max) return;
        float x = TimeX(plot, now, min, max);
        using var pen = new Pen(Color.Cyan, 1.4f) { DashStyle = DashStyle.Dot };
        g.DrawLine(pen, x, plot.Top, x, plot.Bottom);
        g.DrawString("现在", Font, Brushes.Cyan, x + 3, plot.Top + 2);
    }

    private void HandleMouseWheel(object sender, MouseEventArgs e)
    {
        float old = _zoom;
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.15f : .87f), .35f, 6f);
        if (Math.Abs(old - _zoom) > .001f) Invalidate();
    }

    private void HandleMouseDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Middle && e.Button != MouseButtons.Left) return;
        _panning = true;
        _lastMouse = e.Location;
        Cursor = Cursors.SizeAll;
    }

    private void HandleMouseMove(object sender, MouseEventArgs e)
    {
        if (_panning)
        {
            _panX += e.X - _lastMouse.X;
            _panY += e.Y - _lastMouse.Y;
            _lastMouse = e.Location;
            Invalidate();
            return;
        }
        if (_coreSnapshot != null && _coreSnapshot.Corridor.Count > 0)
        {
            _hoverTrain = null;
            _hoverStop = -1;
            var corePlot = new RectangleF(92, 26, Math.Max(80, ClientSize.Width - 112), Math.Max(80, ClientSize.Height - 54));
            if (e.X >= corePlot.Left && e.X <= corePlot.Right && e.Y >= corePlot.Top && e.Y <= corePlot.Bottom)
            {
                int row = (int)Math.Round((e.Y - corePlot.Top) / Math.Max(1, corePlot.Height) * (_coreSnapshot.Corridor.Count - 1));
                var coreBounds = _coreSnapshot.Trains.SelectMany(train => train.Points).Where(point => point.AbsoluteTimeUtc.HasValue)
                    .Select(point => point.AbsoluteTimeUtc.Value.ToUnixTimeMilliseconds() / 1000d).ToList();
                if (coreBounds.Count > 0)
                {
                    double min = coreBounds.Min();
                    double max = coreBounds.Max();
                    if (max - min < 3600) { min -= 1800; max += 1800; }
                    foreach (var train in _coreSnapshot.Trains)
                    {
                        var point = train.Points.FirstOrDefault(item => item.CorridorIndex == row && item.AbsoluteTimeUtc.HasValue);
                        if (point == null) continue;
                        float x = CoreTimeX(corePlot, point.AbsoluteTimeUtc.Value.ToUnixTimeMilliseconds() / 1000d, min, max);
                        if (Math.Abs(x - e.X) <= 14)
                        {
                            _hoverTrain = train.TrainId;
                            _hoverStop = point.CorridorIndex;
                            break;
                        }
                    }
                }
            }
            Invalidate();
            return;
        }
        // Hover text is intentionally lightweight; hit testing maps to the
        // nearest station row and train segment without mutating the model.
        _hoverTrain = null;
        _hoverStop = -1;
        var rows = BuildRows();
        var bounds = GetTimeBounds();
        var plot = new RectangleF(92, 26, Math.Max(80, ClientSize.Width - 112), Math.Max(80, ClientSize.Height - 54));
        if (e.X >= plot.Left && e.X <= plot.Right && e.Y >= plot.Top && e.Y <= plot.Bottom && rows.Count > 0)
        {
            int row = (int)Math.Round((e.Y - plot.Top) / Math.Max(1, plot.Height) * (rows.Count - 1));
            foreach (var train in _trains)
            {
                int index = train.ScheduledStops.FindIndex(s => IndexOf(rows, s.Station) == row);
                if (index >= 0)
                {
                    _hoverTrain = train.Name;
                    _hoverStop = index;
                    break;
                }
            }
        }
        Invalidate();
    }

    private void HandleMouseUp(object sender, MouseEventArgs e)
    {
        _panning = false;
        Cursor = Cursors.Default;
    }

    private void HandleMouseClick(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || string.IsNullOrWhiteSpace(_hoverTrain)) return;
        TrainSelected?.Invoke(this, _hoverTrain);
    }

    private static int IndexOf(IReadOnlyList<string> rows, string station)
    {
        if (string.IsNullOrWhiteSpace(station)) return -1;
        for (int i = 0; i < rows.Count; i++)
            if (string.Equals(rows[i], station, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static float RowY(RectangleF plot, int row, int count)
        => plot.Top + (count <= 1 ? plot.Height / 2f : plot.Height * row / (count - 1f));

    private float TimeX(RectangleF plot, double value, double min, double max)
        => plot.Left + (float)((value - min) / Math.Max(1, max - min) * plot.Width) * _zoom + _panX;

    private static double ParseClock(string value)
    {
        if (TimeSpan.TryParse(value, out var ts)) return ts.TotalSeconds;
        return 0;
    }

    private static string FormatClock(double seconds)
    {
        seconds %= 86400;
        if (seconds < 0) seconds += 86400;
        return TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm");
    }

    private static double NiceTimeStep(double raw)
    {
        double[] choices = { 300, 600, 900, 1800, 3600, 7200, 10800, 21600 };
        return choices.FirstOrDefault(c => c >= raw, choices[^1]);
    }

    private static string TrimStation(string station)
        => station?.Length > 12 ? station.Substring(0, 11) + "…" : station ?? string.Empty;

    private static Color TrainColor(string name)
    {
        int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(name ?? string.Empty) & 0x7fffffff;
        return Color.FromArgb(170, 80 + hash % 150, 80 + hash / 7 % 150, 100 + hash / 31 % 130);
    }
}
