using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Speech.Synthesis;
using System.Threading;
using NAudio.Wave;

namespace RailRouteAssistantDesktop
{
    /// <summary>
    /// 语音播报引擎：音频片段拼接 + 站名 TTS 兜底。
    /// 车号（字母读音+数字）、站台号用预录音频；站名和缺失句式词用 Windows TTS。
    /// 后台线程顺序播放队列中的播报，避免阻塞 UI。
    /// </summary>
    public class VoiceEngine : IDisposable
    {
        private readonly string _audioDir;
        private readonly BlockingCollection<List<Segment>> _queue = new();
        private readonly Thread _playerThread;
        private readonly SpeechSynthesizer _tts;
        private bool _disposed;

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
            { 'K', "快" }, { 'T', "特" }, { 'X', "行" },
        };

        public VoiceEngine(string audioDir)
        {
            _audioDir = audioDir;
            _tts = new SpeechSynthesizer();
            try
            {
                _tts.SelectVoice("Microsoft Huihui");  // 中文女声（Win10/11 自带）
            }
            catch { /* 回退默认语音 */ }
            _tts.Rate = 0;
            _tts.Volume = 100;

            _playerThread = new Thread(PlayerLoop) { IsBackground = true, Name = "VoicePlayer" };
            _playerThread.Start();
        }

        /// <summary>播报类型</summary>
        public enum AnnouncementType { Arriving, StoppedAtStation, Departed }

        /// <summary>
        /// 入队一条播报。
        /// trainCode: 车号如 "G4545"（已拆分后的单段车号）
        /// destination: 终到站名（用于"开往xxxx方向"），可为空
        /// currentStation: 当前站名（停站播报用），可为空
        /// platform: 站台号（停站播报用），0 表示无
        /// delayMinutes: 晚点分钟（发车播报用），<=0 视为正点
        /// </summary>
        public void Enqueue(AnnouncementType type, string trainCode, string destination,
                            string currentStation, int platform, int delayMinutes)
        {
            var segs = BuildSegments(type, trainCode, destination, currentStation, platform, delayMinutes);
            if (segs.Count > 0) _queue.Add(segs);
        }

        private List<Segment> BuildSegments(AnnouncementType type, string trainCode,
            string destination, string currentStation, int platform, int delayMinutes)
        {
            var segs = new List<Segment>();
            string dest = destination ?? "";
            string cur = currentStation ?? "";

            // 开场提示音
            AddAudio(segs, "广播开始音1.mp3");

            switch (type)
            {
                case AnnouncementType.Arriving:
                    // 列车进入地图（接近）：开往xxxx方向的列车 车号 接近，请做好接车准备
                    if (!string.IsNullOrEmpty(dest))
                    {
                        AddTts(segs, "开往");
                        AddTts(segs, dest);
                        AddTts(segs, "方向的列车");
                    }
                    AddTrainNumber(segs, trainCode);
                    AddTts(segs, "接近，请做好接车准备");
                    break;

                case AnnouncementType.StoppedAtStation:
                    // 列车停站：列车停靠在 站名 x站台，开往xxx方向
                    AddAudio(segs, "列车停靠在.wav");
                    if (!string.IsNullOrEmpty(cur)) AddTts(segs, cur);
                    if (platform > 0) { AddDigit(segs, platform); AddAudio(segs, "站台.wav"); }
                    if (!string.IsNullOrEmpty(dest))
                    {
                        AddTts(segs, "开往");
                        AddTts(segs, dest);
                        AddTts(segs, "方向");
                    }
                    break;

                case AnnouncementType.Departed:
                    // 列车发车：开往xxxx方向的列车 车号 正点/晚点x分发车
                    if (!string.IsNullOrEmpty(dest))
                    {
                        AddTts(segs, "开往");
                        AddTts(segs, dest);
                        AddTts(segs, "方向的列车");
                    }
                    AddTrainNumber(segs, trainCode);
                    if (delayMinutes <= 0)
                        AddTts(segs, "正点发车");
                    else
                    {
                        AddTts(segs, "晚点");
                        AddNumber(segs, delayMinutes);
                        AddTts(segs, "分发车");
                    }
                    break;
            }

            AddAudio(segs, "广播结束音1.mp3");
            return segs;
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
                    // 优先用音频库的字母读音 wav
                    if (LetterReadingFiles.TryGetValue(up, out var wav) && AudioExists(wav))
                        AddAudio(segs, wav);
                    else if (LetterReadingText.TryGetValue(up, out var txt))
                        AddTts(segs, txt);
                    else
                        AddTts(segs, up.ToString());  // 未知字母回退
                }
                else if (char.IsDigit(c))
                {
                    AddAudio(segs, c + ".mp3");
                }
            }
        }

        /// <summary>添加数字读音（多位数逐位读）</summary>
        private void AddNumber(List<Segment> segs, int n)
        {
            if (n <= 0) return;
            foreach (char c in n.ToString()) AddAudio(segs, c + ".mp3");
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
            try
            {
                _tts.Speak(text);  // 同步播放
            }
            catch { /* TTS 不可用时静默 */ }
        }

        public void Dispose()
        {
            _disposed = true;
            _queue.CompleteAdding();
            try { _tts?.Dispose(); } catch { }
        }

        private enum SegKind { Audio, Tts }
        private struct Segment { public SegKind Kind; public string File; public string Text; }
    }
}
