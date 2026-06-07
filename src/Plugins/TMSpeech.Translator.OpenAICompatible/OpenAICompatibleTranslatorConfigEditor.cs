using System.Text.Json;
using TMSpeech.Core.Plugins;

namespace TMSpeech.Translator.OpenAICompatible;

public class OpenAICompatibleTranslatorConfigEditor : IPluginConfigEditor
{
    private readonly Dictionary<string, object> _values = new();
    private readonly List<PluginConfigFormItem> _formItems = new();

    public event EventHandler<EventArgs>? FormItemsUpdated;
    public event EventHandler<EventArgs>? ValueUpdated;

    public OpenAICompatibleTranslatorConfigEditor()
    {
        var defaultConfig = new OpenAICompatibleTranslatorConfig();
        _values["Endpoint"] = defaultConfig.Endpoint;
        _values["ApiKey"] = defaultConfig.ApiKey;
        _values["Model"] = defaultConfig.Model;
        _values["TargetLanguage"] = defaultConfig.TargetLanguage;
        _values["SystemPrompt"] = defaultConfig.SystemPrompt;
        _values["UserPrompt"] = defaultConfig.UserPrompt;

        _formItems.Add(new PluginConfigFormItemText("Endpoint", "API 地址"));
        _formItems.Add(new PluginConfigFormItemPassword("ApiKey", "API Key"));
        _formItems.Add(new PluginConfigFormItemText("Model", "模型"));
        _formItems.Add(new PluginConfigFormItemText("TargetLanguage", "目标语言"));
        _formItems.Add(new PluginConfigFormItemText("SystemPrompt", "系统提示词"));
        _formItems.Add(new PluginConfigFormItemText("UserPrompt", "用户提示词", "{text} 为原文，{targetLanguage} 为目标语言"));
    }

    public IReadOnlyList<PluginConfigFormItem> GetFormItems()
    {
        return _formItems.AsReadOnly();
    }

    public IReadOnlyDictionary<string, object> GetAll()
    {
        return _values;
    }

    public void SetValue(string key, object value)
    {
        _values[key] = value;
        ValueUpdated?.Invoke(this, EventArgs.Empty);
    }

    public object GetValue(string key)
    {
        return _values.TryGetValue(key, out var value) ? value : "";
    }

    public string GenerateConfig()
    {
        return JsonSerializer.Serialize(new OpenAICompatibleTranslatorConfig
        {
            Endpoint = GetString("Endpoint"),
            ApiKey = GetString("ApiKey"),
            Model = GetString("Model"),
            TargetLanguage = GetString("TargetLanguage"),
            SystemPrompt = GetString("SystemPrompt"),
            UserPrompt = GetString("UserPrompt")
        });
    }

    public void LoadConfigString(string config)
    {
        if (string.IsNullOrEmpty(config)) return;

        try
        {
            var cfg = JsonSerializer.Deserialize<OpenAICompatibleTranslatorConfig>(config);
            if (cfg == null) return;

            _values["Endpoint"] = cfg.Endpoint;
            _values["ApiKey"] = cfg.ApiKey;
            _values["Model"] = cfg.Model;
            _values["TargetLanguage"] = cfg.TargetLanguage;
            _values["SystemPrompt"] = cfg.SystemPrompt;
            _values["UserPrompt"] = cfg.UserPrompt;
        }
        catch
        {
        }
    }

    private string GetString(string key)
    {
        return _values.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";
    }
}
