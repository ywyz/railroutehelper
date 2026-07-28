using System;
using System.Collections.Generic;
using System.Linq;

namespace RailRouteAssistant
{
    public static class AlertEngine
    {
        public static List<AlertInfo> Evaluate(List<TrainSnapshot> snapshots)
        {
            var alerts = new List<AlertInfo>();
            var onBoard = snapshots.Where(s => s.IsOnBoard && !s.IsDisposed).ToList();

            foreach (var snap in onBoard)
                alerts.AddRange(EvaluateTrain(snap));

            alerts.AddRange(DetectRouteConflicts(onBoard));
            alerts.AddRange(EvaluateUpcomingTrains(snapshots));

            return alerts
                .OrderByDescending(a => LevelOrder(a.Level))
                .ThenBy(a => a.TrainName)
                .ToList();
        }

        private static int LevelOrder(string level) => level switch
        {
            "critical" => 0,
            "warning" => 1,
            _ => 2
        };

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static List<AlertInfo> EvaluateTrain(TrainSnapshot snap)
        {
            var alerts = new List<AlertInfo>();
            var nextStation = !string.IsNullOrEmpty(snap.NextStationName) ? $" -> {snap.NextStationName}" : "";

            // 判断前方信号状态
            bool signalOpen = snap.HasActingSignal && snap.SignalState.Contains("开放");
            bool signalClosed = snap.HasActingSignal && snap.SignalState.Contains("关闭");

            if (snap.CurrentSpeed > 0)
            {
                // 列车运行中

                // 前方区间不足（信号关闭或无信号时为紧急，信号开放时为警告）
                if (snap.LookaheadCount == 0)
                {
                    var msg = $"前方进路未配置，即将停车{nextStation}";
                    if (snap.HasActingSignal)
                        msg += $"（{snap.SignalState}）";
                    alerts.Add(new AlertInfo
                    {
                        Level = signalOpen ? "warning" : "critical",
                        TrainName = snap.TrainName,
                        Message = msg,
                        TimestampMs = NowMs()
                    });
                }
                else if (snap.LookaheadCount <= 2)
                {
                    var msg = $"进路即将结束（剩余{snap.LookaheadCount}段）{nextStation}";
                    if (snap.HasActingSignal)
                        msg += $"（{snap.SignalState}）";
                    alerts.Add(new AlertInfo
                    {
                        Level = signalOpen ? "warning" : "critical",
                        TrainName = snap.TrainName,
                        Message = msg,
                        TimestampMs = NowMs()
                    });
                }
                else if (snap.LookaheadCount <= 5 && !signalOpen)
                {
                    alerts.Add(new AlertInfo
                    {
                        Level = "warning",
                        TrainName = snap.TrainName,
                        Message = $"前方进路仅剩{snap.LookaheadCount}段{nextStation}",
                        TimestampMs = NowMs()
                    });
                }

                // 信号关闭预警
                if (signalClosed)
                {
                    alerts.Add(new AlertInfo
                    {
                        Level = "warning",
                        TrainName = snap.TrainName,
                        Message = $"前方信号关闭，即将减速停车{nextStation}",
                        TimestampMs = NowMs()
                    });
                }

                // 需要配置前方进路（无论信号是否开放）
                if (snap.NeedsRouteAhead)
                {
                    alerts.Add(new AlertInfo
                    {
                        Level = snap.LookaheadCount <= 2 ? "warning" : "info",
                        TrainName = snap.TrainName,
                        Message = $"需要配置前方进路（剩余{snap.LookaheadCount}段）{nextStation}",
                        TimestampMs = NowMs()
                    });
                }
            }
            else
            {
                // 列车已停车
                if (snap.CanDepart && snap.LookaheadCount == 0 && !snap.FinishedSchedule)
                {
                    alerts.Add(new AlertInfo
                    {
                        Level = "critical",
                        TrainName = snap.TrainName,
                        Message = $"可发车但前方进路未配置{nextStation}",
                        TimestampMs = NowMs()
                    });
                }
            }

            // === 列车停止预告 ===
            // 即将停车（速度低且目标速度为0，且不是到站停车）
            if (snap.CurrentSpeed > 0 && snap.CurrentSpeed <= 5 && snap.TargetSpeed <= 0.1f)
            {
                // 如果前方有进路，可能是到站停车；如果前方没有进路，是进路不足导致停车
                if (snap.LookaheadCount == 0)
                {
                    alerts.Add(new AlertInfo
                    {
                        Level = "warning",
                        TrainName = snap.TrainName,
                        Message = $"因进路不足即将停车{nextStation}",
                        TimestampMs = NowMs()
                    });
                }
                else
                {
                    alerts.Add(new AlertInfo
                    {
                        Level = "warning",
                        TrainName = snap.TrainName,
                        Message = $"即将停车{nextStation}",
                        TimestampMs = NowMs()
                    });
                }
            }

            // 已停止过久（非站台停车 - 没有可发车标志）
            if (snap.CurrentSpeed == 0 && snap.NotMovingSinceTimestamp.HasValue && !snap.FinishedSchedule && !snap.CanDepart)
            {
                var stoppedSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - snap.NotMovingSinceTimestamp.Value;
                if (stoppedSec > 30)
                {
                    var reason = !string.IsNullOrEmpty(snap.StopReasons) ? snap.StopReasons : "未知原因";
                    // 如果前方没有进路，可能是进路导致的停车
                    if (snap.LookaheadCount == 0)
                    {
                        alerts.Add(new AlertInfo
                        {
                            Level = "warning",
                            TrainName = snap.TrainName,
                            Message = $"线路停车 {stoppedSec}s - 前方进路不足（{reason}）",
                            TimestampMs = NowMs()
                        });
                    }
                    else
                    {
                        alerts.Add(new AlertInfo
                        {
                            Level = "warning",
                            TrainName = snap.TrainName,
                            Message = $"线路停车 {stoppedSec}s（{reason}）",
                            TimestampMs = NowMs()
                        });
                    }
                }
            }

            // === 列车启动预告 ===
            if (snap.CanDepart && snap.CurrentSpeed == 0 && !snap.FinishedSchedule)
            {
                var msg = "即将发车";
                if (snap.DelaySeconds > 0)
                    msg += $"（延误{FormatDelay(snap.DelaySeconds)}）";
                msg += nextStation;
                alerts.Add(new AlertInfo
                {
                    Level = "info",
                    TrainName = snap.TrainName,
                    Message = msg,
                    TimestampMs = NowMs()
                });
            }

            // === 故障 ===
            if (snap.IsBrokenDown)
            {
                alerts.Add(new AlertInfo
                {
                    Level = "critical",
                    TrainName = snap.TrainName,
                    Message = "列车故障！",
                    TimestampMs = NowMs()
                });
            }

            return alerts;
        }

