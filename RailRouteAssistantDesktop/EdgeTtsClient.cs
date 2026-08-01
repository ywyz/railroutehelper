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
        /// 百度接口用 spd 控制语速，范围 1（最慢）到 7（最快）。
        /// </summary>
        public static byte[] Synthesize(string text, int speed, CancellationToken ct = default)
        {
            return Task.Run(() => SynthesizeAsync(text, speed, ct)).GetAwaiter().GetResult();
        }

        public static async Task<byte[]> SynthesizeAsync(string text, int speed, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                // 百度翻译 TTS 公开接口：lan=zh 中文，spd 语速。
                speed = Math.Max(VoiceEngine.MinimumSpeechRate, Math.Min(VoiceEngine.MaximumSpeechRate, speed));
                string encoded = HttpUtility.UrlEncode(text);
                string url = $"https://fanyi.baidu.com/gettts?lan=zh&text={encoded}&spd={speed}&source=web";

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
    }
}
