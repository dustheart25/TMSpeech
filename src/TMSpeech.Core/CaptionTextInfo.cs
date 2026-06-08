namespace TMSpeech.Core;

public class CaptionTextInfo
{
    public string OriginalText { get; set; } = "";
    public string CorrectedText { get; set; } = "";
    public string TranslatedText { get; set; } = "";
    public bool HasCorrectedText => !string.IsNullOrWhiteSpace(CorrectedText)
                                    && !string.Equals(OriginalText.Trim(), CorrectedText.Trim(),
                                        StringComparison.Ordinal);
    public bool HasTranslatedText => !string.IsNullOrWhiteSpace(TranslatedText);
}
