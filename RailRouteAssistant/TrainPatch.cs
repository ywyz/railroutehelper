using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace RailRouteAssistant
{
    /// <summary>
    /// Train.Move 的 Harmony 补丁
    /// 每次列车移动时触发，在游戏线程中采集所有列车数据
    /// </summary>
    public static class TrainPatch
    {
        private static float _lastCollect = 0f;
        private static readonly object _collectLock = new object();
        private static bool _collecting = false;
        private static bool _firstCallLogged = false;
        private static float _lastDiagLog = 0f;
        private static int _callCount = 0;

        /// <summary>
        /// Train.Move 的 Postfix - 在每列车移动后被调用
        /// </summary>
        public static void Move_Postfix(object __instance)
        {
            _callCount++;
            try
            {
                // 首次调用日志
                if (!_firstCallLogged)
                {
                    _firstCallLogged = true;
                    Plugin.Log.LogInfo($"Move_Postfix 首次调用! train={__instance?.GetType().Name}");
                }

                // 防止重入
                if (_collecting) return;

                // 节流：每 0.5 秒采集一次
                var now = UnityEngine.Time.time;
                if (now - _lastCollect < Plugin.UpdateInterval.Value) return;

                lock (_collectLock)
                {
                    if (_collecting) return;
                    _collecting = true;
                }

                _lastCollect = now;
                CollectAllTrains();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Move_Postfix 异常: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                _collecting = false;
            }
        }

        /// <summary>
        /// 采集所有列车数据（也可被后台线程调用）
        /// </summary>
        public static void CollectAllTrains()
        {
            try
            {
                // 获取 Ctx.Deps
                var ctxType = AccessTools.TypeByName("Game.Context.Ctx");
                if (ctxType == null)
                {
                    LogDiag("Ctx 类型未找到");
                    DataStore.UpdateSnapshots(new List<TrainSnapshot>(), new List<AlertInfo>(), false);
                    return;
                }

                var depsGetter = AccessTools.PropertyGetter(ctxType, "Deps");
                if (depsGetter == null)
                {
                    LogDiag("Ctx.Deps getter 未找到");
                    DataStore.UpdateSnapshots(new List<TrainSnapshot>(), new List<AlertInfo>(), false);
                    return;
                }

                var controllers = depsGetter.Invoke(null, null);
                if (controllers == null)
                {
                    LogDiag("Ctx.Deps 为 null（游戏未进入地图）");
                    DataStore.UpdateSnapshots(new List<TrainSnapshot>(), new List<AlertInfo>(), false);
                    return;
                }

                // 获取 TrainRepository（实际在棋盘上的列车）
                var trGetter = AccessTools.PropertyGetter(controllers.GetType(), "TrainRepository");
                if (trGetter == null)
                {
                    LogDiag("TrainRepository getter 未找到");
                    DataStore.UpdateSnapshots(new List<TrainSnapshot>(), new List<AlertInfo>(), false);
                    return;
                }
                var trainRepo = trGetter.Invoke(controllers, null);
                if (trainRepo == null)
                {
                    LogDiag("TrainRepository 为 null");
                    DataStore.UpdateSnapshots(new List<TrainSnapshot>(), new List<AlertInfo>(), false);
                    return;
                }

                // 获取 Trains 属性（ICollection<Train>）
                var trainsGetter = AccessTools.PropertyGetter(trainRepo.GetType(), "Trains");
                if (trainsGetter == null)
                {
                    LogDiag("Trains getter 未找到, repo 类型: " + trainRepo.GetType().Name);
                    DataStore.UpdateSnapshots(new List<TrainSnapshot>(), new List<AlertInfo>(), false);
                    return;
                }

                var trainsObj = trainsGetter.Invoke(trainRepo, null);
                if (trainsObj == null)
                {
                    LogDiag("Trains 返回 null");
                    DataStore.UpdateSnapshots(new List<TrainSnapshot>(), new List<AlertInfo>(), true);
                    return;
                }

                var trains = (trainsObj as System.Collections.IEnumerable)?.Cast<object>().ToList();
                if (trains == null || trains.Count == 0)
                {
                    LogDiag($"TrainRepository.Trains 为空 (count={trains?.Count ?? -1})");
                    DataStore.UpdateSnapshots(new List<TrainSnapshot>(), new List<AlertInfo>(), true);
                    return;
                }

                // 采集每辆列车（不再过滤 OnBoard，全部显示）
                var snapshots = new List<TrainSnapshot>();
                int onBoardCount = 0;
                foreach (var train in trains)
                {
                    var snap = SnapshotTrain(train);
                    if (snap != null)
                    {
                        snapshots.Add(snap);
                        if (snap.IsOnBoard && !snap.IsDisposed)
                            onBoardCount++;
                    }
                }

                // 生成告警
                var alerts = AlertEngine.Evaluate(snapshots);

                // 存储
                DataStore.UpdateSnapshots(snapshots, alerts, true);

                LogDiag($"采集完成: 总列车={trains.Count}, 在棋盘上={onBoardCount}, 告警={alerts.Count}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"CollectAllTrains 异常: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void LogDiag(string msg)
        {
            var now = Environment.TickCount / 1000f;
            if (now - _lastDiagLog > 3f)
            {
                _lastDiagLog = now;
                Plugin.Log.LogInfo($"[诊断] {msg} (Move调用次数: {_callCount})");
            }
        }

        private static TrainSnapshot SnapshotTrain(object train)
        {
            try
            {
                var t = train.GetType();
                var snap = new TrainSnapshot { TrainName = SafeField<string>(t, train, "ReportingNumber") ?? "?" };

                snap.CurrentSpeed = SafeInt(t, train, "CurrentSpeedKmph");
                snap.MaxSpeed = SafeInt(t, train, "MaxSpeedKmph");
                snap.TargetSpeed = SafeFloat(t, train, "TargetSpeed");

                var delay = SafeTimeSpan(t, train, "Delay");
                snap.DelaySeconds = delay.TotalSeconds;

                snap.CanDepart = SafeBool(t, train, "CanDepart");
                snap.FinishedSchedule = SafeBool(t, train, "FinishedSchedule");
                snap.IsBrokenDown = SafeBool(t, train, "IsBrokenDown");
                snap.IsOnBoard = SafeBool(t, train, "OnBoard");
                snap.IsDisposed = SafeBool(t, train, "Disposed");
                snap.IsWaitingToBeSpawned = SafeBoolField(t, train, "IsWaitingToBeSpawned");

                var notMoving = SafeDateTime(t, train, "NotMovingSince");
                snap.NotMovingSinceTimestamp = notMoving?.ToUniversalTime().Subtract(new DateTime(1970, 1, 1)).TotalSeconds;

                // 前方预览区间
                var lookahead = SafeGetObject(t, train, "SegmentsInLookahead");
                snap.LookaheadCount = CountSegments(lookahead);

                // 前方信号灯状态
                CollectSignalInfo(t, train, snap);

                // 是否需要前方进路
                try
                {
                    var nraMethod = AccessTools.Method(t, "NeedsRouteAhead");
                    if (nraMethod != null)
                        snap.NeedsRouteAhead = Convert.ToBoolean(nraMethod.Invoke(train, null));
                }
                catch { }

                // 进路区间信息（按信号区间算）
                CollectRouteInfo(t, train, snap);

                // ContractLeg
                var leg = SafeGetObject(t, train, "ContractLeg");
                if (leg != null)
                {
                    var legType = leg.GetType();
                    snap.HasValidRoute = SafeBool(legType, leg, "HasValidRoute");
                    snap.NextPrepareTimeTotalSeconds = SafeTimeSpanNullable(legType, leg, "NextPrepareTime")?.TotalSeconds;
                    snap.NextArrivalTimeTotalSeconds = SafeTimeSpanNullable(legType, leg, "NextArrival")?.TotalSeconds;
                }

                // 下一站 + 站台号
                CollectNextStation(t, train, snap);

                snap.StopReasons = GetStopReasons(train);

                return snap;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"SnapshotTrain 异常: {ex.Message}");
                return null;
            }
        }

        // === 工具方法 ===

        private static T SafeField<T>(Type t, object obj, string name)
        {
            try
            {
                var field = AccessTools.Field(t, name);
                if (field == null) return default;
                var val = field.GetValue(obj);
                if (val is T tv) return tv;
                if (val != null) return (T)Convert.ChangeType(val, typeof(T));
                return default;
            }
            catch { return default; }
        }

        private static bool SafeBoolField(Type t, object obj, string name)
        {
            try
            {
                var field = AccessTools.Field(t, name);
                return field != null && Convert.ToBoolean(field.GetValue(obj));
            }
            catch { return false; }
        }

        private static string SafeString(Type t, object obj, string method)
        {
            try { return AccessTools.Method(t, method)?.Invoke(obj, null)?.ToString() ?? "?"; }
            catch { return "?"; }
        }

        private static int SafeInt(Type t, object obj, string prop)
        {
            try
            {
                var g = AccessTools.PropertyGetter(t, prop);
                return g != null ? Convert.ToInt32(g.Invoke(obj, null)) : 0;
            }
            catch { return 0; }
        }

        private static float SafeFloat(Type t, object obj, string prop)
        {
            try
            {
                var g = AccessTools.PropertyGetter(t, prop);
                return g != null ? Convert.ToSingle(g.Invoke(obj, null)) : 0f;
            }
            catch { return 0f; }
        }

        private static bool SafeBool(Type t, object obj, string prop)
        {
            try
            {
                var g = AccessTools.PropertyGetter(t, prop);
                return g != null && Convert.ToBoolean(g.Invoke(obj, null));
            }
            catch { return false; }
        }

        private static TimeSpan SafeTimeSpan(Type t, object obj, string prop)
        {
            try
            {
                var g = AccessTools.PropertyGetter(t, prop);
                return g != null ? (TimeSpan)g.Invoke(obj, null) : TimeSpan.Zero;
            }
            catch { return TimeSpan.Zero; }
        }

        private static TimeSpan? SafeTimeSpanNullable(Type t, object obj, string prop)
        {
            try
            {
                var g = AccessTools.PropertyGetter(t, prop);
                if (g == null) return null;
                var val = g.Invoke(obj, null);
                return val as TimeSpan?;
            }
            catch { return null; }
        }

        private static DateTime? SafeDateTime(Type t, object obj, string prop)
        {
            try
            {
                var g = AccessTools.PropertyGetter(t, prop);
                if (g == null) return null;
                var val = g.Invoke(obj, null);
                return val as DateTime?;
            }
            catch { return null; }
        }

        private static object SafeGetObject(Type t, object obj, string prop)
        {
            try { return AccessTools.PropertyGetter(t, prop)?.Invoke(obj, null); }
            catch { return null; }
        }

        private static int CountSegments(object segments)
        {
            if (segments == null) return 0;
            try
            {
                var countProp = segments.GetType().GetProperty("Count");
                if (countProp != null) return Convert.ToInt32(countProp.GetValue(segments));
                int count = 0;
                foreach (var _ in (System.Collections.IEnumerable)segments) count++;
                return count;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 采集下一站名和站台号
        /// </summary>
        private static void CollectNextStation(Type trainType, object train, TrainSnapshot snap)
        {
            try
            {
                // 调用 NextStationVisit 方法
                var nsvMethod = AccessTools.Method(trainType, "NextStationVisit");
                if (nsvMethod == null)
                {
                    // 回退到 NextStationVisits
                    var visits = SafeGetObject(trainType, train, "NextStationVisits");
                    snap.NextStationName = GetFirstStationName(visits);
                    return;
                }

                var visit = nsvMethod.Invoke(train, null);
                if (visit == null)
                {
                    snap.NextStationName = "";
                    return;
                }

                var visitType = visit.GetType();

                // 站名
                var stationProp = AccessTools.PropertyGetter(visitType, "Station");
                if (stationProp != null)
                {
                    var station = stationProp.Invoke(visit, null);
                    snap.NextStationName = station?.ToString() ?? "";
                }

                // 站台号
                var platformNumProp = AccessTools.PropertyGetter(visitType, "PlatformNumber");
                if (platformNumProp != null)
                {
                    var val = platformNumProp.Invoke(visit, null);
                    if (val != null)
                    {
                        // Nullable<int>
                        var nullable = val as int?;
                        if (nullable.HasValue)
                            snap.NextPlatformNumber = nullable.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"CollectNextStation 异常: {ex.Message}");
            }
        }

        private static string GetFirstStationName(object visits)
        {
            if (visits == null) return "";
            try
            {
                foreach (var visit in (System.Collections.IEnumerable)visits)
                {
                    var stationProp = AccessTools.PropertyGetter(visit.GetType(), "Station");
                    if (stationProp != null)
                    {
                        var station = stationProp.Invoke(visit, null);
                        if (station != null) return station.ToString();
                    }
                    return visit.ToString();
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 采集进路区间信息（按信号区间算，不是铁轨段数）
        /// </summary>
        private static void CollectRouteInfo(Type trainType, object train, TrainSnapshot snap)
        {
            try
            {
                // ActiveRouteRun 字段 (ServiceRouteRun)
                var arrField = AccessTools.Field(trainType, "ActiveRouteRun");
                if (arrField == null) return;

                var routeRun = arrField.GetValue(train);
                if (routeRun == null) return;

                var rrType = routeRun.GetType();

                // Steps 列表
                var stepsField = AccessTools.Field(rrType, "Steps");
                if (stepsField == null) return;

                var steps = stepsField.GetValue(routeRun) as System.Collections.IList;
                if (steps == null) return;

                snap.RouteTotalSteps = steps.Count;

                // CurrentStepIndex
                var idxField = AccessTools.Field(rrType, "CurrentStepIndex");
                if (idxField != null)
                {
                    snap.RouteCurrentStep = Convert.ToInt32(idxField.GetValue(routeRun));
                    snap.RouteRemainingSteps = snap.RouteTotalSteps - snap.RouteCurrentStep - 1;
                    if (snap.RouteRemainingSteps < 0) snap.RouteRemainingSteps = 0;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"CollectRouteInfo 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 采集前方信号灯状态
        /// </summary>
        private static void CollectSignalInfo(Type trainType, object train, TrainSnapshot snap)
        {
            try
            {
                // 获取 ActingSignalAhead 属性
                var asaGetter = AccessTools.PropertyGetter(trainType, "ActingSignalAhead");
                if (asaGetter == null) return;

                var signal = asaGetter.Invoke(train, null);
                if (signal == null)
                {
                    snap.HasActingSignal = false;
                    snap.SignalState = "前方无信号";
                    return;
                }

                snap.HasActingSignal = true;
                var sigType = signal.GetType();

                var parts = new List<string>();

                var isActingField = AccessTools.Field(sigType, "IsActing");
                var pendingRouteField = AccessTools.Field(sigType, "PendingRoute");

                // 信号开放/关闭/等待
                bool pending = false;
                if (pendingRouteField != null)
                    pending = Convert.ToBoolean(pendingRouteField.GetValue(signal));

                if (isActingField != null)
                {
                    var val = isActingField.GetValue(signal);
                    if (val is bool b)
                    {
                        parts.Add(b ? "开放" : "关闭");
                    }
                    else
                    {
                        var nullable = val as bool?;
                        if (nullable.HasValue)
                            parts.Add(nullable.Value ? "开放" : "关闭");
                        else
                            parts.Add("未确定");
                    }
                }

                if (pending)
                    parts.Add("等待");

                var pendingRouteManualField = AccessTools.Field(sigType, "PendingRouteManual");
                if (pendingRouteManualField != null)
                {
                    var val = Convert.ToBoolean(pendingRouteManualField.GetValue(signal));
                    if (val) parts.Add("手动");
                }

                snap.SignalState = string.Join(" ", parts);
                if (string.IsNullOrEmpty(snap.SignalState))
                    snap.SignalState = signal.ToString();
            }
            catch (Exception ex)
            {
                snap.SignalState = $"信号读取异常: {ex.Message}";
            }
        }

        private static string GetStopReasons(object train)
        {
            try
            {
                var t = train.GetType();
                // StopReasons 是字段(Field)不是属性(Property)
                var field = AccessTools.Field(t, "StopReasons");
                if (field == null) return "";

                var stopReasons = field.GetValue(train);
                if (stopReasons == null) return "";

                var srType = stopReasons.GetType();
                var countProp = srType.GetProperty("Count");
                int count = countProp != null ? Convert.ToInt32(countProp.GetValue(stopReasons)) : 0;
                if (count == 0) return "";

                var firstMethod = AccessTools.Method(srType, "get_First");
                if (firstMethod != null)
                {
                    var first = firstMethod.Invoke(stopReasons, null);
                    if (first != null) return first.ToString();
                }
                return $"{count} 个原因";
            }
            catch { return ""; }
        }
    }
}
