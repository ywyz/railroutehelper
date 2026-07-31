using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RailRouteAssistantDesktop
{
    public enum TrainInfoSource
    {
        Offline,
        Legacy12306,
        Online
    }

    /// <summary>
    /// 单个车次信息（始发站、终到站）。在线结果会覆盖同车号的离线结果。
    /// </summary>
    public sealed class TrainInfo
    {
        public string Code { get; }
        public string Origin { get; }
        public string Destination { get; }
        public TrainInfoSource Source { get; }

        public TrainInfo(string code, string origin, string destination, TrainInfoSource source)
        {
            Code = code;
            Origin = origin;
            Destination = destination;
            Source = source;
        }
    }

    /// <summary>
    /// 车次始发终到查询服务。
    ///
    /// 查询优先级为：12306 当天在线精确结果、路路通离线车次表、12306 冻结静态表。
    /// 首次遇到车次时会异步请求在线接口；同步查询绝不等待网络，并会立即按后两级降级。
    /// 在线缓存与用户自行导出的路路通表位于 %LOCALAPPDATA%\RailRouteAssistant，
    /// 两份随程序发布的离线表均为只读，不会向安装目录写入数据。
    /// </summary>
    public sealed class TrainInfoService : IDisposable
    {
        private const string OnlineSearchUrl = "https://search.12306.cn/search/v1/train/search";
        private const int MaxConcurrentRequests = 3;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(10);

        private readonly HttpClient _http;
        private readonly string[] _lulutongOfflineDataPaths;
        private readonly string _legacy12306SnapshotPath;
        private readonly string _onlineCachePath;
        private readonly ConcurrentDictionary<string, TrainInfo> _lulutongOffline =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, TrainInfo> _legacy12306 =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, CachedTrainInfo> _online =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Lazy<Task>> _inFlight =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTimeOffset> _retryAfterUtc =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _requestGate = new(MaxConcurrentRequests, MaxConcurrentRequests);
        private readonly SemaphoreSlim _cacheFileGate = new(1, 1);
        private readonly CancellationTokenSource _shutdown = new();

        private int _loaded;
        private int _disposed;

        public TrainInfoService(HttpClient http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            string applicationDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RailRouteAssistant");
            _lulutongOfflineDataPaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "data", "train_routes_offline.json"),
                Path.Combine(applicationDataDirectory, "train_routes_offline.json")
            };
            _legacy12306SnapshotPath = Path.Combine(
                AppContext.BaseDirectory, "data", "train_list_12306_legacy.js");
            _onlineCachePath = Path.Combine(applicationDataDirectory, "train_routes_online_cache.json");
        }

        public bool IsLoaded => Volatile.Read(ref _loaded) == 1;
        /// <summary>路路通表与 12306 静态表去重后的离线车次数。</summary>
        public int OfflineCount => _lulutongOffline.Count +
            _legacy12306.Keys.Count(code => !_lulutongOffline.ContainsKey(code));
        public int LulutongOfflineCount => _lulutongOffline.Count;
        public int Legacy12306Count => _legacy12306.Count;
        public int OnlineCount => _online.Count;
        public int Count => _online.Count +
            _lulutongOffline.Keys.Count(code => !_online.ContainsKey(code)) +
            _legacy12306.Keys.Count(code =>
                !_online.ContainsKey(code) && !_lulutongOffline.ContainsKey(code));

        /// <summary>
        /// 在后台加载两级离线表和上次成功的在线缓存；静态表随程序发布，绝不在启动时下载。
        /// </summary>
        public Task LoadAsync()
        {
            return Task.Run(() =>
            {
                LoadLulutongOfflineRoutes();
                LoadLegacy12306Snapshot();
                LoadOnlineCache();
                Volatile.Write(ref _loaded, 1);
            });
        }

        /// <summary>
        /// 纯内存查询，绝不阻塞桌面 UI。优先级：在线、路路通、12306 静态快照。
        /// </summary>
        public bool TryLookup(string code, out TrainInfo info)
        {
            code = NormalizeCode(code);
            if (string.IsNullOrEmpty(code))
            {
                info = null;
                return false;
            }

            if (HasCurrentOnlineResult(code, out var online))
            {
                info = online.ToTrainInfo();
                return true;
            }

            if (_lulutongOffline.TryGetValue(code, out info))
                return true;

            return _legacy12306.TryGetValue(code, out info);
        }

        /// <summary>
        /// 异步确保按车号查询过 12306。请求被去重并限流；调用方无需等待它完成。
        /// </summary>
        public Task EnsureResolvedAsync(string code)
        {
            code = NormalizeCode(code);
            if (!IsLoaded || string.IsNullOrEmpty(code) || Volatile.Read(ref _disposed) == 1)
                return Task.CompletedTask;

            if (HasCurrentOnlineResult(code, out _))
                return Task.CompletedTask;

            if (_retryAfterUtc.TryGetValue(code, out var retryAfter) && retryAfter > DateTimeOffset.UtcNow)
                return Task.CompletedTask;

            var work = _inFlight.GetOrAdd(
                code,
                key => new Lazy<Task>(
                    () => ResolveOnlineAsync(key),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            return work.Value;
        }

        private async Task ResolveOnlineAsync(string code)
        {
            try
            {
                await _requestGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                try
                {
                    if (HasCurrentOnlineResult(code, out _)) return;

                    string serviceDate = GetServiceDate();
                    var result = await QueryOnlineAsync(code, serviceDate, _shutdown.Token).ConfigureAwait(false);
                    if (result == null)
                    {
                        _retryAfterUtc[code] = DateTimeOffset.UtcNow.Add(RetryDelay);
                        return;
                    }

                    _online[code] = new CachedTrainInfo
                    {
                        Code = result.Code,
                        Origin = result.Origin,
                        Destination = result.Destination,
                        ServiceDate = serviceDate,
                        ResolvedAtUtc = DateTimeOffset.UtcNow
                    };
                    _retryAfterUtc.TryRemove(code, out _);
                    await SaveOnlineCacheAsync(_shutdown.Token).ConfigureAwait(false);
                }
                finally
                {
                    _requestGate.Release();
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                // 程序退出时取消后台请求即可。
            }
            catch
            {
                // 短期负缓存，避免游戏每秒刷新时反复请求 12306。
                _retryAfterUtc[code] = DateTimeOffset.UtcNow.Add(RetryDelay);
            }
            finally
            {
                _inFlight.TryRemove(code, out _);
            }
        }

        private async Task<TrainInfo> QueryOnlineAsync(
            string code,
            string serviceDate,
            CancellationToken cancellationToken)
        {
            string url = $"{OnlineSearchUrl}?keyword={Uri.EscapeDataString(code)}&date={serviceDate}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("RailRouteHelper/1.0");
            request.Headers.Accept.ParseAdd("application/json");
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await _http.SendAsync(request, requestTimeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var text = await response.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) return null;

            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var item in data.EnumerateArray())
            {
                if (!TryReadString(item, "station_train_code", out var returnedCode) ||
                    !string.Equals(NormalizeCode(returnedCode), code, StringComparison.OrdinalIgnoreCase) ||
                    !TryReadString(item, "from_station", out var origin) ||
                    !TryReadString(item, "to_station", out var destination) ||
                    string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(destination))
                    continue;

                return new TrainInfo(code, origin.Trim(), destination.Trim(), TrainInfoSource.Online);
            }

            return null;
        }

        private void LoadLulutongOfflineRoutes()
        {
            foreach (var offlineDataPath in _lulutongOfflineDataPaths)
            {
                try
                {
                    if (!File.Exists(offlineDataPath)) continue;

                    var json = File.ReadAllText(offlineDataPath, Encoding.UTF8);
                    var document = JsonSerializer.Deserialize<OfflineRoutesFile>(json, JsonOptions);
                    if (document?.Routes == null) continue;

                    foreach (var pair in document.Routes)
                    {
                        string code = NormalizeCode(pair.Key);
                        var route = pair.Value;
                        if (string.IsNullOrEmpty(code) || route == null ||
                            string.IsNullOrWhiteSpace(route.Origin) || string.IsNullOrWhiteSpace(route.Destination))
                            continue;

                        // 本机导出表排在最后加载，因而可覆盖发布包内随版本附带的旧表。
                        _lulutongOffline[code] = new TrainInfo(
                            code,
                            route.Origin.Trim(),
                            route.Destination.Trim(),
                            TrainInfoSource.Offline);
                    }
                }
                catch
                {
                    // 单个离线表损坏时继续尝试另一来源，后续仍可通过在线查询获取车次。
                }
            }
        }

        /// <summary>
        /// 读取随程序发布的 12306 train_list.js 冻结快照。该表已停止更新，
        /// 仅在在线接口和路路通表均没有精确车次时才使用。
        /// </summary>
        private void LoadLegacy12306Snapshot()
        {
            try
            {
                if (!File.Exists(_legacy12306SnapshotPath)) return;

                string script = File.ReadAllText(_legacy12306SnapshotPath, Encoding.UTF8);
                int declaration = script.IndexOf("var train_list", StringComparison.Ordinal);
                int jsonStart = declaration >= 0 ? script.IndexOf('{', declaration) : -1;
                int jsonEnd = script.LastIndexOf('}');
                if (jsonStart < 0 || jsonEnd < jsonStart) return;

                string json = script.Substring(jsonStart, jsonEnd - jsonStart + 1);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return;

                // 同一车次在快照的多个日期中可能重复；按较新的日期优先，
                // 避免随机覆盖为更旧一天的始终到站。
                foreach (var dateBucket in document.RootElement.EnumerateObject()
                    .OrderByDescending(property => ParseLegacyDate(property.Name)))
                {
                    if (dateBucket.Value.ValueKind != JsonValueKind.Object) continue;

                    foreach (var category in dateBucket.Value.EnumerateObject())
                    {
                        if (category.Value.ValueKind != JsonValueKind.Array) continue;

                        foreach (var item in category.Value.EnumerateArray())
                        {
                            if (!TryReadString(item, "station_train_code", out var stationTrainCode) ||
                                !TryParseLegacyStationTrainCode(stationTrainCode, out var codes,
                                    out var origin, out var destination))
                                continue;

                            foreach (var code in codes)
                            {
                                // 保留快照中较新日期的第一条；在线和路路通仍会在查询时优先。
                                _legacy12306.TryAdd(code, new TrainInfo(
                                    code, origin, destination, TrainInfoSource.Legacy12306));
                            }
                        }
                    }
                }
            }
            catch
            {
                // 静态快照损坏或格式变化时，在线和路路通降级仍然可用。
            }
        }

        private static DateTime ParseLegacyDate(string value)
        {
            return DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var result)
                ? result
                : DateTime.MinValue;
        }

        /// <summary>
        /// 解析 12306 静态表中的 "Z51(北京-南通)"；带斜杠的联合车次会分别建立别名。
        /// </summary>
        private static bool TryParseLegacyStationTrainCode(
            string stationTrainCode,
            out string[] codes,
            out string origin,
            out string destination)
        {
            codes = null;
            origin = null;
            destination = null;
            if (string.IsNullOrWhiteSpace(stationTrainCode)) return false;

            int open = stationTrainCode.IndexOf('(');
            int close = stationTrainCode.LastIndexOf(')');
            if (open <= 0 || close <= open + 1) return false;

            string route = stationTrainCode.Substring(open + 1, close - open - 1);
            int separator = route.LastIndexOf('-');
            if (separator <= 0 || separator >= route.Length - 1) return false;

            origin = route.Substring(0, separator).Trim();
            destination = route.Substring(separator + 1).Trim();
            if (string.IsNullOrEmpty(origin) || string.IsNullOrEmpty(destination)) return false;

            codes = stationTrainCode.Substring(0, open)
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeCode)
                .Where(code => !string.IsNullOrEmpty(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return codes.Length > 0;
        }

        private void LoadOnlineCache()
        {
            try
            {
                if (!File.Exists(_onlineCachePath)) return;

                var json = File.ReadAllText(_onlineCachePath, Encoding.UTF8);
                var document = JsonSerializer.Deserialize<OnlineRoutesFile>(json, JsonOptions);
                if (document?.Routes == null) return;

                foreach (var pair in document.Routes)
                {
                    string code = NormalizeCode(pair.Key);
                    var route = pair.Value;
                    if (string.IsNullOrEmpty(code) || route == null ||
                        !string.Equals(route.ServiceDate, GetServiceDate(), StringComparison.Ordinal) ||
                        string.IsNullOrWhiteSpace(route.Origin) || string.IsNullOrWhiteSpace(route.Destination))
                        continue;

                    route.Code = code;
                    _online[code] = route;
                }
            }
            catch
            {
                // 用户缓存不可读不影响随程序发布的离线降级表。
            }
        }

        private async Task SaveOnlineCacheAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _cacheFileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var routes = _online.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value,
                        StringComparer.OrdinalIgnoreCase);
                    var document = new OnlineRoutesFile
                    {
                        SchemaVersion = 1,
                        SavedAtUtc = DateTimeOffset.UtcNow,
                        Routes = routes
                    };

                    string directory = Path.GetDirectoryName(_onlineCachePath);
                    if (string.IsNullOrEmpty(directory)) return;
                    Directory.CreateDirectory(directory);

                    string temporaryPath = _onlineCachePath + ".new";
                    string json = JsonSerializer.Serialize(document, JsonOptions);
                    await File.WriteAllTextAsync(temporaryPath, json, Encoding.UTF8, cancellationToken)
                        .ConfigureAwait(false);
                    File.Move(temporaryPath, _onlineCachePath, true);
                }
                finally
                {
                    _cacheFileGate.Release();
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                // 退出期间无需持久化尚未完成的结果。
            }
            catch
            {
                // 缓存写入失败不应影响本轮已经得到的在线结果。
            }
        }

        private static bool TryReadString(JsonElement item, string propertyName, out string value)
        {
            value = null;
            if (!item.TryGetProperty(propertyName, out var property) ||
                property.ValueKind != JsonValueKind.String)
                return false;

            value = property.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        private static string NormalizeCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;

            var normalized = new string(code
                .Where(character => !char.IsWhiteSpace(character) && character != '次')
                .ToArray())
                .Trim()
                .ToUpperInvariant();
            return string.IsNullOrEmpty(normalized) ? null : normalized;
        }

        private bool HasCurrentOnlineResult(string code, out CachedTrainInfo online)
        {
            return _online.TryGetValue(code, out online) &&
                string.Equals(online.ServiceDate, GetServiceDate(), StringComparison.Ordinal);
        }

        private static string GetServiceDate()
        {
            return DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _shutdown.Cancel();
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        private sealed class OfflineRoutesFile
        {
            public int SchemaVersion { get; set; }
            public string Source { get; set; }
            public DateTimeOffset GeneratedAtUtc { get; set; }
            public Dictionary<string, OfflineTrainRoute> Routes { get; set; }
        }

        private sealed class OfflineTrainRoute
        {
            public string Origin { get; set; }
            public string Destination { get; set; }
        }

        private sealed class OnlineRoutesFile
        {
            public int SchemaVersion { get; set; }
            public DateTimeOffset SavedAtUtc { get; set; }
            public Dictionary<string, CachedTrainInfo> Routes { get; set; }
        }

        private sealed class CachedTrainInfo
        {
            public string Code { get; set; }
            public string Origin { get; set; }
            public string Destination { get; set; }
            public string ServiceDate { get; set; }
            public DateTimeOffset ResolvedAtUtc { get; set; }

            public TrainInfo ToTrainInfo()
            {
                return new TrainInfo(Code, Origin, Destination, TrainInfoSource.Online);
            }
        }
    }
}
