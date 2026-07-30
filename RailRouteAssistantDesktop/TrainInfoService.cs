using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;

namespace RailRouteAssistantDesktop
{
    /// <summary>
    /// 单个车次信息（始发站、终到站）
    /// </summary>
    public class TrainInfo
    {
        public string Code;          // 车次号，如 "G1"
        public string Origin;        // 始发站
        public string Destination;   // 终到站
    }

    /// <summary>
    /// 车次信息查询服务。
    /// 数据源：12306 官方车次列表 train_list.js（启动时联网更新，本地缓存兜底）。
    /// 文件格式：var train_list ={"日期":{"G":[{"station_train_code":"G1(北京南-上海虹桥)","train_no":"..."}],...}}
    /// </summary>
    public class TrainInfoService
    {
        private const string TrainListUrl = "https://kyfw.12306.cn/otn/resources/js/query/train_list.js?scriptVersion=1.0";
        private static readonly string CachePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "train_list_cache.json");

        private readonly Dictionary<string, TrainInfo> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly HttpClient _http;
        private bool _loaded = false;

        public TrainInfoService(HttpClient http) { _http = http; }

        public int Count => _cache.Count;
        public bool IsLoaded => _loaded;

        /// <summary>启动时加载：先尝试联网更新，失败则用本地缓存</summary>
        public async Task LoadAsync()
        {
            bool onlineOk = await TryFetchOnlineAsync();
            if (!onlineOk)
                LoadFromCacheFile();
            _loaded = true;
        }

        /// <summary>查询车次信息。返回 false 表示未找到。</summary>
        public bool TryLookup(string code, out TrainInfo info)
        {
            if (string.IsNullOrEmpty(code)) { info = null; return false; }
            return _cache.TryGetValue(code, out info);
        }

        private async Task<bool> TryFetchOnlineAsync()
        {
            try
            {
                // 12306 是国内站点，直连优先；失败回退系统代理
                var resp = await _http.GetAsync(TrainListUrl);
                if (!resp.IsSuccessStatusCode) return false;
                var text = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(text) || !text.Contains("train_list")) return false;

                ParseTrainListJs(text);
                SaveCacheFile(text);
                return _cache.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 解析 train_list.js 内容并填充缓存。
        /// 格式：var train_list ={日期:{"G":[{station_train_code,train_no}],...}}
        /// station_train_code 形如 "G1(北京南-上海虹桥)"，括号内 始发-终到。
        /// 多个日期时取字典序最大的（最新）日期的数据。
        /// </summary>
        private void ParseTrainListJs(string text)
        {
            int eq = text.IndexOf('=');
            if (eq < 0) return;
            string json = text.Substring(eq + 1).Trim();
            if (json.EndsWith(";")) json = json.TrimEnd(';');

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 找最新日期
            string latestDate = null;
            foreach (var prop in root.EnumerateObject())
            {
                if (latestDate == null || string.Compare(prop.Name, latestDate, StringComparison.Ordinal) > 0)
                    latestDate = prop.Name;
            }
            if (latestDate == null) return;

            var dayObj = root.GetProperty(latestDate);
            // station_train_code 解析正则：车号(始发站-终到站)
            var regex = new Regex(@"^([A-Za-z0-9]+)\((.+)-(.+)\)$");

            foreach (var typeProp in dayObj.EnumerateObject())
            {
                if (typeProp.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in typeProp.Value.EnumerateArray())
                {
                    if (!entry.TryGetProperty("station_train_code", out var codeEl)) continue;
                    var raw = codeEl.GetString();
                    if (string.IsNullOrEmpty(raw)) continue;
                    var m = regex.Match(raw);
                    if (!m.Success) continue;
                    var code = m.Groups[1].Value;
                    var origin = m.Groups[2].Value;
                    var dest = m.Groups[3].Value;
                    // 同一车号可能重复，后写覆盖（日期内一般唯一）
                    _cache[code] = new TrainInfo { Code = code, Origin = origin, Destination = dest };
                }
            }
        }

        private void SaveCacheFile(string text)
        {
            try { File.WriteAllText(CachePath, text, Encoding.UTF8); } catch { }
        }

        private void LoadFromCacheFile()
        {
            try
            {
                if (!File.Exists(CachePath)) return;
                var text = File.ReadAllText(CachePath, Encoding.UTF8);
                ParseTrainListJs(text);
            }
            catch { }
        }
    }
}
