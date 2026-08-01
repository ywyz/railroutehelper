using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Windows.Media.SpeechSynthesis;
// 两个命名空间都有 SpeechSynthesizer/VoiceGender，用别名消除歧义。
// OneCore 用类型全名访问；这里别名只服务于 System.Speech 的简写。
using SapiSpeechSynthesizer = System.Speech.Synthesis.SpeechSynthesizer;
using SapiVoiceGender = System.Speech.Synthesis.VoiceGender;

namespace RailRouteAssistantDesktop
{
    /// <summary>
    /// 语音播报引擎：音频片段拼接 + 站名 TTS 兜底。
    /// 车号（字母读音+数字）、站台号用预录音频；站名和缺失句式词用 Windows TTS。
    /// 后台线程顺序播放队列中的播报，避免阻塞 UI。
    /// </summary>
    public class VoiceEngine : IDisposable
    {
        private const string TtsProsodyRate = "+20%";
        private const string ChineseCulturePrefix = "zh";
        private readonly string _audioDir;
        private readonly BlockingCollection<List<Segment>> _queue = new();
        private readonly Thread _playerThread;
        private readonly System.Speech.Synthesis.SpeechSynthesizer _tts;           // System.Speech（SAPI5）后端
        private readonly Windows.Media.SpeechSynthesis.SpeechSynthesizer _oneCore;  // OneCore 后端
        private readonly bool _oneCoreAvailable;
        private bool _hasChineseVoice;
        private VoiceSourceMode _mode = VoiceSourceMode.PreRecorded;
        private bool _disposed;

        /// <summary>语音来源模式：预录音频拼接 / 纯 TTS 合成。</summary>
        public enum VoiceSourceMode { PreRecorded, TtsOnly }

        // 纯 TTS 模式下数字逐位读音；预录模式用 0-9.mp3。
        private static readonly string[] DigitChinese = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九" };

        // 车号字母 → 读音音频文件名（音频库中已有的字母读音 wav）
        private static readonly Dictionary<char, string> LetterReadingFiles = new()
        {
            { 'G', "高.wav" },
            { 'D', "动.wav" },
            { 'K', "快.wav" },
        };
        // 字母 → 读音汉字（音频库无该字母读音时，用 TTS 读这个汉字）
        private static readonly Dictionary<char, string> LetterReadingText = new()
        {
            { 'G', "高" }, { 'D', "动" }, { 'C', "城" }, { 'Z', "直" },
            { 'K', "快" }, { 'T', "特" }, { 'X', "行" }, { 'S', "市域" },
        };

        public VoiceEngine(string audioDir)
        {
            _audioDir = audioDir;
                _tts = new SapiSpeechSynthesizer();
            // 优先尝试 OneCore 后端：它能列出本机所有 OneCore voice（含 Kangkang 男声、
            // Yaoyao 等），System.Speech（SAPI5）只能看到 SAPI5 注册的 voice，通常只有 Huihui。
            // OneCore 在 Win10 1809+ 可用；不可用时回退 System.Speech。
            _oneCore = TryCreateOneCore();
            _oneCoreAvailable = _oneCore != null;
            _hasChineseVoice = _oneCoreAvailable ? HasOneCoreChineseVoice() : TrySelectChineseVoice();
            // 具体播报段通过 SSML 设为 +20%，这里保留默认基准速度。
            _tts.Rate = 0;
            _tts.Volume = 100;

            _playerThread = new Thread(PlayerLoop) { IsBackground = true, Name = "VoicePlayer" };
            _playerThread.Start();
        }

        private Windows.Media.SpeechSynthesis.SpeechSynthesizer TryCreateOneCore()
        {
            try { return new Windows.Media.SpeechSynthesis.SpeechSynthesizer(); }
            catch { /* OneCore 不可用（旧系统或精简版）：回退 System.Speech */ return null; }
        }

