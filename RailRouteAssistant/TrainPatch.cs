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
        internal static Type NodeType;
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
        internal static PropertyInfo TrainCurrentSpeed, TrainMaxSpeed, TrainTargetSpeed, TrainDelay, TrainUuid, TrainHead, TrainOperationMode;
        internal static PropertyInfo TrainCanDepart, TrainFinished, TrainBrokenDown, TrainOnBoard, TrainDisposed;
        internal static PropertyInfo TrainNotMovingSince, TrainSegmentsInLookahead, TrainActingSignal;
        internal static PropertyInfo TrainContractLeg, TrainNextStationVisits, TrainActualVisits, TrainLastVisited;
        internal static PropertyInfo TrainStopAndReverse, TrainReverseOnceStopped;

        // Train 字段
        internal static FieldInfo TrainReportingNumber, TrainIsWaitingToBeSpawned, TrainActiveRouteRun, TrainStopReasons;
        internal static FieldInfo TrainUuidField, TrainScheduledVisits, TrainCurrentStopIndex;

        // Train 方法
        internal static MethodInfo TrainNeedsRouteAhead, TrainNextStationVisit;

        // ContractLeg 属性（延迟初始化）

        // Semaphore 属性
        internal static PropertyInfo SemAllocationState, SemIsPendingRoute, SemIsOperational, SemFront;
        internal static FieldInfo SemType;
        internal static MethodInfo SemPathToNextSemaphore, SemIsActingFrom;

        // Connection 属性
        internal static PropertyInfo ConnAllocationState, ConnName;

        // Station 属性
        internal static PropertyInfo StationFriendlyName, StationName;

        // StationVisit 属性
        internal static PropertyInfo VisitStation, VisitPlatformNumber, VisitNonStop, VisitStopDurationMinutes, VisitRelativeTimes;
        internal static FieldInfo VisitFrom, VisitTo, VisitDeparted;

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
                NodeType = AccessTools.TypeByName("Game.Railroad.Node");
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
                    TrainActualVisits = TrainType.GetProperty("ActualVisits", bf);
                    TrainLastVisited = TrainType.GetProperty("LastVisited", bf);
                    TrainStopAndReverse = TrainType.GetProperty("StopAndReverse", bf);
                    TrainReverseOnceStopped = TrainType.GetProperty("ReverseOnceStopped", bf);
                    TrainUuid = TrainType.GetProperty("Uuid", bf);
                    TrainHead = TrainType.GetProperty("Head", bf);
                    TrainOperationMode = TrainType.GetProperty("OperationMode", bf);

                    TrainReportingNumber = AccessTools.Field(TrainType, "ReportingNumber");
                    TrainIsWaitingToBeSpawned = AccessTools.Field(TrainType, "IsWaitingToBeSpawned");
                    TrainActiveRouteRun = AccessTools.Field(TrainType, "ActiveRouteRun");
                    TrainStopReasons = AccessTools.Field(TrainType, "StopReasons");
                    TrainUuidField = AccessTools.Field(TrainType, "Uuid");
                    TrainScheduledVisits = AccessTools.Field(TrainType, "ScheduledVisits");
                    TrainCurrentStopIndex = AccessTools.Field(TrainType, "CurrentStopIndex");

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
                        var nodeType = NodeType;
                        if (nodeType != null)
                            SemAllocationState = nodeType.GetProperty("AllocationState", bf);
                    }
                    SemType = AccessTools.Field(SemaphoreType, "Type");
                    SemIsPendingRoute = SemaphoreType.GetProperty("IsPendingRoute", bf);
                    SemIsOperational = SemaphoreType.GetProperty("IsOperational", bf);
                    if (SemIsOperational == null || SemIsOperational.GetMethod == null)
                        SemIsOperational = NodeType?.GetProperty("IsOperational", bf);
                    SemFront = SemaphoreType.GetProperty("Front", bf);
                    SemPathToNextSemaphore = AccessTools.Method(SemaphoreType, "PathToNextSemaphore", new[] { typeof(bool) });
                    SemIsActingFrom = AccessTools.Method(SemaphoreType, "IsActingFrom");
                }

                // Connection（同样可能继承自 Node，确保拿到有 getter 的属性）
                if (ConnectionType != null)
                {
                    var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    ConnAllocationState = ConnectionType.GetProperty("AllocationState", bf);
                    if (ConnAllocationState == null || ConnAllocationState.GetMethod == null)
                    {
                        var nodeType = NodeType;
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
            if (visitType != null)
            {
                if (VisitStation == null) VisitStation = visitType.GetProperty("Station", bf);
                if (VisitPlatformNumber == null) VisitPlatformNumber = visitType.GetProperty("PlatformNumber", bf);
                if (VisitNonStop == null) VisitNonStop = visitType.GetProperty("NonStop", bf);
                if (VisitStopDurationMinutes == null) VisitStopDurationMinutes = visitType.GetProperty("StopDurationMinutes", bf);
                if (VisitRelativeTimes == null) VisitRelativeTimes = visitType.GetProperty("RelativeTimes", bf);
                if (VisitFrom == null) VisitFrom = AccessTools.Field(visitType, "From");
                if (VisitTo == null) VisitTo = AccessTools.Field(visitType, "To");
                if (VisitDeparted == null) VisitDeparted = AccessTools.Field(visitType, "Departed");
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
        // UUID -> 列车刚越过一个信号后，沿实际行车方向得到的下一座运营信号。
        // 保存对象本身而非一次性状态，使进路后来开通时可在下一次快照中立即消警。
        private static readonly Dictionary<string, object> _immediateSignalWatches = new Dictionary<string, object>();
        private static readonly object _immediateSignalWatchesLock = new object();

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
                CleanupImmediateSignalWatches(snapshots);

                // 用游戏内时间换算"剩余秒数"和"停车时长"。
                // 注意：ContractLeg.NextPrepareTime 是下一交路的准备时刻；本站发车倒计时
                // 必须使用当前 StationVisit 的 To。
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
                        // 当前停站距发车时间。RelativeTimes 的 visit 没有可比较的绝对时刻，
                        // 此时保持 null，由桌面端显示为未知而不是给出错误倒计时。
                        if (s.CurrentDepartureGameTime.HasValue)
                        {
                            var rem = s.CurrentDepartureGameTime.Value - now;
                            s.DepartureRemainingSeconds = rem > 0 ? rem : 0;
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
                    TrainId = GetTrainId(train),
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
                    RequiresDirectionChange = ReflectCache.GetProp(train, ReflectCache.TrainStopAndReverse, false) ||
                        ReflectCache.GetProp(train, ReflectCache.TrainReverseOnceStopped, false),
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

                // 最近实际访问与当前计划停站。两者都不能用 NextStationVisit 代替：
                // 到站时游戏会先从 NextStationVisits 移除本站。
                CollectLastVisit(train, snap);
                CollectCurrentStop(train, snap);

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

        /// <summary>
        /// 列车越过信号机后的精确事件。仅记录实际行车方向的下一座运营信号，
        /// 不使用会跳过已开放信号的 ActingSignalAhead。
        /// </summary>
        public static void SemaphoreAfterTrainEntered_Postfix(object __instance, object train)
        {
            try
            {
                if (__instance == null || train == null || !ReflectCache.Init()) return;
                if (ReflectCache.SemaphoreType == null || !ReflectCache.SemaphoreType.IsInstanceOfType(__instance)) return;

                // 调车不产生正线接车预警；与游戏原方法的常规运行逻辑保持一致。
                var mode = ReflectCache.TrainOperationMode?.GetValue(train);
                if (mode != null && Convert.ToInt32(mode) == 1) return;

                var head = ReflectCache.TrainHead?.GetValue(train);
                if (head == null || ReflectCache.SemIsActingFrom == null) return;

                // 从 ActingFrom 一侧进入是反向通过，不应将该信号后的路径当作列车前方。
                if (Convert.ToBoolean(ReflectCache.SemIsActingFrom.Invoke(__instance, new[] { head }))) return;

                var trainId = GetTrainId(train);
                if (string.IsNullOrEmpty(trainId)) return;

                object nextSignal = GetNextOperationalSignalAfter(__instance);
                lock (_immediateSignalWatchesLock)
                {
                    // null 是有效状态：表示沿当前路径没有下一座可运营信号（例如出图边缘）。
                    _immediateSignalWatches[trainId] = nextSignal;
                }
            }
            catch
            {
                // 绝不能让一个预警采集失败干扰游戏原始的过信号逻辑。
            }
        }

        private static string GetTrainId(object train)
        {
            try
            {
                var id = ReflectCache.TrainUuid?.GetValue(train) ?? ReflectCache.TrainUuidField?.GetValue(train);
                return id?.ToString() ?? "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// 取得本列车应监视的紧邻下一座同向信号。正常情况下来自过信号事件；
        /// 插件中途加载时才从游戏已计算的制动前视链中保守兜底，找不到就视为未知。
        /// </summary>
        private static object GetImmediateNextSignal(object train)
        {
            var trainId = GetTrainId(train);
            if (!string.IsNullOrEmpty(trainId))
            {
                lock (_immediateSignalWatchesLock)
                {
                    if (_immediateSignalWatches.TryGetValue(trainId, out var watched))
                        return watched;
                }
            }

            return FindImmediateSignalInLookahead(train);
        }

        private static object GetNextOperationalSignalAfter(object passedSignal)
        {
            try
            {
                var path = ReflectCache.SemPathToNextSemaphore?.Invoke(passedSignal, new object[] { true })
                    as System.Collections.IEnumerable;
                if (path == null) return null;

                object next = null;
                foreach (var node in path)
                {
                    if (node != null && !ReferenceEquals(node, passedSignal) &&
                        ReflectCache.SemaphoreType != null && ReflectCache.SemaphoreType.IsInstanceOfType(node))
                    {
                        next = node;
                    }
                }
                return next;
            }
            catch { return null; }
        }

        private static object FindImmediateSignalInLookahead(object train)
        {
            try
            {
                var lookahead = ReflectCache.TrainSegmentsInLookahead?.GetValue(train);
                if (lookahead == null || ReflectCache.SemaphoreType == null || ReflectCache.ConnectionType == null ||
                    ReflectCache.SemIsActingFrom == null) return null;

                var nodesProp = lookahead.GetType().GetProperty("Nodes",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var nodes = nodesProp?.GetValue(lookahead) as System.Collections.IEnumerable;
                if (nodes == null) return null;

                object previousConnection = ReflectCache.TrainHead?.GetValue(train);
                foreach (var node in nodes)
                {
                    if (node == null) continue;

                    if (ReflectCache.SemaphoreType.IsInstanceOfType(node))
                    {
                        // 兜底路径不应把已停用的信号当作会拦车的下一信号。
                        if (ReflectCache.SemIsOperational == null ||
                            !Convert.ToBoolean(ReflectCache.SemIsOperational.GetValue(node)))
                        {
                            continue;
                        }

                        if (previousConnection != null && Convert.ToBoolean(
                            ReflectCache.SemIsActingFrom.Invoke(node, new[] { previousConnection })))
                        {
                            return node;
                        }
                    }
                    else if (ReflectCache.ConnectionType.IsInstanceOfType(node))
                    {
                        previousConnection = node;
                    }
                }
            }
            catch { }
            return null;
        }

        private static void CleanupImmediateSignalWatches(List<TrainSnapshot> snapshots)
        {
            var activeIds = new HashSet<string>(snapshots
                .Where(s => s.IsOnBoard && !s.IsDisposed && !string.IsNullOrEmpty(s.TrainId))
                .Select(s => s.TrainId));

            lock (_immediateSignalWatchesLock)
            {
                var stale = _immediateSignalWatches.Keys.Where(id => !activeIds.Contains(id)).ToList();
                foreach (var id in stale) _immediateSignalWatches.Remove(id);
            }
        }

        private static void CollectSignalInfo(object train, TrainSnapshot snap)
        {
            try
            {
                // ActingSignalAhead 会跳过已经开通的信号，可能指向多段进路之后的远方红灯。
                // 告警与桌面“信号”列都必须监视紧邻的下一座同向物理信号。
                var signal = GetImmediateNextSignal(train);
                if (signal == null)
                {
                    snap.HasActingSignal = false;
                    snap.SignalState = "下一信号未知";
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

                // Semaphore.Front 实际等于 ActingFrom（信号前的接近轨道），不是信号后的进路。
                // 不再用它推断“前方关闭”，保留字段的 -1 值仅为兼容旧 HTTP 客户端。
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
                var visit = GetNextStationVisit(train);
                if (visit == null)
                {
                    snap.NextStationName = "";
                    return;
                }

                ReadStationVisit(visit, out var station, out var platform, out var nonStop,
                    out _, out _, out _, out _);
                snap.NextStationName = station;
                snap.NextPlatformNumber = platform;
                snap.NextStationNonStop = nonStop;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"CollectNextStation 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 采集最近一次实际访问。通过站也会写入 ActualVisits，因而可与 ActualVisitCount
        /// 一起构成稳定的事件序号，供桌面端判断“到站”和“通过”。
        /// </summary>
        private static void CollectLastVisit(object train, TrainSnapshot snap)
        {
            try
            {
                var actualVisits = ReflectCache.TrainActualVisits?.GetValue(train);
                snap.ActualVisitCount = CountSegments(actualVisits);
                var scheduledVisits = ReflectCache.TrainScheduledVisits?.GetValue(train);
                snap.ScheduledVisitCount = CountSegments(scheduledVisits);

                var visit = ReflectCache.TrainLastVisited?.GetValue(train);
                if (visit == null) return;

                ReadStationVisit(visit, out var station, out var platform, out var nonStop,
                    out var stopMinutes, out var departureTime, out _, out var departed);
                snap.LastVisitStationName = station;
                snap.LastVisitPlatformNumber = platform;
                snap.LastVisitNonStop = nonStop;
                snap.LastVisitStopDurationMinutes = stopMinutes;
                snap.LastVisitDepartureGameTime = departureTime;
                snap.LastVisitDeparted = departed;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"CollectLastVisit 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 读取当前计划停站。游戏在记录一次访问时先将 CurrentStopIndex 加一，因此
        /// ScheduledVisits[CurrentStopIndex - 1] 才是当前（或刚离开的）本站。
        /// </summary>
        private static void CollectCurrentStop(object train, TrainSnapshot snap)
        {
            try
            {
                var scheduledVisits = ReflectCache.TrainScheduledVisits?.GetValue(train) as System.Collections.IList;
                int currentStopIndex = ReflectCache.GetField(train, ReflectCache.TrainCurrentStopIndex, 0);
                int visitIndex = currentStopIndex - 1;

                if (scheduledVisits != null && visitIndex >= 0 && visitIndex < scheduledVisits.Count)
                {
                    snap.ScheduledVisitCount = scheduledVisits.Count;
                    // CurrentStopIndex 已在游戏记录到站/通过时递增，减一后正是本次访问。
                    // 这个索引用于严格排除地图首站和末站的通过/发车预告。
                    snap.CurrentScheduledVisitIndex = visitIndex;
                    var visit = scheduledVisits[visitIndex];
                    if (visit != null)
                    {
                        ReadStationVisit(visit, out var station, out var platform, out var nonStop,
                            out var stopMinutes, out var departureTime, out _, out _);
                        if (!nonStop)
                        {
                            snap.CurrentStationName = station;
                            snap.CurrentPlatformNumber = platform;
                            snap.CurrentStopDurationMinutes = stopMinutes;
                            snap.CurrentDepartureGameTime = departureTime;
                            return;
                        }
                    }
                }

                // 游戏版本字段变动时，至少退回到最近一次实际停站，绝不退回到“下一站”。
                if (!snap.LastVisitNonStop && !string.IsNullOrEmpty(snap.LastVisitStationName))
                {
                    snap.CurrentStationName = snap.LastVisitStationName;
                    snap.CurrentPlatformNumber = snap.LastVisitPlatformNumber;
                    snap.CurrentStopDurationMinutes = snap.LastVisitStopDurationMinutes;
                    snap.CurrentDepartureGameTime = snap.LastVisitDepartureGameTime;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"CollectCurrentStop 异常: {ex.Message}");
            }
        }

        private static object GetNextStationVisit(object train)
        {
            try
            {
                if (ReflectCache.TrainNextStationVisit != null)
                {
                    var visit = ReflectCache.TrainNextStationVisit.Invoke(train, null);
                    if (visit != null) return visit;
                }

                var visits = ReflectCache.TrainNextStationVisits?.GetValue(train) as System.Collections.IEnumerable;
                if (visits != null)
                {
                    foreach (var visit in visits) return visit;
                }
            }
            catch { }
            return null;
        }

        private static void ReadStationVisit(object visit, out string stationName, out int platformNumber,
            out bool nonStop, out int stopDurationMinutes, out double? departureGameTime,
            out bool relativeTimes, out bool departed)
        {
            stationName = "";
            platformNumber = 0;
            nonStop = false;
            stopDurationMinutes = 0;
            departureGameTime = null;
            relativeTimes = false;
            departed = false;
            if (visit == null) return;

            try
            {
                ReflectCache.EnsureStationTypes(visit.GetType(), null);
                nonStop = ReflectCache.GetProp(visit, ReflectCache.VisitNonStop, false);
                stopDurationMinutes = ReflectCache.GetProp(visit, ReflectCache.VisitStopDurationMinutes, 0);
                relativeTimes = ReflectCache.GetProp(visit, ReflectCache.VisitRelativeTimes, false);
                departed = ReflectCache.GetField(visit, ReflectCache.VisitDeparted, false);

                if (ReflectCache.VisitPlatformNumber != null)
                {
                    var val = ReflectCache.VisitPlatformNumber.GetValue(visit);
                    if (val != null) platformNumber = Convert.ToInt32(val);
                }

                if (!relativeTimes)
                    departureGameTime = GetTimeSpanTotalSeconds(ReflectCache.VisitTo?.GetValue(visit));

                var station = ReflectCache.VisitStation?.GetValue(visit);
                if (station == null) return;

                ReflectCache.EnsureStationTypes(null, station.GetType());
                if (ReflectCache.StationFriendlyName != null && ReflectCache.StationFriendlyName.GetMethod != null)
                    stationName = ReflectCache.StationFriendlyName.GetValue(station) as string ?? "";
                if (string.IsNullOrEmpty(stationName) && ReflectCache.StationName != null && ReflectCache.StationName.GetMethod != null)
                    stationName = ReflectCache.StationName.GetValue(station) as string ?? "";
                if (string.IsNullOrEmpty(stationName))
                    stationName = station.ToString() ?? "";
            }
            catch { }
        }

        private static double? GetTimeSpanTotalSeconds(object value)
        {
            return value is TimeSpan time ? time.TotalSeconds : (double?)null;
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
