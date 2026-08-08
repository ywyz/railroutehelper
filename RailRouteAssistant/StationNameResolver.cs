using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace RailRouteAssistant
{
    /// <summary>
    /// 站点拆分合并：部分创意工坊地图把一个大站拆成多个“小场”（如京广场、城际场、
    /// 徐兰场上行、徐兰场下行），子站点名里不含母站名。本类按当前地图名匹配母站，
    /// 把“XX场”类子站点显示/播报为“母站名+子站点名”（如“郑州东站京广场”）。
    ///
    /// 合并在插件侧统一进行，覆盖所有站名字段（下一站、最近访问、当前停站、地图起讫站、
    /// 计划停车表），因此桌面端显示、语音播报和告警文本一致。
    /// </summary>
    internal static class StationNameResolver
    {
        private static string _mapName;
        private static string _activeParent;
        private static bool _configLoaded;
        // 地图名包含片段 -> 母站名（按当前地图名匹配，匹配到则对全图“XX场”子站点前置母站名）
        private static readonly List<KeyValuePair<string, string>> _mapRules = new();
        // 子站点名 -> 完整名（用户配置的直接映射，优先级最高，覆盖一切）
        private static readonly Dictionary<string, string> _directMappings = new(StringComparer.Ordinal);
        // 内置直接映射兜底（地图名读取失败时使用；仅对已知子站点名生效）
        private static readonly Dictionary<string, string> _builtinDirectMappings = new(StringComparer.Ordinal)
        {
            { "京广场", "郑州东站京广场" },
            { "城际场", "郑州东站城际场" },
            { "徐兰场上行", "郑州东站徐兰场上行" },
            { "徐兰场下行", "郑州东站徐兰场下行" },
        };

        public static string CurrentMapName => _mapName;
        public static string ActiveParent => _activeParent;

        /// <summary>更新当前地图名，并重新计算生效的母站。null/空表示未知地图。</summary>
        public static void UpdateMapName(string mapName)
        {
            if (string.Equals(mapName, _mapName, StringComparison.Ordinal)) return;
            _mapName = mapName;
            _activeParent = null;
            EnsureConfigLoaded();

            if (string.IsNullOrEmpty(mapName))
            {
                Plugin.Log.LogInfo("[StationResolver] 地图名为空，未启用母站合并");
                return;
            }

            foreach (var rule in _mapRules)
            {
                if (mapName.IndexOf(rule.Key, StringComparison.Ordinal) >= 0)
                {
                    _activeParent = rule.Value;
                    Plugin.Log.LogInfo($"[StationResolver] 地图 '{mapName}' 匹配母站 '{_activeParent}'");
                    return;
                }
            }
            Plugin.Log.LogInfo($"[StationResolver] 地图 '{mapName}' 未匹配任何母站规则，子站点保持原名");
        }

        /// <summary>解析站名：用户直接映射优先，其次母站+子站点，最后内置直接映射兜底。</summary>
        public static string Resolve(string stationName)
        {
            if (string.IsNullOrEmpty(stationName)) return stationName;
            EnsureConfigLoaded();
            // 1. 用户配置的直接映射（优先级最高，覆盖一切）
            if (_directMappings.TryGetValue(stationName, out var full)) return full;
            // 2. 地图名匹配到的母站 + 子站点
            if (!string.IsNullOrEmpty(_activeParent) && IsSubYard(stationName))
                return _activeParent + stationName;
            // 3. 内置直接映射兜底（地图名读取失败时使用）
            if (_builtinDirectMappings.TryGetValue(stationName, out var builtin)) return builtin;
            return stationName;
        }

        /// <summary>
        /// 子站点特征：含“场”且不含“站”；或以“上行”/“下行”结尾且不含“站”。
        /// 含“站”的名称（如“南京站高速场”）已被地图作者写成完整名，不再前置母站。
        /// </summary>
        private static bool IsSubYard(string name)
        {
            if (name.IndexOf('站') >= 0) return false;
            if (name.IndexOf('场') >= 0) return true;
            if (name.EndsWith("上行", StringComparison.Ordinal) ||
                name.EndsWith("下行", StringComparison.Ordinal))
                return true;
            return false;
        }

        private static void EnsureConfigLoaded()
        {
            if (_configLoaded) return;
            _configLoaded = true;
            // 内置默认规则：已知会把母站拆成多个“小场”的地图。
            // 地图名只要包含左侧片段即生效；母站名右侧需保留“站”字（如“郑州东站”）。
            AddMapRule("郑州东站", "郑州东站");
            AddMapRule("南京枢纽", "南京南站");
            LoadConfigFile();
        }

        private static void AddMapRule(string fragment, string parent)
        {
            _mapRules.Add(new KeyValuePair<string, string>(fragment, parent));
        }

        /// <summary>
        /// 读取用户配置文件 %LOCALAPPDATA%\RailRouteAssistant\station_groups.txt。
        /// 行格式：
        ///   地图名片段|母站名            （按地图名匹配，对全图“XX场”子站点前置母站名）
        ///   =子站点名|完整名             （直接映射，无视地图名）
        /// 以 # 开头或空行忽略。文件不存在时仅使用内置默认规则。
        /// </summary>
        private static void LoadConfigFile()
        {
            string path;
            try
            {
                // 用 LOCALAPPDATA 环境变量而不是 SpecialFolder.LocalAppData：当前 SDK
                // 引用程序集对 LocalAppData 枚举值的可见性不稳定，环境变量在 Windows 上等价。
                string baseDir = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                if (string.IsNullOrEmpty(baseDir)) return;
                path = Path.Combine(baseDir, "RailRouteAssistant", "station_groups.txt");
            }
            catch { return; }
            if (!File.Exists(path)) return;

            try
            {
                int loaded = 0;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    int bar = line.IndexOf('|');
                    if (bar <= 0) continue;
                    string left = line.Substring(0, bar).Trim();
                    string right = line.Substring(bar + 1).Trim();
                    if (left.Length == 0 || right.Length == 0) continue;

                    if (left.StartsWith("=", StringComparison.Ordinal))
                    {
                        string sub = left.Substring(1).Trim();
                        if (sub.Length > 0)
                        {
                            _directMappings[sub] = right;
                            loaded++;
                        }
                    }
                    else
                    {
                        _mapRules.Add(new KeyValuePair<string, string>(left, right));
                        loaded++;
                    }
                }
                Plugin.Log.LogInfo($"[StationResolver] 从 {path} 加载 {loaded} 条规则");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[StationResolver] 读取配置失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 从游戏反射读取当前地图/关卡名。Rail Route 没有公开稳定的 API，这里按多个候选
    /// 路径尝试：扫描 IControllers / IGameControllers 上名称含 Level/Map/Scenario 等的
    /// 属性，取其对象的 Name/FriendlyName 等字符串属性；再回退到 Unity 活动场景名。
    /// 检测失败时返回 null，此时用户可用 station_groups.txt 的“=子站点|完整名”直接映射兜底。
    /// </summary>
    internal static class MapNameReader
    {
        private static string _cached;
        private static float _lastCheck = -100f;
        private static bool _loggedStructure;
        private const float CheckIntervalSec = 5f;
        // 属性名/类型名关键词：命中其一即认为是关卡/地图相关对象。
        private static readonly string[] Keywords = { "Level", "Map", "Scenario", "World", "Stage", "Chapter", "Mission" };
        // 对象上可能是地图名的字符串属性名。
        private static readonly string[] NameProps = { "Name", "Title", "FriendlyName", "DisplayName", "LevelName", "MapName", "ScenarioName", "FullTitle" };

        public static string TryGetMapName(object controllers)
        {
            try
            {
                float now = Environment.TickCount / 1000f;
                if (now - _lastCheck < CheckIntervalSec)
                    return _cached;
                _lastCheck = now;

                string result = null;
                if (controllers != null)
                {
                    result = TryScanController(controllers, "IControllers");
                    if (string.IsNullOrEmpty(result))
                    {
                        var gc = ReflectCache.IControllersGameControllers?.GetValue(controllers, null);
                        if (gc != null)
                            result = TryScanController(gc, "IGameControllers");
                    }
                }
                if (string.IsNullOrEmpty(result))
                    result = TryGetUnitySceneName();

                if (!string.IsNullOrEmpty(result) && result != _cached)
                {
                    Plugin.Log.LogInfo($"[MapName] 检测到地图名: {result}");
                    _cached = result;
                }
                return _cached;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[MapName] 读取异常: {ex.Message}");
                return _cached;
            }
        }

        private static string TryScanController(object controller, string label)
        {
            try
            {
                var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var type = controller.GetType();

                if (!_loggedStructure)
                {
                    _loggedStructure = true;
                    try
                    {
                        var names = new List<string>();
                        foreach (var p in type.GetProperties(bf))
                            if (p.GetMethod != null) names.Add(p.Name);
                        Plugin.Log.LogInfo($"[MapName] {label} 属性: {string.Join(", ", names)}");
                    }
                    catch { }
                }

                // 先看 controller 自身是否有名字符串属性（如 LevelName/MapName）
                string direct = FindNameString(controller, bf);
                if (LooksLikeMapName(direct)) return direct;

                // 再扫描名称含关键词的属性，取其对象的 Name 类字符串
                foreach (var prop in type.GetProperties(bf))
                {
                    if (prop.GetMethod == null) continue;
                    if (!NameOrTypeMatchesKeyword(prop)) continue;
                    // 先按类型/名称过滤，避免调用有副作用的 getter（如 LevelController.SavingPossible
                    // 内部会调用 Unity FindObjectOfType，在后台线程触发原生层崩溃）。
                    if (!IsSafeToRead(prop)) continue;
                    try
                    {
                        var val = prop.GetValue(controller, null);
                        if (val == null) continue;
                        var name = FindNameString(val, bf);
                        if (LooksLikeMapName(name)) return name;

                        // 深度扫描：LevelController 等对象的 Name 属性可能返回 Unity 场景名，
                        // 真正的地图名可能在其子属性中。递归扫描找含中文的字符串。
                        var deep = ScanDeepStrings(val, bf, depth: 0);
                        if (LooksLikeMapName(deep)) return deep;
                    }
                    catch { }
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 深度扫描对象的字符串属性，找含中文的地图名候选。
        /// 仅读取字符串属性；对引用类型属性最多再递归 1 层。所有属性在调用 getter
        /// 前都先经 <see cref="IsSafeToRead"/> 过滤，避免触发有副作用的 getter
        /// （如 <c>LevelController.SavingPossible</c> 内部调用 Unity
        /// <c>FindObjectOfType</c>，在后台线程会触发原生层崩溃）。
        /// </summary>
        private static string ScanDeepStrings(object obj, BindingFlags bf, int depth)
        {
            if (obj == null || depth > 1) return null;
            try
            {
                var type = obj.GetType();
                foreach (var prop in type.GetProperties(bf))
                {
                    if (prop.GetMethod == null) continue;
                    if (!IsSafeToRead(prop)) continue;
                    try
                    {
                        var val = prop.GetValue(obj, null);
                        if (val == null) continue;

                        if (prop.PropertyType == typeof(string))
                        {
                            var s = val as string;
                            if (string.IsNullOrEmpty(s) || IsUnitySceneName(s)) continue;
                            if (ContainsChinese(s))
                            {
                                Plugin.Log.LogInfo($"[MapName] 深度扫描命中: '{s}'");
                                return s;
                            }
                        }
                        else
                        {
                            var sub = ScanDeepStrings(val, bf, depth + 1);
                            if (!string.IsNullOrEmpty(sub)) return sub;
                        }
                    }
                    catch { }
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 判断属性的 getter 是否可以安全调用。后台线程不能调用任何会触发 Unity
        /// 原生 API 的 getter（如 <c>SavingPossible</c>、<c>IsXxx</c>），否则会
        /// 在 UnityPlayer 原生层崩溃，托管 try-catch 无法捕获。
        /// 规则：只读取字符串属性或可安全递归的引用类型属性；布尔/数值等状态属性
        /// 一律跳过；名称以 Is/Has/Can/Get 开头或含 Possible/Enabled/Ready/Valid/
        /// Saving/Loaded/Count/Length 的属性一律跳过。
        /// </summary>
        private static bool IsSafeToRead(PropertyInfo prop)
        {
            var t = prop.PropertyType;
            if (t == typeof(string)) return IsSafeName(prop.Name);
            // 值类型（含 bool/枚举/结构体）getter 经常有副作用或调用 Unity API，跳过。
            if (t.IsValueType) return false;
            if (!t.IsClass) return false;
            return IsSafeName(prop.Name);
        }

        private static bool IsSafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith("Is", StringComparison.Ordinal) ||
                name.StartsWith("Has", StringComparison.Ordinal) ||
                name.StartsWith("Can", StringComparison.Ordinal) ||
                name.StartsWith("Get", StringComparison.Ordinal)) return false;
            if (name.IndexOf("Possible", StringComparison.Ordinal) >= 0) return false;
            if (name.IndexOf("Enabled", StringComparison.Ordinal) >= 0) return false;
            if (name.IndexOf("Ready", StringComparison.Ordinal) >= 0) return false;
            if (name.IndexOf("Valid", StringComparison.Ordinal) >= 0) return false;
            if (name.IndexOf("Saving", StringComparison.Ordinal) >= 0) return false;
            if (name.IndexOf("Loaded", StringComparison.Ordinal) >= 0) return false;
            if (name.IndexOf("Count", StringComparison.Ordinal) >= 0) return false;
            if (name.IndexOf("Length", StringComparison.Ordinal) >= 0) return false;
            return true;
        }

        private static bool NameOrTypeMatchesKeyword(PropertyInfo prop)
        {
            foreach (var kw in Keywords)
                if (prop.Name.IndexOf(kw, StringComparison.Ordinal) >= 0) return true;
            try
            {
                var t = prop.PropertyType;
                if (t != null)
                    foreach (var kw in Keywords)
                        if (t.Name.IndexOf(kw, StringComparison.Ordinal) >= 0) return true;
            }
            catch { }
            return false;
        }

        private static string FindNameString(object obj, BindingFlags bf)
        {
            var type = obj.GetType();
            foreach (var propName in NameProps)
            {
                try
                {
                    var p = type.GetProperty(propName, bf);
                    if (p?.GetMethod != null)
                    {
                        var v = p.GetValue(obj, null) as string;
                        if (!string.IsNullOrEmpty(v)) return v;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>过滤掉明显不是地图名的字符串（如纯类型名 "TimeController"）。</summary>
        private static bool LooksLikeMapName(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (IsUnitySceneName(s)) return false;
            // 含中文 → 可能是地图名
            foreach (char c in s)
                if (c >= '\u3400' && c <= '\u9fff') return true;
            // 纯英文：要求较长，排除短控制器名
            return s.Length >= 8;
        }

        /// <summary>常见 Unity 场景名，不是游戏地图名。</summary>
        private static bool IsUnitySceneName(string s)
        {
            switch (s)
            {
                case "Bootstrap":
                case "Main":
                case "Demo":
                case "Init":
                case "Loading":
                case "Menu":
                case "Title":
                case "Intro":
                case "Empty":
                case "Persistent":
                    return true;
                default:
                    return false;
            }
        }

        private static bool ContainsChinese(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
                if (c >= '\u3400' && c <= '\u9fff') return true;
            return false;
        }

        private static string TryGetUnitySceneName()
        {
            try
            {
                var t = AccessTools.TypeByName("UnityEngine.SceneManagement.SceneManager");
                if (t == null) return null;
                var getActive = AccessTools.Method(t, "GetActiveScene");
                if (getActive == null) return null;
                var scene = getActive.Invoke(null, null);
                if (scene == null) return null;
                var nameProp = scene.GetType().GetProperty("name");
                return nameProp?.GetValue(scene, null) as string;
            }
            catch { return null; }
        }
    }
}
