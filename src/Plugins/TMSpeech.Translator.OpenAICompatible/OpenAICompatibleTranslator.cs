using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TMSpeech.Core.Plugins;

namespace TMSpeech.Translator.OpenAICompatible;

public class OpenAICompatibleTranslator : ITranslator
{
    private OpenAICompatibleTranslatorConfig _config = new();
    private static readonly HttpClient HttpClient = new();

    public string GUID => "D25B5C4F-3218-4A0C-A62E-4A26A7F7E5A1";
    public string Name => "OpenAI 兼容翻译器";
    public string Description => "通过 OpenAI-compatible Chat Completions API 翻译识别结果";
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
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (string.IsNullOrWhiteSpace(_config.Endpoint)) throw new InvalidOperationException("API 地址不能为空。");
        if (string.IsNullOrWhiteSpace(_config.Model)) throw new InvalidOperationException("模型不能为空。");

        var userPrompt = _config.UserPrompt
            .Replace("{targetLanguage}", _config.TargetLanguage)
            .Replace("{text}", text);

        var payload = new
        {
            model = _config.Model,
            temperature = 0,
            messages = new[]
            {
                new { role = "system", content = _config.SystemPrompt },
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
        if (choices.GetArrayLength() == 0) return "";

        return choices[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    public void Init()
    {
    }

    public void Destroy()
    {
    }
}
