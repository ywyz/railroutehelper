using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace RailRouteAssistant
{
    /// <summary>
    /// 简单 HTTP 服务器 - 在后台线程运行，提供 JSON 数据
    /// </summary>
    public class HttpServer
    {
        private readonly int _port;
        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _running;

        public HttpServer(int port)
        {
            _port = port;
        }

        public void Start()
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Start();

            _running = true;
            _thread = new Thread(Run) { IsBackground = true };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            _listener?.Stop();
        }

        private void Run()
        {
            while (_running)
            {
                try
                {
                    var context = _listener.GetContext();
                    HandleRequest(context);
                }
                catch (HttpListenerException) when (!_running) { break; }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"HTTP 异常: {ex.Message}");
                }
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url.AbsolutePath;

            string json;
            string mime = "application/json; charset=utf-8";

            switch (path)
            {
                case "/":
                case "/data":
                    json = BuildDataJson();
                    break;
                case "/alerts":
                    json = BuildAlertsJson();
                    break;
                case "/health":
                    json = "{\"status\":\"ok\"}";
                    break;
                default:
                    ctx.Response.StatusCode = 404;
                    json = "{\"error\":\"not found\"}";
                    break;
            }

            var buffer = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = mime;
            ctx.Response.ContentLength64 = buffer.Length;
            ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
            ctx.Response.OutputStream.Close();
        }

        private string BuildDataJson()
        {
            var (snapshots, alerts, lastUpdate, gameReady) = DataStore.GetCurrent();
            var gameTimeSec = DataStore.GetGameTimeSeconds();

            var sb = new StringBuilder();
            sb.Append("{");

            // 元数据
            sb.Append("\"apiVersion\":1,");
            sb.Append("\"pluginVersion\":").Append(JsonNullable(TryGetPluginVersion())).Append(",");
            sb.Append("\"gameReady\":").Append(gameReady.ToString().ToLower()).Append(",");
            sb.Append("\"lastUpdate\":\"").Append(lastUpdate.ToString("HH:mm:ss")).Append("\",");
            sb.Append("\"serverTime\":\"").Append(DateTime.Now.ToString("HH:mm:ss")).Append("\",");
            sb.Append("\"gameTime\":").Append(gameTimeSec.HasValue ? $"\"{FormatGameTime(gameTimeSec.Value)}\"" : "null").Append(",");
            sb.Append("\"gameTimeSeconds\":").Append(gameTimeSec.HasValue
                ? gameTimeSec.Value.ToString("R", CultureInfo.InvariantCulture)
                : "null").Append(",");

            // 列车列表
            sb.Append("\"trains\":[");
            for (int i = 0; i < snapshots.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var s = snapshots[i];
                sb.Append("{");
                sb.Append("\"id\":").Append(JsonStr(s.TrainId)).Append(",");
                sb.Append("\"name\":").Append(JsonStr(s.TrainName)).Append(",");
                sb.Append("\"speed\":").Append(s.CurrentSpeed).Append(",");
                sb.Append("\"maxSpeed\":").Append(s.MaxSpeed).Append(",");
                sb.Append("\"targetSpeed\":").Append(s.TargetSpeed.ToString("F1", CultureInfo.InvariantCulture)).Append(",");
                sb.Append("\"delay\":").Append(s.DelaySeconds.ToString("R", CultureInfo.InvariantCulture)).Append(",");
                sb.Append("\"canDepart\":").Append(s.CanDepart.ToString().ToLower()).Append(",");
                sb.Append("\"finished\":").Append(s.FinishedSchedule.ToString().ToLower()).Append(",");
                sb.Append("\"brokenDown\":").Append(s.IsBrokenDown.ToString().ToLower()).Append(",");
                sb.Append("\"onBoard\":").Append(s.IsOnBoard.ToString().ToLower()).Append(",");
                sb.Append("\"waiting\":").Append(s.IsWaitingToBeSpawned.ToString().ToLower()).Append(",");
                sb.Append("\"lookahead\":").Append(s.LookaheadCount).Append(",");
                sb.Append("\"hasRoute\":").Append(s.HasValidRoute.ToString().ToLower()).Append(",");
                sb.Append("\"needsRoute\":").Append(s.NeedsRouteAhead.ToString().ToLower()).Append(",");
                sb.Append("\"hasSignal\":").Append(s.HasActingSignal.ToString().ToLower()).Append(",");
                sb.Append("\"signalState\":").Append(JsonStr(s.SignalState)).Append(",");
                sb.Append("\"signalAllocationState\":").Append(s.SignalAllocationState).Append(",");
                sb.Append("\"frontAllocationState\":").Append(s.FrontAllocationState).Append(",");
                sb.Append("\"routeTotal\":").Append(s.RouteTotalSteps).Append(",");
                sb.Append("\"routeCur\":").Append(s.RouteCurrentStep).Append(",");
                sb.Append("\"routeRemain\":").Append(s.RouteRemainingSteps).Append(",");
                sb.Append("\"platform\":").Append(s.NextPlatformNumber).Append(",");
                sb.Append("\"nextStation\":").Append(JsonStr(s.NextStationName)).Append(",");
                sb.Append("\"nextStationNonStop\":").Append(s.NextStationNonStop.ToString().ToLower()).Append(",");
                sb.Append("\"scheduledStops\":[");
                for (int stopIndex = 0; stopIndex < s.ScheduledStops.Count; stopIndex++)
                {
                    if (stopIndex > 0) sb.Append(",");
                    var stop = s.ScheduledStops[stopIndex];
                    sb.Append("{");
                    sb.Append("\"station\":").Append(JsonStr(stop.StationName)).Append(",");
                    sb.Append("\"platform\":").Append(stop.PlatformNumber).Append(",");
                    sb.Append("\"arrivalTimeSec\":").Append(stop.ArrivalGameTimeSeconds.HasValue ? stop.ArrivalGameTimeSeconds.Value.ToString("R", CultureInfo.InvariantCulture) : "null").Append(",");
                    sb.Append("\"departureTimeSec\":").Append(stop.DepartureGameTimeSeconds.HasValue ? stop.DepartureGameTimeSeconds.Value.ToString("R", CultureInfo.InvariantCulture) : "null").Append(",");
                    sb.Append("\"stopMinutes\":").Append(stop.StopDurationMinutes).Append(",");
                    sb.Append("\"relativeTimes\":").Append(stop.RelativeTimes.ToString().ToLower()).Append(",");
                    sb.Append("\"nonStop\":").Append(stop.NonStop.ToString().ToLower());
                    sb.Append("}");
                }
                sb.Append("],");
                sb.Append("\"actualVisitCount\":").Append(s.ActualVisitCount).Append(",");
                sb.Append("\"scheduledVisitCount\":").Append(s.ScheduledVisitCount).Append(",");
                sb.Append("\"scheduledVisitIndex\":").Append(s.CurrentScheduledVisitIndex).Append(",");
                sb.Append("\"lastVisitStation\":").Append(JsonStr(s.LastVisitStationName)).Append(",");
                sb.Append("\"lastVisitPlatform\":").Append(s.LastVisitPlatformNumber).Append(",");
                sb.Append("\"lastVisitNonStop\":").Append(s.LastVisitNonStop.ToString().ToLower()).Append(",");
                sb.Append("\"lastVisitStopMinutes\":").Append(s.LastVisitStopDurationMinutes).Append(",");
                sb.Append("\"lastVisitDeparted\":").Append(s.LastVisitDeparted.ToString().ToLower()).Append(",");
                sb.Append("\"lastArrivalScheduleDeviationSec\":").Append(s.LastArrivalScheduleDeviationSeconds.HasValue ? s.LastArrivalScheduleDeviationSeconds.Value.ToString("R", CultureInfo.InvariantCulture) : "null").Append(",");
                sb.Append("\"lastDepartureScheduleDelaySec\":").Append(s.LastDepartureScheduleDelaySeconds.HasValue ? s.LastDepartureScheduleDelaySeconds.Value.ToString("R", CultureInfo.InvariantCulture) : "null").Append(",");
                sb.Append("\"requiresDirectionChange\":").Append(s.RequiresDirectionChange.ToString().ToLower()).Append(",");
                sb.Append("\"currentStation\":").Append(JsonStr(s.CurrentStationName)).Append(",");
                sb.Append("\"currentPlatform\":").Append(s.CurrentPlatformNumber).Append(",");
                sb.Append("\"currentStopMinutes\":").Append(s.CurrentStopDurationMinutes).Append(",");
                sb.Append("\"departureRemainingSec\":").Append(s.DepartureRemainingSeconds.HasValue ? s.DepartureRemainingSeconds.Value.ToString("F0", CultureInfo.InvariantCulture) : "null").Append(",");
                sb.Append("\"currentDepartureScheduleDelaySec\":").Append(s.CurrentDepartureScheduleDelaySeconds.HasValue ? s.CurrentDepartureScheduleDelaySeconds.Value.ToString("R", CultureInfo.InvariantCulture) : "null").Append(",");
                sb.Append("\"stopReasons\":").Append(JsonStr(s.StopReasons)).Append(",");
                sb.Append("\"nextPrepareSec\":").Append(s.NextPrepareTimeTotalSeconds.HasValue ? s.NextPrepareTimeTotalSeconds.Value.ToString("F0", CultureInfo.InvariantCulture) : "null").Append(",");
                sb.Append("\"nextArrivalSec\":").Append(s.NextArrivalTimeTotalSeconds.HasValue ? s.NextArrivalTimeTotalSeconds.Value.ToString("F0", CultureInfo.InvariantCulture) : "null").Append(",");
                sb.Append("\"notMovingSince\":").Append(s.NotMovingDuration.HasValue ? s.NotMovingDuration.Value.ToString("F0", CultureInfo.InvariantCulture) : "null").Append(",");
                sb.Append("\"mapEntryTimeSec\":").Append(s.MapEntryGameTimeSeconds.HasValue ? s.MapEntryGameTimeSeconds.Value.ToString("R", CultureInfo.InvariantCulture) : "null").Append(",");
                sb.Append("\"mapExitTimeSec\":").Append(s.MapExitGameTimeSeconds.HasValue ? s.MapExitGameTimeSeconds.Value.ToString("R", CultureInfo.InvariantCulture) : "null").Append(",");
                sb.Append("\"mapEntryStation\":").Append(JsonStr(s.MapEntryStationName ?? "")).Append(",");
                sb.Append("\"mapExitStation\":").Append(JsonStr(s.MapExitStationName ?? "")).Append(",");
                sb.Append("\"mapEntryPlatform\":").Append(s.MapEntryPlatformNumber).Append(",");
                sb.Append("\"mapExitPlatform\":").Append(s.MapExitPlatformNumber).Append(",");
                sb.Append("\"mapEntryNonStop\":").Append(s.MapEntryNonStop.ToString().ToLower()).Append(",");
                sb.Append("\"mapExitNonStop\":").Append(s.MapExitNonStop.ToString().ToLower());
                sb.Append("}");
            }
            sb.Append("],");

            // 告警列表
            sb.Append("\"alerts\":[");
            for (int i = 0; i < alerts.Count; i++)
            {
                if (i > 0) sb.Append(",");
                AppendAlertJson(sb, alerts[i]);
            }
            sb.Append("]");

            sb.Append("}");
            return sb.ToString();
        }

        private string BuildAlertsJson()
        {
            var (_, alerts, _, _) = DataStore.GetCurrent();
            var sb = new StringBuilder();
            sb.Append("{\"alerts\":[");
            for (int i = 0; i < alerts.Count; i++)
            {
                if (i > 0) sb.Append(",");
                AppendAlertJson(sb, alerts[i]);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendAlertJson(StringBuilder sb, AlertInfo alert)
        {
            if (alert == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append("{");
            // Keep the original fields first for old desktop clients.
            sb.Append("\"level\":").Append(JsonStr(alert.Level)).Append(",");
            sb.Append("\"train\":").Append(JsonStr(alert.TrainName)).Append(",");
            sb.Append("\"message\":").Append(JsonStr(alert.Message)).Append(",");
            var fingerprint = string.IsNullOrWhiteSpace(alert.Fingerprint)
                ? AlertFingerprint.Compute(alert)
                : alert.Fingerprint;
            sb.Append("\"fingerprint\":").Append(JsonStr(fingerprint)).Append(",");
            sb.Append("\"timestampMs\":").Append(alert.TimestampMs.ToString(CultureInfo.InvariantCulture)).Append(",");
            sb.Append("\"kind\":").Append(JsonStr(alert.Kind)).Append(",");
            sb.Append("\"severity\":").Append(JsonStr(alert.Severity ?? alert.Level)).Append(",");
            sb.Append("\"primaryTrainId\":").Append(JsonStr(alert.PrimaryTrainId)).Append(",");
            sb.Append("\"relatedTrainIds\":");
            AppendStringArray(sb, alert.RelatedTrainIds);
            sb.Append(",\"stationName\":").Append(JsonStr(alert.StationName)).Append(",");
            sb.Append("\"platformNumber\":").Append(alert.PlatformNumber.ToString(CultureInfo.InvariantCulture)).Append(",");
            sb.Append("\"routeTrackIds\":");
            AppendStringArray(sb, alert.RouteTrackIds);
            sb.Append(",\"summary\":").Append(JsonStr(alert.Summary ?? alert.Message));
            sb.Append("}");
        }

        private static void AppendStringArray(StringBuilder sb, System.Collections.Generic.IEnumerable<string> values)
        {
            sb.Append("[");
            var first = true;
            if (values != null)
            {
                foreach (var value in values)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append(JsonStr(value));
                }
            }
            sb.Append("]");
        }

        private static string JsonStr(string s)
        {
            if (s == null) return "\"\"";

            var escaped = new StringBuilder(s.Length + 2);
            escaped.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': escaped.Append("\\\\"); break;
                    case '"': escaped.Append("\\\""); break;
                    case '\b': escaped.Append("\\b"); break;
                    case '\f': escaped.Append("\\f"); break;
                    case '\n': escaped.Append("\\n"); break;
                    case '\r': escaped.Append("\\r"); break;
                    case '\t': escaped.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            escaped.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            escaped.Append(c);
                        break;
                }
            }
            escaped.Append('"');
            return escaped.ToString();
        }

        private static string JsonNullable(string s)
        {
            return s == null ? "null" : JsonStr(s);
        }

        private static string TryGetPluginVersion()
        {
            try
            {
                return Plugin.PluginVersion;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 游戏内时间（TimeSpan.TotalSeconds）格式化为 24h HH:MM:SS
        /// </summary>
        private static string FormatGameTime(double totalSeconds)
        {
            try
            {
                var ts = TimeSpan.FromSeconds(totalSeconds);
                // 游戏内时间是自模拟开始的累计时长，取一天内的时分秒展示
                return ts.ToString(@"hh\:mm\:ss");
            }
            catch { return ""; }
        }
    }
}
