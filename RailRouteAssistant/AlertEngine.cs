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

            alerts.AddRange(DetectPlatformConflicts(onBoard));
            alerts.AddRange(DetectRouteCollisions(onBoard));
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

            // 信号状态通过 TargetSpeed 判断（IsActing 不代表开放/关闭）
            // TargetSpeed > 0 = 信号开放，可以继续走
            // TargetSpeed ≈ 0 = 信号关闭，需要停车
            bool signalOpen = snap.TargetSpeed > 0.5f;
            bool signalClosed = !signalOpen;
            var sigInfo = signalOpen ? "（信号开放）" : "（信号关闭）";

            // ========== 提前预警：信号开放但前方轨道段未配置进路（灰色） ==========
            // AllocationState: 0=Free(灰/未配), 1=Allocated(绿/已配), 2=Occupied(红/占用)
            if (signalOpen && snap.CurrentSpeed > 0 && snap.FrontAllocationState == 0)
            {
                alerts.Add(new AlertInfo
                {
                    Level = "warning",
                    TrainName = snap.TrainName,
                    Message = $"前方信号开放但前方轨道未配进路 速度{snap.CurrentSpeed}km/h 建议提前配置{nextStation}",
                    TimestampMs = NowMs()
                });
            }

            // ========== 运行中列车 ==========
            if (snap.CurrentSpeed > 0)
            {
                // 前方完全没有铁轨段 = 进路完全未配置
                if (snap.LookaheadCount == 0)
                {
                    alerts.Add(new AlertInfo
                    {
                        Level = "critical",
                        TrainName = snap.TrainName,
                        Message = $"前方进路未配置 即将停车{nextStation}",
                        TimestampMs = NowMs()
                    });
                }
                // 信号关闭 = 列车即将被迫减速停车
                else if (signalClosed)
                {
                    if (snap.CurrentSpeed <= 10)
                    {
                        alerts.Add(new AlertInfo
                        {
                            Level = "critical",
                            TrainName = snap.TrainName,
                            Message = $"前方信号关闭！速度{snap.CurrentSpeed}km/h 即将停车{nextStation}",
                            TimestampMs = NowMs()
                        });
                    }
                    else
                    {
                        alerts.Add(new AlertInfo
                        {
                            Level = "warning",
                            TrainName = snap.TrainName,
                            Message = $"前方信号关闭 速度{snap.CurrentSpeed}km/h 减速中{nextStation}",
                            TimestampMs = NowMs()
                        });
                    }
                }
                // 信号开放 = 列车可以继续走，不告警

                // 速度很低且在减速 - 即将停车
                if (snap.CurrentSpeed <= 5 && signalClosed)
                {
                    alerts.Add(new AlertInfo
                    {
                        Level = "warning",
                        TrainName = snap.TrainName,
                        Message = $"因前方信号关闭即将停车{nextStation}",
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
                    // 非到站停车 - 信号关闭导致停车
                    if (signalClosed && snap.HasActingSignal)
                    {
                        alerts.Add(new AlertInfo
                        {
                            Level = "critical",
                            TrainName = snap.TrainName,
                            Message = $"已停车 - 前方信号关闭{nextStation}",
                            TimestampMs = NowMs()
                        });
                    }
                    else if (snap.LookaheadCount == 0)
                    {
                        alerts.Add(new AlertInfo
                        {
                            Level = "critical",
                            TrainName = snap.TrainName,
                            Message = $"已停车 - 前方进路未配置{nextStation}",
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
        /// 站台冲突检测：两列车前往同一站同一站台
        /// 仅限下一站；若一辆已出站（下一站已变）则不会匹配
        /// </summary>
        private static List<AlertInfo> DetectPlatformConflicts(List<TrainSnapshot> trains)
        {
            var alerts = new List<AlertInfo>();
            var active = trains.Where(t => !t.FinishedSchedule).ToList();

            for (int i = 0; i < active.Count; i++)
            {
                for (int j = i + 1; j < active.Count; j++)
                {
                    var a = active[i];
                    var b = active[j];

                    // 必须有相同的下一站名和站台号
                    if (string.IsNullOrEmpty(a.NextStationName)) continue;
                    if (a.NextStationName != b.NextStationName) continue;
                    if (a.NextPlatformNumber <= 0 || a.NextPlatformNumber != b.NextPlatformNumber)
                        continue;

                    // 一辆在站内停车（speed=0），另一辆正在运行（speed>0）-> 紧急
                    bool aStopped = a.CurrentSpeed == 0;
                    bool bStopped = b.CurrentSpeed == 0;
                    bool aRunning = a.CurrentSpeed > 0;
                    bool bRunning = b.CurrentSpeed > 0;

                    if ((aStopped && bRunning) || (bStopped && aRunning))
                    {
                        alerts.Add(new AlertInfo
                        {
                            Level = "critical",
                            TrainName = $"{a.TrainName}/{b.TrainName}",
                            Message = $"站台冲突：均前往 {a.NextStationName} {a.NextPlatformNumber}台，一列在站一列接近",
                            TimestampMs = NowMs()
                        });
                    }
                    else if (aRunning && bRunning)
                    {
                        // 两列都在运行中前往同一站台 -> 警告
                        alerts.Add(new AlertInfo
                        {
                            Level = "warning",
                            TrainName = $"{a.TrainName}/{b.TrainName}",
                            Message = $"站台可能冲突：均前往 {a.NextStationName} {a.NextPlatformNumber}台",
                            TimestampMs = NowMs()
                        });
                    }
                    // 两列都停车在站 -> 已到达，不报
                }
            }

            return alerts;
        }

        /// <summary>
        /// 碰撞检测：两列车前方进路经过同一段轨道
        /// </summary>
        private static List<AlertInfo> DetectRouteCollisions(List<TrainSnapshot> trains)
        {
            var alerts = new List<AlertInfo>();
            var withRoute = trains
                .Where(t => !t.FinishedSchedule && t.RouteStepTrackIds.Count > 0)
                .ToList();

            for (int i = 0; i < withRoute.Count; i++)
            {
                for (int j = i + 1; j < withRoute.Count; j++)
                {
                    var a = withRoute[i];
                    var b = withRoute[j];

                    // 检查前方进路的轨道段是否有交集
                    var common = a.RouteStepTrackIds.Intersect(b.RouteStepTrackIds).ToList();
                    if (common.Count > 0)
                    {
                        alerts.Add(new AlertInfo
                        {
                            Level = "critical",
                            TrainName = $"{a.TrainName}/{b.TrainName}",
                            Message = $"进路相交：前方{common.Count}段轨道重叠",
                            TimestampMs = NowMs()
                        });
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
