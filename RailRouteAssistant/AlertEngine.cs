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

            // ========== 信号预警逻辑（两级提前 + 一级临界） ==========
            //
            // 时间线：
            //   t1: 信号机在远处，列车未减速（TargetSpeed ≈ CurrentSpeed）
            //   t2: 信号机进入制动距离，TargetSpeed 开始下降但仍 > 0
            //   t3: TargetSpeed ≈ 0，列车大幅减速/停车
            //
            // 预警级别：
            //   warning  - 信号关闭但列车尚未减速（最早反应窗口）
            //   warning  - TargetSpeed 开始下降（列车即将减速）
            //   critical - TargetSpeed ≈ 0（列车已在减速/停车）

            bool hasSignal = snap.HasActingSignal;
            bool isRunning = snap.CurrentSpeed > 0;
            bool trainBraking = snap.TargetSpeed < 0.5f;       // TargetSpeed ≈ 0
            bool trainSlowing = !trainBraking && snap.TargetSpeed > 0.5f && snap.TargetSpeed < snap.CurrentSpeed - 5; // TargetSpeed 开始下降

            // 信号是否已关闭：仅看信号机自身和前方轨道的 AllocationState
            // AllocationState: -1=未知, 0=Free, 1=Allocated, 2=Occupied, 3=Shunting
            // 注意：FrontAllocationState==2(Occupied) 不算信号关闭，因为列车自己可能就在这段轨道上
            bool signalClosed = false;
            string signalReason = "";
            if (hasSignal)
            {
                if (snap.SignalAllocationState == 0)
                {
                    signalClosed = true;
                    signalReason = "信号未开放";
                }
                else if (snap.FrontAllocationState == 0)
                {
                    signalClosed = true;
                    signalReason = "前方轨道未配进路";
                }
            }

            if (isRunning)
            {
                // 到站减速不告警（由进站提示处理）
                bool isStationStop = !string.IsNullOrEmpty(snap.StopReasons) && snap.StopReasons.Contains("Station");

                // ========== Level 1 (warning): 信号关闭但列车尚未减速 ==========
                // 信号机 AllocationState=Free 或前方轨道 Free，但列车 TargetSpeed 仍正常
                // 这是玩家最早的反应窗口（仅当 AllocationState 可读时生效）
                if (signalClosed && !trainBraking && !trainSlowing)
                {
                    alerts.Add(new AlertInfo
                    {
                        Level = "warning",
                        TrainName = snap.TrainName,
                        Message = $"{signalReason} 速度{snap.CurrentSpeed}km/h{nextStation}",
                        TimestampMs = NowMs()
                    });
                }

                // ========== Level 2 (warning): 列车开始降速（TargetSpeed 下降但仍 > 0）==========
                // 这是降速早期，玩家还有时间反应。排除到站减速。直接提示即将停车。
                if (trainSlowing && !isStationStop)
                {
                    alerts.Add(new AlertInfo
                    {
                        Level = "warning",
                        TrainName = snap.TrainName,
                        Message = $"前方信号关闭 即将停车 速度{snap.CurrentSpeed}km/h{nextStation}",
                        TimestampMs = NowMs()
                    });
                }

                // ========== Level 3 (critical): 列车已制动（TargetSpeed ≈ 0）==========
                // 排除到站减速。不依赖 signalClosed，统一用 TargetSpeed<=0.5 判断。
                if (trainBraking && !isStationStop)
                {
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
                    else
                    {
                        alerts.Add(new AlertInfo
                        {
                            Level = "critical",
                            TrainName = snap.TrainName,
                            Message = $"前方信号关闭 即将停车 速度{snap.CurrentSpeed}km/h{nextStation}",
                            TimestampMs = NowMs()
                        });
                    }
                }
            }
            // ========== 无信号机信息时的回退逻辑 ==========
            else if (isRunning && !hasSignal && trainBraking)
            {
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
                else if (snap.CurrentSpeed <= 10)
                {
                    alerts.Add(new AlertInfo
                    {
                        Level = "critical",
                        TrainName = snap.TrainName,
                        Message = $"前方信号关闭 速度{snap.CurrentSpeed}km/h 即将停车{nextStation}",
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
            // ========== 已停车列车 ==========
            else if (!isRunning && !snap.FinishedSchedule)
            {
                // 到站停车不告警（由进站提示处理）
                bool isStationStop = !string.IsNullOrEmpty(snap.StopReasons) && snap.StopReasons.Contains("Station");
                // 统一用 TargetSpeed<=0.5 判断信号关闭，与桌面程序显示逻辑一致
                bool stoppedBySignal = snap.TargetSpeed <= 0.5f;

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
                else if (snap.CanDepart && stoppedBySignal && !isStationStop)
                {
                    // 可发车时刻已到但信号关闭
                    alerts.Add(new AlertInfo
                    {
                        Level = "critical",
                        TrainName = snap.TrainName,
                        Message = $"已停车 - 前方信号关闭{nextStation}",
                        TimestampMs = NowMs()
                    });
                }
                else if (!snap.CanDepart && !isStationStop)
                {
                    // 非到站停车且不能发车 - 信号/进路问题导致停车
                    if (snap.LookaheadCount == 0)
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
                        alerts.Add(new AlertInfo
                        {
                            Level = "critical",
                            TrainName = snap.TrainName,
                            Message = $"已停车 - 前方信号关闭{nextStation}",
                            TimestampMs = NowMs()
                        });
                    }
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
        /// 注意：线路两端/出入图方向的站（站名含"方向"）不报，只有中间停车站才报
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
