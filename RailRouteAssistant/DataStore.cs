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
        public int RouteTotalSteps;        // 进路总区间数
        public int RouteCurrentStep;       // 当前区间索引
        public int RouteRemainingSteps;    // 剩余区间数
        public string NextStationName;
        public double? NextPrepareTimeTotalSeconds;
        public double? NextArrivalTimeTotalSeconds;
        public string StopReasons;
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

        public static (List<TrainSnapshot> snapshots, List<AlertInfo> alerts, DateTime lastUpdate, bool gameReady) GetCurrent()
        {
            lock (_lock)
            {
                return (new List<TrainSnapshot>(_snapshots), new List<AlertInfo>(_alerts), _lastUpdate, _gameReady);
            }
        }
    }
}
