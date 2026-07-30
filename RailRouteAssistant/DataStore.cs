using System;
using System.Collections.Generic;

namespace RailRouteAssistant
{
    /// <summary>
    /// 列车快照
    /// </summary>
    public class TrainSnapshot
    {
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
        public double? NotMovingSinceTimestamp;
        public int LookaheadCount;
        public bool HasValidRoute;
        public bool NeedsRouteAhead;       // 是否需要前方进路
        public bool HasActingSignal;       // 前方是否有信号灯
        public string SignalState;         // 信号灯状态描述
        public int SignalAllocationState = -1;  // 信号机自身的 AllocationState: -1=未知 0=Free 1=Allocated 2=Occupied 3=Shunting
        public int SignalType = -1;        // 信号机类型: 0=Manual 1=Auto 2=Shunting
        public bool SignalIsPendingRoute;  // 信号机是否有正在等待的进路
        public int FrontAllocationState = -1;   // 信号机 Front 轨道段分配状态: -1=未知 0=Free 1=Allocated 2=Occupied
        public int RouteTotalSteps;        // 进路总区间数
        public int RouteCurrentStep;       // 当前区间索引
        public int RouteRemainingSteps;    // 剩余区间数
        public int NextPlatformNumber;     // 下一站站台号
        public string NextStationName;
        public double? NextPrepareTimeTotalSeconds;
        public double? NextArrivalTimeTotalSeconds;
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
