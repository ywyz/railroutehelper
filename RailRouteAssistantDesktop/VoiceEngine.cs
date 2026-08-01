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
        public const int MinimumSpeechRate = 1;
        public const int MaximumSpeechRate = 7;
        public const int DefaultSpeechRate = 7;
        private const string ChineseCulturePrefix = "zh";
        private readonly string _audioDir;
        private readonly BlockingCollection<List<Segment>> _queue = new();
        private readonly Thread _playerThread;
        private readonly System.Speech.Synthesis.SpeechSynthesizer _tts;           // System.Speech（SAPI5）后端
        private readonly Windows.Media.SpeechSynthesis.SpeechSynthesizer _oneCore;  // OneCore 后端
        private readonly bool _oneCoreAvailable;
        private readonly bool _hasOneCoreChineseVoice;
        private readonly bool _hasSapiChineseVoice;
        private readonly object _settingsLock = new();
        private TtsBackend _selectedBackend = TtsBackend.Baidu;
        private string _selectedVoiceName;
        private string _selectedVoiceKey = "baidu:default";
        private int _speechRate = DefaultSpeechRate;
        private bool _onlineTtsEnabled = true;  // 在线 TTS（百度）默认开启，启动时探测可达性
        private bool _disposed;

        /// <summary>用于补全预录素材缺失内容的 TTS 后端。</summary>
        public enum TtsBackend { Baidu, OneCore, Sapi5 }

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
            { 'J', "检" },
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
            // OneCore 和 System.Speech 都尝试选中中文 voice：OneCore 用于合成（能看到 Kangkank 等），
                // System.Speech 作为 OneCore 不可用/失败时的回退。用 | 不短路，确保两者都执行。
                _hasOneCoreChineseVoice = _oneCoreAvailable && TrySelectOneCoreChineseVoice();
                _hasSapiChineseVoice = TrySelectChineseVoice();
            // 探测在线 TTS（百度）可达性：不通则关闭，避免每段播报都超时拖慢。
            _onlineTtsEnabled = ProbeOnlineTts();
            // 具体播报段根据设置动态生成 SSML；这里保留默认基准速度。
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

        private bool TrySelectOneCoreChineseVoice()
        {
            try
            {
                var voice = Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices
                    .FirstOrDefault(v => v.Language.StartsWith(ChineseCulturePrefix, StringComparison.OrdinalIgnoreCase));
                if (voice == null) return false;
                _oneCore.Voice = voice;
                return true;
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

        /// <summary>一个可选的补全 TTS 引擎/音色；预录素材始终优先播放。</summary>
        public sealed class VoiceOption
        {
            public string Key;
            public string DisplayName;  // 菜单显示文字
            public TtsBackend Backend;
            public string VoiceName;
        }

        /// <summary>
        /// 枚举百度和系统中所有可用的中文 TTS voice。所有选项只决定缺词补全引擎；
        /// 车号、数字和提示音等预录素材始终优先。
        /// 优先用 OneCore 枚举（能看到 Kangkang 男声等 OneCore voice），OneCore 不可用
        /// 时回退 System.Speech。不持有 VoiceEngine 实例也能调用——供菜单构建使用。
        /// </summary>
        public static List<VoiceOption> GetAvailableVoices(string audioDir)
        {
            var list = new List<VoiceOption>
            {
                new VoiceOption
                {
                    Key = "baidu:default",
                    DisplayName = "在线 · 百度中文女声",
                    Backend = TtsBackend.Baidu
                }
            };
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
                        DisplayName = "系统 OneCore · " + display,
                        Backend = TtsBackend.OneCore,
                        VoiceName = v.Id
                    });
                }
            }
            catch { /* OneCore 不可用：回退 System.Speech */ }
            // System.Speech（SAPI5）单独列出。旧实现因为在线选项已加入 list，
            // 错把 Count 当作 OneCore 是否可用，导致 SAPI5 voice 永远不会出现在菜单中。
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
                            DisplayName = "系统 SAPI5 · " + display,
                            Backend = TtsBackend.Sapi5,
                            VoiceName = info.Name
                        });
                }
            }
            catch { /* 枚举失败：保留百度和 OneCore 选项 */ }
            return list;
        }

        /// <summary>运行时切换缺词补全 TTS。返回 false 表示指定的本地 voice 已不可用。</summary>
        public bool ApplyVoice(VoiceOption option)
        {
            if (option == null) return false;
            try
            {
                if (option.Backend == TtsBackend.OneCore)
                {
                    if (!_oneCoreAvailable ||
                        !Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices.Any(v => v.Id == option.VoiceName))
                        return false;
                }
                else if (option.Backend == TtsBackend.Sapi5)
                {
                    using var probe = new SapiSpeechSynthesizer();
                    bool exists = probe.GetInstalledVoices().Any(v =>
                        v.Enabled && v.VoiceInfo.Name == option.VoiceName);
                    if (!exists) return false;
                }

                lock (_settingsLock)
                {
                    _selectedBackend = option.Backend;
                    _selectedVoiceName = option.VoiceName;
                    _selectedVoiceKey = option.Key;
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>设置 1（最慢）到 7（最快）的补全语音速度。</summary>
        public void SetSpeechRate(int rate)
        {
            lock (_settingsLock) _speechRate = ClampSpeechRate(rate);
        }

        public int SpeechRate
        {
            get { lock (_settingsLock) return _speechRate; }
        }

        public string SelectedVoiceKey
        {
            get { lock (_settingsLock) return _selectedVoiceKey; }
        }

        internal static int ClampSpeechRate(int rate) =>
            Math.Max(MinimumSpeechRate, Math.Min(MaximumSpeechRate, rate));

        internal static string ToSsmlRate(int rate)
        {
            int percent = (ClampSpeechRate(rate) - 4) * 10;
            return percent >= 0 ? $"+{percent}%" : $"{percent}%";
        }

        /// <summary>只用当前选中的 TTS 播放试听，不经过预录素材。</summary>
        public void EnqueuePreview()
        {
            if (_disposed) return;
            try
            {
                _queue.Add(new List<Segment>
                {
                    new Segment { Kind = SegKind.Tts, Text = "语音切换测试，当前语速设置成功。" }
                });
            }
            catch (InvalidOperationException) { /* 正在关闭 */ }
        }

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
                    // 始终优先使用原有铁路广播音源；只有素材库缺少该字母时才交给 TTS。
                    if (LetterReadingFiles.TryGetValue(up, out var wav) && AudioExists(wav))
                        AddAudio(segs, wav);
                    else if (AudioExists(up + ".mp3"))
                        AddAudio(segs, up + ".mp3");
                    else if (LetterReadingText.TryGetValue(up, out var txt))
                        AddTts(segs, txt);
                    else
                        AddTts(segs, up.ToString());
                }
                else if (char.IsDigit(c))
                {
                    if (AudioExists(c + ".mp3"))
                        AddAudio(segs, c + ".mp3");
                    else
                        AddTts(segs, DigitChinese[c - '0']);
                }
            }
        }

        /// <summary>添加数字读音（多位数逐位读）</summary>
        private void AddNumber(List<Segment> segs, int n)
        {
            if (n <= 0) return;
            foreach (char c in n.ToString())
            {
                if (AudioExists(c + ".mp3"))
                    AddAudio(segs, c + ".mp3");
                else
                    AddTts(segs, DigitChinese[c - '0']);
            }
        }

        /// <summary>时间分钟使用中文基数词，避免把 15 分逐位读成“一五分”。</summary>
        private void AddMinuteCount(List<Segment> segs, int minutes)
        {
            if (minutes <= 0) return;
            string cardinal = FormatChineseCardinal(minutes);
            string recorded = FindRecordedFile(cardinal);
            if (recorded != null)
            {
                AddAudio(segs, recorded);
                AddTts(segs, "分");
            }
            else
            {
                AddTts(segs, cardinal + "分");
            }
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

        private string FindRecordedFile(string phrase)
        {
            if (string.IsNullOrWhiteSpace(phrase)) return null;
            foreach (string extension in new[] { ".wav", ".mp3" })
            {
                string file = phrase + extension;
                if (AudioExists(file)) return file;
            }
            return null;
        }

        private void AddAudio(List<Segment> segs, string file)
        {
            if (AudioExists(file)) segs.Add(new Segment { Kind = SegKind.Audio, File = file });
        }

        private void AddTts(List<Segment> segs, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // 完整词句在素材库中存在时直接使用原声；不存在时才交给选定的 TTS 补全。
            string phrase = text.Trim().Trim('，', '。', '！', '？', ',', '.', '!', '?');
            string recorded = FindRecordedFile(phrase);
            if (recorded != null)
                AddAudio(segs, recorded);
            else
            {
                // 合并相邻 TTS 文本，只发起一次百度/系统合成，消除短词逐段请求造成的
                // 网络等待和句间停顿；预录音频仍自然地充当分隔点。
                if (segs.Count > 0 && segs[segs.Count - 1].Kind == SegKind.Tts)
                {
                    var merged = segs[segs.Count - 1];
                    merged.Text += text;
                    segs[segs.Count - 1] = merged;
                }
                else
                {
                    segs.Add(new Segment { Kind = SegKind.Tts, Text = text });
                }
            }
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
            TtsBackend backend;
            string voiceName;
            int rate;
            lock (_settingsLock)
            {
                backend = _selectedBackend;
                voiceName = _selectedVoiceName;
                rate = _speechRate;
            }

            // 先严格使用用户选择的引擎。旧实现无条件先走百度，导致 OneCore/SAPI
            // 虽然菜单打勾但实际声音完全不变。
            bool played = backend switch
            {
                TtsBackend.Baidu => TryPlayBaidu(text, rate),
                TtsBackend.OneCore => TryPlayOneCore(text, voiceName, rate),
                TtsBackend.Sapi5 => TryPlaySapi(text, voiceName, rate),
                _ => false
            };
            if (played) return;

            // 选定引擎临时失败时才按本地中文 voice → 在线百度的顺序兜底。
            if (backend != TtsBackend.OneCore && TryPlayOneCore(text, null, rate)) return;
            if (backend != TtsBackend.Sapi5 && TryPlaySapi(text, null, rate)) return;
            if (backend != TtsBackend.Baidu && _onlineTtsEnabled) TryPlayBaidu(text, rate);
        }

        /// <summary>启动时探测百度 TTS 是否可达：用短词合成一次，成功则启用在线 TTS。</summary>
        private bool ProbeOnlineTts()
        {
            try
            {
                byte[] data = EdgeTtsClient.Synthesize("测试", DefaultSpeechRate);
                return data != null && data.Length > 100;
            }
            catch { return false; }
        }

        /// <summary>用百度 TTS 合成并用 NAudio 播放 MP3。</summary>
        private bool TryPlayBaidu(string text, int rate)
        {
            try
            {
                byte[] mp3 = EdgeTtsClient.Synthesize(text, rate);
                if (mp3 == null || mp3.Length == 0) return false;
                using var ms = new MemoryStream(mp3);
                using var reader = new Mp3FileReader(ms);
                using var waveOut = new WaveOutEvent();
                waveOut.Init(reader);
                waveOut.Play();
                while (waveOut.PlaybackState == PlaybackState.Playing) Thread.Sleep(50);
                _onlineTtsEnabled = true;
                return true;
            }
            catch
            {
                _onlineTtsEnabled = false;
                return false;
            }
        }

        /// <summary>
        /// 用 OneCore 合成 SSML 并用 NAudio 播放。OneCore 输出 WAV 流，
        /// 转入 MemoryStream 后用 WaveFileReader 解码播放。在后台线程同步阻塞直到播完。
        /// </summary>
        private bool TryPlayOneCore(string text, string voiceName, int rate)
        {
            if (!_oneCoreAvailable || !_hasOneCoreChineseVoice) return false;
            try
            {
                if (!string.IsNullOrEmpty(voiceName))
                {
                    var match = Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices
                        .FirstOrDefault(v => v.Id == voiceName);
                    if (match == null) return false;
                    _oneCore.Voice = match;
                }
                string escaped = SecurityElement.Escape(text) ?? string.Empty;
                string ssml =
                    "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"zh-CN\">" +
                    "<prosody rate=\"" + ToSsmlRate(rate) + "\">" + escaped + "</prosody></speak>";
                var task = _oneCore.SynthesizeSsmlToStreamAsync(ssml).AsTask();
                task.Wait();
                if (task.IsFaulted) return false;
                using var winRtStream = task.Result;
                using var dotnetStream = winRtStream.AsStreamForRead();
                using var ms = new MemoryStream();
                dotnetStream.CopyTo(ms);
                if (ms.Length == 0) return false;
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

        private bool TryPlaySapi(string text, string voiceName, int rate)
        {
            if (!_hasSapiChineseVoice) return false;
            try
            {
                if (!string.IsNullOrEmpty(voiceName)) _tts.SelectVoice(voiceName);
                string escaped = SecurityElement.Escape(text) ?? string.Empty;
                _tts.SpeakSsml(
                    "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"zh-CN\">" +
                    "<prosody rate=\"" + ToSsmlRate(rate) + "\">" + escaped + "</prosody></speak>");
                return true;
            }
            catch
            {
                // 某些 SAPI voice 不支持 prosody SSML，回退其原生 -10..10 速度。
            }
            try
            {
                _tts.Rate = ClampSpeechRate(rate) - 4;
                _tts.Speak(text);
                _tts.Rate = 0;
                return true;
            }
            catch
            {
                try { _tts.Rate = 0; } catch { }
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
