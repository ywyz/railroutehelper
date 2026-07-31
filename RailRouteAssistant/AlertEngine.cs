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
            var platformStr = snap.NextPlatformNumber > 0 ? $" {snap.NextPlatformNumber}台" : "";
            var nextStation = !string.IsNullOrEmpty(snap.NextStationName) ? $" -> {snap.NextStationName}{platformStr}" : "";

            // 信号预警只监视列车刚越过一个信号后锁定的紧邻下一座同向信号。
            // TargetSpeed 只能描述制动进度，不能单独证明“前方信号关闭”。

            bool hasSignal = snap.HasActingSignal;
            bool isRunning = snap.CurrentSpeed > 0;
            bool trainBraking = snap.TargetSpeed < 0.5f;       // TargetSpeed ≈ 0
            bool trainSlowing = !trainBraking && snap.TargetSpeed > 0.5f && snap.TargetSpeed < snap.CurrentSpeed - 5; // TargetSpeed 开始下降

            // AllocationState: -1=未知, 0=Free, 1=Allocated, 2=Occupied, 3=Shunting。
            // 只有下一座物理信号的状态明确为非 Allocated 时才告警；未知/无信号不推断。
            bool signalClosed = false;
            string signalReason = "";
            if (hasSignal)
            {
                if (snap.SignalAllocationState == 0)
                {
                    signalClosed = true;
                    signalReason = snap.SignalIsPendingRoute ? "下一信号正在办理进路" : "下一信号未开放";
                }
                else if (snap.SignalAllocationState == 2)
                {
                    signalClosed = true;
                    signalReason = "下一信号前方区间占用";
                }
                else if (snap.SignalAllocationState == 3)
                {
                    signalClosed = true;
                    signalReason = "下一信号处于调车状态";
                }
            }

            if (isRunning)
            {
                // 到站减速不告警（由进站提示处理）
                bool isStationStop = !string.IsNullOrEmpty(snap.StopReasons) && snap.StopReasons.Contains("Station");

                // ========== Level 1 (warning): 紧邻下一信号已确认不能通行 ==========
                // 这是列车越过当前信号后即可出现的最早预警窗口。
                if (signalClosed && !trainBraking && !trainSlowing && !isStationStop)
                {
                    alerts.Add(new AlertInfo
                    {
                        Level = "warning",
                        TrainName = snap.TrainName,
                        Message = $"{signalReason}，请及时处理 速度{snap.CurrentSpeed}km/h{nextStation}",
                        TimestampMs = NowMs()
                    });
                }

                // ========== Level 2 (warning): 列车开始降速（TargetSpeed 下降但仍 > 0）==========
                // 降速只用于确认已受真实阻挡影响，不能在没有明确信号状态时独立告警。
                if (signalClosed && trainSlowing && !isStationStop)
                {
                    alerts.Add(new AlertInfo
                    {
                        Level = "warning",
                        TrainName = snap.TrainName,
                        Message = $"{signalReason}，列车正在减速 速度{snap.CurrentSpeed}km/h{nextStation}",
                        TimestampMs = NowMs()
                    });
                }

                // ========== Level 3 (critical): 列车已制动（TargetSpeed ≈ 0）==========
                // 只有确认下一座信号不能通行时，TargetSpeed≈0 才升级为紧急。
                if (signalClosed && trainBraking && !isStationStop)
                {
                    alerts.Add(new AlertInfo
                    {
                        Level = "critical",
                        TrainName = snap.TrainName,
                        Message = $"{signalReason}，列车正在制动 速度{snap.CurrentSpeed}km/h{nextStation}",
                        TimestampMs = NowMs()
                    });
                }
            }
            // ========== 已停车列车 ==========
            else if (!isRunning && !snap.FinishedSchedule)
            {
                // 到站停车不告警（由进站提示处理）
                bool isStationStop = !string.IsNullOrEmpty(snap.StopReasons) && snap.StopReasons.Contains("Station");

                if (snap.CanDepart && signalClosed)
                {
                    alerts.Add(new AlertInfo
                    {
                        Level = "critical",
                        TrainName = snap.TrainName,
                        Message = $"可发车但{signalReason}{nextStation}",
                        TimestampMs = NowMs()
                    });
                }
                else if (!snap.CanDepart && !isStationStop && signalClosed)
                {
                    // 非到站停车只有在下一座信号确实不能通行时才归因于信号/进路。
                    alerts.Add(new AlertInfo
                    {
                        Level = "critical",
                        TrainName = snap.TrainName,
                        Message = $"已停车 - {signalReason}{nextStation}",
                        TimestampMs = NowMs()
                    });
                }
                else if (!isStationStop && snap.NotMovingDuration.HasValue)
                {
                    // 其他非到站停车 - 停车超时告警（停车时长已由采集线程用游戏时间差值算出）
                    var stoppedSec = snap.NotMovingDuration.Value;
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

            // ========== 进站停车提示 ==========
            // 列车正在减速且停车原因是进站 -> info 级提示
            if (isRunning && (trainSlowing || trainBraking) &&
                !string.IsNullOrEmpty(snap.StopReasons) && snap.StopReasons.Contains("Station"))
            {
                alerts.Add(new AlertInfo
                {
                    Level = "info",
                    TrainName = snap.TrainName,
                    Message = $"即将进站停车{nextStation}",
                    TimestampMs = NowMs()
                });
            }

            // ========== 发车预告 ==========
            if (snap.CanDepart && snap.CurrentSpeed == 0 && !snap.FinishedSchedule)
            {
                var msg = "即将发车";
                // Train.Delay 会跨站累积；本站提示必须按本站计划发车时刻与游戏时钟计算。
                if (snap.CurrentDepartureScheduleDelaySeconds.HasValue &&
                    snap.CurrentDepartureScheduleDelaySeconds.Value > 60.0)
                {
                    msg += $"（延误{FormatDelay(snap.CurrentDepartureScheduleDelaySeconds.Value)}）";
                }
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
        /// 站台冲突检测：两列车前往同一站同一站台。
        /// 关键：判断到达/占用时间是否重叠，不重叠则不报（时间错开即不冲突）。
        /// 仅限下一站；若一辆已出站（下一站已变）则不会匹配。
        /// 注意：线路两端/出入图方向的站（站名含"方向"）不报，只有中间停车站才报。
        /// 时间窗（均相对当前游戏时间，单位秒）：
        ///   停站车占用至本次实际停站的计划发车时刻
        ///   接近车到达于 now + NextArrivalSec（到达时刻）
        ///   冲突 ⟺ 接近车到达早于停站车发车
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

                    // 方向站（线路两端/出入图方向，站名含"方向"）不报站台冲突
                    if (a.NextStationName.Contains("方向")) continue;

                    bool aStoppedAtStation = IsStationStop(a);
                    bool bStoppedAtStation = IsStationStop(b);
                    bool aRunning = a.IsOnBoard && a.CurrentSpeed > 0 && !aStoppedAtStation;
                    bool bRunning = b.IsOnBoard && b.CurrentSpeed > 0 && !bStoppedAtStation;

                    // 一列停在站内，一列接近：用时间窗判断是否真的冲突
                    if (aStoppedAtStation && bRunning)
                    {
                        if (HasTimeOverlap(stopped: a, approaching: b, out var msg))
                        {
                            alerts.Add(new AlertInfo
                            {
                                Level = "critical",
                                TrainName = $"{a.TrainName}/{b.TrainName}",
                                Message = $"站台冲突：{msg} {a.NextStationName} {a.NextPlatformNumber}台",
                                TimestampMs = NowMs()
                            });
                        }
                        // 时间不重叠（接近车在停站车发车后才到达）→ 不报
                    }
                    else if (bStoppedAtStation && aRunning)
                    {
                        if (HasTimeOverlap(stopped: b, approaching: a, out var msg))
                        {
                            alerts.Add(new AlertInfo
                            {
                                Level = "critical",
                                TrainName = $"{a.TrainName}/{b.TrainName}",
                                Message = $"站台冲突：{msg} {a.NextStationName} {a.NextPlatformNumber}台",
                                TimestampMs = NowMs()
                            });
                        }
                    }
                    else if (aRunning && bRunning)
                    {
                        // 两列都在运行中前往同一站台：无法精确判断（停车时长未知），保守报警告
                        alerts.Add(new AlertInfo
                        {
                            Level = "warning",
                            TrainName = $"{a.TrainName}/{b.TrainName}",
                            Message = $"站台可能冲突：均前往 {a.NextStationName} {a.NextPlatformNumber}台",
                            TimestampMs = NowMs()
                        });
                    }
                    else if (aStoppedAtStation && bStoppedAtStation)
                    {
                        // 两列都已停在同一个站台：真实冲突（同一站台两列车）
                        alerts.Add(new AlertInfo
                        {
                            Level = "critical",
                            TrainName = $"{a.TrainName}/{b.TrainName}",
                            Message = $"站台冲突：两列车同时在 {a.NextStationName} {a.NextPlatformNumber}台",
                            TimestampMs = NowMs()
                        });
                    }
                }
            }

            return alerts;
        }

        /// <summary>
        /// 判断停站车与接近车的时间窗是否重叠。
        /// 停站车占用 [已停, 发车]；接近车占用 [到达, 到达+预估停站]。
        /// 简化判定：接近车到达时刻早于停站车发车时刻 → 重叠（冲突）。
        /// 返回 false 表示时间错开，不冲突。msg 输出冲突描述。
        /// </summary>
        private static bool HasTimeOverlap(TrainSnapshot stopped, TrainSnapshot approaching, out string msg)
        {
            msg = "";
            // 停站车发车剩余秒数：来自当前 StationVisit 的 To；null/0 表示即将发车。
            // NextPrepareTime 是下一交路列车的准备时间，不能用于本站站台占用判断。
            double departIn = stopped.DepartureRemainingSeconds ?? 0;
            // 接近车到达剩余秒数
            double arriveIn = approaching.NextArrivalTimeTotalSeconds ?? 0;

            // 接近车到达晚于停站车发车 → 时间错开，不冲突
            // 留 10 秒余量：停站车发车后需驶离站台，接近车到达前站台应已腾空
            const double ClearMargin = 10.0;
            if (arriveIn >= departIn + ClearMargin)
            {
                return false;  // 不冲突
            }
            // 否则冲突，生成描述
            string depStr = departIn > 0 ? $"{(int)departIn}秒后发车" : "即将发车";
            string arrStr = arriveIn > 0 ? $"{(int)arriveIn}秒后到达" : "即将到达";
            msg = $"停站车{stopped.TrainName}{depStr}，接近车{approaching.TrainName}{arrStr}";
            return true;
        }

        private static bool IsStationStop(TrainSnapshot s)
        {
            return s.IsOnBoard && s.CurrentSpeed == 0 &&
                   !string.IsNullOrEmpty(s.StopReasons) && s.StopReasons.Contains("Station");
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
