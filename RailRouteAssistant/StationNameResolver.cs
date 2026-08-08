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
        // 子站点名 -> 完整名（直接映射，无视地图名；地图名检测失败时使用）
        private static readonly Dictionary<string, string> _directMappings = new(StringComparer.Ordinal);

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

        /// <summary>解析站名：直接映射优先，其次按母站+子站点拼接，否则原样返回。</summary>
        public static string Resolve(string stationName)
        {
            if (string.IsNullOrEmpty(stationName)) return stationName;
            EnsureConfigLoaded();
            if (_directMappings.TryGetValue(stationName, out var full)) return full;
            if (!string.IsNullOrEmpty(_activeParent) && IsSubYard(stationName))
                return _activeParent + stationName;
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
                    try
                    {
                        var val = prop.GetValue(controller, null);
                        if (val == null) continue;
                        var name = FindNameString(val, bf);
                        if (LooksLikeMapName(name)) return name;
                    }
                    catch { }
                }
                return null;
            }
            catch { return null; }
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
            // 纯英文控制器名等不太可能是地图名；含中文或含较长字符即认为可用。
            foreach (char c in s)
                if (c >= '\u3400' && c <= '\u9fff') return true;
            return s.Length >= 4;
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
