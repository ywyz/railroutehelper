using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security;
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
        private const string TtsProsodyRate = "+20%";
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
            { 'K', "快" }, { 'T', "特" }, { 'X', "行" }, { 'S', "市域" },
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
            // 具体播报段通过 SSML 设为 +20%，这里保留默认基准速度。
            _tts.Rate = 0;
            _tts.Volume = 100;

            _playerThread = new Thread(PlayerLoop) { IsBackground = true, Name = "VoicePlayer" };
            _playerThread.Start();
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
            /// <summary>正数表示晚点分钟；零或负数按正点播报。</summary>
            public int DelayMinutes;
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
                    // 开往 xx 方向的列车 车号 已经停靠 xx x站台，本次停车 x 分。
                    AddDirectionPrefix(segs, dest);
                    AddTrainNumber(segs, announcement.TrainCode);
                    AddTts(segs, "已经停靠");
                    AddStationAndPlatform(segs, announcement.Station, announcement.Platform, "站台");
                    if (announcement.StopMinutes > 0)
                    {
                        AddTts(segs, "，本次停车");
                        AddNumber(segs, announcement.StopMinutes);
                        AddTts(segs, "分。");
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
                    if (announcement.DelayMinutes > 0)
                    {
                        AddTts(segs, "晚点");
                        AddNumber(segs, announcement.DelayMinutes);
                        AddTts(segs, "分发车");
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
