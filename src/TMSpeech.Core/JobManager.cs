using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using TMSpeech.Core.Plugins;
using TMSpeech.Core.Services.Notification;

namespace TMSpeech.Core
{
    public enum JobStatus
    {
        Stopped,
        Running,
        Paused,
    }

    public static class JobManagerFactory
    {
        private static Lazy<JobManager> _instance = new(() => new JobManagerImpl());
        public static JobManager Instance => _instance.Value;
    }

    public abstract class JobManager
    {
        private JobStatus _status;

        public JobStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                StatusChanged?.Invoke(this, value);
            }
        }

        public long RunningSeconds { get; protected set; }

        public event EventHandler<JobStatus>? StatusChanged;
        public event EventHandler<SpeechEventArgs>? TextChanged;
        public event EventHandler<SpeechEventArgs>? SentenceDone;
        public event EventHandler<IReadOnlyList<CaptionTextInfo>>? CaptionChanged;
        public event EventHandler<TextInfo>? HistoryTextChanged;
        public event EventHandler<long>? RunningSecondsChanged;

        protected void OnTextChanged(SpeechEventArgs e) => TextChanged?.Invoke(this, e);
        protected void OnSentenceDone(SpeechEventArgs e) => SentenceDone?.Invoke(this, e);
        protected void OnCaptionChanged(IReadOnlyList<CaptionTextInfo> e) => CaptionChanged?.Invoke(this, e);
        protected void OnHistoryTextChanged(TextInfo e) => HistoryTextChanged?.Invoke(this, e);
        protected void OnUpdateRunningSeconds(long seconds) => RunningSecondsChanged?.Invoke(this, seconds);

        public abstract void Start();
        public abstract void Pause();
        public abstract void Stop();
    }

    public class JobManagerImpl : JobManager
    {
        private readonly PluginManager _pluginManager;


        internal JobManagerImpl()
        {
            _pluginManager = PluginManagerFactory.GetInstance();
        }

        private IAudioSource? _audioSource;
        private IRecognizer? _recognizer;
        private ITranslator? _translator;
        private bool _enableTranslation;
        private bool _enableCorrection;
        private HashSet<string> _sensitiveWords;
        private bool _disableInThisSentence = false;
        private string logFile;
        private string currentText = "";
        private readonly object _logLock = new();
        private const int DefaultCaptionCacheCount = 4;
        private readonly object _captionLock = new();
        private readonly List<CaptionSentence> _captionSentences = new();
        private readonly object _recordLock = new();
        private readonly List<RecognitionRecord> _recognitionRecords = new();
        private int _nextRecordId = 1;
        private DateTime _recognitionStartedAt = DateTime.Now;
        private DateTime _lastRecordEndTime = DateTime.Now;
        private readonly object _translationTaskLock = new();
        private readonly List<Task> _translationTasks = new();

        private class CaptionSentence
        {
            public int Id { get; init; }
            public string OriginalText { get; init; } = "";
            public string CorrectedText { get; set; } = "";
            public string TranslatedText { get; set; } = "";

            public string DisplayText => BuildDisplayText(OriginalText, CorrectedText, TranslatedText);
        }

        private class RecognitionRecord
        {
            public int Id { get; init; }
            public int Index { get; init; }
            public DateTime StartTime { get; init; }
            public DateTime EndTime { get; init; }
            public string OriginalText { get; init; } = "";
            public string CorrectedText { get; set; } = "";
            public string TranslatedText { get; set; } = "";
            public TextInfo? HistoryText { get; init; }

            public string DisplayText => BuildDisplayText(OriginalText, CorrectedText, TranslatedText);
        }

        private static bool HasMeaningfulCorrection(string originalText, string correctedText)
        {
            return !string.IsNullOrWhiteSpace(correctedText)
                   && !string.Equals(originalText.Trim(), correctedText.Trim(), StringComparison.Ordinal);
        }

        private static string BuildDisplayText(string originalText, string correctedText, string translatedText)
        {
            var lines = new List<string> { originalText };
            if (HasMeaningfulCorrection(originalText, correctedText))
            {
                lines.Add(correctedText);
            }

            if (!string.IsNullOrWhiteSpace(translatedText))
            {
                lines.Add(translatedText);
            }

            return string.Join("\n", lines);
        }

        private int GetCaptionCacheCount()
        {
            var count = ConfigManagerFactory.Instance.Get<int>(AppearanceConfigTypes.CaptionCacheCount);
            return Math.Clamp(count <= 0 ? DefaultCaptionCacheCount : count, 1, 10);
        }

        private void InitAudioSource()
        {
            var configAudioSource = ConfigManagerFactory.Instance.Get<string>(AudioSourceConfigTypes.AudioSource);
            var config = ConfigManagerFactory.Instance.Get<string>(AudioSourceConfigTypes.GetPluginConfigKey(configAudioSource));

            _audioSource = _pluginManager.AudioSources[configAudioSource];
            if (_audioSource != null)
            {
                _audioSource.LoadConfig(config);
                _audioSource.DataAvailable -= OnAudioSourceOnDataAvailable;
                _audioSource.DataAvailable += OnAudioSourceOnDataAvailable;
                _audioSource.ExceptionOccured -= OnPluginRunningExceptionOccurs;
                _audioSource.ExceptionOccured += OnPluginRunningExceptionOccurs;
            }
        }

        private Timer? _timer;


        private void OnAudioSourceOnDataAvailable(object? o, byte[] data)
        {
            // Console.WriteLine(o?.GetHashCode().ToString("x8") ?? "<null>");
            _recognizer?.Feed(data);
        }

        private void InitRecognizer()
        {
            var configRecognizer = ConfigManagerFactory.Instance.Get<string>(RecognizerConfigTypes.Recognizer);
            var config = ConfigManagerFactory.Instance.Get<string>(RecognizerConfigTypes.GetPluginConfigKey(configRecognizer));
            // default config
            if ((configRecognizer == null || configRecognizer.Length == 0) && _pluginManager.Recognizers.Count > 0)
            {
                configRecognizer = _pluginManager.Recognizers.Keys.First();
            }
            _recognizer = _pluginManager.Recognizers[configRecognizer];

            if (_recognizer != null)
            {
                _recognizer.LoadConfig(config);
                // https://stackoverflow.com/a/1104269
                // use -= first to prevent duplication.
                _recognizer.TextChanged -= OnRecognizerOnTextChanged;
                _recognizer.TextChanged += OnRecognizerOnTextChanged;
                _recognizer.SentenceDone -= OnRecognizerOnSentenceDone;
                _recognizer.SentenceDone += OnRecognizerOnSentenceDone;
                _recognizer.ExceptionOccured -= OnPluginRunningExceptionOccurs;
                _recognizer.ExceptionOccured += OnPluginRunningExceptionOccurs;
            }
        }

        private void InitTranslator()
        {
            _translator = null;
            _enableTranslation = ConfigManagerFactory.Instance.Get<bool>(TranslationConfigTypes.Enabled);
            _enableCorrection = ConfigManagerFactory.Instance.Get<bool>(TranslationConfigTypes.EnableCorrection);
            if (!_enableTranslation && !_enableCorrection) return;

            var configTranslator = ConfigManagerFactory.Instance.Get<string>(TranslationConfigTypes.Translator);
            if (string.IsNullOrEmpty(configTranslator))
            {
                if (_pluginManager.Translators.Count == 0) return;
                configTranslator = _pluginManager.Translators.Keys.First();
            }

            if (!_pluginManager.Translators.TryGetValue(configTranslator, out var translator))
            {
                NotificationManager.Instance.Notify("翻译器初始化失败：找不到已配置的翻译器", "翻译器为空", NotificationType.Warning);
                return;
            }

            var config = ConfigManagerFactory.Instance.Get<string>(TranslationConfigTypes.GetPluginConfigKey(configTranslator));
            translator.LoadConfig(config);
            _translator = translator;
        }

        private void AppendRecognitionLog(string text)
        {
            if (logFile == null || logFile.Length == 0) return;

            lock (_logLock)
            {
                File.AppendAllText(logFile, string.Format("{0:T}: {1}\n", DateTime.Now, text));
            }
        }

        private int AddCaptionSentence(int id, string originalText)
        {
            lock (_captionLock)
            {
                var sentence = new CaptionSentence
                {
                    Id = id,
                    OriginalText = originalText
                };
                _captionSentences.Add(sentence);
                var captionCacheCount = GetCaptionCacheCount();
                while (_captionSentences.Count > captionCacheCount)
                {
                    _captionSentences.RemoveAt(0);
                }

                return id;
            }
        }

        private int AddRecognitionRecord(string originalText, TextInfo historyText)
        {
            lock (_recordLock)
            {
                var endTime = DateTime.Now;
                var startTime = _recognitionRecords.Count == 0 ? _recognitionStartedAt : _lastRecordEndTime;
                if (endTime <= startTime)
                {
                    endTime = startTime.AddMilliseconds(500);
                }

                var record = new RecognitionRecord
                {
                    Id = _nextRecordId++,
                    Index = _recognitionRecords.Count + 1,
                    StartTime = startTime,
                    EndTime = endTime,
                    OriginalText = originalText,
                    HistoryText = historyText
                };
                _recognitionRecords.Add(record);
                _lastRecordEndTime = endTime;
                return record.Id;
            }
        }

        private void UpdateCaptionTranslation(int sentenceId, TranslationResult result)
        {
            TextInfo? updatedHistoryText = null;
            lock (_captionLock)
            {
                var sentence = _captionSentences.FirstOrDefault(x => x.Id == sentenceId);
                if (sentence != null)
                {
                    sentence.CorrectedText = result.CorrectedText;
                    sentence.TranslatedText = result.TranslatedText;
                }
            }

            lock (_recordLock)
            {
                var record = _recognitionRecords.FirstOrDefault(x => x.Id == sentenceId);
                if (record != null)
                {
                    record.CorrectedText = result.CorrectedText;
                    record.TranslatedText = result.TranslatedText;
                    if (record.HistoryText != null)
                    {
                        record.HistoryText.Text = record.DisplayText;
                        updatedHistoryText = record.HistoryText;
                    }
                }
            }

            if (updatedHistoryText != null)
            {
                OnHistoryTextChanged(updatedHistoryText);
            }
        }

        private void ClearCaptionSentences()
        {
            lock (_captionLock)
            {
                _captionSentences.Clear();
            }
        }

        private void ClearRecognitionRecords()
        {
            lock (_recordLock)
            {
                _recognitionRecords.Clear();
                _nextRecordId = 1;
                _recognitionStartedAt = DateTime.Now;
                _lastRecordEndTime = _recognitionStartedAt;
            }

            lock (_translationTaskLock)
            {
                _translationTasks.Clear();
            }
        }

        private void PublishCaptionText(string? liveText = null)
        {
            List<CaptionTextInfo> captions;
            lock (_captionLock)
            {
                captions = _captionSentences
                    .Select(x => new CaptionTextInfo
                    {
                        OriginalText = x.OriginalText,
                        CorrectedText = x.CorrectedText,
                        TranslatedText = x.TranslatedText
                    })
                    .ToList();
            }

            var textInProgress = liveText ?? currentText;
            if (!string.IsNullOrWhiteSpace(textInProgress))
            {
                captions.Add(new CaptionTextInfo
                {
                    OriginalText = textInProgress
                });
            }

            captions = captions.TakeLast(GetCaptionCacheCount()).ToList();
            OnCaptionChanged(captions);
            var captionText = string.Join("\n", captions.Select(x =>
                BuildDisplayText(x.OriginalText, x.CorrectedText, x.TranslatedText)));
            OnTextChanged(new SpeechEventArgs
            {
                Text = new TextInfo(captionText)
            });
        }

        private void ProcessSentenceInBackground(int sentenceId, string originalText)
        {
            var translator = _translator;
            var enableTranslation = _enableTranslation;
            var enableCorrection = _enableCorrection;
            if (translator == null || string.IsNullOrWhiteSpace(originalText)) return;
            if (!enableTranslation && !enableCorrection) return;

            var translationTask = Task.Run(() =>
            {
                try
                {
                    var result = translator is ITextCorrectionTranslator correctionTranslator
                        ? correctionTranslator.ProcessText(originalText, new TextProcessingOptions
                        {
                            EnableCorrection = enableCorrection,
                            EnableTranslation = enableTranslation
                        })
                        : new TranslationResult
                        {
                            OriginalText = originalText,
                            TranslatedText = enableTranslation
                                ? translator.Translate(originalText)?.Trim() ?? ""
                                : ""
                        };

                    result.OriginalText = string.IsNullOrWhiteSpace(result.OriginalText)
                        ? originalText
                        : result.OriginalText.Trim();
                    result.CorrectedText = result.CorrectedText?.Trim() ?? "";
                    result.TranslatedText = result.TranslatedText?.Trim() ?? "";

                    if (string.IsNullOrWhiteSpace(result.CorrectedText)
                        && string.IsNullOrWhiteSpace(result.TranslatedText))
                    {
                        return;
                    }

                    if (HasMeaningfulCorrection(originalText, result.CorrectedText))
                    {
                        AppendRecognitionLog($"纠错: {result.CorrectedText}");
                    }

                    if (!string.IsNullOrWhiteSpace(result.TranslatedText))
                    {
                        AppendRecognitionLog($"译文: {result.TranslatedText}");
                    }

                    UpdateCaptionTranslation(sentenceId, result);
                    PublishCaptionText();
                }
                catch (Exception ex)
                {
                    NotificationManager.Instance.Notify($"文本处理失败：\n{ex.Message}", "文本处理失败", NotificationType.Warning);
                    System.Diagnostics.Debug.WriteLine($"Failed to process recognition text: {ex.Message}");
                }
            });

            lock (_translationTaskLock)
            {
                _translationTasks.Add(translationTask);
            }
        }

        private void WaitForTranslations()
        {
            Task[] tasks;
            lock (_translationTaskLock)
            {
                tasks = _translationTasks.Where(x => !x.IsCompleted).ToArray();
            }

            if (tasks.Length == 0) return;

            try
            {
                Task.WaitAll(tasks, TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Export whatever is available if a translation task fails or times out.
            }
        }

        private void ExportRecognitionRecords()
        {
            if (string.IsNullOrWhiteSpace(logFile)) return;

            List<RecognitionRecord> records;
            lock (_recordLock)
            {
                records = _recognitionRecords.ToList();
            }

            if (records.Count == 0) return;

            var basePath = Path.Combine(
                Path.GetDirectoryName(logFile) ?? "",
                Path.GetFileNameWithoutExtension(logFile));
            var txtPath = $"{basePath}.bilingual.txt";
            var srtPath = $"{basePath}.srt";

            var txtBuilder = new StringBuilder();
            foreach (var record in records)
            {
                txtBuilder.AppendLine($"[{record.EndTime:T}]");
                txtBuilder.AppendLine($"原识别: {record.OriginalText}");
                if (HasMeaningfulCorrection(record.OriginalText, record.CorrectedText))
                {
                    txtBuilder.AppendLine($"纠错: {record.CorrectedText}");
                }
                if (!string.IsNullOrWhiteSpace(record.TranslatedText))
                {
                    txtBuilder.AppendLine($"译文: {record.TranslatedText}");
                }
                txtBuilder.AppendLine();
            }

            File.WriteAllText(txtPath, txtBuilder.ToString());

            var srtBuilder = new StringBuilder();
            foreach (var record in records)
            {
                srtBuilder.AppendLine(record.Index.ToString());
                srtBuilder.AppendLine(
                    $"{FormatSrtTime(record.StartTime - _recognitionStartedAt)} --> {FormatSrtTime(record.EndTime - _recognitionStartedAt)}");
                srtBuilder.AppendLine(record.DisplayText);
                srtBuilder.AppendLine();
            }

            File.WriteAllText(srtPath, srtBuilder.ToString());
        }

        private static string FormatSrtTime(TimeSpan time)
        {
            if (time < TimeSpan.Zero) time = TimeSpan.Zero;
            return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}";
        }

        private void OnRecognizerOnSentenceDone(object? sender, SpeechEventArgs args)
        {
            // Save the sentense to log
            if (logFile != null && logFile.Length > 0)
            {
                try
                {
                    AppendRecognitionLog(args.Text.Text);
                }
                catch (Exception ex)
                {
                    NotificationManager.Instance.Notify(
                        $"写入识别日志失败: {ex.Message}",
                        "日志写入错误",
                        NotificationType.Warning);
                    System.Diagnostics.Debug.WriteLine($"Failed to write recognition log: {ex.Message}");
                    // 清空 logFile 避免重复通知
                    logFile = "";
                }
            }

            _disableInThisSentence = false;
            currentText = "";
            var sentenceId = AddRecognitionRecord(args.Text.Text, args.Text);
            OnSentenceDone(args);
            AddCaptionSentence(sentenceId, args.Text.Text);
            PublishCaptionText("");
            ProcessSentenceInBackground(sentenceId, args.Text.Text);
        }

        private void OnRecognizerOnTextChanged(object? sender, SpeechEventArgs args)
        {
            currentText = args.Text.Text;
            if (!_disableInThisSentence)
            {
                var s = _sensitiveWords.FirstOrDefault(x => args.Text.Text.Contains(x));
                if (!string.IsNullOrEmpty(s))
                {
                    NotificationManager.Instance.Notify($"检测到敏感词：{s}", "敏感词", NotificationType.Warning);
                    _disableInThisSentence = true;
                }
            }

            PublishCaptionText(args.Text.Text);
        }

        private void StartRecognize()
        {
            ClearCaptionSentences();
            ClearRecognitionRecords();
            InitSensitiveWords();
            InitAudioSource();
            InitRecognizer();
            InitTranslator();

            if (_audioSource == null || _recognizer == null)
            {
                Status = JobStatus.Stopped;
                NotificationManager.Instance.Notify("语音源或识别器初始化失败", "语音源或识别器为空！", NotificationType.Error);
                return;
            }


            try
            {
                _recognizer.Start();
            }
            catch (InvalidOperationException ex)
            {
                NotificationManager.Instance.Notify($"识别器启动失败：\n{ex.Message}", "启动失败",
                    NotificationType.Error);
                return;
            }
            catch (Exception ex)
            {
                NotificationManager.Instance.Notify($"识别器启动失败：\n{ex.Message}\n{ex.StackTrace}", "启动失败",
                    NotificationType.Error);
                return;
            }

            try
            {
                _audioSource.Start();
            }
            catch (InvalidOperationException ex)
            {
                _recognizer?.Stop();
                NotificationManager.Instance.Notify($"语音源启动失败：\n{ex.Message}", "启动失败",
                    NotificationType.Error);
                return;
            }
            catch (Exception ex)
            {
                _recognizer?.Stop();
                NotificationManager.Instance.Notify($"语音源启动失败：\n{ex.Message}\n{ex.StackTrace}", "启动失败",
                    NotificationType.Error);
                return;
            }

            var logPath = ConfigManagerFactory.Instance.Get<string>(GeneralConfigTypes.ResultLogPath).Trim();
            if (logPath.Length > 0)
            {
                Directory.CreateDirectory(logPath);
                logFile = Path.Combine(logPath, string.Format("{0:yy-MM-dd-HH-mm-ss}.txt", DateTime.Now));
            } else
            {
                logFile = "";
            }

            if (Status == JobStatus.Stopped) RunningSeconds = 0;

            Status = JobStatus.Running;

            _timer = new Timer(TimerCallback, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }

        private void InitSensitiveWords()
        {
            var sensitiveWords = ConfigManagerFactory.Instance.Get<string>(NotificationConfigTypes.SensitiveWords);
            if (string.IsNullOrWhiteSpace(sensitiveWords))
            {
                _sensitiveWords = new HashSet<string>();
                return;
            }

            _sensitiveWords = new HashSet<string>(sensitiveWords.Split(new[] { ',', '，', '\n' },
                StringSplitOptions.RemoveEmptyEntries));
        }

        private void OnPluginRunningExceptionOccurs(object? e, Exception ex)
        {
            NotificationManager.Instance.Notify($"插件运行异常:\n ({e?.GetType().Module.Name})：{ex.Message}",
                "插件异常", NotificationType.Error);
            // 只能在主线程stop。
            // Stop();
        }


        private void TimerCallback(object? state)
        {
            RunningSeconds++;
            OnUpdateRunningSeconds(RunningSeconds);
        }

        private void StopRecognize()
        {
            try
            {
                _audioSource?.Stop();
                _recognizer?.Stop();
            }
            catch (Exception ex)
            {
                NotificationManager.Instance.Notify($"停止失败：\n{ex.Message}", "停止失败", NotificationType.Fatal);
            }

            if (currentText != null && currentText.Length > 0)
            {
                OnRecognizerOnSentenceDone(_recognizer, new SpeechEventArgs{Text=new TextInfo(currentText)});
                currentText = "";
            }

            _audioSource.DataAvailable -= OnAudioSourceOnDataAvailable;
            _audioSource.ExceptionOccured -= OnPluginRunningExceptionOccurs;

            _recognizer.TextChanged -= OnRecognizerOnTextChanged;
            _recognizer.SentenceDone -= OnRecognizerOnSentenceDone;
            _recognizer.ExceptionOccured -= OnPluginRunningExceptionOccurs;


            _audioSource = null;
            _recognizer = null;
            WaitForTranslations();
            ExportRecognitionRecords();
            _translator = null;
        }

        public override void Start()
        {
            if (Status == JobStatus.Running) return;
            StartRecognize();
        }

        public override void Pause()
        {
            if (Status == JobStatus.Running) StopRecognize();
            Status = JobStatus.Paused;

            _timer?.Dispose();
            _timer = null;
        }

        public override void Stop()
        {
            if (Status == JobStatus.Running) StopRecognize();
            Status = JobStatus.Stopped;

            // Clear text when stopped
            var emptyTextArg = new SpeechEventArgs();
            emptyTextArg.Text = new TextInfo(string.Empty);
            // OnSentenceDone(emptyTextArg); // TODO unable to save existing text.
            OnTextChanged(emptyTextArg);
            OnCaptionChanged(Array.Empty<CaptionTextInfo>());

            _timer?.Dispose();
            _timer = null;
        }
    }
}
