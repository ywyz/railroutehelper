using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace RailRouteAssistant
{
    /// <summary>
    /// 反射缓存 - 只查找一次，后续直接使用
    /// </summary>
    internal static class ReflectCache
    {
        private static bool _initialized = false;
        private static bool _initFailed = false;

        // 游戏类型
        internal static Type CtxType;
        internal static Type TrainType;
        internal static Type SemaphoreType;
        internal static Type ConnectionType;
        internal static Type TimeType;

        // Ctx.Deps
        internal static MethodInfo CtxDepsGetter;

        // 游戏时间访问链：Ctx.Deps(Game.Context.IControllers) -> GameControllers(Game.IGameControllers) -> TimeController(Game.Time.ITimeController) -> CurrentTime(TimeSpan)
        internal static Type IControllersType;
        internal static Type IGameControllersType;
        internal static Type ITimeControllerType;
        internal static PropertyInfo IControllersGameControllers;
        internal static PropertyInfo GameControllersTimeController;
        internal static PropertyInfo TimeControllerCurrentTime;

        // Train 属性 getter
        internal static PropertyInfo TrainCurrentSpeed, TrainMaxSpeed, TrainTargetSpeed, TrainDelay;
        internal static PropertyInfo TrainCanDepart, TrainFinished, TrainBrokenDown, TrainOnBoard, TrainDisposed;
        internal static PropertyInfo TrainNotMovingSince, TrainSegmentsInLookahead, TrainActingSignal;
        internal static PropertyInfo TrainContractLeg, TrainNextStationVisits;

        // Train 字段
        internal static FieldInfo TrainReportingNumber, TrainIsWaitingToBeSpawned, TrainActiveRouteRun, TrainStopReasons;

        // Train 方法
        internal static MethodInfo TrainNeedsRouteAhead, TrainNextStationVisit;

        // ContractLeg 属性（延迟初始化）

        // Semaphore 属性
        internal static PropertyInfo SemAllocationState, SemType, SemIsPendingRoute, SemFront;

        // Connection 属性
        internal static PropertyInfo ConnAllocationState, ConnName;

        // Station 属性
        internal static PropertyInfo StationFriendlyName, StationName;

        // StationVisit 属性
        internal static PropertyInfo VisitStation, VisitPlatformNumber;

        // RouteRun 字段
        internal static FieldInfo RRSteps, RRCurrentStepIndex;

        // ResolvedStep 字段
        internal static FieldInfo StepDestination;

        // UnityEngine.Time
        internal static MethodInfo TimeGetter;

        // StopReasons 相关
        internal static PropertyInfo SRCount;
        internal static MethodInfo SRGetFirst;

        public static bool Init()
        {
            if (_initialized) return !_initFailed;
            _initialized = true;

            try
            {
                // 游戏类型
                CtxType = AccessTools.TypeByName("Game.Context.Ctx");
                TrainType = AccessTools.TypeByName("Game.Train.Train");
                SemaphoreType = AccessTools.TypeByName("Game.Railroad.Semaphore");
                ConnectionType = AccessTools.TypeByName("Game.Railroad.Connection");
                TimeType = AccessTools.TypeByName("UnityEngine.Time");

                if (CtxType != null)
                    CtxDepsGetter = AccessTools.PropertyGetter(CtxType, "Deps");

                if (TrainType != null)
                {
                    var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    TrainCurrentSpeed = TrainType.GetProperty("CurrentSpeedKmph", bf);
                    TrainMaxSpeed = TrainType.GetProperty("MaxSpeedKmph", bf);
                    TrainTargetSpeed = TrainType.GetProperty("TargetSpeed", bf);
                    TrainDelay = TrainType.GetProperty("Delay", bf);
                    TrainCanDepart = TrainType.GetProperty("CanDepart", bf);
                    TrainFinished = TrainType.GetProperty("FinishedSchedule", bf);
                    TrainBrokenDown = TrainType.GetProperty("IsBrokenDown", bf);
                    TrainOnBoard = TrainType.GetProperty("OnBoard", bf);
                    TrainDisposed = TrainType.GetProperty("Disposed", bf);
                    TrainNotMovingSince = TrainType.GetProperty("NotMovingSince", bf);
                    TrainSegmentsInLookahead = TrainType.GetProperty("SegmentsInLookahead", bf);
                    TrainActingSignal = TrainType.GetProperty("ActingSignalAhead", bf);
                    TrainContractLeg = TrainType.GetProperty("ContractLeg", bf);
                    TrainNextStationVisits = TrainType.GetProperty("NextStationVisits", bf);

                    TrainReportingNumber = AccessTools.Field(TrainType, "ReportingNumber");
                    TrainIsWaitingToBeSpawned = AccessTools.Field(TrainType, "IsWaitingToBeSpawned");
                    TrainActiveRouteRun = AccessTools.Field(TrainType, "ActiveRouteRun");
                    TrainStopReasons = AccessTools.Field(TrainType, "StopReasons");

                    TrainNeedsRouteAhead = AccessTools.Method(TrainType, "NeedsRouteAhead");
                    TrainNextStationVisit = AccessTools.Method(TrainType, "NextStationVisit");
                }

                // Semaphore（注意：Semaphore 上 AllocationState 只有 set，getter 在基类 Node）
                if (SemaphoreType != null)
                {
                    var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    SemAllocationState = SemaphoreType.GetProperty("AllocationState", bf);
                    // 若 Semaphore 上的 AllocationState 没有 getter（set-only 遮蔽），从基类 Node 取
                    if (SemAllocationState == null || SemAllocationState.GetMethod == null)
                    {
                        var nodeType = AccessTools.TypeByName("Game.Railroad.Node");
                        if (nodeType != null)
                            SemAllocationState = nodeType.GetProperty("AllocationState", bf);
                    }
                    SemType = SemaphoreType.GetProperty("Type", bf);
                    SemIsPendingRoute = SemaphoreType.GetProperty("IsPendingRoute", bf);
                    SemFront = SemaphoreType.GetProperty("Front", bf);
                }

                // Connection（同样可能继承自 Node，确保拿到有 getter 的属性）
                if (ConnectionType != null)
                {
                    var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    ConnAllocationState = ConnectionType.GetProperty("AllocationState", bf);
                    if (ConnAllocationState == null || ConnAllocationState.GetMethod == null)
                    {
                        var nodeType = AccessTools.TypeByName("Game.Railroad.Node");
                        if (nodeType != null)
                            ConnAllocationState = nodeType.GetProperty("AllocationState", bf);
                    }
                    ConnName = ConnectionType.GetProperty("Name", bf);
                }

                // UnityEngine.Time
                if (TimeType != null)
                    TimeGetter = AccessTools.PropertyGetter(TimeType, "time");

                // 游戏时间访问链
                IControllersType = AccessTools.TypeByName("Game.Context.IControllers");
                IGameControllersType = AccessTools.TypeByName("Game.IGameControllers");
                ITimeControllerType = AccessTools.TypeByName("Game.Time.ITimeController");
                var ifBf = BindingFlags.Public | BindingFlags.Instance;
                if (IControllersType != null)
                    IControllersGameControllers = IControllersType.GetProperty("GameControllers", ifBf);
                if (IGameControllersType != null)
                    GameControllersTimeController = IGameControllersType.GetProperty("TimeController", ifBf);
                if (ITimeControllerType != null)
                    TimeControllerCurrentTime = ITimeControllerType.GetProperty("CurrentTime", ifBf);

                Plugin.Log.LogInfo($"[ReflectCache] 初始化完成: Ctx={CtxType!=null}, Train={TrainType!=null}, Sem={SemaphoreType!=null}, Time={ITimeControllerType!=null}");
                _initFailed = (TrainType == null);
                return !_initFailed;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[ReflectCache] 初始化失败: {ex.Message}");
                _initFailed = true;
                return false;
            }
        }

        /// <summary>
        /// 延迟初始化 RouteRun/ResolvedStep 类型（首次遇到时缓存）
        /// </summary>
        internal static void EnsureRouteTypes(Type routeRunType, Type stepType)
        {
            if (RRSteps == null && routeRunType != null)
            {
                RRSteps = AccessTools.Field(routeRunType, "Steps");
                RRCurrentStepIndex = AccessTools.Field(routeRunType, "CurrentStepIndex");
            }
            if (StepDestination == null && stepType != null)
            {
                StepDestination = stepType.GetField("Destination", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
        }

        /// <summary>
        /// 延迟初始化 StationVisit/Station 属性
        /// </summary>
        internal static void EnsureStationTypes(Type visitType, Type stationType)
        {
            var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            if (VisitStation == null && visitType != null)
            {
                VisitStation = visitType.GetProperty("Station", bf);
                VisitPlatformNumber = visitType.GetProperty("PlatformNumber", bf);
            }
            if (StationFriendlyName == null && stationType != null)
            {
                StationFriendlyName = stationType.GetProperty("FriendlyName", bf);
                StationName = stationType.GetProperty("Name", bf);
            }
        }

        /// <summary>
        /// 延迟初始化 StopReasons 相关
        /// </summary>
        internal static void EnsureStopReasonTypes(Type srType)
        {
            var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            if (SRCount == null && srType != null)
            {
                SRCount = srType.GetProperty("Count", bf);
                SRGetFirst = AccessTools.Method(srType, "get_First");
            }
        }

        // === 快速取值方法 ===

        internal static T GetProp<T>(object obj, PropertyInfo prop, T def = default)
        {
            if (prop == null || obj == null) return def;
            try
            {
                var val = prop.GetValue(obj);
                if (val is T tv) return tv;
                if (val != null) return (T)Convert.ChangeType(val, typeof(T));
                return def;
            }
            catch { return def; }
        }

        internal static T GetField<T>(object obj, FieldInfo field, T def = default)
        {
            if (field == null || obj == null) return def;
            try
            {
                var val = field.GetValue(obj);
                if (val is T tv) return tv;
                if (val != null) return (T)Convert.ChangeType(val, typeof(T));
                return def;
            }
            catch { return def; }
        }
    }

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

        public static void Move_Postfix(object __instance)
        {
            _callCount++;
            try
            {
                if (!_firstCallLogged)
                {
                    _firstCallLogged = true;
                    Plugin.Log.LogInfo($"Move_Postfix 首次调用! train={__instance?.GetType().Name}");
                }

                if (_collecting) return;

                var now = GetUnityTime();
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

        public static void CollectAllTrains()
        {
            try
            {
                if (!ReflectCache.Init())
                {
                    DataStore.UpdateSnapshots(new List<TrainSnapshot>(), new List<AlertInfo>(), false);
                    return;
                }

                var controllers = ReflectCache.CtxDepsGetter?.Invoke(null, null);
                if (controllers == null)
                {
                    LogDiag("Ctx.Deps 为 null（游戏未进入地图）");
                    DataStore.UpdateSnapshots(new List<TrainSnapshot>(), new List<AlertInfo>(), false);
                    DataStore.UpdateGameTime(null);
                    return;
                }

                // 读取游戏内模拟时钟：Ctx.Deps -> GameControllers -> TimeController -> CurrentTime(TimeSpan)
                var gameTimeSec = TryGetGameTime(controllers);
                DataStore.UpdateGameTime(gameTimeSec);

                // TrainRepository
                var trGetter = AccessTools.PropertyGetter(controllers.GetType(), "TrainRepository");
                if (trGetter == null) return;
                var trainRepo = trGetter.Invoke(controllers, null);
                if (trainRepo == null) return;

                // Trains
                var trainsGetter = AccessTools.PropertyGetter(trainRepo.GetType(), "Trains");
                if (trainsGetter == null) return;
                var trainsObj = trainsGetter.Invoke(trainRepo, null);
                if (trainsObj == null)
                {
                    DataStore.UpdateSnapshots(new List<TrainSnapshot>(), new List<AlertInfo>(), true);
                    return;
                }

                var trains = (trainsObj as System.Collections.IEnumerable)?.Cast<object>().ToList();
                if (trains == null || trains.Count == 0)
                {
                    DataStore.UpdateSnapshots(new List<TrainSnapshot>(), new List<AlertInfo>(), true);
                    return;
                }

                // 采集
                var snapshots = new List<TrainSnapshot>(trains.Count);
                foreach (var train in trains)
                {
                    var snap = SnapshotTrain(train);
                    if (snap != null) snapshots.Add(snap);
                }

                // 用游戏内时间换算"剩余秒数"和"停车时长"
                // （NextPrepare/NextArrival 是游戏内绝对时间点，NotMovingSince 也是游戏内绝对时间）
                if (gameTimeSec.HasValue)
                {
                    double now = gameTimeSec.Value;
                    foreach (var s in snapshots)
                    {
                        // 距发车剩余秒数（仅当未来时间点有效）
                        if (s.NextPrepareGameTime.HasValue)
                        {
                            var rem = s.NextPrepareGameTime.Value - now;
                            s.NextPrepareTimeTotalSeconds = rem > 0 ? rem : 0;
                        }
                        // 距到达剩余秒数
                        if (s.NextArrivalGameTime.HasValue)
                        {
                            var rem = s.NextArrivalGameTime.Value - now;
                            s.NextArrivalTimeTotalSeconds = rem > 0 ? rem : 0;
                        }
                        // 停车时长 = 当前游戏时间 - 停车起始时间
                        if (s.NotMovingSinceGameTime.HasValue)
                        {
                            var dur = now - s.NotMovingSinceGameTime.Value;
                            s.NotMovingDuration = dur > 0 ? dur : 0;
                        }
                    }
                }

                var alerts = AlertEngine.Evaluate(snapshots);
                DataStore.UpdateSnapshots(snapshots, alerts, true);

                LogDiag($"采集完成: 列车={snapshots.Count}, 告警={alerts.Count}");
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

        private static float GetUnityTime()
        {
            try
            {
                return ReflectCache.TimeGetter != null
                    ? Convert.ToSingle(ReflectCache.TimeGetter.Invoke(null, null))
                    : Environment.TickCount / 1000f;
            }
            catch { return Environment.TickCount / 1000f; }
        }

        /// <summary>
        /// 读取游戏内模拟时钟：Ctx.Deps(IControllers) -> GameControllers(IGameControllers) -> TimeController(ITimeController) -> CurrentTime(TimeSpan)
        /// 返回 TotalSeconds，失败返回 null
        /// </summary>
        private static double? TryGetGameTime(object controllers)
        {
            try
            {
                if (controllers == null) return null;
                if (ReflectCache.IControllersGameControllers == null ||
                    ReflectCache.GameControllersTimeController == null ||
                    ReflectCache.TimeControllerCurrentTime == null) return null;

                var gameControllers = ReflectCache.IControllersGameControllers.GetValue(controllers, null);
                if (gameControllers == null) return null;

                var timeController = ReflectCache.GameControllersTimeController.GetValue(gameControllers, null);
                if (timeController == null) return null;

                var ct = ReflectCache.TimeControllerCurrentTime.GetValue(timeController, null) as TimeSpan?;
                return ct.HasValue ? ct.Value.TotalSeconds : (double?)null;
            }
            catch { return null; }
        }

        private static TrainSnapshot SnapshotTrain(object train)
        {
            try
            {
                var snap = new TrainSnapshot
                {
                    TrainName = ReflectCache.GetField(train, ReflectCache.TrainReportingNumber, "?") ?? "?",
                    CurrentSpeed = ReflectCache.GetProp(train, ReflectCache.TrainCurrentSpeed, 0),
                    MaxSpeed = ReflectCache.GetProp(train, ReflectCache.TrainMaxSpeed, 0),
                    TargetSpeed = ReflectCache.GetProp(train, ReflectCache.TrainTargetSpeed, 0f),
                    CanDepart = ReflectCache.GetProp(train, ReflectCache.TrainCanDepart, false),
                    FinishedSchedule = ReflectCache.GetProp(train, ReflectCache.TrainFinished, false),
                    IsBrokenDown = ReflectCache.GetProp(train, ReflectCache.TrainBrokenDown, false),
                    IsOnBoard = ReflectCache.GetProp(train, ReflectCache.TrainOnBoard, false),
                    IsDisposed = ReflectCache.GetProp(train, ReflectCache.TrainDisposed, false),
                    IsWaitingToBeSpawned = ReflectCache.GetField(train, ReflectCache.TrainIsWaitingToBeSpawned, false),
                };

                // Delay
                if (ReflectCache.TrainDelay != null)
                {
                    var delayVal = ReflectCache.TrainDelay.GetValue(train);
                    if (delayVal is TimeSpan ts) snap.DelaySeconds = ts.TotalSeconds;
                }

                // NotMovingSince —— 游戏内绝对时间（TimeSpan?），列车移动时为 null
                if (ReflectCache.TrainNotMovingSince != null)
                {
                    var nm = ReflectCache.TrainNotMovingSince.GetValue(train) as TimeSpan?;
                    if (nm.HasValue)
                        snap.NotMovingSinceGameTime = nm.Value.TotalSeconds;  // 游戏内绝对秒数
                }

                // SegmentsInLookahead
                var lookahead = ReflectCache.TrainSegmentsInLookahead?.GetValue(train);
                snap.LookaheadCount = CountSegments(lookahead);

                // 信号
                CollectSignalInfo(train, snap);

                // NeedsRouteAhead
                if (ReflectCache.TrainNeedsRouteAhead != null)
                {
                    try { snap.NeedsRouteAhead = Convert.ToBoolean(ReflectCache.TrainNeedsRouteAhead.Invoke(train, null)); }
                    catch { }
                }

                // 进路
                CollectRouteInfo(train, snap);

                // ContractLeg
                var leg = ReflectCache.TrainContractLeg?.GetValue(train);
                if (leg != null)
                {
                    var legType = leg.GetType();
                    var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    snap.HasValidRoute = ReflectCache.GetProp(leg, legType.GetProperty("HasValidRoute", bf), false);
                    var npt = legType.GetProperty("NextPrepareTime", bf);
                    if (npt != null) snap.NextPrepareGameTime = (npt.GetValue(leg) as TimeSpan?)?.TotalSeconds;
                    var nat = legType.GetProperty("NextArrival", bf);
                    if (nat != null) snap.NextArrivalGameTime = (nat.GetValue(leg) as TimeSpan?)?.TotalSeconds;
                }

                // 下一站
                CollectNextStation(train, snap);

                // StopReasons
                snap.StopReasons = GetStopReasons(train);

                return snap;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"SnapshotTrain 异常: {ex.Message}");
                return null;
            }
        }

        private static int CountSegments(object segments)
        {
            if (segments == null) return 0;
            try
            {
                var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var countProp = segments.GetType().GetProperty("Count", bf);
                if (countProp != null) return Convert.ToInt32(countProp.GetValue(segments));
                int count = 0;
                foreach (var _ in (System.Collections.IEnumerable)segments) count++;
                return count;
            }
            catch { return 0; }
        }

        private static void CollectSignalInfo(object train, TrainSnapshot snap)
        {
            try
            {
                var signal = ReflectCache.TrainActingSignal?.GetValue(train);
                if (signal == null)
                {
                    snap.HasActingSignal = false;
                    snap.SignalState = "前方无信号";
                    return;
                }

                snap.HasActingSignal = true;
                snap.SignalState = signal.ToString();

                // 信号机 AllocationState
                if (ReflectCache.SemAllocationState != null)
                {
                    var val = ReflectCache.SemAllocationState.GetValue(signal);
                    if (val != null) snap.SignalAllocationState = Convert.ToInt32(val);
                }

                // 信号机 Type
                if (ReflectCache.SemType != null)
                {
                    var val = ReflectCache.SemType.GetValue(signal);
                    if (val != null) snap.SignalType = Convert.ToInt32(val);
                }

                // IsPendingRoute
                if (ReflectCache.SemIsPendingRoute != null)
                {
                    snap.SignalIsPendingRoute = Convert.ToBoolean(ReflectCache.SemIsPendingRoute.GetValue(signal));
                }

                // Front Connection AllocationState
                if (ReflectCache.SemFront != null && ReflectCache.SemFront.GetMethod != null)
                {
                    var frontConn = ReflectCache.SemFront.GetValue(signal);
                    if (frontConn != null && ReflectCache.ConnAllocationState != null && ReflectCache.ConnAllocationState.GetMethod != null)
                    {
                        var allocVal = ReflectCache.ConnAllocationState.GetValue(frontConn);
                        if (allocVal != null) snap.FrontAllocationState = Convert.ToInt32(allocVal);
                    }
                }
            }
            catch (Exception ex)
            {
                snap.SignalState = $"信号读取异常: {ex.Message}";
            }
        }

        private static void CollectRouteInfo(object train, TrainSnapshot snap)
        {
            try
            {
                var routeRun = ReflectCache.TrainActiveRouteRun?.GetValue(train);
                if (routeRun == null) return;

                var rrType = routeRun.GetType();
                ReflectCache.EnsureRouteTypes(rrType, null);

                var steps = ReflectCache.RRSteps?.GetValue(routeRun) as System.Collections.IList;
                if (steps == null) return;

                snap.RouteTotalSteps = steps.Count;

                if (ReflectCache.RRCurrentStepIndex != null)
                {
                    snap.RouteCurrentStep = Convert.ToInt32(ReflectCache.RRCurrentStepIndex.GetValue(routeRun));
                    snap.RouteRemainingSteps = Math.Max(0, snap.RouteTotalSteps - snap.RouteCurrentStep - 1);
                }

                // 提取前方轨道段标识
                for (int i = snap.RouteCurrentStep; i < steps.Count; i++)
                {
                    var step = steps[i];
                    if (step == null) continue;

                    var stepType = step.GetType();
                    ReflectCache.EnsureRouteTypes(null, stepType);

                    var destConn = ReflectCache.StepDestination?.GetValue(step);
                    if (destConn == null) continue;

                    // Connection.Name
                    var nameProp = destConn.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (nameProp != null && nameProp.GetMethod != null)
                    {
                        var name = nameProp.GetValue(destConn) as string;
                        if (!string.IsNullOrEmpty(name))
                            snap.RouteStepTrackIds.Add(name);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"CollectRouteInfo 异常: {ex.Message}");
            }
        }

        private static void CollectNextStation(object train, TrainSnapshot snap)
        {
            try
            {
                object visit = null;

                // 优先 NextStationVisit 方法
                if (ReflectCache.TrainNextStationVisit != null)
                    visit = ReflectCache.TrainNextStationVisit.Invoke(train, null);

                // 回退到 NextStationVisits
                if (visit == null && ReflectCache.TrainNextStationVisits != null)
                {
                    var visits = ReflectCache.TrainNextStationVisits.GetValue(train);
                    if (visits != null)
                    {
                        foreach (var v in (System.Collections.IEnumerable)visits) { visit = v; break; }
                    }
                }

                if (visit == null)
                {
                    snap.NextStationName = "";
                    return;
                }

                var visitType = visit.GetType();
                ReflectCache.EnsureStationTypes(visitType, null);

                // Station
                if (ReflectCache.VisitStation != null)
                {
                    var station = ReflectCache.VisitStation.GetValue(visit);
                    if (station != null)
                    {
                        ReflectCache.EnsureStationTypes(null, station.GetType());

                        if (ReflectCache.StationFriendlyName != null && ReflectCache.StationFriendlyName.GetMethod != null)
                            snap.NextStationName = ReflectCache.StationFriendlyName.GetValue(station) as string ?? "";

                        if (string.IsNullOrEmpty(snap.NextStationName) && ReflectCache.StationName != null && ReflectCache.StationName.GetMethod != null)
                            snap.NextStationName = ReflectCache.StationName.GetValue(station) as string ?? "";

                        if (string.IsNullOrEmpty(snap.NextStationName))
                            snap.NextStationName = station.ToString() ?? "";
                    }
                }

                // PlatformNumber
                if (ReflectCache.VisitPlatformNumber != null)
                {
                    var val = ReflectCache.VisitPlatformNumber.GetValue(visit);
                    if (val != null)
                    {
                        try { snap.NextPlatformNumber = Convert.ToInt32(val); }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"CollectNextStation 异常: {ex.Message}");
            }
        }

        private static string GetStopReasons(object train)
        {
            try
            {
                if (ReflectCache.TrainStopReasons == null) return "";

                var stopReasons = ReflectCache.TrainStopReasons.GetValue(train);
                if (stopReasons == null) return "";

                var srType = stopReasons.GetType();
                ReflectCache.EnsureStopReasonTypes(srType);

                int count = ReflectCache.SRCount != null ? Convert.ToInt32(ReflectCache.SRCount.GetValue(stopReasons)) : 0;
                if (count == 0) return "";

                // 枚举全部停车原因，用逗号拼接（之前只取 First() 会漏掉并存的其他原因，如 Station 不是第一个时）
                var reasons = new List<string>();
                var enumerable = stopReasons as System.Collections.IEnumerable;
                if (enumerable != null)
                {
                    foreach (var r in enumerable)
                    {
                        if (r != null) reasons.Add(r.ToString());
                    }
                }
                if (reasons.Count > 0) return string.Join(",", reasons);

                if (ReflectCache.SRGetFirst != null)
                {
                    var first = ReflectCache.SRGetFirst.Invoke(stopReasons, null);
                    if (first != null) return first.ToString();
                }
                return $"{count} 个原因";
            }
            catch { return ""; }
        }
    }
}
