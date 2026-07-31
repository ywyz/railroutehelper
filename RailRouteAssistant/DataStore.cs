using System;
using System.Collections.Generic;

namespace RailRouteAssistant
{
    /// <summary>
    /// 列车快照
    /// </summary>
    public class TrainSnapshot
    {
        public string TrainId;
        public string TrainName;
        public int CurrentSpeed;
        public int MaxSpeed;
        public float TargetSpeed;
        public double DelaySeconds;
        public bool CanDepart;
        public bool FinishedSchedule;
        public bool IsBrokenDown;
        public bool IsOnBoard;
        public bool IsDisposed;
        public bool IsWaitingToBeSpawned;
        public double? NotMovingSinceGameTime;  // 停车起始的游戏内绝对时间（TimeSpan.TotalSeconds）
        public double? NotMovingDuration;       // 停车时长（秒），由采集线程用游戏时间差值算出
        public int LookaheadCount;
        public bool HasValidRoute;
        public bool NeedsRouteAhead;       // 游戏原始的远端进路需求，仅作诊断
        public bool HasActingSignal;       // 是否已找到紧邻的下一座同向运营信号
        public string SignalState;         // 紧邻下一座信号的状态描述
        public int SignalAllocationState = -1;  // 下一座信号: -1=未知 0=Free 1=Allocated 2=Occupied 3=Shunting
        public int SignalType = -1;        // 信号机类型: 0=Manual 1=Auto 2=Shunting
        public bool SignalIsPendingRoute;  // 下一座信号是否有正在等待的进路
        public int FrontAllocationState = -1;   // 兼容旧 API；Semaphore.Front 是接近侧连接，不用于告警
        public int RouteTotalSteps;        // 进路总区间数
        public int RouteCurrentStep;       // 当前区间索引
        public int RouteRemainingSteps;    // 剩余区间数
        public int NextPlatformNumber;     // 下一站站台号
        public string NextStationName;
        public bool NextStationNonStop;     // 下一次访问是否为通过站
        public double? NextPrepareTimeTotalSeconds;  // 距发车的剩余秒数（= NextPrepareGameTime - 当前游戏时间）
        public double? NextArrivalTimeTotalSeconds;  // 距到达的剩余秒数（= NextArrivalGameTime - 当前游戏时间）
        internal double? NextPrepareGameTime;        // 原始游戏内绝对时间（TimeSpan.TotalSeconds），采集后换算成剩余
        internal double? NextArrivalGameTime;        // 原始游戏内绝对时间（TimeSpan.TotalSeconds），采集后换算成剩余

        // 最近一次实际访问。游戏在到站/通过后会把本站从 NextStationVisits 移除，
        // 因而这里才是播报本站与识别通过站的可靠来源。
        public int ActualVisitCount;
        public int ScheduledVisitCount;      // 本趟列车地图内计划访问总数，用于识别首站/末站
        // 最近一次实际访问（或当前停站）在 ScheduledVisits 中的零基索引；-1 表示未知。
        public int CurrentScheduledVisitIndex = -1;
        public string LastVisitStationName;
        public int LastVisitPlatformNumber;
        public bool LastVisitNonStop;
        public int LastVisitStopDurationMinutes;
        public bool LastVisitDeparted;
        public bool RequiresDirectionChange;  // 游戏标记：本次到站后需调向
        internal double? LastVisitArrivalGameTime;
        internal double? LastVisitDepartureGameTime;
        // 最近一次实际到站的正晚点秒数：实际到达游戏时钟 - StationVisit.From。
        // 负数表示早点、正数表示晚点，首次观察到该次访问时固定。
        public double? LastArrivalScheduleDeviationSeconds;
        // 最近一次实际停站的发车晚点秒数：以该次 StationVisit.To 与游戏时钟计算，
        // 且在检测到 Departed 时固定，绝不使用会跨站累积的 Train.Delay。
        public double? LastDepartureScheduleDelaySeconds;

        // 当前计划停站。仅在列车真正停站时使用；To 为游戏内绝对发车时刻。
        public string CurrentStationName;
        public int CurrentPlatformNumber;
        public int CurrentStopDurationMinutes;
        public double? DepartureRemainingSeconds;
        // 当前停站相对计划发车时刻的晚点秒数，仅供“即将发车”提示使用。
        public double? CurrentDepartureScheduleDelaySeconds;
        internal double? CurrentDepartureGameTime;
        public string StopReasons;
        public List<string> RouteStepTrackIds = new List<string>(); // 前方进路步骤的轨道标识
    }

    /// <summary>
    /// 告警信息
    /// </summary>
    public class AlertInfo
    {
        public string Level;    // "critical" / "warning" / "info"
        public string TrainName;
        public string Message;
        public long TimestampMs;
    }

    /// <summary>
    /// 线程安全的数据存储 - 游戏线程写入，HTTP 线程读取
    /// </summary>
    public static class DataStore
    {
        private static readonly object _lock = new object();
        private static List<TrainSnapshot> _snapshots = new List<TrainSnapshot>();
        private static List<AlertInfo> _alerts = new List<AlertInfo>();
        private static DateTime _lastUpdate = DateTime.MinValue;
        private static bool _gameReady = false;
        private static double? _gameTimeSeconds = null;  // 游戏内模拟时钟（TimeSpan.TotalSeconds）

        public static void UpdateSnapshots(List<TrainSnapshot> snapshots, List<AlertInfo> alerts, bool gameReady)
        {
            lock (_lock)
            {
                _snapshots = snapshots ?? new List<TrainSnapshot>();
                _alerts = alerts ?? new List<AlertInfo>();
                _lastUpdate = DateTime.Now;
                _gameReady = gameReady;
            }
        }

        /// <summary>更新游戏内时间（秒），由采集线程调用</summary>
        public static void UpdateGameTime(double? seconds)
        {
            lock (_lock) { _gameTimeSeconds = seconds; }
        }

        public static (List<TrainSnapshot> snapshots, List<AlertInfo> alerts, DateTime lastUpdate, bool gameReady) GetCurrent()
        {
            lock (_lock)
            {
                return (new List<TrainSnapshot>(_snapshots), new List<AlertInfo>(_alerts), _lastUpdate, _gameReady);
            }
        }

        /// <summary>获取游戏内时间（TimeSpan.TotalSeconds），可能为 null</summary>
        public static double? GetGameTimeSeconds()
        {
            lock (_lock) { return _gameTimeSeconds; }
        }
    }
}