        private bool HasOneCoreChineseVoice()
        {
            try
            {
                return Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices
                    .Any(v => v.Language.StartsWith(ChineseCulturePrefix, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        /// <summary>
        /// 动态挑选系统中首个可用的中文语音。不硬编码 voice 名字——
        /// Win10/11 的 Huihui 实际 Name 是 "Microsoft Huihui Desktop"，硬编码
        /// "Microsoft Huihui" 会让 SelectVoice 抛异常并静默回退，在某些机器上
        /// 回退成英文 voice 后会把站名读成乱音。这里按 Culture 匹配，找到才返回 true。
        /// </summary>
        private bool TrySelectChineseVoice()
        {
            try
            {
                // GetInstalledVoices 即便系统完全无语音也会返回空集合而非抛错。
                foreach (var voice in _tts.GetInstalledVoices())
                {
                    if (!voice.Enabled) continue;
                    var info = voice.VoiceInfo;
                    if (info?.Culture != null &&
                        info.Culture.Name.StartsWith(ChineseCulturePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        _tts.SelectVoice(info.Name);
                        return true;
                    }
                }
            }
            catch { /* 语音枚举/选择失败：按无中文 TTS 处理 */ }
            return false;
        }

        /// <summary>一个可选语音来源：预录音源或某个系统 TTS voice。</summary>
        public sealed class VoiceOption
        {
            public string Key;          // "prerecorded" 或 TTS voice Name
            public string DisplayName;  // 菜单显示文字
            public bool IsPreRecorded;  // true=预录音频拼接；false=纯 TTS
            public string VoiceName;    // TTS voice Name；预录模式为 null
        }

        /// <summary>
        /// 枚举系统中所有可用的中文 TTS voice，并附带预录音源选项（当 audioDir 存在时）。
        /// 优先用 OneCore 枚举（能看到 Kangkang 男声等 OneCore voice），OneCore 不可用
        /// 时回退 System.Speech。不持有 VoiceEngine 实例也能调用——供菜单构建使用。
        /// </summary>
        public static List<VoiceOption> GetAvailableVoices(string audioDir)
        {
            var list = new List<VoiceOption>();
            if (!string.IsNullOrEmpty(audioDir) && Directory.Exists(audioDir))
            {
                list.Add(new VoiceOption
                {
                    Key = "prerecorded",
                    DisplayName = "预录广播音源（男声）",
                    IsPreRecorded = true
                });
            }
            // OneCore 优先
            try
            {
                foreach (var v in Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices)
                {
                    if (v?.Language == null) continue;
                    if (!v.Language.StartsWith(ChineseCulturePrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string gender = v.Gender switch
                        {
                            Windows.Media.SpeechSynthesis.VoiceGender.Male => "男声",
                            Windows.Media.SpeechSynthesis.VoiceGender.Female => "女声",
                            _ => null
                        };
                    string display = gender != null ? $"{v.DisplayName}（{gender}）" : v.DisplayName;
                    list.Add(new VoiceOption
                    {
                        Key = "onecore:" + v.Id,
                        DisplayName = "TTS · " + display,
                        IsPreRecorded = false,
                        VoiceName = v.Id
                    });
                }
            }
            catch { /* OneCore 不可用：回退 System.Speech */ }
            // System.Speech 回退
            if (list.Count == 1)
            {
                try
                {
                    using var probe = new SapiSpeechSynthesizer();
                        foreach (var voice in probe.GetInstalledVoices())
                        {
                            if (!voice.Enabled) continue;
                            var info = voice.VoiceInfo;
                            if (info?.Culture == null) continue;
                            if (!info.Culture.Name.StartsWith(ChineseCulturePrefix, StringComparison.OrdinalIgnoreCase))
                                continue;
                            string gender = info.Gender switch
                            {
                                SapiVoiceGender.Male => "男声",
                                SapiVoiceGender.Female => "女声",
                                _ => null
                            };
                        string display = gender != null ? $"{info.Name}（{gender}）" : info.Name;
                        list.Add(new VoiceOption
                        {
                            Key = "sapi5:" + info.Name,
                            DisplayName = "TTS · " + display,
                            IsPreRecorded = false,
                            VoiceName = info.Name
                        });
                    }
                }
                catch { /* 枚举失败：只返回预录选项 */ }
            }
            return list;
        }

        /// <summary>运行时切换语音来源。预录模式回退为音频拼接；TTS 模式选中指定 voice。</summary>
        public void ApplyVoice(VoiceOption option)
        {
            if (option == null) return;
            if (option.IsPreRecorded)
            {
                _mode = VoiceSourceMode.PreRecorded;
                return;
            }
            try
            {
                if (option.Key.StartsWith("onecore:") && _oneCoreAvailable)
                {
                    var match = Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices
                        .FirstOrDefault(v => v.Id == option.VoiceName);
                    if (match != null)
                    {
                        _oneCore.Voice = match;
                        _hasChineseVoice = true;
                        _mode = VoiceSourceMode.TtsOnly;
                        return;
                    }
                }
                // SAPI5 回退
                _tts.SelectVoice(option.VoiceName);
                _hasChineseVoice = true;
                _mode = VoiceSourceMode.TtsOnly;
            }
            catch { /* 选择失败：保留当前模式 */ }
        }

        /// <summary>当前模式，供 UI 显示选中状态。</summary>
        public VoiceSourceMode CurrentMode => _mode;
        /// <summary>当前 TTS voice 名（预录模式为 null）。</summary>
        public string CurrentVoiceName => _mode == VoiceSourceMode.TtsOnly ? _tts?.Voice?.Name : null;

        /// <summary>播报类型</summary>
        public enum AnnouncementType { Arriving, StoppedAtStation, PreDeparture, Departed, Passing, DirectionChange }

        /// <summary>一条播报所需的具名字段，避免将当前站与下一站的位置参数混用。</summary>
        public sealed class Announcement
        {
            public AnnouncementType Type;
            public string TrainCode;
            public string Destination;
            public string Station;
            public int Platform;
            public string NextStation;
            public int NextPlatform;
            public int StopMinutes;
            /// <summary>本次停站后游戏要求列车调向。</summary>
            public bool RequiresDirectionChange;
            /// <summary>正数表示晚点、负数表示早点；零按正点播报；null 表示无法可靠判断。</summary>
            public int? DelayMinutes;
        }

        /// <summary>入队一条播报。</summary>
        public void Enqueue(Announcement announcement)
        {
            if (announcement == null) return;
            var segs = BuildSegments(announcement);
            if (segs.Count > 0) _queue.Add(segs);
        }

        private List<Segment> BuildSegments(Announcement announcement)
        {
            var segs = new List<Segment>();
            string dest = announcement.Destination ?? "";

            // 开场提示音
            AddAudio(segs, "广播开始音1.mp3");

            switch (announcement.Type)
            {
                case AnnouncementType.Arriving:
                    // 等待入图：开往 xx 方向的列车 车号 接近。
                    AddDirectionPrefix(segs, dest);
                    AddTrainNumber(segs, announcement.TrainCode);
                    AddTts(segs, "接近。");
                    break;

                case AnnouncementType.StoppedAtStation:
                    // 开往 xx 方向的列车 车号 早点/正点/晚点 x 分到达 xx x道，本次停车 x 分。
                    AddDirectionPrefix(segs, dest);
                    AddTrainNumber(segs, announcement.TrainCode);
                    if (!announcement.DelayMinutes.HasValue)
                    {
                        AddTts(segs, "到达");
                    }
                    else if (announcement.DelayMinutes.Value < 0)
                    {
                        AddTts(segs, "早点" + FormatChineseCardinal(Math.Abs(announcement.DelayMinutes.Value)) + "分到达");
                    }
                    else if (announcement.DelayMinutes.Value > 0)
                    {
                        AddTts(segs, "晚点" + FormatChineseCardinal(announcement.DelayMinutes.Value) + "分到达");
                    }
                    else
                    {
                        AddTts(segs, "正点到达");
                    }
                    AddStationAndPlatform(segs, announcement.Station, announcement.Platform, "道");
                    if (announcement.StopMinutes > 0)
                    {
                        AddTts(segs, "，本次停车");
                        AddMinuteCount(segs, announcement.StopMinutes);
                        AddTts(segs, "。");
                    }
                    else
                    {
                        AddTts(segs, "，本次停车时间待定。");
                    }
                    if (announcement.RequiresDirectionChange)
                        AddTts(segs, "本次列车需要调向。");
                    break;

                case AnnouncementType.PreDeparture:
                    // 中间站发车前一分钟：xx站 xx道 车号列车 即将发车，请做好准备。
                    AddStationAndPlatform(segs, announcement.Station, announcement.Platform, "道");
                    AddTrainNumber(segs, announcement.TrainCode);
                    AddTts(segs, "列车即将发车，请做好准备。");
                    break;

                case AnnouncementType.Departed:
                    // 开往 xx 方向的列车 车号 正点发车，下一站 xx x道。
                    // 开往 xx 方向的列车 车号 晚点 x 分发车，下一站 xx x道。
                    AddDirectionPrefix(segs, dest);
                    AddTrainNumber(segs, announcement.TrainCode);
                    if (!announcement.DelayMinutes.HasValue)
                    {
                        // 旧插件或 RelativeTimes 没有绝对计划时刻时，不能将累计 Train.Delay
                        // 冒充本站晚点；保守地只确认已发车。
                        AddTts(segs, "已经发车");
                    }
                    else if (announcement.DelayMinutes.Value > 0)
                    {
                        AddTts(segs, "晚点");
                        AddMinuteCount(segs, announcement.DelayMinutes.Value);
                        AddTts(segs, "发车");
                    }
                    else
                    {
                        AddTts(segs, "正点发车");
                    }
                    if (!string.IsNullOrEmpty(announcement.NextStation))
                    {
                        AddTts(segs, "，");
                        AddNextStation(segs, announcement.NextStation, announcement.NextPlatform);
                    }
                    AddTts(segs, "。");
                    break;

                case AnnouncementType.Passing:
                    // 开往 xx 方向的列车 车号 通过 xx x道，下一站 xx x道。
                    AddDirectionPrefix(segs, dest);
                    AddTrainNumber(segs, announcement.TrainCode);
                    AddTts(segs, "通过");
                    AddStationAndPlatform(segs, announcement.Station, announcement.Platform, "道");
                    if (!string.IsNullOrEmpty(announcement.NextStation))
                    {
                        AddTts(segs, "，");
                        AddNextStation(segs, announcement.NextStation, announcement.NextPlatform);
                    }
                    AddTts(segs, "。");
                    break;

                case AnnouncementType.DirectionChange:
                    AddTts(segs, "本次列车需要调向。");
                    break;
            }

            AddAudio(segs, "广播结束音1.mp3");
            return segs;
        }

        private void AddDirectionPrefix(List<Segment> segs, string destination)
        {
            if (!string.IsNullOrEmpty(destination))
            {
                AddTts(segs, "开往");
                AddTts(segs, destination);
                AddTts(segs, "方向的列车");
            }
            else
            {
                AddTts(segs, "列车");
            }
        }

        /// <summary>添加“下一站 xx x道”；没有可用下一站时返回 false。</summary>
        private bool AddNextStation(List<Segment> segs, string station, int platform)
        {
            if (string.IsNullOrEmpty(station)) return false;
            AddTts(segs, "下一站");
            AddStationAndPlatform(segs, station, platform, "道");
            return true;
        }

        private void AddStationAndPlatform(List<Segment> segs, string station, int platform, string platformSuffix)
        {
            if (!string.IsNullOrEmpty(station)) AddTts(segs, station);
            if (platform > 0)
            {
                AddDigit(segs, platform);
                AddTts(segs, platformSuffix);
            }
        }

        /// <summary>添加车号读音：字母读音 + 数字逐位</summary>
        private void AddTrainNumber(List<Segment> segs, string code)
        {
            if (string.IsNullOrEmpty(code)) return;
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (char.IsLetter(c))
                {
                    char up = char.ToUpper(c);
                    if (_mode == VoiceSourceMode.TtsOnly)
                    {
                        // 纯 TTS：字母读对应汉字（高/动/城…），不再用预录 wav。
                        if (LetterReadingText.TryGetValue(up, out var txt))
                            AddTts(segs, txt);
                        else
                            AddTts(segs, up.ToString());
                    }
                    else
                    {
                        // 预录模式：优先用音频库的字母读音 wav
                        if (LetterReadingFiles.TryGetValue(up, out var wav) && AudioExists(wav))
                            AddAudio(segs, wav);
                        else if (LetterReadingText.TryGetValue(up, out var txt))
                            AddTts(segs, txt);
                        else
                            AddTts(segs, up.ToString());  // 未知字母回退
                    }
                }
                else if (char.IsDigit(c))
                {
                    if (_mode == VoiceSourceMode.TtsOnly)
                        AddTts(segs, DigitChinese[c - '0']);
                    else
                        AddAudio(segs, c + ".mp3");
                }
            }
        }

        /// <summary>添加数字读音（多位数逐位读）</summary>
        private void AddNumber(List<Segment> segs, int n)
        {
            if (n <= 0) return;
            foreach (char c in n.ToString())
            {
                if (_mode == VoiceSourceMode.TtsOnly)
                    AddTts(segs, DigitChinese[c - '0']);
                else
                    AddAudio(segs, c + ".mp3");
            }
        }

        /// <summary>时间分钟使用中文基数词，避免把 15 分逐位读成“一五分”。</summary>
        private void AddMinuteCount(List<Segment> segs, int minutes)
        {
            if (minutes <= 0) return;
            AddTts(segs, FormatChineseCardinal(minutes) + "分");
        }

        /// <summary>
        /// 将正整数格式化为普通话基数词。1-9999 显式处理十/百/千位；
        /// 更大的罕见值交给系统中文 TTS 按完整数字解析。
        /// </summary>
        internal static string FormatChineseCardinal(int value)
        {
            if (value <= 0) return "零";
            if (value > 9999) return value.ToString();

            string[] digits = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九" };
            string[] units = { "", "十", "百", "千" };
            var result = new System.Text.StringBuilder();
            bool started = false;
            bool pendingZero = false;

            for (int place = 3; place >= 0; place--)
            {
                int divisor = (int)Math.Pow(10, place);
                int digit = value / divisor % 10;
                if (digit == 0)
                {
                    if (started && value % divisor != 0) pendingZero = true;
                    continue;
                }

                if (pendingZero)
                {
                    result.Append("零");
                    pendingZero = false;
                }

                // 10-19 省略开头的“一”，读“十、十一……十九”。
                if (!(place == 1 && digit == 1 && !started))
                    result.Append(digits[digit]);
                result.Append(units[place]);
                started = true;
            }

            return result.ToString();
        }

        private void AddDigit(List<Segment> segs, int n) => AddNumber(segs, n);

        private bool AudioExists(string file) => File.Exists(Path.Combine(_audioDir, file));

        private void AddAudio(List<Segment> segs, string file)
        {
            if (AudioExists(file)) segs.Add(new Segment { Kind = SegKind.Audio, File = file });
        }

        private void AddTts(List<Segment> segs, string text)
        {
            if (!string.IsNullOrEmpty(text)) segs.Add(new Segment { Kind = SegKind.Tts, Text = text });
        }

        private void PlayerLoop()
        {
            foreach (var segs in _queue.GetConsumingEnumerable())
            {
                try
                {
                    foreach (var s in segs)
                    {
                        if (_disposed) return;
                        if (s.Kind == SegKind.Audio)
                            PlayAudio(s.File);
                        else
                            PlayTts(s.Text);
                    }
                }
                catch { /* 单条播报失败不影响后续 */ }
            }
        }

        private void PlayAudio(string file)
        {
            string path = Path.Combine(_audioDir, file);
            if (!File.Exists(path)) return;
            using var reader = new AudioFileReader(path);
            using var waveOut = new WaveOutEvent();
            waveOut.Init(reader);
            waveOut.Play();
            while (waveOut.PlaybackState == PlaybackState.Playing) Thread.Sleep(50);
        }

        private void PlayTts(string text)
        {
            // 没有中文语音时直接跳过——用英文 voice 读中文会得到无法辨认的乱音，
            // 比静默更糟。预录音频（车号、数字、提示音）不依赖 TTS，仍正常播放。
            if (!_hasChineseVoice) return;

            // 优先用 OneCore 合成：能看到 Kangkang/Yaoyao 等 OneCore voice
            if (_oneCoreAvailable && _mode == VoiceSourceMode.TtsOnly)
            {
                if (TryPlayOneCore(text)) return;
                // OneCore 合成失败：回退 System.Speech
            }

            try
            {
                // 使用 SSML 精确提高合成语音的语速；先转义站名等外部文本，避免破坏 XML。
                string escaped = SecurityElement.Escape(text) ?? string.Empty;
                _tts.SpeakSsml(
                    "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"zh-CN\">" +
                    "<prosody rate=\"" + TtsProsodyRate + "\">" + escaped + "</prosody></speak>");
            }
            catch
            {
                // 少数系统语音可能不支持 SSML；仍以较快的普通语速作为兼容回退。
                try
                {
                    _tts.Rate = 2;
                    _tts.Speak(text);
                    _tts.Rate = 0;
                }
                catch { /* TTS 不可用时静默 */ }
            }
        }

        /// <summary>
        /// 用 OneCore 合成 SSML 并用 NAudio 播放。OneCore 输出 WAV 流，
        /// 转入 MemoryStream 后用 WaveFileReader 解码播放。在后台线程同步阻塞直到播完。
        /// </summary>
        private bool TryPlayOneCore(string text)
        {
            try
            {
                string escaped = SecurityElement.Escape(text) ?? string.Empty;
                string ssml =
                    "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"zh-CN\">" +
                    "<prosody rate=\"" + TtsProsodyRate + "\">" + escaped + "</prosody></speak>";
                var task = _oneCore.SynthesizeSsmlToStreamAsync(ssml).AsTask();
                task.Wait();
                using var winRtStream = task.Result;
                using var dotnetStream = winRtStream.AsStreamForRead();
                using var ms = new MemoryStream();
                dotnetStream.CopyTo(ms);
                ms.Position = 0;
                using var reader = new WaveFileReader(ms);
                using var waveOut = new WaveOutEvent();
                waveOut.Init(reader);
                waveOut.Play();
                while (waveOut.PlaybackState == PlaybackState.Playing) Thread.Sleep(50);
                return true;
            }
            catch
            {
                // OneCore 合成/播放失败（voice 不支持 SSML、音频设备问题等）：回退 System.Speech
                return false;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _queue.CompleteAdding();
            try { _tts?.Dispose(); } catch { }
            try { _oneCore?.Dispose(); } catch { }
        }

        private enum SegKind { Audio, Tts }
        private struct Segment { public SegKind Kind; public string File; public string Text; }
    }
}
