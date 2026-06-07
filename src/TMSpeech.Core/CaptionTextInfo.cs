namespace TMSpeech.Core;

public class CaptionTextInfo
{
    public string OriginalText { get; set; } = "";
    public string TranslatedText { get; set; } = "";
    public bool HasTranslatedText => !string.IsNullOrWhiteSpace(TranslatedText);
}
