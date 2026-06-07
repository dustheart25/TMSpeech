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

        public event EventHandler<JobStatus> StatusChanged;
        public event EventHandler<SpeechEventArgs> TextChanged;
        public event EventHandler<SpeechEventArgs> SentenceDone;
        public event EventHandler<long> RunningSecondsChanged;

        protected void OnTextChanged(SpeechEventArgs e) => TextChanged?.Invoke(this, e);
        protected void OnSentenceDone(SpeechEventArgs e) => SentenceDone?.Invoke(this, e);
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
        private HashSet<string> _sensitiveWords;
        private bool _disableInThisSentence = false;
        private string logFile;
        private string currentText = "";
        private readonly object _logLock = new();
        private const int MaxCaptionSentences = 4;
        private readonly object _captionLock = new();
        private readonly List<CaptionSentence> _captionSentences = new();
        private int _nextCaptionSentenceId = 1;

        private class CaptionSentence
        {
            public int Id { get; init; }
            public string OriginalText { get; init; } = "";
            public string TranslatedText { get; set; } = "";

            public string DisplayText => string.IsNullOrWhiteSpace(TranslatedText)
                ? OriginalText
                : $"{OriginalText}\n{TranslatedText}";
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
            if (!ConfigManagerFactory.Instance.Get<bool>(TranslationConfigTypes.Enabled)) return;

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

        private int AddCaptionSentence(string originalText)
        {
            lock (_captionLock)
            {
                var sentence = new CaptionSentence
                {
                    Id = _nextCaptionSentenceId++,
                    OriginalText = originalText
                };
                _captionSentences.Add(sentence);
                while (_captionSentences.Count > MaxCaptionSentences)
                {
                    _captionSentences.RemoveAt(0);
                }

                return sentence.Id;
            }
        }

        private void UpdateCaptionTranslation(int sentenceId, string translatedText)
        {
            lock (_captionLock)
            {
                var sentence = _captionSentences.FirstOrDefault(x => x.Id == sentenceId);
                if (sentence != null)
                {
                    sentence.TranslatedText = translatedText;
                }
            }
        }

        private void ClearCaptionSentences()
        {
            lock (_captionLock)
            {
                _captionSentences.Clear();
            }
        }

        private void PublishCaptionText(string? liveText = null)
        {
            List<string> texts;
            lock (_captionLock)
            {
                texts = _captionSentences.Select(x => x.DisplayText).ToList();
            }

            var textInProgress = liveText ?? currentText;
            if (!string.IsNullOrWhiteSpace(textInProgress))
            {
                texts.Add(textInProgress);
            }

            var captionText = string.Join("\n", texts.TakeLast(MaxCaptionSentences));
            OnTextChanged(new SpeechEventArgs
            {
                Text = new TextInfo(captionText)
            });
        }

        private void TranslateSentenceInBackground(int sentenceId, string originalText)
        {
            var translator = _translator;
            if (translator == null || string.IsNullOrWhiteSpace(originalText)) return;

            _ = Task.Run(() =>
            {
                try
                {
                    var translatedText = translator.Translate(originalText)?.Trim();
                    if (string.IsNullOrWhiteSpace(translatedText)) return;

                    if (ConfigManagerFactory.Instance.Get<bool>(TranslationConfigTypes.SaveTranslationToLog))
                    {
                        AppendRecognitionLog($"译文: {translatedText}");
                    }

                    UpdateCaptionTranslation(sentenceId, translatedText);
                    PublishCaptionText();
                }
                catch (Exception ex)
                {
                    NotificationManager.Instance.Notify($"翻译失败：\n{ex.Message}", "翻译失败", NotificationType.Warning);
                    System.Diagnostics.Debug.WriteLine($"Failed to translate recognition text: {ex.Message}");
                }
            });
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
            OnSentenceDone(args);
            currentText = "";
            var sentenceId = AddCaptionSentence(args.Text.Text);
            PublishCaptionText("");
            TranslateSentenceInBackground(sentenceId, args.Text.Text);
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

            _timer?.Dispose();
            _timer = null;
        }
    }
}
