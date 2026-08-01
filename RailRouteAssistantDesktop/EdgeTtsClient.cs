using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace RailRouteAssistantDesktop
{
    /// <summary>
    /// 在线中文 TTS 合成客户端。
    /// 使用百度翻译公开 TTS 接口（fanyi.baidu.com/gettts），免费、无需 API key、国内可达。
    /// 返回 MP3 音频字节，由 VoiceEngine 用 NAudio 播放。
    /// 断网或接口不可用时返回 null，由调用方回退到本地 OneCore/System.Speech。
    /// </summary>
    public static class EdgeTtsClient
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        /// <summary>
        /// 用百度 TTS 合成文本，返回 MP3 音频字节。失败返回 null。
        /// voiceShortName 参数保留以兼容旧调用签名；百度接口用 spd（语速）控制，
        /// 这里映射：男声 spd=3，女声 spd=4，实际音色为百度默认女声。
        /// </summary>
        public static byte[] Synthesize(string text, string voiceShortName, CancellationToken ct = default)
        {
            return Task.Run(() => SynthesizeAsync(text, voiceShortName, ct)).GetAwaiter().GetResult();
        }

        public static async Task<byte[]> SynthesizeAsync(string text, string voiceShortName, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                // 百度翻译 TTS 公开接口：lan=zh 中文，spd 语速（1-7）。
                // spd=6 偏快，配合车站广播节奏；voiceShortName 不影响百度接口（单音色），保留参数兼容。
                string encoded = HttpUtility.UrlEncode(text);
                string url = $"https://fanyi.baidu.com/gettts?lan=zh&text={encoded}&spd=6&source=web";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Referrer = new Uri("https://fanyi.baidu.com/");
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

                using var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) return null;
                byte[] data = await resp.Content.ReadAsByteArrayAsync(ct);
                // 百度返回的是 MP3；空内容或过短内容视为失败
                return (data != null && data.Length > 100) ? data : null;
            }
            catch
            {
                return null; // 网络不通/超时，回退本地
            }
        }

        /// <summary>兼容旧调用：百度 TTS 只有一个默认女声，但仍提供选项让用户感知"在线语音"。</summary>
        public static readonly (string ShortName, string DisplayName, bool Male)[] ChineseVoices =
        {
            ("zh-CN-XiaoxiaoNeural", "晓晓（女声）", false),
            ("zh-CN-YunxiNeural",    "云希（男声）", true),
        };
    }
}