        /// <summary>
        /// 进路冲突检测 - 两辆列车前方区间可能重叠
        /// </summary>
        private static List<AlertInfo> DetectRouteConflicts(List<TrainSnapshot> trains)
        {
            var alerts = new List<AlertInfo>();

            var running = trains.Where(t => t.CurrentSpeed > 0 && t.LookaheadCount > 0).ToList();

            for (int i = 0; i < running.Count; i++)
            {
                for (int j = i + 1; j < running.Count; j++)
                {
                    var a = running[i];
                    var b = running[j];

                    if (a.LookaheadCount <= 3 && b.LookaheadCount <= 3)
                    {
                        if (!string.IsNullOrEmpty(a.NextStationName) &&
                            a.NextStationName == b.NextStationName)
                        {
                            alerts.Add(new AlertInfo
                            {
                                Level = "warning",
                                TrainName = $"{a.TrainName}/{b.TrainName}",
                                Message = $"进路可能冲突：均前往 {a.NextStationName}",
                                TimestampMs = NowMs()
                            });
                        }
                    }
                }
            }

            return alerts;
        }

        /// <summary>
        /// 即将入图列车预告
        /// </summary>
        private static List<AlertInfo> EvaluateUpcomingTrains(List<TrainSnapshot> allTrains)
        {
            var alerts = new List<AlertInfo>();

            var waiting = allTrains.Where(t => t.IsWaitingToBeSpawned && !t.IsDisposed).ToList();

            foreach (var w in waiting)
            {
                if (w.NextPrepareTimeTotalSeconds.HasValue)
                {
                    var prepareSec = w.NextPrepareTimeTotalSeconds.Value;
                    if (prepareSec <= 300)
                    {
                        alerts.Add(new AlertInfo
                        {
                            Level = "info",
                            TrainName = w.TrainName,
                            Message = prepareSec <= 60
                                ? $"即将进入地图（{FormatDelay(prepareSec)}后）-> {w.NextStationName}"
                                : $"即将进入地图（{FormatDelay(prepareSec)}后）",
                            TimestampMs = NowMs()
                        });
                    }
                }
                else
                {
                    alerts.Add(new AlertInfo
                    {
                        Level = "info",
                        TrainName = w.TrainName,
                        Message = $"等待进入地图 -> {w.NextStationName}",
                        TimestampMs = NowMs()
                    });
                }
            }

            return alerts;
        }

        private static string FormatDelay(double seconds)
        {
            if (seconds < 60) return $"{(int)seconds}s";
            return $"{(int)(seconds / 60)}m{(int)(seconds % 60)}s";
        }
    }
}
