using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TMSpeech.Core.Plugins
{
    public class TranslationResult
    {
        public string OriginalText { get; set; } = "";
        public string CorrectedText { get; set; } = "";
        public string TranslatedText { get; set; } = "";

        public string DisplayOriginalText => string.IsNullOrWhiteSpace(CorrectedText)
            ? OriginalText
            : CorrectedText;
    }

    public class TextProcessingOptions
    {
        public bool EnableCorrection { get; set; }
        public bool EnableTranslation { get; set; }
    }

    public interface ITranslator : IPlugin
    {
        string Translate(string text);
    }

    public interface ITextCorrectionTranslator : ITranslator
    {
        TranslationResult ProcessText(string text, TextProcessingOptions options);
    }
}
