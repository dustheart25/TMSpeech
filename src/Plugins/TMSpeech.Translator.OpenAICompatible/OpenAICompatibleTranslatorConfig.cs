using System.Text.Json.Serialization;

namespace TMSpeech.Translator.OpenAICompatible;

public class OpenAICompatibleTranslatorConfig
{
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "https://api.openai.com/v1/chat/completions";

    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "gpt-4o-mini";

    [JsonPropertyName("targetLanguage")]
    public string TargetLanguage { get; set; } = "简体中文";

    [JsonPropertyName("systemPrompt")]
    public string SystemPrompt { get; set; } = "You are a translation engine. Return only the translated text.";

    [JsonPropertyName("userPrompt")]
    public string UserPrompt { get; set; } = "Translate the following text to {targetLanguage}:\n{text}";
}
