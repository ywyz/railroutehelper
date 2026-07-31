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
            sb.Append("\"gameReady\":").Append(gameReady.ToString().ToLower()).Append(",");
            sb.Append("\"lastUpdate\":\"").Append(lastUpdate.ToString("HH:mm:ss")).Append("\",");
            sb.Append("\"serverTime\":\"").Append(DateTime.Now.ToString("HH:mm:ss")).Append("\",");
            sb.Append("\"gameTime\":").Append(gameTimeSec.HasValue ? $"\"{FormatGameTime(gameTimeSec.Value)}\"" : "null").Append(",");

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
                sb.Append("\"targetSpeed\":").Append(s.TargetSpeed.ToString("F1")).Append(",");
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
                sb.Append("\"departureRemainingSec\":").Append(s.DepartureRemainingSeconds.HasValue ? s.DepartureRemainingSeconds.Value.ToString("F0") : "null").Append(",");
                sb.Append("\"currentDepartureScheduleDelaySec\":").Append(s.CurrentDepartureScheduleDelaySeconds.HasValue ? s.CurrentDepartureScheduleDelaySeconds.Value.ToString("R", CultureInfo.InvariantCulture) : "null").Append(",");
                sb.Append("\"stopReasons\":").Append(JsonStr(s.StopReasons)).Append(",");
                sb.Append("\"nextPrepareSec\":").Append(s.NextPrepareTimeTotalSeconds.HasValue ? s.NextPrepareTimeTotalSeconds.Value.ToString("F0") : "null").Append(",");
                sb.Append("\"nextArrivalSec\":").Append(s.NextArrivalTimeTotalSeconds.HasValue ? s.NextArrivalTimeTotalSeconds.Value.ToString("F0") : "null").Append(",");
                sb.Append("\"notMovingSince\":").Append(s.NotMovingDuration.HasValue ? s.NotMovingDuration.Value.ToString("F0") : "null");
                sb.Append("}");
            }
            sb.Append("],");

            // 告警列表
            sb.Append("\"alerts\":[");
            for (int i = 0; i < alerts.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var a = alerts[i];
                sb.Append("{");
                sb.Append("\"level\":").Append(JsonStr(a.Level)).Append(",");
                sb.Append("\"train\":").Append(JsonStr(a.TrainName)).Append(",");
                sb.Append("\"message\":").Append(JsonStr(a.Message));
                sb.Append("}");
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
                var a = alerts[i];
                sb.Append("{");
                sb.Append("\"level\":").Append(JsonStr(a.Level)).Append(",");
                sb.Append("\"train\":").Append(JsonStr(a.TrainName)).Append(",");
                sb.Append("\"message\":").Append(JsonStr(a.Message));
                sb.Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string JsonStr(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            var escaped = s.Replace("\\", "\\\\")
                           .Replace("\"", "\\\"")
                           .Replace("\n", "\\n")
                           .Replace("\r", "\\r")
                           .Replace("\t", "\\t");
            return $"\"{escaped}\"";
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
