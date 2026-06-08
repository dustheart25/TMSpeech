using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TMSpeech.Core.Plugins;

namespace TMSpeech.Translator.OpenAICompatible;

public class OpenAICompatibleTranslator : ITextCorrectionTranslator
{
    private OpenAICompatibleTranslatorConfig _config = new();
    private static readonly HttpClient HttpClient = new();

    public string GUID => "D25B5C4F-3218-4A0C-A62E-4A26A7F7E5A1";
    public string Name => "OpenAI 兼容翻译器";
    public string Description => "通过 OpenAI-compatible Chat Completions API 翻译或纠错识别结果";
    public string Version => "0.1.0";
    public string SupportVersion => "any";
    public string Author => "Built-in";
    public string Url => "";
    public string License => "MIT License";
    public string Note => "字幕内容会发送到你配置的 API 服务。";
    public bool Available => true;

    public IPluginConfigEditor CreateConfigEditor() => new OpenAICompatibleTranslatorConfigEditor();

    public void LoadConfig(string config)
    {
        if (string.IsNullOrEmpty(config)) return;
        _config = JsonSerializer.Deserialize<OpenAICompatibleTranslatorConfig>(config) ?? new OpenAICompatibleTranslatorConfig();
    }

    public string Translate(string text)
    {
        return ProcessText(text, new TextProcessingOptions { EnableTranslation = true }).TranslatedText;
    }

    public TranslationResult ProcessText(string text, TextProcessingOptions options)
    {
        if (string.IsNullOrWhiteSpace(text)) return new TranslationResult();
        if (!options.EnableCorrection && !options.EnableTranslation)
        {
            return new TranslationResult { OriginalText = text };
        }

        if (string.IsNullOrWhiteSpace(_config.Endpoint)) throw new InvalidOperationException("API 地址不能为空。");
        if (string.IsNullOrWhiteSpace(_config.Model)) throw new InvalidOperationException("模型不能为空。");

        var userPrompt = BuildUserPrompt(text, options);
        var payload = new
        {
            model = _config.Model,
            temperature = 0,
            messages = new[]
            {
                new { role = "system", content = BuildSystemPrompt(options) },
                new { role = "user", content = userPrompt }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _config.Endpoint);
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        }

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = HttpClient.SendAsync(request).GetAwaiter().GetResult();
        var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(content);
        var choices = document.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0) return new TranslationResult { OriginalText = text };

        var messageContent = choices[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";

        return ParseStructuredResult(messageContent, text, options);
    }

    private string BuildSystemPrompt(TextProcessingOptions options)
    {
        if (!options.EnableCorrection && options.EnableTranslation)
        {
            return _config.SystemPrompt;
        }

        return "You correct speech recognition text and/or translate it. Return JSON only. Preserve meaning; do not summarize, expand, or add information.";
    }

    private string BuildUserPrompt(string text, TextProcessingOptions options)
    {
        if (!options.EnableCorrection && options.EnableTranslation)
        {
            return _config.UserPrompt
                .Replace("{targetLanguage}", _config.TargetLanguage)
                .Replace("{text}", text);
        }

        var requestedFields = new List<string>();
        if (options.EnableCorrection) requestedFields.Add("\"corrected_text\"");
        if (options.EnableTranslation) requestedFields.Add("\"translated_text\"");

        var task = options switch
        {
            { EnableCorrection: true, EnableTranslation: true } =>
                $"Correct obvious speech recognition errors in the source text, then translate it to {_config.TargetLanguage}.",
            { EnableCorrection: true } =>
                "Correct obvious speech recognition errors in the source text.",
            { EnableTranslation: true } =>
                $"Translate the source text to {_config.TargetLanguage}.",
            _ => ""
        };

        return string.Join("\n", new[]
        {
            task,
            "",
            "Rules:",
            "- Fix only clear recognition mistakes, casing, punctuation, spacing, and sentence boundaries.",
            "- Preserve the original meaning.",
            "- Do not summarize, expand, or add new information.",
            "- If no correction is needed, set corrected_text to the original text.",
            $"- Return JSON only, with exactly these keys: {string.Join(", ", requestedFields)}.",
            "",
            "Source text:",
            text
        });
    }

    private static TranslationResult ParseStructuredResult(string content, string originalText, TextProcessingOptions options)
    {
        if (!options.EnableCorrection && options.EnableTranslation)
        {
            return new TranslationResult
            {
                OriginalText = originalText,
                TranslatedText = content.Trim()
            };
        }

        var json = ExtractJsonObject(content);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                return new TranslationResult
                {
                    OriginalText = originalText,
                    CorrectedText = options.EnableCorrection ? GetJsonString(root, "corrected_text") : "",
                    TranslatedText = options.EnableTranslation ? GetJsonString(root, "translated_text") : ""
                };
            }
            catch
            {
            }
        }

        return new TranslationResult
        {
            OriginalText = originalText,
            CorrectedText = options.EnableCorrection && !options.EnableTranslation ? content.Trim() : "",
            TranslatedText = options.EnableTranslation ? content.Trim() : ""
        };
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? ""
            : "";
    }

    private static string ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text.Substring(start, end - start + 1) : "";
    }

    public void Init()
    {
    }

    public void Destroy()
    {
    }
}
