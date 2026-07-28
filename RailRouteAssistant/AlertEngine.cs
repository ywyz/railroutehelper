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

        /// <summary>
        /// 估算列车还有多少秒需要停车（基于速度和前方区间数）
        /// </summary>
        private static int EstimateSecondsToStop(int speedKmh, int lookahead)
        {
            if (lookahead <= 0) return 0;
            // 粗略估算：每个区间约 200-400 米
            // 速度 km/h -> m/s = /3.6
            // 每段区间按 300 米算
            double distMeters = lookahead * 300.0;
            double speedMs = speedKmh / 3.6;
            if (speedMs < 1) return 999;
            return (int)(distMeters / speedMs);
        }

        private static List<AlertInfo> EvaluateTrain(TrainSnapshot snap)
        {
            var alerts = new List<AlertInfo>();
            var nextStation = !string.IsNullOrEmpty(snap.NextStationName) ? $" -> {snap.NextStationName}" : "";

            bool signalOpen = snap.HasActingSignal && snap.SignalState.Contains("开放");
            bool signalClosed = snap.HasActingSignal && snap.SignalState.Contains("关闭");
            var sigInfo = snap.HasActingSignal ? $"（{snap.SignalState}）" : "";

            // ========== 运行中列车 ==========
            if (snap.CurrentSpeed > 0)
            {
                int estSec = EstimateSecondsToStop(snap.CurrentSpeed, snap.LookaheadCount);

                // 前方0段 - 立即停车
                if (snap.LookaheadCount == 0)
                {
                    alerts.Add(new AlertInfo
                    {
                        Level = "critical",
                        TrainName = snap.TrainName,
                        Message = $"前方进路未配置，即将停车{nextStation}{sigInfo}",
                        TimestampMs = NowMs()
                    });
                }
                // 前方1-2段 - 紧急
                else if (snap.LookaheadCount <= 2)
                {
                    alerts.Add(new AlertInfo
                    {
                        Level = "critical",
                        TrainName = snap.TrainName,
                        Message = $"进路即将结束！剩余{snap.LookaheadCount}段 约{estSec}s后停车{nextStation}{sigInfo}",
                        TimestampMs = NowMs()
                    });
                }
                // 前方3-5段 - 警告
                else if (snap.LookaheadCount <= 5)
                {
                    alerts.Add(new AlertInfo
                    {
                        Level = "warning",
                        TrainName = snap.TrainName,
                        Message = $"前方进路仅剩{snap.LookaheadCount}段 约{estSec}s后需停车{nextStation}{sigInfo}",
                        TimestampMs = NowMs()
                    });
                }

                // 信号关闭 - 警告
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

                // 需要配置前方进路 - 无论信号状态都告警
                if (snap.NeedsRouteAhead)
                {
                    string level = snap.LookaheadCount <= 2 ? "critical" :
                                   snap.LookaheadCount <= 5 ? "warning" : "warning";
                    alerts.Add(new AlertInfo
                    {
                        Level = level,
                        TrainName = snap.TrainName,
                        Message = $"需要配置前方进路！剩余{snap.LookaheadCount}段 速度{snap.CurrentSpeed}km/h 约{estSec}s{nextStation}{sigInfo}",
                        TimestampMs = NowMs()
                    });
                }

                // 速度很低且在减速 - 即将停车
                if (snap.CurrentSpeed <= 5 && snap.TargetSpeed <= 0.1f)
                {
                    alerts.Add(new AlertInfo
                    {
                        Level = "warning",
                        TrainName = snap.TrainName,
                        Message = snap.LookaheadCount == 0
                            ? $"因进路不足即将停车{nextStation}"
                            : $"即将停车{nextStation}",
                        TimestampMs = NowMs()
                    });
                }
            }
            // ========== 已停车列车 ==========
            else if (!snap.FinishedSchedule)
            {
                if (snap.CanDepart && snap.LookaheadCount == 0)
                {
                    alerts.Add(new AlertInfo
                    {
                        Level = "critical",
                        TrainName = snap.TrainName,
                        Message = $"可发车但前方进路未配置{nextStation}",
                        TimestampMs = NowMs()
                    });
                }
                else if (!snap.CanDepart)
                {
                    // 非到站停车 - 线路中停车
                    if (snap.LookaheadCount == 0)
                    {
                        alerts.Add(new AlertInfo
                        {
                            Level = "critical",
                            TrainName = snap.TrainName,
                            Message = $"已停车 - 前方进路未配置{nextStation}{sigInfo}",
                            TimestampMs = NowMs()
                        });
                    }
                    else if (snap.NeedsRouteAhead)
                    {
                        alerts.Add(new AlertInfo
                        {
                            Level = "warning",
                            TrainName = snap.TrainName,
                            Message = $"已停车 - 需要配置前方进路（剩余{snap.LookaheadCount}段）{nextStation}{sigInfo}",
                            TimestampMs = NowMs()
                        });
                    }
                    else if (signalClosed)
                    {
                        alerts.Add(new AlertInfo
                        {
                            Level = "warning",
                            TrainName = snap.TrainName,
                            Message = $"已停车 - 前方信号关闭{nextStation}",
                            TimestampMs = NowMs()
                        });
                    }
                    else
                    {
                        // 停车超时
                        if (snap.NotMovingSinceTimestamp.HasValue)
                        {
                            var stoppedSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - snap.NotMovingSinceTimestamp.Value;
                            if (stoppedSec > 10)
                            {
                                var reason = !string.IsNullOrEmpty(snap.StopReasons) ? snap.StopReasons : "未知";
                                alerts.Add(new AlertInfo
                                {
                                    Level = "warning",
                                    TrainName = snap.TrainName,
                                    Message = $"线路停车{stoppedSec}s（{reason}）{nextStation}",
                                    TimestampMs = NowMs()
                                });
                            }
                        }
                    }
                }
            }

            // ========== 发车预告 ==========
            if (snap.CanDepart && snap.CurrentSpeed == 0 && !snap.FinishedSchedule)
            {
                var msg = "即将发车";
                if (snap.DelaySeconds > 0)
                    msg += $"（延误{FormatDelay(snap.DelaySeconds)}）";
                msg += nextStation;
                alerts.Add(new AlertInfo
                {
                    Level = "warning",
                    TrainName = snap.TrainName,
                    Message = msg,
                    TimestampMs = NowMs()
                });
            }

            // ========== 故障 ==========
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
        /// 进路冲突检测
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
                            Level = prepareSec <= 60 ? "warning" : "info",
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
